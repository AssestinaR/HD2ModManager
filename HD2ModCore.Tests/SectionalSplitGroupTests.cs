using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 把"规划出来的组"翻译成组表：组员是<b>产物里的 section 下标</b>。
/// <para>
/// 守两件事：① 有段来源账时只取那几段（变体组不能把宿主的几何也框进来）；
/// ② <b>没有来源账时退回"这个 unit 的全部节"</b>——那是"这个槽本来就只有它一个来源"的情况，
/// 不退回去组就是空的，界面上会出现一个没有任何成员的复选框。
/// </para>
/// </summary>
public sealed class SectionalSplitGroupTests
{
	private const string Host = "701fe4566e8b0002";
	private const string Donor = "701fe4566e8b0011";

	private static WardrobeSectionMapDocument Map(params (string FileId, int Sections)[] units)
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("/tmp/x.patch_0", "abc", 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			[
				.. units.Select(unit => new WardrobeSectionMapUnit(
					unit.FileId, "Torso", "Undergarment", "Any", "Medium", unit.Sections, true,
					[
						.. Enumerable.Range(0, unit.Sections).Select(index => new WardrobeSectionMapEntry(
							index, "aabbccddeeff0011", 10, "aabbccddeeff0011-10", [])),
					])),
			]);

	private static SectionalSplitPlan Plan(params SectionalSplitGroup[] groups)
		=> new(
			[new SectionalSplitTarget("Torso", "Any", Host, [Donor])],
			groups,
			[],
			[]);

	[Fact]
	public void BuildGroups_FallsBackToEverySectionWhenThereIsNoOriginLedger()
	{
		// 没有 donor 的槽：没走过追加，自然没有段来源账 ⇒ 组员是它所有的节。
		var document = SectionalSplitter.BuildGroups(
			Plan(new SectionalSplitGroup("躯干", "Torso/Any", [Host], true)),
			new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal),
			Map((Host, 4)));

		var group = Assert.Single(document.Groups);
		var member = Assert.Single(group.Members);
		Assert.Equal(Host, member.Unit);
		Assert.Equal([0, 1, 2, 3], member.Sections);
	}

	[Fact]
	public void BuildGroups_UsesOnlyTheLedgerSectionsWhenPresent()
	{
		// 变体组：只框 donor 贡献的那几段（第 4、5 段），不能把宿主的 0..3 也框进来。
		var origins = new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal)
		{
			[Donor] =
			[
				new CanonicalAppendedSection(2, 4, 0, 0),
				new CanonicalAppendedSection(2, 5, 0, 1),
			],
		};

		var document = SectionalSplitter.BuildGroups(
			Plan(new SectionalSplitGroup("外套", "Torso/Any", [Donor], false)),
			origins,
			Map((Host, 4), (Donor, 2)));

		var group = Assert.Single(document.Groups);
		var member = Assert.Single(group.Members);
		Assert.Equal(Donor, member.Unit);
		Assert.Equal([4, 5], member.Sections);
		Assert.Equal("Torso/Any", group.ExclusiveGroup);
	}

	[Fact]
	public void BuildGroups_SkipsUnitsMissingFromTheProducedMap()
	{
		var document = SectionalSplitter.BuildGroups(
			Plan(new SectionalSplitGroup("躯干", "Torso/Any", ["ffffffffffffffff"], true)),
			new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal),
			Map((Host, 4)));

		Assert.Empty(Assert.Single(document.Groups).Members);
	}
}
