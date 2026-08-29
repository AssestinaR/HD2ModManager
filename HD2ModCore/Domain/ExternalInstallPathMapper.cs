namespace HD2ModCore.Domain;

// 作用：只负责外部文件相对路径的规范化和安全映射，不访问文件系统。
// Purpose: Pure path normalization and safe mapping for external files; does not access the filesystem.
public static class ExternalInstallPathMapper
{
	private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	public static bool TryMap(
		string sourceRelativePath,
		out ExternalInstallTargetRoot targetRoot,
		out string normalizedSourceRelativePath,
		out string targetRelativePath)
	{
		targetRoot = default;
		normalizedSourceRelativePath = string.Empty;
		targetRelativePath = string.Empty;

		if (!TryNormalizeRelativePath(sourceRelativePath, out normalizedSourceRelativePath))
			return false;

		var segments = normalizedSourceRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 1 && IsAddonFile(segments[0]))
		{
			targetRoot = ExternalInstallTargetRoot.GameBin;
			targetRelativePath = segments[0];
			return true;
		}

		if (segments.Length >= 2
			&& PathComparer.Equals(segments[0], "bin")
			&& IsAddonFile(segments[^1]))
		{
			targetRoot = ExternalInstallTargetRoot.GameBin;
			targetRelativePath = string.Join('/', segments.Skip(1));
			return true;
		}

		return false;
	}

	public static bool TryNormalizeRelativePath(string? path, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path)) return false;

		var candidate = path.Trim().Replace('\\', '/');
			if (candidate.StartsWith("/", StringComparison.Ordinal)
			|| candidate.StartsWith("//", StringComparison.Ordinal)
			|| Path.IsPathRooted(candidate)
			|| candidate.Contains('\0'))
			return false;

		var result = new List<string>();
		foreach (var segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment is ".") continue;
			if (segment is ".." || segment.Contains(':')) return false;
			result.Add(segment);
		}

		if (result.Count == 0) return false;
		normalizedPath = string.Join('/', result);
		return true;
	}

	public static string CreateConflictKey(ExternalInstallTargetRoot targetRoot, string targetRelativePath)
	{
		if (!TryNormalizeRelativePath(targetRelativePath, out var normalized))
			throw new ArgumentException("Target relative path must be a safe relative path.", nameof(targetRelativePath));

		return $"{targetRoot}:{normalized.ToUpperInvariant()}";
	}

	private static bool IsAddonFile(string path)
		=> path.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".addon", StringComparison.OrdinalIgnoreCase);
}
