using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

/// <summary>
/// 拆件规划：<b>把"一个 Mod 里的一堆 unit"规划成"7 个标准槽 × 身形"</b>。
/// <para>
/// 守三件事：① <b>绝不跨身形合并</b>（游戏按件记录的 body 挑 unit，Slim 与 Stocky 并进同一个
/// Any unit 会两套同时出现）；② <b>非标准槽并进 Torso</b>，且要说出来并了哪些；
/// ③ <b>结果确定</b>（同一份输入两次跑出同一个计划，否则预演不可信）。
/// </para>
/// </summary>
public sealed class SectionalSplitPlannerTests
{
	private static SectionalSplitSource Unit(string id, string slot, int sections, string body = "Any", string name = "")
		=> new(id, slot, body, sections, name);

	[Fact]
	public void Plan_AlreadySevenSlots_ProducesOneTargetPerSlotAndNoVariants()
	{
		var units = SectionalSplitPlanner.StandardSlots
			.Select((slot, index) => Unit($"00000000000000{index:x2}", slot, 3))
			.ToArray();

		var plan = SectionalSplitPlanner.Plan(units);

		Assert.Equal(7, plan.Targets.Count);
		Assert.All(plan.Targets, target => Assert.Empty(target.DonorUnitIds));
		// 每个槽只有基础组，没有变体组。
		Assert.Equal(7, plan.Groups.Count);
		Assert.All(plan.Groups, group => Assert.True(group.EnabledByDefault));
		Assert.Empty(plan.Warnings);
	}

