using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliCrossArmorTransferExecutionResult(
	bool Success,
	string? OutputDirectory,
	string? ReportPath,
	int OutputUnitCount,
	int ReplacementMeshCount,
	int MinifiedMeshCount,
	IReadOnlyList<CliCrossArmorTransferDiagnostic> Diagnostics);

/// <summary>
/// Materializes one validated Cross-armor candidate in a new external
/// directory. The hidden-Unit cache is deliberately no-op so the headless
/// output operation does not create or mutate Manager-derived cache files.
/// </summary>
internal static class CliCrossArmorTransferExecutor
{
	public static async ValueTask<CliCrossArmorTransferExecutionResult> ApplyAsync(
		CliCrossArmorTransferPlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliCrossArmorTransferPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
		{
			return new CliCrossArmorTransferExecutionResult(
				false, null, null, 0, 0, 0, validation.Diagnostics);
		}

		var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot, document.Request.GameDataDirectory);
		var parent = Path.GetDirectoryName(target)
			?? throw new CliUserException("CrossArmorOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-cross-armor-" + Guid.NewGuid().ToString("N"));
		try
		{
			await using var reader = CoreServices.CreateModInformationReader();
			var orchestrator = CoreServices.CreateCanonicalCrossArmorOrchestrator(
				validation.Current.Paths,
				reader,
				new NoOpHiddenUnitOutputCache());
			var request = new CrossArmorTransferCandidateRequest(
				validation.Current.SourcePatchTocPath,
				document.Request.GameDataDirectory,
				staged,
				validation.Current.CorePlan,
				CrossArmorMaterialBindingMode.PreserveSourceReferences,
				validation.Current.PreparedSourceEntries,
				Progress: null,
				AutoHideUnmappedTargetUnits: document.Request.AutoHideUnmappedTargetUnits,
				DirectSourceUnitReuse: true,
				UseSharedHiddenUnitTemplate: true);
			var result = await orchestrator.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
			var diagnostics = result.Issues.Select(CreateDiagnostic).ToList();
			if (!result.IsSuccessful)
			{
				return new CliCrossArmorTransferExecutionResult(
					false,
					null,
					null,
					result.OutputUnitCount,
					result.ReplacementMeshCount,
					result.MinifiedMeshCount,
					DistinctDiagnostics(diagnostics));
			}

			// The generated candidate can take a while. Re-read all frozen facts before
			// publication so a concurrent source/GameData change cannot be published as
			// if it still matched the reviewed plan.
			var finalValidation = await CliCrossArmorTransferPlanArtifacts
				.ValidateAsync(document, workspace, cancellationToken)
				.ConfigureAwait(false);
			if (!finalValidation.IsValid)
			{
				diagnostics.AddRange(finalValidation.Diagnostics);
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorInputChangedBeforePublish", "error", true,
					"跨护甲候选生成期间输入发生变化；结果未发布。请重新生成计划。"));
				return new CliCrossArmorTransferExecutionResult(
					false, null, null, result.OutputUnitCount, result.ReplacementMeshCount, result.MinifiedMeshCount, DistinctDiagnostics(diagnostics));
			}
			if (Directory.Exists(target) || File.Exists(target))
			{
				throw new CliUserException("CrossArmorOutputAlreadyExists", $"输出目录在生成过程中已出现：{target}。为避免混合旧文件，CLI 未发布本次结果。 ");
			}
			Directory.Move(staged, target);
			return new CliCrossArmorTransferExecutionResult(
				true,
				target,
				RemapStagedPath(result.ReportPath, staged, target),
				result.OutputUnitCount,
				result.ReplacementMeshCount,
				result.MinifiedMeshCount,
				DistinctDiagnostics(diagnostics));
		}
		catch (CliUserException) { throw; }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("CrossArmorWriteFailed", $"跨护甲候选输出未完成：{exception.Message}");
		}
		finally
		{
			if (Directory.Exists(staged))
			{
				try { Directory.Delete(staged, recursive: true); }
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
			}
		}
	}

	private static string PrepareOutputDirectory(string outputDirectory, string? libraryRoot, string gameDataDirectory)
	{
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new CliUserException("CrossArmorOutputRequired", "equipment cross-armor apply 必须显式提供 --output <新的输出目录>。 ");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "CrossArmorOutputInvalid");
		if (File.Exists(target) || Directory.Exists(target))
		{
			throw new CliUserException("CrossArmorOutputAlreadyExists", "跨护甲候选只发布到新的目录；当前输出目录已存在。请指定一个新的空目录。 ");
		}
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
		{
			throw new CliUserException("CrossArmorOutputInsideLibrary", "跨护甲候选输出不能位于 Mod 库内；请使用库外的新目录。 ");
		}
		var gameData = Path.GetFullPath(gameDataDirectory);
		if (IsSameOrChildPath(target, gameData))
		{
			throw new CliUserException("CrossArmorOutputInsideGameData", "跨护甲候选输出不能位于游戏 GameData 目录内；CLI 不会直接部署或混入游戏资源。 ");
		}
		var gameRoot = Directory.GetParent(gameData)?.FullName;
		if (!string.IsNullOrWhiteSpace(gameRoot) && IsSameOrChildPath(target, gameRoot))
		{
			throw new CliUserException("CrossArmorOutputInsideGameRoot", "跨护甲候选输出不能位于游戏根目录内；请使用游戏目录以外的新目录。 ");
		}
		return target;
	}

	private static CliCrossArmorTransferDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

	private static IReadOnlyList<CliCrossArmorTransferDiagnostic> DistinctDiagnostics(IEnumerable<CliCrossArmorTransferDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Path, diagnostic.NodeId })
			.Select(group => group.First()).ToArray();

	private static string? RemapStagedPath(string? path, string staged, string target)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		var fullPath = Path.GetFullPath(path);
		var stagedRoot = Path.GetFullPath(staged).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!fullPath.StartsWith(stagedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			&& !fullPath.StartsWith(stagedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			return fullPath;
		return Path.Combine(target, Path.GetRelativePath(stagedRoot, fullPath));
	}

	private static bool IsSameOrChildPath(string candidate, string root)
	{
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private sealed class NoOpHiddenUnitOutputCache : ICanonicalHiddenUnitOutputCache
	{
		public ValueTask InitializeAsync(string gameDataFingerprint, bool gameDataIndexIsCurrent, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;

		public ValueTask<CanonicalHiddenUnitOutput?> TryReadAsync(string archiveName, HD2ModAdaptation.PatchReconstruction.AssetKey unitKey, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<CanonicalHiddenUnitOutput?>(null);

		public ValueTask StoreAsync(string archiveName, CanonicalHiddenUnitOutput output, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;
	}
}
