using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliBatchPatchRepairExecutionMod(
	string NodeId,
	string Name,
	string Status,
	string? Detail,
	string? CandidateDirectory,
	string? BackupDirectory,
	string? ReportPath,
	bool BackupCompleted,
	bool CommitCompleted,
	bool RestoreAttempted,
	bool RestoreCompleted);

internal sealed record CliBatchPatchRepairOverwriteRebuild(
	string NodeId,
	string Name,
	bool Success,
	string? OverwriteDirectory,
	int RebuiltPatchCount,
	int EditedUnitCount,
	int HelmetProcessedPatchCount,
	int HelmetMergedArmorUnitCount,
	int HelmetHiddenUnitCount,
	bool ClearedManagedOverwrite,
	IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics);

internal sealed record CliBatchPatchRepairExecutionResult(
	bool Success,
	bool LibrarySnapshotUpdated,
	int SelectedModCount,
	int RepairedModCount,
	int SkippedModCount,
	int FailedModCount,
	int CanceledModCount,
	int NotStartedModCount,
	string? BackupDirectory,
	string? ManifestPath,
	IReadOnlyList<CliBatchPatchRepairExecutionMod> Mods,
	IReadOnlyList<CliBatchPatchRepairOverwriteRebuild> Overwrites,
	IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics);

/// <summary>
/// Commits a reviewed repair batch under the CLI library lease. Core owns
/// candidate/backup/restore mechanics; this adapter owns plan revalidation,
/// library.json fingerprint refresh, and rebuilding active derived Overwrites.
/// </summary>
internal static class CliBatchPatchRepairExecutor
{
	public static async ValueTask<CliBatchPatchRepairExecutionResult> ApplyAsync(
		CliBatchPatchRepairPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);

