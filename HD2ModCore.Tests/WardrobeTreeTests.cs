using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

/// <summary>
/// 编辑器树（临时结构）的构建、统计与写入。
///
/// 这里锁三件事：
/// 1. 层次全部来自 unit 语义，`Any` 身形同时出现在两支下但**只有一份数据**；
/// 2. 勾一下写到工作区的就是"某 unit 的第 N 个 section 是禁用"，没有别的；
/// 3. 从"继承"转"独立"时用来源的当前状态做初值——否则用户点一下会看到其余
///    已隐藏的 section 突然全回来。
/// </summary>
public sealed class WardrobeTreeTests
{
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

	private static WardrobeTreeNode Find(IReadOnlyList<WardrobeTreeNode> roots, string key)
		=> roots.SelectMany(root => new[] { root }.Concat(root.Descendants()))
			.Single(node => string.Equals(node.Key, key, StringComparison.Ordinal));

	private static WardrobeWorkspaceDocument Own(params WardrobeWorkspaceUnit[] units)
		=> new(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[] { new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Own, units) });

	[Fact]
	public void Build_HasFourGroupingLevelsAboveSections()
	{
		var roots = WardrobeTree.Build(Units());
		Assert.Equal(2, roots.Count);
		Assert.Equal("护甲", roots[0].Label);
		Assert.Equal("内衣", roots[1].Label);
		// 内衣这一支：两个身形，各含若干槽位。
		Assert.Equal(new[] { "纤细", "健壮" }, roots[1].Children.Select(node => node.Label).ToArray());
	}

	[Fact]
	public void Build_ExpandsAnyBodyIntoBothBranches()
	{
		var roots = WardrobeTree.Build(Units());
		var slim = Find(roots, $"piece:Undergarment/body:Slim/slot:Helmet/unit:{HelmetUnderAny}");
		var stocky = Find(roots, $"piece:Undergarment/body:Stocky/slot:Helmet/unit:{HelmetUnderAny}");
		// 同一个身份，两支下各出现一次；数据只有一份。
		Assert.Equal(HelmetUnderAny, slim.Unit);
		Assert.Equal(HelmetUnderAny, stocky.Unit);
		Assert.True(slim.SharedAcrossBodies);
		Assert.True(stocky.SharedAcrossBodies);
		Assert.NotEqual(slim.Key, stocky.Key);
	}

	[Fact]
	public void Build_KeysAreUniqueAcrossTheWholeTree()
	{
		var roots = WardrobeTree.Build(Units());
		var keys = roots.SelectMany(root => new[] { root }.Concat(root.Descendants()))
			.Select(node => node.Key)
			.ToArray();
		Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void Build_OrdersSlotsAnatomically()
	{
		var roots = WardrobeTree.Build(Units());
		var slots = roots[1].Children[0].Children.Select(node => node.Label).ToArray();
		// 头盔在躯干之前：固定顺序，不随字典顺序漂移。
		Assert.Equal(new[] { "头盔", "躯干" }, slots);
	}

	[Fact]
	public void Build_PutsUnknownSemanticsIntoFallbackGroups()
	{
		var roots = WardrobeTree.Build([Unit(LegacyUnit, string.Empty, string.Empty, string.Empty, 1)]);
		Assert.Equal("未标注套组", Assert.Single(roots).Label);
		var body = Assert.Single(roots[0].Children);
		Assert.Equal("未标注身形", body.Label);
		Assert.Equal("未标注槽位", Assert.Single(body.Children).Label);
	}

	[Fact]
	public void Build_SectionNodesCarryIndexAndTriangles()
	{
		var roots = WardrobeTree.Build(Units());
		var decoration = Find(roots, $"piece:Armor/body:Slim/slot:Torso/unit:{TorsoArmorSlim}");
		Assert.Equal(2, decoration.Children.Count);
		Assert.Equal(0, decoration.Children[0].SectionIndex);
		Assert.Equal(100u, decoration.Children[0].Triangles);
		Assert.Equal(101u, decoration.Children[1].Triangles);
	}

	[Fact]
	public void Counts_ReflectTheWorkspace()
	{
		var roots = WardrobeTree.Build(Units());
		var decoration = Find(roots, $"piece:Armor/body:Slim/slot:Torso/unit:{TorsoArmorSlim}");
		Assert.True(WardrobeTree.Counts(decoration, Target, WardrobeWorkspaceDocument.Empty).AllEnabled);

		var workspace = Own(new WardrobeWorkspaceUnit(TorsoArmorSlim, new[] { 0 }));
		var counts = WardrobeTree.Counts(decoration, Target, workspace);
		Assert.Equal(2, counts.SectionCount);
		Assert.Equal(1, counts.DisabledCount);
		Assert.True(counts.IsMixed);
	}

	[Fact]
	public void SetEnabled_OnDecorationTogglesEverySectionItOwns()
	{
		var roots = WardrobeTree.Build(Units());
		var decoration = Find(roots, $"piece:Armor/body:Slim/slot:Torso/unit:{TorsoArmorSlim}");
		var workspace = Own();
		var disabled = WardrobeTree.SetEnabled(workspace, Target, decoration, enabled: false);
		Assert.Equal(new[] { 0, 1 }, disabled.FindArmour(Target)!.Units.Single().Off.ToArray());
		var enabledAgain = WardrobeTree.SetEnabled(disabled, Target, decoration, enabled: true);
		// 一条都不关的 unit 条目要删掉，别在文件里堆空数组。
		Assert.Empty(enabledAgain.FindArmour(Target)!.Units);
	}

	[Fact]
	public void SetEnabled_OnOneBodyBranchIsVisibleInTheOther()
	{
		// 状态按 (unit, 下标) 存、不按节点存，所以两支天然同步。
		var roots = WardrobeTree.Build(Units());
		var slim = Find(roots, $"piece:Undergarment/body:Slim/slot:Torso/unit:{TorsoUnderAny}");
		var stocky = Find(roots, $"piece:Undergarment/body:Stocky/slot:Torso/unit:{TorsoUnderAny}");
		var workspace = WardrobeTree.SetEnabled(Own(), Target, slim, enabled: false);
		Assert.True(WardrobeTree.Counts(stocky, Target, workspace).AllDisabled);
	}

	[Fact]
	public void SetEnabled_RejectsAnArmourThatStillInherits()
	{
		var roots = WardrobeTree.Build(Units());
		var decoration = Find(roots, $"piece:Armor/body:Slim/slot:Torso/unit:{TorsoArmorSlim}");
		var inheriting = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[] { new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Inherit, Array.Empty<WardrobeWorkspaceUnit>()) });
		// 继承模式下写条目会和 mode 自相矛盾；编辑器必须先 MakeOwn。
		Assert.Throws<InvalidOperationException>(
			() => WardrobeTree.SetEnabled(inheriting, Target, decoration, enabled: false));
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
		var roots = WardrobeTree.Build(Units());
		var nodes = roots.SelectMany(root => root.Sections()).ToArray();
		// 节点数是 12（`Any` 的两个 unit 各出现两次），但真实身份只有 7 个——
		// 这正是"同一份数据、两处显示"的代价，也是为什么状态必须按 (unit, 下标) 存。
		Assert.Equal(12, nodes.Length);
		Assert.Equal(7, nodes
			.Select(node => (node.Unit, node.SectionIndex))
			.Distinct()
			.Count());
	}
}
