using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

/// <summary>一个目标护甲（item_id + 名字）。</summary>
public sealed record ArmorMappingCoverageTarget(uint ItemId, string Name);

public enum ArmorMappingCoverageKind
{
	/// <summary>没有声明目标，或声明目标里没有被映射的：照常部署。</summary>
	None,
	/// <summary>声明目标全部已被映射：本次部署跳过这个 Mod 的 patch。</summary>
	Covered,
	/// <summary>声明目标里只有一部分被映射：patch 拆不开，整份照常部署（规划 §12.4）。</summary>
	Partial,
}

public sealed record ArmorMappingCoverage(
	ArmorMappingCoverageKind Kind,
	IReadOnlyList<ArmorMappingCoverageTarget> Covered,
	IReadOnlyList<ArmorMappingCoverageTarget> Uncovered)
{
	public static ArmorMappingCoverage NoCoverage { get; } = new(ArmorMappingCoverageKind.None, [], []);
}

/// <summary>
/// 映射优先于详情页 patch（规划 §8.3 A / §12.3）。纯判定 + 两个小读取器，部署与详情页共用。
/// </summary>
public static class ArmorMappingPatchCoverage
{
	public const string CoveredIssueCode = "ArmorMappingCoversPatch";
	public const string PartialIssueCode = "ArmorMappingCoversPatchPartial";

	/// <summary>部分覆盖的说明（部署日志与详情页共用）：哪几套被映射、游戏里实际显示什么、怎么办。</summary>
	public static string DescribePartial(string modName, ArmorMappingCoverage coverage)
	{
		var mapped = string.Join("、", coverage.Covered.Select(target => target.Name));
		var others = string.Join("、", coverage.Uncovered.Select(target => target.Name));
		return $"『{modName}』的专属 patch 同时覆盖 {mapped}（已被护甲映射）和 {others}（未映射），patch 拆不开，所以整份照常部署、没有跳过。"
			+ $"结果：{others} 显示 patch 的模型；{mapped} 会被映射的运行时覆盖盖住 patch 的部件，可能是 patch 与映射的模型混在一起。"
			+ "想避免混搭，请到详情页去掉这个 Mod 的专属多选（只留一个目标），或从映射页移除这几套的映射。";
	}

	/// <summary>纯函数：声明目标 vs 映射集合。</summary>
	public static ArmorMappingCoverage Classify(
		IReadOnlyList<ArmorMappingCoverageTarget> declaredTargets,
		IReadOnlySet<uint> mappedTargets)
	{
		ArgumentNullException.ThrowIfNull(declaredTargets);
		ArgumentNullException.ThrowIfNull(mappedTargets);
		if (declaredTargets.Count == 0 || mappedTargets.Count == 0) return ArmorMappingCoverage.NoCoverage;
		var covered = declaredTargets.Where(target => mappedTargets.Contains(target.ItemId)).ToArray();
		if (covered.Length == 0) return ArmorMappingCoverage.NoCoverage;
		var uncovered = declaredTargets.Where(target => !mappedTargets.Contains(target.ItemId)).ToArray();
		return new ArmorMappingCoverage(
			uncovered.Length == 0 ? ArmorMappingCoverageKind.Covered : ArmorMappingCoverageKind.Partial,
			covered, uncovered);
	}

	/// <summary>
	/// 已应用协议里的映射目标。协议不存在 / 读不出来 / 未启用 ⇒ 空集合（不跳过任何东西）。
	/// 读「已应用」的协议而不是 workspace：没应用的映射不会改游戏，patch 不能先被跳过。
	/// </summary>
	public static async ValueTask<IReadOnlySet<uint>> ReadAppliedMappedTargetsAsync(
		string? registryRoot, CancellationToken cancellationToken = default)
	{
		var empty = new HashSet<uint>();
		if (string.IsNullOrWhiteSpace(registryRoot)) return empty;
		var path = Path.Combine(registryRoot, RuntimeArmorRegistryService.ProtocolFileName);
		try
		{
			if (!File.Exists(path)) return empty;
			var info = new FileInfo(path);
			if (info.Length > 4 * 1024 * 1024) return empty;
			var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			var document = RuntimeArmorRegistryService.ParseProtocol(text);
			if (!document.Enabled) return empty;
			return document.Mappings.Select(mapping => mapping.TargetItemId).ToHashSet();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return empty;
		}
	}

	/// <summary>
	/// 一个 Mod 的声明目标：专属 ⇒ <c>ExclusiveTargets</c>；非专属且目标是游戏护甲 ⇒ 索引里同档案的全部记录。
	/// 没有目标 / 素体目标 / 索引读不出来 ⇒ 空。<paramref name="kitsByArchive"/> 只在需要时才被调用。
	/// </summary>
	public static IReadOnlyList<ArmorMappingCoverageTarget> DeclaredTargets(
		VpkWardrobeFile wardrobe,
		Func<string, IReadOnlyList<ArmorMappingCoverageTarget>> kitsByArchive)
	{
		ArgumentNullException.ThrowIfNull(wardrobe);
		ArgumentNullException.ThrowIfNull(kitsByArchive);
		if (wardrobe.Exclusive)
			return wardrobe.ExclusiveTargets
				.Select(target => new ArmorMappingCoverageTarget(
					target.ItemId, string.IsNullOrWhiteSpace(target.Name) ? target.ItemId.ToString() : target.Name!))
				.ToArray();
		var archive = wardrobe.TargetArmorArchiveId;
		if (string.IsNullOrWhiteSpace(archive)
			|| archive.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase))
			return [];
		return kitsByArchive(archive);
	}

	/// <summary>索引库里 <c>archive_id → 记录</c>（只读，轻量查询）。读不出来返回空表。</summary>
	public static IReadOnlyDictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>> ReadKitsByArchive(StoragePaths paths)
	{
		var result = new Dictionary<string, List<ArmorMappingCoverageTarget>>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!File.Exists(paths.DbPath)) return new Dictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>>();
			using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT item_id, archive_id, display_name FROM game_data_authority_kits;";
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				var raw = reader.GetInt64(0);
				if (raw < 0 || raw > uint.MaxValue) continue;
				var archive = reader.GetString(1);
				if (!result.TryGetValue(archive, out var list)) result[archive] = list = [];
				list.Add(new ArmorMappingCoverageTarget((uint)raw, reader.GetString(2)));
			}
		}
		catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			return new Dictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>>();
		}
		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ArmorMappingCoverageTarget>)pair.Value, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 对一个节点目录做完整判定（读 wardrobe.json、需要时才查索引）。供部署与详情页共用。
	/// </summary>
	public static async ValueTask<ArmorMappingCoverage> AnalyzeNodeAsync(
		string nodeDirectory,
		IReadOnlySet<uint> mappedTargets,
		Func<IReadOnlyDictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>>> kitIndex,
		CancellationToken cancellationToken = default)
	{
		if (mappedTargets.Count == 0) return ArmorMappingCoverage.NoCoverage;
		if (!File.Exists(VpkModWardrobeService.GetStatePath(nodeDirectory))) return ArmorMappingCoverage.NoCoverage;
		VpkWardrobeFile wardrobe;
		try
		{
			wardrobe = await VpkModWardrobeService.ReadAsync(nodeDirectory, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
		{
			return ArmorMappingCoverage.NoCoverage;
		}
		var declared = DeclaredTargets(wardrobe, archive =>
			kitIndex().TryGetValue(archive, out var kits) ? kits : []);
		return Classify(declared, mappedTargets);
	}
}
