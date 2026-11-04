using System.Globalization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 就地烘焙的翻译：把"哪些 section 不画"从<b>来源</b>键空间翻到<b>目标</b>键空间。
/// </summary>
/// <param name="DisabledByTargetUnit">目标 unit 键空间的禁用集合，直接喂给 <see cref="WardrobeSectionBaker"/>。</param>
/// <param name="DroppedUnits">有几个来源 unit 的禁用条目在目标产物里找不到落点。</param>
/// <param name="DroppedSections">这些丢掉的条目一共涉及多少块几何。</param>
public sealed record VpkSectionBakeTranslation(
	IReadOnlyDictionary<string, IReadOnlyCollection<int>> DisabledByTargetUnit,
	int DroppedUnits,
	int DroppedSections)
{
	public static VpkSectionBakeTranslation Empty { get; } =
		new(new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal), 0, 0);

	/// <summary>有没有条目因为"目标产物里没这个 unit"而被丢掉。</summary>
	public bool HasDropped => DroppedUnits != 0;
}

/// <summary>
/// 「应用」的就地烘焙路径：<b>不再重跑整条换甲</b>。
/// <para>
/// <b>为什么可以就地烘。</b><see cref="WardrobeSectionBaker"/> 本来就是就地、幂等、可逆的
/// （每个要写的 dword 当前必须 ∈ {0, 原值}，否则拒绝写入）。真正缺的只是一张对照表：
/// 用户勾的禁用集合存在<b>来源</b>键空间（与目标护甲无关，换甲也不会丢），
/// 而 <c>Overwrite/</c> 里的 patch 用的是<b>目标</b>键。换甲时写进
/// <see cref="WardrobeSectionMapUnit.SourceUnit"/> 的正是这张表。
/// </para>
/// <para>
/// <b>为什么不能只靠"unit id 相同"。</b>换甲会把来源 unit 的 payload 挂到目标护甲的键上，
/// 两边 id 不同；恒等路径（目标是自己的素体）则完全相同，那时 <c>sourceUnit</c> 为空，
/// 本条按"键就是来源键"处理。
/// </para>
/// </summary>
public static class VpkSectionBakePlan
{
	/// <summary>
	/// 把 <paramref name="disabledBySourceUnit"/>（来源键空间）翻成目标键空间。
	/// <para>
	/// 只翻 <paramref name="targetMap"/> 里有的 unit：目标产物里没有的 unit，写下去只会得到
	/// "找不到落点"的 warning，不如在这里就数出来告诉用户。
	/// </para>
	/// </summary>
	public static VpkSectionBakeTranslation Translate(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledBySourceUnit,
		WardrobeSectionMapDocument targetMap)
	{
		ArgumentNullException.ThrowIfNull(targetMap);
		if (disabledBySourceUnit is null || disabledBySourceUnit.Count == 0)
			return VpkSectionBakeTranslation.Empty;

		// 宽松键：大小写、带不带 0x 都接受（来源键来自 wardrobe.json，可能被手改过）。
		var bySource = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
		foreach (var (unit, sections) in disabledBySourceUnit)
		{
			var key = Normalize(unit);
			if (key.Length == 0 || sections is null || sections.Count == 0)
				continue;
			bySource[key] = sections;
		}

		if (bySource.Count == 0)
			return VpkSectionBakeTranslation.Empty;

		var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
		var matched = new HashSet<string>(StringComparer.Ordinal);
		foreach (var unit in targetMap.Units)
		{
			var sourceKey = string.IsNullOrWhiteSpace(unit.SourceUnit) ? Normalize(unit.FileId) : Normalize(unit.SourceUnit);
			if (!bySource.TryGetValue(sourceKey, out var sections))
				continue;
			matched.Add(sourceKey);
			// 同一个来源 unit 可能落到多个目标 unit（合并方向是一对多）——那几处都要关。
			var targetKey = Normalize(unit.FileId);
			result[targetKey] = result.TryGetValue(targetKey, out var existing)
				? [.. existing.Concat(sections).Distinct().Order()]
				: [.. sections.Distinct().Order()];
		}

		var droppedUnits = 0;
		var droppedSections = 0;
		foreach (var (sourceKey, sections) in bySource)
		{
			if (matched.Contains(sourceKey))
				continue;
			droppedUnits++;
			droppedSections += sections.Count;
		}

		return new VpkSectionBakeTranslation(result, droppedUnits, droppedSections);
	}

	/// <summary>
	/// 一句话结论，给界面用。<b>丢掉的条目必须说出来</b>——用户勾了开关却发现没生效，
	/// 只会以为功能坏了。
	/// </summary>
	public static string Describe(VpkSectionBakeTranslation translation)
	{
		ArgumentNullException.ThrowIfNull(translation);
		if (!translation.HasDropped)
			return string.Empty;
		return string.Format(
			CultureInfo.InvariantCulture,
			"有 {0} 个部件的勾选在当前目标上没有落点（共 {1} 块几何），它们这次不会被烘进去。",
			translation.DroppedUnits,
			translation.DroppedSections);
	}

	private static string Normalize(string? unit)
	{
		if (string.IsNullOrWhiteSpace(unit))
			return string.Empty;
		var text = unit.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			text = text[2..];
		return text.ToLowerInvariant();
	}
}
