using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Computes a conservative deletion projection from the library's real Option
/// ownership graph. This service deliberately has no store, filesystem, UI, or
/// deployment dependency: it tells a later executor exactly what it would need
/// to change, while refusing to hide unsafe generated-content situations.
/// </summary>
public sealed class SafeModDeletionPlanningService : ISafeModDeletionPlanningService
{
	public SafeModDeletionPlan Plan(
		LibrarySnapshot snapshot,
		IReadOnlyCollection<ModNodeId> requestedNodeIds,
		SafeModDeletionScope scope,
		SafeModDeletionSafetySnapshot? safetySnapshot = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(requestedNodeIds);
		cancellationToken.ThrowIfCancellationRequested();

		var blockers = new BlockerCollector();
		var requestCounts = new Dictionary<ModNodeId, int>();
		var requestedOrder = new List<ModNodeId>();
		foreach (var nodeId in requestedNodeIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (requestCounts.TryGetValue(nodeId, out var count))
			{
				requestCounts[nodeId] = checked(count + 1);
				continue;
			}
			requestCounts[nodeId] = 1;
			requestedOrder.Add(nodeId);
		}

		if (requestedOrder.Count == 0)
		{
			blockers.Add(
				"EmptyDeletionRequest",
				SafeModDeletionBlockerSeverity.Blocking,
				"A safe Mod deletion plan requires at least one requested library node.");
		}

		var requestedExisting = new HashSet<ModNodeId>();
		foreach (var nodeId in requestedOrder)
		{
			if (snapshot.Nodes.ContainsKey(nodeId))
			{
				requestedExisting.Add(nodeId);
				continue;
			}
			blockers.Add(
				"RequestedNodeMissing",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Requested library node '{ToKey(nodeId)}' does not exist in the supplied snapshot.",
				[nodeId]);
		}

		var graph = OptionRelationshipGraph.Create(snapshot);
		var traversal = AnalyzeReachableOptions(snapshot, graph, requestedExisting, cancellationToken);
		foreach (var cycle in traversal.Cycles)
		{
			blockers.Add(
				"OptionRelationshipCycle",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Option ownership contains a cycle reachable from the deletion request: {string.Join(" -> ", cycle.Select(ToKey))}.",
				cycle);
		}

		var malformedHosts = FindMalformedHostReferences(snapshot, traversal.ReachableOptionIds, cancellationToken);
		foreach (var malformed in malformedHosts)
		{
			blockers.Add(
				"InvalidOptionHostReference",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Option '{ToKey(malformed.OptionId)}' has an unsafe HostModGuids entry '{malformed.RawHostId}'. {malformed.Detail}",
				[malformed.OptionId]);
		}
		var optionsWithMalformedHosts = malformedHosts.Select(item => item.OptionId).ToHashSet();

		var removalReasons = new Dictionary<ModNodeId, SafeModDeletionRemovalReason>();
		var removalOrder = new List<ModNodeId>();
		foreach (var requestedId in requestedOrder)
		{
			if (!requestedExisting.Contains(requestedId)) continue;
			AddRemoval(requestedId, SafeModDeletionRemovalReason.Requested);
		}

		// A reachable ownership cycle makes automatic descendant expansion
		// ambiguous. Preserve all non-requested Options and expose the blocker
		// instead of guessing which part of the malformed graph should disappear.
		if (scope == SafeModDeletionScope.WithUnsharedOptions && traversal.Cycles.Count == 0)
		{
			var pendingParents = new Queue<ModNodeId>(removalOrder);
			while (pendingParents.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var removedParentId = pendingParents.Dequeue();
				foreach (var child in graph.GetDirectOptions(removedParentId))
				{
					if (removalReasons.ContainsKey(child.Id)) continue;
					if (optionsWithMalformedHosts.Contains(child.Id)) continue;
					var parents = graph.GetParents(child.Id);
					if (parents.Count == 0 || parents.Any(parentId => !removalReasons.ContainsKey(parentId))) continue;

					AddRemoval(child.Id, SafeModDeletionRemovalReason.UnsharedOptionDescendant);
					pendingParents.Enqueue(child.Id);
				}
			}
		}

		var removalIds = removalReasons.Keys.ToHashSet();
		var requestedNodes = requestedOrder
			.Select(nodeId => new SafeModDeletionRequestedNode(
				nodeId,
				snapshot.Nodes.ContainsKey(nodeId),
				removalIds.Contains(nodeId),
				requestCounts[nodeId]))
			.ToArray();
		var libraryNodeRemovals = removalOrder
			.Select(nodeId => ToRemoval(snapshot.Nodes[nodeId], removalReasons[nodeId]))
			.ToArray();
		var nonSharedDescendantRemovals = libraryNodeRemovals
			.Where(removal => removal.Reason == SafeModDeletionRemovalReason.UnsharedOptionDescendant)
			.ToArray();

		var relationRemovals = BuildSurvivingOptionHostRelationRemovals(snapshot, graph, removalIds, cancellationToken);
		var retainedShared = scope == SafeModDeletionScope.WithUnsharedOptions
			? BuildSharedOptionRetentions(snapshot, relationRemovals)
			: Array.Empty<SafeModDeletionSharedOptionRetention>();
		var profileCleanups = BuildProfileCleanups(snapshot, removalIds, cancellationToken);
		var activationCleanups = BuildActivationCleanups(snapshot, libraryNodeRemovals, relationRemovals);
		var physicalPaths = BuildPhysicalPaths(snapshot, libraryNodeRemovals, removalIds, blockers, cancellationToken);

		ApplyGeneratedContentSafety(
			snapshot,
			removalIds,
			safetySnapshot ?? SafeModDeletionSafetySnapshot.Unknown,
			blockers,
			cancellationToken);

		return new SafeModDeletionPlan(
			scope,
			requestedNodes,
			libraryNodeRemovals,
			relationRemovals,
			nonSharedDescendantRemovals,
			retainedShared,
			profileCleanups,
			activationCleanups,
			physicalPaths,
			blockers.ToArray());

		void AddRemoval(ModNodeId nodeId, SafeModDeletionRemovalReason reason)
		{
			if (!removalReasons.TryAdd(nodeId, reason)) return;
			removalOrder.Add(nodeId);
		}
	}

