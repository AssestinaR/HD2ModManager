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
        var simple = Node(simpleId, "Simple", ModNodeKind.Option, [presetId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa3", 0)]);
        var enhanced = Node(enhancedId, "Enhanced", ModNodeKind.Option, [presetId.Value.ToString("N")], [new PatchGroupKey("9ba626afa44a3aa4", 0)]);
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

    private static ModNode Node(
        ModNodeId id,
        string name,
        ModNodeKind kind,
        IReadOnlyList<string> hosts,
        IReadOnlyList<PatchGroupKey> patchGroups)
        => new(
            id,
            id.Value.ToString("N"),
            new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null, kind, hosts),
            patchGroups,
            Array.Empty<ModNodeId>());

    private static Profile Resolve(Profile profile, LibrarySnapshot snapshot, OptionActivationSnapshot states)
    {
        var method = typeof(ProfileApplyService).GetMethod("ResolveEffectiveProfile", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<Profile>(method!.Invoke(null, [profile, snapshot, states]));
    }
}
