namespace HD2ModCli;

public sealed record CliImportValidationIssue(string Code, string Message, bool IsBlocking = true);

public sealed record CliImportTarget(
	string? ManagerRoot,
	string? SettingsPath,
	string? LibraryRoot,
	bool LibraryExists,
	bool LibrarySnapshotExists,
	IReadOnlyList<int> RunningManagerProcessIds);

public sealed record CliImportValidationResult(
	CliImportPlanDocument Document,
	CliImportInspection? CurrentInspection,
	CliImportTarget Target,
	IReadOnlyList<CliImportValidationIssue> Issues)
{
	public bool IsValid => !Issues.Any(issue => issue.IsBlocking);
}

public static class CliImportValidator
{
	public static async ValueTask<CliImportValidationResult> ValidateAsync(
		CliImportPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var issues = new List<CliImportValidationIssue>();
		CliImportInspection? current = null;
		try
		{
			current = await CliImportPlanArtifacts.InspectAsync(document.Source.RequestedPath, cancellationToken).ConfigureAwait(false);
			if (!SourceMatches(document.Source, current.Source))
				issues.Add(new CliImportValidationIssue("ImportSourceShapeChanged", "来源类型或实际扫描根目录已变化；请重新生成导入计划。"));
			if (!CliImportPlanArtifacts.FingerprintsEqual(document.SourceFingerprint, current.SourceFingerprint))
				issues.Add(new CliImportValidationIssue("ImportSourceChanged", "来源文件库存与导出计划时不同；请重新生成导入计划。"));
			if (!CliImportPlanArtifacts.FingerprintsEqual(document.ImportFingerprint, current.ImportFingerprint))
				issues.Add(new CliImportValidationIssue("ImportTreeChanged", "当前导入树与导出计划时不同；请重新生成导入计划。"));
			AddBlockingPlanDiagnostics(current.Import, issues);
			AddOptionPathConflictWarning(current.Import, issues);
		}
		catch (CliUserException exception)
		{
			issues.Add(new CliImportValidationIssue(exception.Code, exception.Message));
		}

		var targetValidation = await CliWriteTargetGuard.ValidateAsync(workspace, CliWriteTargetMode.Import, cancellationToken).ConfigureAwait(false);
		foreach (var issue in targetValidation.Issues)
			issues.Add(new CliImportValidationIssue(issue.Code, issue.Message));
		return new CliImportValidationResult(document, current, targetValidation.Target.ToImportTarget(), issues);
	}

	private static void AddBlockingPlanDiagnostics(HD2ModCore.Domain.ImportPlan plan, ICollection<CliImportValidationIssue> issues)
	{
		foreach (var diagnostic in plan.Diagnostics.Where(diagnostic => diagnostic.IsBlocking)
			.Concat(plan.Items.SelectMany(item => item.Diagnostics ?? Array.Empty<HD2ModCore.Domain.ImportPlanDiagnostic>()).Where(diagnostic => diagnostic.IsBlocking)))
		{
			issues.Add(new CliImportValidationIssue(diagnostic.Code, diagnostic.Message));
		}
	}

	private static void AddOptionPathConflictWarning(HD2ModCore.Domain.ImportPlan plan, ICollection<CliImportValidationIssue> issues)
	{
		foreach (var diagnostic in plan.Diagnostics.Where(diagnostic => string.Equals(diagnostic.Code, "OptionPathConflict", StringComparison.OrdinalIgnoreCase)))
			issues.Add(new CliImportValidationIssue(diagnostic.Code, diagnostic.Message, IsBlocking: false));
	}

	private static bool SourceMatches(CliImportSource left, CliImportSource right)
	{
		try
		{
			return string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
				&& PathsEqual(left.RequestedPath, right.RequestedPath)
				&& PathsEqual(left.EffectivePath, right.EffectivePath);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool PathsEqual(string left, string right)
		=> string.Equals(
			Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			StringComparison.OrdinalIgnoreCase);

}
