using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：将模组库快照与实际 mods 目录对账，发现外部新增、删除和文件变化。
// Purpose: Reconciles the library snapshot with the actual mods directory.
public interface IModLibrarySynchronizer
{
	/// <summary>
	/// Performs a pure filesystem scan of one existing Mod library root. Unlike
	/// <see cref="SynchronizeAsync"/>, this method never creates the root
	/// directory and never assigns random node IDs.
	/// </summary>
	ValueTask<ModLibrarySynchronizationInspection> InspectAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Applies a previously inspected filesystem state to an in-memory snapshot.
	/// Callers may supply stable IDs for newly discovered top-level directories;
	/// this makes reviewed external plans reproducible without changing ordinary
	/// UI synchronization behavior.
	/// </summary>
	ModLibrarySynchronizationResult ApplyInspection(
		LibrarySnapshot snapshot,
		ModLibrarySynchronizationInspection inspection,
		IReadOnlyDictionary<string, ModNodeId>? addedNodeIdsByRelativePath = null,
		DateTimeOffset? timestampUtc = null);

	ValueTask<ModLibrarySynchronizationResult> SynchronizeAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths = null,
		CancellationToken cancellationToken = default);
}
