namespace HD2ModCore.Infrastructure;

// 作用：管理与模组库同卷的短期解压目录，并仅清理带有本应用标记的遗留导入目录。
public sealed class ImportTemporaryDirectoryManager
{
	private const string DirectoryPrefix = "import-";
	private const string MarkerFileName = ".hd2modmanager-import-temp";
	private readonly StoragePaths _paths;

	public ImportTemporaryDirectoryManager(StoragePaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

	public string Create()
	{
		Directory.CreateDirectory(_paths.ImportTempDirectory);
		var directory = Path.Combine(_paths.ImportTempDirectory, DirectoryPrefix + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, MarkerFileName), "HD2ModManager temporary import directory");
		return directory;
	}

	public void Delete(string directory)
	{
		if (!IsManagedDirectory(directory)) return;
		ClearReadOnlyAttributes(directory);
		Directory.Delete(directory, recursive: true);
		TryDeleteEmptyRoot();
	}

	public void CleanupStaleDirectories()
	{
		if (!Directory.Exists(_paths.ImportTempDirectory)) return;
		foreach (var directory in Directory.EnumerateDirectories(_paths.ImportTempDirectory, DirectoryPrefix + "*", SearchOption.TopDirectoryOnly).ToArray())
		{
			try { Delete(directory); } catch { }
		}
		TryDeleteEmptyRoot();
	}

	private static bool IsManagedDirectory(string directory)
		=> Directory.Exists(directory)
			&& Path.GetFileName(directory).StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase)
			&& File.Exists(Path.Combine(directory, MarkerFileName));

	private static void ClearReadOnlyAttributes(string directory)
	{
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
	}

	private void TryDeleteEmptyRoot()
	{
		try
		{
			if (Directory.Exists(_paths.ImportTempDirectory)
				&& !Directory.EnumerateFileSystemEntries(_paths.ImportTempDirectory).Any())
				Directory.Delete(_paths.ImportTempDirectory, recursive: false);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
