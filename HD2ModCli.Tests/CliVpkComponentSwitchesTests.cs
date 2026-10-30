using HD2ModCore.Domain;
using Xunit;
using HD2SourceMigration.VpkConversion;

namespace HD2ModCli.Tests;

/// <summary>
/// 把源侧的组件开关包翻译成 HD2 的 section 掩码。
/// <para>
/// 三件事必须钉住，否则界面上的"猫耳"开关会关错东西：
/// </para>
/// <list type="number">
/// <item><b>按名字精确落到节</b>：同一张底色上的三个部件（<c>c_white_a</c> /
/// <c>c_white_a_ear</c> / <c>c_white_a_tail</c>）必须各自独立，不能一起关。</item>
/// <item><b>跨 unit 合并</b>：一个开关可以关掉两个 unit 里的几何。</item>
/// <item><b>掩码去重</b>：两个包藏的是同一批材质时只能出一个开关。</item>
/// </list>
/// </summary>
public sealed class CliVpkComponentSwitchesTests
{
	private const string Head = "18054b9f5a540001";
	private const string Chest = "18054b9f5a540002";

	private static WardrobeSectionMapDocument Map(params (string Unit, string?[] Materials)[] units)
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch(@"C:\patch\x.patch_0", new string('a', 64), 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			units.Select((unit, index) => new WardrobeSectionMapUnit(
				unit.Unit,
				"Head",
				"Undergarment",
				"Any",
				"Light",
				unit.Materials.Length,
				true,
				unit.Materials.Select((name, section) => new WardrobeSectionMapEntry(
					section,
					"a47787804c45e1a9",
					100,
					"a47787804c45e1a9-100",
					[new WardrobeSectionToggle(0, 0, section, "0x00002010", 300)])
				{
					SourceMaterial = name,
				}).ToArray())
			{
				SourceName = $"unit-{index}",
			}).ToArray());

	private static VpkComponentSwitch Switch(string name, params string[] materials)
		=> new(@"C:\mods\" + name + ".vpk", name, materials);

	[Fact]
	public void Resolve_SeparatesComponentsThatShareOneTexture()
	{
		// 实测形状：三个源材质共用一张 C_White_A，逐节名字是它们唯一的区分。
		var map = Map((Head, ["c_white_a", "c_white_a_ear", "c_white_a_tail"]));

		var document = CliVpkComponentSwitches.Resolve(
			[Switch("猫耳", "c_white_a_ear"), Switch("尾巴", "c_white_a_tail")],
			map);

		Assert.Equal(2, document.Switches.Count);
		Assert.Empty(document.Unmatched);
		var ears = document.Switches.Single(item => item.Name == "猫耳");
		Assert.Equal([1], ears.Disabled[0].Sections);
		var tail = document.Switches.Single(item => item.Name == "尾巴");
		Assert.Equal([2], tail.Disabled[0].Sections);       // 只有第 2 节
	}

	[Fact]
	public void Resolve_SpansMultipleUnitsForOneSwitch()
	{
		var map = Map((Head, ["gem_face"]), (Chest, ["gem_face"]));

		var document = CliVpkComponentSwitches.Resolve([Switch("泪钻", "gem_face")], map);

		var entry = Assert.Single(document.Switches);
		Assert.Equal(2, entry.Disabled.Count);
		Assert.Equal(2, entry.SectionCount);
		Assert.Equal([Head, Chest], entry.Disabled.Select(target => target.Unit).ToArray());
	}

	[Fact]
	public void Resolve_MergesTwoPacksThatHideExactlyTheSameSections()
	{
		var map = Map((Head, ["c_white_a"]));

		// 两个包藏同一批材质 = 同一件事：并列会让用户看到两个开关，点哪个都关同一批几何。
		var document = CliVpkComponentSwitches.Resolve(
			[Switch("猫耳", "c_white_a"), Switch("耳朵", "c_white_a")],
			map);

		var entry = Assert.Single(document.Switches);
		Assert.Equal("猫耳", entry.Name);
		Assert.Contains("耳朵", entry.SourceVpk, StringComparison.Ordinal);
	}

	[Fact]
	public void Resolve_ReportsSwitchesWhoseMaterialsAreNotInThePatch()
	{
		var map = Map((Head, ["manuka_face"]));

		var document = CliVpkComponentSwitches.Resolve([Switch("光环", "rl_halo")], map);

		Assert.Empty(document.Switches);
		var unmatched = Assert.Single(document.Unmatched);
		Assert.Equal("光环", unmatched.Name);
		// 理由必须点到材质名：用户报"装了开关看不到效果"时，这是唯一能查的线索。
		Assert.Contains("rl_halo", unmatched.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Resolve_IgnoresSectionsWithoutASourceName()
	{
		var map = Map((Head, ["c_white_a", null]));

		var document = CliVpkComponentSwitches.Resolve([Switch("猫耳", "c_white_a")], map);

		var entry = Assert.Single(document.Switches);
		Assert.Equal([0], entry.Disabled[0].Sections);
	}
}
