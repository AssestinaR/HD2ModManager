using System.Globalization;

namespace HD2ModCore.Domain;

// 衣柜编辑器的树。**临时结构**：按当前赢家 unit 现算，保存后立刻丢弃（§4.2）。
//
// 层次全部来自 unit 自带的语义（`slot` / `pieceType` / `bodyType`），不需要用户填：
//
//   pieceType（护甲 / 内衣）
//    └─ bodyType（纤细 / 健壮）        ← `Any` 的 unit 同时出现在两支下，是同一个身份
//        └─ slot（头盔 / 躯干 / 胯部…）
//            └─ 装饰「默认」            ← 暂时只有一个：构建产物里还没有装饰级分组
//                └─ section            ← 真正的最小单位
//
// 为什么"装饰"这一层现在恒为「默认」：`sections.json` 只记 unit 级语义，没有装饰分组。
// 旧 mod 的粗粒度装饰恰好就是"一个 unit = 一个装饰，拥有该 unit 的全部 section"（§4.6），
// 所以这一层先折叠成一个节点；将来构建管线带上装饰分组时，这一层自然长出真节点，
// 而权威数据（按 unit + section 下标存）一个字都不用改。
//
// The editor tree. It is temporary: computed from the current winning units and thrown away
// on save. All levels come from unit semantics, so nothing is user-entered. The decoration
// level is a single "default" node for now because the build artefact carries no decoration
// grouping yet; when it does, real nodes appear here without touching the authoritative data.

/// <summary>树节点类型。</summary>
public enum WardrobeTreeNodeKind
{
	/// <summary>根：这件护甲本身。</summary>
	Armour,

	/// <summary>身形变体：健壮 / 纤细。</summary>
	BodyType,

	/// <summary>槽位：头盔 / 胸部 / 胯部……</summary>
	Slot,

	/// <summary>一个 unit。它既是"一件可开关的东西"，也是 section 的容器。</summary>
	Unit,

	/// <summary>section：真正的最小单位。</summary>
	Section,
}

/// <summary>
/// 树节点。<c>Key</c> 稳定且唯一，用于界面上的展开/选中状态；
/// <c>Unit</c> 与 <c>SectionIndex</c> 只有 Unit/Section 节点有。
/// </summary>
public sealed record WardrobeTreeNode(
	WardrobeTreeNodeKind Kind,
	string Key,
	string Label,
	string? Unit,
	int? SectionIndex,
	IReadOnlyList<WardrobeTreeNode> Children)
{
	/// <summary>
	/// 该 unit 的 <c>bodyType</c> 是 <c>Any</c>，所以它同时挂在健壮和纤细两支下。
	/// 界面据此加锁链图标；两处切换天然同步，因为状态按 (unit, 下标) 存，不按节点存。
	/// </summary>
	public bool SharedAcrossBodies { get; init; }

	/// <summary>
	/// Unit 节点上是"护甲 / 内衣"。树上不再为它单开一层（同一件护甲的槽位里可能两种都有），
	/// 但丢了这条信息就没法分辨同一个槽位下的两个 unit，所以挂在行上做小标记。
	/// </summary>
	public string PieceTypeLabel { get; init; } = string.Empty;

	/// <summary>section 节点的三角形数，界面用来判断"这块到底多大"。</summary>
	public uint Triangles { get; init; }

	/// <summary>先序遍历全部后代（不含自己）。</summary>
	public IEnumerable<WardrobeTreeNode> Descendants()
	{
		foreach (var child in Children)
		{
			yield return child;
			foreach (var descendant in child.Descendants())
				yield return descendant;
		}
	}

	/// <summary>该子树下的全部 section 节点。</summary>
	public IEnumerable<WardrobeTreeNode> Sections()
		=> Descendants().Where(node => node.Kind == WardrobeTreeNodeKind.Section);
}

/// <summary>一个节点的勾选统计。</summary>
public readonly record struct WardrobeTreeCounts(int SectionCount, int DisabledCount)
{
	/// <summary>全禁用（界面画成未勾选）。</summary>
	public bool AllDisabled => SectionCount > 0 && DisabledCount == SectionCount;

	/// <summary>全启用（界面画成已勾选）。</summary>
	public bool AllEnabled => SectionCount > 0 && DisabledCount == 0;

	/// <summary>部分禁用（界面画成半选）。</summary>
	public bool IsMixed => !AllDisabled && !AllEnabled;
}

