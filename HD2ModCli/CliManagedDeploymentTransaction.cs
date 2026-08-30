using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A small CLI-only patch transaction around the legacy executor. It moves only
/// files the strict ownership inspector proved are manager-owned, so a failed
/// deploy can restore the previous Patch set instead of leaving GameData empty.
/// External files retain the executor's own rollback transaction.
/// </summary>
internal sealed class CliManagedDeploymentTransaction : IAsyncDisposable
{
	private readonly string _gameDataDirectory;
	private readonly string? _transactionDirectory;
	private readonly IReadOnlyList<PatchBackup> _backups;
	private readonly IReadOnlyList<string> _controlledTargetPaths;
	private readonly string _statePath;
	private readonly bool _stateExisted;
	private readonly byte[]? _stateBytes;
	private bool _finished;

	private CliManagedDeploymentTransaction(
		string gameDataDirectory,
		string? transactionDirectory,
		IReadOnlyList<PatchBackup> backups,
		IReadOnlyList<string> controlledTargetPaths,
		string statePath,
		bool stateExisted,
		byte[]? stateBytes)
	{
		_gameDataDirectory = gameDataDirectory;
		_transactionDirectory = transactionDirectory;
		_backups = backups;
		_controlledTargetPaths = controlledTargetPaths;
		_statePath = statePath;
		_stateExisted = stateExisted;
		_stateBytes = stateBytes;
	}

