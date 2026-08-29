using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Commits only fully staged same-key repair candidates, with independent portable backups outside the Mod library.
public sealed class ModRepairBatchService : IModRepairBatchService
{
    private readonly IModSameKeyReconstructionService reconstruction;
    private readonly IPatchFileNameParser fileNameParser;
    private readonly StoragePaths paths;
	private readonly Action? initializationSeam;
	private readonly Action<string>? commitSeam;
	private readonly Func<string, string, Task> manifestWriter;
	private readonly IModInformationCenter? informationCenter;
	private readonly IModInformationReader? informationReader;

	public ModRepairBatchService(StoragePaths paths, IModSameKeyReconstructionService reconstruction, IPatchFileNameParser fileNameParser, Action? initializationSeam = null, Action<string>? commitSeam = null, Func<string, string, Task>? manifestWriter = null, IModInformationCenter? informationCenter = null, IModInformationReader? informationReader = null)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
		this.reconstruction = reconstruction ?? throw new ArgumentNullException(nameof(reconstruction));
		this.fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
		this.initializationSeam = initializationSeam;
		this.commitSeam = commitSeam;
		this.manifestWriter = manifestWriter ?? WriteManifestAtomicallyAsync;
		this.informationCenter = informationCenter;
		this.informationReader = informationReader;
	}
    public async ValueTask<ModRepairBatchResult> RepairAsync(IReadOnlyList<ModNode> sourceNodes, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null, Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var batchOperationId = operationId.GetValueOrDefault(Guid.NewGuid());
        var createdUtc = DateTimeOffset.UtcNow;
        var sources = sourceNodes.DistinctBy(node => node.Id).ToArray();
        var batchDirectory = Path.Combine(paths.AppRootDirectory, "backups", stamp);
        // Keep the audit manifest outside backup staging so backup/restore cleanup cannot remove it.
        var manifestDirectory = Path.Combine(paths.DataDirectory, "repair-manifests", stamp);
        var stagingRoot = Path.Combine(Directory.GetParent(Path.GetFullPath(modsRootDirectory))?.FullName ?? Path.GetFullPath(modsRootDirectory), ".hd2mod-repair-staging", stamp);
        var results = new List<ModRepairBatchModResult>();
        var finalizedModIds = new HashSet<ModNodeId>();

        var sequence = 0L;
        var completedModCount = 0;
        var batchFailed = false;
        void Report(Guid operationId, Guid? parentId, OperationKind kind, OperationStage stage, OperationState state, long completed, long total, string text, string stageId, string? issueCode = null)
        {
            try
            {
                progress?.Report(new OperationProgressEvent(operationId, parentId, kind, stage, state, completed, total, text, issueCode, DateTimeOffset.UtcNow, sequence++, stageId, text));
            }
            catch (Exception exception)
            {
                // Telemetry is best effort. A consumer callback must never alter committed business state.
                System.Diagnostics.Debug.WriteLine($"[ModRepairBatch] progress callback failed: {exception}");
            }
        }
        IProgress<OperationProgressEvent> ChildProgress(Guid childId)
            => new SynchronousProgress<OperationProgressEvent>(e =>
            {
                // The batch owns each item's single terminal event; inner reconstruction terminals are progress only.
                if (!e.IsTerminal) Report(childId, batchOperationId, OperationKind.RepairBatchItem, e.Stage, e.State, e.Completed, e.Total, e.Message ?? e.StageText ?? string.Empty, e.StageId ?? "RepairMod");
            });

        Report(batchOperationId, null, OperationKind.RepairBatch, OperationStage.Preparing, OperationState.Started, 0, sources.Length, "正在准备批量修复", "BatchPrepare");
        var canceled = false;
        void FinishMod(Guid childOperationId, ModRepairBatchModResult item, OperationStage stage, OperationState state, string message, string issueCode)
        {
            if (!finalizedModIds.Add(item.NodeId)) return;
            if (item.Status is ModRepairBatchModStatus.CandidateFailed or ModRepairBatchModStatus.CommitFailed) batchFailed = true;
            results.Add(item);
            completedModCount++;
            Report(childOperationId, batchOperationId, OperationKind.RepairBatchItem, stage, state, 1, 1, message, "Finalize", issueCode);
            Report(batchOperationId, null, OperationKind.RepairBatch, OperationStage.Processing, OperationState.Progress, completedModCount, sources.Length, $"已完成 {completedModCount}/{sources.Length} 个 Mod", "ModTerminal");
        }
        ModRepairBatchResult? result = null;
        try
        {
            // Initialization is part of the auditable operation, not an untracked prelude.
            Directory.CreateDirectory(batchDirectory);
            Directory.CreateDirectory(manifestDirectory);
            Directory.CreateDirectory(stagingRoot);
            initializationSeam?.Invoke();
            Report(batchOperationId, null, OperationKind.RepairBatch, OperationStage.Preparing, OperationState.Started, 0, sources.Length, "正在准备批量修复", "BatchPrepare");
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var childOperationId = Guid.NewGuid();
                if (canceled || cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.NotStarted, "取消后未开始处理。", null, null, null, "NotStarted"), OperationStage.Canceled, OperationState.Canceled, "取消后未开始处理", "NotStarted");
                    continue;
                }
                Report(batchOperationId, null, OperationKind.RepairBatch, OperationStage.Processing, OperationState.Progress, index, sources.Length, $"正在处理第 {index + 1}/{sources.Length} 个 Mod", "RepairMod");
                Report(childOperationId, batchOperationId, OperationKind.RepairBatchItem, OperationStage.Processing, OperationState.Started, 0, 1, "正在准备修复", "BatchPrepare");
                var sourceDirectory = Path.Combine(modsRootDirectory, source.RelativePath);
                var stagingDirectory = Path.Combine(stagingRoot, source.Id.Value.ToString("N"));
                try
                {
                var state = await reconstruction.InspectAsync(source, modsRootDirectory, gameDataDirectory, cancellationToken, ChildProgress(childOperationId), childOperationId).ConfigureAwait(false);
                if (!state.CanWrite)
                {
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.SkippedNotRepairable, DescribeIssues(state.Issues), null, null, null), OperationStage.Completed, OperationState.Completed, "已跳过：不满足安全修复条件", "NotRepairable"); continue;
                }

                var candidate = await reconstruction.GenerateCandidateAsync(source, modsRootDirectory, gameDataDirectory, stagingDirectory, cancellationToken, ChildProgress(childOperationId), childOperationId).ConfigureAwait(false);
                if (!candidate.IsSuccessful || string.IsNullOrWhiteSpace(candidate.OutputDirectory) || !Directory.Exists(candidate.OutputDirectory))
                {
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.CandidateFailed, DescribeIssues(candidate.Issues), candidate.OutputDirectory, null, candidate.ReportJsonPath), OperationStage.Failed, OperationState.Failed, "候选生成失败", "CandidateFailed"); continue;
                }

                var backupDirectory = Path.Combine(batchDirectory, source.RelativePath);
                cancellationToken.ThrowIfCancellationRequested();
                CopyPatchFiles(sourceDirectory, backupDirectory, overwrite: false);
                Report(childOperationId, batchOperationId, OperationKind.RepairBatchItem, OperationStage.Processing, OperationState.Progress, 0, 1, "已完成备份，正在提交", "BackupCompleted");
                // Commit boundary: after this check no cancellation is observed until replacement and recovery finish.
                try
                {
                    Report(childOperationId, batchOperationId, OperationKind.RepairBatchItem, OperationStage.Processing, OperationState.Progress, 0, 1, "开始提交", "CommitStarted");
					ReplacePatchFiles(sourceDirectory, candidate.OutputDirectory);
					// The filesystem commit is authoritative.  Invalidate derived Mod facts
					// immediately afterwards so a subsequent reader cannot reuse the old
					// Patch/Unit/graph snapshot.  Use a non-cancelable token: cancellation
					// must never leave a successfully committed Mod paired with stale facts.
					await InvalidateCommittedNodeAsync(source.Id).ConfigureAwait(false);
					Report(childOperationId, batchOperationId, OperationKind.RepairBatchItem, OperationStage.Processing, OperationState.Progress, 1, 1, "提交完成", "Committed");
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.Repaired, "候选已通过内部检查并完成提交。", candidate.OutputDirectory, backupDirectory, candidate.ReportJsonPath, "Finalize", true, true), OperationStage.Completed, OperationState.Completed, "已完成修复", "Repaired");
                }
				catch (Exception exception)
				{
                    var restoreAttempted = true;
                    var restoreCompleted = false;
					try
					{
						RestorePatchFiles(sourceDirectory, backupDirectory);
						// A reader may have observed the partially replaced directory before
						// the commit failed.  Drop that result after restoring the backup too.
						await InvalidateCommittedNodeAsync(source.Id).ConfigureAwait(false);
						restoreCompleted = true;
                    }
                    catch (Exception restoreException)
                    {
                        FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.CommitFailed, $"提交失败（{exception.GetType().Name}）：{exception.Message}；恢复失败（{restoreException.GetType().Name}）：{restoreException.Message}", candidate.OutputDirectory, backupDirectory, candidate.ReportJsonPath, "CommitFailed", true, false, restoreAttempted, false), OperationStage.Failed, OperationState.Failed, "提交失败且恢复失败", "CommitFailed");
                        continue;
                    }
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.CommitFailed, $"提交失败（{exception.GetType().Name}）：{exception.Message}；已恢复备份。", candidate.OutputDirectory, backupDirectory, candidate.ReportJsonPath, "CommitFailed", true, false, restoreAttempted, restoreCompleted), OperationStage.Failed, OperationState.Failed, "提交失败，已恢复备份", "CommitFailed");
                }
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.Canceled, "当前 Mod 在安全边界取消。", null, null, null, "Canceled"), OperationStage.Canceled, OperationState.Canceled, "已取消", "Canceled");
                }
                catch (Exception exception)
                {
                    FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.CandidateFailed, $"未知异常（{exception.GetType().Name}）：{exception.Message}", null, null, null, "CandidateFailed"), OperationStage.Failed, OperationState.Failed, "处理失败", "BatchModFailed");
                }
            }

            result = CreateResult();
            Report(batchOperationId, null, OperationKind.RepairBatch, canceled ? OperationStage.Canceled : OperationStage.Finalizing, canceled ? OperationState.Canceled : OperationState.Progress, completedModCount, sources.Length, "正在写入批次清单", "WriteBatchManifest");
        }
        catch (Exception exception)
        {
            batchFailed = true;
            System.Diagnostics.Debug.WriteLine($"[ModRepairBatch] unexpected batch failure: {exception}");
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                if (finalizedModIds.Contains(source.Id)) continue;
                var childOperationId = Guid.NewGuid();
                FinishMod(childOperationId, new(source.Id, source.Metadata.Name, ModRepairBatchModStatus.NotStarted, $"批次初始化/编排失败，未开始处理：{exception.Message}", null, null, null, "NotStarted"), OperationStage.Failed, OperationState.Failed, "未开始处理", "BatchFailed");
            }
        }
        finally
        {
            result ??= CreateResult();
            var manifestPath = result.ManifestPath;
            try
            {
                await manifestWriter(manifestPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Manifest auditing must remain visible even when the filesystem rejects the write.
                System.Diagnostics.Debug.WriteLine($"[ModRepairBatch] manifest write failed: {exception}");
                result = result with { ManifestWriteFailed = true, ManifestIssueCode = "ManifestWriteFailed" };
                batchFailed = true;
                Report(batchOperationId, null, OperationKind.RepairBatch, OperationStage.Finalizing, OperationState.Progress, completedModCount, sources.Length, $"批次清单写入失败：{exception.Message}", "ManifestWriteFailed", "ManifestWriteFailed");
            }
			// Candidate directories are retained when they contain audit artifacts, but
			// a batch that never produced a candidate must not leave empty staging roots.
			TryDeleteEmptyStagingRoot(stagingRoot);
            var finalState = canceled ? OperationState.Canceled : batchFailed ? OperationState.Failed : OperationState.Completed;
            var finalStage = canceled ? OperationStage.Canceled : batchFailed ? OperationStage.Failed : OperationStage.Completed;
            Report(batchOperationId, null, OperationKind.RepairBatch, finalStage, finalState, completedModCount, sources.Length, canceled ? "批次已取消" : batchFailed ? "批次失败" : "批次已完成", "Finalize", batchFailed ? "BatchFailed" : null);
        }
        return result!;

        ModRepairBatchResult CreateResult()
        {
            var manifestPath = Path.Combine(manifestDirectory, "backup.json");
            return new ModRepairBatchResult(batchDirectory, manifestPath, sources.Length, results.Count(item => item.Status == ModRepairBatchModStatus.Repaired), results.Count(item => item.Status == ModRepairBatchModStatus.SkippedNotRepairable), results.Count(item => item.Status is ModRepairBatchModStatus.CandidateFailed or ModRepairBatchModStatus.CommitFailed), results.ToArray(), results.Count(item => item.Status != ModRepairBatchModStatus.NotStarted), results.Count(item => item.Status == ModRepairBatchModStatus.Canceled), results.Count(item => item.Status == ModRepairBatchModStatus.NotStarted), batchOperationId, createdUtc, DateTimeOffset.UtcNow);
        }
    }

	private static async Task WriteManifestAtomicallyAsync(string manifestPath, string json)
    {
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream))
		{
			await writer.WriteAsync(json).ConfigureAwait(false);
			await writer.FlushAsync().ConfigureAwait(false);
			await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
		}
		File.Move(temporaryPath, manifestPath, overwrite: true);
	}

	private async ValueTask InvalidateCommittedNodeAsync(ModNodeId nodeId)
	{
		try
		{
			// The source Patch directory has already crossed its commit boundary.  Clear
			// both transient decoded payloads and persistent derived facts before any
			// subsequent operation can observe the replacement.
			informationReader?.InvalidateNode(nodeId);
			if (informationCenter is not null)
				await informationCenter.InvalidateNodeAsync(nodeId, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			// Cache invalidation is best effort after the physical commit.  Do not
			// roll back a valid repair because a derived-data index is unavailable;
			// log loudly so the next synchronization can recover it.
			System.Diagnostics.Debug.WriteLine($"[ModRepairBatch] cache invalidation failed for {nodeId}: {exception}");
		}
	}

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private void CopyPatchFiles(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException($"Mod directory does not exist: {sourceDirectory}");
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourcePath in EnumeratePatchFiles(sourceDirectory))
        {
            File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), overwrite);
        }
    }

    private void ReplacePatchFiles(string sourceDirectory, string candidateDirectory)
    {
        var candidates = EnumeratePatchFiles(candidateDirectory).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("重构候选不包含任何 Patch 或 sidecar 文件。");
        foreach (var sourcePath in EnumeratePatchFiles(sourceDirectory).ToArray()) File.Delete(sourcePath);
        commitSeam?.Invoke("AfterDelete");
        foreach (var candidatePath in candidates) File.Copy(candidatePath, Path.Combine(sourceDirectory, Path.GetFileName(candidatePath)), overwrite: false);
        commitSeam?.Invoke("AfterCopy");
    }

    private void RestorePatchFiles(string sourceDirectory, string backupDirectory)
    {
        foreach (var sourcePath in EnumeratePatchFiles(sourceDirectory).ToArray()) File.Delete(sourcePath);
        CopyPatchFiles(backupDirectory, sourceDirectory, overwrite: false);
    }

    private IEnumerable<string> EnumeratePatchFiles(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => fileNameParser.TryParse(Path.GetFileName(path), out _));

	private static void TryDeleteEmptyStagingRoot(string stagingRoot)
	{
		try
		{
			if (!Directory.Exists(stagingRoot)
				|| Directory.EnumerateFileSystemEntries(stagingRoot).Any())
				return;
			Directory.Delete(stagingRoot, recursive: false);

			var batchRoot = Directory.GetParent(stagingRoot);
			if (batchRoot is not null
				&& string.Equals(batchRoot.Name, ".hd2mod-repair-staging", StringComparison.OrdinalIgnoreCase)
				&& Directory.Exists(batchRoot.FullName)
				&& !Directory.EnumerateFileSystemEntries(batchRoot.FullName).Any())
				Directory.Delete(batchRoot.FullName, recursive: false);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

    private static string DescribeIssues(IReadOnlyList<CoreIssue> issues)
        => issues.Count == 0 ? "不满足安全重构条件。" : string.Join("；", issues.Take(3).Select(issue => issue.Message));
}
