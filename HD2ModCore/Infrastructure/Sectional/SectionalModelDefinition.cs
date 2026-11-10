using HD2ModAdaptation.Analysis;
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
/// <b>件从「拆件计划」出，不回读产物。</b>vpk2patch 那边也是这么做的：
/// <c>VpkPatchAssembler</c> 在建 unit 的同时就把 <c>RegistryPieces</c> 定下来
/// （部位 → 槽位、头盔 → <c>body=3</c>、本体 → <c>body 0/1</c>），最后由 CLI 写进
/// <c>custom-model.json</c>。这里保持同一形状：<see cref="SectionalSplitPlan.Targets"/>
/// 就是"将产出哪些 unit、各是什么槽位/身形/层"的权威描述，素体定义直接从它翻译。
/// </para>
///
/// <para>
/// <b>身形按目标自己的来，不强行归一。</b>同一槽位若 slim 与 stocky 是两套不同模型，
/// 计划里就是两个 target，于是两条 piece（<c>body=1</c> / <c>body=0</c>）；
/// 只有该槽位来源全是 Any 时才是一个 target、一条 <c>body=3</c>。
/// 唯一例外是头盔：它没有身形变体，一律 Any。
/// </para>
/// </summary>
public static class SectionalModelDefinition
{
	/// <summary>头盔槽（0）没有身形变体，注册表只接受 <c>body=Any</c>。</summary>
	private static readonly uint HelmetSlot = ArmorStructure.SlotOf(ArmorPart.Helmet) ?? 0u;

	/// <summary>按拆件计划造素体定义：一个 target → 一条 piece。</summary>
	public static RuntimeArmorRegistryModel Build(
		SectionalSplitPlan plan,
		string key,
		string name)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		var pieces = new List<RuntimeArmorRegistryModelPiece>(plan.Targets.Count);
		var seen = new HashSet<(uint Body, uint Slot, uint PieceType)>();
		foreach (var target in plan.Targets)
		{
			if (string.IsNullOrWhiteSpace(target.HostUnitId)) continue;
			var part = ArmorStructure.TryParse(target.Slot);
			if (part is null) continue;
			var slot = ArmorStructure.SlotOf(part.Value);
			if (slot is null) continue;

			// 注册表身形号：Stocky=0 / Slim=1 / Any=3。
			// **不能把 UnitMeshBodyVariant 直接转 uint**：那套枚举是 Slim=1 / Stocky=2 / Any=3，
			// Stocky 会写成 2 —— 游戏里没有那一行，壮硕身形直接不生效。
			var body = RegistryBody(ArmorStructure.ParseBody(target.BodyType));
			if (slot.Value == HelmetSlot) body = RegistryBody(UnitMeshBodyVariant.Any);
			// 层号固定 0：库里 10 份游戏里能用的素体都是"一个槽×身形一条 piece"，
			// 层（内层/护甲/装饰）在同一 unit 内由 section 开关表达，不再靠 pieceType 分格。
			const uint pieceType = 0;
			// 注册表里 (body, slot, pieceType) 只能对应一个 unit；重复会让整份定义读不出来。
			if (!seen.Add((body, slot.Value, pieceType))) continue;
			pieces.Add(new RuntimeArmorRegistryModelPiece(body, target.HostUnitId, slot.Value, pieceType));
		}

		return new RuntimeArmorRegistryModel(key, null, pieces, name);
	}

	/// <summary>枚举身形 → 注册表身形号（Stocky=0 / Slim=1 / Any=3）。</summary>
	private static uint RegistryBody(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Slim => 1u,
		UnitMeshBodyVariant.Stocky => 0u,
		_ => 3u,
	};

	/// <summary>件级 PieceType 字符串 → 层号（0 内层 / 1 护甲 / 2 装饰）。</summary>
	private static uint LayerOf(string? pieceType) => pieceType?.Trim().ToLowerInvariant() switch
	{
		"armor" => 1u,
		"accessory" => 2u,
		_ => 0u,
	};
}
