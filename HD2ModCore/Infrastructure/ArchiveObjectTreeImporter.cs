using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：使用随程序分发的 7-Zip 解包到临时目录，然后复用文件夹导入生成对象树。
public sealed class ArchiveObjectTreeImporter : IArchiveObjectTreeImporter
{
	private readonly IObjectTreeImporter _folderImporter;
	private readonly SevenZipArchiveExtractor _archiveExtractor = new();

	public ArchiveObjectTreeImporter(IObjectTreeImporter folderImporter)
	{
		_folderImporter = folderImporter ?? throw new ArgumentNullException(nameof(folderImporter));
	}

	public async ValueTask<ImportedObjectTree> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(archiveFilePath))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(archiveFilePath));
		}

		var fullPath = Path.GetFullPath(archiveFilePath);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException("Archive not found.", fullPath);
		}

		var extractRoot = Path.Combine(Path.GetTempPath(), "HD2ModManager", "import", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(extractRoot);

		try
		{
			await _archiveExtractor.ExtractAsync(fullPath, extractRoot, cancellationToken).ConfigureAwait(false);
			var tree = await _folderImporter.ImportFolderAsync(extractRoot, cancellationToken).ConfigureAwait(false);
			return tree with { SourceDisplayName = Path.GetFileName(fullPath) };
		}
		finally
		{
			TryDeleteTemporaryDirectory(extractRoot);
		}
	}

	private static void TryDeleteTemporaryDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);

			// The extraction root is nested below Temp/HD2ModManager/import. Remove
			// only empty parents so concurrent imports remain unaffected.
			var importRoot = Directory.GetParent(directory)?.FullName;
			if (importRoot is not null
				&& Directory.Exists(importRoot)
				&& !Directory.EnumerateFileSystemEntries(importRoot).Any())
				Directory.Delete(importRoot, recursive: false);
			var managerRoot = importRoot is null ? null : Directory.GetParent(importRoot)?.FullName;
			if (managerRoot is not null
				&& Directory.Exists(managerRoot)
				&& !Directory.EnumerateFileSystemEntries(managerRoot).Any())
				Directory.Delete(managerRoot, recursive: false);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

}
