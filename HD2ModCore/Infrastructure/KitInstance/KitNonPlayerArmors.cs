using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 「非玩家」护甲与头盔记录表（专属多选的「全选」与护甲映射选品器都要用到它们）。
/// <para>
/// 装备权威表里没有「玩家可穿」字段，本地游戏数据里也没有依据（战争债券和超级商店的数据在服务器上）。
/// 同名的多条记录绝大多数是玩家能拥有的不同版本，一般只差配色；真正不是玩家穿的护甲只有下面 3 条，
/// 对应同口径缺失的非玩家头盔有 4 条。以后出现新的非玩家记录，往对应表里加一行即可。
/// </para>
/// <para>
/// <b>来源（规划 kit-instance-multi-target-plan-20261114.md §6.3 与 armor-mapping-picker-plan-20261114.md §3.1）：</b>
/// </para>
/// <list type="number">
/// <item>OneClickArmorSet 的 <c>RESEARCH.md</c>（MIT，2026）：读游戏内 offers 表，结论是同一件护甲可以有多个可拥有的副本
/// （B-01 有 9 个、B-24 有 3 个、FS-05 有 2 个），有些副本不在 offers 表里，谁也拿不到。</item>
/// <item>helldivers-2/json 的 <c>items/armor/armor.json</c>（官方 Items API 整理，2025-02 之后没再更新，新护甲没收录）：
/// 护甲在三组同名记录里各漏掉一条；头盔（slot 0）按同一口径核对并对照本地权威索引，共 4 条（其中 CM-09 <c>0x4db09ca4</c> 为同名重复头盔）。</item>
/// </list>
/// FS-05 124623533 另有实测依据：它是民主官 NPC 穿的那套。
/// </summary>
public static class KitNonPlayerArmors
{
	/// <summary>FS-05 民主官 NPC 护甲记录 ID。</summary>
	public const uint DemocracyOfficerArmorId = 124623533u;

	/// <summary>
	/// 民主官 NPC 的**头**：一个不属于任何护甲记录的 unit（NPC 头部资源，材质共 5 个）。
	/// <para>
	/// <b>2026-11-24 探针 v7 实机重新量过</b>（旧记录 <c>7862870bc9822eeb</c> 作废）：
	/// 旧 id 在整轮全量扫描 + 36 个候选 id 的 <c>units_by_resource(IdString64)</c> 查询里
	/// **一个实例都没有**，所以按它隐藏从来没有生效过。新值的依据：
	/// </para>
	/// <list type="number">
	/// <item>它坐在民主官的根 <c>15678e360612b805</c> **正坐标上**（dx=dy=dz=0），全船**只有 1 个实例**。</item>
	/// <item>它属于 <c>kit_instance_hider.HEAD_CANDIDATES</c> 那 31 个 NPC 头部 unit 之一。</item>
	/// <item>飞船上的 NPC 结构一致：每个都是「84～95 节点的根 + 一个 179 节点的伴生 unit」，
	/// 女舰长 / 维修技术员 / SEAF / 守岸人那几套的 179 节点伴生 unit 全在这个候选表里。</item>
	/// </list>
	/// <para>
	/// 仍是"一个资源 id"，所以按实例隐藏（<c>UV.set_unit_visibility</c>）而不是换材质 ——
	/// 换材质是全局的，会把所有用这套 NPC 头的角色一起改掉。
	/// </para>
	/// </summary>
	public const ulong DemocracyOfficerHeadUnitId = 0x387f271c7f49614fUL;

	/// <summary>
	/// 民主官的根 unit（2026-11-24 v7 实测）：<c>anim=true</c>、84 个场景节点、全船唯一，
	/// 他的 17 个护甲件（含斗篷、独有左肩）全在这个坐标上。
	/// <b>它不出现任何护甲记录里</b>，所以打映射永远不会改写它 —— 拿它当"定位/身份"标记，
	/// 就不用像以前那样为了留信标而牺牲一两个槽位不做替换。
	/// </summary>
	public const ulong DemocracyOfficerRootUnitId = 0x15678e360612b805UL;

	/// <summary>
	/// 民主官独有左肩（他这套 NPC 记录的 slot8 件）——兜底标记：他穿这套时一定有，
	/// 而且只有他穿（<c>kit-instance.rules</c> 一直拿它当 marker）。缺点是被整身替换时会被藏掉。
	/// </summary>
	public const ulong DemocracyOfficerShoulderUnitId = 0x82595b5d94c57343UL;

	/// <summary>
	/// 这条护甲记录穿在身上时、除自身部件外还要一并按实例隐藏的 unit（目前只有民主官的头）。
	/// <b>只在模型的头真的会显示时才用它</b>（头身合并生效）：否则民主官会没有头。
	/// </summary>
	public static IReadOnlyList<ulong> ExtraHideUnits(uint itemId)
		=> itemId == DemocracyOfficerArmorId ? [DemocracyOfficerHeadUnitId]
			: KitNpcRecords.TryGet(itemId, out var npc) ? npc.ExtraHideUnits
			: [];

	/// <summary>unit 资源的类型 id（`0xe0a48d0be9a7453f` = unit）。</summary>
	private const ulong UnitAssetTypeId = 0xe0a48d0be9a7453fUL;

