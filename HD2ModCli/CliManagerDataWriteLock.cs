using System.Diagnostics;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Coordinates the narrow set of CLI commands that write manager-owned data
/// outside the Mod library: asset metadata and derived GameData indexes.
/// </summary>
internal static class CliManagerProcessGuard
{
	public static void EnsureDesktopManagerIsClosed()
	{
		try
		{
			var processIds = Process.GetProcessesByName("HD2ModManager")
				.Select(process =>
				{
					try { return process.Id; }
					finally { process.Dispose(); }
				})
				.OrderBy(id => id)
				.ToArray();
			if (processIds.Length != 0)
			{
				throw new CliUserException("ManagerProcessRunning", $"检测到 HD2ModManager 正在运行（PID：{string.Join(", ", processIds)}）。请先关闭所有桌面端实例，再执行管理器数据写入。");
			}
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			throw new CliUserException("ManagerProcessCheckFailed", $"无法可靠枚举 HD2ModManager 进程；为避免与桌面端并发写入，CLI 已拒绝本次写入：{exception.Message}");
		}
	}
}

internal sealed class CliManagerDataWriteLock : IAsyncDisposable
{
	private readonly string _path;
	private FileStream? _stream;

	private CliManagerDataWriteLock(string path, FileStream stream)
	{
		_path = path;
		_stream = stream;
	}

	public static ValueTask<CliManagerDataWriteLock> AcquireAsync(StoragePaths paths, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(paths);
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(paths.DataDirectory);
		var path = Path.Combine(paths.DataDirectory, ".hd2mod-cli-manager-data-write.lock");
		try
		{
			var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
			return ValueTask.FromResult(new CliManagerDataWriteLock(path, stream));
		}
		catch (IOException)
		{
			throw new CliUserException("ManagerDataWriteBusy", "另一条 CLI 管理器数据写入正在进行，或遗留锁文件尚未清理。请确认没有其他索引或资产元数据任务后重试。 ");
		}
	}

	public ValueTask DisposeAsync()
	{
		var current = Interlocked.Exchange(ref _stream, null);
		if (current is not null) current.Dispose();
		try
		{
			if (File.Exists(_path)) File.Delete(_path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		return ValueTask.CompletedTask;
	}
}
