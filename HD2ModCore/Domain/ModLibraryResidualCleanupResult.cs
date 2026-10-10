namespace HD2ModCore.Domain;

// 作用：模组库残留目录清理的结果，记录删除数量与逐目录失败明细。
// Purpose: Result of a residual mods-root cleanup pass, recording removals and per-directory failures.
public sealed record ModLibraryResidualCleanupResult(
	int RemovedCount,
	IReadOnlyList<string> Failures);
