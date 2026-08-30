using System.Diagnostics;
using HD2ModCore.Infrastructure;

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

		var target = await ValidateTargetAsync(workspace, issues, cancellationToken).ConfigureAwait(false);
		return new CliImportValidationResult(document, current, target, issues);
	}

	private static async ValueTask<CliImportTarget> ValidateTargetAsync(
		CliWorkspace workspace,
		ICollection<CliImportValidationIssue> issues,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			issues.Add(new CliImportValidationIssue("ManagerRootRequired", "写入型导入必须提供 --manager-root，以便使用管理器的真实缓存根并检查桌面端未运行。"));
		if (workspace.SettingsPath is null || !File.Exists(workspace.SettingsPath))
			issues.Add(new CliImportValidationIssue("ManagerSettingsRequired", "写入型导入要求 --manager-root 中存在 settings.json。"));
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
		{
			issues.Add(new CliImportValidationIssue("LibraryNotConfigured", "未能确定目标 Mod 库。请在 settings.json 中配置 ModLibraryFolder，或显式传入 --library。"));
			return new CliImportTarget(workspace.ManagerRoot, workspace.SettingsPath, null, false, false, FindRunningManagerProcesses());
		}

		var libraryRoot = workspace.LibraryRoot;
		var libraryExists = Directory.Exists(libraryRoot);
		var librarySnapshotPath = Path.Combine(libraryRoot, "library.json");
		var snapshotExists = File.Exists(librarySnapshotPath);
		if (libraryExists && !snapshotExists)
		{
			try
			{
				if (Directory.EnumerateFileSystemEntries(libraryRoot).Any())
					issues.Add(new CliImportValidationIssue("LibrarySnapshotMissing", "目标 Mod 库已有文件但没有 library.json；CLI 不会猜测或接管未登记内容。"));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CliImportValidationIssue("LibraryTargetUnreadable", $"无法检查目标 Mod 库：{exception.Message}"));
			}
		}
		if (!libraryExists)
		{
			var parent = Directory.GetParent(libraryRoot)?.FullName;
			if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
				issues.Add(new CliImportValidationIssue("LibraryParentNotFound", $"目标 Mod 库的父目录不存在：{parent ?? libraryRoot}"));
		}

		if (snapshotExists)
		{
			try
			{
				_ = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (CliUserException exception)
			{
				issues.Add(new CliImportValidationIssue(exception.Code, exception.Message));
			}
		}

		// A second Manager installation can still target the exact same Mod library.
		// Until the desktop application participates in the same OS write lease, a
		// write-side CLI command must conservatively wait for every Manager process
		// to close rather than trusting only the executable directory passed here.
		var managerProcesses = FindRunningManagerProcesses();
		if (managerProcesses.Count > 0)
			issues.Add(new CliImportValidationIssue(
				"ManagerProcessRunning",
				$"检测到 HD2ModManager 正在运行（PID：{string.Join(", ", managerProcesses)}）。请先关闭桌面端，再执行 apply，避免同时写入 Mod 库。"));
		return new CliImportTarget(workspace.ManagerRoot, workspace.SettingsPath, libraryRoot, libraryExists, snapshotExists, managerProcesses);
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

	private static IReadOnlyList<int> FindRunningManagerProcesses()
	{
		try
		{
			return Process.GetProcessesByName("HD2ModManager")
				.Select(process =>
				{
					try { return process.Id; }
					finally { process.Dispose(); }
				})
				.OrderBy(id => id)
				.ToArray();
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			// A failed enumeration must not be treated as "definitely closed" for a
			// write command against this manager root.
			return [-1];
		}
	}
}
