using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

/// <summary>
/// P6 S2：低污染模式的 sectional 压缩编译（规划 §10）。
/// 纯规划测试不依赖任何样本；真数据由环境变量给出（同 <c>SectionalPhysBoneMergeTests</c>：
/// <c>HD2_TEST_SECTIONAL_MOD</c>、<c>HD2_TEST_GAME_DATA</c>），缺样本静默跳过。
/// </summary>
public sealed class SectionalLowPollutionTests(ITestOutputHelper output)
{
	// ── 纯规划 ─────────────────────────────────────────────────────────────

	private static SectionalCompressionSource Src(string id, ArmorPart? part, string body = "Any")
		=> new(id, part, body);

	/// <summary>原有的头 / 身体 / 手臂槽（不含 §17 额外生成的「身体+手臂」合体组，它由专门的测试锁）。</summary>
	private static IReadOnlyList<SectionalCompressionGroup> G(SectionalCompressionPlan plan)
		=> [.. plan.Groups.Where(group => group.Role != SectionalCompressionRole.BodyArms)];

	[Fact]
	public void BodyArms_IsAnExtraUnitOfBodyPlusArms_WithADerivedStableId()
	{
		// 3 槽：头、身体、手臂照旧，末尾多一个合体组 = 身体槽成员 + 手臂槽成员，ID 由身体槽的 ID 所有者派生。
		var three = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: false);
		var bodyArms = Assert.Single(three.Groups, group => group.Role == SectionalCompressionRole.BodyArms);
		Assert.Equal(["a2", "a3", "a4", "a5", "a6", "a7"], bodyArms.Members.Select(member => member.SourceUnitId));
		Assert.Equal(SectionalCompressionPlanner.DeriveBodyArmsId("a2"), bodyArms.UnitId);
		Assert.Equal(16, bodyArms.UnitId.Length);
		Assert.DoesNotContain(bodyArms.UnitId, SevenSlots().Select(unit => unit.FileId));
		Assert.Equal(bodyArms.UnitId, SectionalCompressionPlanner.Plan(SevenSlots().Reverse().ToArray(), false)
			.Groups.Single(group => group.Role == SectionalCompressionRole.BodyArms).UnitId);
		Assert.Contains(bodyArms.UnitId, three.SurvivingUnitIds);
		Assert.True(three.NeedsMerge);

