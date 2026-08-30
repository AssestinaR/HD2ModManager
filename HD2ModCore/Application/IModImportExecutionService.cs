using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>
/// Executes a previously reviewed import source against a Mod library. Source
/// planning and target validation intentionally remain separate responsibilities
/// so command-line callers can require an explicit plan → validate → apply flow.
/// </summary>
public interface IModImportExecutionService
{
	ValueTask<ModImportExecutionResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default);
	ValueTask<ModImportExecutionResult> ImportArchiveAsync(string archivePath, CancellationToken cancellationToken = default);
}

public sealed record ModImportExecutionResult(
	LibrarySnapshot Snapshot,
	string SourceDisplayName,
	IReadOnlyList<ModNodeId> AddedNodeIds,
	IReadOnlyList<ModNodeId> ChangedNodeIds,
	int InitializedOptionActivationCount,
	bool HasOptionPathConflicts = false)
{
	/// <summary>
	/// Every stable node identity discovered by the current import source,
	/// including identities that already existed before a retry. This lets a
	/// caller safely resume a post-import Profile append without treating the
	/// absence of newly added nodes as proof that the source had no deployable
	/// roots.
	/// </summary>
	public IReadOnlyList<ModNodeId> ImportedNodeIds { get; init; } = Array.Empty<ModNodeId>();

	public IReadOnlyList<ModNodeId> AffectedNodeIds
		=> AddedNodeIds.Concat(ChangedNodeIds).Distinct().ToArray();

	public ImportCacheInvalidationStatus CacheInvalidationStatus { get; init; } = ImportCacheInvalidationStatus.Completed;

	public IReadOnlyList<ModNodeId> CacheInvalidationFailedNodeIds { get; init; } = Array.Empty<ModNodeId>();
}

/// <summary>
/// Signals that file/library import committed, but a post-commit state update
/// (currently default Option activation persistence) could not finish. Callers
/// must report this distinctly instead of pretending the import was rolled back.
/// </summary>
public sealed class ModImportPostCommitException : Exception
{
	public ModImportPostCommitException(ModImportExecutionResult partialResult, Exception innerException)
		: base("Mod content was imported, but post-import state initialization did not finish.", innerException)
	{
		PartialResult = partialResult;
	}

	public ModImportExecutionResult PartialResult { get; }
}
