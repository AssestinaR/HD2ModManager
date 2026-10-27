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
		var removals = new List<ModLibraryResidualRemoval>();
		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Normalize(Path.GetRelativePath(root, directory));
			if (string.Equals(Path.GetFileName(directory), TrashDirectoryName, StringComparison.OrdinalIgnoreCase)
				|| referenced.Contains(relative)
				|| referenced.Any(path => path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
				continue;
			if (!TryClassifyResidual(directory, out var fileNames, cancellationToken)) continue;
			try
			{
				ClearAttributes(directory);
				Directory.Delete(directory, recursive: true);
				removed++;
				removals.Add(new ModLibraryResidualRemoval(relative, fileNames));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{relative}: {exception.Message}");
			}
		}
		return new(removed, failures) { Removals = removals };
	}

	// 判定一个未被库引用的目录是否"纯粹是残留"。
	// A directory is residual when the library does not reference it and nothing inside it can be used by
	// the manager: no patch payload (including sidecars), no recognized external-install file, no logical
	// node marker. Everything else — including a folder holding only a mod author's json/text files —
	// qualifies, because the manager can never load such a folder as a Mod anyway.
	private bool TryClassifyResidual(string directory, out IReadOnlyList<string> fileNames, CancellationToken cancellationToken)
	{
		var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
		fileNames = files.Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToArray();
		if (files.Length == 0) return true;
		if (ModNodeStorageProtocol.TryReadLogicalNodeId(directory, out _)) return false;
		if (ExternalInstallFileFactsService.HasRecognizedFiles(directory)) return false;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(file);
			if (_patchFileNameParser.TryParse(name, out _)) return false;
			if (!IsHarmlessResidualFile(name)) return false;
		}
		return true;
	}

	// 管理器自身写下的元数据，以及 Mod 作者随包附带、管理器从不消费的说明/元数据文件。
	// Manager-written metadata plus author-supplied notes/metadata the manager never consumes.
	private static bool IsHarmlessResidualFile(string name)
	{
		if (string.Equals(name, ModNodeStorageProtocol.LogicalMarkerFileName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, ModNodeStorageProtocol.OptionRelationFileName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
			|| name.StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
			return true;

		var extension = Path.GetExtension(name);
		return extension.Length > 1 && HarmlessResidualExtensions.Contains(extension);
	}

	// 只放"管理器完全不读、删掉也不影响任何 Mod 加载"的扩展名。刻意不含 .vpk / .ini / .lua /
	// .patch_N / .gpu_resources / .stream：那些要么是载荷，要么已被上游判定覆盖。
	// Extensions the manager never reads. Deliberately excludes payload and recognized-install formats.
	private static readonly HashSet<string> HarmlessResidualExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".json", ".txt", ".log", ".md", ".nfo", ".url",
		".png", ".jpg", ".jpeg", ".webp", ".bmp",
	};

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
