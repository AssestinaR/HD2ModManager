using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>
/// Atomic library-structure mutations shared by the desktop Manager and the
/// headless CLI. It owns the relationship sidecar, library snapshot, Profile
/// cleanup and host-scoped Option state as one semantic operation.
/// </summary>
public interface IModRelationshipMutationService
{
	ValueTask<LogicalModCreationResult> CreateLogicalContainerAsync(
		LibrarySnapshot snapshot,
		string name,
		string? notes = null,
		CancellationToken cancellationToken = default);

	ValueTask<ModRelationshipMutationResult> AddOptionParentsAsync(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		CancellationToken cancellationToken = default);

	ValueTask<ModRelationshipMutationResult> RemoveOptionParentsAsync(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		CancellationToken cancellationToken = default);
}

public sealed record LogicalModCreationResult(
	LibrarySnapshot Snapshot,
	ModNode Node,
	bool Changed);

public sealed record ModRelationshipMutationResult(
	LibrarySnapshot Snapshot,
	ModNodeId HostId,
	IReadOnlyList<ModNodeId> ChangedNodeIds,
	IReadOnlyList<ModNodeId> AddedRelationOptionIds,
	IReadOnlyList<ModNodeId> RemovedRelationOptionIds,
	IReadOnlyList<ProfileId> AffectedProfileIds,
	bool Changed)
{
	public bool ConvertedAnyNodeToOption => AddedRelationOptionIds.Count > 0;
	public bool RestoredAnyNodeToStandard => RemovedRelationOptionIds.Count > 0;
}

public enum ModRelationshipMutationFailure
{
	EmptyName,
	EmptySelection,
	DuplicateSelection,
	HostNotFound,
	HostKindUnsupported,
	SourceNotFound,
	SourceKindUnsupported,
	SelfRelation,
	DirectRelationNotFound,
	InvalidExistingRelation,
	CycleDetected,
	StoragePathInvalid,
	StorageMissing,
}

public sealed class ModRelationshipMutationException : InvalidOperationException
{
	public ModRelationshipMutationException(
		ModRelationshipMutationFailure failure,
		string message,
		ModNodeId? hostId = null,
		ModNodeId? sourceId = null,
		IReadOnlyList<ModNodeId>? cyclePath = null)
		: base(message)
	{
		Failure = failure;
		HostId = hostId;
		SourceId = sourceId;
		CyclePath = cyclePath?.ToArray() ?? Array.Empty<ModNodeId>();
	}

	public ModRelationshipMutationFailure Failure { get; }
	public ModNodeId? HostId { get; }
	public ModNodeId? SourceId { get; }
	public IReadOnlyList<ModNodeId> CyclePath { get; }
}
