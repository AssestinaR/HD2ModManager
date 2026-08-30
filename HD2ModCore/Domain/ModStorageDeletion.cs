namespace HD2ModCore.Domain;

// 作用：描述一个 Mod 库目录的物理删除结果；逻辑删除只能在目录已删除、已隔离或已登记重启清理后提交。
// Purpose: Describes physical removal of one stored Mod root. Logical removal may commit only after the root is gone, isolated, or durably scheduled for cleanup.
public enum ModStorageDeletionState
{
	NotRequested,
	DeletedImmediately,
	PendingCleanup,
	PendingRestart,
	Missing,
	Failed,
}

public sealed record ModStorageDeletionResult(
	string RelativePath,
	ModStorageDeletionState State,
	string? Detail = null)
{
	public bool CanRemoveLibraryNode => State is
		ModStorageDeletionState.NotRequested or
		ModStorageDeletionState.DeletedImmediately or
		ModStorageDeletionState.PendingCleanup or
		ModStorageDeletionState.PendingRestart or
		ModStorageDeletionState.Missing;
}

// 作用：一次删除操作对库快照和物理存储的联合结果。
// Purpose: Combined result of a deletion operation across the library snapshot and physical storage.
public sealed record ModLibraryDeletionResult(
	LibrarySnapshot Snapshot,
	IReadOnlyList<ModNodeId> RemovedNodeIds,
	IReadOnlyList<ModNodeId> FailedNodeIds,
	IReadOnlyList<ModStorageDeletionResult> StorageResults)
{
	public int DeletedImmediatelyCount => StorageResults.Count(result => result.State == ModStorageDeletionState.DeletedImmediately);
	public int PendingCleanupCount => StorageResults.Count(result => result.State == ModStorageDeletionState.PendingCleanup);
	public int PendingRestartCount => StorageResults.Count(result => result.State == ModStorageDeletionState.PendingRestart);
	public IReadOnlyList<ModStorageDeletionResult> Failures => StorageResults
		.Where(result => result.State == ModStorageDeletionState.Failed)
		.ToArray();
	public bool HasFailures => FailedNodeIds.Count != 0;
}

// 作用：启动或同步前对受管理回收区和待重启删除项进行一次恢复性清理的结果。
// Purpose: Result of recovery cleanup for the managed trash area and reboot-pending entries before synchronization.
public sealed record ModStoragePendingCleanupResult(
	int RemovedCount,
	int PendingCount,
	IReadOnlyList<string> Failures)
{
	public bool HasFailures => Failures.Count != 0;
}
