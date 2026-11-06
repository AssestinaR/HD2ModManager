using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：把老库里还叫 `vpk2patch/` 的基底目录一次性改名为 `sectional/`。
// Purpose: One-time rename of legacy payload directories to the current name.
public interface IModContentLayoutMigration
{
	ValueTask<ModContentLayoutMigrationResult> MigrateAsync(
		string modsRootDirectory,
		CancellationToken cancellationToken = default);
}
