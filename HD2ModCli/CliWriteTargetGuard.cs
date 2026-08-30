using System.Diagnostics;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Immutable facts about the disk target a CLI mutation intends to use.
/// This deliberately does not authorize a write by itself; callers must hold
/// a <see cref="CliLibraryWriteSession"/> before mutating the target.
/// </summary>
internal sealed record CliWriteTarget(
	string? ManagerRoot,
	string? SettingsPath,
	string? LibraryRoot,
	bool ManagerRootExists,
	bool SettingsExists,
	bool LibraryExists,
	bool LibrarySnapshotExists,
	bool LibraryContentsReadable,
	bool LibraryContainsUnregisteredContent,
	IReadOnlyList<int> RunningManagerProcessIds)
{
	public string? LibrarySnapshotPath
		=> string.IsNullOrWhiteSpace(LibraryRoot) ? null : Path.Combine(LibraryRoot, "library.json");

	public CliImportTarget ToImportTarget()
		=> new(ManagerRoot, SettingsPath, LibraryRoot, LibraryExists, LibrarySnapshotExists, RunningManagerProcessIds);
}

internal sealed record CliWriteTargetIssue(string Code, string Message);

/// <summary>
/// Mutation commands operate only on an existing registered library. Import is
/// the sole exception: it may create a new library or populate an empty one.
/// </summary>
internal enum CliWriteTargetMode
{
	Mutation,
	Import,
}

/// <summary>
/// Result of a no-write target preflight. A non-null snapshot is always read
/// through the strict, non-migrating JSON store.
/// </summary>
internal sealed record CliWriteTargetValidation(
	CliWriteTarget Target,
	CliWriteTargetMode Mode,
	LibrarySnapshot? Snapshot,
	IReadOnlyList<CliWriteTargetIssue> Issues)
{
	public bool IsValid => Issues.Count == 0 && (Snapshot is not null || Mode == CliWriteTargetMode.Import);

	public void ThrowIfInvalid()
	{
		if (IsValid) return;
		var issue = Issues.FirstOrDefault()
			?? new CliWriteTargetIssue("WriteTargetValidationIncomplete", "写入目标验证未返回可用的 Mod 库快照。");
		throw new CliUserException(issue.Code, issue.Message);
	}
}

/// <summary>
/// Shared CLI-side protection for every command that mutates a Mod library.
/// It intentionally requires a real manager installation, an already-created
/// library snapshot, no desktop Manager process, and a strict snapshot read.
/// </summary>
internal static class CliWriteTargetGuard
{
	public static async ValueTask<CliWriteTargetValidation> ValidateAsync(
		CliWorkspace workspace,
		CliWriteTargetMode mode = CliWriteTargetMode.Mutation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var issues = new List<CliWriteTargetIssue>();
		var managerRoot = workspace.ManagerRoot;
		var settingsPath = workspace.SettingsPath;
		var libraryRoot = workspace.LibraryRoot;
		var managerRootExists = !string.IsNullOrWhiteSpace(managerRoot) && Directory.Exists(managerRoot);
		var settingsExists = !string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath);
		var libraryExists = !string.IsNullOrWhiteSpace(libraryRoot) && Directory.Exists(libraryRoot);
		var librarySnapshotExists = libraryExists
			&& !string.IsNullOrWhiteSpace(libraryRoot)
			&& File.Exists(Path.Combine(libraryRoot, "library.json"));
		var libraryContents = InspectLibraryContents(libraryRoot, libraryExists, librarySnapshotExists);
		var managerProcessIds = FindRunningManagerProcessIds();
		var target = new CliWriteTarget(
			managerRoot,
			settingsPath,
			libraryRoot,
			managerRootExists,
			settingsExists,
			libraryExists,
			librarySnapshotExists,
			libraryContents.IsReadable,
			libraryContents.ContainsUnregisteredContent,
			managerProcessIds);

		AddRequiredTargetIssues(workspace, target, mode, libraryContents.Error, issues);
		AddManagerProcessIssue(target, issues);

