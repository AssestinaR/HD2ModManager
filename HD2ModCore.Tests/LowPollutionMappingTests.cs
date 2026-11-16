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
	private const string BodyArmsUnit = "00000000b0a70004";

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

	private static LowPollutionLayout Layout(bool headMerge, bool arms = true, bool head = true, bool bodyArms = false)
	{
		var units = new List<LowPollutionLayoutUnit>();
		if (!headMerge && head) units.Add(new(LowPollutionRoles.Head, HeadUnit));
		units.Add(new(headMerge ? LowPollutionRoles.HeadBody : LowPollutionRoles.Body, BodyUnit));
		if (arms) units.Add(new(LowPollutionRoles.Arms, ArmsUnit));
		if (arms && bodyArms) units.Add(new(LowPollutionRoles.BodyArms, BodyArmsUnit));
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
	public void SchemeA_NeverUsesHelmetsForArms_WhileKeepingTwoSlotHelmetIndependent()
	{
		// 方案 A + 2b：目标 K1 除躯干外没有别的独有护甲部件，即使同套 2 槽头盔 K3 一起映射，也不会跨槽把 Arms 放到头盔 0x1001 上。
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]);
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var armor = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 1, "m1", new HashSet<uint> { 3 });

		Assert.Equal(LowPollutionMappingStatus.Unavailable, armor.Status);
		Assert.Empty(armor.Links);
		Assert.Contains("除躯干外没有可安全承载手臂的护甲部件", armor.Reason, StringComparison.Ordinal);

		// 保留头盔独立映射（2b）：2 槽组合里只要同套护甲也在 companions 中，头盔仍标为 Mapped（Links/Hide 为空）。
		var helmet = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 3, "m1", new HashSet<uint> { 1 });
		Assert.Equal(LowPollutionMappingStatus.Mapped, helmet.Status);
		Assert.Empty(helmet.Links);
		Assert.Empty(helmet.Hide);

		// 没有护甲同时映射 ⇒ 仍然是「仅头盔 2 槽不可」。
		Assert.Equal(LowPollutionMappingStatus.Unavailable,
			LowPollutionMappingPlanner.Plan(index, Layout(headMerge: true), 3, "m1", new HashSet<uint>()).Status);
	}

	[Fact]
	public void TwoSlotAndThreeSlotArmor_NeedArmorArmsCarriers_UnlessSingleNonTorsoUsesBodyArms()
	{
		var noNonTorso = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]);
		var noNonTorsoIndex = SectionalExclusivePlanner.BuildIndex(noNonTorso);

		// 没有任何非躯干独有部件时（即使同套 2 槽头盔在 companions、即使带 BodyArms），不能把手臂放躯干或头盔上，均不可映射。
		var twoSlotWithHelmet = LowPollutionMappingPlanner.Plan(noNonTorsoIndex, Layout(headMerge: true), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Unavailable, twoSlotWithHelmet.Status);
		Assert.Empty(twoSlotWithHelmet.Links);

		var threeSlot = LowPollutionMappingPlanner.Plan(noNonTorsoIndex, Layout(headMerge: false), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Unavailable, threeSlot.Status);
		Assert.Empty(threeSlot.Links);

		var torsoOnlyBodyArms = LowPollutionMappingPlanner.Plan(noNonTorsoIndex, Layout(headMerge: true, bodyArms: true), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Unavailable, torsoOnlyBodyArms.Status);

		// 躯干被共用、仅剩 1 个非躯干独有件 0x6001 时，带 BodyArms 可由非躯干承载件合一映射。
		var sharedTorsoOneArm = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x2001 or 0x7001))]);
		var withBodyArms = LowPollutionMappingPlanner.Plan(
			SectionalExclusivePlanner.BuildIndex(sharedTorsoOneArm), Layout(headMerge: true, bodyArms: true), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Mapped, withBodyArms.Status);
		Assert.Equal([new LowPollutionLink(Hex(0x6001), BodyArmsUnit)], withBodyArms.Links);

		// 目标自己有非躯干独有部件时，不使用头盔 0x1001。
		var own = LowPollutionMappingPlanner.Plan(
			SectionalExclusivePlanner.BuildIndex(World()), Layout(headMerge: true), 1, "m1", new HashSet<uint> { 3 });
		Assert.Equal(LowPollutionMappingStatus.Mapped, own.Status);
		Assert.DoesNotContain(own.Links, link => link.Carrier == Hex(0x1001));
	}

	[Fact]
	public void Service_KeepsTwoSlotHelmetIndependentWhileArmorWithoutArmsStaysUnavailable()
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-" + Guid.NewGuid().ToString("N"));
		try
		{
			// 压缩布局 2 槽：head-body + arms。K1 没有除躯干外的独有护甲部件，即使与头盔 K3 一起映射，护甲也保持 Unavailable，而 K3 按 2b 保留为 Mapped。
			WriteMod(mods, "A", "m1", BodyUnit,
				"{\"schema\":\"hd2mod.low-pollution-layout/1\",\"mode\":\"two-slot\",\"headMerge\":true,\"units\":["
				+ $"{{\"role\":\"head-body\",\"unit\":\"{BodyUnit}\"}},{{\"role\":\"arms\",\"unit\":\"{ArmsUnit}\"}}]}}");
			var noArmorArms = Snapshot(World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]));
			var deployed = new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16), Convert.ToUInt64(ArmsUnit, 16) };

			var combo = LowPollutionMappingService.Plan(mods, noArmorArms, [new(1, null, "m1"), new(3, null, "m1")], deployed)
				.ToDictionary(plan => plan.TargetItemId);
			Assert.Equal(LowPollutionMappingStatus.Unavailable, combo[1].Status);
			Assert.Equal(LowPollutionMappingStatus.Mapped, combo[3].Status);
			Assert.Empty(combo[3].Links);
			Assert.Empty(combo[3].Hide);
			Assert.Empty(LowPollutionHiderRules.Build(combo.Values));

			// 仅映射 K3 头盔时同样不可。
			var helmetOnly = LowPollutionMappingService.Plan(mods, noArmorArms, [new(3, null, "m1")], deployed);
			Assert.Equal(LowPollutionMappingStatus.Unavailable, Assert.Single(helmetOnly).Status);

			// 护甲自身具备手臂承载件时，整套均可映射，且 K1 会隐藏 K3/K4 的原版头盔部件。
			var fullCombo = LowPollutionMappingService.Plan(mods, Snapshot(World()), [new(1, null, "m1"), new(3, null, "m1")], deployed)
				.ToDictionary(plan => plan.TargetItemId);
			Assert.All(fullCombo.Values, plan => Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status));
			Assert.Contains(Hex(0x1001), fullCombo[1].Hide);
			Assert.Contains(Hex(0x1002), fullCombo[1].Hide);
		}
		finally
		{
			try { Directory.Delete(mods, recursive: true); }
			catch { }
		}
	}

	[Theory]
	[InlineData(false, false, false, false, false, true)]
	[InlineData(true, false, false, false, false, false)]
	[InlineData(false, true, false, false, false, false)]
	[InlineData(false, false, true, false, false, true)] // 口径二：0x… 未命名内部记录不拦截孪生共享
	[InlineData(false, false, false, false, true, false)]
	[InlineData(false, false, false, true, false, true)]
	public void Service_SharesTwinCarriersOnlyWhenEveryOwnerHasTheSameLink(
		bool omitTwin, bool differentModel, bool addUnnamedOwner, bool changeTwinLayer, bool addNonPlayerOwner, bool expectedMapped)
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-" + Guid.NewGuid().ToString("N"));
		try
		{
			var layout = $$"""
				{"schema":"hd2mod.low-pollution-layout/1","mode":"three-slot","headMerge":false,
				 "units":[{"role":"body","unit":"{{BodyUnit}}"},{"role":"arms","unit":"{{ArmsUnit}}"}]}
				""";
			WriteMod(mods, "A", "m1", BodyUnit, layout);
			WriteMod(mods, "B", "m2", BodyUnit, layout);
			var twinParts = new[] { P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100), P(7, 1, 3, 0x7100) };
			var kits = new List<GameDataAuthorityKit>
			{
				Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", [P(1, 0, 3, 0xcafe), .. twinParts]),
				Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B",
					[P(1, 0, 3, 0xbeef), .. (changeTwinLayer ? twinParts.Select(piece => piece with { PieceType = 0u }) : twinParts.Reverse())]),
				Kit(3, 300, GameDataEquipmentKitKind.Armor, "Other", P(2, 1, 3, 0x2300), P(6, 1, 3, 0x6300), P(7, 1, 3, 0x7100)),
			};
			if (addUnnamedOwner)
				kits.Add(Kit(4, 400, GameDataEquipmentKitKind.Armor, "0x00000004", twinParts));
			if (addNonPlayerOwner)
				kits.Add(Kit(5, 500, GameDataEquipmentKitKind.Unknown, "Twin NonPlayer", twinParts));
			var snapshot = Snapshot(kits);
			var deployed = new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16), Convert.ToUInt64(ArmsUnit, 16) };
			var mappings = new List<RuntimeArmorRegistryMapping> { new(3, null, "m1"), new(1, null, "m1") };
			if (!omitTwin) mappings.Add(new(2, null, differentModel ? "m2" : "m1"));
			var plans = LowPollutionMappingService.Plan(mods, snapshot, mappings, deployed).ToDictionary(plan => plan.TargetItemId);

			Assert.Equal(Plan(kits, Layout(headMerge: false, head: false), item: 3).Links, plans[3].Links);
			var links = WardrobeLinkCompiler.Compile(snapshot, mappings, 1, true, lowPollution: plans);
			Assert.Equal(0, links.LowPollutionConflicts);
			if (expectedMapped)
			{
				Assert.Equal(LowPollutionMappingStatus.Mapped, plans[1].Status);
				Assert.Equal(LowPollutionMappingStatus.Mapped, plans[2].Status);
				Assert.Equal(plans[1].Links, plans[2].Links);
				Assert.Equal(4, links.LowPollutionLinkCount);
				Assert.Single(links.Text.Split('\n'), line => line == $"link={Hex(0x2100)},{BodyUnit}");
				var rule = Assert.Single(LowPollutionHiderRules.Build(plans.Values), rule => rule.All.Contains(Hex(0x2100)));
				Assert.Contains(Hex(0x7100), rule.Hide);
			}
			else
			{
				Assert.Equal(LowPollutionMappingStatus.Unavailable, plans[1].Status);
				Assert.Empty(plans[1].Links);
				Assert.Equal(2, links.LowPollutionLinkCount);
			}
		}
		finally
		{
			Directory.Delete(mods, recursive: true);
		}
	}

	[Fact]
	public void Service_RejectsASharedBodyWhenOnlyOneTwinCanPlaceItsArms()
	{
		var mods = Path.Combine(Path.GetTempPath(), "hd2-lp-" + Guid.NewGuid().ToString("N"));
		try
		{
			WriteMod(mods, "A", "m1", BodyUnit, $$"""
				{"schema":"hd2mod.low-pollution-layout/1","mode":"two-slot","headMerge":true,
				 "units":[{"role":"head-body","unit":"{{BodyUnit}}"},{"role":"arms","unit":"{{ArmsUnit}}"}]}
				""");
			var snapshot = Snapshot([
				Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", P(2, 0, 3, 0x2100)),
				Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B", P(2, 0, 3, 0x2100)),
				Kit(3, 100, GameDataEquipmentKitKind.Helmet, "Helmet A", P(0, 0, 3, 0x1100)),
			]);
			var deployed = new HashSet<ulong> { Convert.ToUInt64(BodyUnit, 16), Convert.ToUInt64(ArmsUnit, 16) };
			var plans = LowPollutionMappingService.Plan(mods, snapshot, [new(1, null, "m1"), new(2, null, "m1"), new(3, null, "m1")], deployed);
			Assert.All(plans.Where(plan => plan.TargetItemId is 1 or 2), plan => Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status));
			Assert.Empty(LowPollutionHiderRules.Build(plans));
		}
		finally
		{
			Directory.Delete(mods, recursive: true);
		}
	}

	[Fact]
	public void NoUniqueTorso_HangsTheBodyOnAnotherUniquePart_NeverOnTheSharedTorso()
	{
		// 0x2001 也被 K2 拥有 ⇒ 躯干部件都被共用。K1 还剩独有的左臂 0x6001、右臂 0x7001（规划 §16）。
		static List<GameDataAuthorityKit> SharedTorso(Func<List<GameDataAuthorityPiece>, List<GameDataAuthorityPiece>>? edit = null)
		{
			var kits = World(edit);
			kits[1] = Kit(2, 200, GameDataEquipmentKitKind.Armor, null,
				P(1, 0, 3, 0xcafe), P(2, 0, 3, 0x2000), P(2, 1, 3, 0x2001), P(3, 0, 3, 0x3000), P(6, 0, 3, 0x6000));
			return kits;
		}

		var plan = Plan(SharedTorso(), Layout(headMerge: false));
		Assert.True(plan.Status == LowPollutionMappingStatus.Mapped, plan.Reason);
		Assert.Equal(
			[new LowPollutionLink(Hex(0x6001), BodyUnit), new LowPollutionLink(Hex(0x7001), ArmsUnit)],
			plan.Links);
		Assert.DoesNotContain(plan.Links, link => link.Carrier is "0000000000002000" or "0000000000002001");
		Assert.Equal([Hex(0x6001)], plan.Signature);
		Assert.Contains(Hex(0x2001), plan.Hide);
		Assert.Contains(plan.Notes, note => note.Contains("身体改挂在「左臂」", StringComparison.Ordinal) && note.Contains("第一人称", StringComparison.Ordinal));

		// 胯 / 腿优先于手臂：加一个独有的左腿 0x4001 ⇒ 身体落在左腿，手臂照旧落在左臂。
		var leg = Plan(SharedTorso(t => [.. t, P(4, 1, 3, 0x4001)]), Layout(headMerge: false));
		Assert.True(leg.Status == LowPollutionMappingStatus.Mapped, leg.Reason);
		Assert.Equal(BodyUnit, leg.Links.Single(link => link.Carrier == Hex(0x4001)).Unit);
		Assert.Equal(ArmsUnit, leg.Links.Single(link => link.Carrier == Hex(0x6001)).Unit);

		// 只剩一个独有部件、布局有「身体+手臂」合体 unit（§17）⇒ 承载件改挂合体 unit，整套映射。
		var merged = Plan(SharedTorso(t => [.. t.Where(piece => piece.UnitAssetKey.FileId != 0x7001)]), Layout(headMerge: false, bodyArms: true));
		Assert.True(merged.Status == LowPollutionMappingStatus.Mapped, merged.Reason);
		Assert.Equal([new LowPollutionLink(Hex(0x6001), BodyArmsUnit)], merged.Links);
		Assert.Equal([Hex(0x6001)], merged.Signature);
		Assert.Equal([BodyUnit, BodyArmsUnit], merged.UnitSignature);
		Assert.Contains(merged.Notes, note => note.Contains("身体和手臂合成一个 unit", StringComparison.Ordinal));
		// 有两个独有部件时不用合体 unit。
		Assert.DoesNotContain(Plan(SharedTorso(), Layout(headMerge: false, bodyArms: true)).Links, link => link.Unit == BodyArmsUnit);

		// 只剩一个独有部件、布局里没有合体 unit（旧布局）⇒ 手臂没处放，整套不可（Q1：不做半套）。
		var one = Plan(SharedTorso(t => [.. t.Where(piece => piece.UnitAssetKey.FileId != 0x7001)]), Layout(headMerge: false));
		Assert.Equal(LowPollutionMappingStatus.Unavailable, one.Status);
		Assert.Contains("手臂", one.Reason, StringComparison.Ordinal);
		// 素体没有手臂槽时，一个独有部件就够。
		Assert.Equal(LowPollutionMappingStatus.Mapped,
			Plan(SharedTorso(t => [.. t.Where(piece => piece.UnitAssetKey.FileId != 0x7001)]), Layout(headMerge: false, arms: false)).Status);

		// 连别的独有部件也没有 ⇒ 仍然不可（这里 K2 拥有 K1 的全部部件，原因是换色孪生）。
		var none = Plan(SharedTorso(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]), Layout(headMerge: false));
		Assert.Equal(LowPollutionMappingStatus.Unavailable, none.Status);
		Assert.Empty(none.Links);
		Assert.Contains("只有颜色不同", none.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void BodyArms_IsNeverUsedWhenTheBodySitsOnTheTorso()
	{
		// 独有躯干 0x2001 在，躯干外没有别的独有部件：合体 unit 放在躯干上第一人称下手臂会跟着消失 ⇒ 不用，照旧不可（3 槽）。
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6001 or 0x7001))]);
		var plan = Plan(kits, Layout(headMerge: false, bodyArms: true));
		Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status);
		Assert.Contains("手臂", plan.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Freshness_ALayoutWithoutBodyArms_IsStale()
	{
		Assert.True(LowPollutionFreshness.MissesBodyArms(Layout(headMerge: false)));
		Assert.True(LowPollutionFreshness.MissesBodyArms(Layout(headMerge: true)));
		Assert.False(LowPollutionFreshness.MissesBodyArms(Layout(headMerge: false, bodyArms: true)));
		Assert.False(LowPollutionFreshness.MissesBodyArms(Layout(headMerge: false, arms: false)));
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
	public void SameNameGroup_RequiresAllOwnersToMapTheSameModelJointly()
	{
		var kits = World(t => [.. t.Where(piece => piece.UnitAssetKey.FileId != 0x7001)]);
		// 同名的另一条记录（item 6）也拥有 0x2001：只映射 item 1 时，未映射的 item 6 也是所有权拥有者，
		// 阻止单条越权使用 0x2001；剩下的 0x6001 不足以同时映射躯干和手臂。
		kits.Add(Kit(6, 900, GameDataEquipmentKitKind.Armor, "K1", P(2, 1, 3, 0x2001), P(6, 1, 3, 0x6006)));
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var single = LowPollutionMappingPlanner.Plan(index, Layout(headMerge: false), 1, "m1");
		Assert.Equal(LowPollutionMappingStatus.Unavailable, single.Status);

		// 两条同名记录一起映射到同一个素体时，0x2001 全组达成共识，各自再把手臂挂到自己的非躯干独有件上。
		var group = LowPollutionMappingPlanner.PlanArmorGroup(index, Layout(headMerge: false), [1, 6], "m1");
		Assert.Equal(LowPollutionMappingStatus.Mapped, group[1].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, group[6].Status);
		Assert.Equal(BodyUnit, group[1].Links.Single(link => link.Carrier == Hex(0x2001)).Unit);
		Assert.Equal(BodyUnit, group[6].Links.Single(link => link.Carrier == Hex(0x2001)).Unit);
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
		Assert.Equal(LowPollutionHiderRules.Radius, byCarrier.Radius);
	}

	private static LowPollutionMappingPlan Synthetic(uint item, string[] carriers, string[] hide, string unit = BodyUnit)
		=> new(item, $"T{item}", "m1", LowPollutionMappingStatus.Mapped, null,
			[.. carriers.Select(carrier => new LowPollutionLink(carrier, unit))], hide, [carriers[0]], [unit], []);

	[Fact]
	public void HiderRules_SplitBodyModel_GivesEachTorsoUnitItsOwnRuleName_AndTheFileSerializes()
	{
		// 2026-11-14 实机：分体素体有两个躯干压缩 unit，两条规则都叫 lp-<item>-u，协议判重名，整份规则与 wardrobe.links 都没写成。
		string stocky = Hex(0x5001), slim = Hex(0x5002);
		var plan = new LowPollutionMappingPlan(1, "T1", "m1", LowPollutionMappingStatus.Mapped, null,
			[new LowPollutionLink(Hex(0xa), stocky), new LowPollutionLink(Hex(0xb), slim)],
			[Hex(0xc), Hex(0xd)], [Hex(0xa), Hex(0xb)], [stocky, slim], []);

		var rules = LowPollutionHiderRules.Build([plan]);

		Assert.Equal(["lp-1", "lp-1-s2", "lp-1-u", "lp-1-u2"], rules.Select(rule => rule.Name).Order(StringComparer.Ordinal));
		Assert.Equal([stocky], rules.Single(rule => rule.Name == "lp-1-u").All);
		Assert.Equal([slim], rules.Single(rule => rule.Name == "lp-1-u2").All);
		var text = KitInstanceRulesProtocol.Serialize(new KitInstanceRulesDocument(1, rules));
		Assert.True(KitInstanceRulesProtocol.TryParse(text, out var parsed, out var error), error);
		Assert.Equal(4, parsed!.Rules.Count);
	}

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

	// ── S5：模型按体型分开做的布局（规划 §13.4）───────────────────────────

	private const string BodyUnitSlim = "00000000b0d10011";
	private const string ArmsUnitSlim = "00000000a7a50012";

	private static LowPollutionLayout SplitLayout(bool bodyStocky = true, bool bodySlim = true, bool armsStocky = true, bool armsSlim = true)
	{
		var units = new List<LowPollutionLayoutUnit> { new(LowPollutionRoles.Head, HeadUnit) };
		if (bodyStocky) units.Add(new(LowPollutionRoles.Body, BodyUnit, LowPollutionBodies.Stocky));
		if (bodySlim) units.Add(new(LowPollutionRoles.Body, BodyUnitSlim, LowPollutionBodies.Slim));
		if (armsStocky) units.Add(new(LowPollutionRoles.Arms, ArmsUnit, LowPollutionBodies.Stocky));
		if (armsSlim) units.Add(new(LowPollutionRoles.Arms, ArmsUnitSlim, LowPollutionBodies.Slim));
		return new LowPollutionLayout("three-slot", false, units);
	}

	/// <summary>目标 K1：躯干、手臂两种体型各有专属承载件（健壮 raw 0 / 纤细 raw 1）；0x2000 / 0x6000 与 K2 共用。</summary>
	private static List<GameDataAuthorityKit> BodySplitWorld(Func<List<GameDataAuthorityPiece>, List<GameDataAuthorityPiece>>? edit = null)
	{
		var t = new List<GameDataAuthorityPiece>
		{
			P(1, 0, 3, 0xcafe),
			P(2, 0, 3, 0x2000), P(2, 1, 0, 0x2a01), P(2, 1, 1, 0x2b01),
			P(3, 0, 3, 0x3000),
			P(6, 0, 3, 0x6000), P(6, 1, 0, 0x6a01), P(6, 1, 1, 0x6b01),
		};
		if (edit is not null) t = edit(t);
		return
		[
			Kit(1, 100, GameDataEquipmentKitKind.Armor, null, [.. t]),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, null, P(1, 0, 3, 0xcafe), P(2, 0, 3, 0x2000), P(3, 0, 3, 0x3000), P(6, 0, 3, 0x6000)),
		];
	}

	[Fact]
	public void SplitModel_EachBodyCarrierLinksToThatBodysUnit_AndBothTorsoUnitsAreSignatures()
	{
		var plan = Plan(BodySplitWorld(), SplitLayout());

		Assert.True(plan.Status == LowPollutionMappingStatus.Mapped, plan.Reason);
		Assert.Equal(
			[
				new LowPollutionLink(Hex(0x2a01), BodyUnit), new LowPollutionLink(Hex(0x2b01), BodyUnitSlim),
				new LowPollutionLink(Hex(0x6a01), ArmsUnit), new LowPollutionLink(Hex(0x6b01), ArmsUnitSlim),
			],
			plan.Links);
		Assert.Equal([Hex(0x2a01), Hex(0x2b01)], plan.Signature);
		// 备用签名：两个躯干压缩 unit 都要（resource_name 返回覆盖后的哪个都能触发）。
		Assert.Equal([BodyUnit, BodyUnitSlim], plan.UnitSignature);
		Assert.Equal([Hex(0x2000), Hex(0x3000), Hex(0x6000)], plan.Hide);
		Assert.DoesNotContain(plan.Notes, note => note.Contains("顶上", StringComparison.Ordinal));
		// 规则：两套签名 × 每套都带全部隐藏集（原 ID 的两个 + 两个压缩 unit）。
		var rules = LowPollutionHiderRules.Build([plan]);
		Assert.Contains(rules, rule => rule.Name == "lp-1-u" && rule.All.Contains(BodyUnit));
		Assert.Contains(rules, rule => rule.Name == "lp-1-u2" && rule.All.Contains(BodyUnitSlim));
	}

	[Fact]
	public void SplitModel_BodySpecificCarrierWinsOverAHigherLayerGeneralOne()
	{
		// 躯干槽里有一个更外层的通用件 0x2c01（raw 3，第 2 层）：模型的槽拆开时体型专属的优先，不能选它。
		var plan = Plan(BodySplitWorld(t => [.. t, P(2, 2, 3, 0x2c01)]), SplitLayout());
		Assert.True(plan.Status == LowPollutionMappingStatus.Mapped, plan.Reason);
		Assert.DoesNotContain(plan.Links, link => link.Carrier == Hex(0x2c01));
		Assert.Contains(plan.Links, link => link.Carrier == Hex(0x2a01));

		// 模型没拆（全 Any）时排序不变：最外层的通用件照旧胜出（逐字节不变的保证）。
		var unsplit = Plan(BodySplitWorld(t => [.. t, P(2, 2, 3, 0x2c01)]), Layout(headMerge: false));
		Assert.Contains(unsplit.Links, link => link.Carrier == Hex(0x2c01));
	}

	[Fact]
	public void SplitModel_GeneralOnlyCarrierForASplitSlot_MakesTheTargetUnavailable_ButAnUnsplitSlotIsFine()
	{
		// 手臂承载件只有通用件（SC-34 / PH-9 的形状），模型的手臂分壮实 / 纤细：一个部件放不下两份 ⇒ 整套不可，不写任何行。
		var world = BodySplitWorld(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6a01 or 0x6b01)), P(6, 1, 3, 0x6c01)]);
		var plan = Plan(world, SplitLayout());
		Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status);
		Assert.Empty(plan.Links);
		Assert.Contains("通用部件两种体型共用", plan.Reason, StringComparison.Ordinal);
		Assert.Contains("手臂", plan.Reason, StringComparison.Ordinal);

		// 同一个目标，模型的手臂没拆（Any）：照旧链到那一个 Any unit。
		var unsplitArms = Plan(world, SplitLayout(armsStocky: false, armsSlim: false) with
		{
			Units = [.. SplitLayout(armsStocky: false, armsSlim: false).Units, new(LowPollutionRoles.Arms, ArmsUnit)],
		});
		Assert.True(unsplitArms.Status == LowPollutionMappingStatus.Mapped, unsplitArms.Reason);
		Assert.Equal(ArmsUnit, unsplitArms.Links.Single(link => link.Carrier == Hex(0x6c01)).Unit);

		// 躯干同理：只有通用躯干承载件 + 躯干拆开 ⇒ 不可。
		var torsoGeneral = Plan(BodySplitWorld(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x2a01 or 0x2b01)), P(2, 1, 3, 0x2c01)]), SplitLayout());
		Assert.Equal(LowPollutionMappingStatus.Unavailable, torsoGeneral.Status);
		Assert.Contains("躯干", torsoGeneral.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void SplitModel_AMissingBody_UsesTheOtherBodysUnit_AndSaysSo()
	{
		// 模型手臂只有壮实一份：纤细承载件也链到它（用户 Q2 (b)），Notes 写明可能穿模。
		var plan = Plan(BodySplitWorld(), SplitLayout(armsSlim: false));
		Assert.True(plan.Status == LowPollutionMappingStatus.Mapped, plan.Reason);
		Assert.Equal(ArmsUnit, plan.Links.Single(link => link.Carrier == Hex(0x6a01)).Unit);
		Assert.Equal(ArmsUnit, plan.Links.Single(link => link.Carrier == Hex(0x6b01)).Unit);
		Assert.Contains(plan.Notes, note => note.Contains("纤细体型没有自己的手臂模型", StringComparison.Ordinal) && note.Contains("顶上", StringComparison.Ordinal));
		Assert.DoesNotContain(plan.Notes, note => note.Contains("健壮体型没有自己的", StringComparison.Ordinal));
		// 只有一个手臂 unit：不算拆开，所以即使承载件是通用的也不会不可。
		var general = Plan(
			BodySplitWorld(t => [.. t.Where(piece => piece.UnitAssetKey.FileId is not (0x6a01 or 0x6b01)), P(6, 1, 3, 0x6c01)]),
			SplitLayout(armsSlim: false));
		Assert.True(general.Status == LowPollutionMappingStatus.Mapped, general.Reason);
	}

	[Fact]
	public void UnsplitLayout_PlansAreIdenticalToTheOnesBeforeS5_WhetherParsedFromSchema1OrBuiltByHand()
	{
		// 全 Any 布局：/1 文件读出来的布局与手搭的 Any 布局规划结果完全相同（链接、隐藏、签名、说明）。
		var parsed = LowPollutionLayoutReader.TryParse(
			LayoutJson(("head", HeadUnit), ("body", BodyUnit), ("arms", ArmsUnit)), out var layout, out var error);
		Assert.True(parsed, error);
		var fromFile = Plan(World(), layout!);
		var byHand = Plan(World(), Layout(headMerge: false));
		Assert.Equal(byHand.Links, fromFile.Links);
		Assert.Equal(byHand.Hide, fromFile.Hide);
		Assert.Equal(byHand.Signature, fromFile.Signature);
		Assert.Equal(byHand.UnitSignature, fromFile.UnitSignature);
		Assert.Equal(byHand.Notes, fromFile.Notes);
		Assert.Equal([BodyUnit], fromFile.UnitSignature);
	}

	[Fact]
	public void SplitHeadLayout_HelmetCarriersAreOnlyMappedWhenBothBodiesCanBeServed()
	{
		// 头槽按体型拆开本来不会由压缩器产生（头盔没有体型）；读到这样的布局时头盔的通用承载件放不下两份 ⇒ 不可，而不是挑一份。
		var layout = new LowPollutionLayout("three-slot", false,
		[
			new(LowPollutionRoles.Head, HeadUnit, LowPollutionBodies.Stocky),
			new(LowPollutionRoles.Head, "000000000ead0013", LowPollutionBodies.Slim),
			new(LowPollutionRoles.Body, BodyUnit),
		]);
		var plan = Plan(World(), layout, item: 3);
		Assert.Equal(LowPollutionMappingStatus.Unavailable, plan.Status);
		Assert.Contains("头", plan.Reason, StringComparison.Ordinal);
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

	[Fact]
	public async Task Apply_SkippedModelWithoutDeployedUnits_DoesNotFailTheOtherMappings()
	{
		// 2026-11-14 实机：被跳过的素体（目标不是自己的素体）恰好没部署，「已部署 Unit」检查先于跳过执行，整次应用失败。
		await using var env = await Env.CreateAsync();
		WriteMod(env.Paths.ModsDirectory, "Other", "m2", "0000000000000009", LayoutJson(("body", "0000000000000009")));
		var undeployed = new RuntimeArmorRegistryModel("m2", null, [new RuntimeArmorRegistryModelPiece(3, "0000000000000009", 2, 0)]);
		var first = await env.Service.ReadSnapshotAsync();

		var result = await env.Service.ApplyAsync(
			first.Fingerprint, true, [new(1, null, "m1"), new(2, null, "m2")], [env.Model(), undeployed], [], [],
			gameDataDirectory: env.Data, lowPollutionMode: true,
			skipModelKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m2" });

		var report = Assert.Single(result.LowPollution);
		Assert.True(report.IsMapped, report.Reason);
		Assert.Equal(2, result.Document.Mappings.Count);
		var protocol = RuntimeArmorRegistryService.ParseProtocol(
			await File.ReadAllTextAsync(Path.Combine(env.Registry, RuntimeArmorRegistryService.ProtocolFileName)));
		Assert.Equal("m1", Assert.Single(protocol.Mappings).ModelKey);
	}

	[Fact]
	public void PlanArmorGroup_ConstrainedTwinAndPartialPrivateSibling_ShareConsensusCarrierCleanly()
	{
		// B-01 / TR-40 模式：Twin A / Twin B 在所有拥有者均已映射到同一素体时，按全组共识把 Body 放共享躯干、Arms 放全组共用手臂；
		// 即使同组中另有一套 Brother C 拥有自己的私有躯干 0x2003、仅在 0x6001 上与孪生体共用，三套仍可联合成功。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(3, 300, GameDataEquipmentKitKind.Armor, "Brother C", P(2, 1, 3, 0x2300), P(6, 1, 3, 0x6100)),
		};
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var plans = LowPollutionMappingPlanner.PlanArmorGroup(index, Layout(headMerge: false), [1, 2, 3], "m1");

		Assert.All(plans.Values, plan => Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status));
		Assert.Equal(plans[1].Links, plans[2].Links);
		Assert.Equal([new LowPollutionLink(Hex(0x2100), BodyUnit), new LowPollutionLink(Hex(0x6100), ArmsUnit)], plans[1].Links);
		Assert.Equal([new LowPollutionLink(Hex(0x2300), BodyUnit), new LowPollutionLink(Hex(0x6100), ArmsUnit)], plans[3].Links);
	}

	[Fact]
	public void PlanArmorGroup_JointTwinBodyArmsFallback_AndTorsoPreferenceAvoidStarvation()
	{
		// 1) 当孪生组在非躯干与躯干都有共用件时，HasTorsoOnAll 优先将躯干放回 torso，不抢走唯一非躯干 arms 承载件。
		var withSharedArm = new List<GameDataAuthorityKit>
		{
			Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", P(2, 0, 3, 0x2100), P(6, 2, 3, 0x6100)),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B", P(2, 0, 3, 0x2100), P(6, 2, 3, 0x6100)),
		};
		var mappedNormal = LowPollutionMappingPlanner.PlanArmorGroup(
			SectionalExclusivePlanner.BuildIndex(withSharedArm), Layout(headMerge: false, bodyArms: true), [1, 2], "m1");
		Assert.All(mappedNormal.Values, plan => Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status));
		Assert.Equal([new LowPollutionLink(Hex(0x2100), BodyUnit), new LowPollutionLink(Hex(0x6100), ArmsUnit)], mappedNormal[1].Links);
		Assert.Equal(mappedNormal[1].Links, mappedNormal[2].Links);

		// 2) 当孪生组躯干与外界共用、只剩唯一全组共用非躯干部件 0x6100，且布局带 body-arms 时，联合规划共同回退到 BodyArmsUnit。
		var sharedTorsoOneArm = new List<GameDataAuthorityKit>
		{
			Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", P(2, 0, 3, 0x2000), P(6, 1, 3, 0x6100)),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B", P(2, 0, 3, 0x2000), P(6, 1, 3, 0x6100)),
			Kit(3, 300, GameDataEquipmentKitKind.Armor, "Unmapped Other", P(2, 0, 3, 0x2000), P(6, 1, 3, 0x6300)),
		};
		var mappedBodyArms = LowPollutionMappingPlanner.PlanArmorGroup(
			SectionalExclusivePlanner.BuildIndex(sharedTorsoOneArm), Layout(headMerge: false, bodyArms: true), [1, 2], "m1");
		Assert.All(mappedBodyArms.Values, plan => Assert.Equal(LowPollutionMappingStatus.Mapped, plan.Status));
		Assert.Equal([new LowPollutionLink(Hex(0x6100), BodyArmsUnit)], mappedBodyArms[1].Links);
		Assert.Equal(mappedBodyArms[1].Links, mappedBodyArms[2].Links);
	}

	[Fact]
	public void PlanArmorGroup_NonPlayerOwnerStripsAffectedTwinsWhileIndependentSiblingSurvives()
	{
		// 非玩家记录或未映射拥有者污染 0x6100 时，依赖它的 Twin A/B 整套剥除；有自己私有 0x6300 的 Brother C 不受牵连、单独成功。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, 100, GameDataEquipmentKitKind.Armor, "Twin A", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, "Twin B", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(3, 300, GameDataEquipmentKitKind.Armor, "Brother C", P(2, 1, 3, 0x2300), P(6, 1, 3, 0x6100), P(7, 1, 3, 0x6300)),
			Kit(4, 400, GameDataEquipmentKitKind.Unknown, "NPC", P(2, 1, 3, 0x2400), P(6, 1, 3, 0x6100)),
		};
		var plans = LowPollutionMappingPlanner.PlanArmorGroup(
			SectionalExclusivePlanner.BuildIndex(kits), Layout(headMerge: false), [1, 2, 3], "m1");

		Assert.Equal(LowPollutionMappingStatus.Unavailable, plans[1].Status);
		Assert.Equal(LowPollutionMappingStatus.Unavailable, plans[2].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[3].Status);
		Assert.Equal([new LowPollutionLink(Hex(0x2300), BodyUnit), new LowPollutionLink(Hex(0x6300), ArmsUnit)], plans[3].Links);
	}

	[Fact]
	public void PlanArmorGroup_IgnoresUnnamedAndUnobtainableNonPlayerOwners_RescuingTwins()
	{
		// 口径二：未命名 0x… 记录与「拿不到」的非玩家记录（2107035846 / 1969314276）不拦截共用承载件。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, 100, GameDataEquipmentKitKind.Armor, "SC-37", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(2, 200, GameDataEquipmentKitKind.Armor, "AF-50", P(2, 1, 3, 0x2200), P(6, 1, 3, 0x6100)),
			Kit(3, 300, GameDataEquipmentKitKind.Armor, "0xe1e36c37", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
			Kit(2107035846u, 400, GameDataEquipmentKitKind.Armor, "CM-09", P(2, 1, 3, 0x2100), P(6, 1, 3, 0x6100)),
		};
		var plans = LowPollutionMappingPlanner.PlanArmorGroup(
			SectionalExclusivePlanner.BuildIndex(kits), Layout(headMerge: false), [1, 2], "m1");

		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[1].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[2].Status);
		Assert.Equal([new LowPollutionLink(Hex(0x2100), BodyUnit), new LowPollutionLink(Hex(0x6100), ArmsUnit)], plans[1].Links);
		Assert.Equal([new LowPollutionLink(Hex(0x2200), BodyUnit), new LowPollutionLink(Hex(0x6100), ArmsUnit)], plans[2].Links);
		Assert.DoesNotContain(plans[1].Notes, note => note.Contains("0xe1e36c37", StringComparison.Ordinal));
		Assert.DoesNotContain(plans[1].Notes, note => note.Contains("2107035846", StringComparison.Ordinal));
	}

	[Fact]
	public void PlanArmorGroup_DemocracyOfficerNpcStillBlocksUnmappedTorsoUnlessJointlyMapped()
	{
		// 口径二：会在船上出场的民主官 NPC（124623533）未入组时仍拦阻共用躯干；同组入组时达成共识。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1490803405u, 100, GameDataEquipmentKitKind.Armor, "FS-05", P(2, 1, 3, 0x2500), P(8, 1, 3, 0x8500)),
			Kit(KitNonPlayerArmors.DemocracyOfficerArmorId, 100, GameDataEquipmentKitKind.Armor, "FS-05", P(2, 1, 3, 0x2500), P(6, 1, 3, 0x6500)),
			Kit(3, 300, GameDataEquipmentKitKind.Armor, "Other", P(2, 1, 3, 0x2300), P(6, 1, 3, 0x6300)),
		};
		var index = SectionalExclusivePlanner.BuildIndex(kits);

		var blocked = LowPollutionMappingPlanner.PlanArmorGroup(index, Layout(headMerge: false), [1490803405u, 3u], "m1");
		Assert.Equal(LowPollutionMappingStatus.Unavailable, blocked[1490803405u].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, blocked[3u].Status);

		var joint = LowPollutionMappingPlanner.PlanArmorGroup(
			index, Layout(headMerge: false), [1490803405u, KitNonPlayerArmors.DemocracyOfficerArmorId], "m1");
		Assert.Equal(LowPollutionMappingStatus.Mapped, joint[1490803405u].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, joint[KitNonPlayerArmors.DemocracyOfficerArmorId].Status);
	}

	[Fact]
	public void PlanArmorGroup_PrioritizesTightlyConstrainedBodyArms_RescuingFs05WhenSiblingSharesTorso()
	{
		// FS-05 1490803405（未知）与 CE-67 共用 0x2c00 躯干、且两套玩家版 1490803405/3970674111 共用 0x8500 左肩（不牵连民主官 NPC 124623533）：
		// 即使按 ItemId 排序 1490803405 在 3970674111 之前，MRV 也必须先让只有 1 个可用承载件的 3970674111 锁定 0x8500 -> body-arms，
		// 不让 1490803405 抢先占位 0x2c00 导致双方全部失败。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1490803405u, 100, GameDataEquipmentKitKind.Armor, "FS-05",
				P(2, 1, 3, 0x2500), P(2, 1, 3, 0x2c00), P(8, 1, 3, 0x8500)),
			Kit(3970674111u, 100, GameDataEquipmentKitKind.Armor, "FS-05",
				P(2, 1, 3, 0x2500), P(8, 1, 3, 0x8500)),
			Kit(KitNonPlayerArmors.DemocracyOfficerArmorId, 100, GameDataEquipmentKitKind.Armor, "FS-05",
				P(2, 1, 3, 0x2500), P(8, 1, 3, 0x8000)),
			Kit(4081083564u, 200, GameDataEquipmentKitKind.Armor, "CE-67",
				P(2, 1, 3, 0x2c00), P(2, 1, 3, 0x2e00), P(6, 1, 3, 0x6e00)),
		};
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var plans = LowPollutionMappingPlanner.PlanArmorGroup(
			index, Layout(headMerge: true, bodyArms: true), [1490803405u, 3970674111u, 4081083564u], "m1");

		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[1490803405u].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[3970674111u].Status);
		Assert.Equal(LowPollutionMappingStatus.Mapped, plans[4081083564u].Status);
		Assert.Equal([new LowPollutionLink(Hex(0x8500), BodyArmsUnit)], plans[1490803405u].Links);
		Assert.Equal([new LowPollutionLink(Hex(0x8500), BodyArmsUnit)], plans[3970674111u].Links);
		Assert.Equal([new LowPollutionLink(Hex(0x2e00), BodyUnit), new LowPollutionLink(Hex(0x6e00), ArmsUnit)], plans[4081083564u].Links);
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
			foreach (var name in new[] { "DS-42", "CE-67", "B-08" })
			{
				var example = armors.FirstOrDefault(plan => plan.TargetName.StartsWith(name, StringComparison.Ordinal));
				if (example is not null)
					output.WriteLine($"  {example.TargetName}：{example.Status} links={example.Links.Count} hide={example.Hide.Count} sig={string.Join('/', example.Signature)} {example.Reason}");
				if (example is not null) foreach (var note in example.Notes) output.WriteLine($"    · {note}");
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