/// <summary>
/// 树的构建、统计与写入。全部是纯函数：输入 unit 表 + 工作区，输出树 / 新工作区，
/// 不碰文件、不碰界面——这样"勾一下到底写了什么"能离线测。
/// </summary>
public static class WardrobeTree
{
	private const string UnknownPieceType = "Unknown";
	private const string UnknownBodyType = "Unknown";
	private const string UnknownSlot = "Unknown";
	private const string AnyBodyType = "Any";

	// 固定顺序：界面每次打开都长一样，不随字典顺序漂移。
	// 身形按"健壮 → 纤细"排：这是游戏里更常见的那支在前，也是用户给的参考顺序。
	private static readonly string[] BodyTypeOrder = ["Stocky", "Slim"];
	private static readonly string[] SlotOrder =
	[
		"Helmet", "Torso", "Hip",
		"LeftShoulder", "RightShoulder", "LeftArm", "RightArm",
		"LeftLeg", "RightLeg",
	];

	/// <summary>
	/// 用一件护甲的赢家 unit 表构建树。根是这件护甲本身。
	/// `Any` 身形会被展开到健壮和纤细两支下——是同一个身份，不是两份数据。
	/// </summary>
	public static WardrobeTreeNode Build(string armourLabel, IReadOnlyList<WardrobeSectionMapUnit> units)
	{
		ArgumentNullException.ThrowIfNull(units);
		var rootKey = "armour";
		// Unit 的序号按**槽位**在整个 unit 集合上算，而不是在每个身形分支里各算一遍：
		// 否则 Any 的 unit 在健壮和纤细两支下会得到不同编号，看起来像两个不同的 unit。
		var ordinals = units
			.GroupBy(unit => Normalize(unit.Slot, UnknownSlot))
			.ToDictionary(
				group => group.Key,
				group => group
					.OrderBy(unit => unit.FileId, StringComparer.Ordinal)
					.Select((unit, ordinal) => (unit.FileId, ordinal))
					.ToDictionary(pair => pair.FileId, pair => pair.ordinal, StringComparer.Ordinal),
				StringComparer.Ordinal);
		var bodyNodes = new List<WardrobeTreeNode>();
		foreach (var bodyGroup in units
			.SelectMany(unit => ExpandBodies(unit))
			.GroupBy(pair => pair.Body)
			.OrderBy(group => OrderOf(group.Key, BodyTypeOrder))
			.ThenBy(group => group.Key, StringComparer.Ordinal))
		{
			var slotNodes = new List<WardrobeTreeNode>();
			foreach (var slotGroup in bodyGroup
				.GroupBy(pair => Normalize(pair.Unit.Slot, UnknownSlot))
				.OrderBy(group => OrderOf(group.Key, SlotOrder))
				.ThenBy(group => group.Key, StringComparer.Ordinal))
			{
				// Unit 的序号在整个槽位上算（不是在每个身形分支里各算一遍），
				// 这样 Any 的 unit 在健壮和纤细两支下是同一个编号——否则看起来像两个不同的 unit。
				var slotKey = Normalize(slotGroup.First().Unit.Slot, UnknownSlot);
				var unitNodes = slotGroup
					.Select(pair => pair.Unit)
					.OrderBy(unit => unit.FileId, StringComparer.Ordinal)
					.Select(unit => BuildUnitNode(
						rootKey,
						bodyGroup.Key,
						slotKey,
						unit,
						ordinals[slotKey][unit.FileId]))
					.ToArray();
				slotNodes.Add(new WardrobeTreeNode(
					WardrobeTreeNodeKind.Slot,
					$"{rootKey}/body:{bodyGroup.Key}/slot:{slotKey}",
					SlotLabel(slotKey),
					Unit: null,
					SectionIndex: null,
					unitNodes));
			}

			bodyNodes.Add(new WardrobeTreeNode(
				WardrobeTreeNodeKind.BodyType,
				$"{rootKey}/body:{bodyGroup.Key}",
				BodyLabel(bodyGroup.Key),
				Unit: null,
				SectionIndex: null,
				slotNodes));
		}

		return new WardrobeTreeNode(
			WardrobeTreeNodeKind.Armour,
			rootKey,
			armourLabel,
			Unit: null,
			SectionIndex: null,
			bodyNodes);
	}

