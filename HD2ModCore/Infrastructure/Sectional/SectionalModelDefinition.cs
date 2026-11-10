using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把 sectional 的「标准 7 槽」产物登记成一具自定义素体（<c>custom-model.json</c>）。
///
/// <para>
/// <b>为什么必须写。</b>sectional 与 vpk2patch 只该差在来源（一个来自 legacy patch 集合、
/// 一个来自 vpk 集合）。vpk2patch 的产物里有一份 <c>custom-model.json</c> ——
/// "这套 unit 是一具素体"的凭证，护甲映射页、换甲、按槽位开关都靠它。
/// sectional 那边一直没写，于是同样的 7 槽产物，一条能被当素体用，另一条不能。
/// </para>
///
/// <para>
/// <b>件从产出的 patch 里逐个 unit 读，不从 section 表推。</b>产出的
/// <c>sectional/sections.json</c> 是**折叠后**的表（整个 patch 只有一条 unit、slot 为空），
/// 推不出"每个槽一件"。而 patch 里每个 unit 自己带着 customization（身形 / 槽位 / 层），
/// 那才是权威事实。
/// </para>
///
/// <para>
/// <b>身形按 unit 自己的来，不强行归一。</b>同一槽位若来源里 slim 与 stocky 是两套不同模型，
/// 拆件器会产出两个 unit，这里就落成两条 piece（<c>body=1</c> 与 <c>body=0</c>）；
/// 只有当这个槽位所有来源都是 Any 时，产出才是一个 unit、一条 <c>body=3</c> 的 piece。
/// 唯一的例外是头盔：它没有身形变体，一律 Any。
/// </para>
/// </summary>
public static class SectionalModelDefinition
{
	/// <summary>头盔槽（0）没有身形变体，注册表只接受 <c>body=Any</c>。</summary>
	private static readonly uint HelmetSlot = ArmorStructure.SlotOf(ArmorPart.Helmet) ?? 0u;

	/// <summary>
	/// 读一个 7 槽 patch，把里面每个 unit 的 customization 变成一条 piece。
	/// </summary>
	/// <param name="patchPath">产出的 unit patch（<c>sectional/&lt;hex&gt;.patch_0</c>）。</param>
	public static async ValueTask<RuntimeArmorRegistryModel> BuildFromPatchAsync(
		string patchPath,
		string key,
		string name,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		var entries = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner()
			.ScanEntriesAsync(patchPath, cancellationToken)
			.ConfigureAwait(false);
		var reader = new PatchUnitMeshReader();
		var pieces = new List<RuntimeArmorRegistryModelPiece>(entries.Count);
		var seen = new HashSet<(uint Body, uint Slot, uint PieceType)>();
		foreach (var entry in entries)
		{
			if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId) continue;
			var mesh = await reader.ReadAsync(entry, cancellationToken).ConfigureAwait(false);
			var info = mesh.Model.CustomizationInfo;

			var part = ArmorStructure.TryParse(info.Slot);
			if (part is null) continue;
			var slot = ArmorStructure.SlotOf(part.Value);
			if (slot is null) continue;

			var body = (uint)ArmorStructure.ParseBody(info.BodyType);
			if (slot.Value == HelmetSlot) body = (uint)UnitMeshBodyVariant.Any;
			var pieceType = LayerOf(info.PieceType);
			// 注册表里 (body, slot, pieceType) 只能对应一个 unit；重复会让整份定义读不出来。
			if (!seen.Add((body, slot.Value, pieceType))) continue;
			pieces.Add(new RuntimeArmorRegistryModelPiece(
				body, entry.AssetKey.FileId.ToString("x16"), slot.Value, pieceType));
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
