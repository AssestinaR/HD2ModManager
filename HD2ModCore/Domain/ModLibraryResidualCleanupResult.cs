namespace HD2ModCore.Domain;

// 作用：模组库残留目录清理的结果，记录删除数量、逐目录失败明细，以及被删除的目录名与其中的文件清单
// （后者用于日志审计：删掉的东西必须能回溯，否则"清扫"和"误删"无法区分）。
// Purpose: Result of a residual mods-root cleanup pass, recording removals, per-directory failures, and
// the removed directories with the file names they contained so a sweep can always be audited.
public sealed record ModLibraryResidualCleanupResult(
	int RemovedCount,
	IReadOnlyList<string> Failures)
{
	public IReadOnlyList<ModLibraryResidualRemoval> Removals { get; init; } = Array.Empty<ModLibraryResidualRemoval>();
}

// 作用：一个被清理掉的残留目录，连同它当时包含的文件名（不含完整路径，避免日志泄露绝对路径）。
// Purpose: One removed residual directory plus the file names it held (names only, no absolute paths).
public sealed record ModLibraryResidualRemoval(
	string RelativePath,
	IReadOnlyList<string> FileNames);
