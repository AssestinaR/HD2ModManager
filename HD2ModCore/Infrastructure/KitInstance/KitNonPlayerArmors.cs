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
	/// 民主官 NPC 的脸：一个不属于任何护甲记录的 unit（飞船上的 NPC 头部，材质共 5 个）。
	/// <para>
	/// 依据（2026-11-16 实机，Hider 0.4.4 的 <c>officer_near</c> 清单）：民主官标记件 0.6 米内共 36 个 unit，
	/// 其中只有它同时满足「不在任何护甲记录里」和「用到了『守岸人 替换 女舰长』Mod 覆盖的原版材质」——
	/// 这正是装了那个 Mod 后民主官脸消失的原因。护甲映射的隐藏集管不到它，所以单独补进来。
	/// 同一处还有 15678e36…、5b390845…、8085023e…、9512a3db…、be0be6b1… 等非护甲 unit，尚未确认是不是头发/眼睛。
	/// </para>
	/// </summary>
	public const ulong DemocracyOfficerFaceUnitId = 0x7862870bc9822eebUL;

	/// <summary>
	/// 这条护甲记录穿在身上时、除自身部件外还要一并按实例隐藏的 unit（目前只有民主官的脸）。
	/// <b>只在模型的头真的会显示时才用它</b>（头身合并生效）：否则民主官会没有头。
	/// </summary>
	public static IReadOnlyList<ulong> ExtraHideUnits(uint itemId)
		=> itemId == DemocracyOfficerArmorId ? [DemocracyOfficerFaceUnitId]
			: KitNpcRecords.TryGet(itemId, out var npc) ? npc.ExtraHideUnits
			: [];

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
