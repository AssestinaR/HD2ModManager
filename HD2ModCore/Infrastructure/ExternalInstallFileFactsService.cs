using System.Security.Cryptography;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：扫描 Mod 根目录中的安全外部安装文件并计算其完整事实；不会自动部署未识别文件。
// Purpose: Scans safe external install files under a Mod root and computes complete facts; unknown files are never auto-deployed.
public sealed class ExternalInstallFileFactsService : IExternalInstallFileFactsService
{
	public async ValueTask<ExternalInstallDetectionResult> DetectAsync(
		string sourceRootDirectory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceRootDirectory))
			throw new ArgumentException("A source root directory is required.", nameof(sourceRootDirectory));
		if (!Directory.Exists(sourceRootDirectory))
			throw new DirectoryNotFoundException(sourceRootDirectory);

		var files = new List<ExternalInstallFileFact>();
		var unrecognized = new List<string>();
		foreach (var fullPath in EnumerateCandidateFiles(sourceRootDirectory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = Path.GetRelativePath(sourceRootDirectory, fullPath);
			if (IsReparsePoint(fullPath))
			{
				if (IsExternalCandidate(relativePath))
					unrecognized.Add(NormalizeForResult(relativePath));
				continue;
			}
			if (!TryMapRecognizedFile(
				fullPath,
				relativePath,
				out var targetRoot,
				out var normalizedSource,
				out var targetRelativePath,
				out var kind))
			{
				if (IsExternalCandidate(relativePath))
					unrecognized.Add(NormalizeForResult(relativePath));
				continue;
			}

			var info = new FileInfo(fullPath);
			var hash = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
			files.Add(new ExternalInstallFileFact(normalizedSource, targetRoot, targetRelativePath, info.Length, hash, kind, info.LastWriteTimeUtc));
		}

		files.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.SourceRelativePath, right.SourceRelativePath));
		unrecognized.Sort(StringComparer.OrdinalIgnoreCase);
		var conflicts = files
			.GroupBy(file => file.ConflictKey, StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.Select(group => new ExternalInstallConflict(group.Key, group.ToArray()))
			.ToArray();

		return new ExternalInstallDetectionResult(
			Path.GetFullPath(sourceRootDirectory),
			files,
			unrecognized,
			conflicts);
	}

	// Used by the import planner/object-tree scanner before a full hash pass. It
	// answers only whether the directory contains a file belonging to the
	// explicitly supported external-install contract.
	public static bool HasRecognizedFiles(string sourceRootDirectory)
	{
		if (string.IsNullOrWhiteSpace(sourceRootDirectory) || !Directory.Exists(sourceRootDirectory)) return false;
		foreach (var path in EnumerateCandidateFiles(sourceRootDirectory))
		{
			if (!IsReparsePoint(path)
				&& TryMapRecognizedFile(path, Path.GetRelativePath(sourceRootDirectory, path), out _, out _, out _, out _))
				return true;
		}
		return false;
	}

	// Shared by full fact production, generation calculation and library copying
	// so INI classification cannot disagree between import and later deployment.
	internal static bool TryMapRecognizedFile(
		string fullPath,
		string relativePath,
		out ExternalInstallTargetRoot targetRoot,
		out string normalizedSourceRelativePath,
		out string targetRelativePath,
		out ExternalInstallFileKind kind)
	{
		targetRoot = default;
		normalizedSourceRelativePath = string.Empty;
		targetRelativePath = string.Empty;
		kind = default;

		if (ExternalInstallPathMapper.TryMap(
			relativePath,
			out targetRoot,
			out normalizedSourceRelativePath,
			out targetRelativePath))
		{
			kind = ExternalInstallFileKind.ReShadePlugin;
			return true;
		}

		if (!ReShadeIniClassifier.IsReShadePreset(fullPath)) return false;
		if (!ExternalInstallPathMapper.TryMap(
			relativePath,
			ExternalInstallFileKind.ReShadePreset,
			out targetRoot,
			out normalizedSourceRelativePath,
			out targetRelativePath))
		{
			return false;
		}

		kind = ExternalInstallFileKind.ReShadePreset;
		return true;
	}

	private static bool IsExternalCandidate(string relativePath)
	{
		if (!ExternalInstallPathMapper.TryNormalizeRelativePath(relativePath, out var normalized)) return false;
		var segments = normalized.Split('/');
		return segments.Length == 1
			|| string.Equals(segments[0], "bin", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeForResult(string relativePath)
		=> ExternalInstallPathMapper.TryNormalizeRelativePath(relativePath, out var normalized)
			? normalized
			: relativePath.Replace('\\', '/');

	private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
	}

	// This is intentionally internal: generation calculation needs the same
	// no-follow-reparse traversal as detection, but must not hash files again.
	internal static IEnumerable<string> EnumerateCandidateFiles(string sourceRootDirectory)
	{
		IEnumerable<string> rootFiles;
		try { rootFiles = Directory.EnumerateFiles(sourceRootDirectory, "*", SearchOption.TopDirectoryOnly); }
		catch (IOException) { rootFiles = Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { rootFiles = Array.Empty<string>(); }
		foreach (var file in rootFiles) yield return file;

		var binDirectory = Path.Combine(sourceRootDirectory, "bin");
		if (!Directory.Exists(binDirectory) || IsReparsePoint(binDirectory)) yield break;

		var pending = new Stack<string>();
		pending.Push(binDirectory);
		while (pending.Count > 0)
		{
			var directory = pending.Pop();
			IEnumerable<string> files;
			try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly); }
			catch (IOException) { continue; }
			catch (UnauthorizedAccessException) { continue; }
			foreach (var file in files) yield return file;

			IEnumerable<string> children;
			try { children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly); }
			catch (IOException) { continue; }
			catch (UnauthorizedAccessException) { continue; }
			foreach (var child in children)
				if (!IsReparsePoint(child)) pending.Push(child);
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}
}
