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
