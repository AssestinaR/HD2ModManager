using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：把 Mod 的物理删除和库快照删除拆开。目录先被删除、隔离或可靠登记为待重启清理，调用方才可移除快照节点。
// Purpose: Separates physical Mod-root removal from snapshot removal. A caller may remove a node only after its root is gone, quarantined, or durably registered for reboot cleanup.
public sealed class ModStorageDeletionService : IModStorageDeletionService
{
	private const string TrashDirectoryName = ".hd2modmanager-trash";
	private const int PendingDocumentVersion = 1;
	private static readonly TimeSpan[] RetryDelays =
	[
		TimeSpan.Zero,
		TimeSpan.FromMilliseconds(70),
		TimeSpan.FromMilliseconds(180),
		TimeSpan.FromMilliseconds(420),
	];

	private readonly StoragePaths _paths;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public ModStorageDeletionService(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<ModStorageDeletionResult> DeleteAsync(
		string nodeRelativePath,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!TryResolveManagedDirectory(nodeRelativePath, out var source, out var normalizedRelativePath, out var validationError))
				return new ModStorageDeletionResult(nodeRelativePath, ModStorageDeletionState.Failed, validationError);

			if (!Directory.Exists(source))
			{
				return File.Exists(source)
					? new ModStorageDeletionResult(normalizedRelativePath, ModStorageDeletionState.Failed, "Mod 存储路径不是目录，已拒绝删除。")
					: new ModStorageDeletionResult(normalizedRelativePath, ModStorageDeletionState.Missing, "存储目录已不存在。");
			}

			var transactionDirectory = Path.Combine(_paths.DeletionTrashDirectory, Guid.NewGuid().ToString("N"));
			var quarantined = Path.Combine(transactionDirectory, Path.GetFileName(source));
			var moveFailure = await TryMoveToQuarantineAsync(source, quarantined, cancellationToken).ConfigureAwait(false);
			if (moveFailure is null)
			{
				var purgeFailure = await TryDeleteDirectoryAsync(quarantined, cancellationToken).ConfigureAwait(false);
				if (purgeFailure is null)
				{
					TryDeleteEmptyDirectory(transactionDirectory);
					TryDeleteEmptyDirectory(_paths.DeletionTrashDirectory);
					return new ModStorageDeletionResult(normalizedRelativePath, ModStorageDeletionState.DeletedImmediately);
				}

				var quarantineRelativePath = NormalizeRelativePath(Path.GetRelativePath(_paths.ModsDirectory, quarantined));
				var schedule = TryScheduleTreeForRestart(quarantined);
				var entry = new PendingDeletionEntry(
					Guid.NewGuid().ToString("N"),
					normalizedRelativePath,
					quarantineRelativePath,
					IsAtOriginalLocation: false,
					RestartRequested: schedule.AnyScheduled,
					DateTimeOffset.UtcNow,
					BuildPendingDetail(purgeFailure, schedule));
				var persistenceFailure = await TryUpsertPendingAsync(entry, cancellationToken).ConfigureAwait(false);
				var detail = CombineDetails(BuildPendingDetail(purgeFailure, schedule), persistenceFailure);
				return new ModStorageDeletionResult(
					normalizedRelativePath,
					schedule.AnyScheduled ? ModStorageDeletionState.PendingRestart : ModStorageDeletionState.PendingCleanup,
					detail);
			}

			// A handle can prevent a same-volume move as well as deletion. Persist the tombstone
			// before asking Windows to delete at restart so synchronization can never resurrect it.
			// TryMoveToQuarantineAsync 在移动之前就建好了事务目录；移动失败时它是空的，
			// 不删掉的话每次失败都会在 .hd2modmanager-trash 里留下一个空目录。
			TryDeleteEmptyDirectory(transactionDirectory);
			var originalEntry = new PendingDeletionEntry(
				Guid.NewGuid().ToString("N"),
				normalizedRelativePath,
				normalizedRelativePath,
				IsAtOriginalLocation: true,
				RestartRequested: false,
				DateTimeOffset.UtcNow,
				$"无法立即隔离目录：{DescribeException(moveFailure)}");
			var originalPersistenceFailure = await TryUpsertPendingAsync(originalEntry, cancellationToken).ConfigureAwait(false);
			if (originalPersistenceFailure is not null)
			{
				return new ModStorageDeletionResult(
					normalizedRelativePath,
					ModStorageDeletionState.Failed,
					CombineDetails($"无法立即隔离目录：{DescribeException(moveFailure)}", originalPersistenceFailure));
			}

			var originalSchedule = TryScheduleTreeForRestart(source);
			if (!originalSchedule.AnyScheduled)
			{
				await TryRemovePendingAsync(originalEntry.Id, cancellationToken).ConfigureAwait(false);
				return new ModStorageDeletionResult(
					normalizedRelativePath,
					ModStorageDeletionState.Failed,
					CombineDetails($"无法立即删除或隔离目录：{DescribeException(moveFailure)}", originalSchedule.Detail));
			}

			var scheduledEntry = originalEntry with
			{
				RestartRequested = true,
				LastDetail = BuildPendingDetail(moveFailure, originalSchedule),
			};
			var schedulePersistenceFailure = await TryUpsertPendingAsync(scheduledEntry, cancellationToken).ConfigureAwait(false);
			return new ModStorageDeletionResult(
				normalizedRelativePath,
				ModStorageDeletionState.PendingRestart,
				CombineDetails(scheduledEntry.LastDetail, schedulePersistenceFailure));
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ModStoragePendingCleanupResult> RecoverPendingAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var document = await LoadPendingAsync(cancellationToken).ConfigureAwait(false);
			if (document.Entries.Count == 0) return new ModStoragePendingCleanupResult(0, 0, Array.Empty<string>());

			var remaining = new List<PendingDeletionEntry>();
			var failures = new List<string>();
			var removed = 0;
			foreach (var entry in document.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!TryResolveManagedDirectory(entry.StoredRelativePath, out var storedPath, out _, out var validationError, allowTrash: true))
				{
					failures.Add($"待删除项 {entry.OriginalRelativePath} 的路径无效：{validationError}");
					remaining.Add(entry);
					continue;
				}

				if (!Directory.Exists(storedPath) && !File.Exists(storedPath))
				{
					removed++;
					continue;
				}

				var deleteFailure = Directory.Exists(storedPath)
					? await TryDeleteDirectoryAsync(storedPath, cancellationToken).ConfigureAwait(false)
					: await TryDeleteFileAsync(storedPath, cancellationToken).ConfigureAwait(false);
				if (deleteFailure is null)
				{
					removed++;
					continue;
				}

				var schedule = entry.RestartRequested ? RebootScheduleResult.None("已登记等待系统重启后清理。") : TryScheduleTreeForRestart(storedPath);
				var updated = entry with
				{
					RestartRequested = entry.RestartRequested || schedule.AnyScheduled,
					LastDetail = BuildPendingDetail(deleteFailure, schedule),
				};
				remaining.Add(updated);
				if (!updated.RestartRequested)
					failures.Add($"待删除项 {entry.OriginalRelativePath} 尚未清理：{DescribeException(deleteFailure)}");
			}

