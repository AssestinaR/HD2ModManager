using System.Diagnostics;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliGameDataIndexExecutionResult(
	bool Success,
	string Operation,
	bool BaseIndexCurrent,
	bool AdvancedEquipmentFactsCurrent,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics);

/// <summary>
/// Applies a previously validated index rebuild under a Manager-data lock.
/// This is intentionally separate from library write sessions: neither the
/// library snapshot nor GameData needs to be modified for index maintenance.
/// </summary>
internal static class CliGameDataIndexExecutor
{
	public static async ValueTask<CliGameDataIndexExecutionResult> ApplyAsync(
		CliGameDataIndexPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var firstValidation = await CliGameDataIndexPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!firstValidation.IsValid || firstValidation.Current is null)
		{
			return new CliGameDataIndexExecutionResult(false, document.Request.Operation, false, false, firstValidation.Diagnostics);
		}

		EnsureManagerIsClosed();
		await using var dataLock = await CliGameDataIndexWriteLock.AcquireAsync(firstValidation.Current.Paths, cancellationToken).ConfigureAwait(false);
		EnsureManagerIsClosed();
		var validation = await CliGameDataIndexPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
		{
			return new CliGameDataIndexExecutionResult(false, document.Request.Operation, false, false, validation.Diagnostics);
		}

		try
		{
			switch (validation.Current.Operation)
			{
				case CliGameDataIndexOperation.RebuildBase:
				{
					var archiveHashes = await File.ReadAllTextAsync(validation.Current.Paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false);
					await CoreServices.CreateAssetArchiveIndexService(validation.Current.Paths)
						.BuildOrRebuildAsync(document.Request.GameDataDirectory, archiveHashes, progress: null, cancellationToken)
						.ConfigureAwait(false);
					break;
				}
				case CliGameDataIndexOperation.RebuildAllEquipmentFacts:
					await CoreServices.CreateAdvancedEquipmentIndexService(validation.Current.Paths)
						.RebuildAllUnitPartFactsAsync(document.Request.GameDataDirectory, progress: null, cancellationToken)
						.ConfigureAwait(false);
					break;
				case CliGameDataIndexOperation.BuildMissingEquipmentFacts:
					await CoreServices.CreateAdvancedEquipmentIndexService(validation.Current.Paths)
						.BuildMissingUnitPartFactsAsync(document.Request.GameDataDirectory, progress: null, cancellationToken)
						.ConfigureAwait(false);
					break;
				default:
					throw new CliUserException("GameDataIndexOperationInvalid", "未知的 GameData 索引计划操作。 ");
			}

			var post = await CliGameDataIndexPlanArtifacts.BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
			var expectedReady = post.Operation switch
			{
				CliGameDataIndexOperation.RebuildBase => post.Document.Inputs.BaseIndexCurrent,
				CliGameDataIndexOperation.RebuildAllEquipmentFacts or CliGameDataIndexOperation.BuildMissingEquipmentFacts
					=> post.Document.Inputs.BaseIndexCurrent && post.Document.Inputs.AdvancedEquipmentFactsCurrent,
				_ => false,
			};
			if (!expectedReady)
			{
				var diagnostics = post.Document.Diagnostics.Concat(
				[
					new CliGameDataDiagnostic("GameDataIndexPostconditionFailed", "error", true,
						"索引操作结束后目标索引状态仍未满足；请检查输出诊断，CLI 未继续执行其他操作。")
				]).ToArray();
				return new CliGameDataIndexExecutionResult(false, document.Request.Operation, post.Document.Inputs.BaseIndexCurrent, post.Document.Inputs.AdvancedEquipmentFactsCurrent, diagnostics);
			}
			return new CliGameDataIndexExecutionResult(true, document.Request.Operation, post.Document.Inputs.BaseIndexCurrent, post.Document.Inputs.AdvancedEquipmentFactsCurrent, post.Document.Diagnostics);
		}
		catch (CliUserException) { throw; }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException
			or FormatException)
		{
			throw new CliUserException("GameDataIndexWriteFailed", $"GameData 索引操作未完成：{exception.Message}");
		}
	}

	private static void EnsureManagerIsClosed()
	{
		try
		{
			var processIds = Process.GetProcessesByName("HD2ModManager")
				.Select(process =>
				{
					try { return process.Id; }
					finally { process.Dispose(); }
				})
				.OrderBy(id => id)
				.ToArray();
			if (processIds.Length != 0)
			{
				throw new CliUserException("ManagerProcessRunning", $"检测到 HD2ModManager 正在运行（PID：{string.Join(", ", processIds)}）。请先关闭所有桌面端实例，再执行索引写入。");
			}
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			throw new CliUserException("ManagerProcessCheckFailed", $"无法可靠枚举 HD2ModManager 进程；为避免与桌面端并发写入，CLI 已拒绝本次索引写入：{exception.Message}");
		}
	}
}

internal sealed class CliGameDataIndexWriteLock : IAsyncDisposable
{
	private readonly string path;
	private FileStream? stream;

	private CliGameDataIndexWriteLock(string path, FileStream stream)
	{
		this.path = path;
		this.stream = stream;
	}

	public static ValueTask<CliGameDataIndexWriteLock> AcquireAsync(StoragePaths paths, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(paths);
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(paths.DataDirectory);
		var path = Path.Combine(paths.DataDirectory, ".hd2mod-cli-index-write.lock");
		try
		{
			var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
			return ValueTask.FromResult(new CliGameDataIndexWriteLock(path, stream));
		}
		catch (IOException)
		{
			throw new CliUserException("GameDataIndexWriteBusy", "另一条 CLI 索引写入正在进行，或遗留锁文件尚未清理。请确认没有其他索引任务后重试。 ");
		}
	}

	public ValueTask DisposeAsync()
	{
		var current = Interlocked.Exchange(ref stream, null);
		if (current is not null) current.Dispose();
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		return ValueTask.CompletedTask;
	}
}
