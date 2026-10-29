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
//
// 这是 `game_data_unit_parts` 与 `game_data_stream_layouts` 的**唯一**写入方。
// 曾经还有一条 `BuildOrRefreshAsync` 平行路径：它自己再跑一遍 `GameDataArchiveIndexer`
// 去拿 stream 布局，等于把基础索引的活干第二遍。它没有任何调用方（GUI 与 CLI 都走
// RebuildAll/BuildMissing），却让"两张表谁写"变成两个答案，所以整条路径连同
// SaveAsync / ClearEquipmentFactsAsync / ReadAnalyzedUnitsAsync 一起删掉了。
public sealed class AdvancedEquipmentIndexService : IAdvancedEquipmentIndexService
{
	// internal 而不是 private：用例要拿它去写 meta，否则那份"版本号字符串"会被复制到测试里，
	// 以后改版本号时测试会悄悄验证一个过期的值。
	internal const string Version = "equipment-v7-registry-unit-enumeration";
	private const string SourceFingerprintMetaKey = "advanced_equipment_index_source_fingerprint";
	private readonly StoragePaths paths;
	private readonly UnitMeshPartClassifier classifier = new();
	private readonly ICanonicalHiddenUnitOutputCache hiddenUnitCache;
	private readonly CanonicalHiddenUnitBuilder hiddenUnitBuilder;

	public AdvancedEquipmentIndexService(
		StoragePaths paths,
		ICanonicalHiddenUnitOutputCache? hiddenUnitCache = null,
		CanonicalHiddenUnitBuilder? hiddenUnitBuilder = null)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
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
			if (string.IsNullOrWhiteSpace(currentBaseFingerprint)
				|| !string.Equals(advancedSourceFingerprint, currentBaseFingerprint, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(await SqliteSchema.GetMetaAsync(connection, "advanced_equipment_index_version", cancellationToken).ConfigureAwait(false), Version, StringComparison.Ordinal))
			{
				return false;
			}

			// 完整性判据与"要分析哪些 unit"共用同一个查询：两边各写一份 SQL 时，只要有一边
			// 的集合定义漂了（以前一边按 archive 分类、一边按注册表），这个门就永远为假。
			var units = await ReadEquipmentUnitsAsync(cancellationToken).ConfigureAwait(false);
			return units.Count != 0 && units.All(unit => unit.HasPartFacts && unit.HasStreamLayouts);
		}
		catch (SqliteException exception) when (IsMissingSchemaObject(exception))
		{
			// An older or partially-created database is not current. The next
			// write path will migrate it through EnsureSchemaAsync.
			return false;
		}
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

