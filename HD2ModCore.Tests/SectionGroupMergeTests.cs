using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 重转换之后的组表合并：这次转出来的组是权威，用户手建的组按新产物重算后保留，
/// 旧文件里的自动组一律丢掉。
/// <para>
/// 守的是一个实机事故（2026-11-10）：重构建之后界面还停在上一次的 21 个组，其中 9 个组的 unit
/// 早已不在产物里（点了没反应）。原因是换载荷时 `components.json` 被**故意保留**——用户能改组表，
/// 所以换载荷不敢覆盖它；但转换器也没把新组写回去，于是新的"按来源命名"永远到不了界面。
/// </para>
/// </summary>
public sealed class SectionGroupMergeTests
{
	private const string UnitA = "a0cc3d2318d9e237";
	private const string UnitB = "00a8b09bf2f697f7";
	private const string MissingUnit = "ffffffffffffffff";

	private static WardrobeSectionMapDocument Map()
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("/tmp/x.patch_0", "abc", 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			[
				Unit(UnitA, 3),
				Unit(UnitB, 2),
			]);

	private static WardrobeSectionMapUnit Unit(string fileId, int sections)
		=> new(fileId, "Torso", "Undergarment", "Any", "Medium", sections, true,
			[
				.. Enumerable.Range(0, sections).Select(index => new WardrobeSectionMapEntry(
					index, "aabbccddeeff0011", 10, $"aabbccddeeff0011-{index}", [])),
			]);

	private static SectionGroupDocument Fresh()
		=> new(SectionGroupDocument.CurrentSchema,
			[Group("尾巴", SectionGroupSource.DonorUnit, (UnitA, new[] { 0, 1 }))],
			[]);

	private static SectionGroupDocument Previous()
		=> new(SectionGroupDocument.CurrentSchema,
			[
				// 上一轮的自动组：这次没有对应来源，必须丢掉（否则界面上是一个点了没反应的复选框）。
				Group("2787157324_lod4", SectionGroupSource.DonorUnit, (UnitB, new[] { 0 })),
				// 用户手建的：保留，成员按这次产物重算（越界段、不存在的 unit 都要剪掉）。
				Group("我的组", SectionGroupSource.Manual, (UnitA, new[] { 0, 9 }), (MissingUnit, new[] { 0 })),
			],
			[]);

	private static SectionGroup Group(string name, SectionGroupSource source, params (string Unit, int[] Sections)[] members)
		=> new(name, name, [], [.. members.Select(member => new SectionGroupTarget(member.Unit, member.Sections))])
		{
			Source = source,
			// 自动组的稳定键由生成器给；手建组没有键。
			Key = source == SectionGroupSource.Manual ? null : "source:" + name,
		};

	[Fact]
	public void Merge_KeepsTheFreshGroupsAndPrunesManualOnes()
	{
		var merged = SectionGroupMerge.Merge(Fresh(), Previous(), Map());

		Assert.Equal(["尾巴", "我的组"], merged.Groups.Select(group => group.Name));
		var manual = merged.Groups[1];
		var member = Assert.Single(manual.Members);
		Assert.Equal(UnitA, member.Unit);
		// 9 越界、缺失的 unit 被剪掉，只留 0。
		Assert.Equal([0], member.Sections);
		Assert.Empty(merged.Unmatched);
	}

	[Fact]
	public void Merge_KeepsAUserRenameOfAnAutomaticGroup()
	{
		// 上一轮用户把「尾巴」改名成「尾巴（大）」，成员 unit 没变。
		var previous = new SectionGroupDocument(SectionGroupDocument.CurrentSchema,
			[Group("尾巴（大）", SectionGroupSource.DonorUnit, (UnitA, new[] { 0, 1 })) with { Key = "source:尾巴" }], []);

		var merged = SectionGroupMerge.Merge(Fresh(), previous, Map());

		Assert.Equal("尾巴（大）", Assert.Single(merged.Groups).Name);
	}

	[Fact]
	public void Merge_DoesNotBringBackAGroupTheUserDeleted()
	{
		var previous = SectionGroupDocument.Empty with
		{
			Hidden = [new SectionGroupTombstone("尾巴", "source:尾巴")],
		};

		var merged = SectionGroupMerge.Merge(Fresh(), previous, Map());

		Assert.Empty(merged.Groups);
		// 删除记录要带走：下一次重建还得抑制。
		Assert.Single(merged.Hidden);
	}

	[Fact]
	public void Merge_DropsOldAutomaticGroups()
	{
		var merged = SectionGroupMerge.Merge(Fresh(), Previous(), Map());

		Assert.DoesNotContain(merged.Groups, group => group.Name == "2787157324_lod4");
	}

	[Fact]
	public void Merge_ReportsAManualGroupThatNoLongerHasGeometry()
	{
		var previous = new SectionGroupDocument(SectionGroupDocument.CurrentSchema,
			[Group("全没了", SectionGroupSource.Manual, (MissingUnit, new[] { 0 }))], []);

		var merged = SectionGroupMerge.Merge(Fresh(), previous, Map());

		Assert.Equal(["尾巴"], merged.Groups.Select(group => group.Name));
		var unmatched = Assert.Single(merged.Unmatched);
		Assert.Equal("全没了", unmatched.Name);
		Assert.Contains("手建", unmatched.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Merge_KeepsFreshWithoutPrevious()
	{
		var merged = SectionGroupMerge.Merge(Fresh(), previous: null, Map());

		Assert.Single(merged.Groups);
		Assert.Empty(merged.Unmatched);
	}
}
