using HD2ModCore.Domain;

namespace HD2ModCli;

internal sealed record CliHelmetToArmorMergeSourceExecution(
	string SourceNodeId,
	string ModName,
	bool Success,
	string? OutputDirectory,
	int ProcessedPatchCount,
	int MergedArmorUnitCount,
	int HiddenHelmetUnitCount,
	IReadOnlyList<CliHelmetToArmorMergeDiagnostic> Diagnostics);

internal sealed record CliHelmetToArmorMergeExecutionResult(
	bool Success,
	string? OutputDirectory,
	IReadOnlyList<CliHelmetToArmorMergeSourceExecution> Sources,
	IReadOnlyList<CliHelmetToArmorMergeDiagnostic> Diagnostics);

/// <summary>
/// Executes an already-reviewed helmet-to-armor plan only into a fresh,
/// library-external directory.  Every selected source gets an isolated output
/// child so Patch filenames from different Mods can never overwrite each
/// other.  Nothing is published until every source output succeeds.
/// </summary>
internal static class CliHelmetToArmorMergeExecutor
{
	public static async ValueTask<CliHelmetToArmorMergeExecutionResult> ApplyAsync(
		CliHelmetToArmorMergePlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliHelmetToArmorMergePlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
		{
			return new CliHelmetToArmorMergeExecutionResult(
				false,
				null,
				Array.Empty<CliHelmetToArmorMergeSourceExecution>(),
				validation.Diagnostics);
		}

		var target = PrepareOutputDirectory(
			outputDirectory,
			workspace.LibraryRoot,
			document.Request.GameDataDirectory);
		var parent = Path.GetDirectoryName(target)
			?? throw new CliUserException("HelmetMergeOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-helmet-merge-" + Guid.NewGuid().ToString("N"));
		var sourceExecutions = new List<CliHelmetToArmorMergeSourceExecution>();
		var diagnostics = new List<CliHelmetToArmorMergeDiagnostic>(validation.Diagnostics);
		try
		{
			foreach (var selection in validation.ResolvedSelections.OrderBy(pair => pair.Key.Value))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourceNodeId = CliProjections.FormatId(selection.Key);
				if (!validation.Current.SourcesByNodeId.TryGetValue(sourceNodeId, out var source))
				{
					var missing = new CliHelmetToArmorMergeDiagnostic(
						"HelmetMergeSourceNotFound",
						"error",
						true,
						$"当前验证结果缺少已选来源：{sourceNodeId}。",
						NodeId: sourceNodeId);
					diagnostics.Add(missing);
					return new CliHelmetToArmorMergeExecutionResult(false, null, sourceExecutions, diagnostics);
				}

				var sourceOutput = Path.Combine(staged, "source-" + sourceNodeId);
				var result = await CliHelmetToArmorMergePlanArtifacts
					.ExecuteAsync(validation.Current, selection.Key, selection.Value, sourceOutput, cancellationToken)
					.ConfigureAwait(false);
				var sourceDiagnostics = result.Issues.Select(CreateDiagnostic).ToArray();
				diagnostics.AddRange(sourceDiagnostics);
				sourceExecutions.Add(new CliHelmetToArmorMergeSourceExecution(
					sourceNodeId,
					source.Node.Metadata.Name,
					result.IsSuccessful,
					result.IsSuccessful ? sourceOutput : null,
					result.ProcessedPatchCount,
					result.MergedArmorUnitCount,
					result.HiddenHelmetUnitCount,
					sourceDiagnostics));
				if (!result.IsSuccessful)
				{
					return new CliHelmetToArmorMergeExecutionResult(false, null, sourceExecutions, DistinctDiagnostics(diagnostics));
				}
			}

			if (sourceExecutions.Count == 0)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeSelectionRequired",
					"error",
					true,
					"计划没有可执行的来源模型集合。"));
				return new CliHelmetToArmorMergeExecutionResult(false, null, sourceExecutions, DistinctDiagnostics(diagnostics));
			}
			if (Directory.Exists(target) || File.Exists(target))
			{
				throw new CliUserException(
					"HelmetMergeOutputAlreadyExists",
					$"输出目录在生成过程中已出现：{target}。为避免混合旧文件，CLI 未发布本次结果。 ");
			}
			Directory.Move(staged, target);
			var published = sourceExecutions
				.Select(source => source with
				{
					OutputDirectory = source.OutputDirectory is null
						? null
						: RemapStagedPath(source.OutputDirectory, staged, target)
				})
				.ToArray();
			return new CliHelmetToArmorMergeExecutionResult(true, target, published, DistinctDiagnostics(diagnostics));
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
			throw new CliUserException("HelmetMergeWriteFailed", $"头盔合并输出未完成：{exception.Message}");
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
			throw new CliUserException("HelmetMergeOutputRequired", "equipment helmet-merge apply 必须显式提供 --output <新的输出目录>。 ");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "HelmetMergeOutputInvalid");
		if (File.Exists(target) || Directory.Exists(target))
		{
			throw new CliUserException(
				"HelmetMergeOutputAlreadyExists",
				$"头盔合并只发布到新的目录，当前输出已存在：{target}。请指定一个新的目录，避免覆盖或混合旧 Patch。 ");
		}
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
		{
			throw new CliUserException(
				"HelmetMergeOutputInsideLibrary",
				"头盔合并输出不能位于 Mod 库内；请使用 Mod 库以外的新目录。 ");
		}

		var gameData = Path.GetFullPath(gameDataDirectory);
		if (IsSameOrChildPath(target, gameData))
		{
			throw new CliUserException(
				"HelmetMergeOutputInsideGameData",
				"头盔合并输出不能位于游戏 GameData 目录内；CLI 不会直接部署或混入游戏资源。 ");
		}
		var gameRoot = Directory.GetParent(gameData)?.FullName;
		if (!string.IsNullOrWhiteSpace(gameRoot) && IsSameOrChildPath(target, gameRoot))
		{
			throw new CliUserException(
				"HelmetMergeOutputInsideGameRoot",
				"头盔合并输出不能位于游戏根目录内；请使用游戏目录以外的新目录。 ");
		}
		return target;
	}

	private static IReadOnlyList<CliHelmetToArmorMergeDiagnostic> DistinctDiagnostics(
		IEnumerable<CliHelmetToArmorMergeDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(diagnostic => new
			{
				diagnostic.Code,
				diagnostic.Severity,
				diagnostic.Message,
				diagnostic.Path,
				diagnostic.NodeId,
			})
			.Select(group => group.First())
			.ToArray();

	private static CliHelmetToArmorMergeDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

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
}
