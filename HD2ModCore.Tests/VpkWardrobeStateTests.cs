using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

/// <summary>
/// VPK 模组的组件树与开关状态。
/// <para>
/// 这里守两件事：<b>只画基础 7 槽</b>（换甲后 patch 里还有护肩/护甲板和一堆隐藏占位，
/// 把它们画进树里会让人以为能打开），以及<b>勾选是可逆的纯函数</b>（状态存在 mod 库里，
/// patch 是产物，改错了还能再烘）。
/// </para>
/// </summary>
public sealed class VpkWardrobeStateTests
{
	private const string Torso = "cae35ee15168145f";
	private const string Pelvis = "17da843a4908511e";
	private const string Helmet = "af431352d9b95e83";
	private const string ArmorPlate = "3c09e08493ee44b8";

	private static WardrobeSectionMapDocument CreateMap() => new(
		WardrobeSectionMapDocument.CurrentSchema,
		new WardrobeSectionMapPatch(@"C:\patch\9ba626afa44a3aa3.patch_0", new string('a', 64), 1, null),
		WardrobeSectionMapDocument.ExpectedSectionRecordSize,
		WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
		WardrobeSectionMapDocument.TocRelativeOffsets,
		[
			Unit(Torso, "Torso", "Undergarment", "Any", 3),
			Unit(Pelvis, "Hip", "Undergarment", "Any", 2),
			Unit(Helmet, "Helmet", "Undergarment", "Any", 2),
			// 换甲之后才会出现的护甲板：1 个 section 的隐藏壳，不该出现在树里。
			Unit(ArmorPlate, "Hip", "Armor", "Stocky", 1),
		]);

	private static WardrobeSectionMapUnit Unit(string fileId, string slot, string pieceType, string bodyType, int sections)
		=> new(fileId, slot, pieceType, bodyType, "Light", sections, true,
			Enumerable.Range(0, sections)
				.Select(index => new WardrobeSectionMapEntry(
					index,
					"a47787804c45e1a9",
					100,
					"a47787804c45e1a9-100",
					[new WardrobeSectionToggle(0, 0, index, "0x00002010", 300)]))
				.ToArray());

	[Fact]
	public void Build_DrawsOnlyTheBaseSevenSlots()
	{
		var tree = VpkModWardrobeTree.Build("VPK Lunalice 替换 RE-2310", CreateMap());

		var units = tree.Sections().Select(section => section.Unit).Distinct().ToArray();
		Assert.Contains(Torso, units);
		Assert.Contains(Pelvis, units);
		Assert.Contains(Helmet, units);
		Assert.DoesNotContain(ArmorPlate, units);

		// Any 身形展开到健壮与纤细两支下：7 个槽位两支都有，且都标"链接"。
		var bodies = tree.Children.Where(child => child.Kind == WardrobeTreeNodeKind.BodyType).ToArray();
		Assert.Equal(2, bodies.Length);
		Assert.All(bodies, body => Assert.True(body.Children.Count > 0));
		Assert.All(tree.Sections(), section => Assert.NotNull(section.Unit));
	}

	[Fact]
	public void SetEnabled_OnAUnit_TogglesEverySectionOfThatUnit()
	{
		var tree = VpkModWardrobeTree.Build("x", CreateMap());
		var torso = tree.Descendants().First(node => node.Kind == WardrobeTreeNodeKind.Unit && node.Unit == Torso);

		var disabled = VpkWardrobeState.Empty.SetEnabled(torso, enabled: false);

		Assert.Equal(new[] { 0, 1, 2 }, disabled.Disabled[Torso]);
		Assert.True(disabled.IsDisabled(Torso, 1));

		var restored = disabled.SetEnabled(torso, enabled: true);
		Assert.Empty(restored.Disabled);
	}

	[Fact]
	public void SetEnabled_OnASection_TouchesOnlyThatSection()
	{
		var tree = VpkModWardrobeTree.Build("x", CreateMap());
		var section = tree.Descendants().First(node =>
			node.Kind == WardrobeTreeNodeKind.Section && node.Unit == Torso && node.SectionIndex == 1);

		var disabled = VpkWardrobeState.Empty.SetEnabled(section, enabled: false);

		Assert.Equal(new[] { 1 }, disabled.Disabled[Torso]);
		Assert.False(disabled.IsDisabled(Torso, 0));
	}

	[Fact]
	public void SetEnabled_DropsEmptyEntriesInsteadOfLeavingEmptyArrays()
	{
		var tree = VpkModWardrobeTree.Build("x", CreateMap());
		var pelvis = tree.Descendants().First(node => node.Kind == WardrobeTreeNodeKind.Unit && node.Unit == Pelvis);

		var state = VpkWardrobeState.Empty
			.SetEnabled(pelvis, enabled: false)
			.SetEnabled(pelvis, enabled: true);

		Assert.DoesNotContain(Pelvis, state.Disabled.Keys);
	}
}
