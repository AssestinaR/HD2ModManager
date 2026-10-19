using System.Text.Json.Serialization;

namespace HD2ModCore.Domain;

// 衣柜的**权威数据**：按护甲 itemId 存，与 mod 无关。
//
// 为什么与 mod 无关：用户勾掉的是"这件护甲上第 N 块几何不该出现"，而不是
// "某个 mod 的第 N 块几何"。换 mod 之后这个意图仍然成立（哪怕新 mod 的第 N 块
// 恰好是别的东西——那是用户需要承担的风险，释放视觉来源即可全部清除）。
// 因此这里**不存指纹**：指纹是构建产物的属性，存进来就会把权威数据绑死在一次构建上。
//
// 与 `armor-registry.workspace.json` 同一个先例：管理器只写工作区
// `<mod库>/.hd2modmanager/wardrobe.workspace.json`，再由它编译成
// `<ArmorRegistry>/wardrobe.masks` 给 Lua 读。直接改编译产物会被下一次应用覆盖。
//
// 树结构（套组 → 身形 → 槽位 → 装饰 → section）**不落盘**：它是按当前赢家 unit
// 临时构建的视图，保存后立刻丢弃。落盘的只有"某 unit 的第 N 个 section 是禁用"。
//
// The authoritative store is keyed by armour item id, not by mod: the user's intent is
// "this piece of geometry on this armour should not appear", which outlives whichever mod
// happens to supply the model. Fingerprints are deliberately absent — they are a property
// of a build artefact, and storing them would pin the authoritative data to one build.

/// <summary>
/// 一件护甲的掩码模式。
/// <para><see cref="Inherit"/>：作为映射目标时，原样继承视觉来源的全部禁用状态。</para>
/// <para><see cref="Own"/>：不再继承，只用自己的那份（可以是空集 = "一块都不关"）。</para>
/// 视觉来源恒为 <see cref="Own"/>：它没有来源可继承。
/// </summary>
public enum WardrobeMaskMode
{
	/// <summary>继承视觉来源的禁用状态（映射目标的默认）。</summary>
	Inherit,

	/// <summary>用自己的禁用状态，不再继承。</summary>
	Own,
}

/// <summary>
/// 一个 unit 上被禁用的 section 下标集合。
/// 只存"禁用"：缺失 = 启用，这样新出现的 section 自动可见，不必逐个补写。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WardrobeWorkspaceUnit(string Unit, IReadOnlyList<int> Off);

/// <summary>
/// 一件护甲的权威掩码。<c>ItemId</c> 是护甲装备的 itemId（不是 mod、不是 unit）。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WardrobeWorkspaceArmour(
	uint ItemId,
	WardrobeMaskMode Mode,
	IReadOnlyList<WardrobeWorkspaceUnit> Units);

/// <summary>
/// `wardrobe.workspace.json` 的内存表示。
/// 文件里**只出现有内容的护甲**：没有条目的护甲一律按"继承"处理，
/// 于是"从未编辑过"和"显式选择继承"是同一个状态，不需要额外记录。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WardrobeWorkspaceDocument(
	string Schema,
	int Revision,
	IReadOnlyList<WardrobeWorkspaceArmour> Armours)
{
	/// <summary>当前格式标识。格式变了就换字符串，让旧文件显式失效。</summary>
	public const string CurrentSchema = "hd2mod.wardrobe/1";

	/// <summary>空工作区：没有任何护甲被编辑过。</summary>
	public static WardrobeWorkspaceDocument Empty { get; } = new(
		CurrentSchema,
		0,
		Array.Empty<WardrobeWorkspaceArmour>());

	/// <summary>是否一片空白（用于"用户从未编辑过"的判断）。</summary>
	public bool IsEmpty => Armours.Count == 0;

	/// <summary>按 itemId 找一件护甲；找不到返回 null（= 继承）。</summary>
	public WardrobeWorkspaceArmour? FindArmour(uint itemId)
	{
		foreach (var armour in Armours)
		{
			if (armour.ItemId == itemId)
				return armour;
		}
		return null;
	}

	/// <summary>
	/// 某件护甲最终是否禁用某个 section。这是编辑器和编译共用的唯一判定入口，
	/// 避免"界面显示已关、编译出来没关"这类两边各写一遍导致的偏差。
	/// </summary>
	public bool IsDisabled(uint itemId, string unit, int sectionIndex)
	{
		var armour = FindArmour(itemId);
		if (armour is null || armour.Mode != WardrobeMaskMode.Own)
			return false;
		foreach (var entry in armour.Units)
		{
			if (!string.Equals(entry.Unit, unit, StringComparison.Ordinal))
				continue;
			foreach (var index in entry.Off)
			{
				if (index == sectionIndex)
					return true;
			}
		}
		return false;
	}

	/// <summary>一件护甲一共禁用了多少块几何。摘要与诊断用。</summary>
	public int DisabledCount(uint itemId)
	{
		var armour = FindArmour(itemId);
		return armour is null || armour.Mode != WardrobeMaskMode.Own
			? 0
			: armour.Units.Sum(entry => entry.Off.Count);
	}
}
