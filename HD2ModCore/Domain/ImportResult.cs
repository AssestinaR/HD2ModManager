namespace HD2ModCore.Domain;

/// <summary>
/// Reports whether post-import derived-cache invalidation crossed its entire
/// boundary. Import content itself is already committed at this point, so a
/// caller must distinguish a partial cache outcome from a failed import.
/// </summary>
public enum ImportCacheInvalidationStatus
{
	Completed,
	Partial,
	Failed,
}

// 作用：一次将导入源落库后的结果（包含库快照与本次导入的根对象 id）。
// Purpose: Result of importing a source into the library (includes snapshot and the imported root node id).
public sealed record ImportResult(
	LibrarySnapshot Snapshot,
	ModNodeId ImportedRootId,
	string SourceDisplayName,
	bool HasOptionPathConflicts = false)
{
	/// <summary>
	/// Stable identities for every node represented by this import source. The
	/// legacy <see cref="ImportedRootId"/> remains the first imported tree root
	/// for callers that only need a single display representative.
	/// </summary>
	public IReadOnlyList<ModNodeId> ImportedNodeIds { get; init; } = Array.Empty<ModNodeId>();

	/// <summary>
	/// The concrete logical Option parents affected by manifest Include path
	/// collisions. Callers can keep ordinary defaults for every other node
	/// instead of treating a whole import batch as conflicted.
	/// </summary>
	public IReadOnlyList<OptionPathConflict> OptionPathConflicts { get; init; } = Array.Empty<OptionPathConflict>();

	/// <summary>
	/// Cache invalidation is intentionally reported separately from the committed
	/// Mod files and library snapshot. A retry can repair stale derived data
	/// without pretending the import rolled back.
	/// </summary>
	public ImportCacheInvalidationStatus CacheInvalidationStatus { get; init; } = ImportCacheInvalidationStatus.Completed;

	public IReadOnlyList<ModNodeId> CacheInvalidationFailedNodeIds { get; init; } = Array.Empty<ModNodeId>();
}
