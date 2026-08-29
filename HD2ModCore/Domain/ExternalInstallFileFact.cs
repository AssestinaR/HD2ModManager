namespace HD2ModCore.Domain;

// 作用：记录一个被安全识别、可由管理器自动映射的外部安装文件。
// Purpose: Describes an external install file that was safely identified and can be mapped by the manager.
public sealed record ExternalInstallFileFact(
	string SourceRelativePath,
	ExternalInstallTargetRoot TargetRoot,
	string TargetRelativePath,
	long Length,
	string Sha256,
	ExternalInstallFileKind Kind = ExternalInstallFileKind.ReShadePlugin,
	DateTimeOffset LastWriteTimeUtc = default)
{
	// 作用：为同一目标路径生成稳定、大小写不敏感的冲突键。
	// Purpose: Produces a stable, case-insensitive conflict key for one target path.
	public string ConflictKey => ExternalInstallPathMapper.CreateConflictKey(TargetRoot, TargetRelativePath);
}
