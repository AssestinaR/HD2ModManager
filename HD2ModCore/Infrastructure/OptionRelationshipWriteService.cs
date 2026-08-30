using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Validates and writes the host-scoped Option state used by deployment.  This
/// deliberately owns no library store and no UI state: callers supply the
/// immutable library snapshot and the activation store they intend to mutate.
/// </summary>
public sealed class OptionRelationshipWriteService : IOptionRelationshipWriteService
{
	public OptionHostRelationshipSnapshot GetHostSnapshot(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionActivations);

		var host = ResolveHost(snapshot, hostId);
		return BuildHostSnapshot(host, optionActivations.CreateSnapshot());
	}

	public async ValueTask<OptionRelationshipMutationSnapshot> SetHostScopedOptionEnabledAsync(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId,
		ModNodeId optionId,
		bool enabled,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionActivations);
		cancellationToken.ThrowIfCancellationRequested();

		var host = ResolveHost(snapshot, hostId);
		RequireDirectOption(snapshot, host, optionId);

		var before = optionActivations.CreateSnapshot();
		var optionKey = ToKey(optionId);
		var hostKey = ToKey(hostId);
		if (before.HasState(optionKey, hostKey) && before.IsEnabled(optionKey, hostKey) == enabled)
		{
			return new OptionRelationshipMutationSnapshot(
				BuildHostSnapshot(host, before),
				Changed: false);
		}

		await optionActivations.SetEnabledAsync(optionKey, hostKey, enabled, cancellationToken).ConfigureAwait(false);
		return new OptionRelationshipMutationSnapshot(
			BuildHostSnapshot(host, optionActivations.CreateSnapshot()),
			Changed: true);
	}

	public async ValueTask<OptionRelationshipMutationSnapshot> MoveDirectOptionsAsync(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		int targetPosition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionActivations);
		ArgumentNullException.ThrowIfNull(optionIds);
		cancellationToken.ThrowIfCancellationRequested();

		var host = ResolveHost(snapshot, hostId);
		var beforeStates = optionActivations.CreateSnapshot();
		var before = BuildHostSnapshot(host, beforeStates);
		if (optionIds.Count == 0)
		{
			throw Failure(
				OptionRelationshipMutationFailure.EmptyOptionSelection,
				"At least one direct Option must be selected before it can be reordered.",
				hostId);
		}
		if (targetPosition < 0 || targetPosition > before.DirectOptions.Count)
		{
			throw Failure(
				OptionRelationshipMutationFailure.InvalidTargetPosition,
				$"Target position {targetPosition} is outside the valid range 0 through {before.DirectOptions.Count} for host '{ToKey(hostId)}'.",
				hostId);
		}

		var selected = new HashSet<ModNodeId>();
		foreach (var optionId in optionIds)
		{
			RequireDirectOption(snapshot, host, optionId);
			if (!selected.Add(optionId))
			{
				throw Failure(
					OptionRelationshipMutationFailure.DuplicateOptionSelection,
					$"Option '{ToKey(optionId)}' was selected more than once for a single reorder.",
					hostId,
					optionId);
			}
		}

		var missingState = before.DirectOptions.FirstOrDefault(option => !option.HasExplicitActivationState);
		if (missingState is not null)
		{
			throw Failure(
				OptionRelationshipMutationFailure.ActivationStateMissing,
				$"Cannot reorder host '{ToKey(hostId)}' because direct Option '{ToKey(missingState.OptionId)}' has no persisted activation state. Initialize missing Option states before reordering.",
				hostId,
				missingState.OptionId);
		}

		var currentOrder = before.DirectOptions.Select(option => option.OptionId).ToArray();
		var movedInCurrentOrder = currentOrder.Where(selected.Contains).ToArray();
		var remaining = currentOrder.Where(option => !selected.Contains(option)).ToList();
		var removedBeforeTarget = currentOrder.Take(targetPosition).Count(selected.Contains);
		var insertionIndex = Math.Clamp(targetPosition - removedBeforeTarget, 0, remaining.Count);
		remaining.InsertRange(insertionIndex, movedInCurrentOrder);
		var requestedOrder = remaining.ToArray();

		if (currentOrder.SequenceEqual(requestedOrder))
			return new OptionRelationshipMutationSnapshot(before, Changed: false);

		var hostKey = ToKey(hostId);
		var mutations = requestedOrder
			.Select((optionId, index) => new OptionEffectiveOrderMutation(ToKey(optionId), hostKey, index))
			.ToArray();
		var changed = await optionActivations.SetEffectiveOrdersAsync(mutations, cancellationToken).ConfigureAwait(false);
		return new OptionRelationshipMutationSnapshot(
			BuildHostSnapshot(host, optionActivations.CreateSnapshot()),
			Changed: changed > 0);
	}

	private static HostContext ResolveHost(LibrarySnapshot snapshot, ModNodeId hostId)
	{
		if (!snapshot.Nodes.TryGetValue(hostId, out var host))
		{
			throw Failure(
				OptionRelationshipMutationFailure.HostNotFound,
				$"Option host '{ToKey(hostId)}' does not exist in the supplied library snapshot.",
				hostId);
		}
		if (host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
		{
			throw Failure(
				OptionRelationshipMutationFailure.HostKindNotSupported,
				$"Node '{ToKey(hostId)}' is '{host.Metadata.Kind}' and cannot host direct Options.",
				hostId);
		}

		var directOptionsByHost = BuildDirectOptionsByHost(snapshot);
		EnsureNoReachableCycle(hostId, directOptionsByHost);
		return new HostContext(
			hostId,
			directOptionsByHost.TryGetValue(hostId, out var directOptions)
				? directOptions
				: Array.Empty<ModNode>());
	}

	private static void RequireDirectOption(LibrarySnapshot snapshot, HostContext host, ModNodeId optionId)
	{
		if (!snapshot.Nodes.TryGetValue(optionId, out var option))
		{
			throw Failure(
				OptionRelationshipMutationFailure.OptionNotFound,
				$"Option '{ToKey(optionId)}' does not exist in the supplied library snapshot.",
				host.Id,
				optionId);
		}
		if (option.Metadata.Kind != ModNodeKind.Option)
		{
			throw Failure(
				OptionRelationshipMutationFailure.OptionKindInvalid,
				$"Node '{ToKey(optionId)}' is '{option.Metadata.Kind}', not an Option Mod.",
				host.Id,
				optionId);
		}
		if (!host.DirectOptions.Any(candidate => candidate.Id == optionId))
		{
			throw Failure(
				OptionRelationshipMutationFailure.DirectRelationNotFound,
				$"Option '{ToKey(optionId)}' is not a direct Option of host '{ToKey(host.Id)}'.",
				host.Id,
				optionId);
		}
	}

	private static OptionHostRelationshipSnapshot BuildHostSnapshot(HostContext host, OptionActivationSnapshot activationStates)
	{
		var hostKey = ToKey(host.Id);
		var states = host.DirectOptions
			.Select(option =>
			{
				var optionKey = ToKey(option.Id);
				return new OrderedOptionState(
					option,
					activationStates.IsEnabled(optionKey, hostKey),
					activationStates.HasState(optionKey, hostKey),
					activationStates.GetEffectiveOrder(optionKey, hostKey));
			})
			.OrderBy(state => state.EffectiveOrder ?? int.MaxValue)
			.ThenBy(state => state.Node.Metadata.OptionOrder ?? int.MaxValue)
			.ThenBy(state => state.Node.Id.Value)
			.Select((state, index) => new OptionHostScopedState(
				state.Node.Id,
				state.Enabled,
				state.HasExplicitActivationState,
				state.EffectiveOrder,
				index))
			.ToArray();

		return new OptionHostRelationshipSnapshot(host.Id, Array.AsReadOnly(states));
	}

	private static IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNode>> BuildDirectOptionsByHost(LibrarySnapshot snapshot)
	{
		var directByHost = new Dictionary<ModNodeId, List<ModNode>>();
		foreach (var option in snapshot.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Option))
		{
			var seenHosts = new HashSet<ModNodeId>();
			foreach (var rawHostId in option.Metadata.HostModGuids ?? Array.Empty<string>())
			{
				if (!Guid.TryParse(rawHostId, out var parsedHostId)) continue;
				var hostId = new ModNodeId(parsedHostId);
				if (!seenHosts.Add(hostId)) continue;
				if (!snapshot.Nodes.TryGetValue(hostId, out var host)
					|| host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
					continue;

				if (!directByHost.TryGetValue(hostId, out var directOptions))
					directByHost[hostId] = directOptions = new List<ModNode>();
				directOptions.Add(option);
			}
		}

		return directByHost.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<ModNode>)pair.Value.ToArray());
	}

	private static void EnsureNoReachableCycle(
		ModNodeId hostId,
		IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNode>> directOptionsByHost)
	{
		var visitStates = new Dictionary<ModNodeId, VisitState>();
		var path = new List<ModNodeId>();
		Visit(hostId);
		return;

		void Visit(ModNodeId current)
		{
			visitStates[current] = VisitState.Visiting;
			path.Add(current);
			if (directOptionsByHost.TryGetValue(current, out var children))
			{
				foreach (var child in children)
				{
					if (visitStates.TryGetValue(child.Id, out var childState))
					{
						if (childState != VisitState.Visiting) continue;
						var cycleStart = path.IndexOf(child.Id);
						var cycle = path.Skip(cycleStart).Append(child.Id).ToArray();
						throw Failure(
							OptionRelationshipMutationFailure.CycleDetected,
							$"Option relationship cycle reachable from host '{ToKey(hostId)}': {string.Join(" -> ", cycle.Select(ToKey))}.",
							hostId,
							child.Id,
							cycle);
					}
					Visit(child.Id);
				}
			}
			path.RemoveAt(path.Count - 1);
			visitStates[current] = VisitState.Visited;
		}
	}

	private static OptionRelationshipMutationException Failure(
		OptionRelationshipMutationFailure failure,
		string message,
		ModNodeId? hostId = null,
		ModNodeId? optionId = null,
		IReadOnlyList<ModNodeId>? cyclePath = null)
		=> new(failure, message, hostId, optionId, cyclePath);

	private static string ToKey(ModNodeId id) => id.Value.ToString("N");

	private sealed record HostContext(ModNodeId Id, IReadOnlyList<ModNode> DirectOptions);

	private sealed record OrderedOptionState(
		ModNode Node,
		bool Enabled,
		bool HasExplicitActivationState,
		int? EffectiveOrder);

	private enum VisitState
	{
		Visiting,
		Visited,
	}
}
