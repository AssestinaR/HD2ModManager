using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>
/// Applies small, validated Profile mutations to an in-memory library snapshot.
/// The service is deliberately storage- and UI-neutral: callers own loading,
/// locking, persistence, and presentation.
/// </summary>
public interface IProfileMutationService
{
	/// <summary>Creates an empty Profile with a unique, case-insensitive name.</summary>
	ProfileMutationResult CreateProfile(LibrarySnapshot snapshot, string name, bool makeActive = false);

	/// <summary>Deletes one Profile and clears the active selection when it points at that Profile.</summary>
	ProfileMutationResult DeleteProfile(LibrarySnapshot snapshot, ProfileId profileId);

	/// <summary>Renames one existing Profile without changing its deployment revision.</summary>
	ProfileMutationResult RenameProfile(LibrarySnapshot snapshot, ProfileId profileId, string name);

	/// <summary>Sets the sole active Profile, or clears it when <paramref name="profileId"/> is null.</summary>
	ProfileMutationResult SetActiveProfile(LibrarySnapshot snapshot, ProfileId? profileId);

	/// <summary>Appends new deployable Mod entries to one Profile.</summary>
	ProfileMutationResult AddEntries(LibrarySnapshot snapshot, ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds);

	/// <summary>Removes existing deployable Mod entries from one Profile.</summary>
	ProfileMutationResult RemoveEntries(LibrarySnapshot snapshot, ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds);

	/// <summary>
	/// Inserts new deployable Mod entries at a zero-based position in the Profile's
	/// current stable order. Position <c>0</c> is before the first entry and
	/// <c>Entries.Count</c> is after the last entry.
	/// </summary>
	ProfileMutationResult InsertEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> nodeIds,
		int targetPosition);

	/// <summary>
	/// Moves existing Profile entries as one stable block to a zero-based
	/// pre-move insertion position. Selected entries keep their current relative
	/// order regardless of the order in which their IDs were supplied.
	/// </summary>
	ProfileMutationResult MoveEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> nodeIds,
		int targetPosition);

	/// <summary>
	/// Replaces the Profile's full entry order. The supplied IDs must contain
	/// every current entry exactly once.
	/// </summary>
	ProfileMutationResult ReorderEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> orderedNodeIds);

	/// <summary>
	/// Replaces a Profile's complete logical membership and order in one pure
	/// mutation. Unlike a sequence of add/remove/reorder calls, this increments
	/// the deployment revision at most once and permits an empty final Profile.
	/// Existing entries retain their original AddedUtc timestamp.
	/// </summary>
	ProfileMutationResult ReplaceEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> orderedNodeIds);
}

/// <summary>Identifies the requested pure Profile operation.</summary>
public enum ProfileMutationOperation
{
	CreateProfile,
	DeleteProfile,
	RenameProfile,
	SetActiveProfile,
	AddEntries,
	RemoveEntries,
	InsertEntries,
	MoveEntries,
	ReorderEntries,
	ReplaceEntries,
}

/// <summary>Distinguishes an applied mutation, an idempotent request, and a safe rejection.</summary>
public enum ProfileMutationStatus
{
	Applied,
	NoChange,
	Rejected,
}

/// <summary>Machine-readable reasons a Profile mutation was rejected.</summary>
public enum ProfileMutationFailure
{
	InvalidProfileName,
	ProfileNameConflict,
	ProfileNotFound,
	EmptyEntrySelection,
	DuplicateEntrySelection,
	NodeNotFound,
	NodeKindNotSupported,
	NodeNotDeployable,
	EntryAlreadyPresent,
	EntryNotFound,
	InvalidTargetPosition,
	ReorderMustContainEveryEntry,
	ProfileContainsDuplicateEntries,
	ProfileEntryNodeMissing,
	ProfileContainsNonDeployableEntry,
}

/// <summary>
/// The complete outcome of one in-memory Profile mutation. On rejection or an
/// idempotent request, <see cref="Snapshot"/> is the supplied snapshot; no
/// persistence or external side effect has occurred.
/// </summary>
public sealed record ProfileMutationResult(
	ProfileMutationOperation Operation,
	ProfileMutationStatus Status,
	LibrarySnapshot Snapshot,
	ProfileId? ProfileId,
	IReadOnlyList<ModNodeId> EntryIds,
	ProfileMutationFailure? Failure = null,
	string? Message = null)
{
	public bool Succeeded => Status is not ProfileMutationStatus.Rejected;

	public bool Changed => Status is ProfileMutationStatus.Applied;
}
