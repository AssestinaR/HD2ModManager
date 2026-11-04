using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>components.json</c>（<c>hd2mod.vpk-components/1</c>）的格式契约。
/// <para>
/// 重点不是"能不能读"，而是三件事：<b>中文名字不被转义</b>（这份文件是给人 grep 的）、
/// <b>schema 变了要显式失效</b>（而不是被误读成"零个开关"）、
/// <b>坏文件只影响开关区</b>（读不懂返回 <c>null</c>，不该让详情页打不开）。
/// </para>
/// </summary>
public sealed class SectionGroupFileTests : IDisposable
{
	private const string UnitA = "18054b9f5a540001";
	private const string UnitB = "18054b9f5a540002";

	private readonly string directory = Path.Combine(
		Path.GetTempPath(), "hd2-switch-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	internal static SectionGroupDocument CreateDocument() => new(
		SectionGroupDocument.CurrentSchema,
		[
			new SectionGroup(
				"猫耳",
				"Manuka_JABIS猫耳关.vpk",
				["c_white_a_ear"],
				[new SectionGroupTarget(UnitA, [3])]),
			new SectionGroup(
				"外套",
				"Manuka_JABIS外套关.vpk",
				["_2_navy", "_03_coat"],
				[
					new SectionGroupTarget(UnitA, [5, 6]),
					new SectionGroupTarget(UnitB, [1]),
				]),
		],
		[new SectionGroupUnmatched("文身", "Manuka_JABIS文身关.vpk", "它换的是贴图，不是可见性。")]);

	[Fact]
	public void Serialize_KeepsChineseNamesReadable()
	{
		var text = SectionGroupFile.Serialize(CreateDocument());
		// 不转义：这份文件要能直接读、直接 grep。
		Assert.Contains("\"猫耳\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u732B", text, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void RoundTrip_KeepsEveryField()
	{
		var parsed = SectionGroupFile.Parse(SectionGroupFile.Serialize(CreateDocument()));

		Assert.Equal(SectionGroupDocument.CurrentSchema, parsed.Schema);
		Assert.Equal(2, parsed.Switches.Count);
		var ears = parsed.Switches[0];
		Assert.Equal("猫耳", ears.Name);
		Assert.Equal(["c_white_a_ear"], ears.Materials);
		Assert.Equal(1, ears.SectionCount);

		// 跨 unit 的开关必须原样保留两个目标（一个开关关两处几何）。
		var coat = parsed.Switches[1];
		Assert.Equal(2, coat.Disabled.Count);
		Assert.Equal(new[] { 5, 6 }, coat.Disabled[0].Sections);
		Assert.Equal(UnitB, coat.Disabled[1].Unit);
		Assert.Equal(3, coat.SectionCount);

		Assert.Single(parsed.Unmatched);
		Assert.Equal("文身", parsed.Unmatched[0].Name);
	}

	[Fact]
	public void Parse_RejectsAnUnknownSchemaInsteadOfReturningZeroSwitches()
	{
		var text = SectionGroupFile.Serialize(CreateDocument())
			.Replace(SectionGroupDocument.CurrentSchema, "hd2mod.vpk-components/0", StringComparison.Ordinal);
		var exception = Assert.Throws<InvalidDataException>(() => SectionGroupFile.Parse(text));
		Assert.Contains("schema", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TryReadAsync_ReturnsNullForAMissingOrBrokenFile()
	{
		Assert.Null(await SectionGroupFile.TryReadAsync(directory));

		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(Path.Combine(directory, SectionGroupFile.FileName), "{ not json");
		Assert.Null(await SectionGroupFile.TryReadAsync(directory));
	}

	[Fact]
	public async Task WriteThenRead_IsStableAndAtomic()
	{
		var document = CreateDocument();
		await SectionGroupFile.WriteAsync(directory, document);
		var read = await SectionGroupFile.TryReadAsync(directory);

		Assert.NotNull(read);
		Assert.Equal(document.Switches.Count, read!.Switches.Count);
		// 原子写不该留下临时文件。
		Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
	}
}
