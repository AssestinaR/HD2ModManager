using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class BatchPatchRepairCliTests
{
	[Fact]
	public async Task BatchRepairPlan_MissingGameDataIndex_WritesAuditableNoMutationPlan()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "batch-repair-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var profilesBefore = await File.ReadAllBytesAsync(fixture.ProfilesPath);
		var sourcePath = Path.Combine(fixture.LibraryRoot, fixture.StandardA.RelativePath, "1111111111111111.patch_0");
		var patchBefore = await File.ReadAllBytesAsync(sourcePath);

		var result = await CliTestConsole.RunCapturedAsync(
			"patch", "repair", "batch", "plan", fixture.StandardASelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, result.ExitCode);
		Assert.True(File.Exists(planPath));
		using var response = result.ParseJson();
		var repair = response.RootElement.GetProperty("repair");
		Assert.Equal("hd2mod.batch-patch-repair-plan", repair.GetProperty("schema").GetString());
		Assert.Equal(1, repair.GetProperty("mods").GetArrayLength());
		Assert.Equal(fixture.StandardASelector, repair.GetProperty("mods")[0].GetProperty("nodeId").GetString());
		Assert.Contains(
			repair.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "BatchRepairArchiveHashesMissing", StringComparison.Ordinal));

		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(profilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
		Assert.Equal(patchBefore, await File.ReadAllBytesAsync(sourcePath));
	}

	[Fact]
	public async Task BatchRepairValidate_RejectsSourceDriftBeforeAnyApply()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "batch-repair-drift-plan.json");
		Assert.Equal(2, await CliApplication.RunAsync(
		[
			"patch", "repair", "batch", "plan", fixture.StandardASelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]));

		var sourcePath = Path.Combine(fixture.LibraryRoot, fixture.StandardA.RelativePath, "1111111111111111.patch_0");
		await File.AppendAllBytesAsync(sourcePath, [0xEE]);
		var result = await CliTestConsole.RunCapturedAsync(
			"patch", "repair", "batch", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, result.ExitCode);
		using var response = result.ParseJson();
		Assert.Contains(
			response.RootElement.GetProperty("validation").GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "BatchRepairInputChanged", StringComparison.Ordinal));
	}

	[Fact]
	public async Task BatchRepairValidate_RejectsPlanFactTamperingButAllowsSelectedAsTheOnlyEditableField()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "batch-repair-tamper-plan.json");
		Assert.Equal(2, await CliApplication.RunAsync(
		[
			"patch", "repair", "batch", "plan", fixture.StandardASelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]));

		var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
		plan["mods"]![0]!["selected"] = true;
		await File.WriteAllTextAsync(planPath, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var selectedOnly = await CliTestConsole.RunCapturedAsync(
			"patch", "repair", "batch", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.Equal(2, selectedOnly.ExitCode);
		using (var selectedOnlyJson = selectedOnly.ParseJson())
		{
			var codes = selectedOnlyJson.RootElement.GetProperty("validation").GetProperty("diagnostics").EnumerateArray()
				.Select(item => item.GetProperty("code").GetString()).ToArray();
			Assert.DoesNotContain(codes, code => string.Equals(code, "BatchRepairPlanTampered", StringComparison.Ordinal));
			Assert.Contains(codes, code => string.Equals(code, "BatchRepairSelectionNotOutdated", StringComparison.Ordinal));
		}

		plan["mods"]![0]!["name"] = "forged-name";
		await File.WriteAllTextAsync(planPath, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var result = await CliTestConsole.RunCapturedAsync(
			"patch", "repair", "batch", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, result.ExitCode);
		using var response = result.ParseJson();
		Assert.Contains(
			response.RootElement.GetProperty("validation").GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "BatchRepairPlanTampered", StringComparison.Ordinal));
	}
}
