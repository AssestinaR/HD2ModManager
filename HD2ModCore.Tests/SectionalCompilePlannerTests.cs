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
