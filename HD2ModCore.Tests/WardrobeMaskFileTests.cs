using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>wardrobe.masks</c> 的格式契约测试。规格：
/// <c>HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md</c>。
/// 这些用例锁的是"文件语义"，不是实现细节——Lua 侧按同一份规格实现，两边必须一致。
/// </summary>
public sealed class WardrobeMaskFileTests
{
	private const string SlimUnit = "cae35ee15168145f";
	private const string StockyUnit = "9841b16544c8fbae";
	private const string FaceFingerprint = "aa6b1bd624443008-9290";
	private const string RibbonFingerprint = "aadf05da56ef08cb-822";

	private static string Document(params string[] lines)
		=> string.Join('\n', lines) + "\n";

	[Fact]
	public void Parse_MissingFile_IsEquivalentToAllEnabled()
	{
		var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), WardrobeMaskFile.FileName);
		var document = WardrobeMaskFile.Read(path);
		Assert.Same(WardrobeMaskDocument.Empty, document);
		Assert.True(document.Enabled);
		var view = WardrobeMaskView.Create(document);
		Assert.False(view.IsDisabled(42, SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void Parse_AcceptsTheDocumentedGrammar()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2",
			"revision=7",
			"enabled=true",
			"# 注释行必须被忽略",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"off=1," + StockyUnit + ",0," + FaceFingerprint,
			"target=1234,1"));

		Assert.Equal(7, document.Revision);
		Assert.True(document.Enabled);
		Assert.Equal(2, document.Entries.Count);
		Assert.Equal(1u, document.TargetSets[1234]);
	}

	[Fact]
	public void Parse_RejectsUnknownKey()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true", "off_target=1," + SlimUnit + ",0," + FaceFingerprint)));
		Assert.Contains("未知字段", exception.Message, StringComparison.Ordinal);
		Assert.Contains("第 4 行", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsDuplicateEntry()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"off=1," + SlimUnit + ",0," + FaceFingerprint)));
		Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsDuplicateTargetSet()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"target=9,1", "target=9,2")));
		Assert.Contains("掩码集重复", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsMalformedFingerprint()
	{
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true", "off=1," + SlimUnit + ",0,aa6b1bd624443008")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true", "off=1," + SlimUnit + ",0,aa6b-9290-extra")));
	}

	[Fact]
	public void Parse_RejectsMissingRequiredKeys()
	{
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("revision=1", "enabled=true")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("schema=2", "enabled=true")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("schema=2", "revision=1")));
		// schema=1 是上一版格式（含 inherit/override），必须明确拒绝而不是半解析。
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true")));
	}

	[Fact]
	public void Parse_NormalizesUnitPrefixAndCase()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1,0xCAE35EE15168145F,0,AA6B1BD624443008-9290"));
		Assert.Equal(SlimUnit, document.Entries[0].Unit);
		Assert.Equal(FaceFingerprint, document.Entries[0].Fingerprint);
	}

	[Fact]
	public void Serialize_IsDeterministicForUnsortedInput()
	{
		var unsorted = new WardrobeMaskDocument(
			3,
			true,
			new[]
			{
				new WardrobeMaskEntry(2, StockyUnit, 3, RibbonFingerprint),
				new WardrobeMaskEntry(1, SlimUnit, 2, RibbonFingerprint),
				new WardrobeMaskEntry(1, SlimUnit, 0, FaceFingerprint),
			},
			new Dictionary<uint, uint> { [9] = 2, [7] = 1 });

		var first = WardrobeMaskFile.Serialize(unsorted);
		var second = WardrobeMaskFile.Serialize(WardrobeMaskFile.Parse(first));
		Assert.Equal(first, second);
		Assert.Equal(
			new[]
			{
				"schema=2",
				"revision=3",
				"enabled=true",
				"off=1," + SlimUnit + ",0," + FaceFingerprint,
				"off=1," + SlimUnit + ",2," + RibbonFingerprint,
				"off=2," + StockyUnit + ",3," + RibbonFingerprint,
				"target=7,1",
				"target=9,2",
			},
			first.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public void View_TargetWithoutSetHidesNothing()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint));
		var view = WardrobeMaskView.Create(document);
		Assert.Null(view.SetOf(1234));
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		// 掩码集本身仍然存在，只是没有目标指向它。
		Assert.True(view.IsDisabledInSet(1, SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_EmptySetHidesNothing()
	{
		// "声明自己是例外、但一条都不关"必须和"没有声明"区分开：
		// 前者在源文件里是一份显式数据，编译后落成"目标指向一个空集"。
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"target=1234,2"));
		var view = WardrobeMaskView.Create(document);
		Assert.Equal(2u, view.SetOf(1234));
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_TwoTargetsCanFollowDifferentSources()
	{
		// 多个视觉来源：目标 7 跟来源 A 的掩码集，目标 9 跟来源 B 的。
		// 这正是"全局只有一份来源掩码"表达不了的情形。
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"off=2," + SlimUnit + ",3," + RibbonFingerprint,
			"target=7,1",
			"target=9,2"));
		var view = WardrobeMaskView.Create(document);
		Assert.True(view.IsDisabled(7, SlimUnit, 0, FaceFingerprint));
		Assert.False(view.IsDisabled(7, SlimUnit, 3, RibbonFingerprint));
		Assert.True(view.IsDisabled(9, SlimUnit, 3, RibbonFingerprint));
		Assert.False(view.IsDisabled(9, SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_FingerprintMismatchLeavesSectionVisible()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"target=1234,1"));
		var view = WardrobeMaskView.Create(document);
		Assert.True(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		// 指纹对不上就是"设置静默失效"，不能把别的 section 一起关掉。
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, RibbonFingerprint));
		Assert.False(view.IsDisabled(1234, SlimUnit, 1, FaceFingerprint));
	}

	[Fact]
	public void View_DisabledDocumentKeepsContentButHidesNothing()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=false",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"target=1234,1"));
		var view = WardrobeMaskView.Create(document);
		Assert.Single(document.Entries);
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		Assert.False(view.IsDisabledInSet(1, SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_UnitFullyDisabledRequiresEverySection()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=2", "revision=1", "enabled=true",
			"off=1," + SlimUnit + ",0," + FaceFingerprint,
			"off=1," + SlimUnit + ",1," + RibbonFingerprint,
			"target=1234,1"));
		var view = WardrobeMaskView.Create(document);
		var all = new[]
		{
			new WardrobeSectionIdentity(SlimUnit, 0, FaceFingerprint),
			new WardrobeSectionIdentity(SlimUnit, 1, RibbonFingerprint),
		};
		Assert.True(view.IsUnitFullyDisabled(1234, SlimUnit, all));
		// 只关了一个 section 时不能判成整件禁用：那会让整个 unit 不注册，其余几何一起消失。
		var partial = new[]
		{
			new WardrobeSectionIdentity(SlimUnit, 0, FaceFingerprint),
			new WardrobeSectionIdentity(SlimUnit, 2, "a3545af6a33991cc-27525"),
		};
		Assert.False(view.IsUnitFullyDisabled(1234, SlimUnit, partial));
		// 指纹不符等于没关，同样不能判成整件禁用。
		var mismatched = new[] { new WardrobeSectionIdentity(SlimUnit, 0, RibbonFingerprint) };
		Assert.False(view.IsUnitFullyDisabled(1234, SlimUnit, mismatched));
		// 空列表不能算"全关"：没有 section 不等于整件都要藏。
		Assert.False(view.IsUnitFullyDisabled(1234, SlimUnit, Array.Empty<WardrobeSectionIdentity>()));
	}

	[Fact]
	public void Fingerprint_UsesIntegerDivisionForTriangles()
	{
		Assert.Equal(9290u, WardrobeSectionFingerprint.TrianglesFromIndexCount(27870));
		Assert.Equal(0u, WardrobeSectionFingerprint.TrianglesFromIndexCount(2));
		Assert.Equal("aa6b1bd624443008-9290", WardrobeSectionFingerprint.Format(0xaa6b1bd624443008UL, 9290));
		Assert.Equal("none-822", WardrobeSectionFingerprint.FormatWithoutMaterial(822));
	}

	[Fact]
	public void Write_IsAtomicAndReadsBackIdentically()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2mod-wardrobe-" + Guid.NewGuid().ToString("N"));
		try
		{
			var path = Path.Combine(directory, WardrobeMaskFile.FileName);
			var document = WardrobeMaskFile.Parse(Document(
				"schema=2", "revision=5", "enabled=true",
				"off=1," + SlimUnit + ",0," + FaceFingerprint,
				"target=1234,1"));
			WardrobeMaskFile.Write(path, document);
			Assert.True(File.Exists(path));
			// 临时文件不能留在目录里：它会被下次扫描当成垃圾，也让目录变脏。
			Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
			var readBack = WardrobeMaskFile.Read(path);
			Assert.Equal(WardrobeMaskFile.Serialize(document), WardrobeMaskFile.Serialize(readBack));
			Assert.Equal(5, readBack.Revision);
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Parse_RejectsOversizedText()
	{
		var filler = new string('#', WardrobeMaskFile.MaximumBytes + 1);
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(filler));
	}
}
