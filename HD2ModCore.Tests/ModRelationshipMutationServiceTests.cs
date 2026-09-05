using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ModRelationshipMutationServiceTests
{
	[Fact]
	public async Task CreateLogicalContainer_PersistsMarker_AndProfileExpansionUsesItOnlyAsAnchor()
	{
		await using var fixture = await Fixture.CreateAsync();
		var snapshot = fixture.EmptySnapshot();

		var created = await fixture.Service.CreateLogicalContainerAsync(snapshot, "角色替换文件夹", "可作为选项主体");

		Assert.True(created.Changed);
		Assert.True(created.Node.Metadata.IsLogicalContainer);
		Assert.Empty(created.Node.PatchGroups);
		var logicalDirectory = fixture.NodeDirectory(created.Node);
		Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(logicalDirectory)));

		var child = fixture.Node(
			"实际 Patch",
			"actual-patch",
			ModNodeKind.Option,
			[Key(created.Node.Id)],
			hasPatch: true);
		fixture.CreateNodeDirectory(child);
		var linked = created.Snapshot with
		{
			Nodes = new Dictionary<ModNodeId, ModNode>(created.Snapshot.Nodes)
			{
				[child.Id] = child,
			},
		};
		var profile = new Profile(ProfileId.New(), "测试", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
			[new ProfileEntry(created.Node.Id, 0, DateTimeOffset.UtcNow)]);
		var activations = new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>
		{
			[Key(child.Id)] = new Dictionary<string, OptionHostState>
			{
				[Key(created.Node.Id)] = new(true, 0),
			},
		});

		var effective = ProfileEffectiveProfileResolver.Resolve(profile, linked, activations);

		Assert.Equal([child.Id], effective.Entries.Select(entry => entry.NodeId).ToArray());
	}

	[Fact]
	public async Task AddAndRemoveOptionParents_ConvertsProfileMember_PreservesOtherParent_ThenRestoresStandard()
	{
		await using var fixture = await Fixture.CreateAsync();
		var hostA = fixture.Node("主体 A", "host-a", ModNodeKind.Standard, hasPatch: true);
		var hostB = fixture.Node("主体 B", "host-b", ModNodeKind.Standard, hasPatch: true);
		var source = fixture.Node("来源", "source", ModNodeKind.Standard, hasPatch: true);
		fixture.CreateNodeDirectory(hostA);
		fixture.CreateNodeDirectory(hostB);
		fixture.CreateNodeDirectory(source);
		var now = DateTimeOffset.UtcNow;
		var profile = new Profile(ProfileId.New(), "默认", now, now,
			[new ProfileEntry(source.Id, 0, now), new ProfileEntry(hostA.Id, 1, now)]);
		var snapshot = fixture.Snapshot([hostA, hostB, source], [profile]);
		await fixture.Store.SaveAsync(snapshot);

		var addedA = await fixture.Service.AddOptionParentsAsync(snapshot, hostA.Id, [source.Id]);
		var optionAfterA = addedA.Snapshot.Nodes[source.Id];
		Assert.Equal(ModNodeKind.Option, optionAfterA.Metadata.Kind);
		Assert.False(optionAfterA.Metadata.IsLogicalContainer);
		Assert.Equal([Key(hostA.Id)], optionAfterA.Metadata.HostModGuids);
		Assert.DoesNotContain(addedA.Snapshot.Profiles.Single().Entries, entry => entry.NodeId == source.Id);
		Assert.True(fixture.Activations.CreateSnapshot().IsEnabled(Key(source.Id), Key(hostA.Id)));
		Assert.True(File.Exists(ModNodeStorageProtocol.OptionRelationPath(fixture.NodeDirectory(source))));
		Assert.False(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(fixture.NodeDirectory(source))));

		var addedB = await fixture.Service.AddOptionParentsAsync(addedA.Snapshot, hostB.Id, [source.Id]);
		var shared = addedB.Snapshot.Nodes[source.Id];
		Assert.Equal(new[] { Key(hostA.Id), Key(hostB.Id) }.OrderBy(value => value), shared.Metadata.HostModGuids!.OrderBy(value => value));
		Assert.True(fixture.Activations.CreateSnapshot().IsEnabled(Key(source.Id), Key(hostB.Id)));

		var removedA = await fixture.Service.RemoveOptionParentsAsync(addedB.Snapshot, hostA.Id, [source.Id]);
		Assert.Equal(ModNodeKind.Option, removedA.Snapshot.Nodes[source.Id].Metadata.Kind);
		Assert.Equal([Key(hostB.Id)], removedA.Snapshot.Nodes[source.Id].Metadata.HostModGuids);
		Assert.False(fixture.Activations.CreateSnapshot().HasState(Key(source.Id), Key(hostA.Id)));
		Assert.True(fixture.Activations.CreateSnapshot().HasState(Key(source.Id), Key(hostB.Id)));

		var removedB = await fixture.Service.RemoveOptionParentsAsync(removedA.Snapshot, hostB.Id, [source.Id]);
		var restored = removedB.Snapshot.Nodes[source.Id];
		Assert.Equal(ModNodeKind.Standard, restored.Metadata.Kind);
		Assert.Null(restored.Metadata.HostModGuids);
		Assert.False(restored.Metadata.IsLogicalContainer);
		Assert.False(fixture.Activations.CreateSnapshot().HasState(Key(source.Id), Key(hostB.Id)));
		Assert.False(File.Exists(ModNodeStorageProtocol.OptionRelationPath(fixture.NodeDirectory(source))));
		Assert.False(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(fixture.NodeDirectory(source))));
		Assert.DoesNotContain(removedB.Snapshot.Profiles.Single().Entries, entry => entry.NodeId == source.Id);
	}

	[Fact]
	public async Task RemoveOptionParent_PrunesEmptyHostFromProfile()
	{
		await using var fixture = await Fixture.CreateAsync();
		var host = fixture.Node("空主体", "empty-host", ModNodeKind.Standard, hasPatch: false) with
		{
			Metadata = fixture.Node("空主体", "empty-host-metadata", ModNodeKind.Standard).Metadata with
			{
				IsLogicalContainer = true,
			},
		};
		var source = fixture.Node("空选项", "empty-option", ModNodeKind.Option, [Key(host.Id)], hasPatch: false);
		fixture.CreateNodeDirectory(host);
		fixture.CreateNodeDirectory(source);
		var now = DateTimeOffset.UtcNow;
		var profile = new Profile(ProfileId.New(), "默认", now, now, [new ProfileEntry(host.Id, 0, now)]);
		var snapshot = fixture.Snapshot([host, source], [profile]);
		await fixture.Store.SaveAsync(snapshot);

		var result = await fixture.Service.RemoveOptionParentsAsync(snapshot, host.Id, [source.Id]);

		Assert.True(result.Changed);
		Assert.Empty(result.Snapshot.Profiles.Single().Entries);
		Assert.Equal(ModNodeKind.Standard, result.Snapshot.Nodes[source.Id].Metadata.Kind);
	}

	[Fact]
	public async Task AddOptionParents_RejectsCycleBeforeWritingSidecarsOrSnapshot()
	{
		await using var fixture = await Fixture.CreateAsync();
		var root = fixture.Node("根", "root", ModNodeKind.Standard, hasPatch: true);
		var alpha = fixture.Node("Alpha", "alpha", ModNodeKind.Option, [Key(root.Id)], hasPatch: true);
		var beta = fixture.Node("Beta", "beta", ModNodeKind.Option, [Key(alpha.Id)], hasPatch: true);
		fixture.CreateNodeDirectory(root);
		fixture.CreateNodeDirectory(alpha);
		fixture.CreateNodeDirectory(beta);
		ModNodeStorageProtocol.WriteOptionRelation(fixture.NodeDirectory(alpha), alpha.Metadata);
		ModNodeStorageProtocol.WriteOptionRelation(fixture.NodeDirectory(beta), beta.Metadata);
		var snapshot = fixture.Snapshot([root, alpha, beta]);
		await fixture.Store.SaveAsync(snapshot);
		var libraryBefore = await File.ReadAllBytesAsync(fixture.Paths.LibraryPath);
		var alphaBefore = await File.ReadAllBytesAsync(ModNodeStorageProtocol.OptionRelationPath(fixture.NodeDirectory(alpha)));

		var exception = await Assert.ThrowsAsync<ModRelationshipMutationException>(
			async () => await fixture.Service.AddOptionParentsAsync(snapshot, beta.Id, [alpha.Id]).AsTask());

		Assert.Equal(ModRelationshipMutationFailure.CycleDetected, exception.Failure);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.Paths.LibraryPath));
		Assert.Equal(alphaBefore, await File.ReadAllBytesAsync(ModNodeStorageProtocol.OptionRelationPath(fixture.NodeDirectory(alpha))));
		Assert.False(fixture.Activations.CreateSnapshot().HasState(Key(alpha.Id), Key(beta.Id)));
	}

	private static string Key(ModNodeId id) => id.Value.ToString("N");

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, StoragePaths paths)
		{
			RootDirectory = rootDirectory;
			Paths = paths;
			Store = new JsonModLibraryStore(paths, allowLegacyMigration: false);
			Activations = new OptionActivationStore(Path.Combine(paths.ModsDirectory, "option-activations.json"));
			Service = new ModRelationshipMutationService(paths, Store, Activations);
		}

		public string RootDirectory { get; }
		public StoragePaths Paths { get; }
		public JsonModLibraryStore Store { get; }
		public OptionActivationStore Activations { get; }
		public ModRelationshipMutationService Service { get; }

		public static ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-mod-relationship-" + Guid.NewGuid().ToString("N"));
			var manager = Path.Combine(root, "manager");
			var library = Path.Combine(root, "library");
			Directory.CreateDirectory(manager);
			Directory.CreateDirectory(library);
			return ValueTask.FromResult(new Fixture(root, new StoragePaths(manager, library)));
		}

		public LibrarySnapshot EmptySnapshot()
			=> new(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>());

		public LibrarySnapshot Snapshot(IReadOnlyList<ModNode> nodes, IReadOnlyList<Profile>? profiles = null)
			=> new(1, DateTimeOffset.UtcNow, nodes.ToDictionary(node => node.Id), profiles ?? Array.Empty<Profile>());

		public ModNode Node(
			string name,
			string relativePath,
			ModNodeKind kind,
			IReadOnlyList<string>? hosts = null,
			bool hasPatch = false)
			=> new(
				ModNodeId.New(),
				relativePath,
				new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null, kind, hosts),
				hasPatch ? [new PatchGroupKey("0123456789abcdef", 0)] : Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());

		public string NodeDirectory(ModNode node) => Path.Combine(Paths.ModsDirectory, node.RelativePath);

		public void CreateNodeDirectory(ModNode node)
		{
			var directory = NodeDirectory(node);
			Directory.CreateDirectory(directory);
			foreach (var patch in node.PatchGroups)
				File.WriteAllBytes(Path.Combine(directory, $"{patch.ArchiveHex16}.patch_{patch.PatchIndex}"), [0x01]);
		}

		public ValueTask DisposeAsync()
		{
			try
			{
				if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
			}
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
