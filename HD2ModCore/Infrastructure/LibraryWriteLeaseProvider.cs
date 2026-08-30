using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Uses an exclusive OS file handle rather than a named in-memory gate so it
/// protects independent CLI processes. The lease file is DeleteOnClose and is
/// never treated as library content by scanners.
/// </summary>
public sealed class LibraryWriteLeaseProvider : ILibraryWriteLeaseProvider
{
	private const string LeaseFileName = ".hd2mod-cli-write.lock";

	public ValueTask<ILibraryWriteLease> AcquireAsync(string libraryRoot, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
		cancellationToken.ThrowIfCancellationRequested();
		var fullRoot = Path.GetFullPath(libraryRoot);
		try
		{
			Directory.CreateDirectory(fullRoot);
			var stream = new FileStream(
				Path.Combine(fullRoot, LeaseFileName),
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 1,
				FileOptions.DeleteOnClose);
			return ValueTask.FromResult<ILibraryWriteLease>(new FileLibraryWriteLease(fullRoot, stream));
		}
		catch (IOException exception)
		{
			throw new LibraryWriteLeaseUnavailableException(fullRoot, exception);
		}
	}

	private sealed class FileLibraryWriteLease : ILibraryWriteLease
	{
		private FileStream? _stream;

		public FileLibraryWriteLease(string libraryRoot, FileStream stream)
		{
			LibraryRoot = libraryRoot;
			_stream = stream;
		}

		public string LibraryRoot { get; }

		public ValueTask DisposeAsync()
		{
			Interlocked.Exchange(ref _stream, null)?.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
