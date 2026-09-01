using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

// 作用：从本地文件系统读取可选的 archivehashes.json（仅补充显示名称）。
// Purpose: Reads optional community archive metadata used only to enrich display names.
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
