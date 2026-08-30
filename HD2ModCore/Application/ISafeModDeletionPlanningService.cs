using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>
/// Produces a no-write, relationship-aware deletion plan for Mod-library nodes.
/// The plan is intentionally separate from physical deletion and JSON mutation so
/// a caller can inspect every consequence before it acquires a write lease.
/// </summary>
public interface ISafeModDeletionPlanningService
{
	/// <summary>
	/// Plans removal of the requested library nodes. Option ownership is resolved
	/// solely from <see cref="ModNodeMetadata.HostModGuids"/>; legacy
	/// <see cref="ModNode.Children"/> data is never used as an ownership source.
	/// </summary>
	SafeModDeletionPlan Plan(
		LibrarySnapshot snapshot,
		IReadOnlyCollection<ModNodeId> requestedNodeIds,
		SafeModDeletionScope scope,
		SafeModDeletionSafetySnapshot? safetySnapshot = null,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Controls whether only the selected nodes are removed, or whether Option
/// descendants that have no surviving real host are removed with them.
/// </summary>
public enum SafeModDeletionScope
{
	Selected = 0,
	WithUnsharedOptions = 1,
}

/// <summary>
/// A complete, immutable deletion proposal. It contains no executable file or
/// persistence operation; a future executor must revalidate this projection
/// against a fresh snapshot before applying it.
/// </summary>
public sealed record SafeModDeletionPlan(
	SafeModDeletionScope Scope,
	IReadOnlyList<SafeModDeletionRequestedNode> RequestedNodes,
	IReadOnlyList<SafeModDeletionNodeRemoval> LibraryNodeRemovals,
	IReadOnlyList<SafeModDeletionOptionHostRelationRemoval> SurvivingOptionHostRelationsToStrip,
	IReadOnlyList<SafeModDeletionNodeRemoval> NonSharedDescendantRemovals,
	IReadOnlyList<SafeModDeletionSharedOptionRetention> PreservedSharedOptions,
	IReadOnlyList<SafeModDeletionProfileEntryCleanup> ProfileEntriesToClean,
	IReadOnlyList<SafeModDeletionActivationCleanupKey> ActivationCleanupKeys,
	IReadOnlyList<SafeModDeletionPhysicalPath> PhysicalRelativePaths,
	IReadOnlyList<SafeModDeletionBlocker> Blockers)
{
	/// <summary>
	/// True when the plan contains a safety blocker. Warnings remain visible in
	/// <see cref="Blockers"/> but do not by themselves make the plan unsafe.
	/// </summary>
	public bool IsBlocked => Blockers.Any(blocker => blocker.Severity == SafeModDeletionBlockerSeverity.Blocking);
}

/// <summary>
/// One requested node after deduplication. <see cref="RequestCount"/> preserves
/// whether an agent accidentally supplied the same node more than once.
/// </summary>
public sealed record SafeModDeletionRequestedNode(
	ModNodeId NodeId,
	bool Exists,
	bool WillRemove,
	int RequestCount);

public enum SafeModDeletionRemovalReason
{
	Requested = 0,
	UnsharedOptionDescendant = 1,
}

/// <summary>
/// One logical library node that an executor would remove if this plan passes
/// all later validation and rollback gates.
/// </summary>
public sealed record SafeModDeletionNodeRemoval(
	ModNodeId NodeId,
	string Name,
	ModNodeKind Kind,
	string RelativePath,
	SafeModDeletionRemovalReason Reason);

/// <summary>
/// Removes one host relation from a surviving Option's HostModGuids. The
/// remaining hosts are supplied so an executor can write the exact new value
/// instead of re-inferring it from mutable state.
/// </summary>
public sealed record SafeModDeletionOptionHostRelationRemoval(
	ModNodeId OptionId,
	ModNodeId RemovedHostId,
	IReadOnlyList<ModNodeId> RemainingHostIds);

/// <summary>
/// An Option retained because at least one real host survives the deletion.
/// </summary>
public sealed record SafeModDeletionSharedOptionRetention(
	ModNodeId OptionId,
	string Name,
	IReadOnlyList<ModNodeId> RemovedHostIds,
	IReadOnlyList<ModNodeId> SurvivingHostIds,
	string RelativePath);

/// <summary>
/// A Profile membership record that references a node planned for removal.
/// </summary>
public sealed record SafeModDeletionProfileEntryCleanup(
	ProfileId ProfileId,
	string ProfileName,
	ModNodeId NodeId,
	int LoadOrder);

public enum SafeModDeletionActivationCleanupKind
{
	OptionActivationForOption = 0,
	OptionActivationForHost = 1,
	OptionActivationForRelation = 2,
	DecorationActivationForDecoration = 3,
	DecorationActivationForHost = 4,
}

/// <summary>
/// A persistence key that a future deletion executor must remove from the
/// Option or Decoration activation stores. It is descriptive rather than tied
/// to any current JSON schema so the planner remains UI-independent.
/// </summary>
public sealed record SafeModDeletionActivationCleanupKey(
	SafeModDeletionActivationCleanupKind Kind,
	ModNodeId PrimaryNodeId,
	ModNodeId? SecondaryNodeId = null);

/// <summary>
/// A physical library path associated with one or more planned logical
/// removals. The path always remains relative; an unsafe value is surfaced as a
/// blocker rather than being normalized into a potentially dangerous target.
/// </summary>
public sealed record SafeModDeletionPhysicalPath(
	string RelativePath,
	IReadOnlyList<ModNodeId> NodeIds,
	bool IsSafeRelativePath);

public enum SafeModDeletionBlockerSeverity
{
	Warning = 0,
	Blocking = 1,
}

/// <summary>
/// An extensible safety finding. <see cref="Code"/> deliberately remains a
/// string so future rollback/deployment subsystems can add machine-readable
/// blockers without changing this public plan contract.
/// </summary>
public sealed record SafeModDeletionBlocker(
	string Code,
	SafeModDeletionBlockerSeverity Severity,
	string Message,
	IReadOnlyList<ModNodeId> RelatedNodeIds,
	IReadOnlyList<string> RelativePaths);

/// <summary>
/// Optional facts obtained by a caller that owns the live activation and
/// Overwrite stores. The deletion planner never reads those files itself.
/// </summary>
public sealed record SafeModDeletionSafetySnapshot(
	bool IsDecorationActivationStateKnown,
	IReadOnlyList<SafeModDeletionEnabledDecoration> EnabledDecorations,
	bool IsManagedOverwriteStateKnown,
	IReadOnlyList<SafeModDeletionManagedOverwrite> ManagedOverwrites)
{
	public static SafeModDeletionSafetySnapshot Unknown { get; } = new(
		IsDecorationActivationStateKnown: false,
		EnabledDecorations: Array.Empty<SafeModDeletionEnabledDecoration>(),
		IsManagedOverwriteStateKnown: false,
		ManagedOverwrites: Array.Empty<SafeModDeletionManagedOverwrite>());

	public static SafeModDeletionSafetySnapshot EmptyKnown { get; } = new(
		IsDecorationActivationStateKnown: true,
		EnabledDecorations: Array.Empty<SafeModDeletionEnabledDecoration>(),
		IsManagedOverwriteStateKnown: true,
		ManagedOverwrites: Array.Empty<SafeModDeletionManagedOverwrite>());
}

/// <summary>
/// A live enabled Decoration-to-host attachment. Deletion must first disable it
/// and rebuild or clear its host Overwrite through the UI-aware workflow.
/// </summary>
public sealed record SafeModDeletionEnabledDecoration(
	ModNodeId DecorationId,
	ModNodeId HostId);

public enum SafeModDeletionManagedOverwriteKind
{
	Decoration = 0,
	HelmetToArmor = 1,
	Other = 2,
}

/// <summary>
/// A managed generated payload that could require a dedicated rollback before
/// a library node is removed. A caller can list affected hosts in addition to
/// the owning node when the payload is shared.
/// </summary>
public sealed record SafeModDeletionManagedOverwrite(
	SafeModDeletionManagedOverwriteKind Kind,
	ModNodeId OwnerNodeId,
	string RelativePath,
	IReadOnlyList<ModNodeId>? AffectedHostIds = null,
	bool CanSafelyRollback = false);