			await SavePendingAsync(document with { Entries = remaining }, cancellationToken).ConfigureAwait(false);
			TryDeleteEmptyDirectory(_paths.DeletionTrashDirectory);
			return new ModStoragePendingCleanupResult(removed, remaining.Count, failures);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<IReadOnlySet<string>> GetSynchronizationExclusionsAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var document = await LoadPendingAsync(cancellationToken).ConfigureAwait(false);
			return document.Entries
				.Where(entry => entry.IsAtOriginalLocation)
				.Select(entry => NormalizeRelativePath(entry.OriginalRelativePath))
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask<Exception?> TryMoveToQuarantineAsync(string source, string destination, CancellationToken cancellationToken)
	{
		Exception? last = null;
		foreach (var delay in RetryDelays)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			try
			{
				ClearDeletionAttributesRecursive(source);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				Directory.Move(source, destination);
				return null;
			}
			catch (Exception exception) when (IsDeleteOperationException(exception))
			{
				last = exception;
			}
		}
		return last ?? new IOException("无法将 Mod 目录移动到受管理回收区。");
	}

	private async ValueTask<Exception?> TryDeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
	{
		Exception? last = null;
		foreach (var delay in RetryDelays)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			try
			{
				if (!Directory.Exists(directory)) return null;
				ClearDeletionAttributesRecursive(directory);
				Directory.Delete(directory, recursive: true);
				return null;
			}
			catch (Exception exception) when (IsDeleteOperationException(exception))
			{
				last = exception;
			}
		}

		// Directory.Delete(recursive:true) 不是原子的：它会先删掉能删的文件，只在被占用的那一个上
		// 失败并抛出，于是磁盘上留下"空目录"或"目录 + 少量残余文件"。把剩余数量写进错误信息，
		// 否则调用方（以及用户）只看到一句 "Access is denied"，无从判断还剩什么。
		// The recursive delete is not atomic: it removes everything it can and only fails on the
		// locked entry, leaving an empty directory or a directory with a few leftovers behind.
		var remaining = CountRemainingEntries(directory);
		var message = last is null ? "无法删除 Mod 目录。" : DescribeException(last);
		return new IOException(remaining > 0 ? $"{message}（目录仍存在，剩余 {remaining} 项）" : message, last);
	}

	private static int CountRemainingEntries(string directory)
	{
		try
		{
			return Directory.Exists(directory)
				? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Count()
				: 0;
		}
		catch (Exception exception) when (IsDeleteOperationException(exception))
		{
			return -1;
		}
	}

	private async ValueTask<Exception?> TryDeleteFileAsync(string path, CancellationToken cancellationToken)
	{
		Exception? last = null;
		foreach (var delay in RetryDelays)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			try
			{
				if (!File.Exists(path)) return null;
				ClearDeletionAttributes(path);
				File.Delete(path);
				return null;
			}
			catch (Exception exception) when (IsDeleteOperationException(exception))
			{
				last = exception;
			}
		}
		return last ?? new IOException("无法删除待清理文件。");
	}

	private static bool IsDeleteOperationException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

	private static void ClearDeletionAttributesRecursive(string directory)
	{
		var pending = new Stack<string>();
		pending.Push(directory);
		while (pending.Count != 0)
		{
			var current = pending.Pop();
			ClearDeletionAttributes(current);
			foreach (var entry in Directory.EnumerateFileSystemEntries(current))
			{
				ClearDeletionAttributes(entry);
				if (!Directory.Exists(entry)) continue;
				var attributes = File.GetAttributes(entry);
				if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
			}
		}
	}

	private static void ClearDeletionAttributes(string path)
	{
		var attributes = File.GetAttributes(path);
		var deletionFriendly = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
		if (deletionFriendly != attributes) File.SetAttributes(path, deletionFriendly);
	}

	private static RebootScheduleResult TryScheduleTreeForRestart(string root)
	{
		if (!OperatingSystem.IsWindows()) return RebootScheduleResult.None("当前平台不支持重启删除。");
		try
		{
			if (File.Exists(root) && !Directory.Exists(root))
				return SchedulePathsForRestart([root]);

			if (!Directory.Exists(root)) return RebootScheduleResult.None("待删除路径已不存在。");
			var files = new List<string>();
			var directories = new List<string>();
			CollectTreePaths(root, files, directories);
			var ordered = files
				.OrderByDescending(GetPathDepth)
				.Concat(directories.OrderByDescending(GetPathDepth))
				.ToArray();
			return SchedulePathsForRestart(ordered);
		}
		catch (Exception exception) when (IsDeleteOperationException(exception))
		{
			return RebootScheduleResult.None($"无法枚举重启删除项：{DescribeException(exception)}");
		}
	}

	private static void CollectTreePaths(string directory, ICollection<string> files, ICollection<string> directories)
	{
		foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
		{
			var attributes = File.GetAttributes(entry);
			if (!Directory.Exists(entry))
			{
				files.Add(entry);
				continue;
			}

			if ((attributes & FileAttributes.ReparsePoint) == 0) CollectTreePaths(entry, files, directories);
			directories.Add(entry);
		}
		directories.Add(directory);
	}

	private static RebootScheduleResult SchedulePathsForRestart(IEnumerable<string> paths)
	{
		var scheduled = 0;
		var failures = new List<string>();
		foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!File.Exists(path) && !Directory.Exists(path)) continue;
			if (MoveFileEx(path, null, MoveFileDelayUntilReboot))
			{
				scheduled++;
				continue;
			}

			var error = Marshal.GetLastWin32Error();
			failures.Add($"{Path.GetFileName(path)}: {new Win32Exception(error).Message} ({error})");
		}
		var detail = failures.Count == 0
			? $"已登记 {scheduled} 个路径等待系统重启后删除。"
			: $"已登记 {scheduled} 个路径等待系统重启后删除；未登记 {failures.Count} 个路径：{string.Join("；", failures.Take(3))}";
		return new RebootScheduleResult(scheduled > 0, failures.Count == 0, detail);
	}

	private async ValueTask<PendingDeletionDocument> LoadPendingAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.PendingDeletionPath)) return new PendingDeletionDocument(PendingDocumentVersion, []);
		try
		{
			await using var stream = File.OpenRead(_paths.PendingDeletionPath);
			var document = await JsonSerializer.DeserializeAsync<PendingDeletionDocument>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
			if (document is null || document.Entries is null)
				throw new InvalidDataException("待删除记录缺少必要字段；为避免待删除 Mod 被同步器重新登记，已停止自动同步。");
			if (document.Version != PendingDocumentVersion)
				throw new InvalidDataException("待删除记录版本不受支持；为避免待删除 Mod 被同步器重新登记，已停止自动同步。");
			return document with { Entries = document.Entries.Where(entry => entry is not null).ToList() };
		}
		catch (JsonException)
		{
			throw new InvalidDataException("待删除记录不是有效 JSON；为避免待删除 Mod 被同步器重新登记，已停止自动同步。");
		}
		catch (FileNotFoundException)
		{
			return new PendingDeletionDocument(PendingDocumentVersion, []);
		}
		catch (DirectoryNotFoundException)
		{
			return new PendingDeletionDocument(PendingDocumentVersion, []);
		}
		catch (IOException exception)
		{
			throw new IOException("无法读取待删除记录；为避免待删除 Mod 被同步器重新登记，已停止自动同步。", exception);
		}
	}

	private async ValueTask SavePendingAsync(PendingDeletionDocument document, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_paths.ModsDirectory);
		if (document.Entries.Count == 0)
		{
			if (File.Exists(_paths.PendingDeletionPath)) File.Delete(_paths.PendingDeletionPath);
			return;
		}

		var temporary = _paths.PendingDeletionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await JsonSerializer.SerializeAsync(stream, document with { Version = PendingDocumentVersion }, cancellationToken: cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			File.Move(temporary, _paths.PendingDeletionPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	private async ValueTask<string?> TryUpsertPendingAsync(PendingDeletionEntry entry, CancellationToken cancellationToken)
	{
		try
		{
			var document = await LoadPendingAsync(cancellationToken).ConfigureAwait(false);
			var entries = document.Entries
				.Where(existing => !string.Equals(existing.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(existing.StoredRelativePath, entry.StoredRelativePath, StringComparison.OrdinalIgnoreCase))
				.Append(entry)
				.ToList();
			await SavePendingAsync(document with { Entries = entries }, cancellationToken).ConfigureAwait(false);
			return null;
		}
		catch (Exception exception) when (IsDeleteOperationException(exception) || exception is InvalidDataException)
		{
			return $"无法写入待删除记录：{DescribeException(exception)}";
		}
	}

	private async ValueTask TryRemovePendingAsync(string id, CancellationToken cancellationToken)
	{
		try
		{
			var document = await LoadPendingAsync(cancellationToken).ConfigureAwait(false);
			var entries = document.Entries.Where(entry => !string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
			await SavePendingAsync(document with { Entries = entries }, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (IsDeleteOperationException(exception) || exception is InvalidDataException)
		{
			_ = exception;
		}
	}

	private bool TryResolveManagedDirectory(string relativePath, out string fullPath, out string normalizedRelativePath, out string? error, bool allowTrash = false)
	{
		normalizedRelativePath = NormalizeRelativePath(relativePath);
		fullPath = string.Empty;
		error = null;
		if (string.IsNullOrWhiteSpace(normalizedRelativePath))
		{
			error = "Mod 存储路径为空，已拒绝删除。";
			return false;
		}

		var modsRoot = Path.GetFullPath(_paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var candidate = Path.GetFullPath(Path.Combine(modsRoot, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
		var protectedRoot = modsRoot + Path.DirectorySeparatorChar;
		if (!candidate.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase))
		{
			error = "Mod 存储路径越过了当前模组库根目录，已拒绝删除。";
			return false;
		}
		if (!allowTrash && (string.Equals(candidate, _paths.DeletionTrashDirectory, StringComparison.OrdinalIgnoreCase)
			|| candidate.StartsWith(_paths.DeletionTrashDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Path.GetFileName(candidate), TrashDirectoryName, StringComparison.OrdinalIgnoreCase)))
		{
			error = "受管理回收区不是可删除 Mod 根目录。";
			return false;
		}

		fullPath = candidate;
		return true;
	}

	private static void TryDeleteEmptyDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static int GetPathDepth(string path)
		=> path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);

	private static string NormalizeRelativePath(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace('\\', '/').Trim('/');

	private static string DescribeException(Exception exception)
		=> string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

	private static string BuildPendingDetail(Exception failure, RebootScheduleResult schedule)
		=> CombineDetails($"立即清理失败：{DescribeException(failure)}", schedule.Detail) ?? "等待后续清理。";

	private static string? CombineDetails(string? first, string? second)
		=> string.Join(" ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

	private const uint MoveFileDelayUntilReboot = 0x00000004;

	private sealed record PendingDeletionDocument(int Version, List<PendingDeletionEntry> Entries);

	private sealed record PendingDeletionEntry(
		string Id,
		string OriginalRelativePath,
		string StoredRelativePath,
		bool IsAtOriginalLocation,
		bool RestartRequested,
		DateTimeOffset CreatedUtc,
		string? LastDetail);

	private sealed record RebootScheduleResult(bool AnyScheduled, bool IsComplete, string? Detail)
	{
		public static RebootScheduleResult None(string? detail = null) => new(false, false, detail);
	}
}
