using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

/// <summary>
/// 编辑器树（临时结构）的构建、统计与写入。
///
/// 这里锁三件事：
/// 1. 层次是「护甲 → 身形 → 槽位 → Unit → section」，全部来自 unit 语义；
///    `Any` 身形同时出现在两支下，但**只有一份数据**，连 Unit 编号都一致；
/// 2. 勾一下写到工作区的就是"某 unit 的第 N 个 section 是禁用"，没有别的；
/// 3. 从"继承"转"独立"时用来源的当前状态做初值——否则用户点一下会看到其余
///    已隐藏的 section 突然全回来。
/// </summary>
public sealed class WardrobeTreeTests
{
	private const string ArmourLabel = "RE-2310 荣誉卫队";
	private const string TorsoArmorSlim = "3c09e08493ee44b8";
	private const string TorsoUnderAny = "cae35ee15168145f";
	private const string HelmetUnderAny = "af431352d9b95e83";
	private const string LegacyUnit = "00000000000000aa";
	private const uint Target = 1745464610;
	private const uint Source = 820333399;

	private static WardrobeSectionMapUnit Unit(string fileId, string slot, string pieceType, string bodyType, int sections)
		=> new(
			fileId,
			slot,
			pieceType,
			bodyType,
			"Medium",
			sections,
			true,
			Enumerable.Range(0, sections)
				.Select(index => new WardrobeSectionMapEntry(
					index,
					"a47787804c45e1a9",
					(uint)(100 + index),
					WardrobeSectionFingerprint.Format(0xa47787804c45e1a9, (uint)(100 + index)),
					new[] { new WardrobeSectionToggle(0, 0, index, "0x00002010", (uint)(300 + index)) }))
				.ToArray());

	private static WardrobeSectionMapUnit[] Units() =>
	[
		Unit(TorsoArmorSlim, "Torso", "Armor", "Slim", 2),
		Unit(TorsoUnderAny, "Torso", "Undergarment", "Any", 3),
		Unit(HelmetUnderAny, "Helmet", "Undergarment", "Any", 2),
	];

	private static WardrobeTreeNode Build(params WardrobeSectionMapUnit[] units)
		=> WardrobeTree.Build(ArmourLabel, units.Length == 0 ? Units() : units);

	private static WardrobeTreeNode Find(WardrobeTreeNode root, string key)
		=> new[] { root }.Concat(root.Descendants())
			.Single(node => string.Equals(node.Key, key, StringComparison.Ordinal));

