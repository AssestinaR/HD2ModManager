using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>wardrobe.workspace.json</c>（<c>hd2mod.wardrobe/1</c>）的契约测试。
///
/// 这是**权威数据**：丢了等于丢用户的勾选。所以这里锁的重点是
/// "坏数据必须拒绝，不能读成半份设置"，以及"继承与独立两种模式互不串味"。
/// </summary>
public sealed class WardrobeWorkspaceTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string UnitB = "cae35ee15168145f";
	private const uint Source = 820333399;
	private const uint Target = 1745464610;

	private static WardrobeWorkspaceDocument CreateDocument() => new(
		WardrobeWorkspaceDocument.CurrentSchema,
		4,
		new[]
		{
			new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
			{
				new WardrobeWorkspaceUnit(UnitB, new[] { 2 }),
				new WardrobeWorkspaceUnit(UnitA, new[] { 1, 0 }),
			}),
			new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Own, new[]
			{
				new WardrobeWorkspaceUnit(UnitA, Array.Empty<int>()),
			}),
		});

	[Fact]
	public void Empty_TreatsEveryArmourAsInheriting()
	{
		var document = WardrobeWorkspaceDocument.Empty;
		Assert.True(document.IsEmpty);
		Assert.Null(document.FindArmour(Source));
		Assert.False(document.IsDisabled(Source, UnitA, 0));
		Assert.Equal(0, document.DisabledCount(Source));
	}

	[Fact]
	public void Serialize_ThenParse_IsStable()
	{
		var once = WardrobeWorkspaceFile.Serialize(CreateDocument());
		var again = WardrobeWorkspaceFile.Serialize(WardrobeWorkspaceFile.Parse(once));
		Assert.Equal(once, again, StringComparer.Ordinal);
	}

	[Fact]
	public void Serialize_WritesModeAsAName()
	{
		// 枚举写成名字而不是数字：这份文件会被手改，"mode": 1 只会让人去数枚举。
		var text = WardrobeWorkspaceFile.Serialize(CreateDocument());
		Assert.Contains("\"mode\": \"own\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\"mode\": 1", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Normalize_SortsArmoursUnitsAndIndices()
	{
		var normalized = WardrobeWorkspaceFile.Normalize(CreateDocument());
		Assert.Equal(new[] { Source, Target }, normalized.Armours.Select(armour => armour.ItemId).ToArray());
		var units = normalized.Armours[0].Units;
		Assert.Equal(UnitA, units[0].Unit);
		Assert.Equal(UnitB, units[1].Unit);
		Assert.Equal(new[] { 0, 1 }, units[0].Off.ToArray());
	}

	[Fact]
	public void IsDisabled_OnlyAppliesToOwnMode()
	{
		var own = CreateDocument();
		Assert.True(own.IsDisabled(Source, UnitA, 0));
		Assert.True(own.IsDisabled(Source, UnitA, 1));
		Assert.False(own.IsDisabled(Source, UnitA, 2));
		// 目标用的是它自己那份（空集）：不继承来源，所以什么都不关。
		Assert.False(own.IsDisabled(Target, UnitA, 0));
		// 来源自己：UnitA 关了 0 和 1，UnitB 关了 2，合计 3 块。
		Assert.Equal(3, own.DisabledCount(Source));
		Assert.Equal(0, own.DisabledCount(Target));
	}

	[Fact]
	public void InheritArmour_NeverDisablesAnything()
	{
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[] { new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Inherit, Array.Empty<WardrobeWorkspaceUnit>()) });
		Assert.False(document.IsDisabled(Target, UnitA, 0));
		Assert.Empty(WardrobeWorkspaceFile.Validate(document));
	}

	[Fact]
	public void Parse_RejectsUnknownSchema()
	{
		var text = WardrobeWorkspaceFile.Serialize(CreateDocument())
			.Replace("hd2mod.wardrobe/1", "hd2mod.wardrobe/2", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeWorkspaceFile.Parse(text));
		Assert.Contains("schema", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsInheritModeThatCarriesEntries()
	{
		// 继承的含义就是"没有自己的数据"；两者并存时编辑器显示一份、编译用另一份。
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[]
			{
				new WardrobeWorkspaceArmour(Target, WardrobeMaskMode.Inherit, new[]
				{
					new WardrobeWorkspaceUnit(UnitA, new[] { 0 }),
				}),
			});
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("armour_mode_contradiction", error.Code);
	}

	[Fact]
	public void Parse_RejectsDuplicateArmour()
	{
		var document = CreateDocument() with
		{
			Armours = new[]
			{
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, Array.Empty<WardrobeWorkspaceUnit>()),
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, Array.Empty<WardrobeWorkspaceUnit>()),
			},
		};
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("armour_duplicate", error.Code);
	}

	[Fact]
	public void Parse_RejectsDuplicateUnit()
	{
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[]
			{
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
				{
					new WardrobeWorkspaceUnit(UnitA, new[] { 0 }),
					new WardrobeWorkspaceUnit("0x" + UnitA.ToUpperInvariant(), new[] { 1 }),
				}),
			});
		// 大小写与 0x 前缀不同的同一个 unit 必须算重复，否则会变成两个键、部分设置失效。
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("unit_duplicate", error.Code);
	}

	[Fact]
	public void Parse_RejectsDuplicateSectionIndex()
	{
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[]
			{
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
				{
					new WardrobeWorkspaceUnit(UnitA, new[] { 3, 3 }),
				}),
			});
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("section_index_duplicate", error.Code);
	}

	[Fact]
	public void Parse_RejectsSectionIndexOutOfRange()
	{
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[]
			{
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
				{
					new WardrobeWorkspaceUnit(UnitA, new[] { 65536 }),
				}),
			});
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("section_index_out_of_range", error.Code);
	}

	[Fact]
	public void Parse_RejectsNonHexUnit()
	{
		var document = new WardrobeWorkspaceDocument(
			WardrobeWorkspaceDocument.CurrentSchema,
			1,
			new[]
			{
				new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
				{
					new WardrobeWorkspaceUnit("Helmet", new[] { 0 }),
				}),
			});
		var error = Assert.Single(WardrobeWorkspaceFile.Validate(document));
		Assert.Equal("unit_invalid", error.Code);
	}

	[Fact]
	public void Parse_RejectsMalformedJson()
	{
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeWorkspaceFile.Parse("{ nope"));
		Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsUnknownMembers()
	{
		// "offs" 这类手误默认会被静默忽略，然后整份设置变成"什么都没关"。
		var text = """
		{
		  "schema": "hd2mod.wardrobe/1",
		  "revision": 1,
		  "armours": [ { "itemId": 42, "mode": "own", "units": [ { "unit": "af431352d9b95e83", "offs": [0] } ] } ]
		}
		""";
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeWorkspaceFile.Parse(text));
		Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RejectsUnknownModeName()
	{
		var text = """
		{
		  "schema": "hd2mod.wardrobe/1",
		  "revision": 1,
		  "armours": [ { "itemId": 42, "mode": "replace", "units": [] } ]
		}
		""";
		Assert.Throws<InvalidDataException>(() => WardrobeWorkspaceFile.Parse(text));
	}

	[Fact]
	public void Read_MissingFileIsEmpty()
	{
		var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), WardrobeWorkspaceFile.FileName);
		Assert.Same(WardrobeWorkspaceDocument.Empty, WardrobeWorkspaceFile.Read(path));
	}

	[Fact]
	public void Write_ThenRead_RoundTrips()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-ws-").FullName;
		try
		{
			var path = WardrobeWorkspaceFile.ResolvePath(directory);
			WardrobeWorkspaceFile.Write(path, CreateDocument());
			var read = WardrobeWorkspaceFile.Read(path);
			Assert.Equal(4, read.Revision);
			Assert.Equal(2, read.Armours.Count);
			Assert.True(read.IsDisabled(Source, UnitA, 1));
			Assert.Contains(".hd2modmanager", path, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Write_RefusesInvalidDocument()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-ws-").FullName;
		try
		{
			var path = Path.Combine(directory, WardrobeWorkspaceFile.FileName);
			var invalid = CreateDocument() with
			{
				Armours = new[]
				{
					new WardrobeWorkspaceArmour(Source, WardrobeMaskMode.Own, new[]
					{
						new WardrobeWorkspaceUnit(UnitA, new[] { -1 }),
					}),
				},
			};
			Assert.Throws<InvalidDataException>(() => WardrobeWorkspaceFile.Write(path, invalid));
			Assert.False(File.Exists(path));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void NextRevision_BumpsAndWraps()
	{
		Assert.Equal(5, WardrobeWorkspaceFile.NextRevision(CreateDocument()).Revision);
		var atMax = CreateDocument() with { Revision = int.MaxValue };
		Assert.Equal(0, WardrobeWorkspaceFile.NextRevision(atMax).Revision);
	}
}
