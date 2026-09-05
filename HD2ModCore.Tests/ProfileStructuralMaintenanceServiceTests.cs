using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证关系或删除后的 Profile 空结构清理只影响受影响分支。
// Purpose: Verifies relationship/deletion cleanup only prunes affected empty Profile branches.
public sealed class ProfileStructuralMaintenanceServiceTests
{
	private static readonly DateTimeOffset InitialTime = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public void CleanupAfterMutation_RemovesDeletedEmptyBranch_AndKeepsUnrelatedEmptyEntry()
	{
		var root = Node("空主体", "root", isLogicalContainer: true);
		var payloadChild = Node("实际 Patch", "child", ModNodeKind.Option, [Key(root.Id)], hasPatch: true);
		var unrelated = Node("未受影响的空条目", "unrelated", isLogicalContainer: true);
		var profile = Profile("默认", [
			new ProfileEntry(root.Id, 0, InitialTime),
			new ProfileEntry(unrelated.Id, 1, InitialTime),
		]);
		var before = Snapshot([root, payloadChild, unrelated], [profile]);
		var afterNodes = new Dictionary<ModNodeId, ModNode>(before.Nodes);
		afterNodes.Remove(payloadChild.Id);
		var after = before with { Nodes = afterNodes };

		var result = new ProfileStructuralMaintenanceService().CleanupAfterMutation(
			before,
			after,
			[payloadChild.Id],
			new HashSet<ModNodeId> { payloadChild.Id },
			InitialTime.AddMinutes(1));

		Assert.True(result.Changed);
		Assert.Equal([profile.Id], result.AffectedProfileIds);
		Assert.Equal([root.Id], result.RemovedEntryNodeIds);
		Assert.Equal([unrelated.Id], Entries(result.Snapshot, profile.Id));
		Assert.Equal(1, result.Snapshot.Profiles.Single().Revision);
	}

	[Fact]
	public void CleanupAfterMutation_RetainsAffectedRootWhenDescendantStillHasPayload()
	{
		var root = Node("逻辑主体", "root", isLogicalContainer: true);
		var child = Node("实际 Patch", "child", ModNodeKind.Option, [Key(root.Id)], hasPatch: true);
		var profile = Profile("默认", [new ProfileEntry(root.Id, 0, InitialTime)]);
		var snapshot = Snapshot([root, child], [profile]);

		var result = new ProfileStructuralMaintenanceService().CleanupAfterMutation(
			snapshot,
			snapshot,
			[root.Id],
			timestampUtc: InitialTime.AddMinutes(1));

		Assert.False(result.Changed);
		Assert.Same(snapshot, result.Snapshot);
		Assert.Equal([root.Id], Entries(result.Snapshot, profile.Id));
	}

	[Fact]
	public void CleanupAfterMutation_TreatsReShadeAsEffectivePayload()
	{
		var reshade = Node("ReShade", "reshade", ModNodeKind.ReShade, hasPatch: false);
		var profile = Profile("默认", [new ProfileEntry(reshade.Id, 0, InitialTime)]);
		var snapshot = Snapshot([reshade], [profile]);

		var result = new ProfileStructuralMaintenanceService().CleanupAfterMutation(
			snapshot,
			snapshot,
			[reshade.Id],
			timestampUtc: InitialTime.AddMinutes(1));

		Assert.False(result.Changed);
		Assert.Equal([reshade.Id], Entries(result.Snapshot, profile.Id));
	}

	private static ModNode Node(
		string name,
		string relativePath,
		ModNodeKind kind = ModNodeKind.Standard,
		IReadOnlyList<string>? hosts = null,
		bool hasPatch = false,
		bool isLogicalContainer = false)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(name, null, InitialTime, null, kind, hosts, IsLogicalContainer: isLogicalContainer),
			hasPatch ? [new PatchGroupKey("0123456789abcdef", 0)] : Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

	private static Profile Profile(string name, IReadOnlyList<ProfileEntry> entries)
		=> new(ProfileId.New(), name, InitialTime, InitialTime, entries);

	private static LibrarySnapshot Snapshot(IReadOnlyList<ModNode> nodes, IReadOnlyList<Profile> profiles)
		=> new(1, InitialTime, nodes.ToDictionary(node => node.Id), profiles);

	private static IReadOnlyList<ModNodeId> Entries(LibrarySnapshot snapshot, ProfileId profileId)
		=> snapshot.Profiles.Single(profile => profile.Id == profileId).Entries
			.OrderBy(entry => entry.LoadOrder)
			.Select(entry => entry.NodeId)
			.ToArray();

	private static string Key(ModNodeId id) => id.Value.ToString("N");
}
