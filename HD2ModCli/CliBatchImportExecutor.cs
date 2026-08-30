using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliBatchImportSourceExecution(
	int Index,
	string SourcePath,
	string Status,
	ModImportExecutionResult? Result,
	string? ErrorCode,
	string? ErrorMessage);

internal sealed record CliBatchImportExecutionResult(
	string Status,
	bool Success,
	IReadOnlyList<CliBatchImportSourceExecution> Sources,
	CliImportProfileAddition? ProfileAddition,
	CliBatchImportPlanValidationResult Validation,
	string? ErrorCode = null,
	string? ErrorMessage = null);

/// <summary>
/// Applies validated sources one after another under one library lease. A batch
/// deliberately does not promise rollback of completed file imports: an
/// archive can be large and each source is a real independently-valid Mod
/// import. The result makes that boundary explicit to scripts and Agents.
/// </summary>
internal static class CliBatchImportExecutor
{
	public static async ValueTask<CliBatchImportExecutionResult> ApplyAsync(
		CliBatchImportPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliBatchImportPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!validation.IsValid)
			return new CliBatchImportExecutionResult("validation-failed", false, Array.Empty<CliBatchImportSourceExecution>(), null, validation);

		var target = validation.Target;
		if (target is null || string.IsNullOrWhiteSpace(target.LibraryRoot) || string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("BatchImportApplyValidationIncomplete", "批量导入验证未提供可执行的目标 Mod 库。请重新运行 import batch validate。 ");
		var paths = new StoragePaths(workspace.ManagerRoot, target.LibraryRoot);
		try
		{
			await using var lease = await CoreServices.CreateLibraryWriteLeaseProvider()
				.AcquireAsync(paths.ModsDirectory, cancellationToken)
				.ConfigureAwait(false);
			validation = await CliBatchImportPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
				return new CliBatchImportExecutionResult("validation-failed", false, Array.Empty<CliBatchImportSourceExecution>(), null, validation);

			var completed = new List<CliBatchImportSourceExecution>(document.Imports.Count);
			var importedNodeIds = new List<ModNodeId>();
			for (var index = 0; index < validation.Sources.Count; index++)
			{
				var source = validation.Sources[index];
				var current = source.Validation.CurrentInspection?.Source;
				if (current is null)
				{
					return new CliBatchImportExecutionResult(
						"validation-failed", false, completed, null, validation,
						"BatchImportSourceInspectionMissing", "批量导入验证没有返回可执行来源。 ");
				}
				try
				{
					var execution = CoreServices.CreateModImportExecutionService(paths);
					var result = string.Equals(current.Kind, "archive", StringComparison.Ordinal)
						? await execution.ImportArchiveAsync(current.EffectivePath, cancellationToken).ConfigureAwait(false)
						: await execution.ImportFolderAsync(current.EffectivePath, cancellationToken).ConfigureAwait(false);
					importedNodeIds.AddRange(GetImportedNodeIds(result));
					completed.Add(new CliBatchImportSourceExecution(index, current.RequestedPath, "completed", result, null, null));
				}
				catch (ModImportPostCommitException exception)
				{
					importedNodeIds.AddRange(GetImportedNodeIds(exception.PartialResult));
					completed.Add(new CliBatchImportSourceExecution(
						index,
						current.RequestedPath,
						"post-commit-incomplete",
						exception.PartialResult,
						"ImportPostCommitIncomplete",
						exception.InnerException?.Message ?? exception.Message));
					return new CliBatchImportExecutionResult(
						"post-commit-incomplete", false, completed, null, validation,
						"ImportPostCommitIncomplete", exception.InnerException?.Message ?? exception.Message);
				}
				catch (Exception exception) when (exception is IOException
					or UnauthorizedAccessException
					or InvalidDataException
					or JsonException
					or DirectoryNotFoundException
					or FileNotFoundException)
				{
					completed.Add(new CliBatchImportSourceExecution(index, current.RequestedPath, "failed", null, "BatchImportSourceFailed", exception.Message));
					return new CliBatchImportExecutionResult("partial", false, completed, null, validation, "BatchImportSourceFailed", exception.Message);
				}
			}

			try
			{
				var profileAddition = await CliImportProfileIntegration
					.AddAsync(paths, document.AddToProfile, importedNodeIds, cancellationToken)
					.ConfigureAwait(false);
				return new CliBatchImportExecutionResult("completed", true, completed, profileAddition, validation);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
			{
				return new CliBatchImportExecutionResult(
					"post-commit-incomplete", false, completed, null, validation,
					"ImportProfilePostCommitIncomplete", exception.Message);
			}
		}
		catch (LibraryWriteLeaseUnavailableException exception)
		{
			throw new CliUserException("LibraryWriteBusy", $"目标 Mod 库正被另一条 CLI 写入操作占用：{exception.LibraryRoot}。请等待当前操作完成后重试。");
		}
	}

	private static IReadOnlyList<ModNodeId> GetImportedNodeIds(ModImportExecutionResult import)
		=> import.ImportedNodeIds.Count != 0 ? import.ImportedNodeIds : import.AddedNodeIds;
}
