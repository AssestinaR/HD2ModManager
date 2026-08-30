using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class OptionRelationshipWriteServiceTests
{
	[Fact]
	public async Task SetHostScopedOptionEnabledAsync_ChangesOnlyRequestedHost_AndPreservesOrder()
	{
		await using var fixture = await Fixture.CreateAsync();
		var hostA = fixture.Node(ModNodeKind.Standard, "Host A");
		var hostB = fixture.Node(ModNodeKind.Standard, "Host B");
		var shared = fixture.Option("Shared", [hostA.Id, hostB.Id], optionOrder: 1);
		var sibling = fixture.Option("Sibling", [hostA.Id], optionOrder: 0);
		var snapshot = fixture.Snapshot(hostA, hostB, shared, sibling);
		await fixture.Store.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(shared.Id), Key(hostA.Id), false, 8),
			new OptionActivationInitialization(Key(shared.Id), Key(hostB.Id), true, 27),
			new OptionActivationInitialization(Key(sibling.Id), Key(hostA.Id), true, 4),
		]);

		var result = await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, hostA.Id, shared.Id, true);

		Assert.True(result.Changed);
		var sharedForA = Assert.Single(result.Host.DirectOptions, state => state.OptionId == shared.Id);
		Assert.True(sharedForA.Enabled);
		Assert.True(sharedForA.HasExplicitActivationState);
		Assert.Equal(8, sharedForA.EffectiveOrder);
		Assert.True(fixture.Store.CreateSnapshot().IsEnabled(Key(shared.Id), Key(hostA.Id)));
		Assert.True(fixture.Store.CreateSnapshot().IsEnabled(Key(shared.Id), Key(hostB.Id)));
		Assert.Equal(27, fixture.Store.CreateSnapshot().GetEffectiveOrder(Key(shared.Id), Key(hostB.Id)));

		var idempotent = await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, hostA.Id, shared.Id, true);
		Assert.False(idempotent.Changed);
	}

	[Fact]
	public async Task MoveDirectOptionsAsync_ReordersEveryDirectOption_PreservesDisabledState_AndDoesNotCrossHost()
	{
		await using var fixture = await Fixture.CreateAsync();
		var hostA = fixture.Node(ModNodeKind.Standard, "Host A");
		var hostB = fixture.Node(ModNodeKind.Standard, "Host B");
		var first = fixture.Option("First", [hostA.Id], optionOrder: 0);
		var shared = fixture.Option("Shared", [hostA.Id, hostB.Id], optionOrder: 1);
		var last = fixture.Option("Last", [hostA.Id], optionOrder: 2);
		var snapshot = fixture.Snapshot(hostA, hostB, first, shared, last);
		await fixture.Store.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(first.Id), Key(hostA.Id), false, 0),
			new OptionActivationInitialization(Key(shared.Id), Key(hostA.Id), true, 1),
			new OptionActivationInitialization(Key(last.Id), Key(hostA.Id), false, 2),
			new OptionActivationInitialization(Key(shared.Id), Key(hostB.Id), true, 39),
		]);

		var result = await fixture.Service.MoveDirectOptionsAsync(snapshot, fixture.Store, hostA.Id, [last.Id, first.Id], targetPosition: 0);

		Assert.True(result.Changed);
		Assert.Equal([first.Id, last.Id, shared.Id], result.Host.DirectOptions.Select(state => state.OptionId).ToArray());
		Assert.Equal([0, 1, 2], result.Host.DirectOptions.Select(state => state.EffectiveOrder).ToArray());
		Assert.False(Assert.Single(result.Host.DirectOptions, state => state.OptionId == first.Id).Enabled);
		Assert.False(Assert.Single(result.Host.DirectOptions, state => state.OptionId == last.Id).Enabled);
		Assert.True(Assert.Single(result.Host.DirectOptions, state => state.OptionId == shared.Id).Enabled);

		var persisted = fixture.Store.CreateSnapshot();
		Assert.Equal(0, persisted.GetEffectiveOrder(Key(first.Id), Key(hostA.Id)));
		Assert.Equal(1, persisted.GetEffectiveOrder(Key(last.Id), Key(hostA.Id)));
		Assert.Equal(2, persisted.GetEffectiveOrder(Key(shared.Id), Key(hostA.Id)));
		Assert.True(persisted.IsEnabled(Key(shared.Id), Key(hostB.Id)));
		Assert.Equal(39, persisted.GetEffectiveOrder(Key(shared.Id), Key(hostB.Id)));
	}

	[Fact]
	public async Task MoveDirectOptionsAsync_RejectsAnUninitializedDirectState_WithoutWriting()
	{
		await using var fixture = await Fixture.CreateAsync();
		var host = fixture.Node(ModNodeKind.Standard, "Host");
		var first = fixture.Option("First", [host.Id]);
		var missing = fixture.Option("Missing", [host.Id]);
		var snapshot = fixture.Snapshot(host, first, missing);
		await fixture.Store.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(first.Id), Key(host.Id), true, 7),
		]);

		var exception = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.MoveDirectOptionsAsync(snapshot, fixture.Store, host.Id, [first.Id], 1).AsTask());

		Assert.Equal(OptionRelationshipMutationFailure.ActivationStateMissing, exception.Failure);
		Assert.Equal(missing.Id, exception.OptionId);
		var state = fixture.Store.CreateSnapshot();
		Assert.Equal(7, state.GetEffectiveOrder(Key(first.Id), Key(host.Id)));
		Assert.False(state.HasState(Key(missing.Id), Key(host.Id)));
	}

	[Fact]
	public async Task Mutations_RejectInvalidHostsNonOptionsAndUnrelatedOptionsWithoutWriting()
	{
		await using var fixture = await Fixture.CreateAsync();
		var standardHost = fixture.Node(ModNodeKind.Standard, "Host");
		var decoration = fixture.Node(ModNodeKind.Decoration, "Decoration");
		var unrelatedHost = fixture.Node(ModNodeKind.Standard, "Other host");
		var option = fixture.Option("Option", [standardHost.Id]);
		var snapshot = fixture.Snapshot(standardHost, decoration, unrelatedHost, option);

		var invalidHost = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, decoration.Id, option.Id, true).AsTask());
		Assert.Equal(OptionRelationshipMutationFailure.HostKindNotSupported, invalidHost.Failure);

		var nonOption = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, standardHost.Id, standardHost.Id, true).AsTask());
		Assert.Equal(OptionRelationshipMutationFailure.OptionKindInvalid, nonOption.Failure);

		var unrelated = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, unrelatedHost.Id, option.Id, true).AsTask());
		Assert.Equal(OptionRelationshipMutationFailure.DirectRelationNotFound, unrelated.Failure);
		Assert.False(fixture.Store.HasState(Key(option.Id), Key(standardHost.Id)));
		Assert.False(fixture.Store.HasState(Key(option.Id), Key(unrelatedHost.Id)));
	}

	[Fact]
	public async Task Mutations_RejectReachableOptionCyclesBeforeWriting()
	{
		await using var fixture = await Fixture.CreateAsync();
		var root = fixture.Node(ModNodeKind.Standard, "Root");
		var alpha = fixture.Option("Alpha", [root.Id]);
		var beta = fixture.Option("Beta", [alpha.Id]);
		alpha = alpha with { Metadata = alpha.Metadata with { HostModGuids = [Key(root.Id), Key(beta.Id)] } };
		var snapshot = fixture.Snapshot(root, alpha, beta);

		var exception = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.SetHostScopedOptionEnabledAsync(snapshot, fixture.Store, root.Id, alpha.Id, true).AsTask());

		Assert.Equal(OptionRelationshipMutationFailure.CycleDetected, exception.Failure);
		Assert.Equal([alpha.Id, beta.Id, alpha.Id], exception.CyclePath);
		Assert.False(fixture.Store.HasState(Key(alpha.Id), Key(root.Id)));
	}

	[Fact]
	public async Task MoveDirectOptionsAsync_RejectsCrossHostAndInvalidTargetPositions()
	{
		await using var fixture = await Fixture.CreateAsync();
		var hostA = fixture.Node(ModNodeKind.Standard, "Host A");
		var hostB = fixture.Node(ModNodeKind.Standard, "Host B");
		var forA = fixture.Option("For A", [hostA.Id]);
		var forB = fixture.Option("For B", [hostB.Id]);
		var snapshot = fixture.Snapshot(hostA, hostB, forA, forB);
		await fixture.Store.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(forA.Id), Key(hostA.Id), true, 0),
			new OptionActivationInitialization(Key(forB.Id), Key(hostB.Id), true, 0),
		]);

		var crossHost = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.MoveDirectOptionsAsync(snapshot, fixture.Store, hostA.Id, [forB.Id], 0).AsTask());
		Assert.Equal(OptionRelationshipMutationFailure.DirectRelationNotFound, crossHost.Failure);

		var target = await Assert.ThrowsAsync<OptionRelationshipMutationException>(
			async () => await fixture.Service.MoveDirectOptionsAsync(snapshot, fixture.Store, hostA.Id, [forA.Id], 2).AsTask());
		Assert.Equal(OptionRelationshipMutationFailure.InvalidTargetPosition, target.Failure);
	}

	[Fact]
	public async Task GetHostSnapshot_AllowsOptionHostsAndUsesDeploymentTieBreakers()
	{
		await using var fixture = await Fixture.CreateAsync();
		var root = fixture.Node(ModNodeKind.Standard, "Root");
		var hostOption = fixture.Option("Host option", [root.Id], optionOrder: 0);
		var later = fixture.Option("Later", [hostOption.Id], optionOrder: 9);
		var earlier = fixture.Option("Earlier", [hostOption.Id], optionOrder: 1);
		var snapshot = fixture.Snapshot(root, hostOption, later, earlier);
		await fixture.Store.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(hostOption.Id), Key(root.Id), true),
			new OptionActivationInitialization(Key(later.Id), Key(hostOption.Id), true),
			new OptionActivationInitialization(Key(earlier.Id), Key(hostOption.Id), false),
		]);

		var result = fixture.Service.GetHostSnapshot(snapshot, fixture.Store, hostOption.Id);

		Assert.Equal(hostOption.Id, result.HostId);
		Assert.Equal([earlier.Id, later.Id], result.DirectOptions.Select(state => state.OptionId).ToArray());
		Assert.Equal([0, 1], result.DirectOptions.Select(state => state.Position).ToArray());
	}

	private static string Key(ModNodeId id) => id.Value.ToString("N");

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _rootDirectory;

		private Fixture(string rootDirectory)
		{
			_rootDirectory = rootDirectory;
			Store = new OptionActivationStore(Path.Combine(rootDirectory, "option-activations.json"));
			Service = new OptionRelationshipWriteService();
		}

		public OptionActivationStore Store { get; }

		public OptionRelationshipWriteService Service { get; }

		public static ValueTask<Fixture> CreateAsync()
		{
			var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(rootDirectory);
			return ValueTask.FromResult(new Fixture(rootDirectory));
		}

		public ModNode Node(ModNodeKind kind, string name)
			=> new(
				ModNodeId.New(),
				name,
				new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null, kind),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());

		public ModNode Option(string name, IReadOnlyList<ModNodeId> hosts, int? optionOrder = null)
			=> new(
				ModNodeId.New(),
				name,
				new ModNodeMetadata(
					name,
					null,
					DateTimeOffset.UtcNow,
					null,
					ModNodeKind.Option,
					hosts.Select(Key).ToArray(),
					OptionOrder: optionOrder),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());

		public LibrarySnapshot Snapshot(params ModNode[] nodes)
			=> new(
				1,
				DateTimeOffset.UtcNow,
				nodes.ToDictionary(node => node.Id),
				Array.Empty<Profile>());

		public ValueTask DisposeAsync()
		{
			try
			{
				if (Directory.Exists(_rootDirectory)) Directory.Delete(_rootDirectory, recursive: true);
			}
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
