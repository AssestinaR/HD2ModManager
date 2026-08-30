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
	private readonly StoragePaths _paths;
	private readonly JsonProfileStore _profileStore;

	public JsonModLibraryStore(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_profileStore = new JsonProfileStore(paths);
	}

	private string SnapshotPath => _paths.LibraryPath;

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
		Directory.CreateDirectory(_paths.ModsDirectory);
		if (!File.Exists(SnapshotPath))
		{
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

	private async ValueTask SaveCoreAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken)
	{
		if (snapshot is null)
		{
			throw new ArgumentNullException(nameof(snapshot));
		}

		Directory.CreateDirectory(_paths.ModsDirectory);

		var normalized = snapshot with
		{
			Version = CurrentVersion,
			SavedUtc = snapshot.SavedUtc == default ? DateTimeOffset.UtcNow : snapshot.SavedUtc,
			Profiles = Array.Empty<Profile>(),
		};

		await _profileStore.SaveAsync(snapshot.Profiles, snapshot.ActiveProfileId, cancellationToken).ConfigureAwait(false);

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
}
