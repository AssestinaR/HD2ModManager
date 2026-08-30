using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.ArchiveHashes;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// 作用：使用 SQLite 构建并查询持久化的 (TypeID, FileID) -> ArchiveId 反向索引，用于推导替换目标与冲突分析。
// Purpose: Builds and queries a persisted (TypeID, FileID) -> ArchiveId reverse index using SQLite.
public sealed class AssetArchiveIndexService : IAssetArchiveIndexService
{
	private readonly StoragePaths _paths;
	private readonly IGameDataArchiveIndexer _archiveIndexer;
	private readonly UnitMeshPartClassifier _unitPartClassifier;

	public AssetArchiveIndexService(StoragePaths paths, IGameDataArchiveIndexer? archiveIndexer = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_archiveIndexer = archiveIndexer ?? new GameDataArchiveIndexer();
		_unitPartClassifier = new UnitMeshPartClassifier();
	}

	public ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(File.Exists(_paths.DbPath));

	public async ValueTask<GameDataIndexStatus> GetIndexStatusAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		var normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(normalizedGameDataDirectory))
		{
			throw new DirectoryNotFoundException($"GameData directory does not exist: {normalizedGameDataDirectory}");
		}

		try
		{
			_ = JsonSerializer.Deserialize<ArchiveHashesRoot>(archiveHashesJson) ?? new ArchiveHashesRoot();
		}
		catch (JsonException)
		{
			return new GameDataIndexStatus(GameDataIndexState.Invalid, await GetFingerprintAsync(cancellationToken).ConfigureAwait(false), Path.GetFullPath(gameDataDirectory), string.Empty);
		}

		var currentFingerprint = await ComputeSourceFingerprintAsync(
			normalizedGameDataDirectory,
			archiveHashesJson,
			cancellationToken).ConfigureAwait(false);
		var stored = await GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		if (stored is null)
		{
			// A missing database still has a concrete GameData + archive metadata
			// identity.  Exposing it lets plan-gated callers safely build an index
			// from scratch and detect drift before apply.
			return new GameDataIndexStatus(GameDataIndexState.Missing, null, Path.GetFullPath(gameDataDirectory), currentFingerprint);
		}
		await using var versionConnection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await versionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
		var state = string.Equals(stored.SourceFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase)
			? GameDataIndexState.Current
			: GameDataIndexState.Stale;

		return new GameDataIndexStatus(state, stored, Path.GetFullPath(gameDataDirectory), currentFingerprint);
	}

	public async ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath))
		{
			return null;
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var builtUtcText = await SqliteSchema.GetMetaAsync(connection, "built_utc", cancellationToken).ConfigureAwait(false);
		if (!DateTimeOffset.TryParse(builtUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var builtUtc))
		{
			return null;
		}

		return new GameDataIndexFingerprint(
			await SqliteSchema.GetMetaAsync(connection, "game_data_directory", cancellationToken).ConfigureAwait(false) ?? string.Empty,
			builtUtc,
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "archives_total", cancellationToken).ConfigureAwait(false)),
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "archives_indexed", cancellationToken).ConfigureAwait(false)),
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "asset_keys_total", cancellationToken).ConfigureAwait(false)),
			await SqliteSchema.GetMetaAsync(connection, "source_fingerprint", cancellationToken).ConfigureAwait(false) ?? string.Empty);
	}

	public async ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath))
		{
			return Array.Empty<GameDataArchiveSummary>();
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT a.archive_id, a.display_name, a.category, COUNT(e.entry_index) AS entry_count,
	   CASE WHEN EXISTS (SELECT 1 FROM archive_issues i WHERE i.archive_id = a.archive_id)
			THEN '存在问题' ELSE '已索引' END AS status
FROM archives a
LEFT JOIN archive_entries e ON e.archive_id = a.archive_id
GROUP BY a.archive_id, a.display_name, a.category
ORDER BY a.archive_id;";

		var result = new List<GameDataArchiveSummary>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add(new GameDataArchiveSummary(
				reader.GetString(0),
				reader.GetString(1),
				reader.GetString(2),
				Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
				reader.GetString(4)));
		}

		return result;
	}

	public async ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(string packageName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
		if (!File.Exists(_paths.DbPath)) return null;

		var catalog = await new FileSystemAssetMetadataCatalogProvider(_paths).LoadAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using var summaryCommand = connection.CreateCommand();
		summaryCommand.CommandText = @"
SELECT a.archive_id, a.display_name, a.category, COUNT(e.entry_index),
       CASE WHEN EXISTS (SELECT 1 FROM archive_issues i WHERE i.archive_id = a.archive_id)
            THEN '存在问题' ELSE '已索引' END
FROM archives a
LEFT JOIN archive_entries e ON e.archive_id = a.archive_id
WHERE a.archive_id = $package
GROUP BY a.archive_id, a.display_name, a.category;";
		summaryCommand.Parameters.AddWithValue("$package", packageName);
		await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await summaryReader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
		var summary = new GameDataArchiveSummary(
			summaryReader.GetString(0), summaryReader.GetString(1), summaryReader.GetString(2),
			Convert.ToInt32(summaryReader.GetInt64(3), CultureInfo.InvariantCulture), summaryReader.GetString(4));
		await summaryReader.DisposeAsync().ConfigureAwait(false);

		var keys = new List<CoreAssetKey>();
		await using var entriesCommand = connection.CreateCommand();
		entriesCommand.CommandText = @"
SELECT type_id, file_id
FROM archive_entries
WHERE archive_id = $package
ORDER BY type_id, file_id;";
		entriesCommand.Parameters.AddWithValue("$package", packageName);
		await using var entriesReader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await entriesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			keys.Add(new CoreAssetKey(unchecked((ulong)entriesReader.GetInt64(0)), unchecked((ulong)entriesReader.GetInt64(1))));
		}
		await entriesReader.DisposeAsync().ConfigureAwait(false);

		var unitKeys = keys.Where(key => key.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId).ToHashSet();
		var partsByUnit = await GetUnitPartFactsAsync(unitKeys, cancellationToken).ConfigureAwait(false);
		var assets = new List<GameDataArchiveAssetEntry>(keys.Count);
		foreach (var key in keys)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var shared = await GetSharedArchivesAsync(connection, summary.PackageName, key.TypeId, key.FileId, cancellationToken).ConfigureAwait(false);
			var typeName = catalog.Types.TryGetValue(key.TypeId, out var type) ? type.Name : $"unknown ({key.TypeId:x16})";
			var friendlyName = catalog.Files.TryGetValue(key.FileId, out var file) ? file.FriendlyName : "—";
			assets.Add(new GameDataArchiveAssetEntry(
				key,
				typeName,
				friendlyName,
				partsByUnit.TryGetValue(key, out var parts) ? DescribeParts(parts) : "—",
				shared.Select(x => x.PackageName).ToArray(),
				shared.Select(x => x.DisplayName).ToArray()));
		}

		var issues = new List<CoreIssue>();
		await using var issuesCommand = connection.CreateCommand();
		issuesCommand.CommandText = "SELECT code, message FROM archive_issues WHERE archive_id = $package ORDER BY issue_id";
		issuesCommand.Parameters.AddWithValue("$package", packageName);
		await using var issuesReader = await issuesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await issuesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Warning, issuesReader.GetString(0), issuesReader.GetString(1)));
		}

		return new GameDataArchiveDetails(summary, assets, issues);
	}

	public async ValueTask<IReadOnlyDictionary<CoreAssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(IReadOnlySet<CoreAssetKey> unitAssetKeys, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitAssetKeys);
		if (unitAssetKeys.Count == 0 || !File.Exists(_paths.DbPath)) return new Dictionary<CoreAssetKey, IReadOnlyList<GameDataUnitPartFact>>();
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,reason
FROM game_data_unit_parts

