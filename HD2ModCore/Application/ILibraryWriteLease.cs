namespace HD2ModCore.Application;

/// <summary>
/// A process-scoped exclusive lease for mutations of one Mod library root.
/// It complements in-process JSON gates so two CLI processes cannot both
/// execute a validated write plan against the same library at once.
/// </summary>
public interface ILibraryWriteLease : IAsyncDisposable
{
	string LibraryRoot { get; }
}

public interface ILibraryWriteLeaseProvider
{
	ValueTask<ILibraryWriteLease> AcquireAsync(string libraryRoot, CancellationToken cancellationToken = default);
}

public sealed class LibraryWriteLeaseUnavailableException : IOException
{
	public LibraryWriteLeaseUnavailableException(string libraryRoot, Exception innerException)
		: base($"Another HD2 Mod CLI mutation is already active for library: {libraryRoot}", innerException)
	{
		LibraryRoot = libraryRoot;
	}

	public string LibraryRoot { get; }
}
