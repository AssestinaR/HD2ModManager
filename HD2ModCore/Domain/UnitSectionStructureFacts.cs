namespace HD2ModCore.Domain;

// 一个 unit 的 section 结构事实（信息中心的无节点事实）。
//
// 为什么它是"无节点"的：section 结构属于**资产**，不属于哪个 Mod 节点——
// 同一个 unit 可能由某个 Mod 的 patch 提供，也可能只存在于原版 archive 里。
// 身份因此是"游戏目录 + 这一批 unit 键"，与 ArmorCoverage 同一套做法。
//
// 为什么复用 WardrobeSectionMapEntry：section 映射表（sections.json）、衣柜编辑器、
// 以及将来的任何需求方都该看到**同一份** section 描述。各造一份 DTO 就会出现
// "编辑器算出来的指纹和 CLI 的不一样"这类最难查的偏差。
//
// Node-less: section structure belongs to an asset, not to a mod node — the same unit may
// come from a mod's patch or exist only in a vanilla archive. Identity is the game directory
// plus the batch of unit keys, mirroring ArmorCoverage. It reuses WardrobeSectionMapEntry so
// every consumer sees one description of a section.

/// <summary>一个 unit 的 section 结构。字段与 <see cref="WardrobeSectionMapUnit"/> 对齐。</summary>
public sealed record UnitSectionStructureFact(
	string Unit,
	string Slot,
	string PieceType,
	string BodyType,
	string Weight,
	int SectionCount,
	bool LodConsistent,
	IReadOnlyList<WardrobeSectionMapEntry> Sections)
{
	/// <summary>从映射表条目转过来，保持唯一的一份换算规则。</summary>
	public static UnitSectionStructureFact From(WardrobeSectionMapUnit unit)
	{
		ArgumentNullException.ThrowIfNull(unit);
		return new UnitSectionStructureFact(
			unit.FileId,
			unit.Slot,
			unit.PieceType,
			unit.BodyType,
			unit.Weight,
			unit.SectionCount,
			unit.LodConsistent,
			unit.Sections);
	}
}

/// <summary>
/// 一批 unit 的 section 结构事实。<c>StructureGeneration</c> 是"这批 unit 的结构"的身份，
/// 由命中的 patch/archive 指纹组成——它变了就说明结构可能变了，缓存该失效。
/// </summary>
public sealed record UnitSectionStructureFacts(
	string StructureGeneration,
	IReadOnlyDictionary<string, UnitSectionStructureFact> Units,
	IReadOnlyList<string> MissingUnitIds,
	IReadOnlyList<CoreIssue> Issues)
{
	/// <summary>按 unit 键取结构；不存在时返回 null（而不是抛）。</summary>
	public UnitSectionStructureFact? Find(string? unit)
	{
		if (string.IsNullOrWhiteSpace(unit))
			return null;
		var normalized = WardrobeSectionFingerprint.TryNormalizeUnit(unit, out var value)
			? value
			: unit.Trim().ToLowerInvariant();
		return Units.TryGetValue(normalized, out var fact) ? fact : null;
	}

	/// <summary>把事实还原成映射表条目，供树构建与投影使用。</summary>
	public IReadOnlyList<WardrobeSectionMapUnit> ToMapUnits()
		=> Units.Values
			.OrderBy(fact => fact.Unit, StringComparer.Ordinal)
			.Select(fact => new WardrobeSectionMapUnit(
				fact.Unit,
				fact.Slot,
				fact.PieceType,
				fact.BodyType,
				fact.Weight,
				fact.SectionCount,
				fact.LodConsistent,
				fact.Sections))
			.ToArray();
}
