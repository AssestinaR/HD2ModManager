namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 「非玩家」护甲记录表（专属多选的「全选」要跳过它们，下拉里也要标出来）。
/// <para>
/// 装备权威表里没有「玩家可穿」字段，本地游戏数据里也没有依据（战争债券和超级商店的数据在服务器上）。
/// 同名的多条记录绝大多数是玩家能拥有的不同版本，一般只差配色；真正不是玩家穿的只有下面 3 条。
/// 其余有名字的护甲一律算玩家护甲。以后出现新的非玩家记录，往 <see cref="Entries"/> 里加一行即可。
/// </para>
/// <para>
/// <b>来源（规划 kit-instance-multi-target-plan-20261114.md §6.3，两份社区依据结论一致）：</b>
/// </para>
/// <list type="number">
/// <item>OneClickArmorSet 的 <c>RESEARCH.md</c>（MIT，2026）：读游戏内 offers 表，结论是同一件护甲可以有多个可拥有的副本
/// （B-01 有 9 个、B-24 有 3 个、FS-05 有 2 个），有些副本不在 offers 表里，谁也拿不到。</item>
/// <item>helldivers-2/json 的 <c>items/armor/armor.json</c>（官方 Items API 整理，2025-02 之后没再更新，新护甲没收录）：
/// 在三组同名记录里各漏掉一条，正好是下面三条。</item>
/// </list>
/// FS-05 124623533 另有实测依据：它是民主官 NPC 穿的那套。
/// </summary>
public static class KitNonPlayerArmors
{
	/// <summary>一条非玩家记录。<paramref name="Label"/> 就是下拉里显示的标注。</summary>
	public sealed record Entry(uint ItemId, string Label);

	public static IReadOnlyList<Entry> Entries { get; } =
	[
		new(124623533u, "非玩家（民主官 NPC）"),    // FS-05，已实测
		new(2107035846u, "非玩家（拿不到）"),       // CM-09
		new(1969314276u, "非玩家（拿不到）"),       // SC-30
	];

	/// <summary>这条记录是不是非玩家；是则给出标注。</summary>
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
}
