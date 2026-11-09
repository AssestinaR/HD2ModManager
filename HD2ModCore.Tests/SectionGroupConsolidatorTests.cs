using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 「把 section 组对齐到新几何」这条收口。
/// <para>
/// 组的成员是 <c>(unit, section 下标)</c>，而 <c>sections.json</c> 每次重建都变。2026-11-09
/// 实测 Lilium 的 12 组里 7 组已经指错节（「手套Glove」记 <c>0006:2</c>，实际在 <c>0006:10</c>）——
/// 这不是"组丢了"，是**组还在、指着别人**，点开关关掉的是别的部件。
/// </para>
/// <para>
/// 修法的判据只有一个：不信下标，只信**源材质裸名**。这组用例把三条边界钉住 ——
/// 能归位、能保留手建组、找不到材质时宁可丢掉也不乱指。
/// </para>
/// </summary>
public sealed class SectionGroupConsolidatorTests
{
	private const string UnitA = "18054b9f5a540001";
	private const string UnitB = "18054b9f5a540002";

	[Fact]
	public void 索引漂移时按材质名归位且id不变()
	{
		// 旧文件：猫耳在 0001 的第 3 节。
		var previous = Document(Group("猫耳", ["c_white_a_ear"], (UnitA, [3])));
		// 新几何：同一个材质跑到第 5 节去了（重建后边界变了）。
		var sections = Sections((UnitA, new[] { ("plum_body", 0), ("plum_face", 1), ("c_white_a_ear", 5) }));

		var result = SectionGroupConsolidator.Consolidate(fresh: null, previous, sections);

		var group = Assert.Single(result.Document.Groups);
		Assert.Equal([5], group.Members[0].Sections);
		Assert.Equal(1, result.Refreshed);
		// **id 不能变**：界面的选中态、互斥关系、wardrobe 预设都按 id 记。
		Assert.False(string.IsNullOrEmpty(group.Id));
		Assert.Equal(previous.Groups[0].EffectiveId, group.Id);
	}

	[Fact]
	public void 同一个材质落在两个unit上时两个都算()
	{
		var previous = Document(Group("鞋Shoes", ["sister_shoes"], (UnitA, [2])));
		var sections = Sections(
			(UnitA, new[] { ("sister_shoes", 4) }),
			(UnitB, new[] { ("sister_shoes", 4), ("plum_body", 0) }));

		var result = SectionGroupConsolidator.Consolidate(null, previous, sections);

		var group = Assert.Single(result.Document.Groups);
		Assert.Equal(2, group.Members.Count);
		Assert.Contains(group.Members, member => member.Unit == UnitA && member.Sections.SequenceEqual(new[] { 4 }));
		Assert.Contains(group.Members, member => member.Unit == UnitB && member.Sections.SequenceEqual(new[] { 4 }));
	}

	[Fact]
	public void 材质在新几何里消失时丢掉该组并说明原因()
	{
		var previous = Document(Group("已删除的部件", ["gone_material"], (UnitA, [1])));
		var sections = Sections((UnitA, new[] { ("plum_body", 0) }));

		var result = SectionGroupConsolidator.Consolidate(null, previous, sections);

		Assert.Empty(result.Document.Groups);
		Assert.Equal(1, result.Dropped);
		var unmatched = Assert.Single(result.Document.Unmatched);
		Assert.Equal("已删除的部件", unmatched.Name);
	}

	[Fact]
	public void 手建组会被保留并重算()
	{
		var manual = Group("我的手建组", ["plum_body"], (UnitA, [9])) with
		{
			Source = SectionGroupSource.Manual,
			Id = "gmanual000001",
			ExclusiveGroup = "上半身",
		};
		var previous = Document(manual);
		var sections = Sections((UnitA, new[] { ("plum_body", 0) }));

		var result = SectionGroupConsolidator.Consolidate(null, previous, sections);

		var group = Assert.Single(result.Document.Groups);
		Assert.Equal("gmanual000001", group.Id);
		Assert.Equal("上半身", group.ExclusiveGroup);
		Assert.Equal(SectionGroupSource.Manual, group.Source);
		Assert.Equal([0], group.Members[0].Sections);
		Assert.Equal(1, result.KeptManual);
	}

