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

		CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
		await using var dataLock = await CliManagerDataWriteLock.AcquireAsync(firstValidation.Current.Paths, cancellationToken).ConfigureAwait(false);
		CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
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
					var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(validation.Current.Paths)
						.GetArchiveHashesJsonAsync(cancellationToken).ConfigureAwait(false);
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

}
