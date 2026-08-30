using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：将模组库快照与实际 mods 目录对账，发现外部新增、删除和文件变化。
// Purpose: Reconciles the library snapshot with the actual mods directory.
public interface IModLibrarySynchronizer
{
	ValueTask<ModLibrarySynchronizationResult> SynchronizeAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths = null,
		CancellationToken cancellationToken = default);
}