	private static WardrobeTreeNode BuildUnitNode(
		string rootKey,
		string bodyType,
		string slot,
		WardrobeSectionMapUnit unit,
		int ordinal)
	{
		var unitKey = $"{rootKey}/body:{bodyType}/slot:{slot}/unit:{unit.FileId}";
		var sections = unit.Sections
			.OrderBy(section => section.Index)
			.Select(section => new WardrobeTreeNode(
				WardrobeTreeNodeKind.Section,
				$"{unitKey}/section:{section.Index}",
				$"#{section.Index} · {section.Triangles.ToString(CultureInfo.InvariantCulture)} 面",
				unit.FileId,
				section.Index,
				Array.Empty<WardrobeTreeNode>())
			{
				Triangles = section.Triangles,
			})
			.ToArray();
		return new WardrobeTreeNode(
			WardrobeTreeNodeKind.Unit,
			unitKey,
			$"Unit {ordinal.ToString(CultureInfo.InvariantCulture)} {unit.FileId}",
			unit.FileId,
			SectionIndex: null,
			sections)
		{
			SharedAcrossBodies = string.Equals(Normalize(unit.BodyType, UnknownBodyType), AnyBodyType, StringComparison.Ordinal),
			PieceTypeLabel = PieceTypeLabel(Normalize(unit.PieceType, UnknownPieceType)),
		};
	}

	/// <summary>把 `Any` 身形展开成两支；其余原样一支。</summary>
	private static IEnumerable<(WardrobeSectionMapUnit Unit, string Body)> ExpandBodies(WardrobeSectionMapUnit unit)
	{
		var body = Normalize(unit.BodyType, UnknownBodyType);
		if (!string.Equals(body, AnyBodyType, StringComparison.Ordinal))
		{
			yield return (unit, body);
			yield break;
		}

		yield return (unit, "Slim");
		yield return (unit, "Stocky");
	}

