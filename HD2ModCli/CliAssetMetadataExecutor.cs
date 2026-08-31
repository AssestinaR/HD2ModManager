using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliAssetMetadataExecutionResult(
	bool Success,
	bool SettingsUpdated,
	bool RollbackAttempted,
	bool RollbackSucceeded,
	string? PreservedBackupDirectory,
	CliAssetMetadataSnapshot? Local,
	IReadOnlyList<CliAssetMetadataDiagnostic> Diagnostics);

/// <summary>
/// Commits only the hash-verified staged payload created by the plan command.
/// No network request is issued here; an interrupted commit restores the five
/// managed metadata files and settings.json from a local journal.
/// </summary>
internal static class CliAssetMetadataExecutor
{
	public static async ValueTask<CliAssetMetadataExecutionResult> ApplyAsync(
		CliAssetMetadataPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);

		var firstValidation = await CliAssetMetadataPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!firstValidation.IsValid || firstValidation.Current is null)
			return Failure(firstValidation.Diagnostics);

		CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
		var paths = new StoragePaths(workspace.ManagerRoot!);
		await using var dataLock = await CliManagerDataWriteLock.AcquireAsync(paths, cancellationToken).ConfigureAwait(false);
		CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
		var validation = await CliAssetMetadataPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
			return Failure(validation.Diagnostics);

		var journal = await CreateJournalAsync(paths, workspace.SettingsPath!, cancellationToken).ConfigureAwait(false);
		try
		{
			await CopyStagedResourcesAsync(document, paths, cancellationToken).ConfigureAwait(false);
			await UpdateLastCheckUtcAsync(workspace.SettingsPath!, document.Inputs.SettingsSha256, cancellationToken).ConfigureAwait(false);

			var postDiagnostics = new List<CliAssetMetadataDiagnostic>();
			var post = await CliAssetMetadataPlanArtifacts.CaptureSnapshotAsync(paths, postDiagnostics, cancellationToken).ConfigureAwait(false);
			if (!post.IsCompleteAndVerified
				|| !string.Equals(post.Fingerprint, document.Staging.Snapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("提交后本地资产元数据摘要与计划暂存内容不一致。");
			}

			TryDeleteDirectory(journal.BackupDirectory);
			return new CliAssetMetadataExecutionResult(
				true,
				true,
				false,
				false,
				null,
				post,
				DistinctDiagnostics(validation.Diagnostics.Concat(postDiagnostics)));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await TryRestoreAsync(journal, paths, workspace.SettingsPath!, CancellationToken.None).ConfigureAwait(false);
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
		{
			var rollbackSucceeded = await TryRestoreAsync(journal, paths, workspace.SettingsPath!, cancellationToken).ConfigureAwait(false);
			var diagnostics = validation.Diagnostics.Concat(
			[
				new CliAssetMetadataDiagnostic(
					"AssetMetadataCommitFailed",
					"error",
					true,
					$"资产元数据提交失败：{exception.Message}",
					paths.ResourcesDirectory),
				new CliAssetMetadataDiagnostic(
					rollbackSucceeded ? "AssetMetadataRollbackSucceeded" : "AssetMetadataRollbackFailed",
					rollbackSucceeded ? "warning" : "error",
					!rollbackSucceeded,
					rollbackSucceeded ? "已恢复提交前的本地资产元数据与设置。" : "无法完整恢复提交前内容；已保留备份目录供人工检查。",
					rollbackSucceeded ? null : journal.BackupDirectory),
			]);
			if (rollbackSucceeded) TryDeleteDirectory(journal.BackupDirectory);
			return new CliAssetMetadataExecutionResult(
				false,
				false,
				true,
				rollbackSucceeded,
				rollbackSucceeded ? null : journal.BackupDirectory,
				null,
				DistinctDiagnostics(diagnostics));
		}
	}

	private static async ValueTask<CliAssetMetadataCommitJournal> CreateJournalAsync(
		StoragePaths paths,
		string settingsPath,
		CancellationToken cancellationToken)
	{
		var backupDirectory = Path.Combine(paths.DataDirectory, ".hd2mod-cli-asset-metadata-backup-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(backupDirectory);
		var existingFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (var fileName in CliAssetMetadataPlanArtifacts.GetRequiredFileNames())
			{
				var source = Path.Combine(paths.ResourcesDirectory, fileName);
				var exists = File.Exists(source);
				existingFiles[fileName] = exists;
				if (exists)
				{
					var destination = Path.Combine(backupDirectory, "resources", fileName);
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					await CopyFileAtomicAsync(source, destination, cancellationToken).ConfigureAwait(false);
				}
			}

			var settingsExisted = File.Exists(settingsPath);
			if (!settingsExisted)
				throw new FileNotFoundException("settings.json 不存在，无法在提交资产元数据后安全记录检查时间。", settingsPath);
			var backupSettings = Path.Combine(backupDirectory, "settings.json");
			await CopyFileAtomicAsync(settingsPath, backupSettings, cancellationToken).ConfigureAwait(false);
			return new CliAssetMetadataCommitJournal(backupDirectory, existingFiles, settingsExisted);
		}
		catch
		{
			TryDeleteDirectory(backupDirectory);
			throw;
		}
	}

	private static async ValueTask CopyStagedResourcesAsync(
		CliAssetMetadataPlanDocument document,
		StoragePaths targetPaths,
		CancellationToken cancellationToken)
	{
		var sourceDirectory = CliAssetMetadataPlanArtifacts.GetStagingResourcesDirectory(document);
		Directory.CreateDirectory(targetPaths.ResourcesDirectory);
		foreach (var fileName in CliAssetMetadataPlanArtifacts.GetRequiredFileNames())
		{
			var source = Path.Combine(sourceDirectory, fileName);
			if (!File.Exists(source))
				throw new FileNotFoundException($"暂存资产元数据文件不存在：{fileName}", source);
			var destination = Path.Combine(targetPaths.ResourcesDirectory, fileName);
			await CopyFileAtomicAsync(source, destination, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async ValueTask UpdateLastCheckUtcAsync(
		string settingsPath,
		string expectedSha256,
		CancellationToken cancellationToken)
	{
		var bytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken).ConfigureAwait(false);
		var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
		if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
			throw new IOException("settings.json 在提交期间已变化。");
		var root = JsonNode.Parse(bytes) as JsonObject
			?? throw new InvalidDataException("settings.json 根节点必须是对象。");
		root["LastAssetMetadataCheckUtc"] = DateTime.UtcNow;
		await WriteTextAtomicallyAsync(settingsPath, root.ToJsonString(CliOutput.JsonOptions), cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<bool> TryRestoreAsync(
		CliAssetMetadataCommitJournal journal,
		StoragePaths paths,
		string settingsPath,
		CancellationToken cancellationToken)
	{
		try
		{
			foreach (var fileName in CliAssetMetadataPlanArtifacts.GetRequiredFileNames())
			{
				var target = Path.Combine(paths.ResourcesDirectory, fileName);
				if (journal.ExistingFiles.TryGetValue(fileName, out var existed) && existed)
				{
					var backup = Path.Combine(journal.BackupDirectory, "resources", fileName);
					if (!File.Exists(backup)) return false;
					await CopyFileAtomicAsync(backup, target, cancellationToken).ConfigureAwait(false);
				}
				else if (File.Exists(target))
				{
					File.Delete(target);
				}
			}
			if (journal.SettingsExisted)
			{
				var backupSettings = Path.Combine(journal.BackupDirectory, "settings.json");
				if (!File.Exists(backupSettings)) return false;
				await CopyFileAtomicAsync(backupSettings, settingsPath, cancellationToken).ConfigureAwait(false);
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
	}

	private static async ValueTask CopyFileAtomicAsync(string source, string destination, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
			}
			File.Move(temporary, destination, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	private static async ValueTask WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
	{
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	private static CliAssetMetadataExecutionResult Failure(IEnumerable<CliAssetMetadataDiagnostic> diagnostics)
		=> new(false, false, false, false, null, null, DistinctDiagnostics(diagnostics));

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	private static IReadOnlyList<CliAssetMetadataDiagnostic> DistinctDiagnostics(IEnumerable<CliAssetMetadataDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Message, diagnostic.Path })
			.Select(group => group.First())
			.ToArray();

	private sealed record CliAssetMetadataCommitJournal(
		string BackupDirectory,
		IReadOnlyDictionary<string, bool> ExistingFiles,
		bool SettingsExisted);
}
