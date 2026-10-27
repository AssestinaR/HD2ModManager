namespace HD2ModCore.Domain;

// 作用：一次索引垃圾回收的结果。所有计数都是"本次真正删除的数量"，便于在日志里核对。
// Purpose: Result of one index garbage-collection pass. Every count is what was actually removed,
// so a sweep can be audited from the log alone.
public sealed record ModIndexGarbageCollectionResult(
	int RemovedCacheFiles,
	int RemovedIndexNodes,
	int RemovedFactRows,
	int RemovedBackupDirectories,
	int RemovedTemporaryFiles,
	IReadOnlyList<string> Failures)
{
	public bool IsEmpty => RemovedCacheFiles == 0
		&& RemovedIndexNodes == 0
		&& RemovedFactRows == 0
		&& RemovedBackupDirectories == 0
		&& RemovedTemporaryFiles == 0;

	public int TotalRemoved => RemovedCacheFiles + RemovedIndexNodes + RemovedFactRows
		+ RemovedBackupDirectories + RemovedTemporaryFiles;

	public static ModIndexGarbageCollectionResult Nothing { get; } =
		new(0, 0, 0, 0, 0, Array.Empty<string>());
}
