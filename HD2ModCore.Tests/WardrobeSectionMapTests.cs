using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>sections.json</c>（<c>hd2mod.section-map/2</c>）的格式契约测试。
/// 重点不在"能不能读"，而在**布局常量不符时必须拒绝**——那三个字段是运行时硬编码的假设，
/// 放过去就会写到别的字段上，而结果"看起来仍然像一份正常的表"。
/// </summary>
public sealed class WardrobeSectionMapTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string UnitB = "cae35ee15168145f";

	internal static WardrobeSectionMapDocument CreateMap() => new(
		WardrobeSectionMapDocument.CurrentSchema,
		new WardrobeSectionMapPatch(@"C:\patch\9ba626afa44a3aa3.patch_0", new string('a', 64), 592887, new string('b', 64)),
		WardrobeSectionMapDocument.ExpectedSectionRecordSize,
		WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
		WardrobeSectionMapDocument.TocRelativeOffsets,
		new[]
		{
			new WardrobeSectionMapUnit(UnitA, "Head", "Full", "Slim", "Light", 2, true, new[]
			{
				new WardrobeSectionMapEntry(0, "a47787804c45e1a9", 8852, "a47787804c45e1a9-8852", new[]
				{
					new WardrobeSectionToggle(0, 0, 0, "0x00002010", 26556),
					new WardrobeSectionToggle(1, 1, 0, "0x00002800", 26556),
				}),
				new WardrobeSectionMapEntry(1, "a648fd251c740b12", 1024, "a648fd251c740b12-1024", new[]
				{
					new WardrobeSectionToggle(0, 0, 1, "0x00002020", 3072),
					new WardrobeSectionToggle(1, 1, 1, "0x00002810", 3072),
				}),
			}),
			new WardrobeSectionMapUnit(UnitB, "Chest", "Full", "Slim", "Light", 1, true, new[]
			{
				new WardrobeSectionMapEntry(0, "aadf05da56ef08cb", 822, "aadf05da56ef08cb-822", new[]
				{
					new WardrobeSectionToggle(0, 0, 0, "0x00003010", 2466),
				}),
			}),
		});

	[Fact]
	public void Serialize_ThenParse_ThenSerialize_IsStable()
	{
		var once = WardrobeSectionMapFile.Serialize(CreateMap());
		var again = WardrobeSectionMapFile.Serialize(WardrobeSectionMapFile.Parse(once));
		Assert.Equal(once, again, StringComparer.Ordinal);
	}

	[Fact]
	public void Parse_KeepsEveryField()
	{
		var parsed = WardrobeSectionMapFile.Parse(WardrobeSectionMapFile.Serialize(CreateMap()));
		Assert.Equal(WardrobeSectionMapDocument.CurrentSchema, parsed.Schema);
		Assert.Equal(new string('a', 64), parsed.Patch.Sha256);
		Assert.Equal(592887, parsed.Patch.Size);
		Assert.Equal(2, parsed.Units.Count);
		var unit = parsed.FindUnit(UnitA);
		Assert.NotNull(unit);
		Assert.Equal("Head", unit.Slot);
		Assert.Equal(2, unit.Sections.Count);
		var section = parsed.FindSection(UnitA, 1);
		Assert.NotNull(section);
		Assert.Equal("a648fd251c740b12-1024", section.Fingerprint);
		Assert.Equal(2, section.Toggles.Count);
		Assert.Equal("0x00002810", section.Toggles[1].NumIndicesOffset);
	}

	[Fact]
	public void Parse_RejectsUnknownSchema()
	{
		var text = WardrobeSectionMapFile.Serialize(CreateMap())
			.Replace("hd2mod.section-map/2", "hd2mod.section-map/1", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse(text));
		Assert.Contains("schema", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsDifferentSectionRecordSize()
	{
		var text = WardrobeSectionMapFile.Serialize(CreateMap())
			.Replace("\"sectionRecordSize\": 24", "\"sectionRecordSize\": 20", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse(text));
		Assert.Contains("sectionRecordSize", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsDifferentNumIndicesOffset()
	{
		var text = WardrobeSectionMapFile.Serialize(CreateMap())
			.Replace("\"numIndicesOffsetInSection\": 16", "\"numIndicesOffsetInSection\": 12", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse(text));
		Assert.Contains("numIndicesOffsetInSection", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsForeignOffsetDialect()
	{
		var text = WardrobeSectionMapFile.Serialize(CreateMap())
			.Replace("\"offsets\": \"tocRelative\"", "\"offsets\": \"fileAbsolute\"", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse(text));
		Assert.Contains("offsets", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsMissingPatchFingerprint()
	{
		var text = WardrobeSectionMapFile.Serialize(CreateMap())
			.Replace(new string('a', 64), string.Empty, StringComparison.Ordinal);
		Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse(text));
	}

	[Fact]
	public void Parse_RejectsMalformedJson()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeSectionMapFile.Parse("{ not json"));
		Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void FindUnit_AcceptsPrefixAndUppercase()
	{
		var map = CreateMap();
		Assert.NotNull(map.FindUnit("0x" + UnitA));
		Assert.NotNull(map.FindUnit(UnitA.ToUpperInvariant()));
		Assert.Null(map.FindUnit("0000000000000000"));
	}

	[Fact]
	public void FindSection_OutOfRangeReturnsNull()
	{
		var map = CreateMap();
		Assert.Null(map.FindSection(UnitA, 2));
		Assert.Null(map.FindSection(UnitA, -1));
		Assert.Null(map.FindSection("0000000000000000", 0));
	}

	[Fact]
	public void TotalSectionCount_AddsUp()
	{
		Assert.Equal(3, CreateMap().TotalSectionCount);
	}
}
