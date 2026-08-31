using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliDecorationEditExecutionResult(
	bool Success,
	bool DecorationPlanCommitted,
	bool ActivationStateCommitted,
	int PrunedActivationHostCount,
	IReadOnlyList<CliHostDerivedExecutionHost> Hosts,
	IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics);

/// <summary>
/// Commits an edit to an imported PatchSnapshot decoration and keeps every
/// currently enabled host coherent with that new definition.  The plan file,
/// generated host Overwrites and relevant activation record form one logical
/// transaction even though they live in separate files/directories.
/// </summary>
internal static class CliDecorationEditExecutor
{
	public static async ValueTask<CliDecorationEditExecutionResult> ApplyAsync(
		CliDecorationEditPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);

		var first = await CliDecorationEditPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!first.IsValid || first.Current is null)
			return Failure(first.Diagnostics);

		try
		{
			await using var session = await CliWriteTargetGuard
				.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
				.ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			// The exported document is review material, never write authority.  It
			// must still describe the same source snapshot, activation state and
			// GameData facts after the library lease has been acquired.
			var locked = await CliDecorationEditPlanArtifacts
				.ValidateAsync(document, workspace, cancellationToken)
				.ConfigureAwait(false);
			if (!locked.IsValid || locked.Current is null)
				return Failure(locked.Diagnostics);

			return await ApplyLockedAsync(locked.Current, workspace, session, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (CliUserException exception)
		{
			return Failure([new CliDecorationEditDiagnostic(exception.Code, "error", true, exception.Message)]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			return Failure(
			[
				new CliDecorationEditDiagnostic(
					"DecorationEditApplyFailed", "error", true,
					$"装饰编辑未完成：{exception.Message}",
					Details: exception.ToString()),
			]);
		}
	}

	private static async ValueTask<CliDecorationEditExecutionResult> ApplyLockedAsync(
		CliDecorationEditBuildResult current,
		CliWorkspace workspace,
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		var diagnostics = new List<CliDecorationEditDiagnostic>(current.Document.Diagnostics);
		var hosts = Array.Empty<CliHostDerivedExecutionHost>();
		var planWritten = false;
		var derivedSucceeded = false;
		var activationCommitted = false;
		var prunedActivationHosts = 0;
		CliDecorationEditFileState? planState = null;
		CliDecorationEditFileState? activationState = null;
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			planState = await CliDecorationEditFileState
				.CaptureAsync(current.DecorationPlanPath, required: true, cancellationToken)
				.ConfigureAwait(false);
			EnsurePlanInputUnchanged(current, planState);
			activationState = await CliDecorationEditFileState
				.CaptureAsync(session.Paths.DecorationActivationsPath, required: false, cancellationToken)
				.ConfigureAwait(false);
			EnsureActivationInputUnchanged(current, activationState);

			var proposedBytes = JsonSerializer.SerializeToUtf8Bytes(current.ProposedPlan, CliOutput.JsonOptions);
			var proposedPlanSha256 = ComputeSha256(proposedBytes);
			await planState.ReplaceAsync(proposedBytes, cancellationToken).ConfigureAwait(false);
			planWritten = true;
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			var rebuildRequest = new CliHostDerivedRequest(
				CliHostDerivedOperation.Rebuild,
				CliHostDerivedAction.Rebuild,
				current.Document.ActiveHosts
					.Where(host => host.Exists && host.WillBeRebuilt)
					.Select(host => host.NodeId)
					.ToArray(),
				Array.Empty<string>(),
				false,
				current.Document.Target.GameDataDirectory);
			var desired = await CliHostDerivedOverwritePlanArtifacts
				.BuildAsync(workspace, rebuildRequest, cancellationToken)
				.ConfigureAwait(false);
			diagnostics.AddRange(desired.Document.Diagnostics.Select(ToEditDiagnostic));
			if (!desired.Document.IsReady)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditRebuildPlanBlocked", "error", true,
					"写入候选 decoration.json 后无法为已启用主体生成安全回建计划；已恢复原定义。"));
				diagnostics.AddRange(await planState.RestoreAsync().ConfigureAwait(false));
				return new CliDecorationEditExecutionResult(false, false, false, 0, hosts, DistinctDiagnostics(diagnostics));
			}

			var derived = await CliHostDerivedOverwriteExecutor
				.ApplyBuiltAsync(desired, session, current.BaselineHostDerived.HostsById, cancellationToken)
				.ConfigureAwait(false);
			hosts = derived.Hosts.ToArray();
			diagnostics.AddRange(derived.Diagnostics.Select(ToEditDiagnostic));
			if (!derived.Success)
			{
				diagnostics.AddRange(await planState.RestoreAsync().ConfigureAwait(false));
				return new CliDecorationEditExecutionResult(false, false, false, 0, hosts, DistinctDiagnostics(diagnostics));
			}
			derivedSucceeded = true;

			// The rebuild can be expensive. Re-check both companion state files after
			// it completes so a concurrent writer cannot be silently combined with
			// this edit before the final activation cleanup.
			var planAfterRebuild = await CliDecorationEditFileState
				.CaptureAsync(current.DecorationPlanPath, required: true, cancellationToken)
				.ConfigureAwait(false);
			if (!string.Equals(planAfterRebuild.Sha256, proposedPlanSha256, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException(
					"DecorationEditPlanChangedDuringApply",
					"回建期间 decoration.json 发生了外部变化；为避免覆盖并发修改，本次编辑已回滚。 ");
			var activationAfterRebuild = await CliDecorationEditFileState
				.CaptureAsync(session.Paths.DecorationActivationsPath, required: false, cancellationToken)
				.ConfigureAwait(false);
			EnsureActivationInputUnchanged(current, activationAfterRebuild);

			var activationCommit = await CommitDetachedActivationCleanupAsync(current, session, cancellationToken).ConfigureAwait(false);
			activationCommitted = activationCommit.Committed;
			prunedActivationHosts = activationCommit.PrunedHostCount;
			diagnostics.AddRange(activationCommit.Diagnostics);
			if (!activationCommit.Success)
			{
				await RecoverAsync(current, session, planState, activationState, derivedSucceeded, diagnostics).ConfigureAwait(false);
				return new CliDecorationEditExecutionResult(false, false, false, 0, hosts, DistinctDiagnostics(diagnostics));
			}

			return new CliDecorationEditExecutionResult(
				true,
				true,
				activationCommitted,
				prunedActivationHosts,
				hosts,
				DistinctDiagnostics(diagnostics));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (planWritten && planState is not null)
				await RecoverAsync(current, session, planState, activationState, derivedSucceeded, diagnostics).ConfigureAwait(false);
			throw;
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic(exception.Code, "error", true, exception.Message));
			if (planWritten && planState is not null)
				await RecoverAsync(current, session, planState, activationState, derivedSucceeded, diagnostics).ConfigureAwait(false);
			return new CliDecorationEditExecutionResult(false, false, false, 0, hosts, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic(
				"DecorationEditApplyFailed", "error", true,
				$"装饰编辑未完成：{exception.Message}",
				Details: exception.ToString()));
			if (planWritten && planState is not null)
				await RecoverAsync(current, session, planState, activationState, derivedSucceeded, diagnostics).ConfigureAwait(false);
			return new CliDecorationEditExecutionResult(false, false, false, 0, hosts, DistinctDiagnostics(diagnostics));
		}
	}

	private static async ValueTask<CliDecorationEditActivationCommit> CommitDetachedActivationCleanupAsync(
		CliDecorationEditBuildResult current,
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		var toRemove = current.Document.ActiveHosts
			.Where(host => host.Exists && host.WasDeclaredBeforeEdit && !host.WillBeDeclaredAfterEdit)
			.Select(host => NormalizeGuid(host.NodeId))
			.Where(value => value is not null)
			.Cast<string>()
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (toRemove.Count == 0)
			return CliDecorationEditActivationCommit.NoChange;

		try
		{
			// The tolerant desktop store must not be allowed to silently erase a
			// malformed activation file during a headless edit.
			var snapshot = await DecorationActivationStore
				.TryLoadReadOnlySnapshotAsync(session.Paths.DecorationActivationsPath, cancellationToken)
				.ConfigureAwait(false);
			var decorationKey = current.Decoration.Id.Value.ToString("N");
			var before = snapshot.GetEnabledHosts(decorationKey);
			var after = before
				.Where(value => !toRemove.Contains(NormalizeGuid(value) ?? value))
				.ToArray();
			var pruned = before.Count - after.Length;
			if (pruned == 0) return CliDecorationEditActivationCommit.NoChange;
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var store = CoreServices.CreateDecorationActivationStore(session.Paths);
			await store.SetEnabledHostsBatchAsync(
				new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
				{
					[decorationKey] = after,
				},
				cancellationToken).ConfigureAwait(false);
			return new CliDecorationEditActivationCommit(true, true, pruned, Array.Empty<CliDecorationEditDiagnostic>());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
		{
			return new CliDecorationEditActivationCommit(
				false,
				false,
				0,
				[
					new CliDecorationEditDiagnostic(
						"DecorationEditActivationCommitFailed", "error", true,
						$"新装饰定义已回建主体，但无法安全更新已失效的启用状态：{exception.Message}",
						Path: session.Paths.DecorationActivationsPath,
						Details: exception.ToString()),
				]);
		}
	}

	private static async ValueTask RecoverAsync(
		CliDecorationEditBuildResult current,
		CliLibraryWriteSession session,
		CliDecorationEditFileState planState,
		CliDecorationEditFileState? activationState,
		bool derivedSucceeded,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		foreach (var diagnostic in await planState.RestoreAsync().ConfigureAwait(false))
			diagnostics.Add(diagnostic);
		if (derivedSucceeded)
		{
			var restore = await CliHostDerivedOverwriteExecutor
				.ApplyBuiltAsync(current.BaselineHostDerived, session, cancellationToken: CancellationToken.None)
				.ConfigureAwait(false);
			foreach (var diagnostic in restore.Diagnostics.Select(ToEditDiagnostic))
				diagnostics.Add(diagnostic);
			if (!restore.Success)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditHostRollbackFailed", "error", true,
					"无法将所有已启用主体的 Overwrite 回建到装饰编辑前状态。"));
			}
		}
		if (activationState is not null)
			foreach (var diagnostic in await activationState.RestoreAsync().ConfigureAwait(false))
				diagnostics.Add(diagnostic);
	}

	private static void EnsurePlanInputUnchanged(CliDecorationEditBuildResult current, CliDecorationEditFileState state)
	{
		if (!state.Exists
			|| !string.Equals(state.Sha256, current.Document.CurrentPlanSha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new CliUserException(
				"DecorationEditPlanChangedBeforeApply",
				"写入前 decoration.json 已变化；请重新导出规格、生成计划并审阅。 ");
		}
	}

	private static void EnsureActivationInputUnchanged(CliDecorationEditBuildResult current, CliDecorationEditFileState state)
	{
		var expected = current.Document.Inputs.HostDerived.DecorationActivations;
		if (state.Exists != expected.Exists
			|| state.Length != expected.Length
			|| !string.Equals(state.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new CliUserException(
				"DecorationEditActivationChangedBeforeApply",
				"写入前 decoration-activations.json 已变化；请重新生成并审阅装饰编辑计划。 ");
		}
	}

	private static string? NormalizeGuid(string? value)
		=> Guid.TryParse(value, out var id) ? id.ToString("N") : null;

	private static CliDecorationEditDiagnostic ToEditDiagnostic(CliHostDerivedDiagnostic source)
		=> new(source.Code, source.Severity, source.Blocking, source.Message, source.Path, source.NodeId, source.Details);

	private static string ComputeSha256(byte[] bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes));

	private static CliDecorationEditExecutionResult Failure(IEnumerable<CliDecorationEditDiagnostic> diagnostics)
		=> new(false, false, false, 0, Array.Empty<CliHostDerivedExecutionHost>(), DistinctDiagnostics(diagnostics));

	private static IReadOnlyList<CliDecorationEditDiagnostic> DistinctDiagnostics(IEnumerable<CliDecorationEditDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Severity}\u001f{item.Message}\u001f{item.Path}\u001f{item.NodeId}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ThenBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private sealed record CliDecorationEditActivationCommit(
		bool Success,
		bool Committed,
		int PrunedHostCount,
		IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics)
	{
		public static CliDecorationEditActivationCommit NoChange { get; } = new(true, false, 0, Array.Empty<CliDecorationEditDiagnostic>());
	}

	private sealed record CliDecorationEditFileState(string Path, bool Exists, long Length, string? Sha256, byte[]? Contents)
	{
		public static async ValueTask<CliDecorationEditFileState> CaptureAsync(string path, bool required, CancellationToken cancellationToken)
		{
			if (Directory.Exists(path))
				throw new CliUserException("DecorationEditFileTargetIsDirectory", $"装饰编辑文件路径被目录占用：{path}");
			if (!File.Exists(path))
			{
				if (required)
					throw new CliUserException("DecorationEditFileMissing", $"装饰编辑需要的文件不存在：{path}");
				return new CliDecorationEditFileState(path, false, 0, null, null);
			}
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReparsePoint) != 0)
				throw new CliUserException("DecorationEditFileReparsePoint", $"装饰编辑文件不能是链接或其他 reparse point：{path}");
			var contents = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			return new CliDecorationEditFileState(path, true, contents.LongLength, ComputeSha256(contents), contents);
		}

		public async ValueTask ReplaceAsync(byte[] contents, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(contents);
			var directory = System.IO.Path.GetDirectoryName(Path)
				?? throw new CliUserException("DecorationEditFilePathInvalid", $"无法解析装饰编辑文件目录：{Path}");
			if (Directory.Exists(Path))
				throw new CliUserException("DecorationEditFileTargetIsDirectory", $"装饰编辑文件路径被目录占用：{Path}");
			if (File.Exists(Path) && (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
				throw new CliUserException("DecorationEditFileReparsePoint", $"装饰编辑文件不能变为链接或其他 reparse point：{Path}");
			Directory.CreateDirectory(directory);
			var temporary = Path + ".cli-decoration-edit-" + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
				File.Move(temporary, Path, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
		}

		public async ValueTask<IReadOnlyList<CliDecorationEditDiagnostic>> RestoreAsync()
		{
			try
			{
				if (Exists)
				{
					if (Contents is null) throw new InvalidDataException("缺少回滚所需的原始文件内容。 ");
					await ReplaceAsync(Contents, CancellationToken.None).ConfigureAwait(false);
				}
				else if (File.Exists(Path))
				{
					if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
						throw new InvalidDataException("无法删除已变为 reparse point 的回滚文件。 ");
					File.Delete(Path);
				}
				return Array.Empty<CliDecorationEditDiagnostic>();
			}
			catch (Exception exception) when (exception is CliUserException or IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return
				[
					new CliDecorationEditDiagnostic(
						"DecorationEditFileRollbackFailed", "error", true,
						$"无法恢复“{Path}”：{exception.Message}",
						Path,
						Details: exception.ToString()),
				];
			}
		}

		private static string ComputeSha256(byte[] bytes)
			=> Convert.ToHexString(SHA256.HashData(bytes));
	}
}
