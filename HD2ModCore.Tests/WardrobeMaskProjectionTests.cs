using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 权威工作区 → <c>wardrobe.masks</c> 的投影测试。
///
/// 这里锁的是 §14.3 那张表：视觉来源自己的集合、映射目标指向同一个 set id（= 继承）、
/// 独立编辑的目标有自己的 set id（= 覆盖）。以及两条刻意的取舍：
/// 解析不到的条目只警告、set id 按 itemId 升序分配。
/// </summary>
public sealed class WardrobeMaskProjectionTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string UnitB = "cae35ee15168145f";
	private const string UnknownUnit = "00000000000000ff";
	private const uint Source = 820333399;
	private const uint SecondSource = 900000001;
	private const uint Target = 1745464610;
	private const uint SecondTarget = 530315896;

	private static WardrobeSectionIndex Index(params WardrobeSectionMapDocument[] documents)
		=> WardrobeSectionIndex.Create(documents.Length == 0
			? new[] { WardrobeSectionMapTests.CreateMap() }
			: documents);

	private static WardrobeWorkspaceDocument Workspace(params WardrobeWorkspaceArmour[] armours)
		=> new(WardrobeWorkspaceDocument.CurrentSchema, 7, armours);

	private static WardrobeWorkspaceArmour Own(uint itemId, params WardrobeWorkspaceUnit[] units)
		=> new(itemId, WardrobeMaskMode.Own, units);

	private static WardrobeWorkspaceUnit Off(string unit, params int[] indices)
		=> new(unit, indices);

	[Fact]
	public void Project_TargetsInheritBySharingTheSourceSetId()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnitA, 0))),
			new[] { new WardrobeMapping(Target, Source), new WardrobeMapping(SecondTarget, Source) },
			Index());
		Assert.True(result.IsValid);
		var set = Assert.Single(result.Sets);
		Assert.Equal(1u, set.SetId);
		Assert.Equal(WardrobeSetOrigin.Source, set.Origin);
		Assert.Equal(new[] { Target, SecondTarget }.Order().ToArray(), set.InheritorItemIds.ToArray());
		// 继承不是新机制：两个目标指向同一个 set id。
		Assert.Equal(
			"schema=2\nrevision=7\nenabled=true\n"
			+ "off=1," + UnitA + ",0,a47787804c45e1a9-8852\n"
			+ "target=" + SecondTarget + ",1\n"
			+ "target=" + Target + ",1\n",
			WardrobeMaskFile.Serialize(result.Document));
	}

	[Fact]
	public void Project_OwnModeTargetStopsInheriting()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(
				Own(Source, Off(UnitA, 0)),
				Own(Target, Off(UnitA, 1))),
			new[] { new WardrobeMapping(Target, Source), new WardrobeMapping(SecondTarget, Source) },
			Index());
		Assert.True(result.IsValid);
		Assert.Equal(2, result.Sets.Count);
		Assert.Equal(WardrobeSetOrigin.Own, result.Sets.Single(set => set.OwnerItemId == Target).Origin);
		// 独立设置的目标指向自己的集合，另一个目标仍然继承来源。
		Assert.Equal(2u, result.Document.TargetSets[Target]);
		Assert.Equal(1u, result.Document.TargetSets[SecondTarget]);
	}

	[Fact]
	public void Project_EmptyOwnSetIsAnExplicitException()
	{
		// 用户把某个目标的勾选全部取消：条目是空的，但"覆盖掉继承"这件事必须留下来。
		var result = WardrobeMaskProjection.Project(
			Workspace(
				Own(Source, Off(UnitA, 0)),
				Own(Target)),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		Assert.True(result.IsValid);
		Assert.Equal(2u, result.Document.TargetSets[Target]);
		Assert.DoesNotContain(result.Document.Entries, entry => entry.SetId == 2u);
		Assert.Contains("target=" + Target + ",2", WardrobeMaskFile.Serialize(result.Document), StringComparison.Ordinal);
	}

	[Fact]
	public void Project_SourceWithNothingDisabledEmitsNoTargetLine()
	{
		var result = WardrobeMaskProjection.Project(
			WardrobeWorkspaceDocument.Empty,
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		Assert.True(result.IsValid);
		Assert.Empty(result.Document.Entries);
		Assert.Empty(result.Document.TargetSets);
		Assert.Empty(result.Sets);
	}

	[Fact]
	public void Project_AllocatesSetIdsByItemIdAscending()
	{
		// 两个来源，映射顺序故意反过来：id 必须只由 itemId 决定，否则同一份输入会产出两份字节。
		var result = WardrobeMaskProjection.Project(
			Workspace(
				Own(Source, Off(UnitA, 0)),
				Own(SecondSource, Off(UnitB, 0))),
			new[] { new WardrobeMapping(Target, SecondSource), new WardrobeMapping(SecondTarget, Source) },
			Index());
		Assert.True(result.IsValid);
		Assert.Equal(Source, result.Sets.Single(set => set.SetId == 1).OwnerItemId);
		Assert.Equal(SecondSource, result.Sets.Single(set => set.SetId == 2).OwnerItemId);
	}

	[Fact]
	public void Project_KeepsWorkspaceRevision()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnitA, 0))),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		Assert.Equal(7, result.Document.Revision);
		Assert.True(result.Document.Enabled);
	}

	[Fact]
	public void Project_DisablingEverySectionIsReportedAsAFullyHiddenUnit()
	{
		// UnitB 只有一个 section；关掉它等于整块关闭，校验时应当看得出来。
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnitB, 0))),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		var validation = WardrobeMaskCompiler.Validate(result.Document, Index());
		Assert.True(validation.IsValid);
		var target = Assert.Single(validation.Targets);
		Assert.Equal(UnitB, Assert.Single(target.FullyHiddenUnits));
	}

	[Fact]
	public void Project_SkipsUnknownUnitWithAWarning()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnknownUnit, 0))),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		// 工作区是惰性的、与 mod 无关的：它可能引用当前没部署的 mod 的 unit。
		// 为此让整次编译失败，等于"换一次 mod 就再也编不出掩码"。
		Assert.True(result.IsValid);
		Assert.Equal(1, result.SkippedEntryCount);
		var warning = Assert.Single(result.Diagnostics);
		Assert.Equal("unit_not_in_any_map", warning.Code);
	}

	[Fact]
	public void Project_SkipsOutOfRangeSectionWithAWarning()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnitA, 9))),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		Assert.True(result.IsValid);
		Assert.Equal(1, result.SkippedEntryCount);
		var warning = Assert.Single(result.Diagnostics);
		Assert.Equal("section_not_in_unit", warning.Code);
	}

	[Fact]
	public void Project_RejectsATargetMappedToTwoSources()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off(UnitA, 0))),
			new[] { new WardrobeMapping(Target, Source), new WardrobeMapping(Target, SecondSource) },
			Index());
		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "target_conflict" && diagnostic.IsError);
	}

	[Fact]
	public void Project_RejectsNonHexUnit()
	{
		var result = WardrobeMaskProjection.Project(
			Workspace(Own(Source, Off("Helmet", 0))),
			new[] { new WardrobeMapping(Target, Source) },
			Index());
		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unit_invalid" && diagnostic.IsError);
	}

	// --- 合并多份映射表 -----------------------------------------------------

	[Fact]
	public void Index_MergesUnitsFromSeveralMaps()
	{
		var second = WardrobeSectionMapTests.CreateMap() with
		{
			Units = WardrobeSectionMapTests.CreateMap().Units
				.Where(unit => unit.FileId == UnitB)
				.ToArray(),
		};
		var first = WardrobeSectionMapTests.CreateMap() with
		{
			Units = WardrobeSectionMapTests.CreateMap().Units
				.Where(unit => unit.FileId == UnitA)
				.ToArray(),
		};
		var index = WardrobeSectionIndex.Create(new[] { first, second });
		Assert.Equal(2, index.UnitCount);
		Assert.NotNull(index.FindSection(UnitA, 1));
		Assert.NotNull(index.FindSection(UnitB, 0));
	}

	[Fact]
	public void Index_AcceptsTheSameUnitWhenBothMapsAgree()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var index = WardrobeSectionIndex.Create(new[] { map, map });
		Assert.Equal(2, index.UnitCount);
	}

	[Fact]
	public void Index_RejectsTheSameUnitWithDifferentContent()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var drifted = map with
		{
			Units = map.Units
				.Select(unit => unit.FileId == UnitA
					? unit with
					{
						Sections = unit.Sections
							.Select(section => section.Index == 0
								? section with { Fingerprint = "ffffffffffffffff-8852" }
								: section)
							.ToArray(),
					}
					: unit)
				.ToArray(),
		};
		// 两份表来自不同的构建：按哪一份都会关错几何，宁可直接拒绝。
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionIndex.Create(new[] { map, drifted }));
		Assert.Contains(UnitA, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ResolvePaths_FindsSectionMapsUnderADirectory()
	{
		var root = Directory.CreateTempSubdirectory("hd2mod-wardrobe-maps-").FullName;
		try
		{
			var map = WardrobeSectionMapTests.CreateMap();
			var first = Path.Combine(root, "mod-a", WardrobeSectionMapFile.FileName);
			var second = Path.Combine(root, "mod-b", "nested", WardrobeSectionMapFile.FileName);
			Directory.CreateDirectory(Path.GetDirectoryName(first)!);
			Directory.CreateDirectory(Path.GetDirectoryName(second)!);
			File.WriteAllText(first, WardrobeSectionMapFile.Serialize(map));
			File.WriteAllText(second, WardrobeSectionMapFile.Serialize(map));
			var paths = WardrobeSectionMapFile.ResolvePaths(root);
			Assert.Equal(2, paths.Count);
			// 单个文件也接受，这样 CLI 三个命令的 --sections 口径一致。
			Assert.Equal(first, Assert.Single(WardrobeSectionMapFile.ResolvePaths(first)));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
