namespace HD2ModCli;

internal sealed record CliLibrarySynchronizationExecutionResult(
	bool Success,
	bool Changed,
	IReadOnlyList<string> AddedNodeIds,
	IReadOnlyList<string> ChangedNodeIds,
	IReadOnlyList<string> MissingNodeIds,
	IReadOnlyList<CliLibrarySynchronizationDiagnostic> Diagnostics);

/// <summary>
/// Saves only validated node facts under the ordinary CLI library lease. It
/// never rewrites profiles.json, activation files, physical Mod files, caches,
/// or GameData.
/// </summary>
internal static class CliLibrarySynchronizationExecutor
{
	public static async ValueTask<CliLibrarySynchronizationExecutionResult> ApplyAsync(
		CliLibrarySynchronizationPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliLibrarySynchronizationPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
			return Failure(validation.Diagnostics);

		await using var session = await CliWriteTargetGuard
			.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
			.ConfigureAwait(false);
		// Repeat validation inside the cross-process lease before writing the
		// library file; the plan never bypasses this second scan.
		var locked = await CliLibrarySynchronizationPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!locked.IsValid || locked.Current is null)
			return Failure(locked.Diagnostics);

		var synchronization = locked.Current.Synchronization;
		if (!synchronization.HasSnapshotChanges)
		{
			return new CliLibrarySynchronizationExecutionResult(
				true,
				false,
				Array.Empty<string>(),
				Array.Empty<string>(),
				synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
				locked.Diagnostics);
		}

		_ = await session.SaveNodesOnlyAsync(synchronization.Snapshot, cancellationToken).ConfigureAwait(false);
		return new CliLibrarySynchronizationExecutionResult(
			true,
			true,
			synchronization.AddedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.ChangedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			locked.Diagnostics);
	}

	private static CliLibrarySynchronizationExecutionResult Failure(
		IReadOnlyList<CliLibrarySynchronizationDiagnostic> diagnostics)
		=> new(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
}