WHERE unit_type_id=$unitType AND unit_file_id IN (SELECT value FROM json_each($fileIds))
ORDER BY unit_file_id,confidence DESC,mesh_info_index;";
		command.Parameters.AddWithValue("$unitType", unchecked((long)HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId));
		command.Parameters.AddWithValue("$fileIds", JsonSerializer.Serialize(unitAssetKeys.Where(key => key.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId).Select(key => unchecked((long)key.FileId))));
		var result = new Dictionary<CoreAssetKey, List<GameDataUnitPartFact>>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var fact = new GameDataUnitPartFact(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetInt32(3),
				unchecked((uint)reader.GetInt64(4)),
				(UnitMeshPartKind)reader.GetInt32(5),
				(UnitMeshPartLayer)reader.GetInt32(6),
				(UnitMeshBodyVariant)reader.GetInt32(7),
				reader.GetString(8),
				reader.GetInt32(10),
				reader.GetInt32(11) != 0,
				reader.GetInt32(12) != 0,
				reader.GetString(13))
			{
				PieceType = reader.GetString(9)
			};
			if (!result.TryGetValue(fact.UnitAssetKey, out var parts))
			{
				parts = new List<GameDataUnitPartFact>();
				result[fact.UnitAssetKey] = parts;
			}
			parts.Add(fact);
		}
		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameDataUnitPartFact>)pair.Value);
	}

	public async ValueTask BuildOrRebuildAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		IProgress<IndexBuildProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		var normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(normalizedGameDataDirectory))
		{
			throw new DirectoryNotFoundException($"GameData directory does not exist: {normalizedGameDataDirectory}");
		}

		Directory.CreateDirectory(_paths.IndexDirectory);

		ArchiveHashesRoot root;
		try
		{
			root = JsonSerializer.Deserialize<ArchiveHashesRoot>(archiveHashesJson) ?? new ArchiveHashesRoot();
		}
		catch (JsonException ex)
		{
			throw new FormatException("Invalid archivehashes.json format.", ex);
		}


		SqliteConnection.ClearAllPools();
		NormalizeExistingIndexFileAttributes();

		var archives = FlattenArchives(root);
		var metadataByPackage = BuildMetadataByPackage(archives);
		var sourceFingerprint = await ComputeSourceFingerprintAsync(
			normalizedGameDataDirectory,
			archiveHashesJson,
			cancellationToken).ConfigureAwait(false);
		var archiveProgress = new Progress<GameDataArchiveIndexProgress>(item =>
			progress?.Report(new IndexBuildProgress(item.Current, Math.Max(item.Total, 1), $"{item.Stage}：{item.Item}")));
		var facts = await _archiveIndexer.BuildAsync(
			new GameDataArchiveInput(normalizedGameDataDirectory, metadataByPackage.Keys.ToArray(), metadataByPackage, IncludeStreamLayouts: false),
			archiveProgress,
			cancellationToken).ConfigureAwait(false);
		if (facts.Archives.Count == 0)
		{
			throw new InvalidDataException($"No GameData archives were discovered in: {normalizedGameDataDirectory}");
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);

		await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await PreserveUnitPartFactsAsync(connection, tx, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.ClearIndexDataAsync(connection, cancellationToken).ConfigureAwait(false);

		var total = facts.Archives.Count;
		var current = 0;
		var indexed = facts.Archives.Count(a => a.IsIndexed);

		var df = new Dictionary<CoreAssetKey, int>();
		var assetArchives = new Dictionary<CoreAssetKey, HashSet<string>>();
		await using var archiveCommand = CreateArchiveInsertCommand(connection);
		await using var issueCommand = CreateArchiveIssueInsertCommand(connection);
		await using var entryCommand = CreateArchiveEntryInsertCommand(connection);

		foreach (var archive in facts.Archives)
		{
			cancellationToken.ThrowIfCancellationRequested();
			current++;
			progress?.Report(new IndexBuildProgress(current, total, archive.PackageName));

			await InsertArchiveAsync(archiveCommand, archive, cancellationToken).ConfigureAwait(false);
			foreach (var issue in archive.Issues)
				await InsertArchiveIssueAsync(issueCommand, archive.PackageName, issue, cancellationToken).ConfigureAwait(false);

			foreach (var entry in archive.Entries)
			{
				await InsertArchiveEntryAsync(entryCommand, entry, cancellationToken).ConfigureAwait(false);
				var key = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
				if (df.TryGetValue(key, out var count))
				{
					df[key] = count + 1;
				}
				else
				{
					df[key] = 1;
				}

				if (!assetArchives.TryGetValue(key, out var set))
				{
					set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					assetArchives[key] = set;
				}
				set.Add(archive.PackageName);
			}
		}

		foreach (var (key, count) in df)
		{
			await InsertAssetAsync(connection, key, count, cancellationToken).ConfigureAwait(false);
		}

		foreach (var (key, archivesSet) in assetArchives)
		{
			foreach (var archiveId in archivesSet)
			{
				await InsertAssetArchiveAsync(connection, key, archiveId, cancellationToken).ConfigureAwait(false);
			}
		}

		await RestoreUnitPartFactsAsync(connection, tx, cancellationToken).ConfigureAwait(false);

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		await SqliteSchema.SetMetaAsync(connection, "built_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "archives_total", total.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "archives_indexed", indexed.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "asset_keys_total", df.Count.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_directory", normalizedGameDataDirectory, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "source_fingerprint", sourceFingerprint, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "parser_version", facts.ParserVersion, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "index_schema_version", facts.SchemaVersion, cancellationToken).ConfigureAwait(false);
		// Readers use pooled shared-cache connections. Purge their pre-rebuild pages so
		// in-process catalog queries observe the committed index without requiring restart.
		SqliteConnection.ClearAllPools();

		progress?.Report(new IndexBuildProgress(total, Math.Max(total, 1), "基础资产索引已完成；跨护甲高级数据按需建立。"));
	}

	public async ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> FindStreamLayoutsAsync(
		IReadOnlyList<HD2ModCore.Domain.GameDataStreamComponentFact> components,
		uint vertexStride,
		bool requireSkinned = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(components);
		if (!File.Exists(_paths.DbPath)) return Array.Empty<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		var signature = BuildLayoutSignature(components);
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned
FROM game_data_stream_layouts
WHERE layout_signature=$signature AND vertex_stride=$stride AND ($skinned=0 OR is_skinned=1)
ORDER BY archive_id,unit_file_id,stream_index;";
		command.Parameters.AddWithValue("$signature", signature);
		command.Parameters.AddWithValue("$stride", vertexStride);
		command.Parameters.AddWithValue("$skinned", requireSkinned ? 1 : 0);
		var result = new List<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var parsed = JsonSerializer.Deserialize<List<HD2ModCore.Domain.GameDataStreamComponentFact>>(reader.GetString(7)) ?? new List<HD2ModCore.Domain.GameDataStreamComponentFact>();
			result.Add(new HD2ModCore.Domain.GameDataStreamLayoutFact(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetInt32(3),
				unchecked((ulong)reader.GetInt64(4)),
				unchecked((uint)reader.GetInt64(5)),
				unchecked((uint)reader.GetInt64(6)),
				parsed,
				reader.GetString(8),
				reader.GetInt32(9) != 0));
		}
		return result;
	}

	public async ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> GetStreamLayoutsAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath)) return Array.Empty<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned
