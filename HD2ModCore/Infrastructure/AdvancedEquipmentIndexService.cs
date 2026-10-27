using System.Globalization;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;
using AdaptationStreamLayoutFact = HD2ModAdaptation.Analysis.GameDataStreamLayoutFact;
using AdaptationStreamComponentFact = HD2ModAdaptation.Analysis.GameDataStreamComponentFact;

namespace HD2ModCore.Infrastructure;

// Purpose: Builds optional incremental Armor Unit mesh facts for cross-armor transfer.
public sealed class AdvancedEquipmentIndexService : IAdvancedEquipmentIndexService
{
	private const string Version = "equipment-v6-authoritative-equipment-facts";
	private const string SourceFingerprintMetaKey = "advanced_equipment_index_source_fingerprint";
	private readonly StoragePaths paths;
	private readonly IGameDataArchiveIndexer archiveIndexer;
	private readonly UnitMeshPartClassifier classifier = new();
	private readonly ICanonicalHiddenUnitOutputCache hiddenUnitCache;
	private readonly CanonicalHiddenUnitBuilder hiddenUnitBuilder;

	public AdvancedEquipmentIndexService(
		StoragePaths paths,
		IGameDataArchiveIndexer? archiveIndexer = null,
		ICanonicalHiddenUnitOutputCache? hiddenUnitCache = null,
		CanonicalHiddenUnitBuilder? hiddenUnitBuilder = null)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
		this.archiveIndexer = archiveIndexer ?? new GameDataArchiveIndexer();
		this.hiddenUnitCache = hiddenUnitCache ?? new CanonicalHiddenUnitOutputCache(Path.Combine(paths.DataDirectory, "hidden-unit-cache"));
		this.hiddenUnitBuilder = hiddenUnitBuilder ?? new CanonicalHiddenUnitBuilder();
	}

	public async ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return false;
		try
		{
			await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			var schemaVersion = await SqliteSchema.GetMetaAsync(connection, "schema_version", cancellationToken).ConfigureAwait(false);
			if (!string.Equals(schemaVersion, SqliteSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
			{
				return false;
			}

			var currentBaseFingerprint = await SqliteSchema.GetMetaAsync(connection, "source_fingerprint", cancellationToken).ConfigureAwait(false);
			var advancedSourceFingerprint = await SqliteSchema.GetMetaAsync(connection, SourceFingerprintMetaKey, cancellationToken).ConfigureAwait(false);
			return !string.IsNullOrWhiteSpace(currentBaseFingerprint)
				&& string.Equals(advancedSourceFingerprint, currentBaseFingerprint, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(await SqliteSchema.GetMetaAsync(connection, "advanced_equipment_index_version", cancellationToken).ConfigureAwait(false), Version, StringComparison.Ordinal)
				&& await HasCompleteEquipmentFactsAsync(connection, cancellationToken).ConfigureAwait(false);
		}
		catch (SqliteException exception) when (IsMissingSchemaObject(exception))
		{
			// An older or partially-created database is not current. The next
			// write path will migrate it through EnsureSchemaAsync.
			return false;
		}
	}

	public async ValueTask BuildOrRefreshAsync(string gameDataDirectory, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		var directory = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"GameData directory does not exist: {directory}");
		if (!File.Exists(paths.DbPath)) throw new InvalidOperationException("请先建立基础 Game Data 资产索引。");
		await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

		var archives = await ReadArchivesAsync(cancellationToken).ConfigureAwait(false);
		if (archives.Count == 0) throw new InvalidDataException("基础索引中没有可用 Archive。");
		var equipmentArchiveIds = archives.Where(item => IsEquipmentCategory(item.Category)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (equipmentArchiveIds.Count == 0) throw new InvalidDataException("基础索引中没有可用的 armor 或 helmet Archive。");
		var metadata = archives.ToDictionary(item => item.Id, item => new GameDataArchiveMetadata(item.Hex, item.Name, item.Category), StringComparer.OrdinalIgnoreCase);
		var baseFingerprint = await new AssetArchiveIndexService(paths).GetFingerprintAsync(cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("基础 Game Data 资产索引缺少版本指纹。");
		var rebuildEquipmentFacts = !await IsCurrentAsync(cancellationToken).ConfigureAwait(false);
		var adapterProgress = new Progress<GameDataArchiveIndexProgress>(item => progress?.Report(new IndexBuildProgress(item.Current, Math.Max(item.Total, 1), $"{item.Stage}：{item.Item}")));
		var index = await archiveIndexer.BuildAsync(new GameDataArchiveInput(directory, archives.Select(item => item.Id).ToArray(), metadata, IncludeStreamLayouts: true), adapterProgress, cancellationToken).ConfigureAwait(false);
		var authorityRoles = await ReadAuthorityRolesAsync(cancellationToken).ConfigureAwait(false);
		if (rebuildEquipmentFacts)
		{
			await ClearEquipmentFactsAsync(cancellationToken).ConfigureAwait(false);
		}
		var units = index.Archives.Where(archive => equipmentArchiveIds.Contains(archive.PackageName)).SelectMany(archive => archive.Entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).Select(entry => (archive.PackageName, entry))).ToArray();
		var analyzedUnits = await ReadAnalyzedUnitsAsync(cancellationToken).ConfigureAwait(false);
		var unitsToAnalyze = units.Where(item => !analyzedUnits.Contains((item.PackageName, new CoreAssetKey(item.entry.AssetKey.TypeId, item.entry.AssetKey.FileId)))).ToArray();
		var parts = new List<GameDataUnitPartFact>();
		var resolver = new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(directory);
		var reader = new GameDataUnitMeshReader(resolver);
		var current = 0;
		foreach (var (archiveId, entry) in unitsToAnalyze)
		{
			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report(new IndexBuildProgress(++current, Math.Max(units.Length, 1), $"分析装备 Unit：{archiveId}"));
			try
			{
				var unit = await reader.ReadAsync(archiveId, entry.AssetKey, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
				authorityRoles.TryGetValue(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId), out var authorityRole);
				parts.AddRange(CreatePartFacts(archiveId, new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId), entry.AssetKey, unit.Model, authorityRole));
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException or KeyNotFoundException) { }
		}

		await SaveAsync(parts, index.StreamLayouts, baseFingerprint.SourceFingerprint, cancellationToken).ConfigureAwait(false);
	}

	public ValueTask RebuildAllUnitPartFactsAsync(string gameDataDirectory, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default)
		=> BuildUnitPartFactsAsync(gameDataDirectory, rebuildAll: true, progress, cancellationToken);

	public ValueTask BuildMissingUnitPartFactsAsync(string gameDataDirectory, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default)
		=> BuildUnitPartFactsAsync(gameDataDirectory, rebuildAll: false, progress, cancellationToken);

	private async ValueTask BuildUnitPartFactsAsync(string gameDataDirectory, bool rebuildAll, IProgress<IndexBuildProgress>? progress, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		if (!Directory.Exists(gameDataDirectory)) throw new DirectoryNotFoundException($"GameData directory does not exist: {gameDataDirectory}");
		if (!File.Exists(paths.DbPath)) throw new InvalidOperationException("请先建立基础 Game Data 资产索引。");
		await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

		var archives = await ReadArchivesAsync(cancellationToken).ConfigureAwait(false);
		var equipmentArchiveIds = archives.Where(item => IsEquipmentCategory(item.Category)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var fingerprint = await new AssetArchiveIndexService(paths).GetFingerprintAsync(cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("基础 Game Data 资产索引缺少版本指纹。");
		var effectiveRebuildAll = rebuildAll || !await IsCurrentAsync(cancellationToken).ConfigureAwait(false);
		var allUnits = await ReadEquipmentUnitsAsync(equipmentArchiveIds, cancellationToken).ConfigureAwait(false);
		var units = effectiveRebuildAll
			? allUnits
			: allUnits.Where(unit => !unit.HasPartFacts).ToArray();
		var authorityRoles = await ReadAuthorityRolesAsync(cancellationToken).ConfigureAwait(false);
		var resolver = new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(Path.GetFullPath(gameDataDirectory));
		var reader = new GameDataUnitMeshReader(resolver);
		await hiddenUnitCache.InitializeAsync(fingerprint.SourceFingerprint, gameDataIndexIsCurrent: true, cancellationToken).ConfigureAwait(false);
		var avatarTransforms = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var parts = new List<GameDataUnitPartFact>();
		var layouts = new List<AdaptationStreamLayoutFact>();
		var total = Math.Max(units.Count, 1);
		for (var index = 0; index < units.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var unit = units[index];
			progress?.Report(new IndexBuildProgress(index + 1, total, $"分析{(effectiveRebuildAll ? "全部" : "缺失")} Unit 部位：{unit.ArchiveId}"));
			try
			{
				var adaptationKey = new HD2ModAdaptation.PatchReconstruction.AssetKey(unit.AssetKey.TypeId, unit.AssetKey.FileId);
				var parsed = await reader.ReadAsync(unit.ArchiveId, adaptationKey, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
				authorityRoles.TryGetValue(unit.AssetKey, out var authorityRole);
				parts.AddRange(CreatePartFacts(unit.ArchiveId, new CoreAssetKey(unit.AssetKey.TypeId, unit.AssetKey.FileId), adaptationKey, parsed.Model, authorityRole));
				layouts.AddRange(parsed.Model.Streams.Select(stream => new AdaptationStreamLayoutFact(
					unit.ArchiveId,
					adaptationKey,
					stream.Index,
					stream.ComponentInfoId,
					parsed.Model.Version,
					stream.VertexStride,
					stream.Components.Select(component => new AdaptationStreamComponentFact(
						component.Type,
						component.Format,
						component.Index,
						component.Unknown,
						component.Size)).ToArray())));
				try
				{
					if (await hiddenUnitCache.TryReadAsync(unit.ArchiveId, adaptationKey, cancellationToken).ConfigureAwait(false) is null)
					{
						var hidden = hiddenUnitBuilder.Build(parsed, avatarTransforms);
						await hiddenUnitCache.StoreAsync(unit.ArchiveId, hidden, cancellationToken).ConfigureAwait(false);
					}
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
				{
					// Cache prewarming must never discard an otherwise valid Unit-part fact.
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
			{
				// Keep one malformed Unit from aborting the remaining independent facts.
			}
		}
		await SaveUnitPartFactsAsync(
			parts,
			units.Select(unit => (unit.ArchiveId, unit.AssetKey)).ToArray(),
			effectiveRebuildAll,
			layouts,
			fingerprint.SourceFingerprint,
			cancellationToken).ConfigureAwait(false);
		progress?.Report(new IndexBuildProgress(total, total, effectiveRebuildAll ? "全部 Armor / Helmet Unit 部位事实已完成。" : "缺失 Armor / Helmet Unit 部位事实已完成。"));
	}

	private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<IReadOnlyList<(string Id, string Category, string Name, string? Hex)>> ReadArchivesAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT archive_id,category,display_name,archive_hex FROM archives WHERE status='Indexed' ORDER BY archive_id;";
		var result = new List<(string, string, string, string?)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
		return result;
	}

	private static bool IsEquipmentCategory(string category)
		=> category.Contains("armor", StringComparison.OrdinalIgnoreCase)
			|| category.Contains("helmet", StringComparison.OrdinalIgnoreCase);

	private async ValueTask<HashSet<(string ArchiveId, CoreAssetKey UnitAssetKey)>> ReadAnalyzedUnitsAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT DISTINCT p.archive_id,p.unit_type_id,p.unit_file_id FROM game_data_unit_parts p JOIN archives a ON a.archive_id=p.archive_id WHERE lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%'";
		var result = new HashSet<(string, CoreAssetKey)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			result.Add((reader.GetString(0), new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2)))));
		return result;
	}

	private async ValueTask<IReadOnlyList<EquipmentUnitWorkItem>> ReadEquipmentUnitsAsync(IReadOnlySet<string> archiveIds, CancellationToken cancellationToken)
	{
		if (archiveIds.Count == 0) return Array.Empty<EquipmentUnitWorkItem>();
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT a.archive_id,e.type_id,e.file_id,
       EXISTS(SELECT 1 FROM game_data_unit_parts p WHERE p.archive_id=a.archive_id AND p.unit_type_id=e.type_id AND p.unit_file_id=e.file_id)
FROM archives a
JOIN archive_entries e ON e.archive_id=a.archive_id
WHERE (lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%')
  AND e.type_id=$unitType
ORDER BY a.archive_id,e.file_id;";
		command.Parameters.AddWithValue("$unitType", unchecked((long)PatchUnitMeshReader.UnitTypeId));
		var result = new List<EquipmentUnitWorkItem>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add(new EquipmentUnitWorkItem(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetBoolean(3)));
		}
		return result;
	}

	private async ValueTask ClearEquipmentFactsAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM game_data_unit_parts WHERE archive_id IN (SELECT archive_id FROM archives WHERE lower(category) LIKE '%armor%' OR lower(category) LIKE '%helmet%');";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask SaveUnitPartFactsAsync(
		IReadOnlyList<GameDataUnitPartFact> parts,
		IReadOnlyList<(string ArchiveId, CoreAssetKey UnitAssetKey)> units,
		bool rebuildAll,
		IReadOnlyList<AdaptationStreamLayoutFact> layouts,
		string sourceFingerprint,
		CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await using var delete = connection.CreateCommand();
		delete.Transaction = transaction;
		delete.CommandText = rebuildAll
			? "DELETE FROM game_data_unit_parts WHERE archive_id IN (SELECT archive_id FROM archives WHERE lower(category) LIKE '%armor%' OR lower(category) LIKE '%helmet%');"
			: "DELETE FROM game_data_unit_parts WHERE (archive_id,unit_type_id,unit_file_id) IN (SELECT $archive_id,$unit_type_id,$unit_file_id);";
		if (rebuildAll)
		{
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		else
		{
			// SQLite parameters are used per Unit to keep this compatible with older SQLite builds.
			foreach (var unit in units)
			{
				await using var unitDelete = connection.CreateCommand();
				unitDelete.Transaction = transaction;
				unitDelete.CommandText = "DELETE FROM game_data_unit_parts WHERE archive_id=$archive AND unit_type_id=$type AND unit_file_id=$file;";
				unitDelete.Parameters.AddWithValue("$archive", unit.ArchiveId);
				unitDelete.Parameters.AddWithValue("$type", unchecked((long)unit.UnitAssetKey.TypeId));
				unitDelete.Parameters.AddWithValue("$file", unchecked((long)unit.UnitAssetKey.FileId));
				await unitDelete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		if (rebuildAll)
		{
			await ExecuteAsync(connection, transaction, "DELETE FROM game_data_stream_layouts;", cancellationToken).ConfigureAwait(false);
		}
		else
		{
			foreach (var unit in units)
			{
				await using var layoutDelete = connection.CreateCommand();
				layoutDelete.Transaction = transaction;
				layoutDelete.CommandText = "DELETE FROM game_data_stream_layouts WHERE archive_id=$archive AND unit_type_id=$type AND unit_file_id=$file;";
				layoutDelete.Parameters.AddWithValue("$archive", unit.ArchiveId);
				layoutDelete.Parameters.AddWithValue("$type", unchecked((long)unit.UnitAssetKey.TypeId));
				layoutDelete.Parameters.AddWithValue("$file", unchecked((long)unit.UnitAssetKey.FileId));
				await layoutDelete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		await using var insert = CreatePartCommand(connection, transaction);
		foreach (var part in parts) await InsertPartAsync(insert, part, cancellationToken).ConfigureAwait(false);
		await using var layoutInsert = CreateLayoutCommand(connection, transaction);
		foreach (var layout in layouts) await InsertLayoutAsync(layoutInsert, layout, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_version", Version, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, SourceFingerprintMetaKey, sourceFingerprint, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask SaveAsync(IReadOnlyList<GameDataUnitPartFact> parts, IReadOnlyList<AdaptationStreamLayoutFact> layouts, string sourceFingerprint, CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await ExecuteAsync(connection, transaction, "DELETE FROM game_data_unit_parts WHERE archive_id NOT IN (SELECT archive_id FROM archives WHERE lower(category) LIKE '%armor%' OR lower(category) LIKE '%helmet%');", cancellationToken).ConfigureAwait(false);
		await ExecuteAsync(connection, transaction, "DELETE FROM game_data_unit_parts WHERE NOT EXISTS (SELECT 1 FROM archive_entries e WHERE e.archive_id=game_data_unit_parts.archive_id AND e.type_id=game_data_unit_parts.unit_type_id AND e.file_id=game_data_unit_parts.unit_file_id);", cancellationToken).ConfigureAwait(false);
		await ExecuteAsync(connection, transaction, "DELETE FROM game_data_stream_layouts;", cancellationToken).ConfigureAwait(false);
		await using var partsCommand = CreatePartCommand(connection, transaction);
		foreach (var part in parts) await InsertPartAsync(partsCommand, part, cancellationToken).ConfigureAwait(false);
		await using var layoutsCommand = CreateLayoutCommand(connection, transaction);
		foreach (var layout in layouts) await InsertLayoutAsync(layoutsCommand, layout, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_version", Version, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, SourceFingerprintMetaKey, sourceFingerprint, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<bool> HasCompleteEquipmentFactsAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = @"
WITH equipment_units AS
(
    SELECT a.archive_id, e.type_id, e.file_id
    FROM archives a
    JOIN archive_entries e ON e.archive_id = a.archive_id
    WHERE a.status = 'Indexed'
      AND (lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%')
      AND e.type_id = $unitType
)
SELECT CASE WHEN
    EXISTS (SELECT 1 FROM equipment_units)
    AND NOT EXISTS
    (
        SELECT 1
        FROM equipment_units u
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM game_data_unit_parts p
            WHERE p.archive_id = u.archive_id
              AND p.unit_type_id = u.type_id
              AND p.unit_file_id = u.file_id
        )
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM equipment_units u
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM game_data_stream_layouts s
            WHERE s.archive_id = u.archive_id
              AND s.unit_type_id = u.type_id
              AND s.unit_file_id = u.file_id
        )
    )
    THEN 1 ELSE 0 END;";
		command.Parameters.AddWithValue("$unitType", unchecked((long)PatchUnitMeshReader.UnitTypeId));
		return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
	}

	private static bool IsMissingSchemaObject(SqliteException exception)
		=> exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
			|| exception.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
			|| exception.Message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase);

	private async ValueTask<IReadOnlyDictionary<CoreAssetKey, AuthorityUnitRole>> ReadAuthorityRolesAsync(
		CancellationToken cancellationToken)
	{
		// The customization table's archive hash identifies the logical equipment
		// item, not necessarily the physical archive that stores a shared Unit.
		// Index roles by Unit AssetKey globally so a shared body/helmet Unit keeps
		// its authoritative slot and body variant when it is stored in another archive.
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
		SELECT unit_type_id,unit_file_id,body_variant,part_kind,piece_type,slot
		FROM game_data_authority_pieces
		WHERE kit_kind IN ($armor,$helmet)
	  AND unit_type_id=$unitType
		ORDER BY unit_file_id,piece_id;";
		command.Parameters.AddWithValue("$armor", (int)GameDataEquipmentKitKind.Armor);
		command.Parameters.AddWithValue("$helmet", (int)GameDataEquipmentKitKind.Helmet);
		command.Parameters.AddWithValue("$unitType", unchecked((long)PatchUnitMeshReader.UnitTypeId));
		var grouped = new Dictionary<CoreAssetKey, List<AuthorityRoleInput>>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var key = new CoreAssetKey(
				unchecked((ulong)reader.GetInt64(0)),
				unchecked((ulong)reader.GetInt64(1)));
			if (!grouped.TryGetValue(key, out var values)) grouped[key] = values = new List<AuthorityRoleInput>();
			values.Add(new AuthorityRoleInput(
				(UnitMeshBodyVariant)reader.GetInt32(2),
				(UnitMeshPartKind)reader.GetInt32(3),
				unchecked((uint)reader.GetInt64(4)),
				unchecked((uint)reader.GetInt64(5))));
		}

		return grouped.ToDictionary(pair => pair.Key, pair => AuthorityUnitRole.Create(pair.Value));
	}

	private static async ValueTask ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreatePartCommand(SqliteConnection connection, SqliteTransaction transaction)
	{
		var command = connection.CreateCommand(); command.Transaction = transaction;
		command.CommandText = "INSERT INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,authority_slot,authority_piece_type,reason) VALUES($a,$t,$f,$m,$id,$k,$l,$v,$n,$piece,$c,$visual,$lod,$vertices,$triangles,$quality,$origin,$aslot,$apt,$r)";
		foreach (var name in new[] { "$a", "$n", "$piece", "$r" }) command.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$t", "$f", "$m", "$id", "$k", "$l", "$v", "$c", "$visual", "$lod", "$vertices", "$triangles", "$quality", "$origin", "$aslot", "$apt" }) command.Parameters.Add(name, SqliteType.Integer); command.Prepare(); return command;
	}

	private sealed record EquipmentUnitWorkItem(string ArchiveId, CoreAssetKey AssetKey, bool HasPartFacts);

	private static async ValueTask InsertPartAsync(SqliteCommand command, GameDataUnitPartFact part, CancellationToken cancellationToken)
	{
		command.Parameters["$a"].Value = part.ArchiveId; command.Parameters["$t"].Value = unchecked((long)part.UnitAssetKey.TypeId); command.Parameters["$f"].Value = unchecked((long)part.UnitAssetKey.FileId); command.Parameters["$m"].Value = part.MeshInfoIndex; command.Parameters["$id"].Value = part.MeshId; command.Parameters["$k"].Value = (int)part.PartKind; command.Parameters["$l"].Value = (int)part.Layer; command.Parameters["$v"].Value = (int)part.BodyVariant; command.Parameters["$n"].Value = part.SemanticName; command.Parameters["$piece"].Value = part.PieceType; command.Parameters["$c"].Value = part.Confidence; command.Parameters["$visual"].Value = part.IsVisualMesh ? 1 : 0; command.Parameters["$lod"].Value = part.IsLod ? 1 : 0; command.Parameters["$vertices"].Value = part.VertexCount; command.Parameters["$triangles"].Value = part.TriangleCount; command.Parameters["$quality"].Value = (int)part.GeometryQuality; command.Parameters["$origin"].Value = (int)part.Origin; command.Parameters["$aslot"].Value = part.Cell.IsKnown ? part.Cell.Slot : ArmorCell.UnknownCoordinate; command.Parameters["$apt"].Value = part.Cell.IsKnown ? part.Cell.Layer : ArmorCell.UnknownCoordinate; command.Parameters["$r"].Value = part.Reason; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private IReadOnlyList<GameDataUnitPartFact> CreatePartFacts(
		string archiveId,
		CoreAssetKey unitKey,
		HD2ModAdaptation.PatchReconstruction.AssetKey adaptationKey,
		UnitMeshModel model,
		AuthorityUnitRole? authorityRole)
	{
		var geometryByMesh = UnitGeometryFactsBuilder.Analyze(model).Meshes.ToDictionary(fact => fact.MeshInfoIndex);
		// The model parser still owns visual/culling/LOD and geometry facts.  The
		// installed customization table owns the player-equipment identity, so
		// avoid community bone-name lookup here entirely.
		return classifier.Classify(adaptationKey, model).Select(part =>
		{
			geometryByMesh.TryGetValue(part.MeshInfoIndex, out var geometry);
			var kind = authorityRole?.PartKind ?? part.PartKind;
			var bodyVariant = authorityRole?.BodyVariant ?? part.BodyVariant;
			var layer = part.Layer == UnitMeshPartLayer.Unknown && authorityRole?.FallbackLayer is { } fallbackLayer
				? fallbackLayer
				: part.Layer;
			var hasAuthorityIdentity = authorityRole?.HasIdentity == true;
			var reason = hasAuthorityIdentity
				? $"{authorityRole!.Detail}; geometry from parsed Unit."
				: part.Reason;
			return new GameDataUnitPartFact(archiveId, unitKey, part.MeshInfoIndex, part.MeshId, kind, layer, bodyVariant, part.SemanticName, hasAuthorityIdentity ? 100 : part.Confidence, part.IsVisualMesh, part.IsLod, reason)
			{
				Origin = hasAuthorityIdentity ? GameDataFactOrigin.AuthoritativeGameData : GameDataFactOrigin.DerivedUnitStructure,
				PieceType = part.PieceType,
				Cell = authorityRole?.Cell ?? ArmorCell.Unknown,
				VertexCount = geometry?.VertexCount ?? 0,
				TriangleCount = geometry?.TriangleCount ?? 0,
				GeometryQuality = geometry?.Quality ?? UnitMeshGeometryQuality.Unreadable
			};
		}).ToArray();
	}

	private sealed record AuthorityRoleInput(UnitMeshBodyVariant BodyVariant, UnitMeshPartKind PartKind, uint PieceType, uint Slot);

	private sealed record AuthorityUnitRole(
		UnitMeshPartKind? PartKind,
		UnitMeshBodyVariant? BodyVariant,
		UnitMeshPartLayer? FallbackLayer,
		ArmorCell Cell,
		string Detail)
	{
		public bool HasIdentity => PartKind is not null || BodyVariant is not null;

		public static AuthorityUnitRole Create(IReadOnlyList<AuthorityRoleInput> inputs)
		{
			var partKinds = inputs.Select(input => input.PartKind)
				.Where(kind => kind != UnitMeshPartKind.Unknown)
				.Distinct()
				.ToArray();
			UnitMeshPartKind? partKind = partKinds.Length == 1 ? partKinds[0] : null;
			var variants = inputs.Select(input => input.BodyVariant)
				.Where(variant => variant != UnitMeshBodyVariant.Unknown && variant != UnitMeshBodyVariant.Other)
				.Distinct()
				.ToArray();
			UnitMeshBodyVariant? bodyVariant = variants.Contains(UnitMeshBodyVariant.Any)
				|| variants.Contains(UnitMeshBodyVariant.Slim) && variants.Contains(UnitMeshBodyVariant.Stocky)
					? UnitMeshBodyVariant.Any
					: variants.Length == 1 ? variants[0] : null;
			// pieceType 是"部位内的第几个 unit 槽"：0 = 内层、1 = 护甲、2 = 装饰。
			// 只有一个槽时才能据此判层；混着多个槽就不知道这一件到底属于哪层，宁可不判。
			// （旧实现把 ==1 判成"内层"，方向是反的。）
			var pieceTypes = inputs.Select(input => input.PieceType).Distinct().OrderBy(value => value).ToArray();
			UnitMeshPartLayer? fallbackLayer = pieceTypes.Length == 1
				? pieceTypes[0] == 0 ? UnitMeshPartLayer.Undergarment : UnitMeshPartLayer.Armor
				: null;
			// 权威格子：部位槽 × 层。实测一个 unit 全局只属于一个格子（2006 个 unit 零冲突），
			// 所以这里正常只有一种取值；不一致说明数据真的矛盾，那就老实报"不知道"，
			// 不要把两个不同的格子折成一个——折了以后下游无法区分躯干内层和躯干护甲。
			var slots = inputs.Select(input => input.Slot).Distinct().ToArray();
			var cell = slots.Length == 1 && pieceTypes.Length == 1
				? new ArmorCell(unchecked((int)slots[0]), unchecked((int)pieceTypes[0]))
				: ArmorCell.Unknown;
			// 一个 unit 被多个部位引用是**事实**，不是数据缺陷：把全部归属都写进 detail，
			// 不要用一句 "Ambiguous" 把它抹掉（共享 unit 属于所有引用它的护甲）。
			var partText = partKind?.ToString()
				?? (partKinds.Length > 1 ? string.Join('|', partKinds) : "Ambiguous");
			var detail = $"Authoritative GameData equipment relation: Part={partText}; Body={bodyVariant?.ToString() ?? "Ambiguous"}; PieceType={string.Join(',', pieceTypes)}; Slot={string.Join(',', slots)}";
			return new AuthorityUnitRole(partKind, bodyVariant, fallbackLayer, cell, detail);
		}
	}

	private static SqliteCommand CreateLayoutCommand(SqliteConnection connection, SqliteTransaction transaction)
	{
		var command = connection.CreateCommand(); command.Transaction = transaction;
		command.CommandText = "INSERT INTO game_data_stream_layouts(archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned) VALUES($a,$t,$f,$s,$c,$v,$stride,$components,$signature,$skinned)";
		foreach (var name in new[] { "$a", "$components", "$signature" }) command.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$t", "$f", "$s", "$c", "$v", "$stride", "$skinned" }) command.Parameters.Add(name, SqliteType.Integer); command.Prepare(); return command;
	}

	private static async ValueTask InsertLayoutAsync(SqliteCommand command, AdaptationStreamLayoutFact layout, CancellationToken cancellationToken)
	{
		command.Parameters["$a"].Value = layout.PackageName; command.Parameters["$t"].Value = unchecked((long)layout.UnitAssetKey.TypeId); command.Parameters["$f"].Value = unchecked((long)layout.UnitAssetKey.FileId); command.Parameters["$s"].Value = layout.StreamIndex; command.Parameters["$c"].Value = unchecked((long)layout.ComponentInfoId); command.Parameters["$v"].Value = layout.UnitVersion; command.Parameters["$stride"].Value = layout.VertexStride; command.Parameters["$components"].Value = System.Text.Json.JsonSerializer.Serialize(layout.Components.Select(component => new HD2ModCore.Domain.GameDataStreamComponentFact(component.Type, component.Format, component.Index, component.Unknown, component.Size))); command.Parameters["$signature"].Value = layout.LayoutSignature; command.Parameters["$skinned"].Value = layout.IsSkinned ? 1 : 0; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
