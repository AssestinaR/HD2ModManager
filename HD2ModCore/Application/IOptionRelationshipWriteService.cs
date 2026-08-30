using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Application;

/// <summary>
/// Writes host-scoped Option activation and ordering state without depending on
/// any particular UI or command-line presentation.  Option ownership remains
/// data-driven: an Option is direct only when its <see cref="ModNodeMetadata.HostModGuids"/>
/// contains the supplied host.
/// </summary>
public interface IOptionRelationshipWriteService
{
	/// <summary>
	/// Returns the direct Options for one host in the exact order used by the
	/// effective deployment resolver.  This validates the host and rejects a
	/// relationship cycle reachable from it.
	/// </summary>
	OptionHostRelationshipSnapshot GetHostSnapshot(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId);

	/// <summary>
	/// Explicitly enables or disables one direct Option for one host.  An Option
	/// shared by several hosts is changed only for <paramref name="hostId"/>.
	/// </summary>
	ValueTask<OptionRelationshipMutationSnapshot> SetHostScopedOptionEnabledAsync(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId,
		ModNodeId optionId,
		bool enabled,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Moves the selected direct Options as one stable block to a zero-based
	/// insertion position in the host's pre-move direct Option list.  Position
	/// <c>0</c> is before the first item and <c>DirectOptions.Count</c> is after
	/// the last item.  Every direct Option receives a host-local effective order;
	/// existing enabled/disabled states are preserved exactly.
	/// </summary>
	ValueTask<OptionRelationshipMutationSnapshot> MoveDirectOptionsAsync(
		LibrarySnapshot snapshot,
		OptionActivationStore optionActivations,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		int targetPosition,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// A presentation-neutral view of one host's direct Option relations.  The
/// sequence is deterministic and mirrors the order used when a Profile expands
/// enabled Options for deployment.
/// </summary>
public sealed record OptionHostRelationshipSnapshot(
	ModNodeId HostId,
	IReadOnlyList<OptionHostScopedState> DirectOptions);

/// <summary>
/// Result of one persisted Option-relation mutation.  <see cref="Changed"/>
/// is false for a valid idempotent request.
/// </summary>
public sealed record OptionRelationshipMutationSnapshot(
	OptionHostRelationshipSnapshot Host,
	bool Changed);

/// <summary>
/// The persisted activation state of one Option under one specific host.
/// <see cref="HasExplicitActivationState"/> distinguishes an absent historical
/// state from an explicitly disabled state; callers can therefore avoid
/// accidentally freezing import defaults during a reorder.
/// </summary>
public sealed record OptionHostScopedState(
	ModNodeId OptionId,
	bool Enabled,
	bool HasExplicitActivationState,
	int? EffectiveOrder,
	int Position);

/// <summary>
/// Machine-readable failures that callers can map to their own UI or CLI
/// wording without parsing exception text.
/// </summary>
public enum OptionRelationshipMutationFailure
{
	HostNotFound,
	HostKindNotSupported,
	OptionNotFound,
	OptionKindInvalid,
	DirectRelationNotFound,
	CycleDetected,
	ActivationStateMissing,
	EmptyOptionSelection,
	DuplicateOptionSelection,
	InvalidTargetPosition,
}

/// <summary>
/// Thrown before writing whenever a requested host-scoped Option operation is
/// structurally unsafe or ambiguous.
/// </summary>
public sealed class OptionRelationshipMutationException : InvalidOperationException
{
	public OptionRelationshipMutationException(
		OptionRelationshipMutationFailure failure,
		string message,
		ModNodeId? hostId = null,
		ModNodeId? optionId = null,
		IReadOnlyList<ModNodeId>? cyclePath = null)
		: base(message)
	{
		Failure = failure;
		HostId = hostId;
		OptionId = optionId;
		CyclePath = cyclePath is null
			? Array.Empty<ModNodeId>()
			: Array.AsReadOnly(cyclePath.ToArray());
	}

	public OptionRelationshipMutationFailure Failure { get; }

	public ModNodeId? HostId { get; }

	public ModNodeId? OptionId { get; }

	/// <summary>
	/// Contains the repeated closing node when <see cref="Failure"/> is
	/// <see cref="OptionRelationshipMutationFailure.CycleDetected"/>.
	/// </summary>
	public IReadOnlyList<ModNodeId> CyclePath { get; }
}
