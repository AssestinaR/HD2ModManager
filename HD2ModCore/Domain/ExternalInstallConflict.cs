namespace HD2ModCore.Domain;

// 作用：表示多个来源文件映射到同一个外部安装目标。
// Purpose: Represents multiple source files mapping to one external install target.
public sealed record ExternalInstallConflict(
	string ConflictKey,
	IReadOnlyList<ExternalInstallFileFact> Files);
