using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ProfileAlignCliTests
{
	[Fact]
	public async Task ProfileAlign_ExportInspectPlanValidateApply_OnlyRewritesProfilesAndIsIdempotent()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "profile-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "profile-align-plan.json");
		var before = await fixture.LoadSnapshotAsync();
		var originalA = before.Profiles.Single(profile => profile.Id == fixture.DefaultProfile.Id).Entries.Single(entry => entry.NodeId == fixture.StandardA.Id).AddedUtc;
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.OptionActivationsPath);

		Assert.Equal(0, await RunAsync(
			"profile", "align", "export", fixture.DefaultProfileSelector,
			"--library", fixture.LibraryRoot,
			"--output", specificationPath,
			"--json"));
		using (var specification = JsonDocument.Parse(await File.ReadAllTextAsync(specificationPath)))
		{
			Assert.Equal("hd2mod.profile-align-spec", specification.RootElement.GetProperty("schema").GetString());
			Assert.Equal("preserve", specification.RootElement.GetProperty("activeMode").GetString());
		}
		await RewriteSpecificationAsync(specificationPath, [fixture.StandardCSelector, fixture.StandardASelector]);

		Assert.Equal(0, await RunAsync(
			"profile", "align", "inspect", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"profile", "align", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"profile", "align", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--json"));

		var apply = await CliTestConsole.RunCapturedAsync(
			"profile", "align", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm", "--json");
		Assert.True(apply.ExitCode == 0, apply.StandardOutput + Environment.NewLine + apply.StandardError);
		using (var applyJson = apply.ParseJson())
		{
			Assert.True(applyJson.RootElement.GetProperty("ok").GetBoolean());
			Assert.Equal("applied", applyJson.RootElement.GetProperty("execution").GetProperty("status").GetString());
			Assert.True(applyJson.RootElement.GetProperty("execution").GetProperty("profilesWritten").GetBoolean());
		}

		var after = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(after.Profiles, item => item.Id == fixture.DefaultProfile.Id);
		Assert.Equal([fixture.StandardC.Id, fixture.StandardA.Id], profile.Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray());
		Assert.Equal(1, profile.Revision);
		Assert.Equal(originalA, profile.Entries.Single(entry => entry.NodeId == fixture.StandardA.Id).AddedUtc);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
		var profilesAfterApply = await File.ReadAllBytesAsync(fixture.ProfilesPath);

		var secondApply = await CliTestConsole.RunCapturedAsync(
			"profile", "align", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm", "--json");
		Assert.Equal(0, secondApply.ExitCode);
		using (var secondJson = secondApply.ParseJson())
		{
			Assert.Equal("already-aligned", secondJson.RootElement.GetProperty("execution").GetProperty("status").GetString());
			Assert.False(secondJson.RootElement.GetProperty("execution").GetProperty("profilesWritten").GetBoolean());
		}
		Assert.Equal(profilesAfterApply, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	[Fact]
	public async Task ProfileAlign_EmptyTargetAndActiveMode_ChangesOnlyTheReviewedProfileState()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var store = new JsonModLibraryStore(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot), allowLegacyMigration: false);
		var before = await fixture.LoadSnapshotAsync();
		var created = CoreServices.CreateProfileMutationService().CreateProfile(before, "Other", makeActive: true);
		Assert.True(created.Changed);
		await store.SaveAsync(created.Snapshot);
		var specificationPath = Path.Combine(fixture.RootDirectory, "empty-profile-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "empty-profile-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);

		Assert.Equal(0, await RunAsync(
			"profile", "align", "export", fixture.DefaultProfileSelector,
			"--library", fixture.LibraryRoot,
			"--output", specificationPath,
			"--json"));
		await RewriteSpecificationAsync(specificationPath, Array.Empty<string>(), "setActive");
		Assert.Equal(0, await RunAsync(
			"profile", "align", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"profile", "align", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm", "--json"));

		var after = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(after.Profiles, item => item.Id == fixture.DefaultProfile.Id);
		Assert.Empty(profile.Entries);
		Assert.Equal(1, profile.Revision);
		Assert.Equal(fixture.DefaultProfile.Id, after.ActiveProfileId);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
	}

	[Fact]
	public async Task ProfileAlign_ClearIfActive_ClearsOnlyTheReviewedActiveProfileWithoutChangingMembership()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "clear-active-profile-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "clear-active-profile-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);

		Assert.Equal(0, await RunAsync(
			"profile", "align", "export", fixture.DefaultProfileSelector,
			"--library", fixture.LibraryRoot,
			"--output", specificationPath,
			"--json"));
		await RewriteSpecificationAsync(specificationPath, [fixture.StandardASelector, fixture.StandardBSelector], "clearIfActive");
		Assert.Equal(0, await RunAsync(
			"profile", "align", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"profile", "align", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm", "--json"));

		var after = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(after.Profiles, item => item.Id == fixture.DefaultProfile.Id);
		Assert.Equal([fixture.StandardA.Id, fixture.StandardB.Id], profile.Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray());
		Assert.Equal(0, profile.Revision);
		Assert.Null(after.ActiveProfileId);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
	}

	[Fact]
	public async Task ProfileAlign_RejectsTamperedPlanAndProfileDriftWithoutWriting()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "drift-profile-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "drift-profile-plan.json");
		Assert.Equal(0, await RunAsync(
			"profile", "align", "export", fixture.DefaultProfileSelector,
			"--library", fixture.LibraryRoot,
			"--output", specificationPath,
			"--json"));
		await RewriteSpecificationAsync(specificationPath, [fixture.StandardCSelector, fixture.StandardASelector]);
		Assert.Equal(0, await RunAsync(
			"profile", "align", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));

		var forged = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
		forged["request"]!["entryNodeIds"]![0] = fixture.StandardBSelector;
		await File.WriteAllTextAsync(planPath, forged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var forgedValidation = await CliTestConsole.RunCapturedAsync(
			"profile", "align", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--json");
		Assert.Equal(2, forgedValidation.ExitCode);
		using (var forgedJson = forgedValidation.ParseJson())
			Assert.Equal("ProfileAlignPlanIntegrityMismatch", forgedJson.RootElement.GetProperty("error").GetProperty("code").GetString());

		Assert.Equal(0, await RunAsync(
			"profile", "align", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--overwrite", "--json"));
		var store = new JsonModLibraryStore(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot), allowLegacyMigration: false);
		var beforeDrift = await fixture.LoadSnapshotAsync();
		var drift = CoreServices.CreateProfileMutationService().MoveEntries(beforeDrift, fixture.DefaultProfile.Id, [fixture.StandardA.Id], targetPosition: 2);
		Assert.True(drift.Changed);
		await store.SaveAsync(drift.Snapshot);
		var profilesBeforeApply = await File.ReadAllBytesAsync(fixture.ProfilesPath);

		var validation = await CliTestConsole.RunCapturedAsync(
			"profile", "align", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--json");
		Assert.Equal(2, validation.ExitCode);
		using (var validationJson = validation.ParseJson())
		{
			var codes = validationJson.RootElement.GetProperty("validation").GetProperty("diagnostics")
				.EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
			Assert.Contains("ProfileAlignInputChanged", codes);
		}
		Assert.Equal(profilesBeforeApply, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	private static async ValueTask RewriteSpecificationAsync(string path, IReadOnlyList<string> entryIds, string? activeMode = null)
	{
		var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
		var entries = new JsonArray();
		foreach (var entryId in entryIds)
			entries.Add(entryId);
		root["entryNodeIds"] = entries;
		if (activeMode is not null)
			root["activeMode"] = activeMode;
		await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
	}

	private static async ValueTask<int> RunAsync(params string[] args)
		=> (await CliTestConsole.RunCapturedAsync(args)).ExitCode;
}