		var first = await CliBatchPatchRepairPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!first.IsValid || first.Current is null)
			return Failure(first.Diagnostics, document.SelectedNodeIds.Count);

		try
		{
			await using var session = await CliWriteTargetGuard
				.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
				.ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			// The exported plan is never write authority. Re-run all filesystem,
			// compatibility and GameData checks after acquiring the cross-process
			// lease, immediately before Core is allowed to touch source Patches.
			var locked = await CliBatchPatchRepairPlanArtifacts
				.ValidateAsync(document, workspace, cancellationToken)
				.ConfigureAwait(false);
			if (!locked.IsValid || locked.Current is null)
				return Failure(locked.Diagnostics, document.SelectedNodeIds.Count);

			var selected = ResolveSelectedNodes(document, locked.Current.Snapshot);
			if (selected.Count == 0)
			{
				return new CliBatchPatchRepairExecutionResult(
					true,
					false,
					0,
					0,
					0,
					0,
					0,
					0,
					null,
					null,
					Array.Empty<CliBatchPatchRepairExecutionMod>(),
					Array.Empty<CliBatchPatchRepairOverwriteRebuild>(),
					DistinctDiagnostics(locked.Diagnostics));
			}

			await using var reader = CoreServices.CreateModInformationReader();
			await using var informationCenter = CoreServices.CreateModInformationCenter(session.Paths, reader);
			var repairService = CoreServices.CreateModRepairBatchService(session.Paths, informationCenter, reader);
			var repair = await repairService.RepairAsync(
				selected,
				session.Paths.ModsDirectory,
				document.GameData.Directory,
				cancellationToken).ConfigureAwait(false);

			var diagnostics = new List<CliBatchPatchRepairDiagnostic>(locked.Diagnostics);
			var executions = repair.Mods.Select(CreateExecutionMod).ToArray();
			AddRepairDiagnostics(diagnostics, repair);

			var repairedIds = repair.Mods
				.Where(item => item.Status == ModRepairBatchModStatus.Repaired)
				.Select(item => item.NodeId)
				.ToHashSet();
			var snapshotUpdated = false;
			LibrarySnapshot snapshot = locked.Current.Snapshot;
			if (repairedIds.Count != 0)
			{
				var persisted = await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false)
					?? throw new CliUserException("LibrarySnapshotUnreadable", "批量修复后无法重新读取 library.json；不会尝试覆盖未知状态。 ");
				var refresh = await RefreshRepairedNodeFactsAsync(session, persisted, repairedIds, cancellationToken).ConfigureAwait(false);
				snapshot = refresh.Snapshot;
				snapshotUpdated = refresh.Changed;
				diagnostics.AddRange(refresh.Diagnostics);
			}

			var overwrites = repairedIds.Count == 0
				? Array.Empty<CliBatchPatchRepairOverwriteRebuild>()
				: await RebuildActiveOverwritesAsync(snapshot, session.Paths, document.GameData.Directory, reader, cancellationToken).ConfigureAwait(false);
			foreach (var overwrite in overwrites)
				diagnostics.AddRange(overwrite.Diagnostics);

			var success = repair.FailedModCount == 0
				&& repair.ManifestWriteFailed == false
				&& overwrites.All(overwrite => overwrite.Success)
				&& !diagnostics.Any(diagnostic => diagnostic.Blocking);
			if (repair.ManifestWriteFailed)
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					repair.ManifestIssueCode ?? "BatchRepairManifestWriteFailed",
					"error",
					true,
					"Patch 修复已产生结果，但审计清单写入失败；请检查管理器数据目录并重新核验。",
					repair.ManifestPath));
				success = false;
			}

			return new CliBatchPatchRepairExecutionResult(
				success,
				snapshotUpdated,
				selected.Count,
				repair.RepairedModCount,
				repair.SkippedModCount,
				repair.FailedModCount,
				repair.CanceledModCount,
				repair.NotStartedModCount,
				repair.BatchDirectory,
				repair.ManifestPath,
				executions,
				overwrites,
				DistinctDiagnostics(diagnostics));
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("BatchRepairApplyFailed", $"批量 Patch 修复未完成：{exception.Message}");
		}
	}

	private static IReadOnlyList<ModNode> ResolveSelectedNodes(
		CliBatchPatchRepairPlanDocument document,
		LibrarySnapshot snapshot)
	{
		var selected = new List<ModNode>();
		foreach (var planned in document.Mods.Where(mod => mod.Selected))
		{
			if (!Guid.TryParse(planned.NodeId, out var value)
				|| !snapshot.Nodes.TryGetValue(new ModNodeId(value), out var node))
			{
				throw new CliUserException("BatchRepairSelectedModMissing", $"计划中选中的 Mod 已不存在：{planned.NodeId}");
			}
			selected.Add(node);
		}
		return selected;
	}

	private static async ValueTask<(LibrarySnapshot Snapshot, bool Changed, IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics)> RefreshRepairedNodeFactsAsync(
		CliLibraryWriteSession session,
		LibrarySnapshot source,
		IReadOnlySet<ModNodeId> repairedIds,
		CancellationToken cancellationToken)
	{
		var diagnostics = new List<CliBatchPatchRepairDiagnostic>();
		var synchronizer = CoreServices.CreateModLibrarySynchronizer();
		var inspection = await synchronizer.InspectAsync(source, session.Paths.ModsDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
		var directories = inspection.Directories.ToDictionary(
			directory => NormalizeRelativePath(directory.RelativePath),
			StringComparer.OrdinalIgnoreCase);
		var nodes = source.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
		var changed = false;
		var timestamp = DateTimeOffset.UtcNow;
		foreach (var nodeId in repairedIds)
		{
			if (!source.Nodes.TryGetValue(nodeId, out var node))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairCommittedNodeMissing",
					"error",
					true,
					"Patch 已完成修复，但对应 Mod 节点在刷新 library.json 前消失。",
					NodeId: CliProjections.FormatId(nodeId)));
				continue;
			}
			if (!directories.TryGetValue(NormalizeRelativePath(node.RelativePath), out var directory))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairCommittedPatchMissing",
					"error",
					true,
					$"Patch 已完成修复，但刷新时未发现其库目录中的有效 Patch：{node.Metadata.Name}",
					Path.Combine(session.Paths.ModsDirectory, node.RelativePath),
					CliProjections.FormatId(node.Id)));
				continue;
			}
			if (PatchGroupsEqual(node.PatchGroups, directory.PatchGroups)
				&& string.Equals(node.ContentFingerprint, directory.ContentFingerprint, StringComparison.Ordinal))
				continue;
			nodes[nodeId] = node with
			{
				PatchGroups = directory.PatchGroups,
				ContentFingerprint = directory.ContentFingerprint,
				Metadata = node.Metadata with { ModifiedUtc = timestamp },
			};
			changed = true;
		}
		if (!changed)
			return (source, false, diagnostics);

		var next = source with { Nodes = nodes, SavedUtc = timestamp };
		var persisted = await session.SaveNodesOnlyAsync(next, cancellationToken).ConfigureAwait(false);
		return (persisted, true, diagnostics);
	}

	private static async ValueTask<IReadOnlyList<CliBatchPatchRepairOverwriteRebuild>> RebuildActiveOverwritesAsync(
		LibrarySnapshot snapshot,
		StoragePaths paths,
		string gameDataDirectory,
		IModInformationReader reader,
		CancellationToken cancellationToken)
	{
		DecorationActivationSnapshot activations;
		try
		{
			activations = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(
				paths.DecorationActivationsPath,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return
			[
				new CliBatchPatchRepairOverwriteRebuild(
					string.Empty,
					"派生 Overwrite",
					false,
					null,
					0,
					0,
					0,
					0,
					0,
					false,
					[new CliBatchPatchRepairDiagnostic(
						"BatchRepairDecorationActivationsUnreadable",
						"error",
						true,
						$"无法读取 decoration-activations.json，已保留现有 Overwrite：{exception.Message}",
						paths.DecorationActivationsPath,
						Details: exception.ToString())])
			];
		}

		var hosts = new HashSet<ModNodeId>();
		foreach (var hostId in activations.EnabledHostsByDecoration.Values.SelectMany(values => values))
		{
			if (TryParseNodeId(hostId, out var nodeId)) hosts.Add(nodeId);
		}
		foreach (var node in snapshot.Nodes.Values.Where(node => node.Metadata.HelmetToArmorMerged))
			hosts.Add(node.Id);
		if (hosts.Count == 0) return Array.Empty<CliBatchPatchRepairOverwriteRebuild>();

		var decorations = snapshot.Nodes.Values
			.Where(node => node.Metadata.Kind == ModNodeKind.Decoration)
			.ToDictionary(node => node.Id.Value.ToString("N"), StringComparer.OrdinalIgnoreCase);
		var decorationRebuild = CoreServices.CreateDecorationHostRebuildService(paths, reader);
		var helmetMerge = CoreServices.CreateDirectHelmetToArmorMergeService(paths, reader);
		var overwriteService = CoreServices.CreateHostDerivedOverwriteService(decorationRebuild, helmetMerge);
		var results = new List<CliBatchPatchRepairOverwriteRebuild>();

		foreach (var hostId in hosts.OrderBy(id => id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(hostId, out var host))
			{
				results.Add(FailedOverwrite(
					CliProjections.FormatId(hostId),
					"未知主体",
					"BatchRepairActiveHostMissing",
					"装饰或头盔合并状态引用了已不存在的主体；已保留其他 Overwrite。"));
				continue;
			}
			if (!IsHostNode(host))
			{
				results.Add(FailedOverwrite(
					CliProjections.FormatId(host.Id),
					host.Metadata.Name,
					"BatchRepairActiveHostInvalid",
					"装饰或头盔合并状态引用的主体不再是带 Patch 的普通/选项 Mod；已保留其现有 Overwrite。"));
				continue;
			}

			var attachmentsResult = await ReadEnabledAttachmentsAsync(host, decorations, activations, paths, cancellationToken).ConfigureAwait(false);
			if (attachmentsResult.Diagnostics.Any(diagnostic => diagnostic.Blocking))
			{
				results.Add(new CliBatchPatchRepairOverwriteRebuild(
					CliProjections.FormatId(host.Id), host.Metadata.Name, false, null, 0, 0, 0, 0, 0, false, attachmentsResult.Diagnostics));
				continue;
			}

			try
			{
				var result = await overwriteService.RebuildAsync(
					new HostDerivedOverwriteRequest(
						host,
						attachmentsResult.Attachments,
						host.Metadata.HelmetToArmorMerged,
						paths.ModsDirectory,
						gameDataDirectory),
					cancellationToken).ConfigureAwait(false);
				reader.InvalidateNode(host.Id);
				var issues = attachmentsResult.Diagnostics
					.Concat(result.Issues.Select(issue => CreateOverwriteDiagnostic(issue, host)))
					.ToArray();
				results.Add(new CliBatchPatchRepairOverwriteRebuild(
					CliProjections.FormatId(host.Id),
					host.Metadata.Name,
					result.Succeeded,
					result.OverwriteDirectory,
					result.DecorationRebuild?.RebuiltPatchCount ?? 0,
					result.DecorationRebuild?.EditedUnitCount ?? 0,
					result.HelmetMerge?.ProcessedPatchCount ?? 0,
					result.HelmetMerge?.MergedArmorUnitCount ?? 0,
					result.HelmetMerge?.HiddenHelmetUnitCount ?? 0,
					result.ClearedManagedOverwrite || result.DecorationRebuild?.ClearedGeneratedOverwrite == true,
					issues));
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or InvalidOperationException
				or DirectoryNotFoundException
				or FileNotFoundException)
			{
				results.Add(FailedOverwrite(
					CliProjections.FormatId(host.Id),
					host.Metadata.Name,
					"BatchRepairOverwriteRebuildFailed",
					$"重建活动 Overwrite 失败；Patch 修复已保留，现有 Overwrite 未被 CLI 主动删除：{exception.Message}",
					exception.ToString()));
			}
		}
		return results;
	}

	private static async ValueTask<(IReadOnlyList<DecorationHostRebuildAttachment> Attachments, IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics)> ReadEnabledAttachmentsAsync(
		ModNode host,
		IReadOnlyDictionary<string, ModNode> decorations,
		DecorationActivationSnapshot activations,
		StoragePaths paths,
		CancellationToken cancellationToken)
	{
		var hostKey = host.Id.Value.ToString("N");
		var attachments = new List<DecorationHostRebuildAttachment>();
		var diagnostics = new List<CliBatchPatchRepairDiagnostic>();
		foreach (var (rawDecorationId, hosts) in activations.EnabledHostsByDecoration.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (!hosts.Contains(hostKey)) continue;
			var key = NormalizeGuid(rawDecorationId);
			if (key is null || !decorations.TryGetValue(key, out var decoration))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairActiveDecorationMissing",
					"error",
					true,
					"活动装饰状态引用了不存在的装饰 Mod；为避免清空旧 Overwrite，已跳过该主体。",
					NodeId: CliProjections.FormatId(host.Id)));
				continue;
			}
			var planPath = Path.Combine(paths.ModsDirectory, decoration.RelativePath, "decoration.json");
			if (!File.Exists(planPath))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairActiveDecorationPlanMissing",
					"error",
					true,
					$"活动装饰缺少 decoration.json；为避免清空旧 Overwrite，已跳过该主体：{decoration.Metadata.Name}",
					planPath,
					CliProjections.FormatId(host.Id)));
				continue;
			}
			try
			{
				var plan = JsonSerializer.Deserialize<DecorationPlanDocument>(
					await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false),
					new JsonSerializerOptions(JsonSerializerDefaults.Web));
				if (plan is null)
				{
					diagnostics.Add(new CliBatchPatchRepairDiagnostic(
						"BatchRepairActiveDecorationPlanUnreadable",
						"error",
						true,
						$"活动装饰的 decoration.json 为空或无法解析；已跳过主体：{decoration.Metadata.Name}",
						planPath,
						CliProjections.FormatId(host.Id)));
					continue;
				}
				attachments.Add(new DecorationHostRebuildAttachment(
					decoration.Id.Value.ToString("N"),
					decoration.RelativePath,
					plan));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairActiveDecorationPlanUnreadable",
					"error",
					true,
					$"无法读取活动装饰定义；已跳过主体：{decoration.Metadata.Name}。{exception.Message}",
					planPath,
					CliProjections.FormatId(host.Id),
					exception.ToString()));
			}
		}
		return (attachments, DistinctDiagnostics(diagnostics));
	}

	private static void AddRepairDiagnostics(
		ICollection<CliBatchPatchRepairDiagnostic> diagnostics,
		ModRepairBatchResult result)
	{
		foreach (var item in result.Mods)
		{
			var nodeId = CliProjections.FormatId(item.NodeId);
			switch (item.Status)
			{
				case ModRepairBatchModStatus.CandidateFailed:
				case ModRepairBatchModStatus.CommitFailed:
					diagnostics.Add(new CliBatchPatchRepairDiagnostic(
						"BatchRepairModFailed",
						"error",
						true,
						$"{item.Name} 修复失败：{item.Detail ?? item.Status.ToString()}",
						NodeId: nodeId));
					break;
				case ModRepairBatchModStatus.SkippedNotRepairable:
					diagnostics.Add(new CliBatchPatchRepairDiagnostic(
						"BatchRepairModSkipped",
						"warning",
						false,
						$"{item.Name} 已跳过：{item.Detail ?? "不满足安全修复条件。"}",
						NodeId: nodeId));
					break;
				case ModRepairBatchModStatus.Canceled:
				case ModRepairBatchModStatus.NotStarted:
					diagnostics.Add(new CliBatchPatchRepairDiagnostic(
						"BatchRepairModIncomplete",
						"warning",
						false,
						$"{item.Name} 未完成：{item.Detail ?? item.Status.ToString()}",
						NodeId: nodeId));
					break;
			}
		}
	}

	private static CliBatchPatchRepairExecutionMod CreateExecutionMod(ModRepairBatchModResult item)
		=> new(
			CliProjections.FormatId(item.NodeId),
			item.Name,
			item.Status.ToString(),
			item.Detail,
			item.CandidateDirectory,
			item.BackupDirectory,
			item.ReportPath,
			item.BackupCompleted,
			item.CommitCompleted,
			item.RestoreAttempted,
			item.RestoreCompleted);

	private static CliBatchPatchRepairOverwriteRebuild FailedOverwrite(
		string nodeId,
		string name,
		string code,
		string message,
		string? details = null)
		=> new(
			nodeId,
			name,
			false,
			null,
			0,
			0,
			0,
			0,
			0,
			false,
			[new CliBatchPatchRepairDiagnostic(code, "error", true, message, NodeId: nodeId, Details: details)]);

	private static CliBatchPatchRepairDiagnostic CreateOverwriteDiagnostic(CoreIssue issue, ModNode host)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			CliProjections.FormatId(host.Id),
			issue.ExceptionMessage);

	private static CliBatchPatchRepairExecutionResult Failure(
		IReadOnlyList<CliBatchPatchRepairDiagnostic> diagnostics,
		int selectedCount)
		=> new(
			false,
			false,
			selectedCount,
			0,
			0,
			0,
			0,
			0,
			null,
			null,
			Array.Empty<CliBatchPatchRepairExecutionMod>(),
			Array.Empty<CliBatchPatchRepairOverwriteRebuild>(),
			DistinctDiagnostics(diagnostics));

	private static bool IsHostNode(ModNode node)
		=> node.PatchGroups.Count != 0 && node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option;

	private static bool TryParseNodeId(string? raw, out ModNodeId nodeId)
	{
		if (Guid.TryParse(raw, out var value))
		{
			nodeId = new ModNodeId(value);
			return true;
		}
		nodeId = default;
		return false;
	}

	private static string? NormalizeGuid(string? raw)
		=> Guid.TryParse(raw, out var value) ? value.ToString("N") : null;

	private static string NormalizeRelativePath(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "."
			? string.Empty
			: path.Replace(Path.DirectorySeparatorChar, '/').Trim('/');

	private static bool PatchGroupsEqual(IReadOnlyList<PatchGroupKey> left, IReadOnlyList<PatchGroupKey> right)
		=> left.Count == right.Count
			&& left.Zip(right).All(pair => string.Equals(pair.First.ArchiveHex16, pair.Second.ArchiveHex16, StringComparison.OrdinalIgnoreCase)
				&& pair.First.PatchIndex == pair.Second.PatchIndex);

	private static IReadOnlyList<CliBatchPatchRepairDiagnostic> DistinctDiagnostics(IEnumerable<CliBatchPatchRepairDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Message, diagnostic.Path, diagnostic.NodeId, diagnostic.Details })
			.Select(group => group.First())
			.ToArray();
}
