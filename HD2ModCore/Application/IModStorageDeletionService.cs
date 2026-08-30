using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：负责 Mod 库目录的可验证删除、受管理回收与待重启清理；不直接修改库快照。
// Purpose: Owns verified stored-Mod removal, managed quarantine, and reboot-pending cleanup without mutating the library snapshot.
public interface IModStorageDeletionService
{
	ValueTask<ModStorageDeletionResult> DeleteAsync(
		string nodeRelativePath,
		CancellationToken cancellationToken = default);

	ValueTask<ModStoragePendingCleanupResult> RecoverPendingAsync(
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlySet<string>> GetSynchronizationExclusionsAsync(
		CancellationToken cancellationToken = default);
}
