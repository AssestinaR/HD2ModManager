using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：在关系或节点删除完成后，仅清理受影响的空 Profile 结构，不参与普通 Profile 编辑。
// Purpose: Prunes only affected empty Profile structures after relationship or node deletion mutations.
/// <summary>
/// UI- and storage-neutral maintenance for the structural side of Profiles.
/// A Profile entry is an intent/reference, so an empty Standard node is valid
/// during ordinary editing. This service is deliberately invoked only after a
/// relationship or deletion mutation, where an affected empty structure may be
/// safely reclaimed.
/// </summary>
public sealed class ProfileStructuralMaintenanceService
{
	public ProfileStructuralMaintenanceResult CleanupAfterMutation(
		LibrarySnapshot beforeSnapshot,
		LibrarySnapshot afterSnapshot,
		IReadOnlyCollection<ModNodeId> affectedNodeIds,
		IReadOnlySet<ModNodeId>? explicitlyRemovedNodeIds = null,
		DateTimeOffset? timestampUtc = null)
	{
		ArgumentNullException.ThrowIfNull(beforeSnapshot);
		ArgumentNullException.ThrowIfNull(afterSnapshot);
		ArgumentNullException.ThrowIfNull(affectedNodeIds);

		var seeds = affectedNodeIds.Distinct().ToArray();
		var explicitRemovals = explicitlyRemovedNodeIds ?? EmptyNodeSet;
		if (seeds.Length == 0 && explicitRemovals.Count == 0)
			return Unchanged(afterSnapshot);

		var beforeGraph = OptionRelationshipGraph.Create(beforeSnapshot);
		var afterGraph = OptionRelationshipGraph.Create(afterSnapshot);
		var affected = new HashSet<ModNodeId>(seeds);
		ExpandAncestors(beforeGraph, seeds, affected);
		ExpandAncestors(afterGraph, seeds, affected);

		var timestamp = timestampUtc ?? DateTimeOffset.UtcNow;
		var payloadMemo = new Dictionary<ModNodeId, bool>();
		var payloadVisiting = new HashSet<ModNodeId>();
		var profiles = afterSnapshot.Profiles.ToArray();
		var affectedProfiles = new List<ProfileId>();
		var removedEntryIds = new HashSet<ModNodeId>();

		for (var profileIndex = 0; profileIndex < profiles.Length; profileIndex++)
		{
			var profile = profiles[profileIndex];
			var orderedEntries = profile.Entries
				.OrderBy(entry => entry.LoadOrder)
				.ThenBy(entry => entry.AddedUtc)
				.ThenBy(entry => entry.NodeId.Value)
				.ToArray();
			var keptEntries = new List<ProfileEntry>(orderedEntries.Length);

			foreach (var entry in orderedEntries)
			{
				var explicitlyRemoved = explicitRemovals.Contains(entry.NodeId);
				var affectedEmptyNode = affected.Contains(entry.NodeId)
					&& !HasEffectivePayload(entry.NodeId, afterSnapshot, afterGraph, payloadMemo, payloadVisiting);
				if (explicitlyRemoved || affectedEmptyNode)
				{
					removedEntryIds.Add(entry.NodeId);
					continue;
				}

				keptEntries.Add(entry);
			}

			if (keptEntries.Count == orderedEntries.Length)
				continue;

			affectedProfiles.Add(profile.Id);
			profiles[profileIndex] = profile with
			{
				Entries = NormalizeEntries(keptEntries),
				ModifiedUtc = timestamp,
				Revision = checked(profile.Revision + 1),
			};
		}

		if (affectedProfiles.Count == 0)
			return Unchanged(afterSnapshot);

		var updated = afterSnapshot with
		{
			Profiles = profiles,
			SavedUtc = timestamp,
		};
		return new ProfileStructuralMaintenanceResult(
			updated,
			affectedProfiles.ToArray(),
			removedEntryIds.ToArray(),
			Changed: true);
	}

	private static void ExpandAncestors(
		OptionRelationshipGraph graph,
		IReadOnlyCollection<ModNodeId> seeds,
		ISet<ModNodeId> affected)
	{
		var pending = new Stack<ModNodeId>(seeds);
		var visited = new HashSet<ModNodeId>();
		while (pending.Count > 0)
		{
			var current = pending.Pop();
			if (!visited.Add(current))
				continue;

			foreach (var parentId in graph.GetParents(current))
			{
				if (affected.Add(parentId))
					pending.Push(parentId);
			}
		}
	}

	private static bool HasEffectivePayload(
		ModNodeId nodeId,
		LibrarySnapshot snapshot,
		OptionRelationshipGraph graph,
		IDictionary<ModNodeId, bool> memo,
		ISet<ModNodeId> visiting)
	{
		if (memo.TryGetValue(nodeId, out var cached))
			return cached;
		if (!snapshot.Nodes.TryGetValue(nodeId, out var node))
			return memo[nodeId] = false;

		// ReShade is an external deployment payload and intentionally has no
		// GameData PatchGroups of its own.
		if (node.Metadata.Kind == ModNodeKind.ReShade || node.PatchGroups.Count > 0)
			return memo[nodeId] = true;

		// A malformed legacy cycle must not make cleanup recurse forever. Such a
		// cycle has no payload at this point, so it can be reclaimed if affected.
		if (!visiting.Add(nodeId))
			return false;

		try
		{
			foreach (var child in graph.GetDirectOptions(nodeId))
			{
				if (HasEffectivePayload(child.Id, snapshot, graph, memo, visiting))
					return memo[nodeId] = true;
			}

			return memo[nodeId] = false;
		}
		finally
		{
			visiting.Remove(nodeId);
		}
	}

	private static IReadOnlyList<ProfileEntry> NormalizeEntries(IReadOnlyList<ProfileEntry> entries)
		=> entries.Select((entry, index) => entry with { LoadOrder = index }).ToArray();

	private static ProfileStructuralMaintenanceResult Unchanged(LibrarySnapshot snapshot)
		=> new(snapshot, Array.Empty<ProfileId>(), Array.Empty<ModNodeId>(), Changed: false);

	private static readonly IReadOnlySet<ModNodeId> EmptyNodeSet =
		new HashSet<ModNodeId>();
}

public sealed record ProfileStructuralMaintenanceResult(
	LibrarySnapshot Snapshot,
	IReadOnlyList<ProfileId> AffectedProfileIds,
	IReadOnlyList<ModNodeId> RemovedEntryNodeIds,
	bool Changed)
{
	public bool RemovedAnyEntries => RemovedEntryNodeIds.Count > 0;
}
