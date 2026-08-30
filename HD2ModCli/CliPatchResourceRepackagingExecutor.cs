using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliPatchResourceRepackagingExecutionResult(
	bool Success,
	string? OutputDirectory,
	string? PatchTocPath,
	int SelectedAssetCount,
	IReadOnlyList<CliPatchResourceDiagnostic> Diagnostics);

/// <summary>
/// Materializes a validated resource subset in an isolated sibling directory,
/// then publishes it with one directory move.  The Mod library remains read
/// only throughout this workflow.
/// </summary>
internal static class CliPatchResourceRepackagingExecutor
{
	public static async ValueTask<CliPatchResourceRepackagingExecutionResult> ApplyAsync(
		CliPatchResourceRepackagingPlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliPatchResourceRepackagingPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
		{
			return new CliPatchResourceRepackagingExecutionResult(
				false,
				null,
				null,
				validation.ResolvedSelections.Count,
				validation.Diagnostics);
		}

		var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot);
		var parent = Path.GetDirectoryName(target)
			?? throw new CliUserException("PatchResourceOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-patch-repackage-" + Guid.NewGuid().ToString("N"));
		try
		{
			var service = CoreServices.CreatePatchResourceRepackagingService();
			var written = await service.WriteAsync(validation.ResolvedSelections, staged, cancellationToken).ConfigureAwait(false);
			if (Directory.Exists(target) || File.Exists(target))
			{
				throw new CliUserException(
					"PatchResourceOutputAlreadyExists",
					$"输出目录在生成过程中已出现：{target}。为避免混合旧文件，CLI 未发布本次结果。");
			}
			Directory.Move(staged, target);
			var patchToc = Directory.EnumerateFiles(target, "*.patch_*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
			return new CliPatchResourceRepackagingExecutionResult(
				true,
				target,
				patchToc,
				validation.ResolvedSelections.Count,
				validation.Diagnostics);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("PatchResourceWriteFailed", $"Patch 资源重打包未完成：{exception.Message}");
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
			throw new CliUserException("PatchResourceOutputRequired", "patch repackage apply 必须显式提供 --output <新的输出目录>。");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "PatchResourceOutputInvalid");
		if (File.Exists(target) || Directory.Exists(target))
		{
			throw new CliUserException(
				"PatchResourceOutputAlreadyExists",
				$"Patch 资源重打包只发布到新的目录，当前输出已存在：{target}。请指定一个新的目录，避免覆盖或混合旧 Patch。 ");
		}
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
		{
			throw new CliUserException(
				"PatchResourceOutputInsideLibrary",
				"Patch 资源重打包输出不能位于 Mod 库内；这会制造未登记内容。请使用 Mod 库以外的新目录。 ");
		}
		return target;
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
