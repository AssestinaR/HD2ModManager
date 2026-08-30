using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliDeploymentExecutionResult(
	bool Success,
	string? DeploymentMethod,
	ApplyResult? ApplyResult,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics);

/// <summary>
/// Guarded commit entry for a previously exported deployment plan. It never
/// trusts the JSON plan alone: all inputs are rebuilt and validated before and
/// after both write leases are acquired.
/// </summary>
internal static class CliDeploymentExecutor
{
	public static async ValueTask<CliDeploymentExecutionResult> ApplyAsync(
		CliDeploymentPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var firstValidation = await CliDeploymentPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken).ConfigureAwait(false);
		if (!firstValidation.IsValid)
			return new CliDeploymentExecutionResult(false, null, null, firstValidation.Diagnostics);

		try
		{
			await using var librarySession = await CliWriteTargetGuard.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken).ConfigureAwait(false);
			await librarySession.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			var targetValidation = new GameDataTargetGuard().Validate(workspace.GameDataRoot, cancellationToken);
			if (!targetValidation.IsValid || string.IsNullOrWhiteSpace(targetValidation.GameDataDirectory))
				return new CliDeploymentExecutionResult(false, null, null, ToDiagnostics(targetValidation.Issues, "game-data"));

			await using var gameLease = await CoreServices.CreateGameDataDeploymentLeaseProvider()
				.AcquireAsync(targetValidation.GameDataDirectory, cancellationToken)
				.ConfigureAwait(false);
			await librarySession.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			var lockedValidation = await CliDeploymentPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken).ConfigureAwait(false);
			if (!lockedValidation.IsValid || lockedValidation.Current is null)
				return new CliDeploymentExecutionResult(false, null, null, lockedValidation.Diagnostics);
			var current = lockedValidation.Current;
			if (current.OwnershipInspection is null || !current.OwnershipInspection.IsCleanBootstrap && !current.OwnershipInspection.IsManaged)
			{
				var diagnostics = lockedValidation.Diagnostics.Concat(
				[
					new CliDeploymentDiagnostic("DeploymentOwnershipNotProven", "error", true, "ownership", "GameData 中的现有 Patch 归属未被完整证明，已拒绝写入。"),
				]).ToArray();
				return new CliDeploymentExecutionResult(false, null, null, diagnostics);
			}

			var materialized = await CliDeploymentPlanArtifacts.MaterializeAsync(current, cancellationToken).ConfigureAwait(false);
			if (!materialized.IsReady)
				return new CliDeploymentExecutionResult(false, null, null, lockedValidation.Diagnostics.Concat(materialized.Diagnostics).ToArray());
			if (!CliDeploymentPlanArtifacts.MatchesMaterializedPlan(document, materialized))
			{
				var diagnostics = lockedValidation.Diagnostics.Concat(materialized.Diagnostics).Append(
					new CliDeploymentDiagnostic("DeploymentMaterializationDrift", "error", true, "apply", "Lua 物化后的 Patch 或外部文件映射与已确认计划不一致；已拒绝写入 GameData。"))
					.ToArray();
				return new CliDeploymentExecutionResult(false, null, null, diagnostics);
			}

			var capability = CoreServices.CreateDeploymentCapabilityService().Probe(librarySession.Paths.ModsDirectory, targetValidation.GameDataDirectory);
			if (!capability.IsAvailable || capability.Method is null)
			{
				return new CliDeploymentExecutionResult(false, null, null, lockedValidation.Diagnostics.Concat(materialized.Diagnostics).Append(
					new CliDeploymentDiagnostic("DeploymentUnavailable", "error", true, "capability", capability.Error ?? capability.Summary)).ToArray());
			}

			var executionPlan = materialized.ApplyPlan! with { DeploymentMethod = capability.Method.Value };
			await using var transaction = await CliManagedDeploymentTransaction.BeginAsync(
				current.OwnershipInspection,
				executionPlan.Operations,
				cancellationToken).ConfigureAwait(false);
			var applyResult = await CoreServices.CreateApplyExecutor().ExecuteAsync(executionPlan, CancellationToken.None).ConfigureAwait(false);
			var applyDiagnostics = ToDiagnostics(applyResult.Issues, "apply");
			if (applyResult.Success)
			{
				await transaction.CompleteAsync().ConfigureAwait(false);
				return new CliDeploymentExecutionResult(true, capability.Method.Value.ToString().ToLowerInvariant(), applyResult, lockedValidation.Diagnostics.Concat(materialized.Diagnostics).Concat(applyDiagnostics).ToArray());
			}

			var rollbackDiagnostics = await transaction.RollbackAsync().ConfigureAwait(false);
			return new CliDeploymentExecutionResult(false, capability.Method.Value.ToString().ToLowerInvariant(), applyResult, lockedValidation.Diagnostics.Concat(materialized.Diagnostics).Concat(applyDiagnostics).Concat(rollbackDiagnostics).ToArray());
		}
		catch (CliUserException exception)
		{
			return new CliDeploymentExecutionResult(false, null, null,
			[
				new CliDeploymentDiagnostic(exception.Code, "error", true, "apply", exception.Message),
			]);
		}
		catch (GameDataDeploymentLeaseUnavailableException exception)
		{
			return new CliDeploymentExecutionResult(false, null, null,
			[
				new CliDeploymentDiagnostic("GameDataDeploymentBusy", "error", true, "apply", "GameData 正被另一条部署操作占用；请等待后重试。", exception.GameDataDirectory, Details: exception.ToString()),
			]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			return new CliDeploymentExecutionResult(false, null, null,
			[
				new CliDeploymentDiagnostic("DeploymentApplyFailed", "error", true, "apply", exception.Message, Details: exception.ToString()),
			]);
		}
	}

	private static IReadOnlyList<CliDeploymentDiagnostic> ToDiagnostics(IEnumerable<CoreIssue> issues, string stage)
		=> issues.Select(issue => new CliDeploymentDiagnostic(
			issue.Code,
			issue.Severity.ToString().ToLowerInvariant(),
			issue.Severity == CoreIssueSeverity.Error,
			stage,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage)).ToArray();
}
