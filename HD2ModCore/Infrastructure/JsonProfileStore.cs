using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Json;

namespace HD2ModCore.Infrastructure;

// 作用：使用独立 JSON 文件持久化 Profile，避免配置状态混入可迁移的 mods/library.json。
// Purpose: Persists Profiles in a separate JSON file so user profile state does not mix into portable mods/library.json.
public sealed class JsonProfileStore
{
	private const int CurrentVersion = 2;
	private sealed record ProfileFileSnapshot(int Version, ProfileId? ActiveProfileId, IReadOnlyList<Profile> Profiles);

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

	private readonly StoragePaths _paths;

	public JsonProfileStore(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<(IReadOnlyList<Profile> Profiles, ProfileId? ActiveProfileId)> TryLoadAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.ProfilesPath))
		{
			await TryMigrateLegacyProfilesOnceAsync(cancellationToken).ConfigureAwait(false);
			if (!File.Exists(_paths.ProfilesPath)) return (Array.Empty<Profile>(), null);
		}

		try
		{
			var json = await File.ReadAllTextAsync(_paths.ProfilesPath, cancellationToken).ConfigureAwait(false);
			if (TryDeserializeCurrent(json, out var snapshot))
			{
				return (snapshot.Profiles ?? Array.Empty<Profile>(), snapshot.ActiveProfileId);
			}

			if (TryDeserializeLegacyList(json, out var legacyProfiles))
			{
				await SaveAsync(legacyProfiles, null, cancellationToken).ConfigureAwait(false);
				return (legacyProfiles, null);
			}

			await BackupUnreadableProfileFileAsync(cancellationToken).ConfigureAwait(false);
			await SaveAsync(Array.Empty<Profile>(), null, cancellationToken).ConfigureAwait(false);
			return (Array.Empty<Profile>(), null);
		}
		catch (IOException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			throw;
		}
	}

	public async ValueTask SaveAsync(IReadOnlyList<Profile> profiles, ProfileId? activeProfileId, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_paths.ModsDirectory);
		var snapshot = new ProfileFileSnapshot(CurrentVersion, activeProfileId, profiles ?? Array.Empty<Profile>());
		var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
		var tmp = _paths.ProfilesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
			File.Move(tmp, _paths.ProfilesPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tmp)) File.Delete(tmp);
		}
	}

	// Strict read-only counterpart for headless inspection. Unlike TryLoadAsync it
	// never migrates, repairs, backs up, or creates profile files.
	public async ValueTask<(IReadOnlyList<Profile> Profiles, ProfileId? ActiveProfileId)> TryLoadReadOnlyAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.ProfilesPath)) return (Array.Empty<Profile>(), null);

		var json = await File.ReadAllTextAsync(_paths.ProfilesPath, cancellationToken).ConfigureAwait(false);
		if (TryDeserializeCurrent(json, out var snapshot))
			return (snapshot.Profiles ?? Array.Empty<Profile>(), snapshot.ActiveProfileId);
		if (TryDeserializeLegacyList(json, out var legacyProfiles))
			return (legacyProfiles, null);

		throw new InvalidDataException("Profile data is invalid JSON.");
	}

	private async ValueTask TryMigrateLegacyProfilesOnceAsync(CancellationToken cancellationToken)
	{
		if (File.Exists(_paths.LegacyProfileMigrationMarkerPath)
			|| !File.Exists(_paths.LegacyProfilesPath)
			|| File.Exists(_paths.ProfilesPath)) return;

		Directory.CreateDirectory(_paths.ModsDirectory);
		File.Copy(_paths.LegacyProfilesPath, _paths.ProfilesPath, overwrite: false);
		Directory.CreateDirectory(_paths.DataDirectory);
		await File.WriteAllTextAsync(_paths.LegacyProfileMigrationMarkerPath, "migrated", cancellationToken).ConfigureAwait(false);
	}

	private static bool TryDeserializeCurrent(string json, out ProfileFileSnapshot snapshot)
	{
		try
		{
			var parsed = JsonSerializer.Deserialize<ProfileFileSnapshot>(json, SerializerOptions);
			if (parsed is not null && parsed.Version == CurrentVersion)
			{
				snapshot = parsed;
				return true;
			}
		}
		catch (JsonException)
		{
			// The file may be the pre-v2 profile list format.
		}

		snapshot = default!;
		return false;
	}

	private static bool TryDeserializeLegacyList(string json, out IReadOnlyList<Profile> profiles)
	{
		try
		{
			var parsed = JsonSerializer.Deserialize<List<Profile>>(json, SerializerOptions);
			if (parsed is not null)
			{
				profiles = parsed;
				return true;
			}
		}
		catch (JsonException)
		{
			// Not a valid v1 list either.
		}

		profiles = Array.Empty<Profile>();
		return false;
	}

	private async ValueTask BackupUnreadableProfileFileAsync(CancellationToken cancellationToken)
	{
		var backupPath = _paths.ProfilesPath + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
		await using var source = new FileStream(_paths.ProfilesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using var destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
		await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
	}
}
