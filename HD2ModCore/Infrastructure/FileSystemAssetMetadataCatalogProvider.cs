using System.Globalization;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.ArchiveHashes;

namespace HD2ModCore.Infrastructure;

// Purpose: Loads archive, file and type metadata from cached community hash list files.
public sealed class FileSystemAssetMetadataCatalogProvider : IAssetMetadataCatalogProvider
{
	private readonly StoragePaths _paths;

	public FileSystemAssetMetadataCatalogProvider(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<AssetMetadataCatalog> LoadAsync(CancellationToken cancellationToken = default)
	{
		var archives = File.Exists(_paths.ArchiveHashesPath)
			? await LoadArchivesAsync(_paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
			: new Dictionary<string, ArchiveMetadata>(StringComparer.OrdinalIgnoreCase);
		var files = File.Exists(_paths.FriendlyNamesPath)
			? await LoadFilesAsync(_paths.FriendlyNamesPath, cancellationToken).ConfigureAwait(false)
			: new Dictionary<ulong, FileMetadata>();
		var types = File.Exists(_paths.TypeHashesPath)
			? await LoadTypesAsync(_paths.TypeHashesPath, cancellationToken).ConfigureAwait(false)
			: new Dictionary<ulong, TypeMetadata>();

		return new AssetMetadataCatalog(archives, files, types);
	}

	private static async Task<IReadOnlyDictionary<string, ArchiveMetadata>> LoadArchivesAsync(string path, CancellationToken cancellationToken)
	{
		ArchiveHashesRoot root;
		try
		{
			await using var stream = File.OpenRead(path);
			root = await JsonSerializer.DeserializeAsync<ArchiveHashesRoot>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
				?? new ArchiveHashesRoot();
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
		{
			// This file is optional display metadata. A damaged cache must not
			// make the authoritative installed-GameData index unusable.
			return new Dictionary<string, ArchiveMetadata>(StringComparer.OrdinalIgnoreCase);
		}

		var result = new Dictionary<string, ArchiveMetadata>(StringComparer.OrdinalIgnoreCase);
		var categoryOrder = 0;
		foreach (var (category, map) in root)
		{
			if (string.IsNullOrWhiteSpace(category) || map is null)
			{
				categoryOrder++;
				continue;
			}
			var archiveOrder = 0;
			foreach (var (archiveId, displayName) in map)
			{
				if (string.IsNullOrWhiteSpace(archiveId) || string.IsNullOrWhiteSpace(displayName))
				{
					archiveOrder++;
					continue;
				}

				var normalized = archiveId.Trim().ToLowerInvariant();
				result[normalized] = new ArchiveMetadata(normalized, category.Trim(), displayName.Trim(), categoryOrder, archiveOrder);
				archiveOrder++;
			}
			categoryOrder++;
		}

		return result;
	}

	private static async Task<IReadOnlyDictionary<ulong, FileMetadata>> LoadFilesAsync(string path, CancellationToken cancellationToken)
	{
		var result = new Dictionary<ulong, FileMetadata>();
		await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0)
			{
				continue;
			}

			var separator = trimmed.IndexOfAny([' ', '\t']);
			if (separator <= 0)
			{
				continue;
			}

			var idText = trimmed[..separator];
			var name = trimmed[separator..].Trim();
			if (ulong.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId) && name.Length > 0)
			{
				result[fileId] = new FileMetadata(fileId, name);
			}
		}

		return result;
	}

	private static async Task<IReadOnlyDictionary<ulong, TypeMetadata>> LoadTypesAsync(string path, CancellationToken cancellationToken)
	{
		var result = new Dictionary<ulong, TypeMetadata>();
		await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0)
			{
				continue;
			}

			var separator = trimmed.IndexOfAny([' ', '\t']);
			if (separator <= 0)
			{
				continue;
			}

			var idText = trimmed[..separator];
			var name = trimmed[separator..].Trim();
			if (ulong.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var typeId) && name.Length > 0)
			{
				result[typeId] = new TypeMetadata(typeId, name, ClassifyType(name));
			}
		}

		return result;
	}

	private static AssetTypeCategory ClassifyType(string typeName)
	{
		return typeName.Trim().ToLowerInvariant() switch
		{
			"unit" or "prefab" or "renderable" or "geometry_group" => AssetTypeCategory.Model,
			"material" or "shading_environment" or "shading_environment_mapping" => AssetTypeCategory.Material,
			"texture" or "texture_atlas" => AssetTypeCategory.Texture,
			"wwise_bank" or "wwise_dep" or "wwise_stream" or "wwise_metadata" or "wwise_properties" or "bik" or "bik2" => AssetTypeCategory.Audio,
			"animation" or "state_machine" or "bones" or "ik_skeleton" => AssetTypeCategory.Animation,
			"physics" or "ragdoll_profile" or "cloth" or "havok_ai_properties" or "havok_physics_properties" => AssetTypeCategory.Physics,
			"lua" or "flow" => AssetTypeCategory.Script,
			"font" or "runtime_font" or "mouse_cursor" => AssetTypeCategory.UI,
			"config" or "network_config" or "strings" or "package" or "hash_lookup" or "level" => AssetTypeCategory.Config,
			_ => AssetTypeCategory.Unknown,
		};
	}
}