	/// <summary>
	/// 给民主官那套补一件 **slot0「头」**（他的头 unit 不属于任何护甲记录，权威表里没有它）。
	/// <para>
	/// 补上之后，"把民主官当映射目标"就和普通护甲**完全同构**，两个行为都由既有的换件链路给出：
	/// </para>
	/// <list type="bullet">
	/// <item>视觉来源有 slot0 的件 ⇒ <c>link=头,来源的头</c>：引擎按正常换件把它挂在他头上
	/// （不需要克隆、不需要按骨名重绑 —— 位置和姿势都由引擎给）。</item>
	/// <item>视觉来源没有 ⇒ <c>link=头,隐藏占位</c>：他的头消失（"头已并进身体"那种素体就该这样）。</item>
	/// </list>
	/// <para>
	/// 以前这件事靠"运行时在锚点旁边按实例藏头"这条专线（<c>repair=…,0,…,头,-</c>），
	/// 实现上要额外一条通路，而且和换件结果互相打架；现在是同一份数据、同一条路。
	/// </para>
	/// <para>幂等：已经有这个件就原样返回。</para>
	/// </summary>
	public static IReadOnlyList<GameDataAuthorityKit> WithOfficerHead(IReadOnlyList<GameDataAuthorityKit> kits)
	{
		ArgumentNullException.ThrowIfNull(kits);
		var index = -1;
		for (var i = 0; i < kits.Count; i++)
		{
			if (kits[i].ItemId != DemocracyOfficerArmorId) continue;
			index = i;
			break;
		}
		if (index < 0) return kits;

		var kit = kits[index];
		var headKey = new AssetKey(UnitAssetTypeId, DemocracyOfficerHeadUnitId);
		foreach (var piece in kit.Pieces)
			if (piece.UnitAssetKey == headKey) return kits;

		var pieces = new List<GameDataAuthorityPiece>(kit.Pieces.Count + 1);
		pieces.AddRange(kit.Pieces);
		// 身形取 Any（头与身形无关）；Weight / ToneVariations 对换件没有影响，取与他其它件同类的最小值。
		pieces.Add(new GameDataAuthorityPiece(
			RawBodyType: 3u,
			BodyVariant: UnitMeshBodyVariant.Any,
			Slot: 0u,
			PartKind: UnitMeshPartKind.Helmet,
			PieceType: 0u,
			Weight: 1u,
			ToneVariations: 0,
			UnitAssetKey: headKey,
			Resources: Array.Empty<GameDataAuthorityResourceReference>()));

		var result = kits.ToArray();
		result[index] = kit with { Pieces = pieces };
		return result;
	}

	/// <summary>
	/// 特殊 NPC：选品器里单独成行（带 NPC 标注、只在「套装」页出现），映射要求头并进身体。
	/// 民主官（穿 FS-05 的那条记录）和飞船 NPC 伪记录（女舰长、维修技术员，见 <see cref="KitNpcRecords"/>）。
	/// </summary>
	public static bool IsSpecialNpc(uint itemId) => itemId == DemocracyOfficerArmorId || KitNpcRecords.IsNpc(itemId);

	/// <summary>特殊 NPC 在选品器里的标注（「民主官 NPC」「NPC」…）；不是特殊 NPC 返回 <c>null</c>。</summary>
	public static string? NpcTag(uint itemId)
		=> itemId == DemocracyOfficerArmorId ? "民主官 NPC"
			: KitNpcRecords.TryGet(itemId, out var npc) ? npc.Tag
			: null;

	/// <summary>一条非玩家记录。<paramref name="Label"/> 就是下拉里显示的标注。</summary>
	public sealed record Entry(uint ItemId, string Label);

	public static IReadOnlyList<Entry> Entries { get; } =
	[
		new(124623533u, "非玩家（民主官 NPC）"),    // FS-05，已实测
		new(KitNpcRecords.FemaleCaptainItemId, "非玩家（NPC）"),   // 管理器自造的飞船 NPC 伪记录，见 KitNpcRecords
		new(KitNpcRecords.TechnicianItemId, "非玩家（NPC）"),
		new(KitNpcRecords.SeafSoldierItemId, "非玩家（NPC）"),
		new(2107035846u, "非玩家（拿不到）"),       // CM-09
		new(1969314276u, "非玩家（拿不到）"),       // SC-30
	];

	/// <summary>非玩家头盔记录表（与官方 Items API / 本地权威索引核对）。</summary>
	public static IReadOnlyList<Entry> HelmetEntries { get; } =
	[
		new(1303420068u, "非玩家（拿不到）"),       // 0x4db09ca4 CM-09“碎骨杀手”
		new(2015397810u, "非玩家（拿不到）"),       // 0x782087b2 SC-30 (0xd8bce5f1)
		new(2606482720u, "非玩家（拿不到）"),       // 0x9b5bc520 SC-30 (0xd8bce5f1)
		new(88270577u, "非玩家（拿不到）"),         // 0x0542e6f1 SC-34 (0x2eec8681)
	];

	/// <summary>全部非玩家头盔 item_id 集合。</summary>
	public static IReadOnlySet<uint> AllHelmetIds { get; } = HelmetEntries.Select(entry => entry.ItemId).ToHashSet();

	/// <summary>这条护甲记录是不是非玩家；是则给出标注。</summary>
	public static bool TryGetLabel(uint itemId, out string label)
	{
		foreach (var entry in Entries)
		{
			if (entry.ItemId != itemId) continue;
			label = entry.Label;
			return true;
		}

		label = string.Empty;
		return false;
	}

	public static bool Contains(uint itemId) => TryGetLabel(itemId, out _);

	/// <summary>判断是否为已知非玩家头盔记录。</summary>
	public static bool IsNonPlayerHelmet(uint itemId) => AllHelmetIds.Contains(itemId);

	/// <summary>按装备类别判断是否为非玩家记录（护甲查护甲表，头盔查头盔表）。</summary>
	public static bool IsNonPlayerKit(uint itemId, GameDataEquipmentKitKind kind)
		=> kind == GameDataEquipmentKitKind.Helmet ? IsNonPlayerHelmet(itemId) : Contains(itemId);
}
