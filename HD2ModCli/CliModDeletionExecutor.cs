namespace HD2ModCli;

/// <summary>
/// The plan document is never an authority to delete: apply validates before
/// and after acquiring the cross-process library lease, then delegates the
/// actual relationship-aware mutation to the existing CLI deletion boundary.
/// </summary>
internal static class CliModDeletionExecutor
{
	public static async ValueTask<CliModDeletionApplyResult> ApplyAsync(
		CliModDeletionPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var first = await CliModDeletionPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!first.IsValid)
			return new CliModDeletionApplyResult(false, null, first.Diagnostics);
		try
		{
			await using var session = await CliWriteTargetGuard.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken).ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var locked = await CliModDeletionPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
			if (!locked.IsValid)
				return new CliModDeletionApplyResult(false, null, locked.Diagnostics);
			var deletion = await CliLibraryMutationOperations
				.DeleteAsync(session, document.Request.NodeIds, document.Request.Scope, cancellationToken)
				.ConfigureAwait(false);
			return new CliModDeletionApplyResult(
				deletion.Status == CliModDeletionStatus.Completed,
				deletion,
				locked.Diagnostics);
		}
		catch (CliUserException exception)
		{
			return new CliModDeletionApplyResult(false, null,
			[
				new CliModDeletionDiagnostic(exception.Code, "error", true, exception.Message),
			]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			return new CliModDeletionApplyResult(false, null,
			[
				new CliModDeletionDiagnostic("ModDeletionApplyFailed", "error", true, exception.Message, Details: exception.ToString()),
			]);
		}
	}
}

internal sealed record CliModDeletionApplyResult(
	bool Success,
	CliModDeletionExecutionResult? Deletion,
	IReadOnlyList<CliModDeletionDiagnostic> Diagnostics);
