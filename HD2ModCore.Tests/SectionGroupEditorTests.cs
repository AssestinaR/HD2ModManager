using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 组的结构性编辑：新建 / 改名 / 删除 / 设互斥。
/// <para>
/// 守三件事：**改名不动 id**（否则界面选中态与互斥关系错位）、**成员归一**（否则同一个 unit
/// 出现两条记录会重复计数并算出不同 id）、**删组不动几何**（只删定义，成员回到默认可见）。
/// </para>
/// </summary>
public sealed class SectionGroupEditorTests
{
	private const string UnitA = "18054b9f5a540001";
	private const string UnitB = "18054b9f5a540002";

	private static SectionGroupDocument Empty()
		=> new(SectionGroupDocument.CurrentSchema, [], []);

	[Fact]
	public void Create_NormalizesMembersAndAssignsAnId()
	{
		var result = SectionGroupEditor.Create(
			Empty(),
			"  猫耳  ",
			[
				new SectionGroupTarget(UnitA, [3, 1]),
				new SectionGroupTarget(UnitA, [3, 2]),   // 同 unit 再来一条，必须合并
				new SectionGroupTarget("0x" + UnitB.ToUpperInvariant(), [0]),  // 带前缀大写，必须归一
				new SectionGroupTarget(UnitA, []),       // 空节，必须丢
				new SectionGroupTarget("", [7]),         // 空 unit，必须丢
			]);

		Assert.NotNull(result);
		var (document, group) = result!.Value;
		Assert.Equal("猫耳", group.Name);
		Assert.Equal(SectionGroupSource.Manual, group.Source);
		Assert.Equal(2, group.Members.Count);
		Assert.Equal(new[] { 1, 2, 3 }, group.Members[0].Sections);
		Assert.Equal(UnitB, group.Members[1].Unit);
		Assert.Equal(4, group.SectionCount);
		Assert.StartsWith(SectionGroupIdentity.Prefix, group.Id, StringComparison.Ordinal);
		Assert.Single(document.Groups);
	}

	[Fact]
	public void Create_ReturnsNullWhenNothingUsableRemains()
	{
		Assert.Null(SectionGroupEditor.Create(Empty(), "空组", [new SectionGroupTarget(UnitA, [])]));
		Assert.Null(SectionGroupEditor.Create(Empty(), "空组", []));
	}

	[Fact]
	public void Create_GivesDistinctIdsForIdenticalInput()
	{
		var first = SectionGroupEditor.Create(Empty(), "同名", [new SectionGroupTarget(UnitA, [1])]);
		Assert.NotNull(first);
		var second = SectionGroupEditor.Create(first!.Value.Document, "同名", [new SectionGroupTarget(UnitA, [1])]);

		Assert.NotNull(second);
		Assert.NotEqual(first.Value.Group.Id, second!.Value.Group.Id);
		Assert.Equal(2, second.Value.Document.Groups.Count);
	}

	[Fact]
	public void Rename_KeepsTheIdAndTrimsTheName()
	{
		var created = SectionGroupEditor.Create(Empty(), "猫耳", [new SectionGroupTarget(UnitA, [1])]);
		Assert.NotNull(created);
		var group = created!.Value.Group;

		var renamed = SectionGroupEditor.Rename(created.Value.Document, group.Id, "  耳朵（改）  ");

		Assert.NotNull(renamed);
		var updated = Assert.Single(renamed!.Groups);
		Assert.Equal("耳朵（改）", updated.Name);
		Assert.Equal(group.Id, updated.Id);
	}

	[Fact]
	public void Rename_RefusesEmptyNamesAndUnknownIds()
	{
		var created = SectionGroupEditor.Create(Empty(), "猫耳", [new SectionGroupTarget(UnitA, [1])]);
		Assert.NotNull(created);
		var document = created!.Value.Document;

		Assert.Null(SectionGroupEditor.Rename(document, created.Value.Group.Id, "   "));
		Assert.Null(SectionGroupEditor.Rename(document, "g-nope", "名字"));
	}

	[Fact]
	public void Rename_StampsTheIdForALegacyGroupThatHadNone()
	{
		// 旧格式读进来的组 Id 是空的：改一次名之后必须落一个真 id，
		// 否则"改名后 id 跟着变"这条会回来。
		var legacy = new SectionGroup("旧名字", "", [], [new SectionGroupTarget(UnitA, [1])]);
		var document = new SectionGroupDocument(SectionGroupDocument.CurrentSchema, [legacy], []);
		Assert.Empty(legacy.Id);

		var renamed = SectionGroupEditor.Rename(document, legacy.EffectiveId, "新名字");

		Assert.NotNull(renamed);
		var updated = Assert.Single(renamed!.Groups);
		Assert.False(string.IsNullOrEmpty(updated.Id));
		Assert.Equal(legacy.EffectiveId, updated.Id);
	}

	[Fact]
	public void Remove_DropsOnlyTheDefinition()
	{
		var first = SectionGroupEditor.Create(Empty(), "猫耳", [new SectionGroupTarget(UnitA, [1])])!.Value;
		var second = SectionGroupEditor.Create(first.Document, "外套", [new SectionGroupTarget(UnitA, [2])])!.Value;

		var removed = SectionGroupEditor.Remove(second.Document, first.Group.Id);

		Assert.NotNull(removed);
		var remaining = Assert.Single(removed!.Groups);
		Assert.Equal("外套", remaining.Name);
		// 成员仍然记着它覆盖哪几块——"删组"不该顺手把几何信息也抹掉。
		Assert.Equal([2], remaining.Members[0].Sections);
	}

	[Fact]
	public void SetExclusiveGroup_NormalizesBlankToNull()
	{
		var created = SectionGroupEditor.Create(Empty(), "发色 白", [new SectionGroupTarget(UnitA, [1])])!.Value;

		var withGroup = SectionGroupEditor.SetExclusiveGroup(created.Document, created.Group.Id, "  发色  ");
		Assert.Equal("发色", Assert.Single(withGroup!.Groups).ExclusiveGroup);

		var cleared = SectionGroupEditor.SetExclusiveGroup(withGroup!, created.Group.Id, "   ");
		Assert.Null(Assert.Single(cleared!.Groups).ExclusiveGroup);
	}

	[Fact]
	public void Normalize_IsOrderInsensitive()
	{
		var forward = SectionGroupEditor.Normalize([new SectionGroupTarget(UnitA, [1]), new SectionGroupTarget(UnitB, [2])]);
		var reversed = SectionGroupEditor.Normalize([new SectionGroupTarget(UnitB, [2]), new SectionGroupTarget(UnitA, [1])]);

		// 逐项比：SectionGroupTarget 是 record，但它装的 IReadOnlyList 用的是引用相等，
		// 所以直接 Assert.Equal 两个列表会在内容相同时也失败。
		Assert.Equal(forward.Count, reversed.Count);
		for (var index = 0; index < forward.Count; index++)
		{
			Assert.Equal(forward[index].Unit, reversed[index].Unit);
			Assert.Equal(forward[index].Sections, reversed[index].Sections);
		}
	}
}