	private static RelationshipTraversal AnalyzeReachableOptions(
		LibrarySnapshot snapshot,
		OptionRelationshipGraph graph,
		IReadOnlySet<ModNodeId> roots,
		CancellationToken cancellationToken)
	{
		var reachableOptions = new HashSet<ModNodeId>();
		var cycles = new List<IReadOnlyList<ModNodeId>>();
		var emittedCycles = new HashSet<string>(StringComparer.Ordinal);
		var states = new Dictionary<ModNodeId, VisitState>();
		var path = new List<ModNodeId>();
		var pathIndices = new Dictionary<ModNodeId, int>();

		foreach (var rootId in roots.OrderBy(id => id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(rootId, out var root)
				|| root.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
			{
				continue;
			}
			if (root.Metadata.Kind == ModNodeKind.Option) reachableOptions.Add(rootId);
			if (states.ContainsKey(rootId)) continue;

			var stack = new Stack<TraversalFrame>();
			Begin(rootId);
			stack.Push(new TraversalFrame(rootId, graph.GetDirectOptions(rootId), 0));
			while (stack.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var frame = stack.Pop();
				if (frame.NextChildIndex >= frame.Children.Count)
				{
					states[frame.NodeId] = VisitState.Visited;
					pathIndices.Remove(frame.NodeId);
					if (path.Count > 0 && path[^1] == frame.NodeId) path.RemoveAt(path.Count - 1);
					continue;
				}

				var child = frame.Children[frame.NextChildIndex];
				stack.Push(frame with { NextChildIndex = frame.NextChildIndex + 1 });
				reachableOptions.Add(child.Id);
				if (states.TryGetValue(child.Id, out var childState))
				{
					if (childState == VisitState.Visiting && pathIndices.TryGetValue(child.Id, out var cycleStart))
					{
						var cycle = path.Skip(cycleStart).Append(child.Id).ToArray();
						var cycleKey = string.Join("/", cycle.Select(ToKey));
						if (emittedCycles.Add(cycleKey)) cycles.Add(cycle);
					}
					continue;
				}

				Begin(child.Id);
				stack.Push(new TraversalFrame(child.Id, graph.GetDirectOptions(child.Id), 0));
			}
		}

		return new RelationshipTraversal(reachableOptions, cycles);

		void Begin(ModNodeId nodeId)
		{
			states[nodeId] = VisitState.Visiting;
			pathIndices[nodeId] = path.Count;
			path.Add(nodeId);
		}
	}

	private static IReadOnlyList<MalformedHostReference> FindMalformedHostReferences(
		LibrarySnapshot snapshot,
		IReadOnlySet<ModNodeId> relevantOptionIds,
		CancellationToken cancellationToken)
	{
		var result = new List<MalformedHostReference>();
		foreach (var optionId in relevantOptionIds.OrderBy(id => id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(optionId, out var option) || option.Metadata.Kind != ModNodeKind.Option) continue;
			foreach (var rawHostId in option.Metadata.HostModGuids ?? Array.Empty<string>())
			{
				if (string.IsNullOrWhiteSpace(rawHostId))
				{
					result.Add(new MalformedHostReference(optionId, rawHostId ?? string.Empty, "The value is empty."));
					continue;
				}
				if (!Guid.TryParse(rawHostId, out var parsedHostId))
				{
					result.Add(new MalformedHostReference(optionId, rawHostId, "The value is not a GUID."));
					continue;
				}
				var hostId = new ModNodeId(parsedHostId);
				if (!snapshot.Nodes.TryGetValue(hostId, out var host))
				{
					result.Add(new MalformedHostReference(optionId, rawHostId, "The referenced host is missing from the snapshot."));
					continue;
				}
				if (host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
				{
					result.Add(new MalformedHostReference(optionId, rawHostId, $"The referenced host kind '{host.Metadata.Kind}' cannot own Options."));
				}
			}
		}
		return result;
	}

	private static IReadOnlyList<SafeModDeletionOptionHostRelationRemoval> BuildSurvivingOptionHostRelationRemovals(
		LibrarySnapshot snapshot,
		OptionRelationshipGraph graph,
		IReadOnlySet<ModNodeId> removalIds,
		CancellationToken cancellationToken)
	{
		var result = new List<SafeModDeletionOptionHostRelationRemoval>();
		foreach (var option in snapshot.Nodes.Values
			.Where(node => node.Metadata.Kind == ModNodeKind.Option && !removalIds.Contains(node.Id))
			.OrderBy(node => node.Id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var parents = graph.GetParents(option.Id);
			if (parents.Count == 0) continue;
			var removedParents = parents.Where(removalIds.Contains).OrderBy(id => id.Value).ToArray();
			if (removedParents.Length == 0) continue;
			var remainingParents = parents.Where(parentId => !removalIds.Contains(parentId)).OrderBy(id => id.Value).ToArray();
			foreach (var removedParentId in removedParents)
			{
				result.Add(new SafeModDeletionOptionHostRelationRemoval(option.Id, removedParentId, remainingParents));
			}
		}
		return result
			.OrderBy(item => item.OptionId.Value)
			.ThenBy(item => item.RemovedHostId.Value)
			.ToArray();
	}

	private static IReadOnlyList<SafeModDeletionSharedOptionRetention> BuildSharedOptionRetentions(
		LibrarySnapshot snapshot,
		IReadOnlyList<SafeModDeletionOptionHostRelationRemoval> relationRemovals)
	{
		return relationRemovals
			.GroupBy(item => item.OptionId)
			.Select(group =>
			{
				var option = snapshot.Nodes[group.Key];
				var survivingHosts = group.SelectMany(item => item.RemainingHostIds).Distinct().OrderBy(id => id.Value).ToArray();
				return new SafeModDeletionSharedOptionRetention(
					option.Id,
					option.Metadata.Name,
					group.Select(item => item.RemovedHostId).Distinct().OrderBy(id => id.Value).ToArray(),
					survivingHosts,
					option.RelativePath);
			})
			.Where(item => item.SurvivingHostIds.Count > 0)
			.OrderBy(item => item.OptionId.Value)
			.ToArray();
	}

	private static IReadOnlyList<SafeModDeletionProfileEntryCleanup> BuildProfileCleanups(
		LibrarySnapshot snapshot,
		IReadOnlySet<ModNodeId> removalIds,
		CancellationToken cancellationToken)
	{
		var result = new List<SafeModDeletionProfileEntryCleanup>();
		foreach (var profile in snapshot.Profiles.OrderBy(profile => profile.Id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var entry in profile.Entries
				.Where(entry => removalIds.Contains(entry.NodeId))
				.OrderBy(entry => entry.LoadOrder)
				.ThenBy(entry => entry.AddedUtc)
				.ThenBy(entry => entry.NodeId.Value))
			{
				result.Add(new SafeModDeletionProfileEntryCleanup(profile.Id, profile.Name, entry.NodeId, entry.LoadOrder));
			}
		}
		return result;
	}

	private static IReadOnlyList<SafeModDeletionActivationCleanupKey> BuildActivationCleanups(
		LibrarySnapshot snapshot,
		IReadOnlyList<SafeModDeletionNodeRemoval> removals,
		IReadOnlyList<SafeModDeletionOptionHostRelationRemoval> relationRemovals)
	{
		var result = new HashSet<SafeModDeletionActivationCleanupKey>();
		foreach (var removal in removals)
		{
			if (removal.Kind == ModNodeKind.Option)
			{
				result.Add(new SafeModDeletionActivationCleanupKey(
					SafeModDeletionActivationCleanupKind.OptionActivationForOption,
					removal.NodeId));
			}
			if (removal.Kind is ModNodeKind.Standard or ModNodeKind.Option)
			{
				result.Add(new SafeModDeletionActivationCleanupKey(
					SafeModDeletionActivationCleanupKind.OptionActivationForHost,
					removal.NodeId));
				result.Add(new SafeModDeletionActivationCleanupKey(
					SafeModDeletionActivationCleanupKind.DecorationActivationForHost,
					removal.NodeId));
			}
			if (removal.Kind == ModNodeKind.Decoration)
			{
				result.Add(new SafeModDeletionActivationCleanupKey(
					SafeModDeletionActivationCleanupKind.DecorationActivationForDecoration,
					removal.NodeId));
			}
		}

		foreach (var relation in relationRemovals)
		{
			result.Add(new SafeModDeletionActivationCleanupKey(
				SafeModDeletionActivationCleanupKind.OptionActivationForRelation,
				relation.OptionId,
				relation.RemovedHostId));
		}

		return result
			.OrderBy(item => item.Kind)
			.ThenBy(item => item.PrimaryNodeId.Value)
			.ThenBy(item => item.SecondaryNodeId?.Value)
			.ToArray();
	}

	private static IReadOnlyList<SafeModDeletionPhysicalPath> BuildPhysicalPaths(
		LibrarySnapshot snapshot,
		IReadOnlyList<SafeModDeletionNodeRemoval> removals,
		IReadOnlySet<ModNodeId> removalIds,
		BlockerCollector blockers,
		CancellationToken cancellationToken)
	{
		var result = new List<SafeModDeletionPhysicalPath>();
		foreach (var group in removals
			.GroupBy(removal => removal.RelativePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var nodeIds = group.Select(removal => removal.NodeId).Distinct().OrderBy(id => id.Value).ToArray();
			var isSafe = IsSafeRelativePath(group.Key);
			result.Add(new SafeModDeletionPhysicalPath(group.Key, nodeIds, isSafe));
			if (!isSafe)
			{
				blockers.Add(
					"UnsafePhysicalRelativePath",
					SafeModDeletionBlockerSeverity.Blocking,
					$"Library path '{group.Key}' is empty, rooted, or traverses outside the Mod library.",
					nodeIds,
					[group.Key]);
			}

			var survivingNodesAtPath = snapshot.Nodes.Values
				.Where(node => !removalIds.Contains(node.Id)
					&& string.Equals(node.RelativePath, group.Key, StringComparison.OrdinalIgnoreCase))
				.Select(node => node.Id)
				.OrderBy(id => id.Value)
				.ToArray();
			if (survivingNodesAtPath.Length > 0)
			{
				blockers.Add(
					"PhysicalPathSharedWithSurvivingNode",
					SafeModDeletionBlockerSeverity.Blocking,
					$"Removing physical path '{group.Key}' would also remove library nodes that are not in this deletion plan.",
					nodeIds.Concat(survivingNodesAtPath).ToArray(),
					[group.Key]);
			}
		}
		return result;
	}

	private static void ApplyGeneratedContentSafety(
		LibrarySnapshot snapshot,
		IReadOnlySet<ModNodeId> removalIds,
		SafeModDeletionSafetySnapshot safety,
		BlockerCollector blockers,
		CancellationToken cancellationToken)
	{
		var removedNodes = removalIds
			.Where(snapshot.Nodes.ContainsKey)
			.Select(nodeId => snapshot.Nodes[nodeId])
			.ToArray();
		if (removedNodes.Length == 0) return;

		foreach (var node in removedNodes.Where(node => node.Metadata.HelmetToArmorMerged))
		{
			blockers.Add(
				"HelmetToArmorRollbackRequired",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Mod '{ToKey(node.Id)}' is marked as temporarily helmet-merged. Its managed Overwrite must be rolled back before deletion can be applied safely.",
				[node.Id],
				[node.RelativePath]);
		}

		if (!safety.IsDecorationActivationStateKnown)
		{
			blockers.Add(
				"DecorationActivationStateUnknown",
				SafeModDeletionBlockerSeverity.Warning,
				"Decoration activation state was not supplied. A caller must inspect it before executing this plan.",
				removedNodes.Where(node => node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option or ModNodeKind.Decoration).Select(node => node.Id).ToArray());
		}

		if (!safety.IsManagedOverwriteStateKnown && removedNodes.Any(node => node.Metadata.HelmetToArmorMerged))
		{
			blockers.Add(
				"ManagedOverwriteStateUnknown",
				SafeModDeletionBlockerSeverity.Warning,
				"Managed Overwrite state was not supplied for a helmet-merged Mod. A caller must inspect it before execution.",
				removedNodes.Where(node => node.Metadata.HelmetToArmorMerged).Select(node => node.Id).ToArray());
		}

		foreach (var attachment in safety.EnabledDecorations ?? Array.Empty<SafeModDeletionEnabledDecoration>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!removalIds.Contains(attachment.DecorationId) && !removalIds.Contains(attachment.HostId)) continue;
			blockers.Add(
				"EnabledDecorationRollbackRequired",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Enabled Decoration '{ToKey(attachment.DecorationId)}' is attached to host '{ToKey(attachment.HostId)}'. Disable it and rebuild or clear the managed Overwrite before deletion.",
				[attachment.DecorationId, attachment.HostId]);
		}

		foreach (var overwrite in safety.ManagedOverwrites ?? Array.Empty<SafeModDeletionManagedOverwrite>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var affectedHosts = overwrite.AffectedHostIds ?? Array.Empty<ModNodeId>();
			if (!removalIds.Contains(overwrite.OwnerNodeId) && !affectedHosts.Any(removalIds.Contains)) continue;
			if (overwrite.CanSafelyRollback) continue;
			var relatedNodeIds = affectedHosts.Append(overwrite.OwnerNodeId).Distinct().OrderBy(id => id.Value).ToArray();
			blockers.Add(
				"ManagedOverwriteRollbackRequired",
				SafeModDeletionBlockerSeverity.Blocking,
				$"Managed {overwrite.Kind} Overwrite '{overwrite.RelativePath}' intersects this deletion and has no declared safe rollback.",
				relatedNodeIds,
				[string.IsNullOrWhiteSpace(overwrite.RelativePath) ? string.Empty : overwrite.RelativePath]);
		}
	}

	private static SafeModDeletionNodeRemoval ToRemoval(ModNode node, SafeModDeletionRemovalReason reason)
		=> new(node.Id, node.Metadata.Name, node.Metadata.Kind, node.RelativePath, reason);

	private static bool IsSafeRelativePath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath)) return false;
		if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath)) return false;
		if (relativePath.Contains(':', StringComparison.Ordinal)) return false;
		var segments = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.None);
		return segments.Length > 0 && segments.All(segment => !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
	}

	private static string ToKey(ModNodeId nodeId) => nodeId.Value.ToString("N");

	private sealed record RelationshipTraversal(
		IReadOnlySet<ModNodeId> ReachableOptionIds,
		IReadOnlyList<IReadOnlyList<ModNodeId>> Cycles);

	private sealed record TraversalFrame(
		ModNodeId NodeId,
		IReadOnlyList<ModNode> Children,
		int NextChildIndex);

	private sealed record MalformedHostReference(ModNodeId OptionId, string RawHostId, string Detail);

	private enum VisitState
	{
		Visiting,
		Visited,
	}

	private sealed class BlockerCollector
	{
		private readonly Dictionary<string, SafeModDeletionBlocker> _items = new(StringComparer.Ordinal);

		public void Add(
			string code,
			SafeModDeletionBlockerSeverity severity,
			string message,
			IReadOnlyList<ModNodeId>? relatedNodeIds = null,
			IReadOnlyList<string>? relativePaths = null)
		{
			var normalizedNodes = (relatedNodeIds ?? Array.Empty<ModNodeId>())
				.Distinct()
				.OrderBy(id => id.Value)
				.ToArray();
			var normalizedPaths = (relativePaths ?? Array.Empty<string>())
				.Where(path => path is not null)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var key = code + "|" + string.Join(",", normalizedNodes.Select(ToKey)) + "|" + string.Join(",", normalizedPaths);
			var candidate = new SafeModDeletionBlocker(code, severity, message, normalizedNodes, normalizedPaths);
			if (!_items.TryGetValue(key, out var existing) || candidate.Severity > existing.Severity)
				_items[key] = candidate;
		}

		public IReadOnlyList<SafeModDeletionBlocker> ToArray()
			=> _items.Values
				.OrderBy(item => item.Severity)
				.ThenBy(item => item.Code, StringComparer.Ordinal)
				.ThenBy(item => item.RelatedNodeIds.Count == 0 ? Guid.Empty : item.RelatedNodeIds[0].Value)
				.ToArray();
	}
}