FROM game_data_stream_layouts
ORDER BY archive_id,unit_file_id,stream_index;";
		var result = new List<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var components = JsonSerializer.Deserialize<List<HD2ModCore.Domain.GameDataStreamComponentFact>>(reader.GetString(7)) ?? new List<HD2ModCore.Domain.GameDataStreamComponentFact>();
			result.Add(new HD2ModCore.Domain.GameDataStreamLayoutFact(reader.GetString(0), new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))), reader.GetInt32(3), unchecked((ulong)reader.GetInt64(4)), unchecked((uint)reader.GetInt64(5)), unchecked((uint)reader.GetInt64(6)), components, reader.GetString(8), reader.GetInt32(9) != 0));
		}
		return result;
	}

	private async ValueTask<IReadOnlyList<GameDataUnitPartFact>> AnalyzeEquipmentUnitPartsAsync(
		string gameDataDirectory,
		IReadOnlyList<GameDataArchiveFact> archives,
		IReadOnlySet<string> equipmentArchiveIds,
		IReadOnlyDictionary<uint, string> globalBoneNames,
		IProgress<IndexBuildProgress>? progress,
		CancellationToken cancellationToken)
	{
		if (equipmentArchiveIds.Count == 0)
		{
			return Array.Empty<GameDataUnitPartFact>();
		}

		var equipmentArchives = archives.Where(archive => archive.IsIndexed && equipmentArchiveIds.Contains(archive.PackageName)).ToArray();
		var resolver = new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(gameDataDirectory);
		var reader = new GameDataUnitMeshReader(resolver);
		var result = new List<GameDataUnitPartFact>();
		var total = equipmentArchives.Sum(archive => archive.Entries.Count(entry => entry.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId));
		var current = 0;
		foreach (var archive in equipmentArchives)
		{
			foreach (var entry in archive.Entries.Where(entry => entry.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId))
			{
				cancellationToken.ThrowIfCancellationRequested();
				current++;
				progress?.Report(new IndexBuildProgress(current, Math.Max(total, 1), $"分析装备 Unit：{archive.PackageName}"));
				var unitKey = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
				try
				{
					var unit = await reader.ReadAsync(archive.PackageName, entry.AssetKey, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
					result.AddRange(_unitPartClassifier.Classify(entry.AssetKey, unit.Model, globalBoneNames)
						.Select(part => new GameDataUnitPartFact(archive.PackageName, unitKey, part.MeshInfoIndex, part.MeshId, part.PartKind, part.Layer, part.BodyVariant, part.SemanticName, part.Confidence, part.IsVisualMesh, part.IsLod, part.Reason)));
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
				{
					// Preserve the completed archive index even when an individual legacy/unsupported Unit cannot be parsed.
				}
			}
		}

		return result;
	}

	private static string BuildLayoutSignature(IEnumerable<HD2ModCore.Domain.GameDataStreamComponentFact> components)
		=> string.Join(";", components.Select(component => $"{component.Type}:{component.Format}:{component.Index}:{component.Unknown:x16}"));

	public async ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(
		IReadOnlySet<CoreAssetKey> assetKeys,
		CancellationToken cancellationToken = default)
	{
		if (assetKeys is null)
		{
			throw new ArgumentNullException(nameof(assetKeys));
		}

		if (assetKeys.Count == 0 || !File.Exists(_paths.DbPath))
		{
			return Array.Empty<AssetArchiveMatch>();
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var matches = new List<AssetArchiveMatch>();
		foreach (var key in assetKeys.OrderBy(x => x.TypeId).ThenBy(x => x.FileId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			matches.Add(new AssetArchiveMatch(key, await GetArchiveMetadataForAssetAsync(connection, key, cancellationToken).ConfigureAwait(false)));
		}

		return matches;
	}

	public async ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(
		IReadOnlySet<CoreAssetKey> assetKeys,
		IndexFilterSettings filterSettings,
		CancellationToken cancellationToken = default)
	{
		if (assetKeys is null)
		{
			throw new ArgumentNullException(nameof(assetKeys));
		}

		if (assetKeys.Count == 0 || !File.Exists(_paths.DbPath))
		{
			return new Dictionary<string, int>();
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var totalArchivesText = await SqliteSchema.GetMetaAsync(connection, "archives_total", cancellationToken).ConfigureAwait(false);
		_ = int.TryParse(totalArchivesText, out var totalArchives);
		if (totalArchives <= 0)
		{
			totalArchives = 1;
		}

		var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var key in assetKeys)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var df = await GetDfAsync(connection, key, cancellationToken).ConfigureAwait(false);
			if (IsFiltered(df, totalArchives, filterSettings))
			{
				continue;
			}

			await foreach (var archiveId in GetArchivesForAssetAsync(connection, key, cancellationToken).ConfigureAwait(false))
			{
				votes.TryGetValue(archiveId, out var count);
				votes[archiveId] = count + 1;
			}
		}

		return votes;
	}

	private static bool IsFiltered(int df, int totalArchives, IndexFilterSettings settings)
	{
		if (df <= 0)
		{
			return true;
		}

		return settings.Mode switch
		{
			IndexFilterMode.AbsoluteCount => settings.AbsoluteThreshold is int t && df > t,
			IndexFilterMode.Percentage => settings.PercentageThreshold is double p && p > 0 && (df / (double)totalArchives) > p,
			_ => false,
		};
	}

	private void NormalizeExistingIndexFileAttributes()
	{
		foreach (var path in new[] { _paths.DbPath, _paths.DbPath + "-wal", _paths.DbPath + "-shm" })
		{
			if (!File.Exists(path))
			{
				continue;
			}

			File.SetAttributes(path, FileAttributes.Normal);
		}
	}

	private static List<(string ArchiveId, string Category, string DisplayName)> FlattenArchives(ArchiveHashesRoot root)
	{
		var archives = new List<(string ArchiveId, string Category, string DisplayName)>();
		foreach (var (category, map) in root)
		{
			foreach (var (archiveId, displayName) in map)
			{
				if (string.IsNullOrWhiteSpace(archiveId))
				{
					continue;
				}

				archives.Add((archiveId.Trim().ToLowerInvariant(), category, displayName));
			}
		}

		return archives;
	}

	private static IReadOnlySet<string> GetEquipmentArchiveIds(ArchiveHashesRoot root)
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (category, archives) in root)
		{
			if (!category.Contains("armor", StringComparison.OrdinalIgnoreCase)
				&& !category.Contains("helmet", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (var archiveId in archives.Keys)
			{
				result.Add(archiveId.Trim().ToLowerInvariant());
			}
		}
		return result;
	}

	private static IReadOnlyDictionary<uint, string> LoadGlobalBoneNames(string path)
	{
		if (!File.Exists(path))
		{
			return new Dictionary<uint, string>();
		}

		var result = new Dictionary<uint, string>();
		foreach (var line in File.ReadLines(path))
		{
			var fields = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (fields.Length == 2 && uint.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var meshId))
			{
				result.TryAdd(meshId, fields[1]);
			}
		}
		return result;
	}

	private static async ValueTask<string> ComputeSourceFingerprintAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendFingerprintValue(hash, Path.GetFullPath(gameDataDirectory));
		AppendFingerprintValue(hash, SqliteSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
		AppendFingerprintValue(hash, "package-toc-v1");
		AppendFingerprintValue(hash, archiveHashesJson);

		foreach (var path in Directory.EnumerateFiles(gameDataDirectory, "*", SearchOption.TopDirectoryOnly)
			.Where(IsGameDataSourceFile)
			.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new FileInfo(path);
			AppendFingerprintValue(hash, info.Name);
			AppendFingerprintValue(hash, info.Length.ToString(CultureInfo.InvariantCulture));
			AppendFingerprintValue(hash, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
			await Task.Yield();
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static bool IsGameDataSourceFile(string path)
	{
		var name = Path.GetFileName(path);
		if (string.Equals(name, "activation-state.json", StringComparison.OrdinalIgnoreCase)) return false;
		if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;
		return !new PatchFileNameParser().TryParse(name, out _);
	}

	private static void AppendFingerprintValue(IncrementalHash hash, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		hash.AppendData(bytes);
		hash.AppendData(new byte[] { 0 });
	}

	private static int ParseInt(string? value)
		=> int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

	private static IReadOnlyDictionary<string, GameDataArchiveMetadata> BuildMetadataByPackage(
		IEnumerable<(string ArchiveId, string Category, string DisplayName)> archives)
	{
		var result = new Dictionary<string, GameDataArchiveMetadata>(StringComparer.OrdinalIgnoreCase);
		foreach (var archive in archives)
		{
			if (!result.TryGetValue(archive.ArchiveId, out var existing))
			{
				result[archive.ArchiveId] = new GameDataArchiveMetadata(
					archive.ArchiveId,
					archive.DisplayName,
					archive.Category);
				continue;
			}

			var categories = string.Join(", ", new[] { existing.Category, archive.Category }
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
			result[archive.ArchiveId] = existing with
			{
				Category = categories,
				DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? archive.DisplayName : existing.DisplayName
			};
		}

		return result;
	}

	private static async Task PreserveUnitPartFactsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = @"
DROP TABLE IF EXISTS temp.preserved_game_data_unit_parts;
CREATE TEMP TABLE preserved_game_data_unit_parts AS
SELECT archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,reason
FROM game_data_unit_parts;";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task RestoreUnitPartFactsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = @"
INSERT OR IGNORE INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,reason)
SELECT p.archive_id,p.unit_type_id,p.unit_file_id,p.mesh_info_index,p.mesh_id,p.part_kind,p.part_layer,p.body_variant,p.semantic_name,p.piece_type,p.confidence,p.is_visual,p.is_lod,p.reason
FROM preserved_game_data_unit_parts p
JOIN archives a ON a.archive_id=p.archive_id
JOIN archive_entries e ON e.archive_id=p.archive_id AND e.type_id=p.unit_type_id AND e.file_id=p.unit_file_id
WHERE lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%';
DROP TABLE preserved_game_data_unit_parts;";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO archives(archive_id,category,display_name,archive_hex,uses_slim_entry_offset,status) VALUES($a,$c,$n,$h,$s,$status)";
		cmd.Parameters.Add("$a", SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Text);
		cmd.Parameters.Add("$n", SqliteType.Text);
		cmd.Parameters.Add("$h", SqliteType.Text);
		cmd.Parameters.Add("$s", SqliteType.Integer);
		cmd.Parameters.Add("$status", SqliteType.Text);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertArchiveAsync(SqliteCommand cmd, GameDataArchiveFact archive, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archive.PackageName;
		cmd.Parameters["$c"].Value = archive.Category ?? string.Empty;
		cmd.Parameters["$n"].Value = archive.DisplayName ?? archive.PackageName;
		cmd.Parameters["$h"].Value = (object?)archive.ArchiveHex ?? DBNull.Value;
		cmd.Parameters["$s"].Value = archive.UsesSlimEntryOffset ? 1 : 0;
		cmd.Parameters["$status"].Value = archive.IsIndexed ? "Indexed" : "Issues";
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveEntryInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = @"INSERT OR REPLACE INTO archive_entries
(archive_id,entry_index,type_id,file_id,df,toc_data_offset,stream_offset,gpu_resource_offset,toc_data_size,stream_size,gpu_resource_size,unknown1,unknown2,unknown3,unknown4)
VALUES($a,$i,$t,$f,1,$td,$so,$go,$ts,$ss,$gs,$u1,$u2,$u3,$u4)";
		foreach (var name in new[] { "$a" }) cmd.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$i", "$t", "$f", "$td", "$so", "$go", "$ts", "$ss", "$gs", "$u1", "$u2", "$u3", "$u4" }) cmd.Parameters.Add(name, SqliteType.Integer);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertArchiveEntryAsync(SqliteCommand cmd, GameDataArchiveEntryFact entry, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = entry.PackageName;
		cmd.Parameters["$i"].Value = entry.EntryIndex;
		cmd.Parameters["$t"].Value = unchecked((long)entry.AssetKey.TypeId);
		cmd.Parameters["$f"].Value = unchecked((long)entry.AssetKey.FileId);
		cmd.Parameters["$td"].Value = unchecked((long)entry.TocDataOffset);
		cmd.Parameters["$so"].Value = unchecked((long)entry.StreamOffset);
		cmd.Parameters["$go"].Value = unchecked((long)entry.GpuResourceOffset);
		cmd.Parameters["$ts"].Value = entry.TocDataSize;
		cmd.Parameters["$ss"].Value = entry.StreamSize;
		cmd.Parameters["$gs"].Value = entry.GpuResourceSize;
		cmd.Parameters["$u1"].Value = unchecked((long)entry.Unknown1);
		cmd.Parameters["$u2"].Value = unchecked((long)entry.Unknown2);
		cmd.Parameters["$u3"].Value = entry.Unknown3;
		cmd.Parameters["$u4"].Value = entry.Unknown4;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveIssueInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT INTO archive_issues(archive_id,code,message) VALUES($a,$c,$m)";
		cmd.Parameters.Add("$a", SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Text);
		cmd.Parameters.Add("$m", SqliteType.Text);
		cmd.Prepare();
		return cmd;
	}

	private static SqliteCommand CreateUnitPartInsertCommand(SqliteConnection connection)
	{
		var command = connection.CreateCommand();
		command.CommandText = @"INSERT INTO game_data_unit_parts
(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,reason)
VALUES($archive,$type,$file,$mesh,$meshId,$kind,$layer,$variant,$name,$piece,$confidence,$visual,$lod,$reason)";
		foreach (var name in new[] { "$archive", "$name", "$piece", "$reason" }) command.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$type", "$file", "$mesh", "$meshId", "$kind", "$layer", "$variant", "$confidence", "$visual", "$lod" }) command.Parameters.Add(name, SqliteType.Integer);
		command.Prepare();
		return command;
	}

	private static async Task InsertUnitPartAsync(SqliteCommand command, GameDataUnitPartFact part, CancellationToken cancellationToken)
	{
		command.Parameters["$archive"].Value = part.ArchiveId;
		command.Parameters["$type"].Value = unchecked((long)part.UnitAssetKey.TypeId);
		command.Parameters["$file"].Value = unchecked((long)part.UnitAssetKey.FileId);
		command.Parameters["$mesh"].Value = part.MeshInfoIndex;
		command.Parameters["$meshId"].Value = part.MeshId;
		command.Parameters["$kind"].Value = (int)part.PartKind;
		command.Parameters["$layer"].Value = (int)part.Layer;
		command.Parameters["$variant"].Value = (int)part.BodyVariant;
		command.Parameters["$name"].Value = part.SemanticName;
		command.Parameters["$piece"].Value = part.PieceType;
		command.Parameters["$confidence"].Value = part.Confidence;
		command.Parameters["$visual"].Value = part.IsVisualMesh ? 1 : 0;
		command.Parameters["$lod"].Value = part.IsLod ? 1 : 0;
		command.Parameters["$reason"].Value = part.Reason;
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string DescribeParts(IEnumerable<GameDataUnitPartFact> parts)
	{
		var summaries = parts.Where(part => part.IsVisualMesh && !part.IsLod && part.PartKind != UnitMeshPartKind.Unknown)
			.Select(part => $"{PartName(part.PartKind)}－{LayerName(part.Layer)}－{BodyVariantName(part.BodyVariant)}")
			.Distinct(StringComparer.Ordinal).ToArray();
		return summaries.Length == 0 ? "—" : string.Join("，", summaries);
	}

	private static string PartName(UnitMeshPartKind kind) => kind switch
	{
		UnitMeshPartKind.Head => "头部", UnitMeshPartKind.Torso => "胸口", UnitMeshPartKind.Pelvis => "胯部",
		UnitMeshPartKind.LeftArm => "左臂", UnitMeshPartKind.RightArm => "右臂", UnitMeshPartKind.LeftLeg => "左腿", UnitMeshPartKind.RightLeg => "右腿",
		UnitMeshPartKind.LeftShoulder => "左肩甲", UnitMeshPartKind.RightShoulder => "右肩甲", UnitMeshPartKind.Accessory => "附件", _ => "未知"
	};

	private static string LayerName(UnitMeshPartLayer layer) => layer switch
	{
		UnitMeshPartLayer.Undergarment => "内部", UnitMeshPartLayer.Armor => "护甲", UnitMeshPartLayer.Accessory => "附件",
		UnitMeshPartLayer.Culling => "隐藏壳", UnitMeshPartLayer.Static => "静态", _ => "未分类"
	};

	private static string BodyVariantName(UnitMeshBodyVariant variant) => variant switch
	{
		UnitMeshBodyVariant.Slim => "纤细",
		UnitMeshBodyVariant.Stocky => "健壮",
		UnitMeshBodyVariant.Any => "通用",
		UnitMeshBodyVariant.Other => "其他体型",
		_ => "体型未知"
	};

	private static async Task InsertArchiveIssueAsync(SqliteCommand cmd, string archiveId, PatchAnalysisIssue issue, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archiveId;
		cmd.Parameters["$c"].Value = issue.Code;
		cmd.Parameters["$m"].Value = issue.Message;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task InsertAssetAsync(SqliteConnection connection, CoreAssetKey key, int df, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO assets(type_id,file_id,df) VALUES($t,$f,$d)";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));
		cmd.Parameters.AddWithValue("$d", df);
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task InsertAssetArchiveAsync(SqliteConnection connection, CoreAssetKey key, string archiveId, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR IGNORE INTO asset_archives(type_id,file_id,archive_id) VALUES($t,$f,$a)";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));
		cmd.Parameters.AddWithValue("$a", archiveId);
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<int> GetDfAsync(SqliteConnection connection, CoreAssetKey key, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT df FROM assets WHERE type_id=$t AND file_id=$f";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return obj is null ? 0 : Convert.ToInt32(obj);
	}

	private static async Task<IReadOnlyList<ArchiveMetadata>> GetArchiveMetadataForAssetAsync(SqliteConnection connection, CoreAssetKey key, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = @"
SELECT a.archive_id, a.category, a.display_name
FROM asset_archives aa
JOIN archives a ON a.archive_id = aa.archive_id
WHERE aa.type_id=$t AND aa.file_id=$f
ORDER BY a.category, a.display_name, a.archive_id";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		var archives = new List<ArchiveMetadata>();
		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			archives.Add(new ArchiveMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
		}

		return archives;
	}

	private static async Task<IReadOnlyList<(string PackageName, string DisplayName)>> GetSharedArchivesAsync(
		SqliteConnection connection,
		string packageName,
		ulong typeId,
		ulong fileId,
		CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT DISTINCT a.archive_id, a.display_name
FROM archive_entries e
JOIN archives a ON a.archive_id = e.archive_id
WHERE e.type_id = $typeId AND e.file_id = $fileId AND e.archive_id <> $package
ORDER BY a.display_name, a.archive_id;";
		command.Parameters.AddWithValue("$typeId", unchecked((long)typeId));
		command.Parameters.AddWithValue("$fileId", unchecked((long)fileId));
		command.Parameters.AddWithValue("$package", packageName);
		var result = new List<(string PackageName, string DisplayName)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add((reader.GetString(0), reader.GetString(1)));
		}
		return result;
	}

	private static async IAsyncEnumerable<string> GetArchivesForAssetAsync(SqliteConnection connection, CoreAssetKey key, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT archive_id FROM asset_archives WHERE type_id=$t AND file_id=$f";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return reader.GetString(0);
		}
	}
}
