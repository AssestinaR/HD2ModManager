using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal static class CliDeactivationExecutor
{
	public static async ValueTask<CliDeploymentExecutionResult> ApplyAsync(
		CliDeactivationPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var first = await CliDeactivationPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken).ConfigureAwait(false);
		if (!first.IsValid) return new CliDeploymentExecutionResult(false, null, null, first.Diagnostics);
		try
		{
			await using var librarySession = await CliWriteTargetGuard.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken).ConfigureAwait(false);
			await librarySession.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var target = new GameDataTargetGuard().Validate(workspace.GameDataRoot, cancellationToken);
			if (!target.IsValid || string.IsNullOrWhiteSpace(target.GameDataDirectory)) return new CliDeploymentExecutionResult(false, null, null, ToDiagnostics(target.Issues, "game-data"));
			await using var gameLease = await CoreServices.CreateGameDataDeploymentLeaseProvider().AcquireAsync(target.GameDataDirectory, cancellationToken).ConfigureAwait(false);
			await librarySession.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var locked = await CliDeactivationPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken).ConfigureAwait(false);
			if (!locked.IsValid || locked.Current is null) return new CliDeploymentExecutionResult(false, null, null, locked.Diagnostics);
			var ownership = locked.Current.OwnershipInspection;
			if (ownership is null || !ownership.IsCleanBootstrap && !ownership.IsManaged)
				return new CliDeploymentExecutionResult(false, null, null, locked.Diagnostics.Append(new CliDeploymentDiagnostic("DeploymentOwnershipNotProven", "error", true, "ownership", "现有 Patch 归属未被完整证明，已拒绝停用。 ")).ToArray());
			var plan = CliDeactivationPlanArtifacts.CreateApplyPlan(locked.Current);
			await using var transaction = await CliManagedDeploymentTransaction.BeginAsync(ownership, plan.Operations, cancellationToken).ConfigureAwait(false);
			var result = await CoreServices.CreateApplyExecutor().ExecuteAsync(plan, CancellationToken.None).ConfigureAwait(false);
			var diagnostics = locked.Diagnostics.Concat(ToDiagnostics(result.Issues, "deactivate")).ToList();
			if (result.Success)
			{
				await transaction.CompleteAsync().ConfigureAwait(false);
				return new CliDeploymentExecutionResult(true, "delete", result, diagnostics);
			}
			diagnostics.AddRange(await transaction.RollbackAsync().ConfigureAwait(false));
			return new CliDeploymentExecutionResult(false, "delete", result, diagnostics);
		}
		catch (CliUserException exception)
		{
			return new CliDeploymentExecutionResult(false, null, null, [new CliDeploymentDiagnostic(exception.Code, "error", true, "deactivate", exception.Message)]);
		}
		catch (GameDataDeploymentLeaseUnavailableException exception)
		{
			return new CliDeploymentExecutionResult(false, null, null, [new CliDeploymentDiagnostic("GameDataDeploymentBusy", "error", true, "deactivate", "GameData 正被另一条部署操作占用；请等待后重试。", exception.GameDataDirectory, Details: exception.ToString())]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			return new CliDeploymentExecutionResult(false, null, null, [new CliDeploymentDiagnostic("DeactivationApplyFailed", "error", true, "deactivate", exception.Message, Details: exception.ToString())]);
		}
	}

	private static IReadOnlyList<CliDeploymentDiagnostic> ToDiagnostics(IEnumerable<CoreIssue> issues, string stage)
		=> issues.Select(issue => new CliDeploymentDiagnostic(issue.Code, issue.Severity.ToString().ToLowerInvariant(), issue.Severity == CoreIssueSeverity.Error, stage, issue.Message, issue.FilePath, issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null, issue.ExceptionMessage)).ToArray();
}
