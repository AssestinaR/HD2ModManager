namespace HD2ModCore.Domain;

// 作用：外部安装文件检测结果；只有 Files 中的文件允许进入自动部署计划。
// Purpose: External install detection result; only Files may enter an automatic deployment plan.
public sealed record ExternalInstallDetectionResult(
	string SourceRootDirectory,
	IReadOnlyList<ExternalInstallFileFact> Files,
	IReadOnlyList<string> UnrecognizedExternalPaths,
	IReadOnlyList<ExternalInstallConflict> Conflicts)
{
	public bool HasConflicts => Conflicts.Count != 0;
}
