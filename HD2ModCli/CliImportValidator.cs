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
	CliImportProfileTarget? ResolvedProfile,
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
		var profile = ValidateProfileTarget(document.AddToProfile, targetValidation.Snapshot, targetValidation.Target.LibraryRoot, issues);
		return new CliImportValidationResult(document, current, targetValidation.Target.ToImportTarget(), profile, issues);
	}

	private static CliImportProfileTarget? ValidateProfileTarget(
		CliImportProfileTarget? requested,
		HD2ModCore.Domain.LibrarySnapshot? snapshot,
		string? currentLibraryRoot,
		ICollection<CliImportValidationIssue> issues)
	{
		if (requested is null) return null;
		if (!Guid.TryParse(requested.ProfileId, out var rawProfileId))
		{
			issues.Add(new CliImportValidationIssue("ImportProfileTargetInvalid", "导入计划中的目标配置文件 ID 无效。"));
			return null;
		}
		if (snapshot is null)
		{
			issues.Add(new CliImportValidationIssue("ImportProfileTargetUnavailable", "无法读取目标 Mod 库，因此不能确认导入后加入配置文件的目标。"));
			return null;
		}
		if (string.IsNullOrWhiteSpace(requested.LibraryRoot)
			|| string.IsNullOrWhiteSpace(currentLibraryRoot)
			|| !PathsEqual(requested.LibraryRoot, currentLibraryRoot))
		{
			issues.Add(new CliImportValidationIssue("ImportProfileLibraryChanged", "导入计划固定的 Mod 库与当前目标不同；请在正确的库上重新生成计划。"));
			return null;
		}

		var profileId = new HD2ModCore.Domain.ProfileId(rawProfileId);
		var current = snapshot.Profiles.FirstOrDefault(profile => profile.Id == profileId);
		if (current is null)
		{
			issues.Add(new CliImportValidationIssue("ImportProfileTargetMissing", $"导入计划指定的配置文件已不存在：{requested.ProfileName}。请重新生成导入计划。"));
			return null;
		}
		if (!string.Equals(current.Name, requested.ProfileName, StringComparison.Ordinal)
			|| current.Revision != requested.ProfileRevision)
		{
			issues.Add(new CliImportValidationIssue("ImportProfileTargetChanged", "目标配置文件的名称或版本已变化；请重新生成导入计划并重新确认。"));
			return null;
		}
		return requested;
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
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

}
