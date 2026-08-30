using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Executes only a previously reviewed Profile alignment. The plan is never a
/// write authority: it is revalidated before and after the existing library
/// write lease is acquired, and persistence is restricted to profiles.json.
/// </summary>
internal static class CliProfileAlignExecutor
{
	public static async ValueTask<CliProfileAlignExecutionResult> ApplyAsync(
		CliProfileAlignPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var first = await CliProfileAlignPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!first.IsValid)
			return Failure(first.Diagnostics);
		if (!first.RequiresWrite)
			return AlreadyAligned(first.Current?.Document.Before);

		try
		{
			await using var session = await CliWriteTargetGuard.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken).ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var locked = await CliProfileAlignPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
			if (!locked.IsValid)
				return Failure(locked.Diagnostics);
			if (!locked.RequiresWrite)
				return AlreadyAligned(locked.Current?.Document.Before);

			var snapshot = await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false)
				?? throw new CliUserException("LibrarySnapshotUnreadable", "无法在 Profile 对齐写入前读取当前 Mod 库状态。 ");
			var profileId = new ProfileId(Guid.Parse(document.Request.ProfileId));
			var targetNodeIds = document.Request.EntryNodeIds.Select(raw => new ModNodeId(Guid.Parse(raw))).ToArray();
			var service = CoreServices.CreateProfileMutationService();
			var entries = service.ReplaceEntries(snapshot, profileId, targetNodeIds);
			if (!entries.Succeeded)
			{
				return Failure(
				[
					new CliProfileAlignDiagnostic(
						"ProfileAlignEntriesRejected", "error", true,
						entries.Message ?? $"Profile 成员替换被 Core 拒绝：{entries.Failure?.ToString() ?? "未知原因"}。",
						entries.Failure?.ToString()),
				]);
			}

			var plannedActive = string.IsNullOrWhiteSpace(document.After.ActiveProfileId)
				? (ProfileId?)null
				: new ProfileId(Guid.Parse(document.After.ActiveProfileId));
			var active = service.SetActiveProfile(entries.Snapshot, plannedActive);
			if (!active.Succeeded)
			{
				return Failure(
				[
					new CliProfileAlignDiagnostic(
						"ProfileAlignActiveModeRejected", "error", true,
						active.Message ?? $"Profile 活动状态变更被 Core 拒绝：{active.Failure?.ToString() ?? "未知原因"}。",
						active.Failure?.ToString()),
				]);
			}

			var finalSnapshot = active.Snapshot;
			if (!CliProfileAlignPlanArtifacts.MatchesExpectedAfter(document, finalSnapshot))
			{
				return Failure(
				[
					new CliProfileAlignDiagnostic(
						"ProfileAlignResultMismatch", "error", true,
						"Core 生成的 Profile 结果未与已审阅计划的目标状态一致；已拒绝写入。"),
				]);
			}

			if (!entries.Changed && !active.Changed)
				return AlreadyAligned(CliProfileAlignPlanArtifacts.CreateProfileState(finalSnapshot, profileId));

			var persisted = await session.SaveProfilesOnlyAsync(finalSnapshot, cancellationToken).ConfigureAwait(false);
			if (!CliProfileAlignPlanArtifacts.MatchesExpectedAfter(document, persisted))
			{
				return Failure(
				[
					new CliProfileAlignDiagnostic(
						"ProfileAlignPersistedMismatch", "error", true,
						"profiles.json 写入后重新读取的 Profile 状态未与计划一致；请检查存储状态。"),
				]);
			}
			return new CliProfileAlignExecutionResult(
				true,
				"applied",
				true,
				CliProfileAlignPlanArtifacts.CreateProfileState(persisted, profileId),
				Array.Empty<CliProfileAlignDiagnostic>());
		}
		catch (CliUserException exception)
		{
			return Failure([new CliProfileAlignDiagnostic(exception.Code, "error", true, exception.Message)]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or FormatException)
		{
			return Failure(
			[
				new CliProfileAlignDiagnostic("ProfileAlignApplyFailed", "error", true, exception.Message, exception.ToString()),
			]);
		}
	}

	private static CliProfileAlignExecutionResult AlreadyAligned(CliProfileAlignProfileState? profile)
		=> new(true, "already-aligned", false, profile, Array.Empty<CliProfileAlignDiagnostic>());

	private static CliProfileAlignExecutionResult Failure(IReadOnlyList<CliProfileAlignDiagnostic> diagnostics)
		=> new(false, "rejected", false, null, diagnostics);
}

internal sealed record CliProfileAlignExecutionResult(
	bool Success,
	string Status,
	bool ProfilesWritten,
	CliProfileAlignProfileState? Profile,
	IReadOnlyList<CliProfileAlignDiagnostic> Diagnostics);
