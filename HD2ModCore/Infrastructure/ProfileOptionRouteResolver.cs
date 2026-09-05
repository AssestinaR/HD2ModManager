using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Resolves the enabled Option routes below one Profile without changing its
/// logical entry list. Deployment, UI status and diagnostics can use this to
/// distinguish “this relation is enabled” from “this physical Option is
/// already reached through another parent”.
/// </summary>
public static class ProfileOptionRouteResolver
{
	public static ProfileOptionRouteResolution ResolveActive(
		LibrarySnapshot snapshot,
		OptionActivationSnapshot optionStates)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionStates);
		var active = snapshot.ActiveProfileId is { } activeId
			? snapshot.Profiles.FirstOrDefault(profile => profile.Id == activeId)
			: null;
		return active is null
			? ProfileOptionRouteResolution.Empty
			: Resolve(active, snapshot, optionStates);
	}

	public static ProfileOptionRouteResolution Resolve(
		Profile profile,
		LibrarySnapshot snapshot,
		OptionActivationSnapshot optionStates)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionStates);

		var graph = OptionRelationshipGraph.Create(snapshot);
		var reachableNodes = new HashSet<ModNodeId>();
		var reachableRelations = new HashSet<ProfileOptionRoute>();
		foreach (var entry in profile.Entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value))
		{
			if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node)) continue;
			reachableNodes.Add(node.Id);
			if (node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option)
				Visit(node.Id, new HashSet<ModNodeId> { node.Id });
		}

		var effective = ProfileEffectiveProfileResolver.Resolve(profile, snapshot, optionStates);
		return new ProfileOptionRouteResolution(
			profile,
			effective,
			reachableNodes,
			reachableRelations);

		void Visit(ModNodeId parentId, HashSet<ModNodeId> ancestors)
		{
			var parentKey = parentId.Value.ToString("N");
			foreach (var child in graph.GetDirectOptions(parentId))
			{
				if (!optionStates.IsEnabled(child.Id.Value.ToString("N"), parentKey)) continue;
				reachableRelations.Add(new ProfileOptionRoute(parentId, child.Id));
				reachableNodes.Add(child.Id);
				if (!ancestors.Add(child.Id)) continue;
				Visit(child.Id, ancestors);
				ancestors.Remove(child.Id);
			}
		}
	}
}

public readonly record struct ProfileOptionRoute(ModNodeId HostId, ModNodeId OptionId);

public sealed record ProfileOptionRouteResolution(
	Profile? Profile,
	Profile? EffectiveProfile,
	IReadOnlySet<ModNodeId> ReachableNodeIds,
	IReadOnlySet<ProfileOptionRoute> ReachableRelations)
{
	public static ProfileOptionRouteResolution Empty { get; } = new(
		null,
		null,
		new HashSet<ModNodeId>(),
		new HashSet<ProfileOptionRoute>());

	public bool HasActiveProfile => Profile is not null;

	public bool IsNodeReachable(ModNodeId nodeId)
		=> ReachableNodeIds.Contains(nodeId);

	public bool IsNodeEffectivelyDeployed(ModNodeId nodeId)
		=> EffectiveProfile?.Entries.Any(entry => entry.NodeId == nodeId) == true;

	public bool IsRelationReachable(ModNodeId hostId, ModNodeId optionId)
		=> ReachableRelations.Contains(new ProfileOptionRoute(hostId, optionId));

	public int GetReachableParentCount(ModNodeId optionId)
		=> ReachableRelations.Count(route => route.OptionId == optionId);
}
