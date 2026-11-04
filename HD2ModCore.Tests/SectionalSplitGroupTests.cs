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
		// 段来源账**按目标 unit 记**（这里的目标是 Host），InputIndex = donor 在 target.DonorUnitIds 里的位置。
		var origins = new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal)
		{
			[Host] =
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
		// 成员指向**宿主**那个 unit：donor 已经被并掉了，产物里没有它。
		Assert.Equal(Host, member.Unit);
		Assert.Equal([4, 5], member.Sections);
		Assert.Equal("Torso/Any", group.ExclusiveGroup);
	}

	[Fact]
	public void BuildGroups_SplitsHostAndDonorSectionsOfAMergedTarget()
	{
		// 合并之后基础组与变体组必须**各管各的**：
		// InputIndex = -1 是目标自己原有的段（基础件），>= 0 是第几个 donor 贡献的段（变体）。
		// 这条守的是一个真 bug：早先版本按"整块 unit"取，基础组把 donor 的几何也吞了，
		// 而变体组因为查不到 donor 的 unit 记录而变成空组 —— 界面上会看到一个空复选框。
		var origins = new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal)
		{
			[Host] =
			[
				new CanonicalAppendedSection(2, 0, -1, 0),
				new CanonicalAppendedSection(2, 1, -1, 1),
				new CanonicalAppendedSection(2, 2, 0, 0),
				new CanonicalAppendedSection(2, 3, 0, 1),
				new CanonicalAppendedSection(2, 4, 0, 2),
			],
		};

		var document = SectionalSplitter.BuildGroups(
			Plan(
				new SectionalSplitGroup("腿", "LeftLeg/Any", [Host], true),
				new SectionalSplitGroup("腿 变体", "LeftLeg/Any", [Donor], false)),
			origins,
			Map((Host, 5)));

		Assert.Equal(2, document.Groups.Count);
		Assert.Equal([0, 1], Assert.Single(document.Groups[0].Members).Sections);
		Assert.Equal([2, 3, 4], Assert.Single(document.Groups[1].Members).Sections);
		// 两组的成员都指向**宿主**那个 unit（donor 已经被并掉了，产物里没有它）。
		Assert.All(document.Groups, group => Assert.Equal(Host, Assert.Single(group.Members).Unit));
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