	/// <summary>统计一个子树在当前工作区里被禁用了多少 section。</summary>
	public static WardrobeTreeCounts Counts(
		WardrobeTreeNode node,
		uint armourItemId,
		WardrobeWorkspaceDocument workspace)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(workspace);
		var sections = node.Kind == WardrobeTreeNodeKind.Section
			? new[] { node }
			: node.Sections().ToArray();
		var disabled = sections.Count(section =>
			section.Unit is not null
			&& section.SectionIndex is not null
			&& workspace.IsDisabled(armourItemId, section.Unit, section.SectionIndex.Value));
		return new WardrobeTreeCounts(sections.Length, disabled);
	}

	/// <summary>
	/// 把某个子树整体设为启用或禁用，返回新的工作区。
	///
	/// 前提：该护甲的工作区条目已经是 <see cref="WardrobeMaskMode.Own"/>。
	/// 继承模式下改条目会和 <c>mode</c> 自相矛盾，所以这里直接拒绝——
	/// 调用方（编辑器）必须先通过 <see cref="MakeOwn"/> 把继承关系落成一个快照。
	/// </summary>
	public static WardrobeWorkspaceDocument SetEnabled(
		WardrobeWorkspaceDocument workspace,
		uint armourItemId,
		WardrobeTreeNode node,
		bool enabled)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(node);
		var armour = workspace.FindArmour(armourItemId);
		if (armour is null || armour.Mode != WardrobeMaskMode.Own)
		{
			throw new InvalidOperationException(
				$"护甲 {armourItemId} 还没有独立掩码（当前是继承）；请先调用 MakeOwn 把继承关系落成快照再改。");
		}

		var targets = node.Kind == WardrobeTreeNodeKind.Section
			? new[] { node }
			: node.Sections().ToArray();
		var byUnit = armour.Units.ToDictionary(unit => unit.Unit, unit => unit.Off.ToHashSet(), StringComparer.Ordinal);
		foreach (var section in targets)
		{
			if (section.Unit is null || section.SectionIndex is null)
				continue;
			if (!byUnit.TryGetValue(section.Unit, out var indices))
			{
				indices = new HashSet<int>();
				byUnit.Add(section.Unit, indices);
			}

			if (enabled)
				indices.Remove(section.SectionIndex.Value);
			else
				indices.Add(section.SectionIndex.Value);
		}

		// 空条目要删掉：工作区里"有 unit 但一条都不关"和"没有这个 unit"是同一件事，
		// 留着只会让文件里堆一堆空数组。
		var units = byUnit
			.Where(pair => pair.Value.Count > 0)
			.Select(pair => new WardrobeWorkspaceUnit(pair.Key, pair.Value.Order().ToArray()))
			.OrderBy(unit => unit.Unit, StringComparer.Ordinal)
			.ToArray();
		return Replace(workspace, armour with { Units = units });
	}

	/// <summary>
	/// 把某件护甲从"继承"落成"独立"，并用来源当前的禁用集合作为初值。
	///
	/// 为什么要复制而不是从空开始：用户点第一下之前看到的就是继承来的那份，
	/// 从空开始会让其余已隐藏的 section 突然全部出现——那不是"开始独立编辑"，
	/// 那是"把所有设置清掉"。
	/// </summary>
	public static WardrobeWorkspaceDocument MakeOwn(
		WardrobeWorkspaceDocument workspace,
		uint armourItemId,
		WardrobeWorkspaceArmour? inheritedFrom)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var units = inheritedFrom is null || inheritedFrom.Mode != WardrobeMaskMode.Own
			? Array.Empty<WardrobeWorkspaceUnit>()
			: inheritedFrom.Units
				.Where(unit => unit.Off.Count > 0)
				.Select(unit => unit with { Off = unit.Off.Distinct().Order().ToArray() })
				.OrderBy(unit => unit.Unit, StringComparer.Ordinal)
				.ToArray();
		return Replace(workspace, new WardrobeWorkspaceArmour(armourItemId, WardrobeMaskMode.Own, units));
	}

	/// <summary>把某件护甲恢复成继承（丢弃它的独立掩码）。</summary>
	public static WardrobeWorkspaceDocument MakeInherit(WardrobeWorkspaceDocument workspace, uint armourItemId)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var armours = workspace.Armours
			.Where(armour => armour.ItemId != armourItemId)
			.ToArray();
		return new WardrobeWorkspaceDocument(workspace.Schema, workspace.Revision, armours);
	}

	private static WardrobeWorkspaceDocument Replace(WardrobeWorkspaceDocument workspace, WardrobeWorkspaceArmour armour)
	{
		var armours = workspace.Armours
			.Where(candidate => candidate.ItemId != armour.ItemId)
			.Append(armour)
			.OrderBy(candidate => candidate.ItemId)
			.ToArray();
		return new WardrobeWorkspaceDocument(workspace.Schema, workspace.Revision, armours);
	}

	// --- 显示名 -------------------------------------------------------------

	private static string Normalize(string? value, string fallback)
		=> string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

	private static int OrderOf(string value, IReadOnlyList<string> order)
	{
		for (var index = 0; index < order.Count; index++)
		{
			if (string.Equals(order[index], value, StringComparison.Ordinal))
				return index;
		}
		// 未知值排在已知之后，但仍按名字稳定排序。
		return order.Count + 1;
	}

	/// <summary>套组显示名。未知值原样显示，不要吞掉——那会让人以为数据丢了。</summary>
	public static string PieceTypeLabel(string pieceType) => pieceType switch
	{
		"Armor" => "护甲",
		"Undergarment" => "内衣",
		UnknownPieceType => "未标注套组",
		_ => pieceType,
	};

	/// <summary>身形显示名。</summary>
	public static string BodyLabel(string bodyType) => bodyType switch
	{
		"Slim" => "纤细",
		"Stocky" => "健壮",
		AnyBodyType => "通用",
		UnknownBodyType => "未标注身形",
		_ => bodyType,
	};

	/// <summary>槽位显示名。</summary>
	public static string SlotLabel(string slot) => slot switch
	{
		"Helmet" => "头盔",
		"Torso" => "胸部",
		"Hip" => "胯部",
		"LeftShoulder" => "左肩",
		"RightShoulder" => "右肩",
		"LeftArm" => "左臂",
		"RightArm" => "右臂",
		"LeftLeg" => "左腿",
		"RightLeg" => "右腿",
		UnknownSlot => "未标注槽位",
		_ => slot,
	};
}
