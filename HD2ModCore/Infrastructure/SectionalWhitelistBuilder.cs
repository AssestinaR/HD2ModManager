using System.Globalization;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 从"选中的 unit"推出干净资产白名单：<b>unit → 它引用的材质 → 那些材质引用的贴图</b>。
///
/// <para>
/// <b>白名单为什么要存进依据文件、而不是转换时重推。</b>重推依赖游戏资产索引的当前状态；
/// 一旦游戏更新让 armor 表变化，重推会静默换掉一套 unit——产物在用户眼里"自己变了"。
/// 存下来才能比对并报告漂移。
/// </para>
///
/// <para>
/// 这里只做**引用闭包**这一件纯事：给一组引用关系，算出可达的材质与贴图。
/// 真正读 patch 得到引用关系是调用方的事（`PatchGroupAnalyzer` 依赖游戏数据，不进这个函数），
/// 这样规则可以离线测。
/// </para>
/// </summary>
public static class SectionalWhitelistBuilder
{
	/// <summary>
	/// 按引用关系做闭包。<paramref name="references"/> 只需包含两类：
	/// <c>UnitMaterial</c>（unit → 材质）与 <c>MaterialTexture</c>（材质 → 贴图）。
	/// </summary>
	public static SectionalSourceAssetKeys Build(
		IEnumerable<AdaptationAssetKey> selectedUnitKeys,
		IEnumerable<PatchAssetReference> references)
	{
		ArgumentNullException.ThrowIfNull(selectedUnitKeys);
		ArgumentNullException.ThrowIfNull(references);

		var units = selectedUnitKeys.Distinct().ToArray();
		var unitSet = units.ToHashSet();
		var refs = references.ToArray();

		var materials = refs
			.Where(reference => reference.Kind == PatchReferenceKind.UnitMaterial)
			.Where(reference => unitSet.Contains(reference.SourceAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.Distinct()
			.ToArray();

		var materialSet = materials.ToHashSet();
		var textures = refs
			.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture)
			.Where(reference => materialSet.Contains(reference.SourceAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.Distinct()
			.ToArray();

		return new SectionalSourceAssetKeys(
			units.Select(Format).ToArray(),
			materials.Select(Format).ToArray(),
			textures.Select(Format).ToArray());
	}

	/// <summary>
	/// 把"意图 unit"扩成"这套护甲的全部 unit"。
	/// <para>
	/// 意图来自装备目录，只列**kit 槽位**上的 unit（实测某套护甲只有 9 个）。而系列里的装饰件
	/// （翅膀 / 鞋子 / 上衣 …）是**同一个 patch 里另外的 unit**，在目录里没有 kit 归属，
	/// 却是这套护甲真实的一部分。只按意图集筛会把它们全部丢掉——2026-11-10 线上事故：
	/// 11 个来源最后只剩 9 个材质组 + 1 个 unit 组，几何缺一大半。
	/// </para>
	/// <para>
	/// 规则：**哪些 patch 提供了意图 unit，就把那些 patch 里的 unit 全算进来**。
	/// 按 patch 扩（而不是把整条来源链的 unit 全并进来），是为了不把同一来源里
	/// 另一个护甲的 unit 带进来。
	/// </para>
	/// </summary>
	public static async ValueTask<IReadOnlyList<AdaptationAssetKey>> ExpandIntentUnitsAsync(
		IReadOnlyList<AdaptationAssetKey> intentUnits,
		IReadOnlyList<string> patchTocPaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(intentUnits);
		ArgumentNullException.ThrowIfNull(patchTocPaths);

		var seen = intentUnits.ToHashSet();
		var expanded = new List<AdaptationAssetKey>(intentUnits);
		var scanner = new HD2ModAdaptation.PatchReconstruction.PatchTocScanner();
		foreach (var path in patchTocPaths)
		{
			IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries;
			try
			{
				entries = await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException
				or InvalidDataException
				or UnauthorizedAccessException
				or EndOfStreamException)
			{
				continue;
			}

			var units = entries
				.Where(entry => entry.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId)
				.Select(entry => new AdaptationAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId))
				.ToArray();
			if (units.Length == 0 || !units.Any(seen.Contains)) continue;
			foreach (var unit in units)
			{
				if (seen.Add(unit)) expanded.Add(unit);
			}
		}

		return expanded;
	}

	/// <summary>
	/// 从磁盘上的来源 patch 读出引用关系，再算闭包。
	/// <para>
	/// <b>为什么要逐 patch 分析并把同一来源的其它 patch 当"相关材质包"传进去。</b>
	/// 一个 legacy Mod 的材质常常在另一份 patch 里（模型 patch 只带 unit）。
	/// 不传相关包时，unit → 材质这一步什么也读不到，白名单会缺掉全部材质，
	/// 产物就成"模型在、材质没了"。
	/// </para>
	/// </summary>
	public static async Task<SectionalSourceAssetKeys> BuildFromPatchesAsync(
		IReadOnlyCollection<AdaptationAssetKey> unitKeys,
		IReadOnlyList<string> patchTocPaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitKeys);
		ArgumentNullException.ThrowIfNull(patchTocPaths);

		var analyzer = new PatchGroupAnalyzer();
		var references = new List<PatchAssetReference>();
		foreach (var path in patchTocPaths)
		{
			var related = patchTocPaths
				.Where(other => !string.Equals(other, path, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			var analysis = await analyzer
				.AnalyzeDependencyGraphAsync(new PatchGroupInput(path, RelatedMaterialTocFilePaths: related), cancellationToken)
				.ConfigureAwait(false);
			references.AddRange(analysis.References);
		}

		return Build(unitKeys, references);
	}

	/// <summary>白名单里的文本形式：<c>typeId:fileId</c>，各 16 位十六进制。</summary>
	public static string Format(AdaptationAssetKey key)
		=> $"{key.TypeId:x16}:{key.FileId:x16}";

	/// <summary>把一段资产键转成 <see cref="SectionalAssetRef"/>（规划层用的形状）。</summary>
	public static SectionalAssetRef ToRef(AdaptationAssetKey key) => new(key.TypeId, key.FileId);

	/// <summary>判断一个引用闭包是否为空（完全没有任何 unit 时用它给出人话错误）。</summary>
	public static string? Describe(SectionalSourceAssetKeys keys)
	{
		ArgumentNullException.ThrowIfNull(keys);
		if (keys.Units.Count == 0) return "选中的护甲/头盔里没有任何可转换的 unit。";
		if (keys.Materials.Count == 0)
			return "选中的 unit 没有引用任何材质：产物会是一具没有材质的模型，已中止。";
		return null;
	}

	/// <summary>把白名单回读成 <see cref="SectionalAssetRef"/> 集合（转换时按它过滤）。</summary>
	public static IReadOnlySet<SectionalAssetRef> Parse(SectionalSourceAssetKeys keys)
	{
		ArgumentNullException.ThrowIfNull(keys);
		var parsed = new HashSet<SectionalAssetRef>();
		foreach (var raw in keys.Units.Concat(keys.Materials).Concat(keys.Textures))
		{
			if (!SectionalConversionPlanner.TryParseKey(raw, out var key)) continue;
			parsed.Add(key);
		}

		return parsed;
	}

	/// <summary>把类型 id 写成可读形式（诊断用）。</summary>
	internal static string DescribeType(ulong typeId)
		=> typeId.ToString("x16", CultureInfo.InvariantCulture);
}
