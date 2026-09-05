using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ModRelationshipCliTests
{
	[Fact]
	public async Task CreateLogicalMod_AndAddRemoveMultiParentOption_UseTheSharedCoreTransaction()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		Assert.Equal(0, await RunAsync(
			"mod", "create", "角色替换",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));

		var createdSnapshot = await fixture.LoadSnapshotAsync();
		var logical = Assert.Single(createdSnapshot.Nodes.Values, node => node.Metadata.Name == "角色替换");
		Assert.Equal(ModNodeKind.Standard, logical.Metadata.Kind);
		Assert.True(logical.Metadata.IsLogicalContainer);
		Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(Path.Combine(fixture.LibraryRoot, logical.RelativePath))));

		Assert.Equal(0, await RunAsync(
			"option", "parent", "add", fixture.StandardASelector,
			"--host", fixture.RootHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));
		var afterFirstParent = await fixture.LoadSnapshotAsync();
		var optionAfterFirstParent = afterFirstParent.Nodes[fixture.StandardA.Id];
		Assert.Equal(ModNodeKind.Option, optionAfterFirstParent.Metadata.Kind);
		Assert.Equal([CliManagementFixture.Key(fixture.RootHost.Id)], optionAfterFirstParent.Metadata.HostModGuids);
		Assert.DoesNotContain(afterFirstParent.Profiles.Single().Entries, entry => entry.NodeId == fixture.StandardA.Id);
		Assert.True(File.Exists(ModNodeStorageProtocol.OptionRelationPath(Path.Combine(fixture.LibraryRoot, optionAfterFirstParent.RelativePath))));
		Assert.True((await fixture.LoadOptionActivationsAsync()).IsEnabled(
			CliManagementFixture.Key(fixture.StandardA.Id),
			CliManagementFixture.Key(fixture.RootHost.Id)));

		Assert.Equal(0, await RunAsync(
			"option", "parent", "add", fixture.StandardASelector,
			"--host", fixture.StandardBSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));
		var afterSecondParent = await fixture.LoadSnapshotAsync();
		var sharedOption = afterSecondParent.Nodes[fixture.StandardA.Id];
		Assert.Equal(
			new[] { CliManagementFixture.Key(fixture.RootHost.Id), CliManagementFixture.Key(fixture.StandardB.Id) }.OrderBy(value => value),
			sharedOption.Metadata.HostModGuids!.OrderBy(value => value));

		Assert.Equal(0, await RunAsync(
			"option", "parent", "remove", fixture.StandardASelector,
			"--host", fixture.RootHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));
		var afterFirstRemoval = await fixture.LoadSnapshotAsync();
		Assert.Equal(ModNodeKind.Option, afterFirstRemoval.Nodes[fixture.StandardA.Id].Metadata.Kind);
		Assert.Equal([CliManagementFixture.Key(fixture.StandardB.Id)], afterFirstRemoval.Nodes[fixture.StandardA.Id].Metadata.HostModGuids);

		Assert.Equal(0, await RunAsync(
			"option", "parent", "remove", fixture.StandardASelector,
			"--host", fixture.StandardBSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));
		var restored = await fixture.LoadSnapshotAsync();
		var standardAgain = restored.Nodes[fixture.StandardA.Id];
		Assert.Equal(ModNodeKind.Standard, standardAgain.Metadata.Kind);
		Assert.Null(standardAgain.Metadata.HostModGuids);
		Assert.DoesNotContain(restored.Profiles.Single().Entries, entry => entry.NodeId == fixture.StandardA.Id);
		Assert.False(File.Exists(ModNodeStorageProtocol.OptionRelationPath(Path.Combine(fixture.LibraryRoot, standardAgain.RelativePath))));
		Assert.False((await fixture.LoadOptionActivationsAsync()).HasState(
			CliManagementFixture.Key(fixture.StandardA.Id),
			CliManagementFixture.Key(fixture.StandardB.Id)));
	}

	[Fact]
	public async Task DeleteHost_PrunesSharedOptionRelationFromSnapshotAndSidecar()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		Assert.Equal(0, await RunAsync(
			"option", "parent", "add", fixture.StandardASelector,
			"--host", fixture.RootHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));
		Assert.Equal(0, await RunAsync(
			"option", "parent", "add", fixture.StandardASelector,
			"--host", fixture.StandardBSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));

		Assert.Equal(0, await RunAsync(
			"mod", "delete", fixture.RootHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json"));

		var snapshot = await fixture.LoadSnapshotAsync();
		var retained = snapshot.Nodes[fixture.StandardA.Id];
		Assert.Equal(ModNodeKind.Option, retained.Metadata.Kind);
		Assert.Equal([CliManagementFixture.Key(fixture.StandardB.Id)], retained.Metadata.HostModGuids);
		var sidecarPath = ModNodeStorageProtocol.OptionRelationPath(Path.Combine(fixture.LibraryRoot, retained.RelativePath));
		var sidecar = await File.ReadAllTextAsync(sidecarPath);
		Assert.Contains(CliManagementFixture.Key(fixture.StandardB.Id), sidecar, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(CliManagementFixture.Key(fixture.RootHost.Id), sidecar, StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<int> RunAsync(params string[] args)
		=> (await CliTestConsole.RunCapturedAsync(args)).ExitCode;
}