		LibrarySnapshot? snapshot = null;
		if (CanReadStrictSnapshot(workspace, target))
		{
			try
			{
				snapshot = await CreateStrictStore(CreateStoragePaths(target))
					.TryLoadReadOnlyAsync(cancellationToken)
					.ConfigureAwait(false);
				if (snapshot is null)
					issues.Add(new CliWriteTargetIssue(
						"LibrarySnapshotUnreadable",
						$"无法读取 Mod 库状态文件：{target.LibrarySnapshotPath}"));
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or DirectoryNotFoundException
				or FileNotFoundException)
			{
				issues.Add(new CliWriteTargetIssue(
					"LibrarySnapshotUnreadable",
					$"无法安全读取 Mod 库状态文件：{exception.Message}"));
			}
		}

		return new CliWriteTargetValidation(target, mode, snapshot, issues);
	}

	/// <summary>
	/// Acquires the cross-process Core lease only after a valid preflight, then
	/// repeats the full validation while that lease is held. The returned
	/// session owns the lease until it is asynchronously disposed.
	/// </summary>
	public static async ValueTask<CliLibraryWriteSession> AcquireSessionAsync(
		CliWorkspace workspace,
		CliWriteTargetMode mode = CliWriteTargetMode.Mutation,
		CancellationToken cancellationToken = default)
	{
		var preflight = await ValidateAsync(workspace, mode, cancellationToken).ConfigureAwait(false);
		preflight.ThrowIfInvalid();
		var target = preflight.Target;
		var paths = CreateStoragePaths(target);
		ILibraryWriteLease? lease = null;
		try
		{
			lease = await CoreServices.CreateLibraryWriteLeaseProvider()
				.AcquireAsync(paths.ModsDirectory, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (LibraryWriteLeaseUnavailableException exception)
		{
			throw new CliUserException(
				"LibraryWriteBusy",
				$"目标 Mod 库正被另一条 CLI 写入操作占用：{exception.LibraryRoot}。请等待当前操作完成后重试。");
		}
		catch (UnauthorizedAccessException exception)
		{
			throw new CliUserException("LibraryWriteLeaseUnavailable", $"无法取得 Mod 库写入租约：{exception.Message}");
		}

		try
		{
			var lockedValidation = await ValidateAsync(workspace, mode, cancellationToken).ConfigureAwait(false);
			lockedValidation.ThrowIfInvalid();
			if (!TargetsMatch(target, lockedValidation.Target))
			{
				throw new CliUserException(
					"WriteTargetChanged",
					"取得写入租约期间，管理器或 Mod 库目标已变化；请重新执行命令。");
			}
			return new CliLibraryWriteSession(
				workspace,
				lockedValidation.Target,
				mode,
				paths,
				CreateStrictStore(paths),
				lockedValidation.Snapshot,
				lease);
		}
		catch
		{
			await lease.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	internal static StoragePaths CreateStoragePaths(CliWriteTarget target)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (string.IsNullOrWhiteSpace(target.ManagerRoot) || string.IsNullOrWhiteSpace(target.LibraryRoot))
			throw new InvalidOperationException("A complete write target is required before creating storage paths.");
		return new StoragePaths(target.ManagerRoot, target.LibraryRoot);
	}

	internal static JsonModLibraryStore CreateStrictStore(StoragePaths paths)
		=> new(paths, allowLegacyMigration: false);

	internal static bool TargetsMatch(CliWriteTarget expected, CliWriteTarget actual)
		=> PathsEqual(expected.ManagerRoot, actual.ManagerRoot)
			&& PathsEqual(expected.SettingsPath, actual.SettingsPath)
			&& PathsEqual(expected.LibraryRoot, actual.LibraryRoot);

	private static void AddRequiredTargetIssues(
		CliWorkspace workspace,
		CliWriteTarget target,
		CliWriteTargetMode mode,
		Exception? libraryContentsError,
		ICollection<CliWriteTargetIssue> issues)
	{
		if (string.IsNullOrWhiteSpace(target.ManagerRoot))
			issues.Add(new CliWriteTargetIssue(
				"ManagerRootRequired",
				"写入型 CLI 命令必须提供 --manager-root，以便验证管理器设置并拒绝桌面端并发写入。"));
		else if (!target.ManagerRootExists)
			issues.Add(new CliWriteTargetIssue("ManagerRootNotFound", $"管理器目录不存在：{target.ManagerRoot}"));

		if (!target.SettingsExists)
			issues.Add(new CliWriteTargetIssue(
				"ManagerSettingsRequired",
				"写入型 CLI 命令要求 --manager-root 中存在 settings.json。"));
		else if (workspace.Settings is null)
			issues.Add(new CliWriteTargetIssue(
				"ManagerSettingsUnreadable",
				$"无法安全读取管理器设置：{target.SettingsPath}"));

		if (string.IsNullOrWhiteSpace(target.LibraryRoot))
		{
			issues.Add(new CliWriteTargetIssue(
				"LibraryNotConfigured",
				"未能确定目标 Mod 库。请在 settings.json 中配置 ModLibraryFolder，或显式传入 --library。"));
			return;
		}
		if (!target.LibraryExists)
		{
			if (mode == CliWriteTargetMode.Mutation)
				issues.Add(new CliWriteTargetIssue("LibraryDirectoryNotFound", $"目标 Mod 库目录不存在：{target.LibraryRoot}"));
			else
			{
				var parent = Directory.GetParent(target.LibraryRoot)?.FullName;
				if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
				{
					issues.Add(new CliWriteTargetIssue(
						"LibraryParentNotFound",
						$"目标 Mod 库的父目录不存在：{parent ?? target.LibraryRoot}。CLI 不会在未验证的多层路径中创建新库。"));
				}
			}
			return;
		}
		if (!target.LibrarySnapshotExists)
		{
			if (!target.LibraryContentsReadable)
			{
				issues.Add(new CliWriteTargetIssue(
					"LibraryTargetUnreadable",
					$"无法检查目标 Mod 库是否为空：{libraryContentsError?.Message ?? target.LibraryRoot}"));
				return;
			}
			if (mode == CliWriteTargetMode.Mutation || target.LibraryContainsUnregisteredContent)
			{
				issues.Add(new CliWriteTargetIssue(
					"LibrarySnapshotMissing",
					$"目标 Mod 库缺少 library.json：{target.LibrarySnapshotPath}。CLI 不会迁移旧数据或接管未登记内容。"));
			}
		}
	}

	private static void AddManagerProcessIssue(CliWriteTarget target, ICollection<CliWriteTargetIssue> issues)
	{
		if (target.RunningManagerProcessIds.Count == 0) return;
		if (target.RunningManagerProcessIds.Contains(-1))
		{
			issues.Add(new CliWriteTargetIssue(
				"ManagerProcessCheckFailed",
				"无法可靠枚举 HD2ModManager 进程；为避免与桌面端并发写入，CLI 已拒绝本次写入。"));
			return;
		}
		issues.Add(new CliWriteTargetIssue(
			"ManagerProcessRunning",
			$"检测到 HD2ModManager 正在运行（PID：{string.Join(", ", target.RunningManagerProcessIds)}）。请先关闭所有桌面端实例，再执行写入命令。"));
	}

	private static bool CanReadStrictSnapshot(CliWorkspace workspace, CliWriteTarget target)
		=> target.ManagerRootExists
			&& target.SettingsExists
			&& workspace.Settings is not null
			&& target.LibraryExists
			&& target.LibrarySnapshotExists;

	private static CliLibraryContentsInspection InspectLibraryContents(
		string? libraryRoot,
		bool libraryExists,
		bool librarySnapshotExists)
	{
		if (!libraryExists || librarySnapshotExists || string.IsNullOrWhiteSpace(libraryRoot))
			return new CliLibraryContentsInspection(IsReadable: true, ContainsUnregisteredContent: false, Error: null);
		try
		{
			var containsUnregisteredContent = Directory
				.EnumerateFileSystemEntries(libraryRoot)
				.Any(path => !string.Equals(
					Path.GetFileName(path),
					".hd2mod-cli-write.lock",
					StringComparison.OrdinalIgnoreCase));
			return new CliLibraryContentsInspection(IsReadable: true, containsUnregisteredContent, Error: null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new CliLibraryContentsInspection(IsReadable: false, ContainsUnregisteredContent: false, Error: exception);
		}
	}

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
			return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
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

	private static IReadOnlyList<int> FindRunningManagerProcessIds()
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
			// An uncertain process list must never be treated as a safe write window.
			return [-1];
		}
	}

	private sealed record CliLibraryContentsInspection(
		bool IsReadable,
		bool ContainsUnregisteredContent,
		Exception? Error);
}
