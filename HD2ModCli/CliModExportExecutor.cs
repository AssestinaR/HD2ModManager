using System.Security.Cryptography;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliModExportExecutionResult(
	bool Success,
	string? OutputDirectory,
	string? ZipPath,
	int ExportedNodeCount,
	int ExportedFileCount,
	bool OverwroteExisting,
	IReadOnlyList<CliModExportDiagnostic> Diagnostics);

/// <summary>
/// Publishes a validated Mod subtree as one portable zip. The Core exporter
/// writes into a private sibling directory first; the final zip is moved into
/// the requested directory only after the source is rechecked.
/// </summary>
internal static class CliModExportExecutor
{
	public static async ValueTask<CliModExportExecutionResult> ApplyAsync(
		CliModExportPlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		var first = await CliModExportPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!first.IsValid || first.Current is null)
			return Failure(first.Diagnostics);

		var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot);
		var root = first.Current.Root;
		var finalZip = Path.Combine(target, SanitizeFileName(root.Metadata.Name) + ".zip");
		var hadExistingZip = File.Exists(finalZip);
		if (PathExistsIncludingReparsePoint(finalZip) && IsReparsePoint(finalZip))
			throw new CliUserException("ModExportOutputInvalid", $"导出目标压缩包是链接或其它 reparse point，已拒绝覆盖：{finalZip}");
		if (hadExistingZip && !overwrite)
			throw new CliUserException(
				"ModExportOutputAlreadyExists",
				$"导出目标已存在：{finalZip}。如需替换，请显式传入 --overwrite。 ");
		if (Directory.Exists(finalZip))
			throw new CliUserException("ModExportOutputInvalid", $"导出目标压缩包路径已被目录占用：{finalZip}");

		try
		{
			await using var session = await CliWriteTargetGuard
				.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
				.ConfigureAwait(false);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);

			var locked = await CliModExportPlanArtifacts
				.ValidateAsync(document, workspace, cancellationToken)
				.ConfigureAwait(false);
			if (!locked.IsValid || locked.Current is null)
				return Failure(locked.Diagnostics);

			var snapshot = await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false)
				?? throw new CliUserException("LibrarySnapshotUnreadable", "无法在导出前重新读取 library.json。 ");
			var rootId = new HD2ModCore.Domain.ModNodeId(Guid.Parse(locked.Document.Request.NodeId));
			if (!snapshot.Nodes.ContainsKey(rootId))
				throw new CliUserException("ModExportRootMissing", "导出前根 Mod 已从 library.json 消失；请重新生成计划。 ");

			var parent = Path.GetDirectoryName(target)
				?? throw new CliUserException("ModExportOutputInvalid", $"无法解析导出目录的父路径：{target}");
			Directory.CreateDirectory(parent);
			var stagedDirectory = Path.Combine(parent, ".hd2mod-cli-export-" + Guid.NewGuid().ToString("N"));
			try
			{
				var exporter = CoreServices.CreateModExporter(session.Paths);
				var stagedZip = await exporter
					.ExportToZipAsync(rootId, snapshot, stagedDirectory, cancellationToken)
					.ConfigureAwait(false);

				var after = await CliModExportPlanArtifacts
					.BuildAsync(workspace, locked.Document.Request.NodeId, cancellationToken)
					.ConfigureAwait(false);
				if (!string.Equals(
					locked.Document.InputFingerprint,
					after.Document.InputFingerprint,
					StringComparison.OrdinalIgnoreCase))
				{
					throw new CliUserException(
						"ModExportInputChangedDuringApply",
						"Mod、选项树或源文件在压缩过程中发生变化；本次结果未发布，请重新生成计划。 ");
				}

				if (File.Exists(finalZip) && !overwrite)
					throw new CliUserException("ModExportOutputAlreadyExists", $"导出目标已在生成期间出现：{finalZip}。请重新确认或传入 --overwrite。 ");
				if (Directory.Exists(finalZip))
					throw new CliUserException("ModExportOutputInvalid", $"导出目标压缩包路径已在生成期间被目录占用：{finalZip}");
				EnsureOutputDirectoryPathSafe(target);
				if (PathExistsIncludingReparsePoint(finalZip) && IsReparsePoint(finalZip))
					throw new CliUserException("ModExportOutputInvalid", $"导出目标压缩包是链接或其它 reparse point，已拒绝覆盖：{finalZip}");
				var overwroteExisting = File.Exists(finalZip);
				Directory.CreateDirectory(target);
				File.Move(stagedZip, finalZip, overwrite);
				return new CliModExportExecutionResult(
					true,
					target,
					finalZip,
					locked.Document.Nodes.Count,
					locked.Document.Nodes.Sum(node => node.Files.Count),
					overwroteExisting,
					locked.Diagnostics);
			}
			finally
			{
				if (Directory.Exists(stagedDirectory))
				{
					try { Directory.Delete(stagedDirectory, recursive: true); }
					catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
				}
			}
		}
		catch (CliUserException) { throw; }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or CryptographicException)
		{
			return Failure(
			[
				new CliModExportDiagnostic(
					"ModExportApplyFailed", "error", true,
					$"Mod 导出未完成：{exception.Message}",
					Details: exception.ToString()),
			]);
		}
	}

	private static string PrepareOutputDirectory(string outputDirectory, string? libraryRoot)
	{
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new CliUserException("ModExportOutputRequired", "mod export apply 必须显式提供 --output <导出目录>。 ");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "ModExportOutputInvalid");
		if (File.Exists(target))
			throw new CliUserException("ModExportOutputInvalid", $"导出目标是一个文件而不是目录：{target}");
		if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.Directory) == 0)
			throw new CliUserException("ModExportOutputInvalid", $"导出目标不是目录：{target}");
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
			throw new CliUserException("ModExportOutputInsideLibrary", "Mod 导出目录不能位于 Mod 库内；这会制造未登记的库内容。 ");
		EnsureOutputDirectoryPathSafe(target);
		return target;
	}

	private static void EnsureOutputDirectoryPathSafe(string target)
	{
		var current = Path.GetFullPath(target);
		while (!string.IsNullOrWhiteSpace(current))
		{
			if (PathExistsIncludingReparsePoint(current) && IsReparsePoint(current))
				throw new CliUserException("ModExportOutputReparsePoint", $"导出目录路径包含链接或其它 reparse point：{current}");
			if (File.Exists(current))
				throw new CliUserException("ModExportOutputInvalid", $"导出目录路径中的一段是文件而不是目录：{current}");
			var parent = Path.GetDirectoryName(current);
			if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
			current = parent;
		}
	}

	private static bool PathExistsIncludingReparsePoint(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException) { return false; }
		catch (DirectoryNotFoundException) { return false; }
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static string SanitizeFileName(string value)
	{
		foreach (var character in Path.GetInvalidFileNameChars())
			value = value.Replace(character, '_');
		return string.IsNullOrWhiteSpace(value.Trim()) ? "export" : value.Trim();
	}

	private static bool IsSameOrChildPath(string candidate, string root)
	{
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static CliModExportExecutionResult Failure(IEnumerable<CliModExportDiagnostic> diagnostics)
		=> new(false, null, null, 0, 0, false, diagnostics.ToArray());
}