	[Fact]
	public void 这次转出来的组是权威旧的自动组不重复出现()
	{
		var previous = Document(
			Group("猫耳", ["c_white_a_ear"], (UnitA, [3])),
			Group("猫耳（旧名字）", ["c_white_a_ear"], (UnitA, [3])));
		var fresh = Document(Group("猫耳", ["c_white_a_ear"], (UnitA, [5])));
		var sections = Sections((UnitA, new[] { ("c_white_a_ear", 5) }));

		var result = SectionGroupConsolidator.Consolidate(fresh, previous, sections);

		// 只留这次转出来的那一条；旧文件里同名不同名的两条都不该再冒出来。
		var group = Assert.Single(result.Document.Groups);
		Assert.Equal("猫耳", group.Name);
		Assert.Equal([5], group.Members[0].Sections);
	}

	[Fact]
	public void 旧文件里没有id时把当时算出来的那个固化下来()
	{
		var previous = Document(Group("猫耳", ["c_white_a_ear"], (UnitA, [3])));
		Assert.Empty(previous.Groups[0].Id);
		var expected = previous.Groups[0].EffectiveId;
		var sections = Sections((UnitA, new[] { ("c_white_a_ear", 7) }));

		var result = SectionGroupConsolidator.Consolidate(null, previous, sections);

		// 成员变了、id 仍是"旧掩码"算出来的那个：否则用户存下的"这个开关是关着的"会失配。
		Assert.Equal(expected, result.Document.Groups[0].Id);
		Assert.Equal([7], result.Document.Groups[0].Members[0].Sections);
	}

	[Fact]
	public void 没有旧组也没有新组时什么都不产出()
	{
		var sections = Sections((UnitA, new[] { ("plum_body", 0) }));

		var result = SectionGroupConsolidator.Consolidate(null, null, sections);

		Assert.Empty(result.Document.Groups);
		Assert.Empty(result.Document.Unmatched);
		Assert.Equal(0, result.Refreshed);
	}

	/// <summary>
	/// 落盘这一步要能在**只读**文件上工作，并且事后把只读补回去。
	/// <para>
	/// 库里的 Mod 文件被管理器标成只读（换包时 <c>SetDirectoryReadOnly(true)</c>），
	/// 而重构建写组用的是"临时文件 + 改名替换"：目标只读时那一步会抛
	/// 「Access to the path … is denied」—— 那句话一个字都没提"只读"，
	/// 第一眼必然怀疑权限或占用。所以这条用例钉住"清属性 → 写 → 补属性"这套动作。
	/// </para>
	/// </summary>
	[Fact]
	public async Task 只读的组文件也能被重写且只读状态保留()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2-group-ro-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, SectionGroupFile.FileName);
		try
		{
			var previous = Document(Group("猫耳", ["c_white_a_ear"], (UnitA, [3])));
			await SectionGroupFile.WriteAsync(directory, previous);
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

			var result = SectionGroupConsolidator.Consolidate(
				null, previous, Sections((UnitA, new[] { ("c_white_a_ear", 6) })));

			var restore = VpkPayloadSwap.ClearReadOnlyForWrite(path);
			try
			{
				await SectionGroupFile.WriteAsync(directory, result.Document);
			}
			finally
			{
				if (restore) VpkPayloadSwap.MarkReadOnly(path);
			}

			var read = await SectionGroupFile.TryReadAsync(directory);
			Assert.NotNull(read);
			Assert.Equal([6], read!.Groups[0].Members[0].Sections);
			Assert.True(new FileInfo(path).IsReadOnly);
		}
		finally
		{
			if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
			try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
		}
	}

	// ---- 构造 ----

	private static SectionGroup Group(string name, string[] materials, params (string Unit, int[] Sections)[] members)
		=> new(name, name + ".vpk", materials,
			[.. members.Select(member => new SectionGroupTarget(member.Unit, member.Sections))]);

	private static SectionGroupDocument Document(params SectionGroup[] groups)
		=> new(SectionGroupDocument.CurrentSchema, groups, []);

	private static WardrobeSectionMapDocument Sections(params (string Unit, (string Material, int Index)[] Sections)[] units)
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("patch_0", "sha", 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			[.. units.Select(unit => new WardrobeSectionMapUnit(
				unit.Unit, "Helmet", "Undergarment", "Any", "Medium",
				unit.Sections.Length, true,
				[.. unit.Sections.Select((section, index) => new WardrobeSectionMapEntry(
					section.Index, "mat" + index, 100u, "fp" + index, [])
				{
					SourceMaterial = section.Material,
				})]))]);
}
