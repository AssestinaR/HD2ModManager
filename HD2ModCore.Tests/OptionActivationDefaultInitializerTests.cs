using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class OptionActivationDefaultInitializerTests
{
	[Fact]
	public async Task InitializeMissingStatesAsync_EnablesAllStandardChildren_AndOnlyFirstOptionChild()
	{
		var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var root = Node(ModNodeKind.Standard, "Root");
			var directA = Node(ModNodeKind.Option, "A", [root.Id.Value.ToString("N")], optionOrder: 0);
			var directB = Node(ModNodeKind.Option, "B", [root.Id.Value.ToString("N")], optionOrder: 1);
			var nestedA = Node(ModNodeKind.Option, "A1", [directA.Id.Value.ToString("N")], optionOrder: 0);
			var nestedB = Node(ModNodeKind.Option, "A2", [directA.Id.Value.ToString("N")], optionOrder: 1);
			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>
				{
					[root.Id] = root,
					[directA.Id] = directA,
					[directB.Id] = directB,
					[nestedA.Id] = nestedA,
					[nestedB.Id] = nestedB,
				},
				Array.Empty<Profile>());
			var states = new OptionActivationStore(Path.Combine(rootDirectory, "option-activations.json"));
			var initializer = new OptionActivationDefaultInitializer();

			var changed = await initializer.InitializeMissingStatesAsync(snapshot, states, [root.Id]);

			Assert.Equal(4, changed);
			var state = states.CreateSnapshot();
			Assert.True(state.IsEnabled(directA.Id.Value.ToString("N"), root.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(directB.Id.Value.ToString("N"), root.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(nestedA.Id.Value.ToString("N"), directA.Id.Value.ToString("N")));
			Assert.False(state.IsEnabled(nestedB.Id.Value.ToString("N"), directA.Id.Value.ToString("N")));

			await states.SetEnabledAsync(nestedB.Id.Value.ToString("N"), directA.Id.Value.ToString("N"), true);
			Assert.Equal(0, await initializer.InitializeMissingStatesAsync(snapshot, states, [root.Id]));
			Assert.True(states.CreateSnapshot().IsEnabled(nestedB.Id.Value.ToString("N"), directA.Id.Value.ToString("N")));
		}
		finally
		{
			try { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task InitializeMissingStatesAsync_SuppressesOnlyTheConflictedParentsDirectDefaults()
	{
		var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var root = Node(ModNodeKind.Standard, "Root");
			var safe = Node(ModNodeKind.Option, "Safe", [root.Id.Value.ToString("N")], optionOrder: 0);
			var conflicted = new ModNode(
				ModNodeId.New(),
				"Conflicted",
				new ModNodeMetadata(
					"Conflicted",
					null,
					DateTimeOffset.UtcNow,
					null,
					ModNodeKind.Option,
					[root.Id.Value.ToString("N")],
					OptionOrder: 1,
					SuppressDefaultDirectOptionActivation: true),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
			var safeFirst = Node(ModNodeKind.Option, "Safe First", [safe.Id.Value.ToString("N")], optionOrder: 0);
			var safeSecond = Node(ModNodeKind.Option, "Safe Second", [safe.Id.Value.ToString("N")], optionOrder: 1);
			var physicalA = Node(ModNodeKind.Option, "Physical A", [conflicted.Id.Value.ToString("N")], optionOrder: 0);
			var physicalB = Node(ModNodeKind.Option, "Physical B", [conflicted.Id.Value.ToString("N")], optionOrder: 1);
			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>
				{
					[root.Id] = root,
					[safe.Id] = safe,
					[conflicted.Id] = conflicted,
					[safeFirst.Id] = safeFirst,
					[safeSecond.Id] = safeSecond,
					[physicalA.Id] = physicalA,
					[physicalB.Id] = physicalB,
				},
				Array.Empty<Profile>());
			var states = new OptionActivationStore(Path.Combine(rootDirectory, "option-activations.json"));

			await new OptionActivationDefaultInitializer().InitializeMissingStatesAsync(snapshot, states, [root.Id]);

			var state = states.CreateSnapshot();
			Assert.True(state.IsEnabled(safe.Id.Value.ToString("N"), root.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(conflicted.Id.Value.ToString("N"), root.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(safeFirst.Id.Value.ToString("N"), safe.Id.Value.ToString("N")));
			Assert.False(state.IsEnabled(safeSecond.Id.Value.ToString("N"), safe.Id.Value.ToString("N")));
			Assert.False(state.IsEnabled(physicalA.Id.Value.ToString("N"), conflicted.Id.Value.ToString("N")));
			Assert.False(state.IsEnabled(physicalB.Id.Value.ToString("N"), conflicted.Id.Value.ToString("N")));
		}
		finally
		{
			try { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task InitializeMissingStatesAsync_EnablesAllChildrenOfLogicalIncludeContainer()
	{
		var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var root = Node(ModNodeKind.Standard, "Root");
			var logical = new ModNode(
				ModNodeId.New(),
				"Logical",
				new ModNodeMetadata("Logical", null, DateTimeOffset.UtcNow, null, ModNodeKind.Option,
					[root.Id.Value.ToString("N")], IsLogicalContainer: true),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
			var first = Node(ModNodeKind.Option, "First", [logical.Id.Value.ToString("N")], optionOrder: 0);
			var second = Node(ModNodeKind.Option, "Second", [logical.Id.Value.ToString("N")], optionOrder: 1);
			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>
				{
					[root.Id] = root,
					[logical.Id] = logical,
					[first.Id] = first,
					[second.Id] = second,
				},
				Array.Empty<Profile>());
			var states = new OptionActivationStore(Path.Combine(rootDirectory, "option-activations.json"));

			await new OptionActivationDefaultInitializer().InitializeMissingStatesAsync(snapshot, states, [root.Id]);

			var state = states.CreateSnapshot();
			Assert.True(state.IsEnabled(logical.Id.Value.ToString("N"), root.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(first.Id.Value.ToString("N"), logical.Id.Value.ToString("N")));
			Assert.True(state.IsEnabled(second.Id.Value.ToString("N"), logical.Id.Value.ToString("N")));
		}
		finally
		{
			try { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, recursive: true); } catch { }
		}
	}

	private static ModNode Node(ModNodeKind kind, string name, IReadOnlyList<string>? hostIds = null, int? optionOrder = null)
		=> new(
			ModNodeId.New(),
			name,
			new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null, kind, hostIds, OptionOrder: optionOrder),
			Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());
}
