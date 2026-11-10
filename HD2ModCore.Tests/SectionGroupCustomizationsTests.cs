using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 用户对组表做过的改动（改名 / 互斥 / 删除）必须活过重建与重转换。
/// <para>
/// 守的三件事都是实机反馈：改名后被冲回默认名、删掉的组又回来、给两个变体设的互斥丢空。
/// 匹配不能用段下标（每次重建都会漂），所以用"名字或成员 unit 集合"两个键。
/// </para>
/// </summary>
public sealed class SectionGroupCustomizationsTests
{
	private const string UnitA = "a0cc3d2318d9e237";
	private const string UnitB = "00a8b09bf2f697f7";

	private static SectionGroup Group(
		string name,
		params (string Unit, int[] Sections)[] members)
		=> new(name, name, [], [.. members.Select(member => new SectionGroupTarget(member.Unit, member.Sections))])
		{
			Source = SectionGroupSource.DonorUnit,
		};

	[Fact]
	public void Apply_KeepsAUserNameEvenWhenTheMembersMoved()
	{
		// 用户把「尾巴」改名成「尾巴（大）」；重建之后段下标变了（0,1 → 1,2），unit 没变。
		var fresh = new[] { Group("尾巴", (UnitA, [1, 2])) };
		var previous = new[] { Group("尾巴（大）", (UnitA, [0, 1])) };

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		var single = Assert.Single(result.Groups);
		Assert.Equal("尾巴（大）", single.Name);
		// 成员是产物的属性，必须以这一次为准。
		Assert.Equal([1, 2], Assert.Single(single.Members).Sections);
	}

	[Fact]
	public void Apply_KeepsTheUsersExclusiveGroup()
	{
		var fresh = new[] { Group("大衣 健壮", (UnitA, [0])) };
		var previous = new[]
		{
			Group("大衣 健壮", (UnitA, [0])) with { ExclusiveGroup = "大衣" },
		};

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		Assert.Equal("大衣", Assert.Single(result.Groups).ExclusiveGroup);
	}

	[Fact]
	public void Apply_SuppressesGroupsTheUserDeleted()
	{
		// 删过「翅膀」（记的是当时的名字 + 成员 unit）。重建后它又会生成——必须被抑制。
		var fresh = new[] { Group("翅膀", (UnitB, [0])), Group("本体", (UnitA, [0])) };
		var hidden = new[] { new SectionGroupTombstone("翅膀", [new SectionGroupTarget(UnitB, [0])]) };

		var result = SectionGroupCustomizations.Apply(fresh, previous: null, hidden);

		Assert.Equal(["本体"], result.Groups.Select(group => group.Name));
		Assert.Equal(1, result.Suppressed);
	}

	[Fact]
	public void Apply_SuppressesByMemberUnitsEvenWhenTheUserRenamedBeforeDeleting()
	{
		// 先改名再删除：删除记录里是改名后的名字，但 unit 集合还是那一份 ⇒ 仍然要抑制。
		var fresh = new[] { Group("翅膀", (UnitB, [0])) };
		var hidden = new[] { new SectionGroupTombstone("翅膀（大）", [new SectionGroupTarget(UnitB, [0])]) };

		var result = SectionGroupCustomizations.Apply(fresh, previous: null, hidden);

		Assert.Empty(result.Groups);
	}

	[Fact]
	public void Apply_LeavesUnrelatedGroupsAlone()
	{
		var fresh = new[] { Group("本体", (UnitA, [0])), Group("尾巴", (UnitB, [0])) };
		var previous = new[] { Group("本体", (UnitA, [0])) };

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		Assert.Equal(["本体", "尾巴"], result.Groups.Select(group => group.Name));
	}

	[Fact]
	public void WithHidden_ReplacesTheSameNameAndWithoutHiddenBringsItBack()
	{
		var document = SectionGroupDocument.Empty;
		var group = Group("翅膀", (UnitB, [0]));

		var hidden = SectionGroupCustomizations.WithHidden(document, group);
		Assert.Single(hidden.Hidden);

		var again = SectionGroupCustomizations.WithHidden(hidden, group);
		Assert.Single(again.Hidden);

		var restored = SectionGroupCustomizations.WithoutHidden(again, "翅膀");
		Assert.Empty(restored.Hidden);
	}
}
