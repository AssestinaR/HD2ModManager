using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Uses a DeleteOnClose handle in the validated data directory. It is never
/// acquired by plan/validate, only during an explicit deployment commit.
/// </summary>
public sealed class GameDataDeploymentLeaseProvider : IGameDataDeploymentLeaseProvider
{
	private const string LeaseFileName = ".hd2mod-deployment.lock";

	public ValueTask<IGameDataDeploymentLease> AcquireAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		cancellationToken.ThrowIfCancellationRequested();
		var fullPath = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
		try
		{
			var stream = new FileStream(
				Path.Combine(fullPath, LeaseFileName),
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 1,
				FileOptions.DeleteOnClose);
			return ValueTask.FromResult<IGameDataDeploymentLease>(new FileGameDataDeploymentLease(fullPath, stream));
		}
		catch (IOException exception)
		{
			throw new GameDataDeploymentLeaseUnavailableException(fullPath, exception);
		}
	}

	private sealed class FileGameDataDeploymentLease : IGameDataDeploymentLease
	{
		private FileStream? _stream;

		public FileGameDataDeploymentLease(string gameDataDirectory, FileStream stream)
		{
			GameDataDirectory = gameDataDirectory;
			_stream = stream;
		}

		public string GameDataDirectory { get; }

		public ValueTask DisposeAsync()
		{
			Interlocked.Exchange(ref _stream, null)?.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
