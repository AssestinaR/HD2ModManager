using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Computes and persists missing host-scoped Option activation state after a
	/// package tree enters a library. The rule is intentionally structural rather
	/// than UI-specific: direct Options of a standard Mod start enabled, while the
	/// direct children of an Option start with only their first sibling enabled.
	/// A manifest path-conflict parent can suppress only its own direct defaults;
	/// existing explicit user state is never overwritten.
/// </summary>
public sealed class OptionActivationDefaultInitializer
{
	public IReadOnlyList<OptionActivationInitialization> PlanMissingStates(
		LibrarySnapshot snapshot,
		OptionActivationSnapshot existingStates,
		IEnumerable<ModNodeId> affectedNodeIds,
		bool enableDefaults = true,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(existingStates);
		ArgumentNullException.ThrowIfNull(affectedNodeIds);

		var graph = OptionRelationshipGraph.Create(snapshot);
		var startParents = new HashSet<ModNodeId>();
		foreach (var nodeId in affectedNodeIds.Distinct())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(nodeId, out var node)) continue;
			if (node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option)
				startParents.Add(nodeId);
			foreach (var parentId in graph.GetParents(nodeId)) startParents.Add(parentId);
		}
		if (startParents.Count == 0) return Array.Empty<OptionActivationInitialization>();

		var defaults = new List<OptionActivationInitialization>();
		var traversedEdges = new HashSet<(ModNodeId Parent, ModNodeId Child)>();
		foreach (var parentId in startParents)
			PlanDefaults(parentId, new HashSet<ModNodeId> { parentId });
		return defaults;

		void PlanDefaults(ModNodeId parentId, HashSet<ModNodeId> ancestors)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(parentId, out var parent)) return;
			var children = graph.GetDirectOptions(parentId);
			var suppressDirectDefaults = parent.Metadata.SuppressDefaultDirectOptionActivation;
			// Virtual-reference Manifest imports use an empty logical Option as a
			// relationship hub. Every physical Include child is required for that
			// logical choice, unlike ordinary Option siblings where only the first
			// choice is enabled by default.
			var enableAllChildren = parent.Metadata.Kind == ModNodeKind.Standard
				|| parent.Metadata.IsLogicalContainer;
			for (var index = 0; index < children.Count; index++)
			{
				var child = children[index];
				if (!traversedEdges.Add((parentId, child.Id))) continue;
				var parentKey = parentId.Value.ToString("N");
				var childKey = child.Id.Value.ToString("N");
				if (!existingStates.HasState(childKey, parentKey))
					defaults.Add(new OptionActivationInitialization(
						childKey,
						parentKey,
						enableDefaults && !suppressDirectDefaults && (enableAllChildren || index == 0)));
				if (!ancestors.Add(child.Id)) continue;
				PlanDefaults(child.Id, ancestors);
				ancestors.Remove(child.Id);
			}
		}
	}

	public async ValueTask<int> InitializeMissingStatesAsync(
		LibrarySnapshot snapshot,
		OptionActivationStore store,
		IEnumerable<ModNodeId> affectedNodeIds,
		bool enableDefaults = true,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		var defaults = PlanMissingStates(snapshot, store.CreateSnapshot(), affectedNodeIds, enableDefaults, cancellationToken);
		return await store.InitializeMissingStatesAsync(defaults, cancellationToken).ConfigureAwait(false);
	}
}
