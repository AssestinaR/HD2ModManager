using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把 sectional 的「标准 7 槽」映射成一份 <c>custom-model.json</c>（自定义素体定义）。
///
/// <para>
/// <b>为什么必须写。</b>sectional 与 vpk2patch 只该差在来源（一个来自 legacy patch 集合、
/// 一个来自 vpk 集合）。vpk2patch 的产物目录里有一份 <c>custom-model.json</c> —— "这套 unit
/// 是一具素体"的凭证，护甲映射页、换甲、按槽位开关都靠它。sectional 那边一直没写，
/// 于是两条链路不对称：同样的 7 槽产物，一条能被当素体用，另一条不能。
/// </para>
///
/// <para>
/// <b>槽位与身形从 <c>sections.json</c> 来</b>（那份表就是"每个 unit 在哪个槽、哪一层、
/// 哪个身形"的权威描述），不重新从 unit 名去猜。
/// </para>
/// </summary>
public static class SectionalModelDefinition
{
	/// <summary>头盔槽（0）没有身形变体，注册表只接受 <c>body=Any</c>。</summary>
	private static readonly uint HelmetSlot = ArmorStructure.SlotOf(ArmorPart.Helmet) ?? 0u;

	/// <summary>
	/// 用一份 section 表造素体定义。
	/// <para>
	/// 同一 <c>(body, slot, pieceType)</c> 只保留第一条：注册表里那个坐标只能对应一个 unit，
	/// 重复会让整份定义读不出来（<see cref="RuntimeArmorRegistryService"/> 的校验）。
	/// </para>
	/// </summary>
	public static RuntimeArmorRegistryModel Build(
		IReadOnlyList<WardrobeSectionMapUnit> units,
		string key,
		string name)
	{
		ArgumentNullException.ThrowIfNull(units);
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		var pieces = new List<RuntimeArmorRegistryModelPiece>(units.Count);
		var seen = new HashSet<(uint Body, uint Slot, uint PieceType)>();
		foreach (var unit in units)
		{
			if (string.IsNullOrWhiteSpace(unit.FileId)) continue;
			var part = ArmorStructure.TryParse(unit.Slot);
			if (part is null) continue;
			var slot = ArmorStructure.SlotOf(part.Value);
			if (slot is null) continue;

			var body = (uint)ArmorStructure.ParseBody(unit.BodyType);
			if (slot.Value == HelmetSlot) body = (uint)UnitMeshBodyVariant.Any;
			var pieceType = LayerOf(unit.PieceType);
			if (!seen.Add((body, slot.Value, pieceType))) continue;
			pieces.Add(new RuntimeArmorRegistryModelPiece(body, unit.FileId, slot.Value, pieceType));
		}

		return new RuntimeArmorRegistryModel(key, null, pieces, name);
	}

	/// <summary>件级 PieceType 字符串 → 层号（0 内层 / 1 护甲 / 2 装饰）。与编译器的口径一致。</summary>
	private static uint LayerOf(string? pieceType) => pieceType?.Trim().ToLowerInvariant() switch
	{
		"armor" => 1u,
		"accessory" => 2u,
		_ => 0u,
	};
}
