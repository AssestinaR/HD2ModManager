using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：把部署失败的结构化错误整理成普通用户看得懂的摘要：同类错误合并成一条，说明原因和建议，
// 只列几个文件名，原始技术信息只保留第一条放在末尾，完整内容仍在 ApplyResult.Issues 里。
// Purpose: Turns raw deployment issues into a short, human-readable summary - one line per kind of
// problem with a cause and a suggested action, a few file names, and only the first raw technical detail.
internal static class DeploymentFailureSummary
{
	private const int MaxFileNames = 3;

	private sealed record Category(string Title, string Advice);

	private static readonly Category FileInUse = new(
		"有游戏文件无法删除或替换，通常是被其他程序占用了",
		"请先关闭游戏，并确认 Steam 没在校验或更新游戏文件、杀毒软件没在扫描，然后重试。");
	private static readonly Category LinkFailed = new(
		"无法在游戏目录创建模组文件的链接",
		"请确认模组库和游戏在同一个磁盘分区，并且游戏目录可以写入。");
	private static readonly Category SourceMissing = new(
		"模组库里找不到需要部署的源文件",
		"请到模组库刷新；文件确实丢了的话需要重新导入这个模组。");
	private static readonly Category ExternalWrite = new(
		"无法写入游戏目录以外的附属文件（如 bin 目录）",
		"请关闭游戏后重试，并确认这些目录可以写入。");
	private static readonly Category VerifyFailed = new(
		"部署完成后的校验没通过，游戏文件和预期不一致",
		"可能有其他程序同时改动了游戏文件，请关闭游戏和其他修改工具后重试。");
	private static readonly Category StateFailed = new(
		"无法读写部署记录（activation-state.json）",
		"请确认游戏 data 目录可以写入，并且没有被其他程序占用。");
	private static readonly Category RollbackFailed = new(
		"部署失败后的自动回滚也没有完全成功，游戏文件可能处于不完整状态",
		"建议在 Steam 里校验游戏文件完整性，再重新部署。");

	private static Category? Classify(string code) => code switch
	{
		"CannotDeleteExistingPatch" or "CannotDeleteExternalFile" or "CannotPrepareTarget" => FileInUse,
		"HardLinkFailed" or "SymbolicLinkFailed" or "DeployOperationFailed" => LinkFailed,
		"SourceFileMissing" or "ExternalSourceMissing" => SourceMissing,
		"ExternalDeployFailed" => ExternalWrite,
		"DeployedTargetMissing" or "DeployedLinkMismatch" or "DeployedLengthMismatch" or "DeployedHashMissing"
			or "DeployedHashMismatch" or "ExternalTargetMissing" or "ExternalTargetLengthMismatch"
			or "ExternalTargetHashMismatch" => VerifyFailed,
		"ReadActivationStateFailed" or "WriteStateFailed" or "SaveDeactivationStateFailed" or "DeleteActivationStateFailed" => StateFailed,
		_ when code.StartsWith("ExternalRollback", StringComparison.Ordinal) => RollbackFailed,
		_ => null,
	};

	public static string Format(string prefix, IReadOnlyList<CoreIssue> issues)
	{
		var errors = issues.Where(issue => issue.Severity == CoreIssueSeverity.Error).ToArray();
		if (errors.Length == 0) return $"{prefix}，但未返回结构化错误信息。请查看部署日志。";

		var lines = new List<string>();
		var groups = errors
			.GroupBy(issue => Classify(issue.Code))
			.OrderBy(group => group.Key is null ? 1 : 0);
		foreach (var group in groups)
		{
			if (group.Key is null)
			{
				// 不认识的错误码：照原样给出，避免把真实原因藏起来。
				foreach (var issue in group.Take(3)) lines.Add($"[{issue.Code}] {issue.Message}");
				continue;
			}

			var files = group
				.Select(issue => issue.FilePath)
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(path => Path.GetFileName(path!))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var scope = files.Length == 0
				? string.Empty
				: $"（共 {files.Length} 个文件，如 {string.Join("、", files.Take(MaxFileNames))}{(files.Length > MaxFileNames ? " 等" : string.Empty)}）";
			lines.Add($"{group.Key.Title}{scope}。{group.Key.Advice}");
		}

		var first = errors[0];
		var technical = $"技术详情：[{first.Code}] {first.Message}";
		return $"{prefix}：\n{string.Join("\n", lines)}\n{technical}";
	}
}
