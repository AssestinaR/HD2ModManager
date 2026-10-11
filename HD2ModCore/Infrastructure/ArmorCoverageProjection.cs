using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：把"资产索引 + 资产映射 + 实际部署图"折成护甲页真正需要的两个字段（设计文档 §4 #23）。
// Purpose: Collapses index + mapping + deployed graph into the two fields the armor page actually needs.
//
// 为什么值得单独存在：改造前护甲页为了这两个字段去构建**整个** GameData 归档浏览器——
// 那一步会为每一个 Mod 请求一次资产清单（逐包解析 patch），于是"打开护甲页"变成"解析全部 Mod"。
// 而覆盖结论只取决于"已部署且有归属的资产"：谁部署了它、它落在哪个归档。
// 与 Mod 库、激活配置、资产清单都无关。把它独立出来，"首开"就不再与 Mod 数量挂钩。
//
// 与浏览器覆盖层的等价性（逐条核对过，不是推断）：
//   浏览器 GameDataArchiveBrowserService.BuildAsync 的 effectiveAssetsByArchive 由
//   `foreach chain in deployed.AssetChains { if (!mapping.Assets.TryGetValue(chain.AssetKey)) continue; ... }` 得出。
//   本投影用的是同一张 deployed 图与同一个映射服务，差别只有两点：
//     · 映射请求只覆盖"有赢家的部署链"的资产键——浏览器用的是全部 Mod 资产键的超集，
//       而超出部分的链赢家为 null，在护甲页的判定里本来就被过滤掉；
//     · 提前跳过赢家为 null 的链——浏览器把它们放进覆盖层，护甲页随后也会过滤掉。
//   因此对护甲页真正读取的字段而言，两者结果相同。
internal static class ArmorCoverageProjection
{
	// 作用：把一批归档 id 压成一个稳定的短令牌。先排序再哈希，所以枚举顺序不影响身份。
	// Purpose: Compresses a set of archive ids into a stable token; sorted first so enumeration order cannot matter.
	public static string ScopeToken(IEnumerable<string> archiveIds)
	{
		ArgumentNullException.ThrowIfNull(archiveIds);
		var builder = new StringBuilder();
		foreach (var id in archiveIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
			builder.Append(id.ToLowerInvariant()).Append(',');
		return Hash(builder.ToString());
	}

	public static ArmorCoverageFacts Project(
		GameDataIndexFacts index,
		GameDataMappingFacts mapping,
		DeployedOverrideGraph deployed,
		IReadOnlySet<string> archiveIds)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(mapping);
		ArgumentNullException.ThrowIfNull(deployed);
		ArgumentNullException.ThrowIfNull(archiveIds);

		// 只对"索引里确实存在、且被问到"的归档作答。
		// 这条过滤保住了改造前的语义：归档不在索引里时页面拿到的是"未知"（null），不是"未覆盖"（false）。
		// 把"读不到"当成"没有"正是设计文档 §4 里 ModInformationPropertyStatus 反复强调要避免的误判。
		var known = index.Archives
			.Select(archive => archive.PackageName)
			.Where(archiveIds.Contains)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var byArchive = new Dictionary<string, List<GameDataEffectiveAsset>>(StringComparer.OrdinalIgnoreCase);
		foreach (var chain in deployed.AssetChains)
		{
			// 没有归属的部署（Data 里存在、激活状态却没有记录的补丁）不构成"被某个 Mod 覆盖"。
			if (chain.Winner.NodeId is null) continue;
			if (!mapping.Assets.TryGetValue(chain.AssetKey, out var mapped)) continue;
			foreach (var target in mapped.TargetArchives)
			{
				if (!known.Contains(target.ArchiveId)) continue;
				if (!byArchive.TryGetValue(target.ArchiveId, out var assets)) byArchive[target.ArchiveId] = assets = [];
				assets.Add(new GameDataEffectiveAsset(chain.AssetKey, chain.Winner.NodeId, chain.Winner.TargetPatchIndex, chain.IsCompetition));
			}
		}

		var entries = new Dictionary<string, ArmorCoverageEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (var archiveId in known)
		{
			byArchive.TryGetValue(archiveId, out var assets);
			assets ??= [];
			var unitWinner = RankWinners(assets.Where(asset => asset.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId));
			var coverWinner = unitWinner ?? RankWinners(assets);
			entries[archiveId] = new ArmorCoverageEntry(unitWinner is not null, unitWinner, coverWinner);
		}

		var generation = ComputeGeneration(index.Fingerprint.SourceFingerprint, mapping.MappingGeneration, deployed.DeploymentGeneration, archiveIds);
		return new ArmorCoverageFacts(generation, entries, mapping.Issues);
	}

	// 作用：从一组有效资产里挑出"谁在视觉上赢了"。
	// 排序口径与改造前逐字一致：先看覆盖的资产条数（多的赢），再看目标补丁索引（大的赢），最后按节点 id 稳定收尾。
	// Purpose: Picks the visually winning node. The ordering is copied verbatim from the pre-refactor page code.
	private static ModNodeId? RankWinners(IEnumerable<GameDataEffectiveAsset> assets)
		=> assets
			.Where(asset => asset.WinnerNodeId is not null)
			.GroupBy(asset => asset.WinnerNodeId!.Value)
			.OrderByDescending(group => group.Count())
			.ThenByDescending(group => group.Max(asset => asset.TargetPatchIndex))
			.ThenBy(group => group.Key.Value)
			.Select(group => (ModNodeId?)group.Key)
			.FirstOrDefault();

	private static string ComputeGeneration(string indexFingerprint, string mappingGeneration, string deploymentGeneration, IReadOnlySet<string> archiveIds)
	{
		var builder = new StringBuilder()
			.Append(indexFingerprint).Append('|')
			.Append(mappingGeneration).Append('|')
			.Append(deploymentGeneration).Append('|')
			.Append(ScopeToken(archiveIds));
		return Hash(builder.ToString());
	}

	private static string Hash(string value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
