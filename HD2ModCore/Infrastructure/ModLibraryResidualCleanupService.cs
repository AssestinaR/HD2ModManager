using HD2ModCore.Domain;
using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

// 作用：实现模组库残留目录清理，只删除未被库引用且不含任何载荷的"管理器形状"空目录/纯元数据目录。
// Purpose: Implements residual mods-root cleanup; only unreferenced, manager-shaped empty or
// metadata-only directories are removed. Payload-bearing folders are left for synchronization to recover.
public sealed class ModLibraryResidualCleanupService : IModLibraryResidualCleanupService
{
	private const string TrashDirectoryName = ".hd2modmanager-trash";
	private readonly IPatchFileNameParser _patchFileNameParser;

	public ModLibraryResidualCleanupService(IPatchFileNameParser patchFileNameParser)
		=> _patchFileNameParser = patchFileNameParser ?? throw new ArgumentNullException(nameof(patchFileNameParser));

	public ModLibraryResidualCleanupResult Cleanup(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var root = Path.GetFullPath(modsRootDirectory);
		if (!Directory.Exists(root)) return new(0, Array.Empty<string>());

		var referenced = snapshot.Nodes.Values
			.Select(node => Normalize(node.RelativePath))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var removed = 0;
		var failures = new List<string>();
		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Normalize(Path.GetRelativePath(root, directory));
			if (string.Equals(Path.GetFileName(directory), TrashDirectoryName, StringComparison.OrdinalIgnoreCase)
				|| referenced.Contains(relative)
				|| referenced.Any(path => path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
				continue;
			if (!IsSafeResidual(directory, cancellationToken)) continue;
			try
			{
				ClearAttributes(directory);
				Directory.Delete(directory, recursive: true);
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{relative}: {exception.Message}");
			}
		}
		return new(removed, failures);
	}

	private bool IsSafeResidual(string directory, CancellationToken cancellationToken)
	{
		var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
		if (files.Length == 0) return true;
		if (ModNodeStorageProtocol.TryReadLogicalNodeId(directory, out _)) return false;
		if (ExternalInstallFileFactsService.HasRecognizedFiles(directory)) return false;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(file);
			if (_patchFileNameParser.TryParse(name, out _)) return false;
			if (!IsManagerMetadata(name)) return false;
		}
		return true;
	}

	private static bool IsManagerMetadata(string name)
		=> string.Equals(name, ModNodeStorageProtocol.LogicalMarkerFileName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, ModNodeStorageProtocol.OptionRelationFileName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
			|| name.StartsWith("icon.", StringComparison.OrdinalIgnoreCase);

	private static void ClearAttributes(string directory)
	{
		foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Prepend(directory))
		{
			try
			{
				var attributes = File.GetAttributes(path);
				File.SetAttributes(path, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	private static string Normalize(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace('\\', '/').Trim('/');
}