	public static async ValueTask<CliManagedDeploymentTransaction> BeginAsync(
		DeploymentOwnershipInspection ownership,
		IReadOnlyList<ApplyOperation> operations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(ownership);
		ArgumentNullException.ThrowIfNull(operations);
		if (ownership.Status is not (DeploymentOwnershipStatus.CleanBootstrap or DeploymentOwnershipStatus.Managed)
			|| string.IsNullOrWhiteSpace(ownership.GameDataDirectory))
		{
			throw new CliUserException("DeploymentOwnershipNotProven", "当前 GameData 中的 Patch 归属未被完整证明，不能开始部署事务。");
		}

		var gameData = Path.GetFullPath(ownership.GameDataDirectory);
		var controlledTargets = operations
			.Where(operation => operation.Kind is ApplyOperationKind.DeletePatch or ApplyOperationKind.DeployPatch)
			.Select(operation => TryNormalizeSafePatchTarget(gameData, operation.TargetPath))
			.Where(path => path is not null)
			.Cast<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var statePath = Path.Combine(gameData, JsonActivationStateStore.StateFileName);
		var stateExisted = File.Exists(statePath);
		byte[]? stateBytes = null;
		if (stateExisted)
		{
			if (Directory.Exists(statePath) || IsReparsePoint(statePath))
				throw new CliUserException("ActivationStateChanged", "部署开始前 activation-state.json 已不再是可安全恢复的普通文件。");
			stateBytes = await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
		}

		if (ownership.SafeDeletionTargets.Count == 0)
			return new CliManagedDeploymentTransaction(gameData, null, [], controlledTargets, statePath, stateExisted, stateBytes);

		var gameRoot = Directory.GetParent(gameData)?.FullName
			?? throw new CliUserException("GameRootUnavailable", "无法从 GameData 目录确定游戏根目录。");
		var transactionDirectory = Path.Combine(gameRoot, ".hd2modmanager-deployment-transactions", Guid.NewGuid().ToString("N"));
		var backupDirectory = Path.Combine(transactionDirectory, "patches");
		var backups = new List<PatchBackup>();
		try
		{
			Directory.CreateDirectory(backupDirectory);
			var index = 0;
			foreach (var file in ownership.SafeDeletionTargets)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var source = TryNormalizeSafePatchTarget(gameData, file.TargetPath);
				if (source is null || !PathExistsIncludingReparsePoint(source) || Directory.Exists(source))
					throw new CliUserException("ManagedPatchChanged", $"部署开始前已管理 Patch 不再是可安全移动的普通文件：{file.TargetPath}");
				var destination = Path.Combine(backupDirectory, index++.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".patch");
				File.Move(source, destination, overwrite: false);
				backups.Add(new PatchBackup(source, destination));
			}
			return new CliManagedDeploymentTransaction(gameData, transactionDirectory, backups, controlledTargets, statePath, stateExisted, stateBytes);
		}
		catch
		{
			await RestoreMovedPatchesAsync(backups).ConfigureAwait(false);
			TryDeleteDirectory(transactionDirectory);
			throw;
		}
	}

	public ValueTask CompleteAsync()
	{
		if (_finished) return ValueTask.CompletedTask;
		_finished = true;
		TryDeleteDirectory(_transactionDirectory);
		return ValueTask.CompletedTask;
	}

	public async ValueTask<IReadOnlyList<CliDeploymentDiagnostic>> RollbackAsync()
	{
		if (_finished) return Array.Empty<CliDeploymentDiagnostic>();
		_finished = true;
		var diagnostics = new List<CliDeploymentDiagnostic>();
		foreach (var target in _controlledTargetPaths)
		{
			try
			{
				if (Directory.Exists(target))
				{
					diagnostics.Add(new CliDeploymentDiagnostic("RollbackTargetIsDirectory", "error", true, "rollback", "回滚目标被目录占用，无法安全恢复旧 Patch。", target));
					continue;
				}
				if (!PathExistsIncludingReparsePoint(target)) continue;
				File.Delete(target);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				diagnostics.Add(new CliDeploymentDiagnostic("RollbackTargetDeleteFailed", "error", true, "rollback", exception.Message, target, Details: exception.ToString()));
			}
		}

		foreach (var backup in _backups.Reverse())
		{
			try
			{
				if (!File.Exists(backup.BackupPath))
				{
					diagnostics.Add(new CliDeploymentDiagnostic("RollbackBackupMissing", "error", true, "rollback", "已管理 Patch 的事务备份缺失。", backup.BackupPath));
					continue;
				}
				if (File.Exists(backup.OriginalPath) || Directory.Exists(backup.OriginalPath))
				{
					diagnostics.Add(new CliDeploymentDiagnostic("RollbackOriginalOccupied", "error", true, "rollback", "无法恢复旧 Patch：原始目标已被占用。", backup.OriginalPath));
					continue;
				}
				File.Move(backup.BackupPath, backup.OriginalPath, overwrite: false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				diagnostics.Add(new CliDeploymentDiagnostic("RollbackRestoreFailed", "error", true, "rollback", exception.Message, backup.OriginalPath, Details: exception.ToString()));
			}
		}

		try
		{
			if (_stateExisted)
			{
				if (_stateBytes is null) throw new InvalidDataException("The previous activation state bytes were unavailable.");
				var temporary = _statePath + ".cli-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					await File.WriteAllBytesAsync(temporary, _stateBytes).ConfigureAwait(false);
					File.Move(temporary, _statePath, overwrite: true);
				}
				finally
				{
					if (File.Exists(temporary)) File.Delete(temporary);
				}
			}
			else if (File.Exists(_statePath))
			{
				if (IsReparsePoint(_statePath))
					diagnostics.Add(new CliDeploymentDiagnostic("RollbackStateReparsePoint", "error", true, "rollback", "回滚时 activation-state.json 变成了链接或其他 reparse point。", _statePath));
				else
					File.Delete(_statePath);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliDeploymentDiagnostic("RollbackStateRestoreFailed", "error", true, "rollback", exception.Message, _statePath, Details: exception.ToString()));
		}
		TryDeleteDirectory(_transactionDirectory);
		return diagnostics;
	}

	public async ValueTask DisposeAsync()
	{
		if (!_finished) _ = await RollbackAsync().ConfigureAwait(false);
	}

	private static async ValueTask RestoreMovedPatchesAsync(IEnumerable<PatchBackup> backups)
	{
		foreach (var backup in backups.Reverse())
		{
			try
			{
				if (File.Exists(backup.BackupPath) && !PathExistsIncludingReparsePoint(backup.OriginalPath) && !Directory.Exists(backup.OriginalPath))
					File.Move(backup.BackupPath, backup.OriginalPath, overwrite: false);
			}
			catch { }
		}
		await ValueTask.CompletedTask;
	}

	private static string? TryNormalizeSafePatchTarget(string gameDataDirectory, string? targetPath)
	{
		if (string.IsNullOrWhiteSpace(targetPath)) return null;
		try
		{
			var target = Path.GetFullPath(targetPath);
			if (!string.Equals(Path.GetDirectoryName(target), gameDataDirectory, StringComparison.OrdinalIgnoreCase)) return null;
			return new PatchFileNameParser().TryParse(Path.GetFileName(target), out _) ? target : null;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static bool PathExistsIncludingReparsePoint(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException) { return false; }
		catch (DirectoryNotFoundException) { return false; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static void TryDeleteDirectory(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
	}

	private sealed record PatchBackup(string OriginalPath, string BackupPath);
}