		var fingerprint = await new AssetArchiveIndexService(paths).GetFingerprintAsync(cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("基础 Game Data 资产索引缺少版本指纹。");
		var effectiveRebuildAll = rebuildAll || !await IsCurrentAsync(cancellationToken).ConfigureAwait(false);
		var allUnits = await ReadEquipmentUnitsAsync(cancellationToken).ConfigureAwait(false);
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

	/// <summary>
	/// 「哪些 unit 需要部位事实」的**唯一定义**：以注册表为准，archive 只是它被放在哪。
	/// </summary>
	/// <remarks>
	/// 以前这里是按 `archives.category LIKE '%armor%'/'%helmet%'` 扫 archive_entries。注册表里
	/// 2006 个 unit 中有 2 个（披风槽）被护甲套引用、实体却存在一个 Weapon 分类的 archive 里，
	/// 于是永远进不了部位表——任何护甲的披风槽都没有部件事实。判据反过来就没有这个洞：
	/// unit 的归属由注册表给，archive 只决定"从哪个文件解析"。
	///
	/// 一个 unit 可能同时存在于多个 archive（实测 429 个）。此时优先取分类是装备的那一份，
	/// 因为 `EquipmentUnitCatalogService` 只读装备分类的 archive——取别的副本等于白解析一遍。
	/// 这只是**平手时的取舍**，不是判据：只有在所有副本都不是装备分类时（披风就是这种情况）
	/// 才退回到任意副本。
	///
	/// 返回 HasPartFacts / HasStreamLayouts 而不是另写一份"完整性"SQL：两边各写一份时，
	/// 只要集合定义漂了，`IsCurrentAsync` 就会永远为假（本机就是这么坏掉的）。
	/// </remarks>
	internal async ValueTask<IReadOnlyList<EquipmentUnitWorkItem>> ReadEquipmentUnitsAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
WITH registry_units AS
(
    SELECT DISTINCT unit_type_id, unit_file_id
    FROM game_data_authority_pieces
    WHERE kit_kind IN ($armor,$helmet) AND unit_type_id=$unitType AND unit_file_id<>0
),
candidates AS
(
    SELECT e.archive_id, e.type_id, e.file_id,
           CASE WHEN lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%' THEN 0 ELSE 1 END AS preference
    FROM registry_units r
    JOIN archive_entries e ON e.type_id=r.unit_type_id AND e.file_id=r.unit_file_id
    JOIN archives a ON a.archive_id=e.archive_id AND a.status='Indexed'
),
preferred AS
(
    SELECT file_id, MIN(preference) AS best FROM candidates GROUP BY file_id
)
SELECT c.archive_id, c.type_id, c.file_id,
       EXISTS(SELECT 1 FROM game_data_unit_parts p WHERE p.archive_id=c.archive_id AND p.unit_type_id=c.type_id AND p.unit_file_id=c.file_id),
       EXISTS(SELECT 1 FROM game_data_stream_layouts s WHERE s.archive_id=c.archive_id AND s.unit_type_id=c.type_id AND s.unit_file_id=c.file_id)
FROM candidates c
JOIN preferred p ON p.file_id=c.file_id AND p.best=c.preference
ORDER BY c.archive_id, c.file_id;";
		command.Parameters.AddWithValue("$armor", (int)GameDataEquipmentKitKind.Armor);
		command.Parameters.AddWithValue("$helmet", (int)GameDataEquipmentKitKind.Helmet);
		command.Parameters.AddWithValue("$unitType", unchecked((long)PatchUnitMeshReader.UnitTypeId));
		var result = new List<EquipmentUnitWorkItem>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add(new EquipmentUnitWorkItem(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetBoolean(3),
				reader.GetBoolean(4)));
		}
		return result;
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
		command.CommandText = "INSERT INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,reason) VALUES($a,$t,$f,$m,$id,$k,$l,$v,$n,$piece,$c,$visual,$lod,$vertices,$triangles,$quality,$origin,$r)";
		foreach (var name in new[] { "$a", "$n", "$piece", "$r" }) command.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$t", "$f", "$m", "$id", "$k", "$l", "$v", "$c", "$visual", "$lod", "$vertices", "$triangles", "$quality", "$origin" }) command.Parameters.Add(name, SqliteType.Integer); command.Prepare(); return command;
	}

	// internal 是为了让用例能直接钉住"枚举源是注册表"：真跑一遍要解析真实 unit 二进制。
	internal sealed record EquipmentUnitWorkItem(string ArchiveId, CoreAssetKey AssetKey, bool HasPartFacts, bool HasStreamLayouts);

	private static async ValueTask InsertPartAsync(SqliteCommand command, GameDataUnitPartFact part, CancellationToken cancellationToken)
	{
		command.Parameters["$a"].Value = part.ArchiveId; command.Parameters["$t"].Value = unchecked((long)part.UnitAssetKey.TypeId); command.Parameters["$f"].Value = unchecked((long)part.UnitAssetKey.FileId); command.Parameters["$m"].Value = part.MeshInfoIndex; command.Parameters["$id"].Value = part.MeshId; command.Parameters["$k"].Value = (int)part.PartKind; command.Parameters["$l"].Value = (int)part.Layer; command.Parameters["$v"].Value = (int)part.BodyVariant; command.Parameters["$n"].Value = part.SemanticName; command.Parameters["$piece"].Value = part.PieceType; command.Parameters["$c"].Value = part.Confidence; command.Parameters["$visual"].Value = part.IsVisualMesh ? 1 : 0; command.Parameters["$lod"].Value = part.IsLod ? 1 : 0; command.Parameters["$vertices"].Value = part.VertexCount; command.Parameters["$triangles"].Value = part.TriangleCount; command.Parameters["$quality"].Value = (int)part.GeometryQuality; command.Parameters["$origin"].Value = (int)part.Origin; command.Parameters["$r"].Value = part.Reason; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
				// 格子不在这里算、也不落库：它属于 unit，读取时 JOIN 注册表得出（UnitPartFactQuery）。
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
			// 一个 unit 被多个部位引用是**事实**，不是数据缺陷：把全部归属都写进 detail，
			// 不要用一句 "Ambiguous" 把它抹掉（共享 unit 属于所有引用它的护甲）。
			// slot 只用来写这段说明：格子本身由 UnitPartFactQuery 在读取时从注册表算，
			// 那里对"一个 unit 出现两个槽位"有自己的判断（报未知），不在这里重复一遍。
			var slots = inputs.Select(input => input.Slot).Distinct().OrderBy(value => value).ToArray();
			var partText = partKind?.ToString()
				?? (partKinds.Length > 1 ? string.Join('|', partKinds) : "Ambiguous");
			var detail = $"Authoritative GameData equipment relation: Part={partText}; Body={bodyVariant?.ToString() ?? "Ambiguous"}; PieceType={string.Join(',', pieceTypes)}; Slot={string.Join(',', slots)}";
			return new AuthorityUnitRole(partKind, bodyVariant, fallbackLayer, detail);
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
