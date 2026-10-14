using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

// 作用：验证 unit 只读数据视图命令的命令契约与错误路径。
//       正确解析路径（真实 Unit 的网格/几何/骨骼投影）已在真实 Mod 包上手工验证；
//       这里覆盖的是派发、类型校验、选项闸门与文件缺失这些容易回归的部分。
// Purpose: Covers the command contract and failure paths of the read-only Unit data view.
//          The happy path (real Unit mesh/geometry/bone projection) was verified by hand against a
//          real Mod package; this file guards dispatch, key-type validation, option gating and
//          missing-file handling, which are the parts that regress silently.
[Collection("cli-console")]
public sealed class UnitInspectionCliTests
{
	private const string UnitType = "0xe0a48d0be9a7453f";
	private const string BonesType = "0x18dead01056b72e9";

	// 一个几乎必然不存在的路径：用于验证"先做类型校验、后碰磁盘"的顺序。
	// A path that certainly does not exist: proves key-type validation happens before disk access.
	private const string MissingPatch = "Z:/hd2mod-tests/absent/9ba626afa44a3aa3.patch_0";

	[Fact]
	public async Task UnitShow_RejectsNonUnitKeyBeforeTouchingDisk()
	{
		var capture = await CliTestConsole.RunCapturedAsync(
			"unit", "show", MissingPatch,
			"--keys", $"{BonesType}:0x1",
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("UnitKeyTypeMismatch", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task UnitShow_ReportsMissingPatchAsTocNotFound()
	{
		var capture = await CliTestConsole.RunCapturedAsync(
			"unit", "show", MissingPatch,
			"--keys", $"{UnitType}:0x1",
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("PatchTocNotFound", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Theory]
	[InlineData("show")]
	[InlineData("geometry")]
	[InlineData("bones")]
	public async Task UnitReadCommands_RequireKeys(string subcommand)
	{
		var capture = await CliTestConsole.RunCapturedAsync(
			"unit", subcommand, MissingPatch,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("PatchKeysRequired", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Theory]
	[InlineData("show")]
	[InlineData("geometry")]
	[InlineData("bones")]
	public async Task UnitReadCommands_RejectWriteOptions(string subcommand)
	{
		// unit 三个子命令都是只读的：任何写参数都必须被挡在解析之前。
		// All three unit subcommands are read-only: any write option must be rejected before parsing.
		var capture = await CliTestConsole.RunCapturedAsync(
			"unit", subcommand, MissingPatch,
			"--keys", $"{UnitType}:0x1",
			"--output", "somewhere",
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("CommandOptionNotSupported", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task UnitReadCommands_AreRegisteredInCapabilities()
	{
		using var document = System.Text.Json.JsonDocument.Parse(
			System.Text.Json.JsonSerializer.Serialize(CliProjections.CreateCapabilities()));
		var names = document.RootElement.GetProperty("supports")
			.EnumerateArray()
			.Select(item => item.GetProperty("name").GetString())
			.ToHashSet(StringComparer.Ordinal);

		Assert.Contains("unit show", names);
		Assert.Contains("unit geometry", names);
		Assert.Contains("unit bones", names);
	}
}
