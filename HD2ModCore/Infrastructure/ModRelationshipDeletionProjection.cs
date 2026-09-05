using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Applies the relationship portion of a node deletion to Options that remain
/// in the library.  Option ownership is stored on the child, so removing a
/// host must also prune every surviving child's HostModGuids and restore a
/// child to a normal Mod when it loses its final host.
/// </summary>
public static class ModRelationshipDeletionProjection
{
	/// <summary>
	/// Returns only surviving Options whose persisted relationship metadata must
	/// change after <paramref name="removedNodeIds"/> disappear.  Callers own
	/// removal of the nodes themselves, Profile cleanup, and storage writes.
	/// </summary>
	public static IReadOnlyDictionary<ModNodeId, ModNode> GetSurvivingOptionUpdates(
		LibrarySnapshot snapshot,
		IReadOnlySet<ModNodeId> removedNodeIds,
		DateTimeOffset? timestampUtc = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(removedNodeIds);
		if (removedNodeIds.Count == 0)
			return new Dictionary<ModNodeId, ModNode>();

		var timestamp = timestampUtc ?? DateTimeOffset.UtcNow;
		var graph = OptionRelationshipGraph.Create(snapshot);
		var result = new Dictionary<ModNodeId, ModNode>();
		foreach (var node in snapshot.Nodes.Values.Where(node =>
			!removedNodeIds.Contains(node.Id)
			&& node.Metadata.Kind == ModNodeKind.Option))
		{
			var existingHosts = node.Metadata.HostModGuids ?? Array.Empty<string>();
			var remainingHosts = existingHosts
				.Where(rawHostId => !Guid.TryParse(rawHostId, out var hostGuid)
					|| !removedNodeIds.Contains(new ModNodeId(hostGuid)))
				.ToArray();
			if (remainingHosts.SequenceEqual(existingHosts, StringComparer.OrdinalIgnoreCase))
				continue;

			var becomesStandard = remainingHosts.Length == 0;
			var retainsLogicalContainer = node.PatchGroups.Count == 0
				&& (node.Metadata.IsLogicalContainer
					|| graph.GetDirectOptions(node.Id).Any(child => !removedNodeIds.Contains(child.Id)));
			var metadata = node.Metadata with
			{
				Kind = becomesStandard ? ModNodeKind.Standard : ModNodeKind.Option,
				HostModGuids = becomesStandard ? null : remainingHosts,
				OptionOrder = becomesStandard ? null : node.Metadata.OptionOrder,
				SuppressDefaultDirectOptionActivation = becomesStandard
					? false
					: node.Metadata.SuppressDefaultDirectOptionActivation,
				IsLogicalContainer = becomesStandard
					? retainsLogicalContainer
					: node.Metadata.IsLogicalContainer,
				ModifiedUtc = timestamp,
			};
			result[node.Id] = node with { Metadata = metadata };
		}

		return result;
	}

	/// <summary>
	/// Identifies metadata fields that are mirrored into the per-Mod storage
	/// protocol files.  A caller can avoid rewriting sidecars for unrelated
	/// changes such as legacy Children cleanup.
	/// </summary>
	public static bool RequiresStorageProtocolWrite(ModNode before, ModNode after)
	{
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);
		return before.Metadata.Kind != after.Metadata.Kind
			|| before.Metadata.IsLogicalContainer != after.Metadata.IsLogicalContainer
			|| before.Metadata.OptionOrder != after.Metadata.OptionOrder
			|| before.Metadata.SuppressDefaultDirectOptionActivation != after.Metadata.SuppressDefaultDirectOptionActivation
			|| !SequenceEqual(before.Metadata.HostModGuids, after.Metadata.HostModGuids);
	}

	private static bool SequenceEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
	{
		if (ReferenceEquals(left, right)) return true;
		if (left is null || right is null || left.Count != right.Count) return false;
		for (var index = 0; index < left.Count; index++)
			if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase)) return false;
		return true;
	}
}
