using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ModMediaAndBatchImportCliTests
{
	[Fact]
	public async Task ModImageSetAndClear_OnlyTouchCanonicalIconFilesAndNeverDeploy()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var modDirectory = Path.Combine(fixture.LibraryRoot, fixture.StandardA.RelativePath);
		var legacyIcon = Path.Combine(modDirectory, "icon.jpg");
		var source = Path.Combine(fixture.RootDirectory, "cover.png");
		var activationStatePath = Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName);
		await File.WriteAllBytesAsync(legacyIcon, [0xA1, 0xA2]);
		await File.WriteAllBytesAsync(source, [0x50, 0x4E, 0x47, 0x00]);
		await File.WriteAllTextAsync(activationStatePath, "game-data-must-stay-untouched");
		var profileBefore = (await fixture.LoadSnapshotAsync()).Profiles.Single(profile => profile.Id == fixture.DefaultProfile.Id);

		var requiresConfirmation = await CliTestConsole.RunCapturedAsync(
			"mod", "image", "set", fixture.StandardASelector, source,
			"--manager-root", fixture.ManagerRoot,
			"--json");
		Assert.Equal(2, requiresConfirmation.ExitCode);
		Assert.True(File.Exists(legacyIcon));
		Assert.False(File.Exists(Path.Combine(modDirectory, "icon.png")));

		var set = await CliTestConsole.RunCapturedAsync(
			"mod", "image", "set", fixture.StandardASelector, source,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");
		Assert.Equal(0, set.ExitCode);
		using (var setJson = set.ParseJson())
		{
			Assert.Equal("set", setJson.RootElement.GetProperty("image").GetProperty("operation").GetString());
			Assert.Equal("not-performed", setJson.RootElement.GetProperty("image").GetProperty("deployment").GetString());
		}
		var canonicalIcon = Path.Combine(modDirectory, "icon.png");
		Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(canonicalIcon));
		Assert.False(File.Exists(legacyIcon));
		Assert.Equal(canonicalIcon, ModIconLocator.TryResolve(modDirectory));

		var inspection = await CliTestConsole.RunCapturedAsync(
			"mod", "image", "show", fixture.StandardASelector,
			"--library", fixture.LibraryRoot,
			"--json");
		Assert.Equal(0, inspection.ExitCode);
		using (var inspectionJson = inspection.ParseJson())
			Assert.Equal(canonicalIcon, inspectionJson.RootElement.GetProperty("image").GetProperty("resolvedImagePath").GetString());

		var clear = await CliTestConsole.RunCapturedAsync(
			"mod", "image", "clear", fixture.StandardASelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");
		Assert.Equal(0, clear.ExitCode);
		Assert.False(File.Exists(canonicalIcon));
		Assert.Null(ModIconLocator.TryResolve(modDirectory));
		var profileAfter = (await fixture.LoadSnapshotAsync()).Profiles.Single(profile => profile.Id == fixture.DefaultProfile.Id);
		Assert.Equal(profileBefore.Revision, profileAfter.Revision);
		Assert.Equal("game-data-must-stay-untouched", await File.ReadAllTextAsync(activationStatePath));
	}

	[Fact]
	public async Task BatchImportPlanValidateApply_ImportsEachSourceThenAddsTheirDeployableNodesOnce()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var first = await CreateImportSourceAsync(fixture.RootDirectory, "incoming-first", "0123456789abcdef");
		var second = await CreateImportSourceAsync(fixture.RootDirectory, "incoming-second", "fedcba9876543210");
		var planPath = Path.Combine(fixture.RootDirectory, "batch-import-plan.json");
		var activationStatePath = Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName);
		await File.WriteAllTextAsync(activationStatePath, "game-data-must-stay-untouched");

		var planned = await CliTestConsole.RunCapturedAsync(
			"import", "batch", "plan", first, second,
			"--add-to-profile", "active",
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json");
		Assert.Equal(0, planned.ExitCode);
		using (var planJson = planned.ParseJson())
		{
			Assert.Equal(2, planJson.RootElement.GetProperty("batch").GetProperty("sourceCount").GetInt32());
			Assert.True(planJson.RootElement.GetProperty("batch").GetProperty("sequential").GetBoolean());
		}

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"import", "batch", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json",
		]));

		var applied = await CliTestConsole.RunCapturedAsync(
			"import", "batch", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");
		Assert.Equal(0, applied.ExitCode);
		using (var appliedJson = applied.ParseJson())
		{
			var execution = appliedJson.RootElement.GetProperty("execution");
			Assert.Equal("completed", execution.GetProperty("status").GetString());
			Assert.Equal(2, execution.GetProperty("sources").GetArrayLength());
			Assert.Equal(2, execution.GetProperty("profileAddition").GetProperty("addedNodeIds").GetArrayLength());
		}

		var snapshot = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(snapshot.Profiles, item => item.Id == fixture.DefaultProfile.Id);
		Assert.Equal(4, profile.Entries.Count);
		Assert.Equal("game-data-must-stay-untouched", await File.ReadAllTextAsync(activationStatePath));
	}

	[Fact]
	public async Task BatchImportPlan_RejectsSourceDriftAndPlanTamperingBeforeAnyImport()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var first = await CreateImportSourceAsync(fixture.RootDirectory, "incoming-drift-first", "0123456789abcdef");
		var second = await CreateImportSourceAsync(fixture.RootDirectory, "incoming-drift-second", "fedcba9876543210");
		var planPath = Path.Combine(fixture.RootDirectory, "batch-import-drift-plan.json");
		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"import", "batch", "plan", first, second,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json",
		]));
		await File.AppendAllBytesAsync(Path.Combine(second, "fedcba9876543210.patch_0"), [0xFF]);

		var validation = await CliTestConsole.RunCapturedAsync(
			"import", "batch", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json");
		Assert.Equal(2, validation.ExitCode);
		var snapshot = await fixture.LoadSnapshotAsync();
		Assert.Equal(9, snapshot.Nodes.Count);

		var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
		plan["imports"]![0]!["source"]!["requestedPath"] = first + "-forged";
		await File.WriteAllTextAsync(planPath, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var tampered = await CliTestConsole.RunCapturedAsync(
			"import", "batch", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json");
		Assert.Equal(2, tampered.ExitCode);
		using var tamperedJson = tampered.ParseJson();
		Assert.Equal("BatchImportPlanIntegrityMismatch", tamperedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private static async Task<string> CreateImportSourceAsync(string root, string name, string archiveHex16)
	{
		var directory = Path.Combine(root, name);
		Directory.CreateDirectory(directory);
		await File.WriteAllBytesAsync(Path.Combine(directory, archiveHex16 + ".patch_0"), [0x4D, 0x4F, 0x44]);
		return directory;
	}
}
