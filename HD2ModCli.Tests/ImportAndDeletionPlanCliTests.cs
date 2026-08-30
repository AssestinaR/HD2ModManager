using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ImportAndDeletionPlanCliTests
{
	[Fact]
	public async Task ImportPlan_AddToActiveProfile_RequiresStableProfileAndAddsOnlyDeployableNodes()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var source = Path.Combine(fixture.RootDirectory, "incoming-mod");
		Directory.CreateDirectory(source);
		await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [0x4D, 0x4F, 0x44]);
		var planPath = Path.Combine(fixture.RootDirectory, "import-plan.json");
		var activationStatePath = Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName);
		await File.WriteAllTextAsync(activationStatePath, "game-data-must-stay-untouched");

		var planned = await CliTestConsole.RunCapturedAsync(
			"import", "plan", source,
			"--add-to-profile", "active",
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json");
		Assert.Equal(0, planned.ExitCode);
		using (var plannedJson = planned.ParseJson())
		{
			Assert.Equal(fixture.DefaultProfile.Name, plannedJson.RootElement.GetProperty("addToProfile").GetProperty("profileName").GetString());
		}

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"import", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json",
		]));
		var applied = await CliTestConsole.RunCapturedAsync(
			"import", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");
		Assert.Equal(0, applied.ExitCode);
		using (var appliedJson = applied.ParseJson())
		{
			var addition = appliedJson.RootElement.GetProperty("applied").GetProperty("profileAddition");
			Assert.Equal(fixture.DefaultProfile.Name, addition.GetProperty("profileName").GetString());
			Assert.Equal(1, addition.GetProperty("addedNodeIds").GetArrayLength());
		}

		var snapshot = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(snapshot.Profiles, item => item.Id == fixture.DefaultProfile.Id);
		Assert.Equal(3, profile.Entries.Count);
		Assert.Contains(profile.Entries, entry => entry.NodeId != fixture.StandardA.Id && entry.NodeId != fixture.StandardB.Id);
		Assert.Equal("game-data-must-stay-untouched", await File.ReadAllTextAsync(activationStatePath));
	}

	[Fact]
	public async Task ImportPlan_AddToProfile_RejectsProfileRevisionDriftBeforeWritingSource()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var source = Path.Combine(fixture.RootDirectory, "incoming-drift");
		Directory.CreateDirectory(source);
		await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [0x01]);
		var planPath = Path.Combine(fixture.RootDirectory, "import-drift-plan.json");
		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"import", "plan", source,
			"--add-to-profile", fixture.DefaultProfile.Name,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json",
		]));

		var store = new JsonModLibraryStore(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot), allowLegacyMigration: false);
		var before = await fixture.LoadSnapshotAsync();
		var mutation = CoreServices.CreateProfileMutationService().AddEntries(before, fixture.DefaultProfile.Id, [fixture.StandardC.Id]);
		Assert.True(mutation.Changed);
		await store.SaveAsync(mutation.Snapshot);
		var libraryBeforeApply = await File.ReadAllBytesAsync(fixture.LibraryPath);

		var validation = await CliTestConsole.RunCapturedAsync(
			"import", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json");
		Assert.Equal(2, validation.ExitCode);
		using (var validationJson = validation.ParseJson())
		{
			var codes = validationJson.RootElement.GetProperty("validation").GetProperty("issues")
				.EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
			Assert.Contains("ImportProfileTargetChanged", codes);
		}
		Assert.Equal(libraryBeforeApply, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Empty(Directory.EnumerateDirectories(fixture.LibraryRoot, "incoming-drift*", SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task ModDeletePlan_FreezesStorageThenValidatesAndAppliesUnderThePlan()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "deletion-plan.json");
		var gameStatePath = Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName);
		await File.WriteAllTextAsync(gameStatePath, "unrelated-game-data");

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "delete", "plan", fixture.StandardCSelector,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json",
		]));
		Assert.True(File.Exists(planPath));
		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "delete", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json",
		]));
		var apply = await CliTestConsole.RunCapturedAsync(
			"mod", "delete", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");
		Assert.Equal(0, apply.ExitCode);
		using (var applyJson = apply.ParseJson())
		{
			Assert.True(applyJson.RootElement.GetProperty("ok").GetBoolean());
			Assert.Equal("completed", applyJson.RootElement.GetProperty("execution").GetProperty("deletion").GetProperty("status").GetString());
		}

		var snapshot = await fixture.LoadSnapshotAsync();
		Assert.DoesNotContain(fixture.StandardC.Id, snapshot.Nodes.Keys);
		Assert.False(Directory.Exists(Path.Combine(fixture.LibraryRoot, fixture.StandardC.RelativePath)));
		Assert.Equal("unrelated-game-data", await File.ReadAllTextAsync(gameStatePath));
	}

	[Fact]
	public async Task ModDeletePlan_RejectsNewUnreviewedStorageContentWithoutDeleting()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "deletion-drift-plan.json");
		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "delete", "plan", fixture.StandardCSelector,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json",
		]));
		await File.WriteAllTextAsync(Path.Combine(fixture.LibraryRoot, fixture.StandardC.RelativePath, "unreviewed.txt"), "added-after-plan");

		var validate = await CliTestConsole.RunCapturedAsync(
			"mod", "delete", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--json");
		Assert.Equal(2, validate.ExitCode);
		using (var validationJson = validate.ParseJson())
		{
			var codes = validationJson.RootElement.GetProperty("validation").GetProperty("diagnostics")
				.EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
			Assert.Contains("ModDeletionInputChanged", codes);
		}
		var snapshot = await fixture.LoadSnapshotAsync();
		Assert.Contains(fixture.StandardC.Id, snapshot.Nodes.Keys);
		Assert.True(File.Exists(Path.Combine(fixture.LibraryRoot, fixture.StandardC.RelativePath, "unreviewed.txt")));
	}
}
