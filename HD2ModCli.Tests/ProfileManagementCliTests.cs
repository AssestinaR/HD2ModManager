using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ProfileManagementCliTests
{
	[Fact]
	public async Task ProfileCrudAndOrdering_KeepLogicalMembershipDeterministicAndNeverDeploy()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var activationStatePath = Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName);
		await File.WriteAllTextAsync(activationStatePath, "unrelated-game-state");

		Assert.Equal(0, await RunAsync("profile", "list", "--manager-root", fixture.ManagerRoot, "--json"));
		Assert.Equal(0, await RunAsync("profile", "show", fixture.DefaultProfileSelector, "--manager-root", fixture.ManagerRoot, "--json"));
		Assert.Equal(0, await RunAsync("profile", "create", "Working", "--set-active", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		Assert.Equal(0, await RunAsync("profile", "add", "Working", fixture.StandardASelector, fixture.StandardBSelector, "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		Assert.Equal(0, await RunAsync("profile", "insert", "Working", fixture.StandardCSelector, "--position", "1", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		await AssertProfileOrderAsync(fixture, "Working", fixture.StandardC.Id, fixture.StandardA.Id, fixture.StandardB.Id);

		Assert.Equal(0, await RunAsync("profile", "move", "Working", fixture.StandardBSelector, "--position", "1", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		await AssertProfileOrderAsync(fixture, "Working", fixture.StandardB.Id, fixture.StandardC.Id, fixture.StandardA.Id);

		Assert.Equal(0, await RunAsync("profile", "reorder", "Working", fixture.StandardASelector, fixture.StandardBSelector, fixture.StandardCSelector, "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		await AssertProfileOrderAsync(fixture, "Working", fixture.StandardA.Id, fixture.StandardB.Id, fixture.StandardC.Id);

		Assert.Equal(0, await RunAsync("profile", "remove", "Working", fixture.StandardBSelector, "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		await AssertProfileOrderAsync(fixture, "Working", fixture.StandardA.Id, fixture.StandardC.Id);
		Assert.Equal(0, await RunAsync("profile", "rename", "Working", "Final", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		Assert.Equal(0, await RunAsync("profile", "clear-active", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));
		Assert.Equal(0, await RunAsync("profile", "set-active", "Final", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));

		var beforeDelete = await fixture.LoadSnapshotAsync();
		var final = Assert.Single(beforeDelete.Profiles, profile => profile.Name == "Final");
		Assert.Equal(final.Id, beforeDelete.ActiveProfileId);
		Assert.Equal(0, await RunAsync("profile", "delete", "Final", "--manager-root", fixture.ManagerRoot, "--confirm", "--json"));

		var afterDelete = await fixture.LoadSnapshotAsync();
		Assert.DoesNotContain(afterDelete.Profiles, profile => profile.Name == "Final");
		Assert.Null(afterDelete.ActiveProfileId);
		Assert.Equal("unrelated-game-state", await File.ReadAllTextAsync(activationStatePath));
	}

	private static Task<int> RunAsync(params string[] args) => HD2ModCli.CliApplication.RunAsync(args);

	private static async Task AssertProfileOrderAsync(CliManagementFixture fixture, string name, params ModNodeId[] expected)
	{
		var snapshot = await fixture.LoadSnapshotAsync();
		var profile = Assert.Single(snapshot.Profiles, profile => profile.Name == name);
		Assert.Equal(expected, profile.Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray());
	}
}