	/// <summary>
	/// 左右肩不是 7 标准槽之一，但归属明确：左肩→左臂、右肩→右臂。
	/// 并进躯干（以前的行为）会让肩甲跟着躯干被关，而游戏里它是跟手臂一起摆的。
	/// </summary>
	[Fact]
	public void Plan_ShouldersAreMergedIntoTheirOwnArms()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("aaaa000000000001", "Torso", 4),
			Unit("aaaa000000000002", "LeftShoulder", 2),
			Unit("aaaa000000000003", "RightShoulder", 2),
		]);

		Assert.Equal(3, plan.Targets.Count);
		var torso = Assert.Single(plan.Targets, target => target.Slot == "Torso");
		Assert.Empty(torso.DonorUnitIds);
		// 肩部单位成为该臂槽的**宿主**（它的槽位归一后就是这个槽），不是捐体。
		var leftArm = Assert.Single(plan.Targets, target => target.Slot == "LeftArm");
		Assert.Equal("aaaa000000000002", leftArm.HostUnitId);
		var rightArm = Assert.Single(plan.Targets, target => target.Slot == "RightArm");
		Assert.Equal("aaaa000000000003", rightArm.HostUnitId);
		Assert.DoesNotContain(plan.Targets, target =>
			target.Slot == SectionalSplitPlanner.FallbackSlot && target.DonorUnitIds.Count != 0);
	}

	[Fact]
	public void Plan_NeverMergesAcrossBodyTypes()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("bbbb000000000001", "Torso", 4, "Slim"),
			Unit("bbbb000000000002", "Torso", 4, "Stocky"),
		]);

		Assert.Equal(2, plan.Targets.Count);
		Assert.All(plan.Targets, target => Assert.Empty(target.DonorUnitIds));
		Assert.Equal(["Slim", "Stocky"], plan.Targets.Select(target => target.BodyType).OrderBy(body => body, StringComparer.Ordinal));
	}

	[Fact]
	public void Plan_MixedAnyAndConcreteBody_WarnsAboutAmbiguity()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("cccc000000000001", "Torso", 4),
			Unit("cccc000000000002", "Torso", 4, "Slim"),
		]);

		Assert.Equal(2, plan.Targets.Count);
		Assert.Contains(plan.Warnings, warning => warning.Contains("Any", StringComparison.Ordinal));
	}

	[Fact]
	public void Plan_PicksTheSlotOwnerAsHostEvenWhenADonorIsBigger()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("dddd000000000001", "Torso", 1),
			// 用一个**没有明确归属**的非标准槽当捐体（Cape → 兜底到 Torso）。
			// 左右肩现在有自己的归属（→ 左右臂），不再参与"并进躯干"这条路径。
			Unit("dddd000000000002", "Cape", 9),
		]);

		var target = Assert.Single(plan.Targets);
		// 自带该槽位的那个是宿主，哪怕它的节数更少——槽位归属比节数更能说明"谁是这个槽的主体"。
		Assert.Equal("dddd000000000001", target.HostUnitId);
		Assert.Equal(["dddd000000000002"], target.DonorUnitIds);
	}

	[Fact]
	public void Plan_DonorGroupsShareOneExclusiveGroupAndStartDisabled()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("eeee000000000001", "Torso", 4, name: "主体"),
			Unit("eeee000000000002", "Torso", 2, name: "外套"),
			Unit("eeee000000000003", "Torso", 2, name: "披风"),
		]);

		var target = Assert.Single(plan.Targets);
		Assert.Equal(2, target.DonorUnitIds.Count);

		var groups = plan.Groups;
		Assert.Equal(3, groups.Count);
		// 同一个互斥组：选一个变体就等于把基础件关掉——这就是"变体替换"的全部实现。
		Assert.Single(groups.Select(group => group.ExclusiveGroup).Distinct());
		var baseGroup = Assert.Single(groups, group => group.EnabledByDefault);
		Assert.Equal(["eeee000000000001"], baseGroup.MemberUnitIds);
		Assert.All(groups.Where(group => !group.EnabledByDefault), group => Assert.Single(group.MemberUnitIds));
		Assert.Contains(groups, group => group.Name == "外套");
	}

	[Fact]
	public void Plan_DropsUnitsWithoutGeometryAndSaysWhy()
	{
		var plan = SectionalSplitPlanner.Plan([
			Unit("ffff000000000001", "Torso", 4),
			Unit("ffff000000000002", "Torso", 0),
			Unit(string.Empty, "Torso", 2),
		]);

		Assert.Single(plan.Targets);
		Assert.Equal(2, plan.Dropped.Count);
		Assert.All(plan.Dropped, dropped => Assert.False(string.IsNullOrWhiteSpace(dropped.Reason)));
	}

	[Fact]
	public void Plan_IsDeterministic()
	{
		SectionalSplitSource[] units = [
			Unit("1111000000000003", "Torso", 2, name: "C"),
			Unit("1111000000000001", "Torso", 4, name: "A"),
			Unit("1111000000000002", "Torso", 4, name: "B"),
		];

		var first = SectionalSplitPlanner.Plan(units);
		var second = SectionalSplitPlanner.Plan([.. units.Reverse()]);

		Assert.Equal(first.Targets.Select(target => target.HostUnitId), second.Targets.Select(target => target.HostUnitId));
		Assert.Equal(first.Targets.SelectMany(target => target.DonorUnitIds), second.Targets.SelectMany(target => target.DonorUnitIds));
		Assert.Equal(first.Groups.Select(group => group.Name), second.Groups.Select(group => group.Name));
	}

	[Fact]
	public void Plan_EmptyInputExplainsItself()
	{
		var plan = SectionalSplitPlanner.Plan([]);

		Assert.Empty(plan.Targets);
		Assert.Single(plan.Warnings);
	}

	[Fact]
	public void MapSlot_IsCaseInsensitiveAndFallsBackToTorso()
	{
		Assert.Equal("Torso", SectionalSplitPlanner.MapSlot("torso"));
		Assert.Equal("LeftArm", SectionalSplitPlanner.MapSlot(" LEFTARM "));
		// 左右肩有明确归属：并进各自的臂，不落兜底。
		Assert.Equal("LeftArm", SectionalSplitPlanner.MapSlot("LeftShoulder"));
		Assert.Equal("RightArm", SectionalSplitPlanner.MapSlot(" rightshoulder "));
		Assert.Equal("Torso", SectionalSplitPlanner.MapSlot(null));
		Assert.Equal("Torso", SectionalSplitPlanner.MapSlot("Cape"));
	}
}
