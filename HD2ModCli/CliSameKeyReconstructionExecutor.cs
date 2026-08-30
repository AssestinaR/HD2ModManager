using HD2ModCore.Domain;

namespace HD2ModCli;

internal sealed record CliSameKeyReconstructionExecutionResult(
	bool Success,
	string? OutputDirectory,
	string? ReportJsonPath,
	string? ReportMarkdownPath,
	int OutputUnitCount,
	int ReplacementUnitCount,
	int MinifyOnlyUnitCount,
	int ReplacementMeshCount,
	int MinifiedMeshCount,
	IReadOnlyList<CliSameKeyDiagnostic> Diagnostics);

/// <summary>
/// Runs the fixed Canonical Same-key recipe into a freshly-created staging
/// directory, then publishes the complete candidate as one directory move.
/// It never replaces a library Mod, imports output, or deploys to GameData.
/// </summary>
internal static class CliSameKeyReconstructionExecutor
{
	public static async ValueTask<CliSameKeyReconstructionExecutionResult> ApplyAsync(
		CliSameKeyReconstructionPlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliSameKeyReconstructionPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
		{
			return new CliSameKeyReconstructionExecutionResult(
				false, null, null, null, 0, 0, 0, 0, 0, validation.Diagnostics);
		}

		var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot);
		var parent = Path.GetDirectoryName(target)
			?? throw new CliUserException("SameKeyOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-same-key-" + Guid.NewGuid().ToString("N"));
		try
		{
			var result = await CliSameKeyReconstructionPlanArtifacts
				.GenerateAsync(validation.Current, staged, cancellationToken)
				.ConfigureAwait(false);
			var diagnostics = validation.Diagnostics
				.Concat(result.Issues.Select(CreateDiagnostic))
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
			if (!result.IsSuccessful)
			{
				return new CliSameKeyReconstructionExecutionResult(
					false, null, null, null,
					result.OutputUnitCount, result.ReplacementUnitCount, result.MinifyOnlyUnitCount,
					result.ReplacementMeshCount, result.MinifiedMeshCount, diagnostics);
			}
			if (Directory.Exists(target) || File.Exists(target))
			{
				throw new CliUserException(
					"SameKeyOutputAlreadyExists",
					$"输出目录在生成过程中已出现：{target}。为避免混合旧文件，CLI 未发布本次结果。 ");
			}
			Directory.Move(staged, target);
			return new CliSameKeyReconstructionExecutionResult(
				true,
				target,
				RemapStagedPath(result.ReportJsonPath, staged, target),
				RemapStagedPath(result.ReportMarkdownPath, staged, target),
				result.OutputUnitCount,
				result.ReplacementUnitCount,
				result.MinifyOnlyUnitCount,
				result.ReplacementMeshCount,
				result.MinifiedMeshCount,
				diagnostics);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("SameKeyWriteFailed", $"Same-key 重构输出未完成：{exception.Message}");
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

	private static string PrepareOutputDirectory(string outputDirectory, string? libraryRoot)
	{
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new CliUserException("SameKeyOutputRequired", "patch repair apply 必须显式提供 --output <新的输出目录>。 ");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "SameKeyOutputInvalid");
		if (File.Exists(target) || Directory.Exists(target))
		{
			throw new CliUserException(
				"SameKeyOutputAlreadyExists",
				$"Same-key 重构只发布到新的目录，当前输出已存在：{target}。请指定一个新的目录，避免覆盖或混合旧 Patch。 ");
		}
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
		{
			throw new CliUserException(
				"SameKeyOutputInsideLibrary",
				"Same-key 重构输出不能位于 Mod 库内；请使用库外的新目录。 ");
		}
		return target;
	}

	private static CliSameKeyDiagnostic CreateDiagnostic(CoreIssue issue)
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
		var relative = Path.GetRelativePath(stagedRoot, fullPath);
		return Path.Combine(target, relative);
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