	private static WardrobeWorkspaceDocument Own(params WardrobeWorkspaceUnit[] units)
		=> new(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[] { new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Own, units) });

	[Fact]
	public void Build_RootIsTheArmourItself()
	{
		var root = Build();
		Assert.Equal(WardrobeTreeNodeKind.Armour, root.Kind);
		Assert.Equal(ArmourLabel, root.Label);
		// 身形：健壮在前、纤细在后，与游戏里的常见顺序一致。
		Assert.Equal(new[] { "健壮", "纤细" }, root.Children.Select(node => node.Label).ToArray());
	}

	[Fact]
	public void Build_HasNoPieceTypeLevel()
	{
		// 护甲/内衣不再单独成层：同一件护甲的同一个槽位里可能两种都有，
		// 拆成两层会让"这个槽位到底有几个 unit"变得难以看清。
		var root = Build();
		Assert.DoesNotContain(
			root.Descendants(),
			node => node.Label is "护甲" or "内衣");
	}

	[Fact]
	public void Build_OrdersSlotsAnatomically()
	{
		var root = Build();
		var stocky = root.Children[0];
		Assert.Equal(new[] { "头盔", "胸部" }, stocky.Children.Select(node => node.Label).ToArray());
	}

	[Fact]
	public void Build_ExpandsAnyBodyIntoBothBranchesWithTheSameUnitNumber()
	{
		var root = Build();
		var stocky = Find(root, "armour/body:Stocky/slot:Torso/unit:" + TorsoUnderAny);
		var slim = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoUnderAny);
		Assert.Equal(stocky.Label, slim.Label);
		// 编号在槽位范围内统一，所以两支下是同一个 "Unit N"。
		Assert.Equal("Unit 1 " + TorsoUnderAny, slim.Label);
		Assert.True(stocky.SharedAcrossBodies);
		Assert.True(slim.SharedAcrossBodies);
		Assert.NotEqual(stocky.Key, slim.Key);
	}

	[Fact]
	public void Build_UnitNodesCarryThePieceTypeTag()
	{
		// 丢了 pieceType 就没法分辨同一个槽位下的两个 unit，所以挂在行上做小标记。
		var root = Build();
		var armorUnit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoArmorSlim);
		Assert.Equal("护甲", armorUnit.PieceTypeLabel);
		var undergarmentUnit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoUnderAny);
		Assert.Equal("内衣", undergarmentUnit.PieceTypeLabel);
	}

	[Fact]
	public void Build_PropagatesTheLinkedFlagUpWhenEveryUnitIsAny()
	{
		var root = Build(
			Unit(TorsoUnderAny, "Torso", "Undergarment", "Any", 2),
			Unit(HelmetUnderAny, "Helmet", "Undergarment", "Any", 1));
		// 所有子级别都链接 ⇒ 父级也链接。
		Assert.True(root.SharedAcrossBodies);
		foreach (var node in root.Descendants().Where(node => node.Kind != WardrobeTreeNodeKind.Section))
			Assert.True(node.SharedAcrossBodies, node.Key);
	}

	[Fact]
	public void Build_DoesNotPropagateWhenOneUnitBelongsToASingleBody()
	{
		// 默认 fixture 里 TorsoArmorSlim 只属于纤细，所以「胸部」这一支不能标链接。
		var root = Build();
		Assert.False(root.SharedAcrossBodies);
		Assert.True(Find(root, "armour/body:Slim/slot:Helmet").SharedAcrossBodies);
		Assert.False(Find(root, "armour/body:Slim/slot:Torso").SharedAcrossBodies);
	}

	[Fact]
	public void Build_KeysAreUniqueAcrossTheWholeTree()
	{
		var root = Build();
		var keys = new[] { root }.Concat(root.Descendants()).Select(node => node.Key).ToArray();
		Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void Build_PutsUnknownSemanticsIntoFallbackGroups()
	{
		var root = Build(Unit(LegacyUnit, string.Empty, string.Empty, string.Empty, 1));
		var body = Assert.Single(root.Children);
		Assert.Equal("未标注身形", body.Label);
		Assert.Equal("未标注槽位", Assert.Single(body.Children).Label);
	}

	[Fact]
	public void Build_SectionNodesCarryIndexAndTriangles()
	{
		var root = Build();
		var unit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoArmorSlim);
		Assert.Equal(2, unit.Children.Count);
		Assert.Equal(0, unit.Children[0].SectionIndex);
		Assert.Equal(100u, unit.Children[0].Triangles);
		Assert.Equal(101u, unit.Children[1].Triangles);
	}

	[Fact]
	public void Counts_ReflectTheWorkspace()
	{
		var root = Build();
		var unit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoArmorSlim);
		Assert.True(WardrobeTree.Counts(unit, Target, WardrobeWorkspaceDocument.Empty).AllEnabled);

		var workspace = Own(new WardrobeWorkspaceUnit(TorsoArmorSlim, new[] { 0 }));
		var counts = WardrobeTree.Counts(unit, Target, workspace);
		Assert.Equal(2, counts.SectionCount);
		Assert.Equal(1, counts.DisabledCount);
		Assert.True(counts.IsMixed);
	}

	[Fact]
	public void SetEnabled_OnAUnitTogglesEverySectionItOwns()
	{
		var root = Build();
		var unit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoArmorSlim);
		var workspace = Own();
		var disabled = WardrobeTree.SetEnabled(workspace, Target, unit, enabled: false);
		Assert.Equal(new[] { 0, 1 }, disabled.FindArmour(Target)!.Units.Single().Off.ToArray());
		var enabledAgain = WardrobeTree.SetEnabled(disabled, Target, unit, enabled: true);
		// 一条都不关的 unit 条目要删掉，别在文件里堆空数组。
		Assert.Empty(enabledAgain.FindArmour(Target)!.Units);
	}

	[Fact]
	public void SetEnabled_OnTheRootTogglesEverything()
	{
		var root = Build();
		var workspace = WardrobeTree.SetEnabled(Own(), Target, root, enabled: false);
		// 7 个真实身份（Any 的两支是同一份数据，只写一次）。
		Assert.Equal(7, workspace.DisabledCount(Target));
	}

	[Fact]
	public void SetEnabled_OnOneBodyBranchIsVisibleInTheOther()
	{
		// 状态按 (unit, 下标) 存、不按节点存，所以两支天然同步。
		var root = Build();
		var stocky = Find(root, "armour/body:Stocky/slot:Torso/unit:" + TorsoUnderAny);
		var slim = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoUnderAny);
		var workspace = WardrobeTree.SetEnabled(Own(), Target, stocky, enabled: false);
		Assert.True(WardrobeTree.Counts(slim, Target, workspace).AllDisabled);
	}

	[Fact]
	public void SetEnabled_RejectsAnArmourThatStillInherits()
	{
		var root = Build();
		var unit = Find(root, "armour/body:Slim/slot:Torso/unit:" + TorsoArmorSlim);
		var inheriting = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[] { new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Inherit, Array.Empty<WardrobeWorkspaceUnit>()) });
		// 继承模式下写条目会和 mode 自相矛盾；编辑器必须先 MakeOwn。
		Assert.Throws<InvalidOperationException>(
			() => WardrobeTree.SetEnabled(inheriting, Target, unit, enabled: false));
	}

	[Fact]
	public void MakeOwn_SeedsFromTheSourceSnapshot()
	{
		var source = new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
		{
			new WardrobeWorkspaceUnit(TorsoUnderAny, new[] { 2, 0 }),
		});
		var workspace = WardrobeTree.MakeOwn(WardrobeWorkspaceDocument.Empty, Target, source);
		var own = workspace.FindArmour(Target)!;
		Assert.Equal(WardrobeMaskMode.Own, own.Mode);
		// 初值是来源的当前状态，且已排序——用户点第一下之前看到什么，独立后还是什么。
		Assert.Equal(new[] { 0, 2 }, own.Units.Single().Off.ToArray());
	}

	[Fact]
	public void MakeOwn_WithoutASourceStartsEmpty()
	{
		var workspace = WardrobeTree.MakeOwn(WardrobeWorkspaceDocument.Empty, Target, null);
		var own = workspace.FindArmour(Target)!;
		Assert.Equal(WardrobeMaskMode.Own, own.Mode);
		Assert.Empty(own.Units);
	}

	[Fact]
	public void MakeInherit_DropsTheArmoursOwnMask()
	{
		var workspace = Own(new WardrobeWorkspaceUnit(TorsoArmorSlim, new[] { 0 }));
		var restored = WardrobeTree.MakeInherit(workspace, Target);
		Assert.Null(restored.FindArmour(Target));
	}

	[Fact]
	public void Sections_CountsNodesIncludingBothBodyBranches()
	{
		var root = Build();
		var nodes = root.Sections().ToArray();
		// 节点数是 12（`Any` 的两个 unit 各出现两次），但真实身份只有 7 个——
		// 这正是"同一份数据、两处显示"的代价，也是为什么状态必须按 (unit, 下标) 存。
		Assert.Equal(12, nodes.Length);
		Assert.Equal(7, nodes
			.Select(node => (node.Unit, node.SectionIndex))
			.Distinct()
			.Count());
	}
}
