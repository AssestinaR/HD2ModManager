using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Json;

namespace HD2ModCore.Infrastructure;

// 作用：使用 JSON 文件持久化模组库（便于调试与便携，后续可迁移为 SQLite）。
// Purpose: Persists the mod library as a JSON file (portable and debuggable; can later migrate to SQLite).
public sealed class JsonModLibraryStore : IModLibraryStore
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);
	private sealed record LibraryFileSnapshot(
		int Version,
		DateTimeOffset SavedUtc,
		IReadOnlyDictionary<ModNodeId, ModNode> Nodes);

	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters =
		{
            new ModNodeIdJsonConverter(),
			new ProfileIdJsonConverter(),
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
		},
	};

	private const int CurrentVersion = 1;
	private const int TransactionVersion = 1;
	private const string TransactionJournalFileName = ".hd2modmanager-library-transaction.json";
	private readonly StoragePaths _paths;
	private readonly JsonProfileStore _profileStore;
	private readonly bool _allowLegacyMigration;
	private readonly Func<CancellationToken, ValueTask>? _beforeNodeFileCommit;

	public JsonModLibraryStore(StoragePaths paths, bool allowLegacyMigration = true)
		: this(paths, allowLegacyMigration, null)
	{
	}

	// Fault injection stays internal so transactional rollback can be verified
	// without exposing a production-only failure switch through IModLibraryStore.
	internal JsonModLibraryStore(
		StoragePaths paths,
		bool allowLegacyMigration,
		Func<CancellationToken, ValueTask>? beforeNodeFileCommit)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_profileStore = new JsonProfileStore(paths);
		_allowLegacyMigration = allowLegacyMigration;
		_beforeNodeFileCommit = beforeNodeFileCommit;
	}

	private string SnapshotPath => _paths.LibraryPath;
	private string TransactionJournalPath => Path.Combine(_paths.ModsDirectory, TransactionJournalFileName);

	public async ValueTask<LibrarySnapshot?> TryLoadAsync(CancellationToken cancellationToken = default)
	{
		var gate = GetFileGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await TryLoadCoreAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	// Strict read-only counterpart for command-line/agent inspection. It does not
	// create the library directory, migrate legacy files, or write recovery data.
	public async ValueTask<LibrarySnapshot?> TryLoadReadOnlyAsync(CancellationToken cancellationToken = default)
	{
		if (File.Exists(TransactionJournalPath))
		{
			var transaction = await ReadTransactionJournalAsync(cancellationToken).ConfigureAwait(false);
			if (transaction.State == LibraryTransactionState.Prepared)
				throw new InvalidDataException("The Mod library has an unfinished write transaction. Open the Manager once to recover it before using strict read-only inspection.");
			// A committed journal can only be a cleanup remnant after both documents
			// were persisted. Read-only callers may safely inspect that state without
			// deleting the journal themselves.
		}
		if (!File.Exists(SnapshotPath)) return null;

		var gate = GetFileGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var json = await File.ReadAllTextAsync(SnapshotPath, cancellationToken).ConfigureAwait(false);
			var fileSnapshot = JsonSerializer.Deserialize<LibraryFileSnapshot>(json, SerializerOptions);
			if (fileSnapshot is null) return null;
			if (fileSnapshot.Version != CurrentVersion)
				throw new InvalidDataException($"Unsupported library data version. Expected {CurrentVersion}.");

			var profileState = await _profileStore.TryLoadReadOnlyAsync(cancellationToken).ConfigureAwait(false);
			return new LibrarySnapshot(fileSnapshot.Version, fileSnapshot.SavedUtc, fileSnapshot.Nodes, profileState.Profiles, profileState.ActiveProfileId);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Library data is invalid JSON.", exception);
		}
		finally
		{
			gate.Release();
		}
	}

	private async ValueTask<LibrarySnapshot?> TryLoadCoreAsync(CancellationToken cancellationToken)
	{
		await RecoverPendingTransactionCoreAsync(cancellationToken).ConfigureAwait(false);
		if (!File.Exists(SnapshotPath))
		{
			if (!_allowLegacyMigration)
			{
				return null;
			}

			Directory.CreateDirectory(_paths.ModsDirectory);
			var legacySnapshot = await TryLoadLegacyAsync(cancellationToken).ConfigureAwait(false);
			if (legacySnapshot is null)
			{
				return null;
			}

			await SaveCoreAsync(legacySnapshot, cancellationToken).ConfigureAwait(false);
			return legacySnapshot;
		}

		try
		{
			var json = await File.ReadAllTextAsync(SnapshotPath, cancellationToken).ConfigureAwait(false);
			var fileSnapshot = JsonSerializer.Deserialize<LibraryFileSnapshot>(json, SerializerOptions);
			if (fileSnapshot is null)
			{
				return null;
			}

			if (fileSnapshot.Version != CurrentVersion)
			{
				throw new InvalidDataException($"Unsupported library data version. Expected {CurrentVersion}.");
			}

			var profileState = await LoadProfilesAsync(Array.Empty<Profile>(), cancellationToken).ConfigureAwait(false);
			return new LibrarySnapshot(fileSnapshot.Version, fileSnapshot.SavedUtc, fileSnapshot.Nodes, profileState.Profiles, profileState.ActiveProfileId);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Library data is invalid JSON.", exception);
		}
	}

	public async ValueTask SaveAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken = default)
	{
		var gate = GetFileGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await SaveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Atomically persists only the node snapshot file. Profile state is kept in
	/// its separate library-local JSON file and is intentionally left byte-for-
	/// byte untouched. This is for narrowly-scoped filesystem reconciliation,
	/// not general library mutations.
	/// </summary>
	public async ValueTask SaveNodesOnlyAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!Directory.Exists(_paths.ModsDirectory))
			throw new DirectoryNotFoundException($"Mod library directory does not exist: {_paths.ModsDirectory}");

		var gate = GetFileGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await RecoverPendingTransactionCoreAsync(cancellationToken).ConfigureAwait(false);
			await SaveNodesOnlyCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Atomically persists only the separate Profiles document. Mod nodes and
	/// library.json remain byte-for-byte untouched. This exists for operations
	/// whose entire responsibility is a reviewed Profile state transition.
	/// </summary>
	public async ValueTask SaveProfilesOnlyAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!Directory.Exists(_paths.ModsDirectory))
			throw new DirectoryNotFoundException($"Mod library directory does not exist: {_paths.ModsDirectory}");

		var gate = GetFileGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await RecoverPendingTransactionCoreAsync(cancellationToken).ConfigureAwait(false);
			await _profileStore.SaveAsync(snapshot.Profiles, snapshot.ActiveProfileId, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	private async ValueTask SaveCoreAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken)
	{
		if (snapshot is null)
		{
			throw new ArgumentNullException(nameof(snapshot));
		}

		Directory.CreateDirectory(_paths.ModsDirectory);
		await RecoverPendingTransactionCoreAsync(cancellationToken).ConfigureAwait(false);

		var transaction = await CreateTransactionJournalAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _profileStore.SaveAsync(snapshot.Profiles, snapshot.ActiveProfileId, cancellationToken).ConfigureAwait(false);
			if (_beforeNodeFileCommit is not null)
				await _beforeNodeFileCommit(cancellationToken).ConfigureAwait(false);
			await SaveNodesOnlyCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);

			// Both documents now point to the same snapshot. Crossing this commit
			// boundary must not observe cancellation; otherwise a later startup
			// would conservatively roll back a write that has already completed.
			await WriteTransactionJournalAsync(transaction with { State = LibraryTransactionState.Committed }, CancellationToken.None).ConfigureAwait(false);
			TryDeleteTransactionJournal();
		}
		catch (Exception primaryFailure)
		{
			try
			{
				await RestoreTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception recoveryFailure)
			{
				throw new IOException(
					"Mod library write failed and rollback could not restore the previous coherent snapshot.",
					new AggregateException(primaryFailure, recoveryFailure));
			}
			throw;
		}
	}

	private async ValueTask SaveNodesOnlyCoreAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken)
	{
		var normalized = snapshot with
		{
			Version = CurrentVersion,
			SavedUtc = snapshot.SavedUtc == default ? DateTimeOffset.UtcNow : snapshot.SavedUtc,
			Profiles = Array.Empty<Profile>(),
		};

		var fileSnapshot = new LibraryFileSnapshot(normalized.Version, normalized.SavedUtc, normalized.Nodes);
		var json = JsonSerializer.Serialize(fileSnapshot, SerializerOptions);
		var tmp = SnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
			File.Move(tmp, SnapshotPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tmp)) File.Delete(tmp);
		}
	}

	private async ValueTask<LibraryWriteTransaction> CreateTransactionJournalAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var transaction = new LibraryWriteTransaction(
			TransactionVersion,
			LibraryTransactionState.Prepared,
			await CaptureTransactionFileAsync(SnapshotPath, cancellationToken).ConfigureAwait(false),
			await CaptureTransactionFileAsync(_paths.ProfilesPath, cancellationToken).ConfigureAwait(false));
		await WriteTransactionJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
		return transaction;
	}

	private async ValueTask RecoverPendingTransactionCoreAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(TransactionJournalPath))
		{
			cancellationToken.ThrowIfCancellationRequested();
			return;
		}

		// A pending transaction is already a durability boundary. Finish recovery
		// even if the caller canceled its current request; leaving the two JSON
		// files half-restored would be more dangerous than honoring cancellation
		// here.
		var transaction = await ReadTransactionJournalAsync(CancellationToken.None).ConfigureAwait(false);
		if (transaction.State == LibraryTransactionState.Prepared)
			await RestoreTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false);
		else
			TryDeleteTransactionJournal();
	}

	private async ValueTask<LibraryWriteTransaction> ReadTransactionJournalAsync(CancellationToken cancellationToken)
	{
		try
		{
			var json = await File.ReadAllTextAsync(TransactionJournalPath, cancellationToken).ConfigureAwait(false);
			var transaction = JsonSerializer.Deserialize<LibraryWriteTransaction>(json, SerializerOptions);
			if (transaction is null
				|| transaction.Version != TransactionVersion
				|| transaction.Library is null
				|| transaction.Profiles is null
				|| transaction.State is not (LibraryTransactionState.Prepared or LibraryTransactionState.Committed))
			{
				throw new InvalidDataException("Mod library transaction journal is invalid.");
			}
			return transaction;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Mod library transaction journal is invalid JSON.", exception);
		}
	}

	private async ValueTask WriteTransactionJournalAsync(LibraryWriteTransaction transaction, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(transaction);
		var json = JsonSerializer.Serialize(transaction, SerializerOptions);
		var temporary = TransactionJournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
			if (File.Exists(TransactionJournalPath)) ClearReadOnly(TransactionJournalPath);
			File.Move(temporary, TransactionJournalPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) TryDeleteFile(temporary);
		}
	}

	private async ValueTask<LibraryTransactionFileState> CaptureTransactionFileAsync(string path, CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return new LibraryTransactionFileState(false, null, null);
		return new LibraryTransactionFileState(
			true,
			File.GetAttributes(path),
			await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
	}

	private async ValueTask RestoreTransactionAsync(LibraryWriteTransaction transaction, CancellationToken cancellationToken)
	{
		await RestoreTransactionFileAsync(SnapshotPath, transaction.Library, cancellationToken).ConfigureAwait(false);
		await RestoreTransactionFileAsync(_paths.ProfilesPath, transaction.Profiles, cancellationToken).ConfigureAwait(false);
		TryDeleteTransactionJournal();
	}

	private static async ValueTask RestoreTransactionFileAsync(
		string path,
		LibraryTransactionFileState backup,
		CancellationToken cancellationToken)
	{
		if (!backup.Existed)
		{
			if (Directory.Exists(path))
				throw new IOException($"Cannot restore library transaction because a directory exists at file path: {path}");
			TryDeleteFile(path);
			return;
		}
		if (backup.Content is null)
			throw new InvalidDataException($"Library transaction backup is missing file content: {path}");
		if (Directory.Exists(path))
			throw new IOException($"Cannot restore library transaction because a directory exists at file path: {path}");

		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".restore";
		try
		{
			await File.WriteAllBytesAsync(temporary, backup.Content, cancellationToken).ConfigureAwait(false);
			if (File.Exists(path)) ClearReadOnly(path);
			File.Move(temporary, path, overwrite: true);
			if (backup.Attributes is { } attributes) File.SetAttributes(path, attributes);
		}
		finally
		{
			if (File.Exists(temporary)) TryDeleteFile(temporary);
		}
	}

	private void TryDeleteTransactionJournal()
		=> TryDeleteFile(TransactionJournalPath);

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return;
			ClearReadOnly(path);
			File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static void ClearReadOnly(string path)
	{
		var attributes = File.GetAttributes(path);
		if ((attributes & FileAttributes.ReadOnly) != 0)
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
	}

	private SemaphoreSlim GetFileGate()
		=> FileGates.GetOrAdd(Path.GetFullPath(SnapshotPath), static _ => new SemaphoreSlim(1, 1));

	private async ValueTask<(IReadOnlyList<Profile> Profiles, ProfileId? ActiveProfileId)> LoadProfilesAsync(IReadOnlyList<Profile> legacyProfiles, CancellationToken cancellationToken)
	{
		var state = await _profileStore.TryLoadAsync(cancellationToken).ConfigureAwait(false);
		if (state.Profiles.Count > 0)
		{
			return state;
		}

		if (legacyProfiles.Count > 0)
		{
			await _profileStore.SaveAsync(legacyProfiles, null, cancellationToken).ConfigureAwait(false);
			return (legacyProfiles, null);
		}

		return (Array.Empty<Profile>(), null);
	}

	private async ValueTask<LibrarySnapshot?> TryLoadLegacyAsync(CancellationToken cancellationToken)
	{
		var legacyPath = Path.Combine(_paths.DataDirectory, "library", "library.json");
		if (!File.Exists(legacyPath))
		{
			return null;
		}

		try
		{
			var json = await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
			var snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(json, SerializerOptions);
			if (snapshot is null)
			{
				return null;
			}

			var profileState = await LoadProfilesAsync(snapshot.Profiles, cancellationToken).ConfigureAwait(false);
			return snapshot with { Version = CurrentVersion, Profiles = profileState.Profiles, ActiveProfileId = profileState.ActiveProfileId };
		}
		catch
		{
			return null;
		}
	}

	private enum LibraryTransactionState
	{
		Prepared,
		Committed,
	}

	private sealed record LibraryWriteTransaction(
		int Version,
		LibraryTransactionState State,
		LibraryTransactionFileState Library,
		LibraryTransactionFileState Profiles);

	private sealed record LibraryTransactionFileState(
		bool Existed,
		FileAttributes? Attributes,
		byte[]? Content);
}
