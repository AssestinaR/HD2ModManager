using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：定义模组库残留目录清理契约——只允许删除既无 Patch 载荷、又无可识别外部资源、且未被库快照引用的目录。
// Purpose: Contract for removing residual mods-root directories that carry no patch payload, no recognized
// external-install files and no reference from the library snapshot. Payload-bearing folders must be preserved
// so that synchronization can still recover them.
public interface IModLibraryResidualCleanupService
{
	ModLibraryResidualCleanupResult Cleanup(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		CancellationToken cancellationToken = default);
}
