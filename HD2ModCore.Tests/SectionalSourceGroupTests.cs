using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 组表按「一个来源一个组」建：名字是依据文件里的来源条目名（`大衣 健壮` / `尾巴` / `丝袜`…），
/// 成员是这个来源贡献的全部段，跨部位合成一个组。
/// <para>
/// 守三件事：① 同一来源的段在躯干与胯里都进同一个组；② 不带互斥（这些是部件，不是二选一）；
/// ③ "带了 unit 却一段都没归到它"的来源必须报出来——否则用户会以为那个部件不存在。
/// </para>
/// </summary>
public sealed class SectionalSourceGroupTests
{
	private const string TorsoHost = "4ebbfde8692577ff";
	private const string HipHost = "75c5e56949cfe382";
	private const string TailDonor = "126d33b362aa8b48";

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

	private static SectionalSplitPlan Plan(params SectionalSplitTarget[] targets)
		=> new(targets, [], [], []);

	/// <summary>段来源账：mesh 2 是 LOD0 基准；InputIndex -1 = 宿主自己那份，>= 0 = 第几个追加输入。</summary>
	private static IReadOnlyDictionary<string, IReadOnlyList<CanonicalAppendedSection>> Ledger(
		params (string Host, (int Final, int Input)[] Sections)[] targets)
		=> targets.ToDictionary(
			target => target.Host,
			target => (IReadOnlyList<CanonicalAppendedSection>)[
				.. target.Sections.Select(section => new CanonicalAppendedSection(2, section.Final, section.Input, 0)),
			],
			StringComparer.Ordinal);

	private static SectionalSplitter.SectionalSourceProvenance Provenance(
		IReadOnlyDictionary<string, string> own,
		IReadOnlyDictionary<string, IReadOnlyList<string>> inputs)
		=> new(
			own,
			inputs.ToDictionary(
				pair => pair.Key,
				pair => (IReadOnlyDictionary<int, IReadOnlyList<string>>)new Dictionary<int, IReadOnlyList<string>> { [2] = pair.Value },
				StringComparer.Ordinal));

	[Fact]
	public void BuildSourceGroups_MergesEverySectionOfOneSourceIntoOneGroup()
	{
		// 躯干：宿主自己 2 段 + 尾巴追加 1 段；胯：宿主自己 1 段 + 尾巴追加 1 段。
		var sections = Map((TorsoHost, 3), (HipHost, 2));
		var plan = Plan(
			new SectionalSplitTarget("Torso", "Slim", TorsoHost, [TailDonor]),
			new SectionalSplitTarget("Hip", "Slim", HipHost, [TailDonor]));
		var ledger = Ledger(
			(TorsoHost, [(0, -1), (1, -1), (2, 0)]),
			(HipHost, [(0, -1), (1, 0)]));
		var provenance = Provenance(
			new Dictionary<string, string>(StringComparer.Ordinal) { [TorsoHost] = "本体", [HipHost] = "本体" },
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
			{
				[TorsoHost] = ["尾巴"],
				[HipHost] = ["尾巴"],
			});

		var document = SectionalSplitter.BuildSourceGroups(plan, ledger, sections, provenance, ["本体", "尾巴"], []);

		Assert.Equal(["本体", "尾巴"], document.Groups.Select(group => group.Name));
		var body = document.Groups[0];
		var tail = document.Groups[1];
		// 本体：躯干 0、1 + 胯 0；尾巴：躯干 2 + 胯 1 —— 跨部位合成一个组。
		Assert.Equal(
			[(TorsoHost, new[] { 0, 1 }), (HipHost, new[] { 0 })],
			body.Members.Select(member => (member.Unit, member.Sections.ToArray())));
		Assert.Equal(
			[(TorsoHost, new[] { 2 }), (HipHost, new[] { 1 })],
			tail.Members.Select(member => (member.Unit, member.Sections.ToArray())));
		// 部件不是二选一：一律不带互斥组。
		Assert.All(document.Groups, group => Assert.Null(group.ExclusiveGroup));
		// 组行副标题靠这个显示「来自「尾巴」」。
		Assert.All(document.Groups, group => Assert.Equal(group.Name, group.SourceVpk));
		Assert.Empty(document.Unmatched);
	}

	[Fact]
	public void BuildSourceGroups_SkipsTheLedgerAndGivesEverythingToTheHostWhenItIsMissing()
	{
		var sections = Map((TorsoHost, 2));
		var plan = Plan(new SectionalSplitTarget("Torso", "Slim", TorsoHost, [TailDonor]));

		var document = SectionalSplitter.BuildSourceGroups(
			plan,
			new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal),
			sections,
			Provenance(
				new Dictionary<string, string>(StringComparer.Ordinal) { [TorsoHost] = "本体" },
				new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
			["本体"],
			[]);

		var group = Assert.Single(document.Groups);
		Assert.Equal("本体", group.Name);
		Assert.Equal([0, 1], Assert.Single(group.Members).Sections);
	}

	[Fact]
	public void BuildSourceGroups_ReportsASourceThatBroughtUnitsButNoSections()
	{
		// 「翅膀」在这次转换里带了 unit（presentSources），但它的段一段都没归到它
		//（典型原因：装饰追加失败，donor 被丢掉了）。必须报出来，不能静默少一个开关。
		var sections = Map((TorsoHost, 1));
		var plan = Plan(new SectionalSplitTarget("Torso", "Slim", TorsoHost, []));
		var warnings = new List<string>();

		var document = SectionalSplitter.BuildSourceGroups(
			plan,
			Ledger((TorsoHost, [(0, -1)])),
			sections,
			Provenance(
				new Dictionary<string, string>(StringComparer.Ordinal) { [TorsoHost] = "本体" },
				new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
			["本体", "翅膀"],
			warnings);

		Assert.Single(document.Groups);
		var unmatched = Assert.Single(document.Unmatched);
		Assert.Equal("翅膀", unmatched.Name);
		Assert.Contains(warnings, warning => warning.Contains("翅膀", StringComparison.Ordinal));
	}

	[Fact]
	public void BuildSourceGroups_AttributesAnUnknownInputToTheHostAndSaysSo()
	{
		var sections = Map((TorsoHost, 2));
		var plan = Plan(new SectionalSplitTarget("Torso", "Slim", TorsoHost, [TailDonor]));
		var warnings = new List<string>();
		// 账里有第 0 个追加输入，但没有它的来源名（例如那份 patch 不在依据文件里）。
		var provenance = Provenance(
			new Dictionary<string, string>(StringComparer.Ordinal) { [TorsoHost] = "本体" },
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

		var document = SectionalSplitter.BuildSourceGroups(
			plan, Ledger((TorsoHost, [(0, -1), (1, 0)])), sections, provenance, ["本体"], warnings);

		Assert.Equal("本体", Assert.Single(document.Groups).Name);
		Assert.Contains(warnings, warning => warning.Contains("说不清", StringComparison.Ordinal));
	}

	[Fact]
	public void BuildSourceGroups_IsEmptyWhenNothingIsAttributable()
	{
		var document = SectionalSplitter.BuildSourceGroups(
			Plan(new SectionalSplitTarget("Torso", "Slim", "ffffffffffffffff", [])),
			new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal),
			Map((TorsoHost, 1)),
			Provenance(
				new Dictionary<string, string>(StringComparer.Ordinal),
				new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
			[],
			[]);

		Assert.Empty(document.Groups);
	}
}
