using System.Buffers.Binary;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;
using Microsoft.Data.Sqlite;
using AdaptationLuaAssetKeys = HD2ModAdaptation.PatchReconstruction.Lua.LuaAssetKeys;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// P6 S3：低污染模式的软映射（规划 §11）。规划器、Hider 规则、<c>wardrobe.links</c> 分支、先部署后映射、
/// 以及「关开关 / 来源不是压缩素体 / 游戏→游戏 行为不变」的锁。全部纯函数或临时目录，不碰真库。
/// </summary>
public sealed class LowPollutionMappingTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;
	private const string BodyUnit = "00000000b0d10001";
	private const string ArmsUnit = "00000000a7a50002";
	private const string HeadUnit = "000000000ead0003";

	private static GameDataAuthorityPiece P(uint slot, uint layer, uint raw, ulong file)
		=> new(
			raw,
			raw switch { 0 => UnitMeshBodyVariant.Stocky, 1 => UnitMeshBodyVariant.Slim, _ => UnitMeshBodyVariant.Any },
			slot, default, layer, 0, 0, new CoreAssetKey(UnitType, file), []);

	private static GameDataAuthorityKit Kit(uint item, uint set, GameDataEquipmentKitKind kind, string? name, params GameDataAuthorityPiece[] pieces)
		=> new(item, 0, set, kind, $"{item:x16}", name ?? $"K{item}", pieces);

	private static string Hex(ulong value) => value.ToString("x16");

	/// <summary>目标 K1（item 1）：独有躯干 0x2001、独有左臂 0x6001、独有右臂 0x7001；0x2000/0x3000/0x6000 与 K2 共用；披风共用。</summary>
	private static List<GameDataAuthorityKit> World(Func<List<GameDataAuthorityPiece>, List<GameDataAuthorityPiece>>? edit = null)
	{
		var t = new List<GameDataAuthorityPiece>
		{
			P(1, 0, 3, 0xcafe),
			P(2, 0, 3, 0x2000), P(2, 1, 3, 0x2001),
			P(3, 0, 3, 0x3000),
			P(6, 0, 3, 0x6000), P(6, 1, 3, 0x6001),
			P(7, 1, 3, 0x7001),
		};
		if (edit is not null) t = edit(t);
		return
		[
			Kit(1, 100, GameDataEquipmentKitKind.Armor, null, [.. t]),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, null, P(1, 0, 3, 0xcafe), P(2, 0, 3, 0x2000), P(3, 0, 3, 0x3000), P(6, 0, 3, 0x6000)),
			Kit(3, 100, GameDataEquipmentKitKind.Helmet, null, P(0, 0, 3, 0x1001)),
			Kit(4, 100, GameDataEquipmentKitKind.Helmet, null, P(0, 0, 3, 0x1002)),
			Kit(5, 300, GameDataEquipmentKitKind.Helmet, null, P(0, 0, 3, 0x1002)),
		];
	}

	private static LowPollutionLayout Layout(bool headMerge, bool arms = true, bool head = true)
	{
		var units = new List<LowPollutionLayoutUnit>();
		if (!headMerge && head) units.Add(new(LowPollutionRoles.Head, HeadUnit));
		units.Add(new(headMerge ? LowPollutionRoles.HeadBody : LowPollutionRoles.Body, BodyUnit));
		if (arms) units.Add(new(LowPollutionRoles.Arms, ArmsUnit));
		return new LowPollutionLayout(headMerge ? "two-slot" : "three-slot", headMerge, units);
	}

	private static LowPollutionMappingPlan Plan(IReadOnlyList<GameDataAuthorityKit> kits, LowPollutionLayout layout, uint item = 1)
		=> LowPollutionMappingPlanner.Plan(SectionalExclusivePlanner.BuildIndex(kits), layout, item, "m1");

	// ── 规划器 ─────────────────────────────────────────────────────────────

	[Fact]
	public void ThreeSlotArmor_MapsBodyAndArmsOntoUniqueCarriers_AndHidesEverythingElse()
	{
		var plan = Plan(World(), Layout(headMerge: false));

		Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status);
		// 躯干 ← body 槽（最外层独有件 0x2001）；手臂 ← arms 槽（左臂槽的独有件 0x6001，不放躯干槽）。
		Assert.Equal(
			[new LowPollutionLink(Hex(0x2001), BodyUnit), new LowPollutionLink(Hex(0x6001), ArmsUnit)],
			plan.Links);
		// 隐藏集：全部非披风部件 − 承载件（含独有但没当承载件的 0x7001）。披风不动。
		Assert.Equal([Hex(0x2000), Hex(0x3000), Hex(0x6000), Hex(0x7001)], plan.Hide);
		Assert.DoesNotContain(Hex(0xcafe), plan.Hide);
		Assert.Equal([Hex(0x2001)], plan.Signature);
		Assert.Equal([BodyUnit], plan.UnitSignature);
		// 3 槽不映射头：说明里要有提示。
		Assert.Contains(plan.Notes, note => note.Contains("模型的头不显示", StringComparison.Ordinal));
	}

	[Fact]
	public void TwoSlotArmor_MapsHeadBodyAndArms_AndAlsoHidesTheVanillaHelmets()
	{
		var plan = Plan(World(), Layout(headMerge: true));

		Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status);
		Assert.Equal(BodyUnit, plan.Links.Single(link => link.Carrier == Hex(0x2001)).Unit);
		// 同套（set 100）两顶头盔的原版部件进隐藏集；别套的 K5 不算。
		Assert.Contains(Hex(0x1001), plan.Hide);
		Assert.Contains(Hex(0x1002), plan.Hide);
		Assert.Contains(plan.Notes, note => note.Contains("头身合并", StringComparison.Ordinal));
	}

	[Fact]
	public void ArmsCarrier_NeverSitsOnTheTorsoSlot_AndFollowsThePreferenceOrder()
	{
		// 去掉独有的手臂件：只剩躯干第 0 层之外的独有件在躯干槽、右臂 0x7001。手臂应落在右臂（7），不是躯干槽。
		var plan = Plan(World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId != 0x6001)]), Layout(headMerge: false));
		Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status);
		Assert.Equal(ArmsUnit, plan.Links.Single(link => link.Carrier == Hex(0x7001)).Unit);

		// 再去掉右臂：没有别的独有部件可放手臂槽 ⇒ 整套不可（Q1 的默认：不做半套）。
		var none = Plan(World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]), Layout(headMerge: false));
		Assert.Equal(LowPollutionMappingStatus.Unavailable, none.Status);
		Assert.Empty(none.Links);
		Assert.Contains("手臂", none.Reason, StringComparison.Ordinal);

		// 素体没有手臂槽时不需要手臂承载件。
		Assert.Equal(LowPollutionMappingStatus.Mapped,
			Plan(World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]), Layout(headMerge: false, arms: false)).Status);
	}

	[Fact]
	public void ArmsFallback_TwoSlotCombo_PutsTheArmsOnTheMappedHelmetsUniqueHeadPart()
	{
		// 目标 K1 除躯干外没有别的独有部件（Q1 第 1 步落空），2 槽、同套头盔 K3 也映射到这个素体 ⇒ 手臂放在 K3 的独有头部件 0x1001 上。
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]);
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var plan = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 1, "m1", new HashSet<uint> { 3 });

		Assert.True(plan.Status == LowPollutionMappingStatus.Mapped, plan.Reason);
		Assert.Equal(ArmsUnit, plan.Links.Single(link => link.Carrier == Hex(0x1001)).Unit);
		Assert.Equal(BodyUnit, plan.Links.Single(link => link.Carrier == Hex(0x2001)).Unit);
		Assert.Contains(plan.Notes, note => note.Contains("手臂放在头盔 K3（3）", StringComparison.Ordinal));
		// 承载件不进隐藏集（0x1001 是头盔部件，2 槽下本来会被隐藏），同套另一顶头盔的 0x1002 照藏。
		Assert.DoesNotContain(Hex(0x1001), plan.Hide);
		Assert.Contains(Hex(0x1002), plan.Hide);

		// 这顶头盔的另一半：2 槽组合里头盔本身不写映射，但不能被标成「不可」。
		var helmet = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 3, "m1", new HashSet<uint> { 1 });
		Assert.Equal(LowPollutionMappingStatus.Mapped, helmet.Status);
		Assert.Empty(helmet.Links);
		// 没有护甲同时映射 ⇒ 仍然是「仅头盔 2 槽不可」。
		Assert.Equal(LowPollutionMappingStatus.Unavailable,
			LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 3, "m1", new HashSet<uint>()).Status);
	}

	[Fact]
	public void ArmsFallback_OnlyForTwoSlotCombos_ElseTheWholeSetIsUnavailable()
	{
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]);
		var index = SectionalExclusivePlanner.BuildIndex(kits);

		// 2 槽但护甲单独映射（同套头盔没有一起映射）：头盔的独有部件够不着，整套不可，原因里提示一起映射头盔。
		var armorOnly = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 1, "m1", new HashSet<uint>());
		Assert.Equal(LowPollutionMappingStatus.Unavailable, armorOnly.Status);
		Assert.Empty(armorOnly.Links);
		Assert.Contains("一起映射", armorOnly.Reason, StringComparison.Ordinal);

		// 3 槽组合：头盔的独有部件已被头占用，不能再放手臂。
		var threeSlot = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: false), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Unavailable, threeSlot.Status);
		Assert.Empty(threeSlot.Links);
		Assert.DoesNotContain("一起映射", threeSlot.Reason, StringComparison.Ordinal);

		// 2 槽组合，但同套头盔的头部件都被别套共用（K4 的 0x1002 与 K5 共用；K3 不在映射里）：整套不可，不做半套。
		var sharedHelmet = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 1, "m1", new HashSet<uint> { 4 });
		Assert.Equal(LowPollutionMappingStatus.Unavailable, sharedHelmet.Status);
		Assert.Contains("没有可用的", sharedHelmet.Reason, StringComparison.Ordinal);

		// 第 1 步优先：目标自己有除躯干外的独有部件时，不去借头盔的。
		var own = LowPollutionMappingPlanner.Plan(
			SectionalExclusivePlanner.BuildIndex(World()), Layout(headMerge: true), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Mapped, own.Status);
		Assert.DoesNotContain(own.Links, link => link.Carrier == Hex(0x1001));
	}

	[Fact]
	public void Service_PassesTheOtherTargetsOfTheSameModelAsCompanions()
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-" + Guid.NewGuid().ToString("N"));
		try
		{
			// 压缩布局 2 槽：head-body + arms。K1 没有除躯干外的独有部件，靠同套头盔 K3 一起映射才能放下手臂。
			WriteMod(mods, "A", "m1", BodyUnit,
				"{\"schema\":\"hd2mod.low-pollution-layout/1\",\"mode\":\"two-slot\",\"headMerge\":true,\"units\":["
				+ $"{{\"role\":\"head-body\",\"unit\":\"{BodyUnit}\"}},{{\"role\":\"arms\",\"unit\":\"{ArmsUnit}\"}}]}}");
			var snapshot = Snapshot(World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]));
			var deployed = new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16), Convert.ToUInt64(ArmsUnit, 16) };

			var combo = LowPollutionMappingService.Plan(mods, snapshot, [new(1, null, "m1"), new(3, null, "m1")], deployed);
			Assert.All(combo, plan => Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status));

			var alone = LowPollutionMappingService.Plan(mods, snapshot, [new(1, null, "m1")], deployed);
			Assert.Equal(LowPollutionMappingStatus.Unavailable, Assert.Single(alone).Status);
		}
		finally
		{
			try { Directory.Delete(mods, recursive: true); }
			catch { }
		}
	}

	[Fact]
	public void NoUniqueTorso_IsUnavailable_NeverFallsBackToTheSharedOne()
	{
		// 0x2001 也被 K2 拥有 ⇒ 躯干部件都被共用。
		var kits = World();
		kits[1] = Kit(2, 200, GameDataEquipmentKitKind.Armor, null,
			P(1, 0, 3, 0xcafe), P(2, 0, 3, 0x2000), P(2, 1, 3, 0x2001), P(3, 0, 3, 0x3000), P(6, 0, 3, 0x6000));
		var plan = Plan(kits, Layout(headMerge: false));

		Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status);
		Assert.Empty(plan.Links);
		Assert.Contains("躯干", plan.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void ColourTwin_ExplainsThatNoPartCanTellItApart()
	{
		var kits = World();
		// K2 拥有 K1 的全部非披风部件：换色孪生。
		kits[1] = Kit(2, 200, GameDataEquipmentKitKind.Armor, null,
			[.. kits[0].Pieces.Where(piece => piece.UnitAssetKey.FileId != 0xcafe)]);
		var plan = Plan(kits, Layout(headMerge: false));

		Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status);
		Assert.Contains("K2", plan.Reason, StringComparison.Ordinal);
		Assert.Contains("只有颜色不同", plan.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void SameNameGroup_RelaxesUniqueness_LikeTheExclusivePlanner()
	{
		var kits = World();
		// 同名的另一条记录（item 6）也拥有 0x2001：组内共用，仍算独有（P4b 放宽口径），说明里列出同名记录。
		kits.Add(Kit(6, 900, GameDataEquipmentKitKind.Armor, "K1", P(2, 1, 3, 0x2001)));
		var plan = Plan(kits, Layout(headMerge: false));

		Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status);
		Assert.Equal(BodyUnit, plan.Links.Single(link => link.Carrier == Hex(0x2001)).Unit);
		Assert.Contains(plan.Notes, note => note.Contains("K1（6）", StringComparison.Ordinal));
	}

	[Fact]
	public void BodySpecificCarriers_GiveOneCarrierPerBody_AndASecondSignature()
	{
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x2001)), P(2, 1, 0, 0x2a01), P(2, 1, 1, 0x2b01)]);
		var plan = Plan(kits, Layout(headMerge: false));

		Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status);
		Assert.Equal(BodyUnit, plan.Links.Single(link => link.Carrier == Hex(0x2a01)).Unit);
		Assert.Equal(BodyUnit, plan.Links.Single(link => link.Carrier == Hex(0x2b01)).Unit);
		Assert.Equal([Hex(0x2a01), Hex(0x2b01)], plan.Signature);
	}

	[Fact]
	public void Helmet_ThreeSlotMapsTheHeadOnItsUniquePart_TwoSlotAndSharedHeadsAreUnavailable()
	{
		var mapped = Plan(World(), Layout(headMerge: false), item: 3);
		Assert.Equal(LowPollutionMappingStatus.Mapped, mapped.Status);
		Assert.Equal([new LowPollutionLink(Hex(0x1001), HeadUnit)], mapped.Links);
		Assert.Empty(mapped.Hide);   // 这顶头盔只有头一个部件：没有可隐藏的。
		Assert.Equal([Hex(0x1001)], mapped.Signature);
		Assert.Equal([HeadUnit], mapped.UnitSignature);

		var twoSlot = Plan(World(), Layout(headMerge: true), item: 3);
		Assert.Equal(LowPollutionMappingStatus.Unavailable, twoSlot.Status);
		Assert.Contains("2 槽", twoSlot.Reason, StringComparison.Ordinal);

		// K4 的头部件 0x1002 与别套（K5，不同名不同套）共用 ⇒ 没有独有头部件。
		var shared = Plan(World(), Layout(headMerge: false), item: 4);
		Assert.Equal(LowPollutionMappingStatus.Unavailable, shared.Status);
		Assert.Empty(shared.Links);
	}

	[Fact]
	public void MissingKitOrLayoutSlots_AreUnavailableWithAReason()
	{
		Assert.Equal(LowPollutionMappingStatus.Unavailable, Plan(World(), Layout(false), item: 999).Status);
		var noBody = new LowPollutionLayout("three-slot", false, [new(LowPollutionRoles.Head, HeadUnit)]);
		Assert.Contains("躯干槽", Plan(World(), noBody).Reason, StringComparison.Ordinal);
		var noHead = Layout(false, head: false);
		Assert.Contains("头槽", Plan(World(), noHead, item: 3).Reason, StringComparison.Ordinal);
	}

	// ── Hider 规则 ─────────────────────────────────────────────────────────

	[Fact]
	public void HiderRules_EachTargetGetsOneRulePerSignature_SameHideSet()
	{
		var rules = LowPollutionHiderRules.Build([Plan(World(), Layout(headMerge: false))]);

		Assert.Equal(["lp-1", "lp-1-u"], rules.Select(rule => rule.Name).Order(StringComparer.Ordinal));
		var byCarrier = rules.Single(rule => rule.Name == "lp-1");
		var byUnit = rules.Single(rule => rule.Name == "lp-1-u");
		Assert.Equal([Hex(0x2001)], byCarrier.All);
		Assert.Equal([BodyUnit], byUnit.All);
		Assert.Equal(byCarrier.Hide, byUnit.Hide);
		Assert.All(rules, rule => Assert.True(KitInstanceRulesProtocol.TryValidate(rule, out _)));
		Assert.Equal(SectionalExclusivePlanner.Radius, byCarrier.Radius);
	}

	private static LowPollutionMappingPlan Synthetic(uint item, string[] carriers, string[] hide, string unit = BodyUnit)
		=> new(item, $"T{item}", "m1", LowPollutionMappingStatus.Mapped, null,
			[.. carriers.Select(carrier => new LowPollutionLink(carrier, unit))], hide, [carriers[0]], [unit], []);

	[Fact]
	public void HiderRules_NeverHideAnyTargetsCarrier()
	{
		// 同名组里甲的非承载件可能是乙的承载件：乙的角色身上它显示模型，不能被甲的规则藏掉。
		string a = Hex(0xa), b = Hex(0xb);
		var rules = LowPollutionHiderRules.Build(
		[
			Synthetic(1, [a], [b, Hex(0xc)]),
			Synthetic(2, [b], [a, Hex(0xd)]),
		]);

		Assert.Equal([Hex(0xc)], rules.First(rule => rule.Name == "lp-1").Hide);
		Assert.Equal([Hex(0xd)], rules.First(rule => rule.Name == "lp-2").Hide);
	}

	[Fact]
	public void HiderRules_SplitLongHideSets_AndSkipNonMappedAndEmptyTargets()
	{
		var hide = Enumerable.Range(1, KitInstanceRulesProtocol.MaximumHide + 44).Select(i => Hex(0x100000UL + (ulong)i)).ToArray();
		var unavailable = LowPollutionMappingPlan.Fail(7, "x", "m1", LowPollutionMappingStatus.Unavailable, "no");
		var rules = LowPollutionHiderRules.Build([Synthetic(1, [Hex(0xa)], hide), unavailable, Synthetic(3, [Hex(0xe)], [])]);

		Assert.Equal(["lp-1", "lp-1-h2", "lp-1-u", "lp-1-u-h2"], rules.Select(rule => rule.Name).Order(StringComparer.Ordinal));
		Assert.Equal(KitInstanceRulesProtocol.MaximumHide, rules.First(rule => rule.Name == "lp-1").Hide.Count);
		Assert.Equal(44, rules.First(rule => rule.Name == "lp-1-h2").Hide.Count);
		Assert.All(rules, rule => Assert.True(KitInstanceRulesProtocol.TryValidate(rule, out var error), error));
	}

	[Fact]
	public void FitToLimits_DropsTheOverflowInsteadOfProducingAnUnreadableFile()
	{
		var hide = Enumerable.Range(1, KitInstanceRulesProtocol.MaximumHide).Select(i => Hex(0x100000UL + (ulong)i)).ToArray();
		var rules = Enumerable.Range(1, 1200)
			.Select(i => new KitInstanceRule($"r{i}", 0.3, [Hex((ulong)i)], [], hide))
			.ToArray();

		var fitted = KitInstanceRulesProtocol.FitToLimits(rules, out var dropped);

		Assert.True(dropped > 0);
		Assert.Equal(rules.Length - fitted.Count, dropped);
		Assert.True(fitted.Count <= KitInstanceRulesProtocol.MaximumRules);
		var text = KitInstanceRulesProtocol.Serialize(new KitInstanceRulesDocument(1, fitted));   // 不抛
		Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= KitInstanceRulesProtocol.MaximumBytes);
		Assert.True(KitInstanceRulesProtocol.TryParse(text, out var parsed, out var error), error);
		Assert.Equal(fitted.Count, parsed!.Rules.Count);

		Assert.Equal(3, KitInstanceRulesProtocol.FitToLimits(rules.Take(3).ToArray(), out var none).Count);
		Assert.Equal(0, none);
	}

	// ── wardrobe.links ─────────────────────────────────────────────────────

	private static GameDataAuthoritySnapshot Snapshot(IReadOnlyList<GameDataAuthorityKit> kits)
		=> new(GameDataAuthoritySourceKind.IndexDatabase, "test", "fp", DateTimeOffset.UnixEpoch, kits, []);

	private static RuntimeArmorRegistryModel ClassicModel()
		=> new("m1", null, [new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 2, 0), new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000002", 2, 1)]);

	[Fact]
	public void Compile_LowPollutionTarget_EmitsOnlyCarrierLinks_NoHiddenUnit_NoClassicFallback()
	{
		var snapshot = Snapshot(World());
		var plan = Plan(World(), Layout(headMerge: false));

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(1, null, "m1")], 1, true, [ClassicModel()],
			new Dictionary<uint, LowPollutionMappingPlan> { [1] = plan });

		Assert.Equal(2, compiled.LinkCount);
		Assert.Equal(2, compiled.LowPollutionLinkCount);
		Assert.Equal(0, compiled.HiddenCount);
		Assert.Contains($"link={Hex(0x2001)},{BodyUnit}", compiled.Text);
		Assert.Contains($"link={Hex(0x6001)},{ArmsUnit}", compiled.Text);
		Assert.DoesNotContain(WardrobeLinkCompiler.HiddenUnit, compiled.Text);
		// 共用部件一行都不能出现：映射是全局的，写了就是污染。
		foreach (var shared in new[] { 0x2000UL, 0x3000UL, 0x6000UL, 0xcafeUL })
			Assert.DoesNotContain("link=" + Hex(shared), compiled.Text);
	}

	[Theory]
	[InlineData(LowPollutionMappingStatus.Unavailable)]
	[InlineData(LowPollutionMappingStatus.NotDeployed)]
	public void Compile_UnavailableOrNotDeployed_WritesNothingForTheTarget(LowPollutionMappingStatus status)
	{
		var snapshot = Snapshot(World());
		var plan = LowPollutionMappingPlan.Fail(1, "K1", "m1", status, "why");

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(1, null, "m1")], 1, true, [ClassicModel()],
			new Dictionary<uint, LowPollutionMappingPlan> { [1] = plan });

		Assert.Equal(0, compiled.LinkCount);
		Assert.Equal(1, compiled.LowPollutionSkipped);
		Assert.DoesNotContain("link=", compiled.Text);   // 不退回会连带别的护甲的全量映射
	}

	[Fact]
	public void Compile_IsByteIdenticalWhenLowPollutionDoesNotApply()
	{
		var snapshot = Snapshot(World());
		var mappings = new[] { new RuntimeArmorRegistryMapping(1, null, "m1"), new RuntimeArmorRegistryMapping(2, 1) };
		var models = new[] { ClassicModel() };
		var baseline = WardrobeLinkCompiler.Compile(snapshot, mappings, 5, true, models);
		Assert.True(baseline.LinkCount > 0);
		Assert.Equal(0, baseline.LowPollutionLinkCount);

		// 开关关 / 来源不是压缩素体：调用方不给规划结果（或给空表），一个字节都不变。
		Assert.Equal(baseline.Text, WardrobeLinkCompiler.Compile(snapshot, mappings, 5, true, models, null).Text);
		Assert.Equal(baseline.Text, WardrobeLinkCompiler.Compile(snapshot, mappings, 5, true, models, new Dictionary<uint, LowPollutionMappingPlan>()).Text);
		// 规划结果只针对别的目标 / 别的素体键：这条映射照旧。
		var elsewhere = new Dictionary<uint, LowPollutionMappingPlan> { [77] = Plan(World(), Layout(false)) };
		Assert.Equal(baseline.Text, WardrobeLinkCompiler.Compile(snapshot, mappings, 5, true, models, elsewhere).Text);
		var otherModel = new Dictionary<uint, LowPollutionMappingPlan> { [1] = Plan(World(), Layout(false)) with { ModelKey = "other" } };
		Assert.Equal(baseline.Text, WardrobeLinkCompiler.Compile(snapshot, mappings, 5, true, models, otherModel).Text);
		// 游戏护甲 → 游戏护甲（J5）永远走旧路径，哪怕同一份文件里同时有低污染目标。
		var mixed = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(1, null, "m1"), new RuntimeArmorRegistryMapping(2, 1)], 5, true, models,
			new Dictionary<uint, LowPollutionMappingPlan> { [1] = Plan(World(), Layout(false)) });
		Assert.Contains($"link={Hex(0x2000)},{Hex(0x2000)}", mixed.Text);   // 目标 2 的自链接：K2 映射到 K1 的 0x2000 件（同 unit）
		Assert.Contains("map=2,1", mixed.Text);
	}

	[Fact]
	public void Compile_DropsALowPollutionLinkThatFightsAnotherMappingForTheSameKey()
	{
		var plan = Plan(World(), Layout(false));
		// K1 经旧路径把 0x2001 映射到 c0ffee…01；低污染目标（另一个素体）想把同一个承载件映射到 BodyUnit ⇒ 冲突，丢弃并计数。
		var mappings = new[]
		{
			new RuntimeArmorRegistryMapping(1, null, "classic"),
			new RuntimeArmorRegistryMapping(3, null, "m1"),
		};
		var models = new[] { ClassicModel() with { Key = "classic" } };
		var kits = World();
		var conflicting = plan with { TargetItemId = 3, Links = [new LowPollutionLink(Hex(0x2001), BodyUnit)] };
		var compiled = WardrobeLinkCompiler.Compile(
			Snapshot(kits), mappings, 1, true, models, new Dictionary<uint, LowPollutionMappingPlan> { [3] = conflicting });

		Assert.Equal(1, compiled.LowPollutionConflicts);
		Assert.Equal(0, compiled.LowPollutionLinkCount);
		Assert.DoesNotContain($"link={Hex(0x2001)},{BodyUnit}", compiled.Text);
	}

	// ── 布局文件 ───────────────────────────────────────────────────────────

	private static SectionalCompressionSource Src(string id, ArmorPart part) => new(id, part, "Any");

	[Fact]
	public void LayoutReader_RoundTripsTheFileTheCompilerWrites()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[Src("00000000000a0001", ArmorPart.Helmet), Src("00000000000a0002", ArmorPart.Torso), Src("00000000000a0006", ArmorPart.LeftArm)],
			headMerge: false);
		Assert.True(LowPollutionLayoutReader.TryParse(LowPollutionLayoutFile.Serialize(plan), out var layout, out var error), error);

		Assert.Equal("three-slot", layout!.Mode);
		Assert.False(layout.HeadMerge);
		Assert.Equal("00000000000a0001", layout.UnitOf(LowPollutionRoles.Head));
		Assert.Equal("00000000000a0002", layout.UnitOf(LowPollutionRoles.Body));
		Assert.Equal("00000000000a0006", layout.UnitOf(LowPollutionRoles.Arms));
		Assert.Null(layout.UnitOf(LowPollutionRoles.HeadBody));

		var twoSlot = SectionalCompressionPlanner.Plan(
			[Src("00000000000a0001", ArmorPart.Helmet), Src("00000000000a0002", ArmorPart.Torso)], headMerge: true);
		Assert.True(LowPollutionLayoutReader.TryParse(LowPollutionLayoutFile.Serialize(twoSlot), out var merged, out _));
		Assert.True(merged!.HeadMerge);
		Assert.Equal("00000000000a0002", merged.UnitOf(LowPollutionRoles.HeadBody));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("{\"schema\":\"other/1\",\"units\":[]}")]
	[InlineData("{\"schema\":\"hd2mod.low-pollution-layout/1\"}")]
	[InlineData("{\"schema\":\"hd2mod.low-pollution-layout/1\",\"units\":[{\"role\":\"body\",\"unit\":\"XYZ\"}]}")]
	public void LayoutReader_RejectsBrokenFiles(string json)
	{
		Assert.False(LowPollutionLayoutReader.TryParse(json, out var layout, out var error));
		Assert.Null(layout);
		Assert.NotEmpty(error!);
	}

	// ── 编排：哪些映射走新路径、先部署后映射 ───────────────────────────────

	private static string WriteMod(string modsRoot, string name, string key, string pieceUnit, string? layoutText)
	{
		var overwrite = Path.Combine(modsRoot, name, ModNodePayloadLayout.OverwriteDirectoryName);
		Directory.CreateDirectory(overwrite);
		File.WriteAllText(Path.Combine(overwrite, CustomModelCatalog.FileName),
			$$"""{"key":"{{key}}","name":"{{key}}","pieces":[{"unit":"{{pieceUnit}}","body":3,"slot":2,"pieceType":0}]}""");
		if (layoutText is not null) File.WriteAllText(Path.Combine(overwrite, LowPollutionLayoutFile.FileName), layoutText);
		return Path.Combine(modsRoot, name);
	}

	private static string LayoutJson(params (string Role, string Unit)[] units)
		=> "{\"schema\":\"hd2mod.low-pollution-layout/1\",\"mode\":\"three-slot\",\"headMerge\":false,\"units\":["
			+ string.Join(",", units.Select(unit => $"{{\"role\":\"{unit.Role}\",\"unit\":\"{unit.Unit}\"}}")) + "]}";

	[Fact]
	public async Task Prep_OnlyModelsLinkedByAnEnabledMapping_AreEverConsidered()
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-prep-" + Guid.NewGuid().ToString("N"));
		try
		{
			// 两个素体都还没压缩过：m1 有映射，m2 只存在于左边列表。
			WriteMod(mods, "A", "m1", BodyUnit, null);
			WriteMod(mods, "B", "m2", ArmsUnit, null);
			RuntimeArmorRegistryMapping[] mappings = [new(1, null, "m1"), new(2, 77)];

			var keys = LowPollutionFreshness.SourceModelKeys(true, mappings);
			Assert.Equal(["m1"], keys);
			var plan = await LowPollutionFreshness.PlanAsync(mods, keys, lowPollution: true);
			var only = Assert.Single(plan);
			Assert.Equal("m1", only.ModelKey);

			// 映射未启用：谁都不判断，更不会编译。
			Assert.Empty(LowPollutionFreshness.SourceModelKeys(false, mappings));
			// 没有任何素体映射（只有护甲→护甲）：也不判断。
			Assert.Empty(LowPollutionFreshness.SourceModelKeys(true, [new RuntimeArmorRegistryMapping(2, 77)]));
			// 同一个素体被多条映射关联：只算一次。
			Assert.Single(LowPollutionFreshness.SourceModelKeys(true, [new(1, null, "m1"), new(3, null, "M1")]));
		}
		finally
		{
			try { Directory.Delete(mods, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Service_OnlyCompressedModelsTakeTheNewPath_AndUndeployedUnitsWriteNothing()
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-" + Guid.NewGuid().ToString("N"));
		try
		{
			WriteMod(mods, "A-compressed", "m1", BodyUnit, LayoutJson(("body", BodyUnit), ("arms", ArmsUnit)));
			WriteMod(mods, "B-plain", "m2", "00000000000000b2", null);
			WriteMod(mods, "C-broken", "m3", "00000000000000c3", "{ broken");
			var snapshot = Snapshot(World());
			RuntimeArmorRegistryMapping[] mappings =
			[
				new(1, null, "m1"), new(2, null, "m2"), new(3, null, "m3"), new(4, 2),
			];
			var deployed = new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16), Convert.ToUInt64(ArmsUnit, 16) };

			var plans = LowPollutionMappingService.Plan(mods, snapshot, mappings, deployed);

			// m2（没压缩过）与游戏→游戏的映射不出现：走旧路径。
			Assert.Equal([1u, 3u], plans.Select(plan => plan.TargetItemId).Order().ToArray());
			Assert.Equal(LowPollutionMappingStatus.Mapped, plans.Single(plan => plan.TargetItemId == 1).Status);
			// 布局文件在但读不出来：不退回全量映射，要用户重构建。
			var broken = plans.Single(plan => plan.TargetItemId == 3);
			Assert.Equal(LowPollutionMappingStatus.Unavailable, broken.Status);
			Assert.Contains("重构建", broken.Reason, StringComparison.Ordinal);

			// 先部署后映射：压缩 unit 缺一个（哪怕只缺手臂）也整套不写。
			var partial = LowPollutionMappingService.Plan(mods, snapshot, [mappings[0]], new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16) });
			var notDeployed = Assert.Single(partial);
			Assert.Equal(LowPollutionMappingStatus.NotDeployed, notDeployed.Status);
			Assert.Empty(notDeployed.Links);
			Assert.Contains(ArmsUnit, notDeployed.Reason, StringComparison.Ordinal);

			// 无法核对已部署集合：一律按未部署。
			Assert.Equal(LowPollutionMappingStatus.NotDeployed,
				Assert.Single(LowPollutionMappingService.Plan(mods, snapshot, [mappings[0]], null)).Status);
		}
		finally
		{
			try { Directory.Delete(mods, recursive: true); }
			catch { }
		}
	}

	// ── 端到端：ApplyAsync ─────────────────────────────────────────────────

	private static byte[] BootArchiveWithUnit(ulong unitFileId)
	{
		var resource = new byte[] { 1, 2, 3, 4 };
		var payload = new byte[resource.Length + 8];
		BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)resource.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
		resource.CopyTo(payload.AsSpan(8));
		const int typeOffset = 60, entryOffset = 92, payloadOffset = 176;
		var archive = new byte[payloadOffset + payload.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 28), 64);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset), AdaptationLuaAssetKeys.BootFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 16), payloadOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 56), (uint)payload.Length);
		payload.CopyTo(archive.AsSpan(payloadOffset));
		// 把唯一的条目改成一个 unit（与 RuntimeArmorRegistryServiceTests.ApplyFilter 同一手法）。
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(68), HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(92), unitFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(100), HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId);
		return archive;
	}

	private sealed class Env : IAsyncDisposable
	{
		public required string Root { get; init; }
		public required StoragePaths Paths { get; init; }
		public required string Registry { get; init; }
		public required string Data { get; init; }
		public required RuntimeArmorRegistryService Service { get; init; }

		public ValueTask DisposeAsync()
		{
			SqliteConnection.ClearAllPools();
			try { Directory.Delete(Root, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}

		/// <summary>K1：独有躯干 0x2001（另有共用的 0x2000）、一个独有的 0x6001；K2 共用 0x2000。压缩 unit 0x…0001 已部署。</summary>
		public static async Task<Env> CreateAsync(string compressedUnit = "0000000000000001")
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-lp-e2e", Guid.NewGuid().ToString("N"));
			var paths = new StoragePaths(Path.Combine(root, "app"));
			var registry = Path.Combine(root, "registry");
			var data = Path.Combine(root, "data");
			Directory.CreateDirectory(paths.IndexDirectory);
			Directory.CreateDirectory(registry);
			Directory.CreateDirectory(data);
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await using var command = connection.CreateCommand();
				const string type = "-2259526030728936129";
				command.CommandText = $"""
					CREATE TABLE game_data_authority_kits (item_id INTEGER PRIMARY KEY, dlc_id INTEGER NOT NULL, set_id INTEGER NOT NULL,
						kit_kind INTEGER NOT NULL, archive_id TEXT NOT NULL, display_name TEXT NOT NULL);
					INSERT INTO game_data_authority_kits VALUES (1, 0, 100, 0, 'a1', 'K1'), (2, 0, 200, 0, 'a2', 'K2');
					CREATE TABLE game_data_authority_pieces (item_id INTEGER, raw_body_type INTEGER, body_variant INTEGER, slot INTEGER,
						part_kind INTEGER, piece_type INTEGER, weight INTEGER, tone_variations INTEGER, unit_type_id INTEGER, unit_file_id INTEGER);
					INSERT INTO game_data_authority_pieces VALUES
						(1, 3, 3, 2, 0, 0, 0, 0, {type}, {0x2000}),
						(1, 3, 3, 2, 0, 1, 0, 0, {type}, {0x2001}),
						(1, 3, 3, 6, 0, 1, 0, 0, {type}, {0x6001}),
						(2, 3, 3, 2, 0, 0, 0, 0, {type}, {0x2000});
					""";
				await command.ExecuteNonQueryAsync();
			}

			SqliteConnection.ClearAllPools();
			await File.WriteAllBytesAsync(Path.Combine(data, "0000000000000001.patch_0"),
				BootArchiveWithUnit(Convert.ToUInt64(compressedUnit, 16)));
			WriteMod(paths.ModsDirectory, "Model", "m1", compressedUnit, LayoutJson(("body", compressedUnit)));
			return new Env { Root = root, Paths = paths, Registry = registry, Data = data, Service = new RuntimeArmorRegistryService(paths, registry) };
		}

		public RuntimeArmorRegistryModel Model(string unit = "0000000000000001")
			=> new("m1", null, [new RuntimeArmorRegistryModelPiece(3, unit, 2, 0)]);
	}

	[Fact]
	public async Task Apply_LowPollutionMode_WritesCarrierLinksAndHiderRules_AndClearsThemWhenTheMappingGoes()
	{
		await using var env = await Env.CreateAsync();
		var first = await env.Service.ReadSnapshotAsync();

		var result = await env.Service.ApplyAsync(
			first.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: true);

		var report = Assert.Single(result.LowPollution);
		Assert.True(report.IsMapped, report.Reason);
		var links = await File.ReadAllTextAsync(Path.Combine(env.Registry, WardrobeLinkCompiler.FileName));
		Assert.Contains("link=0000000000002001,0000000000000001", links);
		Assert.DoesNotContain(WardrobeLinkCompiler.HiddenUnit, links);
		Assert.DoesNotContain("link=0000000000002000", links);   // K1 与 K2 共用：不映射

		Assert.Equal(2, result.HideRuleCount);   // 承载件签名 + 压缩 unit 签名
		var rules = KitInstanceRulesProtocol.TryParse(
			await File.ReadAllTextAsync(KitInstanceRulesFile.GetMappingPath(env.Registry)), out var document, out var error)
			? document!
			: throw new InvalidDataException(error);
		Assert.Equal(["lp-1", "lp-1-u"], rules.Rules.Select(rule => rule.Name));
		Assert.Equal(["0000000000002000", "0000000000006001"], rules.Rules[0].Hide);
		Assert.Equal(2, KitInstanceRulesFile.CountMappingRules(env.Registry));
		var revision = KitInstanceRulesFile.ReadMappingRevision(env.Registry);

		// 撤销映射：规则文件被清空（不删除文件，revision 递增），Hider 读到 0 条规则。
		var cleared = await env.Service.ApplyAsync(
			result.Fingerprint, true, [], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: true);
		Assert.Equal(0, cleared.HideRuleCount);
		Assert.Equal(0, KitInstanceRulesFile.CountMappingRules(env.Registry));
		Assert.True(File.Exists(KitInstanceRulesFile.GetMappingPath(env.Registry)));
		Assert.True(KitInstanceRulesFile.ReadMappingRevision(env.Registry) > revision);
	}

	[Fact]
	public async Task Apply_SwitchingTheModeOff_ClearsTheRulesAndFallsBackToTheUnchangedClassicLinks()
	{
		await using var env = await Env.CreateAsync();
		var first = await env.Service.ReadSnapshotAsync();
		var on = await env.Service.ApplyAsync(
			first.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: true);
		Assert.Equal(2, on.HideRuleCount);

		// 关开关再应用：规则清空；映射走旧路径（与不传该参数的结果逐字节相同）。
		var off = await env.Service.ApplyAsync(
			on.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: false);
		Assert.Empty(off.LowPollution);
		Assert.Equal(0, KitInstanceRulesFile.CountMappingRules(env.Registry));
		var offLinks = (await File.ReadAllTextAsync(Path.Combine(env.Registry, WardrobeLinkCompiler.FileName)))
			.Split('\n').Where(line => !line.StartsWith("revision=", StringComparison.Ordinal)).ToArray();

		var omitted = await env.Service.ApplyAsync(
			off.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data);
		var omittedLinks = (await File.ReadAllTextAsync(Path.Combine(env.Registry, WardrobeLinkCompiler.FileName)))
			.Split('\n').Where(line => !line.StartsWith("revision=", StringComparison.Ordinal)).ToArray();
		Assert.Equal(offLinks, omittedLinks);
		Assert.Equal(0, omitted.HideRuleCount);
		Assert.Contains(offLinks, line => line.StartsWith("link=0000000000002000,", StringComparison.Ordinal));   // 旧路径：共用部件也映射了
	}

	[Fact]
	public async Task Apply_UndeployedCompressedUnit_WritesNoLinksAndNoRules()
	{
		// 压缩 unit 0x…0009 没有部署（游戏数据里只有 0x…0001）。
		await using var env = await Env.CreateAsync();
		WriteMod(env.Paths.ModsDirectory, "Model", "m1", "0000000000000001", LayoutJson(("body", "0000000000000009")));
		var first = await env.Service.ReadSnapshotAsync();

		var result = await env.Service.ApplyAsync(
			first.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: true);

		var report = Assert.Single(result.LowPollution);
		Assert.Equal("not-deployed", report.Status);
		Assert.Equal(0, result.HideRuleCount);
		Assert.False(File.Exists(KitInstanceRulesFile.GetMappingPath(env.Registry)));
		var linksPath = Path.Combine(env.Registry, WardrobeLinkCompiler.FileName);
		Assert.False(File.Exists(linksPath) && (await File.ReadAllTextAsync(linksPath)).Contains("link=", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Apply_SkippedModelKeys_StayInTheWorkspace_ButAreNeitherLinkedNorHiddenNorInTheProtocol()
	{
		await using var env = await Env.CreateAsync();
		var first = await env.Service.ReadSnapshotAsync();
		var result = await env.Service.ApplyAsync(
			first.Fingerprint, true, [new(1, null, "m1")], [env.Model()], [], [], gameDataDirectory: env.Data, lowPollutionMode: true,
			skipModelKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "M1" });

		Assert.Empty(result.LowPollution);
		Assert.Equal(0, result.HideRuleCount);
		var linksPath = Path.Combine(env.Registry, WardrobeLinkCompiler.FileName);
		Assert.False(File.Exists(linksPath) && (await File.ReadAllTextAsync(linksPath)).Contains("link=", StringComparison.Ordinal));
		Assert.False(File.Exists(KitInstanceRulesFile.GetMappingPath(env.Registry)));
		// 用户的意图还在 workspace；协议里没有它（不会让「映射优先」跳过 patch）。
		Assert.Single(result.Document.Mappings);
		var protocol = RuntimeArmorRegistryService.ParseProtocol(
			await File.ReadAllTextAsync(Path.Combine(env.Registry, RuntimeArmorRegistryService.ProtocolFileName)));
		Assert.Empty(protocol.Mappings);
		Assert.Empty(await ArmorMappingPatchCoverage.ReadAppliedMappedTargetsAsync(env.Registry));
	}
}

/// <summary>
/// S3 在真实产物与真实装备索引上跑一遍（样本由环境变量给出，缺任何一个静默跳过；样本复制到临时目录，真库只读）：
/// <c>HD2_TEST_SECTIONAL_MOD</c>（如雫 3730700338、蚀骨魂龙 3621723720）、<c>HD2_TEST_GAME_DATA</c>、<c>HD2_TEST_MANAGER_ROOT</c>（带 <c>data/indexes/asset-index.sqlite</c>）。
/// </summary>
public sealed class LowPollutionMappingRealDataTests(Xunit.Abstractions.ITestOutputHelper output)
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealModel_PlansEveryRealArmorAndHelmet_WithTheSharedInvariants(bool headMerge)
	{
		var sample = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		var manager = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(sample) || !Directory.Exists(Path.Combine(sample, "sectional"))) return;
		if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game) || string.IsNullOrWhiteSpace(manager)) return;
		var paths = new StoragePaths(manager);
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
		if (snapshot is null) return;

		var root = Path.Combine(Path.GetTempPath(), "hd2-lp-real", Guid.NewGuid().ToString("N"));
		var mod = Path.Combine(root, "mod");
		try
		{
			foreach (var directory in Directory.EnumerateDirectories(sample, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(Path.Combine(mod, Path.GetRelativePath(sample, directory)));
			Directory.CreateDirectory(mod);
			foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
			{
				var destination = Path.Combine(mod, Path.GetRelativePath(sample, file));
				File.Copy(file, destination, overwrite: true);
				File.SetAttributes(destination, FileAttributes.Normal);
			}

			var context = new VpkHostContext(game, paths) { LowPollutionMode = true };
			var compiled = await SectionalCompiler.CompileAsync(mod, context, null, null, headMerge, [], lowPollution: true);
			Assert.True(compiled.Ok, compiled.Error);
			Assert.True(LowPollutionLayoutReader.TryRead(mod, out var layout, out var error), error);
			Assert.Equal(headMerge, layout!.HeadMerge);

			var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);
			var named = snapshot.Kits
				.Where(kit => kit.Kind is GameDataEquipmentKitKind.Armor or GameDataEquipmentKitKind.Helmet
					&& SectionalExclusivePlanner.IsNamed(kit.DisplayName))
				.ToArray();
			var plans = named.Select(kit => LowPollutionMappingPlanner.Plan(index, layout, kit.ItemId, "m1")).ToArray();
			var layoutUnits = layout.AllUnits.ToHashSet(StringComparer.Ordinal);

			foreach (var plan in plans.Where(plan => plan.Status == LowPollutionMappingStatus.Mapped))
			{
				var kit = named.First(candidate => candidate.ItemId == plan.TargetItemId);
				var ownFiles = kit.Pieces.Select(piece => Hex(piece.UnitAssetKey.FileId)).ToHashSet(StringComparer.Ordinal);
				foreach (var link in plan.Links)
				{
					Assert.Contains(link.Unit, layoutUnits);
					Assert.Contains(link.Carrier, ownFiles);   // 承载件一定是目标自己的部件
					// 承载件的拥有者全在目标的同名组里（放宽口径）：绝不是别的套的共用部件。
					var group = kit.Kind == GameDataEquipmentKitKind.Armor
						? SectionalExclusivePlanner.GroupItems(index, kit)
						: index.Kits.Where(other => other.DisplayName == kit.DisplayName || (kit.SetId != 0 && other.SetId == kit.SetId))
							.Select(other => other.ItemId).Append(kit.ItemId).ToHashSet();
					Assert.All(index.Owners[Convert.ToUInt64(link.Carrier, 16)], owner => Assert.Contains(owner, group));
					Assert.DoesNotContain(link.Carrier, plan.Hide);
				}

				Assert.Equal(plan.Links.Count, plan.Links.Select(link => link.Carrier).Distinct().Count());
				Assert.All(plan.Hide, id => Assert.DoesNotContain(id, layoutUnits));
			}

			var rules = LowPollutionHiderRules.Build(plans);
			var fitted = KitInstanceRulesProtocol.FitToLimits(rules, out var dropped);
			var mapped = plans.Count(plan => plan.Status == LowPollutionMappingStatus.Mapped);
			var armors = plans.Where(plan => named.First(kit => kit.ItemId == plan.TargetItemId).Kind == GameDataEquipmentKitKind.Armor).ToArray();
			output.WriteLine(
				$"headMerge={headMerge} 有名字的护甲 {armors.Length} 套：可映射 {armors.Count(plan => plan.Status == LowPollutionMappingStatus.Mapped)}，"
				+ $"不可 {armors.Count(plan => plan.Status != LowPollutionMappingStatus.Mapped)}；头盔 {plans.Length - armors.Length} 条：可映射 {mapped - armors.Count(plan => plan.Status == LowPollutionMappingStatus.Mapped)}");
			output.WriteLine(
				$"Hider 规则 {rules.Count} 条（上限 {KitInstanceRulesProtocol.MaximumRules}），超限丢弃 {dropped}，隐藏集最大 {plans.Where(plan => plan.Status == LowPollutionMappingStatus.Mapped).Select(plan => plan.Hide.Count).DefaultIfEmpty(0).Max()}，平均 {plans.Where(plan => plan.Status == LowPollutionMappingStatus.Mapped).Select(plan => plan.Hide.Count).DefaultIfEmpty(0).Average():0.0}");
			foreach (var group in armors.Where(plan => plan.Status != LowPollutionMappingStatus.Mapped)
				.GroupBy(plan => plan.Reason!.Length > 24 ? plan.Reason![..24] : plan.Reason!).OrderByDescending(group => group.Count()))
				output.WriteLine($"  不可 ×{group.Count()}：{group.First().Reason}（例：{group.First().TargetName}）");
			foreach (var name in new[] { "DS-42", "CE-67" })
			{
				var example = armors.FirstOrDefault(plan => plan.TargetName.StartsWith(name, StringComparison.Ordinal));
				if (example is not null)
					output.WriteLine($"  {example.TargetName}：{example.Status} links={example.Links.Count} hide={example.Hide.Count} sig={string.Join('/', example.Signature)} {example.Reason}");
			}

			Assert.Equal(0, dropped);
			Assert.True(mapped > 0, "真实索引里至少有一套能映射");
			Assert.Equal(fitted.Count, rules.Count);
		}
		finally
		{
			try
			{
				foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(root, recursive: true);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	private static string Hex(ulong value) => value.ToString("x16");
}
