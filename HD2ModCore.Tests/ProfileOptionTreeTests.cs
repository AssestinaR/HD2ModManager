using System.Reflection;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ProfileOptionTreeTests
{
    [Fact]
    public void ResolveEffectiveProfile_RecursesThroughLogicalOption_WithoutDeployingPlaceholder()
    {
        var rootId = ModNodeId.New();
        var presetId = ModNodeId.New();
        var simpleId = ModNodeId.New();
        var enhancedId = ModNodeId.New();
        var now = DateTimeOffset.UtcNow;
        var root = Node(rootId, "Root", ModNodeKind.Standard, [], []);
        var preset = Node(presetId, "Preset", ModNodeKind.Option, [rootId.Value.ToString("N")], []);
        var simple = Node(simpleId, "Simple", ModNodeKind.Option, [presetId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa3", 0)], optionOrder: 0);
        var enhanced = Node(enhancedId, "Enhanced", ModNodeKind.Option, [presetId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa4", 0)], optionOrder: 1);
        var snapshot = new LibrarySnapshot(
            1,
            now,
            new Dictionary<ModNodeId, ModNode>
            {
                [rootId] = root,
                [presetId] = preset,
                [simpleId] = simple,
                [enhancedId] = enhanced,
            },
            Array.Empty<Profile>());
        var profile = new Profile(ProfileId.New(), "Active", now, null, [new ProfileEntry(rootId, 0)]);
        var states = new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>
        {
            [presetId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [rootId.Value.ToString("N")] = new(true, null),
            },
            [simpleId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [presetId.Value.ToString("N")] = new(true, null),
            },
            [enhancedId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [presetId.Value.ToString("N")] = new(true, null),
            },
        });

        var effective = Resolve(profile, snapshot, states);

        Assert.Equal([rootId, simpleId, enhancedId], effective.Entries.Select(entry => entry.NodeId).ToArray());
        Assert.Equal([0, 1, 2], effective.Entries.Select(entry => entry.LoadOrder).ToArray());
    }

    [Fact]
    public async Task OptionActivationStore_PersistsExplicitDisabledState()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "option-activations.json");
            var optionId = Guid.NewGuid().ToString("N");
            var hostId = Guid.NewGuid().ToString("N");
            var store = new OptionActivationStore(path);

            await store.InitializeMissingStatesAsync([new OptionActivationInitialization(optionId, hostId, false)]);

            Assert.True(store.HasState(optionId, hostId));
            Assert.False(store.CreateSnapshot().IsEnabled(optionId, hostId));
            var reloaded = new OptionActivationStore(path);
            Assert.True(reloaded.HasState(optionId, hostId));
            Assert.False(reloaded.CreateSnapshot().IsEnabled(optionId, hostId));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task OptionActivationStore_BatchOrder_PreservesEnabledStateAndRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "option-activations.json");
            var firstOptionId = Guid.NewGuid().ToString("N");
            var secondOptionId = Guid.NewGuid().ToString("N");
            var hostId = Guid.NewGuid().ToString("N");
            var store = new OptionActivationStore(path);
            await store.InitializeMissingStatesAsync(
            [
                new OptionActivationInitialization(firstOptionId, hostId, true),
                new OptionActivationInitialization(secondOptionId, hostId, false),
            ]);

            var changed = await store.SetEffectiveOrdersAsync(
            [
                new OptionEffectiveOrderMutation(firstOptionId, hostId, 1),
                new OptionEffectiveOrderMutation(secondOptionId, hostId, 0),
            ]);

            Assert.Equal(2, changed);
            var snapshot = store.CreateSnapshot();
            Assert.True(snapshot.IsEnabled(firstOptionId, hostId));
            Assert.False(snapshot.IsEnabled(secondOptionId, hostId));
            Assert.Equal(1, snapshot.GetEffectiveOrder(firstOptionId, hostId));
            Assert.Equal(0, snapshot.GetEffectiveOrder(secondOptionId, hostId));

            var reloaded = new OptionActivationStore(path).CreateSnapshot();
            Assert.True(reloaded.IsEnabled(firstOptionId, hostId));
            Assert.False(reloaded.IsEnabled(secondOptionId, hostId));
            Assert.Equal(1, reloaded.GetEffectiveOrder(firstOptionId, hostId));
            Assert.Equal(0, reloaded.GetEffectiveOrder(secondOptionId, hostId));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task OptionActivationStore_ReadOnlySnapshot_PreservesStateWithoutRewritingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "option-activations.json");
            var optionId = Guid.NewGuid().ToString("N");
            var hostId = Guid.NewGuid().ToString("N");
            var store = new OptionActivationStore(path);
            await store.InitializeMissingStatesAsync([new OptionActivationInitialization(optionId, hostId, true, 3)]);
            var before = await File.ReadAllTextAsync(path);

            var snapshot = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path);

            Assert.True(snapshot.IsEnabled(optionId, hostId));
            Assert.Equal(3, snapshot.GetEffectiveOrder(optionId, hostId));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task OptionActivationStore_ReadOnlySnapshot_RejectsMalformedData()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "option-activations.json");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(path, "{ invalid");

            await Assert.ThrowsAsync<InvalidDataException>(async () => await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path).AsTask());
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ResolveEffectiveProfile_UsesHostScopedEffectiveOptionOrder()
    {
        var rootId = ModNodeId.New();
        var firstOptionId = ModNodeId.New();
        var secondOptionId = ModNodeId.New();
        var now = DateTimeOffset.UtcNow;
        var root = Node(rootId, "Root", ModNodeKind.Standard, [], []);
        var first = Node(firstOptionId, "First", ModNodeKind.Option, [rootId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa3", 0)]);
        var second = Node(secondOptionId, "Second", ModNodeKind.Option, [rootId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa4", 0)]);
        var snapshot = new LibrarySnapshot(
            1,
            now,
            new Dictionary<ModNodeId, ModNode>
            {
                [rootId] = root,
                [firstOptionId] = first,
                [secondOptionId] = second,
            },
            Array.Empty<Profile>());
        var profile = new Profile(ProfileId.New(), "Active", now, null, [new ProfileEntry(rootId, 0)]);
        var parentKey = rootId.Value.ToString("N");
        var states = new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>
        {
            [firstOptionId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [parentKey] = new(true, 1),
            },
            [secondOptionId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [parentKey] = new(true, 0),
            },
        });

        var effective = Resolve(profile, snapshot, states);

        Assert.Equal([rootId, secondOptionId, firstOptionId], effective.Entries.Select(entry => entry.NodeId).ToArray());
    }

    [Fact]
    public void ResolveActive_MultiParentOption_SeparatesCurrentRouteFromOtherEffectiveRoute()
    {
        var rootAId = ModNodeId.New();
        var rootBId = ModNodeId.New();
        var sharedOptionId = ModNodeId.New();
        var now = DateTimeOffset.UtcNow;
        var rootA = Node(rootAId, "Root A", ModNodeKind.Standard, [], [new PatchGroupKey("9ba626afa44a3aa1", 0)]);
        var rootB = Node(rootBId, "Root B", ModNodeKind.Standard, [], [new PatchGroupKey("9ba626afa44a3aa2", 0)]);
        var shared = Node(sharedOptionId, "Shared", ModNodeKind.Option,
            [rootAId.Value.ToString("N"), rootBId.Value.ToString("N")],
            [new PatchGroupKey("9ba626afa44a3aa3", 0)]);
        var profile = new Profile(ProfileId.New(), "Active", now, null,
        [
            new ProfileEntry(rootAId, 0, now),
            new ProfileEntry(rootBId, 1, now),
        ]);
        var snapshot = new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode>
        {
            [rootAId] = rootA,
            [rootBId] = rootB,
            [sharedOptionId] = shared,
        }, [profile], profile.Id);
        var states = new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>
        {
            [sharedOptionId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [rootAId.Value.ToString("N")] = new(false, 0),
                [rootBId.Value.ToString("N")] = new(true, 0),
            },
        });

        var routes = ProfileOptionRouteResolver.ResolveActive(snapshot, states);

        Assert.True(routes.IsNodeEffectivelyDeployed(sharedOptionId));
        Assert.False(routes.IsRelationReachable(rootAId, sharedOptionId));
        Assert.True(routes.IsRelationReachable(rootBId, sharedOptionId));
        Assert.Equal(1, routes.GetReachableParentCount(sharedOptionId));
    }

    [Fact]
    public void ResolveActive_MultiParentOption_EnabledThroughBothRoutes_DeploysNodeOnlyOnce()
    {
        var rootAId = ModNodeId.New();
        var rootBId = ModNodeId.New();
        var sharedOptionId = ModNodeId.New();
        var now = DateTimeOffset.UtcNow;
        var rootA = Node(rootAId, "Root A", ModNodeKind.Standard, [], [new PatchGroupKey("9ba626afa44a3aa1", 0)]);
        var rootB = Node(rootBId, "Root B", ModNodeKind.Standard, [], [new PatchGroupKey("9ba626afa44a3aa2", 0)]);
        var shared = Node(sharedOptionId, "Shared", ModNodeKind.Option,
            [rootAId.Value.ToString("N"), rootBId.Value.ToString("N")],
            [new PatchGroupKey("9ba626afa44a3aa3", 0)]);
        var profile = new Profile(ProfileId.New(), "Active", now, null,
        [
            new ProfileEntry(rootAId, 0, now),
            new ProfileEntry(rootBId, 1, now),
        ]);
        var snapshot = new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode>
        {
            [rootAId] = rootA,
            [rootBId] = rootB,
            [sharedOptionId] = shared,
        }, [profile], profile.Id);
        var states = new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>
        {
            [sharedOptionId.Value.ToString("N")] = new Dictionary<string, OptionHostState>
            {
                [rootAId.Value.ToString("N")] = new(true, 0),
                [rootBId.Value.ToString("N")] = new(true, 0),
            },
        });

        var routes = ProfileOptionRouteResolver.ResolveActive(snapshot, states);

        Assert.True(routes.IsNodeEffectivelyDeployed(sharedOptionId));
        Assert.True(routes.IsRelationReachable(rootAId, sharedOptionId));
        Assert.True(routes.IsRelationReachable(rootBId, sharedOptionId));
        Assert.Equal(2, routes.GetReachableParentCount(sharedOptionId));
        Assert.Equal(1, routes.EffectiveProfile!.Entries.Count(entry => entry.NodeId == sharedOptionId));
    }

    private static ModNode Node(
        ModNodeId id,
        string name,
        ModNodeKind kind,
        IReadOnlyList<string> hosts,
        IReadOnlyList<PatchGroupKey> patchGroups,
        int? optionOrder = null)
        => new(
            id,
            id.Value.ToString("N"),
            new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null, kind, hosts, OptionOrder: optionOrder),
            patchGroups,
            Array.Empty<ModNodeId>());

    private static Profile Resolve(Profile profile, LibrarySnapshot snapshot, OptionActivationSnapshot states)
    {
        var method = typeof(ProfileApplyService).GetMethod("ResolveEffectiveProfile", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<Profile>(method!.Invoke(null, [profile, snapshot, states]));
    }
}
