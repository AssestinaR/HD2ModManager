using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliHostDerivedExecutionHost(
	string NodeId,
	string Name,
	bool PlannedRebuildSucceeded,
	bool RollbackAttempted,
	bool RollbackSucceeded,
	string OverwriteDirectory,
	int RebuiltPatchCount,
	int DecorationEditedUnitCount,
	int HelmetProcessedPatchCount,
	int HelmetMergedArmorUnitCount,
	int HelmetHiddenUnitCount,
	bool ClearedManagedOverwrite,
	IReadOnlyList<CliHostDerivedDiagnostic> Diagnostics);

internal sealed record CliHostDerivedExecutionResult(
	bool Success,
	bool StateCommitted,
	string StateMutation,
	IReadOnlyList<CliHostDerivedExecutionHost> Hosts,
	IReadOnlyList<CliHostDerivedDiagnostic> Diagnostics);

/// <summary>
/// Applies a reviewed host-derived plan while holding the same library write
/// lease used by all other CLI mutations.  The Core coordinator owns each
/// individual Overwrite transaction; this executor owns the multi-host and
/// state-file transaction around it.
/// </summary>
internal static class CliHostDerivedOverwriteExecutor
{
	public static async ValueTask<CliHostDerivedExecutionResult> ApplyAsync(
		CliHostDerivedPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);

		var first = await CliHostDerivedOverwritePlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!first.IsValid || first.Current is null)
			return Failure(first.Diagnostics);

		HostDerivedOverwriteService? overwriteService = null;
		var executions = new List<CliHostDerivedExecutionHost>();
		var completed = new List<CliHostDerivedHostBuild>();
		var diagnostics = new List<CliHostDerivedDiagnostic>(first.Diagnostics);
		try
		{
			await using var session = await CliWriteTargetGuard
				.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
				.ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			// The plan is never authority by itself.  Rebuild it after the lock is
			// held so source Patch files, both state files, existing Overwrites and
			// GameData index facts are all still exactly what the reviewer saw.
			var locked = await CliHostDerivedOverwritePlanArtifacts
				.ValidateAsync(document, workspace, cancellationToken)
				.ConfigureAwait(false);
			if (!locked.IsValid || locked.Current is null)
				return Failure(locked.Diagnostics);

			var current = locked.Current;
			diagnostics = new List<CliHostDerivedDiagnostic>(locked.Diagnostics);
			if (current.HostsById.Count == 0)
			{
				return new CliHostDerivedExecutionResult(
					true,
					false,
					"none",
					Array.Empty<CliHostDerivedExecutionHost>(),
					DistinctDiagnostics(diagnostics));
			}

			await using var reader = CoreServices.CreateModInformationReader();
			var decorationRebuild = CoreServices.CreateDecorationHostRebuildService(session.Paths, reader);
			var helmetMerge = CoreServices.CreateDirectHelmetToArmorMergeService(session.Paths, reader);
			overwriteService = CoreServices.CreateHostDerivedOverwriteService(decorationRebuild, helmetMerge);

			foreach (var build in current.HostsById.Values.OrderBy(item => item.Host.Id.Value))
			{
				cancellationToken.ThrowIfCancellationRequested();
				HostDerivedOverwriteResult result;
				try
				{
					result = await overwriteService.RebuildAsync(
						new HostDerivedOverwriteRequest(
							build.Host,
							build.PlannedAttachments,
							build.PlannedHelmetMerged,
							session.Paths.ModsDirectory,
							current.Document.Request.GameDataDirectory),
						cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					await RollbackOverwritesAsync(overwriteService, completed, current.Document.Request.GameDataDirectory, session.Paths, executions, diagnostics).ConfigureAwait(false);
					throw;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
				{
					diagnostics.Add(CreateApplyFailureDiagnostic(build.Host, exception));
					await RollbackOverwritesAsync(overwriteService, completed, current.Document.Request.GameDataDirectory, session.Paths, executions, diagnostics).ConfigureAwait(false);
					return new CliHostDerivedExecutionResult(false, false, "none", executions, DistinctDiagnostics(diagnostics));
				}

				var execution = CreateExecution(build, result);
				executions.Add(execution);
				diagnostics.AddRange(execution.Diagnostics);
				if (!result.Succeeded)
				{
					await RollbackOverwritesAsync(overwriteService, completed, current.Document.Request.GameDataDirectory, session.Paths, executions, diagnostics).ConfigureAwait(false);
					return new CliHostDerivedExecutionResult(false, false, "none", executions, DistinctDiagnostics(diagnostics));
				}
				completed.Add(build);
			}

			var persistence = await CommitStateAsync(document, current, session, cancellationToken).ConfigureAwait(false);
			if (!persistence.Success)
			{
				diagnostics.AddRange(persistence.Diagnostics);
				await RollbackOverwritesAsync(overwriteService, completed, current.Document.Request.GameDataDirectory, session.Paths, executions, diagnostics).ConfigureAwait(false);
				return new CliHostDerivedExecutionResult(false, false, persistence.Mutation, executions, DistinctDiagnostics(diagnostics));
			}

			return new CliHostDerivedExecutionResult(
				true,
				persistence.Committed,
				persistence.Mutation,
				executions,
				DistinctDiagnostics(diagnostics));
		}
		catch (CliUserException exception)
		{
			if (overwriteService is not null && completed.Count != 0)
				await RollbackOverwritesAsync(overwriteService, completed, document.Request.GameDataDirectory, new StoragePaths(workspace.ManagerRoot!, workspace.LibraryRoot!), executions, diagnostics).ConfigureAwait(false);
			diagnostics.Add(new CliHostDerivedDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliHostDerivedExecutionResult(false, false, "none", executions, DistinctDiagnostics(diagnostics));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (overwriteService is not null && completed.Count != 0 && !string.IsNullOrWhiteSpace(workspace.ManagerRoot) && !string.IsNullOrWhiteSpace(workspace.LibraryRoot))
				await RollbackOverwritesAsync(overwriteService, completed, document.Request.GameDataDirectory, new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot), executions, diagnostics).ConfigureAwait(false);
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			if (overwriteService is not null && completed.Count != 0 && !string.IsNullOrWhiteSpace(workspace.ManagerRoot) && !string.IsNullOrWhiteSpace(workspace.LibraryRoot))
				await RollbackOverwritesAsync(overwriteService, completed, document.Request.GameDataDirectory, new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot), executions, diagnostics).ConfigureAwait(false);
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedApplyFailed", "error", true, exception.Message, Details: exception.ToString()));
			return new CliHostDerivedExecutionResult(false, false, "none", executions, DistinctDiagnostics(diagnostics));
		}
	}

	private static async ValueTask<CliHostDerivedStateCommit> CommitStateAsync(
		CliHostDerivedPlanDocument document,
		CliHostDerivedBuildResult current,
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		var mutation = document.Request.Operation switch
		{
			CliHostDerivedOperation.Decoration => "decoration-activations",
			CliHostDerivedOperation.Helmet => "library.helmet-to-armor-merged",
			_ => "none",
		};
		if (document.Request.Operation == CliHostDerivedOperation.Rebuild)
			return CliHostDerivedStateCommit.NoChange;

		string[] affectedPaths = document.Request.Operation == CliHostDerivedOperation.Decoration
			? [session.Paths.DecorationActivationsPath]
			: [session.Paths.LibraryPath];
		var snapshots = new List<CliHostDerivedStateFileSnapshot>();
		try
		{
			foreach (var path in affectedPaths)
				snapshots.Add(await CliHostDerivedStateFileSnapshot.CaptureAsync(path, cancellationToken).ConfigureAwait(false));
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			if (document.Request.Operation == CliHostDerivedOperation.Decoration)
			{
				if (current.DecorationActivationUpdates.Count == 0)
					return new CliHostDerivedStateCommit(true, false, mutation, Array.Empty<CliHostDerivedDiagnostic>());
				// The live store deliberately tolerates a damaged file on desktop.  A
				// headless mutation must not: re-read it strictly while the lease is held.
				_ = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(
					session.Paths.DecorationActivationsPath,
					cancellationToken).ConfigureAwait(false);
				var store = CoreServices.CreateDecorationActivationStore(session.Paths);
				await store.SetEnabledHostsBatchAsync(current.DecorationActivationUpdates, cancellationToken).ConfigureAwait(false);
				return new CliHostDerivedStateCommit(true, true, mutation, Array.Empty<CliHostDerivedDiagnostic>());
			}

			var snapshot = await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false)
				?? throw new CliUserException("LibrarySnapshotUnreadable", "无法在保存头盔合并状态前重新读取 library.json。 ");
			var nodes = snapshot.Nodes.ToDictionary(item => item.Key, item => item.Value);
			var changed = 0;
			var now = DateTimeOffset.UtcNow;
			foreach (var build in current.HostsById.Values)
			{
				if (!nodes.TryGetValue(build.Host.Id, out var node))
					throw new CliUserException("HostDerivedHostChanged", "保存头盔合并状态前，计划中的主体已从 library.json 消失。 ");
				if (node.Metadata.HelmetToArmorMerged == build.PlannedHelmetMerged) continue;
				nodes[node.Id] = node with
				{
					Metadata = node.Metadata with
					{
						HelmetToArmorMerged = build.PlannedHelmetMerged,
						ModifiedUtc = now,
					},
				};
				changed++;
			}
			if (changed == 0) return new CliHostDerivedStateCommit(true, false, mutation, Array.Empty<CliHostDerivedDiagnostic>());
			await session.SaveNodesOnlyAsync(snapshot with { Nodes = nodes, SavedUtc = now }, cancellationToken).ConfigureAwait(false);
			return new CliHostDerivedStateCommit(true, true, mutation, Array.Empty<CliHostDerivedDiagnostic>());
		}
		catch (Exception exception) when (exception is CliUserException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			var diagnostics = new List<CliHostDerivedDiagnostic>
			{
				new("HostDerivedStateCommitFailed", "error", true, $"派生 Overwrite 已生成，但状态文件未能安全提交：{exception.Message}", Details: exception.ToString()),
			};
			foreach (var snapshot in snapshots.AsEnumerable().Reverse())
				diagnostics.AddRange(await snapshot.RestoreAsync().ConfigureAwait(false));
			return new CliHostDerivedStateCommit(false, false, mutation, diagnostics);
		}
	}

	private static async ValueTask RollbackOverwritesAsync(
		HostDerivedOverwriteService overwriteService,
		IEnumerable<CliHostDerivedHostBuild> completed,
		string gameDataDirectory,
		StoragePaths paths,
		IList<CliHostDerivedExecutionHost> executions,
		ICollection<CliHostDerivedDiagnostic> diagnostics)
	{
		foreach (var build in completed.Reverse())
		{
			var index = FindExecution(executions, build.Host.Id);
			if (index >= 0) executions[index] = executions[index] with { RollbackAttempted = true };
			try
			{
				var result = await overwriteService.RebuildAsync(
					new HostDerivedOverwriteRequest(
						build.Host,
						build.CurrentAttachments,
						build.CurrentHelmetMerged,
						paths.ModsDirectory,
						gameDataDirectory),
					CancellationToken.None).ConfigureAwait(false);
				if (result.Succeeded)
				{
					if (index >= 0) executions[index] = executions[index] with { RollbackSucceeded = true };
					continue;
				}
				diagnostics.Add(new CliHostDerivedDiagnostic(
					"HostDerivedRollbackFailed", "error", true,
					$"无法将主体“{build.Host.Metadata.Name}”的 Overwrite 恢复到计划前状态。",
					NodeId: CliProjections.FormatId(build.Host.Id)));
				foreach (var issue in result.Issues)
					diagnostics.Add(CreateCoreDiagnostic(issue, "rollback", build.Host));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
			{
				diagnostics.Add(new CliHostDerivedDiagnostic(
					"HostDerivedRollbackFailed", "error", true,
					$"恢复主体“{build.Host.Metadata.Name}”的 Overwrite 时发生异常：{exception.Message}",
					NodeId: CliProjections.FormatId(build.Host.Id), Details: exception.ToString()));
			}
		}
	}

	private static CliHostDerivedExecutionHost CreateExecution(CliHostDerivedHostBuild build, HostDerivedOverwriteResult result)
	{
		var diagnostics = result.Issues.Select(issue => CreateCoreDiagnostic(issue, "rebuild", build.Host)).ToArray();
		return new CliHostDerivedExecutionHost(
			CliProjections.FormatId(build.Host.Id),
			build.Host.Metadata.Name,
			result.Succeeded,
			false,
			false,
			result.OverwriteDirectory,
			result.DecorationRebuild?.RebuiltPatchCount ?? 0,
			result.DecorationRebuild?.EditedUnitCount ?? 0,
			result.HelmetMerge?.ProcessedPatchCount ?? 0,
			result.HelmetMerge?.MergedArmorUnitCount ?? 0,
			result.HelmetMerge?.HiddenHelmetUnitCount ?? 0,
			result.ClearedManagedOverwrite || result.DecorationRebuild?.ClearedGeneratedOverwrite == true,
			diagnostics);
	}

	private static CliHostDerivedDiagnostic CreateCoreDiagnostic(CoreIssue issue, string stage, ModNode host)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : CliProjections.FormatId(host.Id),
			string.IsNullOrWhiteSpace(issue.ExceptionMessage) ? stage : issue.ExceptionMessage);

	private static CliHostDerivedDiagnostic CreateApplyFailureDiagnostic(ModNode host, Exception exception)
		=> new(
			"HostDerivedRebuildFailed",
			"error",
			true,
			$"重建主体“{host.Metadata.Name}”的 Overwrite 时发生异常：{exception.Message}",
			NodeId: CliProjections.FormatId(host.Id),
			Details: exception.ToString());

	private static int FindExecution(IList<CliHostDerivedExecutionHost> executions, ModNodeId id)
	{
		var key = CliProjections.FormatId(id);
		for (var index = 0; index < executions.Count; index++)
			if (string.Equals(executions[index].NodeId, key, StringComparison.OrdinalIgnoreCase)) return index;
		return -1;
	}

	private static CliHostDerivedExecutionResult Failure(IReadOnlyList<CliHostDerivedDiagnostic> diagnostics)
		=> new(false, false, "none", Array.Empty<CliHostDerivedExecutionHost>(), DistinctDiagnostics(diagnostics));

	private static IReadOnlyList<CliHostDerivedDiagnostic> DistinctDiagnostics(IEnumerable<CliHostDerivedDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Path}\u001f{item.NodeId}\u001f{item.Message}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();

	private sealed record CliHostDerivedStateCommit(
		bool Success,
		bool Committed,
		string Mutation,
		IReadOnlyList<CliHostDerivedDiagnostic> Diagnostics)
	{
		public static CliHostDerivedStateCommit NoChange { get; } = new(true, false, "none", Array.Empty<CliHostDerivedDiagnostic>());
	}

	private sealed record CliHostDerivedStateFileSnapshot(string Path, bool Exists, byte[]? Contents)
	{
		public static async ValueTask<CliHostDerivedStateFileSnapshot> CaptureAsync(string path, CancellationToken cancellationToken)
		{
			if (Directory.Exists(path))
				throw new CliUserException("HostDerivedStateTargetIsDirectory", $"状态文件路径被目录占用：{path}");
			if (!File.Exists(path)) return new CliHostDerivedStateFileSnapshot(path, false, null);
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReparsePoint) != 0)
				throw new CliUserException("HostDerivedStateTargetReparsePoint", $"状态文件不能是链接或其他 reparse point：{path}");
			return new CliHostDerivedStateFileSnapshot(path, true, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
		}

		public async ValueTask<IReadOnlyList<CliHostDerivedDiagnostic>> RestoreAsync()
		{
			try
			{
				if (Exists)
				{
					if (Contents is null) throw new InvalidDataException("缺少原始状态文件内容。");
					var directory = System.IO.Path.GetDirectoryName(Path)
						?? throw new InvalidDataException("无法解析状态文件父目录。");
					Directory.CreateDirectory(directory);
					var temporary = Path + ".cli-host-derived-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
					try
					{
						await File.WriteAllBytesAsync(temporary, Contents).ConfigureAwait(false);
						File.Move(temporary, Path, overwrite: true);
					}
					finally
					{
						if (File.Exists(temporary)) File.Delete(temporary);
					}
				}
				else if (File.Exists(Path))
				{
					if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
						throw new InvalidDataException("无法删除已变为 reparse point 的状态文件。");
					File.Delete(Path);
				}
				return Array.Empty<CliHostDerivedDiagnostic>();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return
				[
					new CliHostDerivedDiagnostic(
						"HostDerivedStateRollbackFailed", "error", true,
						$"无法恢复状态文件“{Path}”：{exception.Message}", Path, Details: exception.ToString()),
				];
			}
		}
	}
}
