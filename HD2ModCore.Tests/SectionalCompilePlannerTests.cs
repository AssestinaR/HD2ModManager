using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

/// <summary>
/// 规划器的规则测试：**纯函数，不碰磁盘**。
/// <para>
/// 这些断言就是"替换护甲"这一层的合同：披风不碰、肩隐藏、一个来源可以复制成多份（每个身形一份）、
/// 头身合并取哪两层、以及"三个桶必须刚好覆盖目标全集"。改规则时先改这里。
/// </para>
/// </summary>
public sealed class SectionalCompilePlannerTests
{
	private const int HelmetSlot = 0;
	private const int CapeSlot = 1;
	private const int TorsoSlot = 2;
	private const int HipsSlot = 3;
	private const int LeftLegSlot = 4;
	private const int RightLegSlot = 5;
	private const int LeftArmSlot = 6;
	private const int RightArmSlot = 7;
	private const int LeftShoulderSlot = 8;
	private const int RightShoulderSlot = 9;

	[Fact]
	public void CapeIsNeverReplacedNorHidden()
	{
		var plan = SectionalCompilePlanner.Build(StandardSource(), StandardTargets(), headMerge: false);

		var cape = new AssetKey(UnitTypeId, 0xcafe);
		Assert.DoesNotContain(plan.Replacements, replacement => replacement.Target.Key == cape);
		Assert.DoesNotContain(plan.Hidden, target => target.Key == cape);
	}

	[Fact]
	public void ShouldersAreHidden()
	{
		var plan = SectionalCompilePlanner.Build(StandardSource(), StandardTargets(), headMerge: false);

		Assert.Contains(plan.Hidden, target => target.Cell.Slot == LeftShoulderSlot);
		Assert.Contains(plan.Hidden, target => target.Cell.Slot == RightShoulderSlot);
	}

