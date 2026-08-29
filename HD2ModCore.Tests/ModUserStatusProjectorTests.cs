using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies player-facing status projection consumes prebuilt facts without re-reading files.
public sealed class ModUserStatusProjectorTests
{
    [Fact]
    public void Project_CurrentMaterialMissingDiagnostic_TakesPriorityOverEnabledStatus()
    {
        var node = CreateNode(ModNodeId.New());
        var profile = new Profile(ProfileId.New(), "Profile", DateTimeOffset.UtcNow, null, [new ProfileEntry(node.Id, 0)], Revision: 3);
        var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [node.Id] = node }, [profile], profile.Id);
        var diagnostic = new ProfileMaterialDiagnostic(node.Id, new AssetKey(0xeac0b497876adedf, 1), ProfileMaterialDiagnosticKind.MissingMaterial, "无可用材质", "测试");
        var diagnostics = new ProfileMaterialDiagnostics(profile.Id, profile.Revision, DateTimeOffset.UtcNow, [diagnostic], []);

        var statuses = ModUserStatusProjector.Project(snapshot, profile.Id, new Dictionary<ModNodeId, ModContentFacts>(), null, diagnostics, null);

        Assert.Equal(ModUserStatusKind.MissingMaterial, statuses[node.Id].Kind);
        Assert.Equal("缺材质", statuses[node.Id].Title);
    }

    [Fact]
    public void Project_SelectedProfileOnly_ReturnsCurrentProfileWithoutTechnicalWork()
    {
        var nodeId = ModNodeId.New();
        var selected = new Profile(ProfileId.New(), "Editing", DateTimeOffset.UtcNow, null, [new ProfileEntry(nodeId, 0, DateTimeOffset.UtcNow)]);
        var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow,
            new Dictionary<ModNodeId, ModNode> { [nodeId] = CreateNode(nodeId) },
            [selected],
            null);

        var statuses = ModUserStatusProjector.Project(snapshot, selected.Id, new Dictionary<ModNodeId, ModContentFacts>(), null, null, null);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(ModUserStatusKind.Stored, status.Kind);
        Assert.True(status.IsInSelectedProfile);
        Assert.False(status.IsInActiveProfile);
    }

    [Fact]
    public void Project_ExpectedCoverage_ReturnsFullyOverridden()
    {
        var nodeId = ModNodeId.New();
        var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, [new ProfileEntry(nodeId, 0, DateTimeOffset.UtcNow)], Revision: 2);
        var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow,
            new Dictionary<ModNodeId, ModNode> { [nodeId] = CreateNode(nodeId) },
            [profile],
            profile.Id);
        var graph = new ProfileOverrideGraph(profile.Id, 2, "graph", "mapping", DateTimeOffset.UtcNow,
            new Dictionary<ModNodeId, string>(), [], [], [new ProfileModCoverage(nodeId, "Test", 1, 0, 1)], []);

        var statuses = ModUserStatusProjector.Project(snapshot, profile.Id, new Dictionary<ModNodeId, ModContentFacts>(), graph, null, null);

        Assert.Equal(ModUserStatusKind.Overridden, Assert.Single(statuses).Value.Kind);
    }

    [Fact]
    public void Project_StaleExpectedRevision_DoesNotProjectOldCoverage()
    {
        var nodeId = ModNodeId.New();
        var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, [new ProfileEntry(nodeId, 0, DateTimeOffset.UtcNow)], Revision: 3);
        var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow,
            new Dictionary<ModNodeId, ModNode> { [nodeId] = CreateNode(nodeId) },
            [profile],
            profile.Id);
        var staleGraph = new ProfileOverrideGraph(profile.Id, 2, "old", "mapping", DateTimeOffset.UtcNow,
            new Dictionary<ModNodeId, string>(), [], [], [new ProfileModCoverage(nodeId, "Test", 1, 0, 1)], []);

        var statuses = ModUserStatusProjector.Project(snapshot, profile.Id, new Dictionary<ModNodeId, ModContentFacts>(), staleGraph, null, null);

        Assert.Equal(ModUserStatusKind.Enabled, Assert.Single(statuses).Value.Kind);
    }

    private static ModNode CreateNode(ModNodeId id)
        => new(id, "test", new ModNodeMetadata("Test", null, DateTimeOffset.UtcNow, null), [], []);
}
