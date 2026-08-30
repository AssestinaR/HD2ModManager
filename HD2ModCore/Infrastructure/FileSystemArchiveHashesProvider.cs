using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

// 作用：从本地文件系统读取 archivehashes.json（用于索引构建的输入数据）。
// Purpose: Reads archivehashes.json from the local filesystem (input data for index building).
public sealed class FileSystemArchiveHashesProvider : IArchiveHashesProvider
{
	private readonly StoragePaths _paths;

	public FileSystemArchiveHashesProvider(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<string> GetArchiveHashesJsonAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.ArchiveHashesPath))
		{
			return "{}";
		}

		return await File.ReadAllTextAsync(_paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false);
	}
}