	[Fact]
	public void OneSourceBecomesOneCopyPerBodyShape()
	{
		var plan = SectionalCompilePlanner.Build(StandardSource(), StandardTargets(), headMerge: false);

		var torso = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == TorsoSlot).ToArray();
		// 躯干最外层是 pc2，Stocky 与 Slim 各一个 unit。
		Assert.Equal(2, torso.Length);
		Assert.All(torso, replacement => Assert.Equal(2, replacement.Target.Cell.Layer));
		Assert.Equal(2, torso.Select(replacement => replacement.Target.Key).Distinct().Count());
	}

	[Fact]
	public void HeadMergePutsBodyOutsideAndHeadOneLayerDeeper()
	{
		var plan = SectionalCompilePlanner.Build(StandardSource(), StandardTargets(), headMerge: true);

		Assert.Equal(SectionalHeadMergePlan.IntoArmorTorso, plan.HeadMerge);

		var body = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == TorsoSlot).ToArray();
		var head = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == HelmetSlot).ToArray();
		Assert.All(body, replacement => Assert.Equal(2, replacement.Target.Cell.Layer));
		Assert.All(head, replacement => Assert.Equal(1, replacement.Target.Cell.Layer));
		// 头的来源是同一个 unit，被复制到躯干 pc1 的两个身形上。
		Assert.Equal(2, head.Length);
		Assert.All(head, replacement => Assert.Equal(TorsoSlot, replacement.Target.Cell.Slot));
	}

	[Fact]
	public void HeadMergeHidesTheHelmetKit()
	{
		var plan = SectionalCompilePlanner.Build(StandardSource(), StandardTargets(), headMerge: true);

		Assert.Contains(plan.Hidden, target => target.Cell.Slot == HelmetSlot);
		Assert.DoesNotContain(plan.Replacements, replacement => replacement.Target.Cell.Slot == HelmetSlot);
	}

	// ── 同一套里有两件护甲（实测 B-08「轻装枪手」+ FS-38「根除者」，setId 3794083209）──
	//
	// 两件共用胸部 pc0/pc1、胯部 pc1 的 unit；FS-38 另有胸部 pc2、胯部 pc2 两层装饰。
	// 旧规则按并集取最外层 → 上衣/裙子只写进 FS-38 独有的 pc2，B-08 的胸部与胯部被顶成隐藏壳。

	private const string LightGunner = "dfa7c28c3b490a8c";
	private const string Eradicator = "813da450b8029cb2";
	private const string SetHelmet = "0290d8011fce62ef";

	private static (SectionalTargetUnit[] Targets, Dictionary<AssetKey, IReadOnlyCollection<string>> Kits) TwoArmourSet()
	{
		var shared = new[]
		{
			Target(0x2001, TorsoSlot, 0), Target(0x2002, TorsoSlot, 0),
			Target(0x2101, TorsoSlot, 1), Target(0x2102, TorsoSlot, 1),
			Target(0x3101, HipsSlot, 1), Target(0x3102, HipsSlot, 1),
			Target(0x6101, LeftArmSlot, 1),
		};
		var eradicatorOnly = new[]
		{
			Target(0x2201, TorsoSlot, 2), Target(0x2202, TorsoSlot, 2),
			Target(0x3201, HipsSlot, 2), Target(0x3202, HipsSlot, 2),
		};
		var helmet = Target(0x0001, HelmetSlot, 0);

		var kits = new Dictionary<AssetKey, IReadOnlyCollection<string>>();
		foreach (var target in shared) kits[target.Key] = [LightGunner, Eradicator];
		foreach (var target in eradicatorOnly) kits[target.Key] = [Eradicator];
		kits[helmet.Key] = [SetHelmet];
		return ([.. shared, .. eradicatorOnly, helmet], kits);
	}

	private static SectionalSourceUnit[] TwoArmourSource() =>
	[
		new("aaaa000000000001", new ArmorCell(HelmetSlot, 0)),
		new("aaaa000000000002", new ArmorCell(TorsoSlot, 0)),
		new("aaaa000000000003", new ArmorCell(HipsSlot, 0)),
		new("aaaa000000000006", new ArmorCell(LeftArmSlot, 0)),
	];

	[Fact]
	public void SharedSetLandsTorsoAndHipOnLayersEveryArmourHas()
	{
		var (targets, kits) = TwoArmourSet();

		var plan = SectionalCompilePlanner.Build(TwoArmourSource(), targets, headMerge: false, kits);

		var torso = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == TorsoSlot).ToArray();
		var hip = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == HipsSlot).ToArray();
		Assert.Equal([0x2101UL, 0x2102UL], torso.Select(replacement => replacement.Target.Key.FileId).Order());
		Assert.Equal([0x3101UL, 0x3102UL], hip.Select(replacement => replacement.Target.Key.FileId).Order());
		// FS-38 独有的装饰层顶成隐藏，否则它会叠在我们的几何外面。
		Assert.All(new ulong[] { 0x2201, 0x2202, 0x3201, 0x3202 }, id =>
			Assert.Contains(plan.Hidden, target => target.Key.FileId == id));
		Assert.Contains(plan.Info, line => line.Contains("共用", StringComparison.Ordinal));
	}

	[Fact]
	public void SharedSetHeadMergeUsesOnlyCommonTorsoLayers()
	{
		var (targets, kits) = TwoArmourSet();

		var plan = SectionalCompilePlanner.Build(TwoArmourSource(), targets, headMerge: true, kits);

		Assert.Equal(SectionalHeadMergePlan.IntoArmorTorso, plan.HeadMerge);
		var body = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == TorsoSlot).ToArray();
		var head = plan.Replacements.Where(replacement => replacement.Source.Cell.Slot == HelmetSlot).ToArray();
		// 两件都有 pc0/pc1：身体占外层 pc1，头占 pc0；FS-38 独有的 pc2 不再接任何几何。
		Assert.All(body, replacement => Assert.Equal(1, replacement.Target.Cell.Layer));
		Assert.All(head, replacement => Assert.Equal(0, replacement.Target.Cell.Layer));
		Assert.Equal(2, body.Length);
		Assert.Equal(2, head.Length);
		Assert.DoesNotContain(plan.Replacements, replacement => replacement.Target.Cell.Layer == 2);
	}

	[Fact]
	public void ArmoursWithoutACommonLayerFallBackToOutermostAndWarn()
	{
		var targets = new[]
		{
			Target(0x2101, TorsoSlot, 1),
			Target(0x2201, TorsoSlot, 2),
		};
		var kits = new Dictionary<AssetKey, IReadOnlyCollection<string>>
		{
			[targets[0].Key] = [LightGunner],
			[targets[1].Key] = [Eradicator],
		};

		var plan = SectionalCompilePlanner.Build(
			[new SectionalSourceUnit("aaaa000000000002", new ArmorCell(TorsoSlot, 0))], targets, headMerge: false, kits);

		Assert.Single(plan.Replacements);
		Assert.Equal(2, plan.Replacements[0].Target.Cell.Layer);
		Assert.Contains(plan.Warnings, line => line.Contains("没有共同的层", StringComparison.Ordinal));
	}

	// ── 被牵连的护甲（实测 CE-74「破裂者」：不在 B-08 那一套，但共用胸/胯/四肢的 unit）──

	private static SectionalCollateralKit Breaker() => new("CE-74", [
		Target(0x2101, TorsoSlot, 1),            // 共用：会接到我们的上衣
		Target(0x3101, HipsSlot, 1),             // 共用：会接到我们的裙子
		Target(0x7201, TorsoSlot, 2),            // 独有装饰层 → 隐藏
		Target(0x7301, HipsSlot, 2),             // 独有装饰层，但还被别的护甲用 → 保持原样
		Target(0x7801, LeftShoulderSlot, 0),     // 独有肩，我们没有肩的来源 → 隐藏
		Target(0x7401, LeftLegSlot, 1),          // 腿：我们有来源，但它在腿上一个替换件都没接到 → 保留
		Target(0xcafe, CapeSlot, 0),             // 披风永远不碰
	]);

	[Fact]
	public void CollateralArmourExtrasAreHiddenExceptSharedOrUncovered()
	{
		var (targets, kits) = TwoArmourSet();
		var source = TwoArmourSource().Append(new SectionalSourceUnit("aaaa000000000004", new ArmorCell(LeftLegSlot, 0))).ToArray();
		var protectedUnits = new HashSet<AssetKey> { new(UnitTypeId, 0x7301) };

		var plan = SectionalCompilePlanner.Build(source, targets, headMerge: false, kits, [Breaker()], protectedUnits);

		var hidden = plan.Hidden.Select(target => target.Key.FileId).ToHashSet();
		Assert.Contains(0x7201UL, hidden);
		Assert.Contains(0x7801UL, hidden);
		Assert.DoesNotContain(0x7301UL, hidden);
		Assert.DoesNotContain(0x7401UL, hidden);
		Assert.DoesNotContain(0xcafeUL, hidden);
		// 共用件照旧由目标套装决定（被替换），不会被牵连规则改成隐藏。
		Assert.Contains(plan.Replacements, replacement => replacement.Target.Key.FileId == 0x2101);
		Assert.Contains(plan.Info, line => line.Contains("CE-74", StringComparison.Ordinal)
			&& line.Contains("隐藏了它独有的 2 个", StringComparison.Ordinal));
	}

	[Fact]
	public void SingleArmourKitMapKeepsTheOutermostRule()
	{
		// 只有一件护甲时，归属表不能改变任何结果（防止新规则误伤常规换甲）。
		var targets = StandardTargets();
		var kits = targets.ToDictionary(target => target.Key, _ => (IReadOnlyCollection<string>)[LightGunner]);

		var withKits = SectionalCompilePlanner.Build(StandardSource(), targets, headMerge: true, kits);
		var without = SectionalCompilePlanner.Build(StandardSource(), targets, headMerge: true);

		Assert.Equal(
			without.Replacements.Select(replacement => (replacement.Source.FileId, replacement.Target.Key)),
			withKits.Replacements.Select(replacement => (replacement.Source.FileId, replacement.Target.Key)));
		Assert.Equal(without.Hidden.Select(target => target.Key), withKits.Hidden.Select(target => target.Key));
	}

	[Fact]
	public void SingleTorsoTrayFallsBackToTheHelmet()
	{
		// 躯干只剩一层：装不下「身体 + 头」，合并必须退回老做法（头由头盔套件承载）。
		var targets = StandardTargets()
			.Where(target => target.Cell.Slot != TorsoSlot)
			.Append(Target(0x9001, TorsoSlot, 0))
			.ToArray();

		var plan = SectionalCompilePlanner.Build(StandardSource(), targets, headMerge: true);

		Assert.Equal(SectionalHeadMergePlan.SkippedNoFreeTorsoLayer, plan.HeadMerge);
		Assert.Contains(plan.Replacements, replacement =>
			replacement.Source.Cell.Slot == HelmetSlot && replacement.Target.Cell.Slot == HelmetSlot);
	}

	[Fact]
	public void MissingHelmetKitIsAnInfoNotAnError()
	{
		var targets = StandardTargets().Where(target => target.Cell.Slot != HelmetSlot).ToArray();

		var plan = SectionalCompilePlanner.Build(StandardSource(), targets, headMerge: false);

		Assert.Equal(SectionalHeadMergePlan.NoHelmetKit, plan.HeadMerge);
		Assert.DoesNotContain(plan.Replacements, replacement => replacement.Source.Cell.Slot == HelmetSlot);
		Assert.NotEmpty(plan.Info);
		Assert.Empty(plan.Warnings);
	}

	[Fact]
	public void SecondSourceInTheSameSlotIsDroppedWithAWarning()
	{
		var source = StandardSource()
			.Append(new SectionalSourceUnit("aaaa0000000000ff", new ArmorCell(TorsoSlot, 1)))
			.ToArray();

		var plan = SectionalCompilePlanner.Build(source, StandardTargets(), headMerge: false);

		Assert.DoesNotContain(plan.Replacements, replacement => replacement.Source.FileId == "aaaa0000000000ff");
		Assert.Contains(plan.Warnings, warning => warning.Contains("只写出最里那一个", StringComparison.Ordinal));
	}

	[Fact]
	public void EveryTargetUnitIsEitherReplacedHiddenOrCape()
	{
		var targets = StandardTargets();
		foreach (var merge in new[] { false, true })
		{
			var plan = SectionalCompilePlanner.Build(StandardSource(), targets, merge);

			var replaced = plan.Replacements.Select(replacement => replacement.Target.Key).ToHashSet();
			var hidden = plan.Hidden.Select(target => target.Key).ToHashSet();
			Assert.Empty(replaced.Intersect(hidden));
			Assert.Equal(plan.Replacements.Count, replaced.Count);

			foreach (var target in targets)
			{
				var covered = replaced.Contains(target.Key)
					|| hidden.Contains(target.Key)
					|| target.Cell.IsCape;
				Assert.True(covered, $"目标 unit 0x{target.Key.FileId:x16}（slot {target.Cell.Slot}）没有被任何一桶接住。");
			}
		}
	}

	// ── 样本 ────────────────────────────────────────────────────────────────

	private const ulong UnitTypeId = 0xe0a48d0be9a7453fUL;

	private static SectionalTargetUnit Target(ulong fileId, int slot, int layer)
		=> new(new AssetKey(UnitTypeId, fileId), new ArmorCell(slot, layer));

	/// <summary>7 个来源 unit：头 + 躯干 + 髋 + 双腿 + 双臂，全在 0 层（自定义素体的形状）。</summary>
	private static SectionalSourceUnit[] StandardSource() =>
	[
		new("aaaa000000000001", new ArmorCell(HelmetSlot, 0)),
		new("aaaa000000000002", new ArmorCell(TorsoSlot, 0)),
		new("aaaa000000000003", new ArmorCell(HipsSlot, 0)),
		new("aaaa000000000004", new ArmorCell(LeftLegSlot, 0)),
		new("aaaa000000000005", new ArmorCell(RightLegSlot, 0)),
		new("aaaa000000000006", new ArmorCell(LeftArmSlot, 0)),
		new("aaaa000000000007", new ArmorCell(RightArmSlot, 0)),
	];

	/// <summary>一件形状与 RE-2310 同构的护甲 + 配套头盔（披风是另一件，不在目标 kit 里）。</summary>
	private static SectionalTargetUnit[] StandardTargets()
	{
		var targets = new List<SectionalTargetUnit>
		{
			Target(0xcafe, CapeSlot, 0),
			Target(0xa001, HelmetSlot, 0),
		};

		// 躯干三层，每层两个身形。
		var id = 0x1000UL;
		foreach (var layer in new[] { 0, 1, 2 })
		{
			targets.Add(Target(id++, TorsoSlot, layer));
			targets.Add(Target(id++, TorsoSlot, layer));
		}

		// 髋只有 1 层；双腿 0/1 层（Any）；双臂 0 层（Any）+ 1 层两个身形。
		targets.Add(Target(id++, HipsSlot, 1));
		targets.Add(Target(id++, HipsSlot, 1));
		targets.Add(Target(id++, LeftLegSlot, 0));
		targets.Add(Target(id++, LeftLegSlot, 1));
		targets.Add(Target(id++, RightLegSlot, 0));
		targets.Add(Target(id++, RightLegSlot, 1));
		targets.Add(Target(id++, LeftArmSlot, 0));
		targets.Add(Target(id++, LeftArmSlot, 1));
		targets.Add(Target(id++, LeftArmSlot, 1));
		targets.Add(Target(id++, RightArmSlot, 0));
		targets.Add(Target(id++, RightArmSlot, 1));
		targets.Add(Target(id++, RightArmSlot, 1));

		// 双肩各两个身形。
		targets.Add(Target(id++, LeftShoulderSlot, 0));
		targets.Add(Target(id++, LeftShoulderSlot, 0));
		targets.Add(Target(id++, RightShoulderSlot, 0));
		targets.Add(Target(id++, RightShoulderSlot, 0));
		return [.. targets];
	}
}
