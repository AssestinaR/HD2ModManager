using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 用户对组表做过的改动（改名 / 互斥 / 删除）必须活过重建与重转换。
/// <para>
/// 匹配**只能靠生成器给的稳定键**（<see cref="SectionGroup.Key"/>），不能靠名字（用户会改）
/// 也不能靠成员（段下标每次都漂；unit 集合太粗——同一个 unit 里可能同时有"本体""尾巴""鞋子"，
/// 按 unit 匹配会把名字互相串掉，2026-11-10 实机就是这样：`大衣 健壮` 被叫成 `Torso (Stocky)`）。
/// </para>
/// </summary>
public sealed class SectionGroupCustomizationsTests
{
	private const string UnitA = "a0cc3d2318d9e237";
	private const string UnitB = "00a8b09bf2f697f7";

	private static SectionGroup Group(
		string name,
		string? key,
		params (string Unit, int[] Sections)[] members)
		=> new(name, name, [], [.. members.Select(member => new SectionGroupTarget(member.Unit, member.Sections))])
		{
			Source = SectionGroupSource.DonorUnit,
			Key = key,
		};

	[Fact]
	public void Apply_KeepsAUserNameEvenWhenTheMembersMoved()
	{
		// 用户把「尾巴」改名成「尾巴（大）」；重建之后段下标变了（0,1 → 1,2），键没变。
		var fresh = new[] { Group("尾巴", "source:尾巴", (UnitA, [1, 2])) };
		var previous = new[] { Group("尾巴（大）", "source:尾巴", (UnitA, [0, 1])) };

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		var single = Assert.Single(result.Groups);
		Assert.Equal("尾巴（大）", single.Name);
		// 成员是产物的属性，必须以这一次为准。
		Assert.Equal([1, 2], Assert.Single(single.Members).Sections);
	}

	/// <summary>
	/// 这一条是实机事故的回归：同一个 unit 里有好几个组时，**绝不能按 unit 集合认亲**。
	/// 否则「大衣 健壮」会被上一轮的 `Torso（Stocky）` 认走，名字就成了槽位名。
	/// </summary>
	[Fact]
	public void Apply_DoesNotAdoptANameJustBecauseTheUnitIsTheSame()
	{
		var fresh = new[] { Group("大衣 健壮", "source:大衣 健壮", (UnitA, [3])) };
		var previous = new[] { Group("Torso（Stocky）", key: null, (UnitA, [0, 1, 2, 3])) };

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		Assert.Equal("大衣 健壮", Assert.Single(result.Groups).Name);
	}

	[Fact]
	public void Apply_KeepsTheUsersExclusiveGroup()
	{
		var fresh = new[] { Group("大衣 健壮", "source:大衣 健壮", (UnitA, [0])) };
		var previous = new[]
		{
			Group("大衣 健壮", "source:大衣 健壮", (UnitA, [0])) with { ExclusiveGroup = "大衣" },
		};

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		Assert.Equal("大衣", Assert.Single(result.Groups).ExclusiveGroup);
	}

	[Fact]
	public void Apply_SuppressesGroupsTheUserDeleted()
	{
		// 删过「翅膀」（记的是键）。重建后它又会生成——必须被抑制。
		var fresh = new[] { Group("翅膀", "source:翅膀", (UnitB, [0])), Group("本体", "source:本体", (UnitA, [0])) };
		var hidden = new[] { new SectionGroupTombstone("翅膀", "source:翅膀") };

		var result = SectionGroupCustomizations.Apply(fresh, previous: null, hidden);

		Assert.Equal(["本体"], result.Groups.Select(group => group.Name));
		Assert.Equal(1, result.Suppressed);
	}

	[Fact]
	public void Apply_SuppressesByKeyEvenWhenTheUserRenamedBeforeDeleting()
	{
		// 先改名再删除：删除记录里的名字是改名后的，键还是那个 ⇒ 仍然要抑制。
		var fresh = new[] { Group("翅膀", "source:翅膀", (UnitB, [0])) };
		var hidden = new[] { new SectionGroupTombstone("翅膀（大）", "source:翅膀") };

		var result = SectionGroupCustomizations.Apply(fresh, previous: null, hidden);

		Assert.Empty(result.Groups);
	}

	[Fact]
	public void Apply_LeavesUnrelatedGroupsAlone()
	{
		var fresh = new[] { Group("本体", "source:本体", (UnitA, [0])), Group("尾巴", "source:尾巴", (UnitB, [0])) };
		var previous = new[] { Group("本体", "source:本体", (UnitA, [0])) };

		var result = SectionGroupCustomizations.Apply(fresh, previous, hidden: null);

		Assert.Equal(["本体", "尾巴"], result.Groups.Select(group => group.Name));
	}

	[Fact]
	public void WithHidden_ReplacesTheSameNameAndWithoutHiddenBringsItBack()
	{
		var document = SectionGroupDocument.Empty;
		var group = Group("翅膀", "source:翅膀", (UnitB, [0]));

		var hidden = SectionGroupCustomizations.WithHidden(document, group);
		Assert.Equal("source:翅膀", Assert.Single(hidden.Hidden).Key);

		var again = SectionGroupCustomizations.WithHidden(hidden, group);
		Assert.Single(again.Hidden);

		var restored = SectionGroupCustomizations.WithoutHidden(again, "翅膀");
		Assert.Empty(restored.Hidden);
	}
}
