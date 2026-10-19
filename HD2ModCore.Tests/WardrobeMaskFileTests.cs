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
			"schema=1",
			"revision=7",
			"enabled=true",
			"# 注释行必须被忽略",
			"off=" + SlimUnit + ",0," + FaceFingerprint,
			"off_target=1234," + SlimUnit + ",3," + RibbonFingerprint,
			"mask_mode=1234,override"));

		Assert.Equal(7, document.Revision);
		Assert.True(document.Enabled);
		Assert.Single(document.SourceMasks);
		Assert.Single(document.TargetMasks);
		Assert.Equal(WardrobeMaskMode.Override, document.TargetModes[1234]);
	}

	[Fact]
	public void Parse_RejectsUnknownKey()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true", "off_source=" + SlimUnit + ",0," + FaceFingerprint)));
		Assert.Contains("未知字段", exception.Message, StringComparison.Ordinal);
		Assert.Contains("第 4 行", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsDuplicateSourceEntry()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off=" + SlimUnit + ",0," + FaceFingerprint,
			"off=" + SlimUnit + ",0," + FaceFingerprint)));
		Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsMalformedFingerprint()
	{
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true", "off=" + SlimUnit + ",0,aa6b1bd624443008")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true", "off=" + SlimUnit + ",0,aa6b-9290-extra")));
	}

	[Fact]
	public void Parse_RejectsMissingRequiredKeys()
	{
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("revision=1", "enabled=true")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("schema=1", "enabled=true")));
		Assert.Throws<InvalidDataException>(() => WardrobeMaskFile.Parse(Document("schema=1", "revision=1")));
	}

	[Fact]
	public void Parse_NormalizesUnitPrefixAndCase()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off=0xCAE35EE15168145F,0,AA6B1BD624443008-9290"));
		Assert.Equal(SlimUnit, document.SourceMasks[0].Unit);
		Assert.Equal(FaceFingerprint, document.SourceMasks[0].Fingerprint);
	}

	[Fact]
	public void Parse_DropsExplicitInheritModeSoSerializationIsStable()
	{
		var text = Document(
			"schema=1", "revision=1", "enabled=true",
			"mask_mode=9,inherit",
			"off=" + SlimUnit + ",0," + FaceFingerprint);
		var document = WardrobeMaskFile.Parse(text);
		Assert.Empty(document.TargetModes);
		var first = WardrobeMaskFile.Serialize(document);
		var second = WardrobeMaskFile.Serialize(WardrobeMaskFile.Parse(first));
		Assert.Equal(first, second);
		Assert.DoesNotContain("inherit", first, StringComparison.Ordinal);
	}

	[Fact]
	public void Serialize_IsDeterministicForUnsortedInput()
	{
		var unsorted = new WardrobeMaskDocument(
			3,
			true,
			new[]
			{
				new WardrobeSourceMaskEntry("9841b16544c8fbae", 3, RibbonFingerprint),
				new WardrobeSourceMaskEntry(SlimUnit, 2, RibbonFingerprint),
				new WardrobeSourceMaskEntry(SlimUnit, 0, FaceFingerprint),
			},
			new[]
			{
				new WardrobeTargetMaskEntry(9, SlimUnit, 1, FaceFingerprint),
				new WardrobeTargetMaskEntry(7, SlimUnit, 1, FaceFingerprint),
			},
			new Dictionary<uint, WardrobeMaskMode> { [9] = WardrobeMaskMode.Override });

		var first = WardrobeMaskFile.Serialize(unsorted);
		var second = WardrobeMaskFile.Serialize(WardrobeMaskFile.Parse(first));
		Assert.Equal(first, second);
		var lines = first.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(
			new[]
			{
				"schema=1",
				"revision=3",
				"enabled=true",
				"off=9841b16544c8fbae,3," + RibbonFingerprint,
				"off=" + SlimUnit + ",0," + FaceFingerprint,
				"off=" + SlimUnit + ",2," + RibbonFingerprint,
				"off_target=7," + SlimUnit + ",1," + FaceFingerprint,
				"off_target=9," + SlimUnit + ",1," + FaceFingerprint,
				"mask_mode=9,override",
			},
			lines);
	}

	[Fact]
	public void View_InheritModeFollowsTheSourceMask()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off=" + SlimUnit + ",0," + FaceFingerprint));
		var view = WardrobeMaskView.Create(document);
		Assert.Equal(WardrobeMaskMode.Inherit, view.ModeOf(1234));
		Assert.True(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		// 指纹对不上就是"设置静默失效"，不能把别的 section 一起关掉。
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, RibbonFingerprint));
	}

	[Fact]
	public void View_OverrideModeDoesNotInheritTheSourceMask()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off=" + SlimUnit + ",0," + FaceFingerprint,
			"mask_mode=1234,override"));
		var view = WardrobeMaskView.Create(document);
		// override 是"不继承"，不是"叠加"：来源关了也不影响这个目标。
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		Assert.True(view.IsSourceDisabled(SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_OverrideTargetOnlySeesItsOwnEntries()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off_target=1234," + SlimUnit + ",3," + RibbonFingerprint,
			"mask_mode=1234,override"));
		var view = WardrobeMaskView.Create(document);
		Assert.True(view.IsDisabled(1234, SlimUnit, 3, RibbonFingerprint));
		Assert.False(view.IsDisabled(9999, SlimUnit, 3, RibbonFingerprint));
	}

	[Fact]
	public void View_DisabledDocumentKeepsContentButHidesNothing()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=false",
			"off=" + SlimUnit + ",0," + FaceFingerprint));
		var view = WardrobeMaskView.Create(document);
		Assert.Single(document.SourceMasks);
		Assert.False(view.IsDisabled(1234, SlimUnit, 0, FaceFingerprint));
		Assert.False(view.IsSourceDisabled(SlimUnit, 0, FaceFingerprint));
	}

	[Fact]
	public void View_UnitFullyDisabledRequiresEverySection()
	{
		var document = WardrobeMaskFile.Parse(Document(
			"schema=1", "revision=1", "enabled=true",
			"off=" + SlimUnit + ",0," + FaceFingerprint,
			"off=" + SlimUnit + ",1," + RibbonFingerprint));
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
				"schema=1", "revision=5", "enabled=true",
				"off=" + SlimUnit + ",0," + FaceFingerprint));
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
