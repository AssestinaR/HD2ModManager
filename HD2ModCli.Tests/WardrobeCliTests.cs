using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// `hd2mod wardrobe sections|compile|validate` 的命令层契约测试。
///
/// 命令层只负责三件事：选项收口、把失败翻译成退出码、失败时不落盘。
/// 语义本身在 <c>HD2ModCore</c> 里测（WardrobeSectionMapTests / WardrobeMaskCompilerTests），
/// 这里刻意只碰"CLI 行为"，不重复校验规则。
/// </summary>
[Collection("cli-console")]
public sealed class WardrobeCliTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string FingerprintA0 = "a47787804c45e1a9-8852";
	private const string FingerprintA1 = "a648fd251c740b12-1024";
	private const uint Target = 1745464610;

	private static string WriteSections(string directory)
	{
		var map = new WardrobeSectionMapDocument(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch(Path.Combine(directory, "a.patch_0"), new string('a', 64), 4096, new string('b', 64)),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			new[]
			{
				new WardrobeSectionMapUnit(UnitA, "Helmet", "Full", "Slim", "Light", 2, true, new[]
				{
					new WardrobeSectionMapEntry(0, "a47787804c45e1a9", 8852, FingerprintA0, new[]
					{
						new WardrobeSectionToggle(0, 0, 0, "0x00002010", 26556),
					}),
					new WardrobeSectionMapEntry(1, "a648fd251c740b12", 1024, FingerprintA1, new[]
					{
						new WardrobeSectionToggle(0, 0, 1, "0x00002020", 3072),
					}),
				}),
			});
		var path = Path.Combine(directory, WardrobeSectionMapFile.FileName);
		File.WriteAllText(path, WardrobeSectionMapFile.Serialize(map));
		return path;
	}

	private static string WriteSpec(string directory, string sectionsFileName, int sectionIndex = 0)
	{
		var spec = $$"""
		{
		  "schema": "hd2mod.wardrobe-spec/1",
		  "revision": 5,
		  "enabled": true,
		  "sections": "{{sectionsFileName}}",
		  "sets": [
		    { "id": 1, "off": [ { "unit": "{{UnitA}}", "section": {{sectionIndex}} } ] }
		  ],
		  "targets": [ { "itemId": {{Target}}, "set": 1 } ]
		}
		""";
		var path = Path.Combine(directory, "wardrobe-spec.json");
		File.WriteAllText(path, spec);
		return path;
	}

	[Fact]
	public async Task Compile_ThenValidate_ProducesAndAcceptsTheMask()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-").FullName;
		try
		{
			var sectionsPath = WriteSections(directory);
			var specPath = WriteSpec(directory, WardrobeSectionMapFile.FileName);
			var maskPath = Path.Combine(directory, WardrobeMaskFile.FileName);

			var compile = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "compile", specPath, "--output", maskPath, "--json");
			Assert.Equal(0, compile.ExitCode);
			Assert.True(File.Exists(maskPath));
			Assert.Equal(
				"schema=2\nrevision=5\nenabled=true\n"
				+ "off=1," + UnitA + ",0," + FingerprintA0 + "\n"
				+ "target=" + Target + ",1\n",
				await File.ReadAllTextAsync(maskPath));

			var validate = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "validate", "--file", maskPath, "--sections", sectionsPath, "--json");
			Assert.Equal(0, validate.ExitCode);
			using var json = validate.ParseJson();
			var validation = json.RootElement.GetProperty("validation");
			Assert.True(validation.GetProperty("isValid").GetBoolean());
			Assert.Equal(1, validation.GetProperty("entryCount").GetInt32());
			Assert.Equal(1, validation.GetProperty("resolvedEntryCount").GetInt32());
			var target = Assert.Single(validation.GetProperty("targets").EnumerateArray().ToArray());
			Assert.Equal(Target, target.GetProperty("itemId").GetUInt32());
			Assert.Equal(1, target.GetProperty("hiddenSections").GetInt32());
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Compile_DoesNotWriteWhenTheSpecReferencesAMissingSection()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-").FullName;
		try
		{
			WriteSections(directory);
			var specPath = WriteSpec(directory, WardrobeSectionMapFile.FileName, sectionIndex: 9);
			var maskPath = Path.Combine(directory, WardrobeMaskFile.FileName);

			var compile = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "compile", specPath, "--output", maskPath, "--json");
			Assert.Equal(2, compile.ExitCode);
			// 半份掩码会让运行时关错几何，所以失败必须一个字节都不写。
			Assert.False(File.Exists(maskPath));
			using var json = compile.ParseJson();
			Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
			var diagnostics = json.RootElement.GetProperty("compiled").GetProperty("diagnostics").EnumerateArray().ToArray();
			Assert.Contains(diagnostics, item => item.GetProperty("code").GetString() == "section_not_in_unit");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Compile_RequiresOverwriteForAnExistingOutput()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-").FullName;
		try
		{
			WriteSections(directory);
			var specPath = WriteSpec(directory, WardrobeSectionMapFile.FileName);
			var maskPath = Path.Combine(directory, WardrobeMaskFile.FileName);

			Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
				"wardrobe", "compile", specPath, "--output", maskPath, "--json")).ExitCode);
			var second = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "compile", specPath, "--output", maskPath, "--json");
			Assert.Equal(2, second.ExitCode);
			Assert.Contains("OutputAlreadyExists", second.StandardOutput, StringComparison.Ordinal);
			var overwrite = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "compile", specPath, "--output", maskPath, "--overwrite", "--json");
			Assert.Equal(0, overwrite.ExitCode);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Validate_ReportsFingerprintDrift()
	{
		var directory = Directory.CreateTempSubdirectory("hd2mod-wardrobe-").FullName;
		try
		{
			var sectionsPath = WriteSections(directory);
			var maskPath = Path.Combine(directory, WardrobeMaskFile.FileName);
			// 掩码里的指纹与映射表不一致：模拟"重建之后同一个下标换了内容"。
			File.WriteAllText(maskPath,
				"schema=2\nrevision=1\nenabled=true\n"
				+ "off=1," + UnitA + ",0,ffffffffffffffff-8852\n"
				+ "target=" + Target + ",1\n");

			var validate = await CliTestConsole.RunCapturedAsync(
				"wardrobe", "validate", "--file", maskPath, "--sections", sectionsPath, "--json");
			Assert.Equal(2, validate.ExitCode);
			using var json = validate.ParseJson();
			Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
			var validation = json.RootElement.GetProperty("validation");
			Assert.False(validation.GetProperty("isValid").GetBoolean());
			Assert.Equal(0, validation.GetProperty("resolvedEntryCount").GetInt32());
			var diagnostics = validation.GetProperty("diagnostics").EnumerateArray().ToArray();
			Assert.Contains(diagnostics, item => item.GetProperty("code").GetString() == "fingerprint_mismatch");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Validate_RequiresBothPaths()
	{
		var missingFile = await CliTestConsole.RunCapturedAsync("wardrobe", "validate", "--sections", "x.json", "--json");
		Assert.Equal(2, missingFile.ExitCode);
		Assert.Contains("WardrobeMaskRequired", missingFile.StandardOutput, StringComparison.Ordinal);

		var missingSections = await CliTestConsole.RunCapturedAsync("wardrobe", "validate", "--file", "x.masks", "--json");
		Assert.Equal(2, missingSections.ExitCode);
		Assert.Contains("WardrobeSectionsRequired", missingSections.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Wardrobe_RejectsUnrelatedOptions()
	{
		var capture = await CliTestConsole.RunCapturedAsync(
			"wardrobe", "validate", "--file", "x.masks", "--sections", "x.json", "--confirm", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("CommandOptionNotSupported", capture.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sections_RequiresOutputPath()
	{
		var capture = await CliTestConsole.RunCapturedAsync("wardrobe", "sections", "a.patch_0", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("OutputRequired", capture.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownWardrobeSubcommand_IsRejected()
	{
		var capture = await CliTestConsole.RunCapturedAsync("wardrobe", "paint", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("InvalidArguments", capture.StandardOutput, StringComparison.Ordinal);
	}
}
