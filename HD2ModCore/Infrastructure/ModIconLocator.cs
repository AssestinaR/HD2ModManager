using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：按约定从模组目录中解析同层级图标文件，不把图标路径写入 library.json。
// Purpose: Resolves convention-based mod icons from the mod directory without persisting icon paths in library.json.
public static class ModIconLocator
{
	private static readonly string[] PreferredFileNames =
	{
		"icon.png",
		"icon.jpg",
		"icon.jpeg",
		"icon.webp",
		"icon.bmp",
	};

	private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png",
		".jpg",
		".jpeg",
		".webp",
		".bmp",
	};

	/// <summary>
	/// The stable, manager-owned icon names. Callers that replace an icon should
	/// operate on this set instead of deleting arbitrary image assets supplied by
	/// a Mod author.
	/// </summary>
	public static IReadOnlyList<string> CanonicalFileNames => PreferredFileNames;

	public static bool IsSupportedExtension(string? extension)
		=> !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);

	public static string? TryResolve(StoragePaths paths, ModNode node)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(node);

		var directory = ResolveNodeDirectory(paths, node.RelativePath);
		return TryResolve(directory);
	}

	public static string? TryResolve(string? modDirectory)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
			{
				return null;
			}

			foreach (var fileName in PreferredFileNames)
			{
				var path = Path.Combine(modDirectory, fileName);
				if (File.Exists(path))
				{
					return path;
				}
			}

			return Directory.EnumerateFiles(modDirectory, "*.*", SearchOption.TopDirectoryOnly)
				.Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}

	private static string ResolveNodeDirectory(StoragePaths paths, string relativePath)
	{
		return Path.GetFullPath(Path.Combine(paths.ModsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
	}
}