		// 2 槽：合体组含头（head-body + arms）。
		var two = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);
		Assert.Equal(["a2", "a3", "a4", "a5", "a1", "a6", "a7"],
			two.Groups.Single(group => group.Role == SectionalCompressionRole.BodyArms).Members.Select(member => member.SourceUnitId));

		// 没有手臂（或没有身体）就不生成。
		Assert.DoesNotContain(SectionalCompressionPlanner.Plan(
			[.. SevenSlots().Where(unit => unit.Part is not (ArmorPart.LeftArm or ArmorPart.RightArm))], false).Groups,
			group => group.Role == SectionalCompressionRole.BodyArms);

		// 分体：合体组也按体型各一个，ID 各自派生、互不相同。
		var split = SectionalCompressionPlanner.Plan(AegirShape(), headMerge: false);
		var splitBodyArms = split.Groups.Where(group => group.Role == SectionalCompressionRole.BodyArms).ToArray();
		Assert.Equal([UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim], splitBodyArms.Select(group => group.Body));
		Assert.Equal(2, splitBodyArms.Select(group => group.UnitId).Distinct().Count());
		Assert.All(splitBodyArms, group => Assert.DoesNotContain(group.UnitId, AegirShape().Select(unit => unit.FileId)));
	}

	private static SectionalCompressionSource[] SevenSlots() =>
	[
		Src("a1", ArmorPart.Helmet), Src("a2", ArmorPart.Torso), Src("a3", ArmorPart.Hips),
		Src("a4", ArmorPart.LeftLeg), Src("a5", ArmorPart.RightLeg), Src("a6", ArmorPart.LeftArm), Src("a7", ArmorPart.RightArm),
	];

	[Fact]
	public void ThreeSlotMode_GroupsHeadBodyArms_AndKeepsOwnerIds()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: false);

		Assert.True(plan.Supported);
		Assert.Equal("three-slot", plan.Mode);
		Assert.Equal(["a1", "a2", "a6"], G(plan).Select(group => group.UnitId));
		Assert.Equal(
			[SectionalCompressionRole.Head, SectionalCompressionRole.Body, SectionalCompressionRole.Arms],
			G(plan).Select(group => group.Role));
		Assert.Equal(["a2", "a3", "a4", "a5"], G(plan)[1].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["a6", "a7"], G(plan)[2].Members.Select(member => member.SourceUnitId));
		Assert.True(plan.NeedsMerge);
		Assert.Empty(plan.Passthrough);
	}

	[Fact]
	public void TwoSlotMode_MergesHeadIntoBody_IdFromTorso()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);

		Assert.True(plan.Supported);
		Assert.Equal("two-slot", plan.Mode);
		Assert.Equal(["a2", "a6"], G(plan).Select(group => group.UnitId));
		Assert.Equal(SectionalCompressionRole.HeadBody, G(plan)[0].Role);
		Assert.Equal(["a2", "a3", "a4", "a5", "a1"], G(plan)[0].Members.Select(member => member.SourceUnitId));
		Assert.Equal(ArmorPart.Torso, G(plan)[0].IdFrom);
		// 被吸收的来源 ID 不留在产物里。
		Assert.Equal(["a2", "a6", SectionalCompressionPlanner.DeriveBodyArmsId("a2")], plan.SurvivingUnitIds);
	}

	[Fact]
	public void IdRule_FallsThroughWhenThePrimaryPartIsMissing_AndIsOrderIndependent()
	{
		// 没有躯干：身体槽 ID 取胯；没有左臂：手臂槽 ID 取右臂；输入顺序不影响结果。
		var source = SevenSlots().Where(unit => unit.Part is not (ArmorPart.Torso or ArmorPart.LeftArm)).Reverse().ToArray();
		var plan = SectionalCompressionPlanner.Plan(source, headMerge: false);

		Assert.Equal(["a1", "a3", "a7"], G(plan).Select(group => group.UnitId));
		Assert.Equal(G(plan).Select(group => group.UnitId), SectionalCompressionPlanner.Plan(SevenSlots().Where(unit => unit.Part is not (ArmorPart.Torso or ArmorPart.LeftArm)).ToArray(), false).Groups.Where(group => group.Role != SectionalCompressionRole.BodyArms).Select(group => group.UnitId));
	}

	[Fact]
	public void MissingParts_OmitTheEmptySlot()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[Src("a1", ArmorPart.Helmet), Src("a2", ArmorPart.Torso)], headMerge: false);

		Assert.Equal(["a1", "a2"], G(plan).Select(group => group.UnitId));
		Assert.False(plan.NeedsMerge);   // 每槽单成员：不合并，也就不需要游戏 data
		Assert.Equal(["a2"], SectionalCompressionPlanner.Plan([Src("a2", ArmorPart.Torso), Src("a6", ArmorPart.LeftArm)], true).Groups.Select(group => group.UnitId).Take(1));
	}

	[Fact]
	public void Cape_PassesThroughUntouched_ShouldersJoinTheArmSlot()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[.. SevenSlots(), Src("b1", ArmorPart.Cape), Src("b2", ArmorPart.LeftShoulder), Src("b3", ArmorPart.RightShoulder)],
			headMerge: false);

		Assert.True(plan.Supported);
		Assert.Equal(["b1"], plan.Passthrough.Select(member => member.SourceUnitId));
		// 手臂槽：ID 仍取左臂，肩排在两条臂之后。
		Assert.Equal(["a6", "a7", "b2", "b3"], G(plan)[2].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["a1", "a2", "a6", SectionalCompressionPlanner.DeriveBodyArmsId("a2"), "b1"], plan.SurvivingUnitIds);
		Assert.Equal(["a2", "a6", SectionalCompressionPlanner.DeriveBodyArmsId("a2"), "b1"],
			SectionalCompressionPlanner.Plan(
				[.. SevenSlots(), Src("b1", ArmorPart.Cape), Src("b2", ArmorPart.LeftShoulder)], headMerge: true).SurvivingUnitIds);
	}

	[Fact]
	public void ShouldersAlone_FormTheArmSlot_AndSupplyItsId()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[Src("a2", ArmorPart.Torso), Src("s2", ArmorPart.RightShoulder), Src("s1", ArmorPart.LeftShoulder)], headMerge: false);

		Assert.Equal(["a2", "s1"], G(plan).Select(group => group.UnitId));
		Assert.Equal(["s1", "s2"], G(plan)[1].Members.Select(member => member.SourceUnitId));
	}

	[Theory]
	[InlineData("Other")]
	[InlineData(null)]
	public void UnrecognisedBodyTypes_AreNotCompressed(string? body)
	{
		var source = SevenSlots().ToList();
		source[1] = new SectionalCompressionSource("a2", ArmorPart.Torso, body);
		var plan = SectionalCompressionPlanner.Plan(source, headMerge: false);

		Assert.False(plan.Supported);
		Assert.Contains("分体", plan.Reason, StringComparison.Ordinal);
	}

	// ── S5：按体型分开做的素体（规划 §13）──────────────────────────────────

	/// <summary>碧蓝航线-埃吉尔睡衣的形状：胸、左右臂各有壮实 / 苗条两份不同模型，头、胯、双腿通用。</summary>
	private static SectionalCompressionSource[] AegirShape() =>
	[
		Src("e1", ArmorPart.Helmet), Src("e2", ArmorPart.Torso, "Slim"), Src("e3", ArmorPart.Torso, "Stocky"),
		Src("e4", ArmorPart.Hips), Src("e5", ArmorPart.LeftArm, "Slim"), Src("e6", ArmorPart.LeftArm, "Stocky"),
		Src("e7", ArmorPart.RightArm, "Slim"), Src("e8", ArmorPart.RightArm, "Stocky"),
		Src("e9", ArmorPart.LeftLeg), Src("ea", ArmorPart.RightLeg),
	];

	[Fact]
	public void SplitBodies_ThreeSlot_MakesFiveUnits_AnyMembersJoinBothBodies()
	{
		var plan = SectionalCompressionPlanner.Plan(AegirShape(), headMerge: false);

		Assert.True(plan.Supported, plan.Reason);
		Assert.Equal(["e1", "e3", "e2", "e6", "e5"], G(plan).Select(group => group.UnitId));
		Assert.Equal(
			[UnitMeshBodyVariant.Any, UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim, UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim],
			G(plan).Select(group => group.Body));
		Assert.Equal(["e3", "e4", "e9", "ea"], G(plan)[1].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["e2", "e4", "e9", "ea"], G(plan)[2].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["e6", "e8"], G(plan)[3].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["e5", "e7"], G(plan)[4].Members.Select(member => member.SourceUnitId));
		Assert.True(plan.HasBodySplit);
		Assert.Equal(["e1", "e3", "e2", "e6", "e5", SectionalCompressionPlanner.DeriveBodyArmsId("e3"), SectionalCompressionPlanner.DeriveBodyArmsId("e2")], plan.SurvivingUnitIds);
		Assert.Empty(plan.Passthrough);
	}

	[Fact]
	public void SplitBodies_TwoSlot_MakesFourUnits_HelmetJoinsBothBodyUnits()
	{
		var plan = SectionalCompressionPlanner.Plan(AegirShape(), headMerge: true);

		Assert.True(plan.Supported, plan.Reason);
		Assert.Equal(["e3", "e2", "e6", "e5"], G(plan).Select(group => group.UnitId));
		Assert.Equal(
			[SectionalCompressionRole.HeadBody, SectionalCompressionRole.HeadBody, SectionalCompressionRole.Arms, SectionalCompressionRole.Arms],
			G(plan).Select(group => group.Role));
		Assert.Equal(["e3", "e4", "e9", "ea", "e1"], G(plan)[0].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["e2", "e4", "e9", "ea", "e1"], G(plan)[1].Members.Select(member => member.SourceUnitId));
	}

	[Fact]
	public void SplitBodies_OnlyTheSlotThatHasVariantsIsSplit()
	{
		var source = SevenSlots().Select(unit => unit.Part == ArmorPart.LeftArm ? Src(unit.FileId, unit.Part, "Stocky") : unit)
			.Append(Src("a8", ArmorPart.LeftArm, "Slim")).ToArray();
		var plan = SectionalCompressionPlanner.Plan(source, headMerge: false);

		Assert.True(plan.Supported, plan.Reason);
		Assert.Equal(["a1", "a2", "a6", "a8"], G(plan).Select(group => group.UnitId));
		Assert.Equal(
			[UnitMeshBodyVariant.Any, UnitMeshBodyVariant.Any, UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim],
			G(plan).Select(group => group.Body));
		// 右臂是通用的：两边各并一份。
		Assert.Equal(["a6", "a7"], G(plan)[2].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["a8", "a7"], G(plan)[3].Members.Select(member => member.SourceUnitId));
	}

	[Fact]
	public void SplitBodies_AMissingBodyHasNoSpecificMember_IdFallsToTheFirstAnyMember_OrNoUnitAtAll()
	{
		// 手臂槽只有苗条左臂 + 通用右臂：壮实那个只由通用右臂组成（ID 取它），苗条那个 = 左臂 + 右臂（ID 取苗条左臂）。
		var withAny = SectionalCompressionPlanner.Plan(
			[Src("a2", ArmorPart.Torso), Src("l1", ArmorPart.LeftArm, "Slim"), Src("r1", ArmorPart.RightArm)], headMerge: false);
		Assert.Equal(["a2", "r1", "l1"], G(withAny).Select(group => group.UnitId));
		Assert.Equal(
			[UnitMeshBodyVariant.Any, UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim], G(withAny).Select(group => group.Body));

		// 手臂槽只有苗条左臂：没有壮实 unit（映射时用苗条的顶上）。
		var only = SectionalCompressionPlanner.Plan([Src("a2", ArmorPart.Torso), Src("l1", ArmorPart.LeftArm, "Slim")], headMerge: false);
		Assert.Equal(["a2", "l1"], G(only).Select(group => group.UnitId));
		Assert.Equal(UnitMeshBodyVariant.Slim, G(only)[1].Body);
	}

	[Fact]
	public void SplitBodies_SameBodySamePartTwice_OrAnyPlusSpecificOfOnePart_AreNotCompressed()
	{
		var twice = SectionalCompressionPlanner.Plan([.. AegirShape(), Src("x1", ArmorPart.Torso, "Stocky")], headMerge: false);
		Assert.False(twice.Supported);
		Assert.Contains("胸部", twice.Reason, StringComparison.Ordinal);
		Assert.Contains("健壮", twice.Reason, StringComparison.Ordinal);

		var mixed = SectionalCompressionPlanner.Plan([.. AegirShape(), Src("x2", ArmorPart.Torso)], headMerge: false);
		Assert.False(mixed.Supported);
	}

	[Fact]
	public void SplitBodies_PlanIsStableAndOrderIndependent()
	{
		var first = SectionalCompressionPlanner.Plan(AegirShape(), headMerge: false);
		var second = SectionalCompressionPlanner.Plan(AegirShape().Reverse().ToArray(), headMerge: false);
		Assert.Equal(LowPollutionLayoutFile.Serialize(first), LowPollutionLayoutFile.Serialize(second));
	}

	[Fact]
	public void LayoutFile_AllAnyStaysOnSchema1WithoutBody_SplitWritesSchema2WithBody()
	{
		var any = LowPollutionLayoutFile.Serialize(SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: false));
		var anyRoot = JsonNode.Parse(any)!.AsObject();
		Assert.Equal("hd2mod.low-pollution-layout/1", anyRoot["schema"]!.GetValue<string>());
		Assert.All(anyRoot["units"]!.AsArray(), unit => Assert.Null(unit!["body"]));

		var split = LowPollutionLayoutFile.Serialize(SectionalCompressionPlanner.Plan(AegirShape(), headMerge: false));
		var root = JsonNode.Parse(split)!.AsObject();
		Assert.Equal("hd2mod.low-pollution-layout/2", root["schema"]!.GetValue<string>());
		Assert.Equal(
			["any", "stocky", "slim", "stocky", "slim", "stocky", "slim"],
			root["units"]!.AsArray().Select(unit => unit!["body"]!.GetValue<string>()));
		Assert.Equal(["e1", "e3", "e2", "e6", "e5", SectionalCompressionPlanner.DeriveBodyArmsId("e3"), SectionalCompressionPlanner.DeriveBodyArmsId("e2")], root["units"]!.AsArray().Select(unit => unit!["unit"]!.GetValue<string>()));
	}

	[Fact]
	public void LayoutReader_ReadsBothSchemas_AndUnitOfFallsBackBodyThenAnyThenOtherBody()
	{
		// /1：没有 body，一律 any。
		Assert.True(LowPollutionLayoutReader.TryParse(
			"{\"schema\":\"hd2mod.low-pollution-layout/1\",\"units\":[{\"role\":\"body\",\"unit\":\"00000000000000b1\"}]}", out var old, out var error), error);
		Assert.Equal(LowPollutionBodies.Any, old!.Units[0].Body);
		Assert.Equal("00000000000000b1", old.UnitOf(LowPollutionRoles.Body, 0));
		Assert.Equal("00000000000000b1", old.UnitOf(LowPollutionRoles.Body, 1));
		Assert.False(old.IsSplit(LowPollutionRoles.Body));

		var plan = SectionalCompressionPlanner.Plan(
			[.. AegirShape().Where(unit => unit.FileId is not ("e5" or "e7"))], headMerge: false);
		Assert.Equal(2, plan.Groups.Count(group => group.Role == SectionalCompressionRole.Body));
		Assert.True(LowPollutionLayoutReader.TryParse(
			LowPollutionLayoutFile.Serialize(SplitHex(plan)), out var layout, out error), error);
		Assert.True(layout!.IsSplit(LowPollutionRoles.Body));
		Assert.Equal("00000000000000e3", layout.UnitOf(LowPollutionRoles.Body, 0));
		Assert.Equal("00000000000000e2", layout.UnitOf(LowPollutionRoles.Body, 1));
		// 手臂槽只剩壮实：苗条退回另一体型的。
		Assert.Equal("00000000000000e6", layout.UnitOf(LowPollutionRoles.Arms, 0));
		Assert.Equal("00000000000000e6", layout.UnitOf(LowPollutionRoles.Arms, 1));
		Assert.True(layout.UsesOtherBody(LowPollutionRoles.Arms, 1));
		Assert.False(layout.UsesOtherBody(LowPollutionRoles.Arms, 0));
		Assert.False(layout.IsSplit(LowPollutionRoles.Arms));
		Assert.Equal(["00000000000000e3", "00000000000000e2"], layout.UnitsOf(LowPollutionRoles.Body));
		// 通用 unit 排在另一体型之前。
		var mixed = new LowPollutionLayout("three-slot", false,
		[
			new(LowPollutionRoles.Arms, "00000000000000a1", LowPollutionBodies.Slim),
			new(LowPollutionRoles.Arms, "00000000000000a2", LowPollutionBodies.Any),
		]);
		Assert.Equal("00000000000000a2", mixed.UnitOf(LowPollutionRoles.Arms, 0));
		Assert.Equal("00000000000000a1", mixed.UnitOf(LowPollutionRoles.Arms, 1));
	}

	/// <summary>测试用：把 e1… 这种短 ID 补成 16 位 hex，layout 读取器要求合法 ID。</summary>
	private static SectionalCompressionPlan SplitHex(SectionalCompressionPlan plan)
	{
		static string Long(string id) => id.PadLeft(16, '0');
		return plan with
		{
			Groups = [.. plan.Groups.Select(group => group with
			{
				UnitId = Long(group.UnitId),
				Members = [.. group.Members.Select(member => member with { SourceUnitId = Long(member.SourceUnitId) })],
			})],
			Passthrough = [.. plan.Passthrough.Select(member => member with { SourceUnitId = Long(member.SourceUnitId) })],
		};
	}

	[Fact]
	public void DuplicateOrUnknownParts_AreNotCompressed()
	{
		var duplicate = SectionalCompressionPlanner.Plan([.. SevenSlots(), Src("c1", ArmorPart.Torso)], headMerge: false);
		Assert.False(duplicate.Supported);
		Assert.Contains("多个来源", duplicate.Reason, StringComparison.Ordinal);

		var unknown = SectionalCompressionPlanner.Plan([.. SevenSlots(), Src("c2", null)], headMerge: false);
		Assert.False(unknown.Supported);

		Assert.False(SectionalCompressionPlanner.Plan([Src("b1", ArmorPart.Cape)], headMerge: false).Supported);
	}

	[Fact]
	public void LayoutFile_FingerprintIsOptional_RoundTrips_AndOldBytesAreUnchanged()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);
		Assert.DoesNotContain("fingerprint", LowPollutionLayoutFile.Serialize(plan));
		var text = LowPollutionLayoutFile.Serialize(plan, "abc123");
		Assert.Equal("abc123", JsonNode.Parse(text)!["fingerprint"]!.GetValue<string>());
		Assert.Equal(LowPollutionLayoutFile.Serialize(plan), LowPollutionLayoutFile.Serialize(plan, null));
	}

	[Fact]
	public void LayoutFile_ListsEverySourcePartOfEachUnit_AndIsStable()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);
		var text = LowPollutionLayoutFile.Serialize(plan);

		Assert.Equal(text, LowPollutionLayoutFile.Serialize(SectionalCompressionPlanner.Plan(SevenSlots().Reverse().ToArray(), headMerge: true)));
		var root = JsonNode.Parse(text)!.AsObject();
		Assert.Equal(LowPollutionLayoutFile.Schema, root["schema"]!.GetValue<string>());
		Assert.Equal("two-slot", root["mode"]!.GetValue<string>());
		var units = root["units"]!.AsArray();
		Assert.Equal(3, units.Count);
		Assert.Equal("a2", units[0]!["unit"]!.GetValue<string>());
		Assert.Equal("head-body", units[0]!["role"]!.GetValue<string>());
		Assert.Equal("torso", units[0]!["idFrom"]!.GetValue<string>());
		Assert.Equal(
			["torso", "hips", "left_leg", "right_leg", "helmet"],
			units[0]!["parts"]!.AsArray().Select(part => part!["part"]!.GetValue<string>()));
		Assert.Equal("arms", units[1]!["role"]!.GetValue<string>());
		Assert.Equal("body-arms", units[2]!["role"]!.GetValue<string>());
		Assert.Equal(SectionalCompressionPlanner.DeriveBodyArmsId("a2"), units[2]!["unit"]!.GetValue<string>());
	}

	// ── 真数据 ─────────────────────────────────────────────────────────────

	private static (string Mod, string Game, string Manager)? Locate()
	{
		var mod = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		var manager = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return null;
		if (!Directory.Exists(Path.Combine(mod, "sectional"))) return null;
		return (mod, game, manager ?? Path.GetTempPath());
	}

	/// <summary>把样本复制到临时目录（真库只读，编译会写 <c>Overwrite/</c>）。</summary>
	private static string CopyMod(string source)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lowpollution-tests", Guid.NewGuid().ToString("N"), "mod");
		Directory.CreateDirectory(root);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(source, directory)));
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			var destination = Path.Combine(root, Path.GetRelativePath(source, file));
			File.Copy(file, destination, overwrite: true);
			File.SetAttributes(destination, FileAttributes.Normal);
		}

		return root;
	}

	private static void DeleteTree(string modDirectory)
	{
		var root = Path.GetDirectoryName(modDirectory)!;
		try
		{
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, FileAttributes.Normal);
			Directory.Delete(root, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static string OverwriteDirectory(string mod) => ModNodePayloadLayout.GetOverwriteDirectory(mod);

	private static string Patch(string directory)
		=> Directory.EnumerateFiles(directory, "*.patch_0").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();

	private static string Id(ulong fileId) => fileId.ToString("x16", CultureInfo.InvariantCulture);

	private static async Task<Dictionary<string, UnitMeshModel>> ReadUnitsAsync(string patch)
	{
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(patch, default);
		var reader = new PatchUnitMeshReader();
		var units = new Dictionary<string, UnitMeshModel>();
		foreach (var entry in scanned.Where(candidate => candidate.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).OrderBy(candidate => candidate.AssetKey.FileId))
		{
			units[Id(entry.AssetKey.FileId)] = (await reader
				.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default)).Model;
		}

		return units;
	}

	private static string HashOverwrite(string mod)
	{
		var overwrite = OverwriteDirectory(mod);
		using var sha = SHA256.Create();
		var lines = Directory.EnumerateFiles(overwrite, "*", SearchOption.AllDirectories)
			.OrderBy(path => path, StringComparer.Ordinal)
			.Select(path => Path.GetRelativePath(overwrite, path) + ":" + Convert.ToHexString(sha.ComputeHash(StableBytes(path))));
		return string.Join("\n", lines);
	}

	/// <summary>
	/// <c>sections.json</c> 里记了编译时临时目录的路径（每次都是新 GUID，普通编译也一样），比较前去掉那一行；其余文件按原字节。
	/// </summary>
	private static byte[] StableBytes(string path)
	{
		var bytes = File.ReadAllBytes(path);
		if (!string.Equals(Path.GetFileName(path), WardrobeSectionMapFile.FileName, StringComparison.OrdinalIgnoreCase)) return bytes;
		var text = System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.UTF8.GetString(bytes), "\"path\": \"[^\"]*\"", "\"path\": \"\"");
		return System.Text.Encoding.UTF8.GetBytes(text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealMod_CompressesToExpectedSlots_StableIds_NoPhysBoneLost(bool headMerge)
	{
		if (Locate() is not { } located) return;
		var mod = CopyMod(located.Mod);
		try
		{
			var context = new VpkHostContext(located.Game, new StoragePaths(located.Manager)) { LowPollutionMode = true };
			var sectional = Path.Combine(mod, "sectional");
			var sourceMap = await WardrobeSectionMapFile.ReadAsync(Path.Combine(sectional, WardrobeSectionMapFile.FileName));
			var plan = SectionalCompressionPlanner.Plan(
				[.. sourceMap.Units.Select(unit => new SectionalCompressionSource(unit.FileId, ArmorStructure.TryParse(unit.Slot), unit.BodyType))],
				headMerge);
			Assert.True(plan.Supported, plan.Reason);

			async Task<SectionalCompileResult> Compile()
				=> await SectionalCompiler.CompileAsync(mod, context, null, null, headMerge, [], lowPollution: true);

			var first = await Compile();
			Assert.True(first.Ok, first.Error);
			// 编完的素体在同样设置下被判成「已是最新」，改头身合并才会重编（S4a 的指纹）。
			var wardrobe = new VpkWardrobeFile(false, VpkWardrobeState.Empty, HeadMergeIntoBody: headMerge);
			Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", mod, true, wardrobe).Status);
			Assert.Equal(LowPollutionPrepStatus.NeedsRecompile,
				LowPollutionFreshness.Assess("k", mod, true, wardrobe with { HeadMergeIntoBody = !headMerge }).Status);
			var firstLayout = await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName));
			var firstUnits = await ReadUnitsAsync(Patch(OverwriteDirectory(mod)));
			var firstHash = HashOverwrite(mod);

			// 件数：3 槽 / 2 槽（披风、肩原样保留，这两份样本没有）。
			Assert.Equal(headMerge ? 2 : 3, G(plan).Count);
			Assert.Single(plan.Groups, group => group.Role == SectionalCompressionRole.BodyArms);
			Assert.Equal(plan.SurvivingUnitIds.Order(StringComparer.Ordinal), firstUnits.Keys.Order(StringComparer.Ordinal));
			Assert.Equal(plan.Groups.Count + plan.Passthrough.Count, firstUnits.Count);
			Assert.Equal(headMerge ? SectionalHeadMergeOutcome.AppliedOwnModel : SectionalHeadMergeOutcome.Off, first.HeadMerge);

			// 重编两次：ID、描述文件与全部产物字节都不变。
			var second = await Compile();
			Assert.True(second.Ok, second.Error);
			Assert.Equal(firstLayout, await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.Equal(firstUnits.Keys.Order(StringComparer.Ordinal), (await ReadUnitsAsync(Patch(OverwriteDirectory(mod)))).Keys.Order(StringComparer.Ordinal));
			Assert.Equal(firstHash, HashOverwrite(mod));

			// 来源物理骨一根不少：每个压缩 unit 的节点表 ⊇ 它所有来源部位的物理骨。
			var sources = await ReadUnitsAsync(Patch(sectional));
			var rigBones = await SectionalPhysBoneRig.ReadBoneHashesAsync(Patch(sectional), default);
			foreach (var group in plan.Groups)
			{
				var missing = SectionalPhysBoneRig.FindMissing(
					group.Members.Select(member => sources[member.SourceUnitId]), rigBones, firstUnits[group.UnitId]);
				output.WriteLine($"headMerge={headMerge} {LowPollutionLayoutFile.RoleName(group.Role)}=0x{group.UnitId} parts={group.Members.Count} rigBones={rigBones.Count} missing={missing.Count}");
				Assert.Empty(missing);
			}

			Assert.DoesNotContain(first.Warnings, warning => warning.Contains("自定义/物理骨", StringComparison.Ordinal));

			// 物理容器按骨名匹配：压缩后的 patch 里容器还在、rig 与来源一致（容器不用跟着合并）。
			Assert.Equal(
				rigBones.Order(),
				(await SectionalPhysBoneRig.ReadBoneHashesAsync(Patch(OverwriteDirectory(mod)), default)).Order());

			// 注册表：只剩压缩 unit 的注册件，没有悬空引用。
			var registry = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), VpkCustomModelFile.FileName)))!;
			var registryUnits = registry["pieces"]!.AsArray().Select(piece => piece!["unit"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal).ToArray();
			Assert.Equal(plan.SurvivingUnitIds.Except(plan.Groups.Where(group => group.Role == SectionalCompressionRole.BodyArms).Select(group => group.UnitId)).Order(StringComparer.Ordinal), registryUnits);
			output.WriteLine($"headMerge={headMerge}: {firstUnits.Count} units, overwrite files:\n{firstHash}");
		}
		finally
		{
			DeleteTree(mod);
		}
	}

	[Fact]
	public async Task RealMod_SwitchingOff_RestoresTheOrdinaryOutputByteForByte()
	{
		if (Locate() is not { } located) return;
		var mod = CopyMod(located.Mod);
		try
		{
			var paths = new StoragePaths(located.Manager);
			var off = new VpkHostContext(located.Game, paths);

			// 开关关：与不传该参数逐字节一致，且没有任何低污染文件。
			var defaultCompile = await SectionalCompiler.CompileAsync(mod, off, null, null, false, []);
			Assert.True(defaultCompile.Ok, defaultCompile.Error);
			var baseline = HashOverwrite(mod);
			var explicitOff = await SectionalCompiler.CompileAsync(mod, off, null, null, false, [], lowPollution: false);
			Assert.True(explicitOff.Ok, explicitOff.Error);
			Assert.Equal(baseline, HashOverwrite(mod));
			Assert.False(File.Exists(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.Equal(
				await File.ReadAllBytesAsync(Patch(Path.Combine(mod, "sectional"))),
				await File.ReadAllBytesAsync(Patch(OverwriteDirectory(mod))));

			// 开了再关：回到与从未开过时完全相同的产物，描述文件也被清掉。
			var on = await SectionalCompiler.CompileAsync(mod, off, null, null, false, [], lowPollution: true);
			Assert.True(on.Ok, on.Error);
			Assert.True(File.Exists(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.NotEqual(baseline, HashOverwrite(mod));
			var back = await SectionalCompiler.CompileAsync(mod, off, null, null, false, []);
			Assert.True(back.Ok, back.Error);
			Assert.Equal(baseline, HashOverwrite(mod));
		}
		finally
		{
			DeleteTree(mod);
		}
	}

	/// <summary>
	/// S5 真数据：碧蓝航线-埃吉尔睡衣（胸、臂分壮实 / 苗条）。<c>HD2_TEST_SPLIT_MOD</c> 指向它（真库，只读：先复制到临时目录），
	/// 另需 <c>HD2_TEST_GAME_DATA</c>、<c>HD2_TEST_MANAGER_ROOT</c>（带装备索引）。缺任何一个静默跳过。
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealSplitMod_CompressesPerBody_NoPhysBoneLost_AndCw9DoesNotPolluteCe27(bool headMerge)
	{
		var sample = Environment.GetEnvironmentVariable("HD2_TEST_SPLIT_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		var manager = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(sample) || !Directory.Exists(Path.Combine(sample, "sectional"))) return;
		if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game) || string.IsNullOrWhiteSpace(manager)) return;
		var mod = CopyMod(sample);
		try
		{
			var paths = new StoragePaths(manager);
			var context = new VpkHostContext(game, paths) { LowPollutionMode = true };
			var sectional = Path.Combine(mod, "sectional");
			var sourceMap = await WardrobeSectionMapFile.ReadAsync(Path.Combine(sectional, WardrobeSectionMapFile.FileName));
			var plan = SectionalCompressionPlanner.Plan(
				[.. sourceMap.Units.Select(unit => new SectionalCompressionSource(unit.FileId, ArmorStructure.TryParse(unit.Slot), unit.BodyType))],
				headMerge);
			Assert.True(plan.Supported, plan.Reason);
			Assert.Equal(headMerge ? 4 : 5, G(plan).Count);
			Assert.Equal(2, plan.Groups.Count(group => group.Role == SectionalCompressionRole.BodyArms));

			var first = await SectionalCompiler.CompileAsync(mod, context, null, null, headMerge, [], lowPollution: true);
			Assert.True(first.Ok, first.Error);
			foreach (var warning in first.Warnings) output.WriteLine("警告：" + warning);

			var layoutPath = Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName);
			var layoutText = await File.ReadAllTextAsync(layoutPath);
			Assert.Equal("hd2mod.low-pollution-layout/2", JsonNode.Parse(layoutText)!["schema"]!.GetValue<string>());
			Assert.False(File.Exists(Path.Combine(OverwriteDirectory(mod), LowPollutionFreshness.UnsupportedMarkerFileName)));

			var units = await ReadUnitsAsync(Patch(OverwriteDirectory(mod)));
			Assert.Equal(plan.SurvivingUnitIds.Order(StringComparer.Ordinal), units.Keys.Order(StringComparer.Ordinal));
			Assert.Equal(plan.Groups.Count, units.Count);

			// 物理骨一根不少：每个压缩 unit 的节点表 ⊇ 它所有来源部位（Any 成员两边都并）的物理骨。
			var sources = await ReadUnitsAsync(Patch(sectional));
			var rigBones = await SectionalPhysBoneRig.ReadBoneHashesAsync(Patch(sectional), default);
			foreach (var group in plan.Groups)
			{
				var missing = SectionalPhysBoneRig.FindMissing(
					group.Members.Select(member => sources[member.SourceUnitId]), rigBones, units[group.UnitId]);
				output.WriteLine($"headMerge={headMerge} {LowPollutionLayoutFile.RoleName(group.Role)}/{group.Body}=0x{group.UnitId} parts={group.Members.Count} missing={missing.Count}");
				Assert.Empty(missing);
			}

			// 注册表：拆开的槽各写 body 0 / 1 两条，各指自己的 unit；头（3 槽）一条 body 3。
			var registry = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), VpkCustomModelFile.FileName)))!;
			var pieces = registry["pieces"]!.AsArray().Select(piece => piece!.AsObject()).ToArray();
			Assert.Equal(G(plan).Count, pieces.Length);
			foreach (var group in G(plan).Where(group => group.Body != UnitMeshBodyVariant.Any))
			{
				var raw = group.Body == UnitMeshBodyVariant.Stocky ? 0 : 1;
				Assert.Contains(pieces, piece => piece["unit"]!.GetValue<string>() == group.UnitId && piece["body"]!.GetValue<int>() == raw);
			}

			// 编完 = 已是最新；重编一次，布局和全部产物字节不变。
			var wardrobe = new VpkWardrobeFile(false, VpkWardrobeState.Empty, HeadMergeIntoBody: headMerge);
			Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", mod, true, wardrobe).Status);
			var firstHash = HashOverwrite(mod);
			var second = await SectionalCompiler.CompileAsync(mod, context, null, null, headMerge, [], lowPollution: true);
			Assert.True(second.Ok, second.Error);
			Assert.Equal(layoutText, await File.ReadAllTextAsync(layoutPath));
			Assert.Equal(firstHash, HashOverwrite(mod));

			// 映射：对真实索引规划 CW-9，链接只落在 CW-9 自己的承载件上，CE-27 共用的部件一个都不出现。
			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
			if (snapshot is null) return;
			Assert.True(LowPollutionLayoutReader.TryRead(mod, out var layout, out var error), error);
			var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);
			var cw9 = snapshot.Kits.FirstOrDefault(kit => kit.ItemId == 3017717209);
			var ce27 = snapshot.Kits.FirstOrDefault(kit => kit.ItemId == 2128569605);
			if (cw9 is null || ce27 is null) return;
			var mapping = LowPollutionMappingPlanner.Plan(index, layout!, cw9.ItemId, "m1");
			output.WriteLine($"headMerge={headMerge} CW-9：{mapping.Status} {mapping.Reason} links={mapping.Links.Count} hide={mapping.Hide.Count}");
			foreach (var link in mapping.Links) output.WriteLine($"  link {link.Carrier} -> {link.Unit}");
			foreach (var note in mapping.Notes) output.WriteLine("  说明：" + note);
			if (headMerge && mapping.Status != LowPollutionMappingStatus.Mapped) return;   // 2 槽下手臂可能需要同套头盔（组合），不是本测试要点
			Assert.Equal(LowPollutionMappingStatus.Mapped, mapping.Status);

			var ce27Files = ce27.Pieces.Select(piece => Id(piece.UnitAssetKey.FileId)).ToHashSet(StringComparer.Ordinal);
			var cw9Files = cw9.Pieces.Select(piece => Id(piece.UnitAssetKey.FileId)).ToHashSet(StringComparer.Ordinal);
			var shared = cw9Files.Intersect(ce27Files).ToArray();
			output.WriteLine($"CW-9 与 CE-27 共用 {shared.Length} 个部件");
			Assert.All(mapping.Links, link =>
			{
				Assert.Contains(link.Carrier, cw9Files);
				Assert.DoesNotContain(link.Carrier, ce27Files);
				Assert.Contains(link.Unit, layout!.AllUnits);
			});
			Assert.Empty(mapping.Links.Select(link => link.Carrier).Intersect(shared));
			// 壮实 / 苗条各链到各自的压缩 unit。
			Assert.Equal(mapping.Links.Count, mapping.Links.Select(link => link.Carrier).Distinct().Count());
			Assert.True(mapping.Links.Select(link => link.Unit).Distinct().Count() >= 2, "壮实和苗条应各链到自己的压缩 unit");
		}
		finally
		{
			DeleteTree(mod);
		}
	}
}
