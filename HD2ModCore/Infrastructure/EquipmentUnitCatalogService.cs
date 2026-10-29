using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using AdaptationPatchUnitMeshReader = HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader;

namespace HD2ModCore.Infrastructure;

// Purpose: Projects SQLite Armor and Helmet Unit-part facts into a no-write source/target transfer preview.
public sealed class EquipmentUnitCatalogService : IEquipmentUnitCatalogService
{
	private readonly StoragePaths paths;

	public EquipmentUnitCatalogService(StoragePaths paths)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesAsync(IReadOnlySet<AssetKey>? unitAssetKeys = null, CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return Array.Empty<EquipmentUnitCatalogEntry>();
		await EnsureCurrentSchemaAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return await ReadEntriesAsync(connection, unitAssetKeys, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesReadOnlyAsync(IReadOnlySet<AssetKey>? unitAssetKeys = null, CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return Array.Empty<EquipmentUnitCatalogEntry>();
		try
		{
			await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			return await ReadEntriesAsync(connection, unitAssetKeys, cancellationToken).ConfigureAwait(false);
		}
		catch (SqliteException)
		{
			// A CLI inspect must not repair/migrate an old database just to obtain
			// a preview. The caller can ask the Manager to rebuild the index.
			return Array.Empty<EquipmentUnitCatalogEntry>();
		}
	}

	private static async ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> ReadEntriesAsync(
		SqliteConnection connection,
		IReadOnlySet<AssetKey>? unitAssetKeys,
		CancellationToken cancellationToken)
	{
		var unitIds = unitAssetKeys?
			.Where(key => key.TypeId == AdaptationPatchUnitMeshReader.UnitTypeId)
			.Select(key => unchecked((long)key.FileId))
			.Distinct()
			.ToArray();
		if (unitAssetKeys is not null && unitIds is { Length: 0 }) return Array.Empty<EquipmentUnitCatalogEntry>();
		await using var command = connection.CreateCommand();
		// 列与行映射走 UnitPartFactQuery：权威格子在那里从注册表 JOIN 出来。以前这里读的是
		// 部位表里那份**副本**（authority_slot/authority_piece_type），而 GetUnitPartFactsAsync
		// 根本不读它——同一张表两个读法，一个有一格没有。
		command.CommandText = $@"
SELECT a.archive_id,a.category,a.display_name,{UnitPartFactQuery.Columns},
			 COALESCE((SELECT MAX(e.toc_data_size + e.stream_size + e.gpu_resource_size)
								 FROM archive_entries e
								 WHERE e.archive_id=a.archive_id
									 AND e.type_id=p.unit_type_id
									 AND e.file_id=p.unit_file_id), 0) AS stored_bytes
{UnitPartFactQuery.From}
JOIN archives a ON a.archive_id=p.archive_id
WHERE (lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%')
  AND p.is_visual=1 AND p.is_lod=0
  AND EXISTS(SELECT 1 FROM archive_entries e WHERE e.archive_id=a.archive_id AND e.type_id=p.unit_type_id AND e.file_id=p.unit_file_id)
	  AND ($unitIds IS NULL OR p.unit_file_id IN (SELECT value FROM json_each($unitIds)))
ORDER BY CASE WHEN lower(a.category) LIKE '%armor%' THEN 0 ELSE 1 END,a.display_name,a.archive_id,p.unit_file_id,p.mesh_info_index;";
		command.Parameters.AddWithValue("$unitIds", unitIds is null ? DBNull.Value : JsonSerializer.Serialize(unitIds));

		var rawParts = new List<(string archiveId, string category, string name, EquipmentUnitPart part)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var fact = UnitPartFactQuery.ReadFact(reader, 3, reader.GetString(0));
			rawParts.Add((
				reader.GetString(0), reader.GetString(1), reader.GetString(2),
				new EquipmentUnitPart(
					fact.UnitAssetKey,
					fact.MeshInfoIndex,
					fact.MeshId,
					fact.PartKind,
					fact.Layer,
					fact.BodyVariant,
					fact.SemanticName,
					fact.Confidence,
					Array.Empty<string>())
				{
					PieceType = fact.PieceType,
					VertexCount = fact.VertexCount,
					TriangleCount = fact.TriangleCount,
					GeometryQuality = fact.GeometryQuality,
					Cell = fact.Cell,
					StoredBytes = reader.GetInt64(3 + UnitPartFactQuery.ColumnCount)
				}));
		}

		var unitKeys = rawParts.Select(item => item.part.UnitAssetKey).Distinct().ToArray();
		var sharedByUnit = await ReadSharedArchiveIdsAsync(connection, unitKeys, cancellationToken).ConfigureAwait(false);
		return rawParts
			.GroupBy(item => new { item.archiveId, item.category, item.name })
			.Select(group => new EquipmentUnitCatalogEntry(
				group.Key.archiveId,
				group.Key.category,
				group.Key.name,
				group.Select(item => item.part with { SharedArchiveIds = sharedByUnit.GetValueOrDefault(item.part.UnitAssetKey, Array.Empty<string>()) }).ToArray()))
			.ToArray();
	}

    public ValueTask<CrossArmorTransferPlan> CreatePlanAsync(
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		string? selectedSourceArchiveId,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		CrossArmorBodyVariantPreference bodyVariantPreference,
		CrossArmorLayerPreference layerPreference,
		IReadOnlyCollection<string> selectedTargetArchiveIds,
		IReadOnlyList<CrossArmorManualMapping>? manualMappings = null,
		IReadOnlyList<CrossArmorManualSuppression>? manualSuppressions = null,
		bool manualMode = false,
		IReadOnlyCollection<string>? additionalSourceArchiveIds = null,
		bool writePlannerDebugLog = true,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sourceCandidates);
		ArgumentNullException.ThrowIfNull(selectedTargetArchiveIds);
		var issues = new List<CoreIssue>();
		var plannerDebug = new List<string>
		{
			$"[START] SourceArchive={selectedSourceArchiveId ?? "<null>"} SourceVariant={selectedSourceBodyVariant?.ToString() ?? "<null>"} Preference={bodyVariantPreference} Layer={layerPreference}",
			$"[TARGETS] {string.Join(",", selectedTargetArchiveIds)}"
		};
		var source = sourceCandidates.SingleOrDefault(candidate => string.Equals(candidate.ArchiveId, selectedSourceArchiveId, StringComparison.OrdinalIgnoreCase));
		if (source is null)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SourceEquipmentRequired", "请选择一个来源 Armor 或 Helmet。"));
			return ValueTask.FromResult(new CrossArmorTransferPlan(sourceCandidates, null, Array.Empty<EquipmentUnitCatalogEntry>(), Array.Empty<CrossArmorTransferMapping>(), Array.Empty<CrossArmorTransferImpact>(), issues));
		}
		var targets = targetCandidates.Where(candidate => selectedTargetArchiveIds.Contains(candidate.ArchiveId, StringComparer.OrdinalIgnoreCase)).ToArray();
		if (targets.Length == 0)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "TargetEquipmentRequired", "请至少选择一个目标 Armor 或 Helmet。"));
			return ValueTask.FromResult(new CrossArmorTransferPlan(sourceCandidates, source, targets, Array.Empty<CrossArmorTransferMapping>(), Array.Empty<CrossArmorTransferImpact>(), issues));
		}

		var sourceArchiveIds = new[] { source.ArchiveId }.Concat(additionalSourceArchiveIds ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var allSourceParts = sourceCandidates.Where(candidate => sourceArchiveIds.Contains(candidate.ArchiveId)).SelectMany(candidate => candidate.Parts).ToArray();
		plannerDebug.Add($"[SOURCE-RAW] Parts={allSourceParts.Length}");
		foreach (var part in allSourceParts.OrderBy(part => part.PartKind).ThenBy(part => part.Layer).ThenBy(part => part.UnitAssetKey.FileId))
			plannerDebug.Add($"[SOURCE-RAW] Unit=0x{part.UnitAssetKey.FileId:x16} Part={part.PartKind} Layer={part.Layer} Variant={part.BodyVariant} Bytes={part.StoredBytes} Semantic={part.SemanticName}");
		var sourceParts = allSourceParts
			.Where(part => part.PartKind != UnitMeshPartKind.Unknown && part.Confidence == 100)
			.Where(part => selectedSourceBodyVariant is null or UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any || part.BodyVariant == selectedSourceBodyVariant || part.BodyVariant == UnitMeshBodyVariant.Any)
			.OrderBy(part => part.PartKind).ThenBy(part => part.Layer).ThenBy(part => part.UnitAssetKey.FileId)
			.ToArray();
		// Culling data belongs to a selected Unit's internal mesh mapping. It is not a
		// standalone visible source part and cannot drive armor replacement by itself.
		sourceParts = sourceParts.Where(part => !part.IsCullingMesh).ToArray();
		plannerDebug.Add($"[SOURCE-FILTERED] Parts={sourceParts.Length}");
		foreach (var part in sourceParts.OrderBy(part => part.PartKind).ThenBy(part => part.Layer).ThenBy(part => part.UnitAssetKey.FileId))
			plannerDebug.Add($"[SOURCE-FILTERED] Unit=0x{part.UnitAssetKey.FileId:x16} Part={part.PartKind} Layer={part.Layer} Variant={part.BodyVariant} Bytes={part.StoredBytes} Semantic={part.SemanticName}");
		var sourceCategoryByUnit = sourceCandidates.Where(candidate => sourceArchiveIds.Contains(candidate.ArchiveId))
			.SelectMany(candidate => candidate.Parts.Select(part => new { part.UnitAssetKey, candidate.Category }))
			.GroupBy(item => item.UnitAssetKey)
			.ToDictionary(group => group.Key, group => group.First().Category);
		if (sourceParts.Length == 0)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SelectedSourceHasNoTransferParts", "所选来源没有可识别的可见非 LOD 部件。"));
		}

		var targetsByUnit = targets
			.Where(target => !target.IsCustomModel)
			.SelectMany(target => target.Parts.Select(part => new TargetUse(target, part)))
			.Where(item => item.Part.PartKind != UnitMeshPartKind.Unknown && item.Part.Confidence == 100)
			.GroupBy(item => item.Part.UnitAssetKey)
			.Select(group =>
			{
				var representative = group.ApplyGeometryOrder(item => item.Part).ThenBy(item => item.Part.MeshInfoIndex).First();
				return new TargetUnitGroup(
					group.Key,
					representative,
					group.ToArray());
			})
			.OrderBy(group => group.UnitAssetKey.FileId)
			.ToArray();
		var manualByTarget = (manualMappings ?? Array.Empty<CrossArmorManualMapping>()).ToDictionary(mapping => mapping.Target.UnitAssetKey);
		var suppressedTargets = (manualSuppressions ?? Array.Empty<CrossArmorManualSuppression>()).Select(suppression => suppression.Target.UnitAssetKey).ToHashSet();

		// 单元格装箱：按注册表权威格子把来源部件放进目标格子。这是**唯一**的规划路径——
		// 以前还有一条按 (部位, 层) 猜的启发式路径，它没有生产调用方（GUI 与 CLI 都走这条），
		// 却让同一批用例断言两套语义，已经删掉。
		var cellPlan = CellCrossArmorPlanProjection.Build(
			sourceParts,
			targets,
			bodyVariantPreference,
			// 计划阶段永远列全：未命中的目标显示成"隐藏"，是否真的写占位由生产端的
			// AutoHideUnmappedTargetUnits 决定。这样计划表能诚实展示"哪些不会被替换"。
			autoHideUnmapped: true,
			manualMappings ?? Array.Empty<CrossArmorManualMapping>(),
			manualSuppressions ?? Array.Empty<CrossArmorManualSuppression>(),
			manualMode,
			plannerDebug);
		issues.AddRange(cellPlan.Issues);
		var cellMappings = cellPlan.Mappings.ToList();
		var cellImpacts = cellPlan.Impacts.ToList();
		AppendCustomModelMappings(cellMappings, targetCandidates, targets, sourceParts, bodyVariantPreference, manualMappings, manualSuppressions, manualMode, issues);
		if (cellMappings.Count == 0)
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "NoTargetParts", "所选目标没有可识别的可见非 LOD 部件。"));
		plannerDebug.Add($"[RESULT] Mappings={cellMappings.Count} Hits={cellMappings.Count(mapping => mapping.WillReplace)} Hidden={cellMappings.Count(mapping => !mapping.WillReplace)} Merged={cellMappings.Count(mapping => mapping.RequiresMerge)}");
		if (writePlannerDebugLog) WritePlannerDebugLog(plannerDebug);
		return ValueTask.FromResult(new CrossArmorTransferPlan(
			sourceCandidates, source, targets, cellMappings, cellImpacts.Distinct().ToArray(), issues));
	}

	// 自定义素体两条规划器共用：它们没有注册表格子，只能走 CustomModelTransferPlanner 的显式映射。
	private static void AppendCustomModelMappings(
		List<CrossArmorTransferMapping> mappings,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targets,
		IReadOnlyList<EquipmentUnitPart> sourceParts,
		CrossArmorBodyVariantPreference bodyVariantPreference,
		IReadOnlyList<CrossArmorManualMapping>? manualMappings,
		IReadOnlyList<CrossArmorManualSuppression>? manualSuppressions,
		bool manualMode,
		List<CoreIssue> issues)
	{
		var customTargets = targets.Where(target => target.IsCustomModel).ToArray();
		if (customTargets.Length == 0) return;
		var customParts = customTargets.SelectMany(target => target.Parts).ToArray();
		var realKeys = targetCandidates.Where(target => !target.IsCustomModel).SelectMany(target => target.Parts).Select(part => part.UnitAssetKey).ToHashSet();
		if (customParts.GroupBy(part => part.UnitAssetKey).Any(group => group.Count() > 1)
			|| customParts.Any(part => realKeys.Contains(part.UnitAssetKey) || part.UnitAssetKey.FileId == 0
				|| part.PartKind == UnitMeshPartKind.Unknown || part.BodyVariant is not (UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)))
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "CustomModelTargetInvalid", "自定义组件 ID 重复、与原版冲突或部位/身形不受支持；请修正素体定义。"));
		if (customTargets.Any(target => targetCandidates.Any(other => other.IsCustomModel
			&& other.CustomModelKey != target.CustomModelKey
			&& other.Parts.Any(part => target.Parts.Any(selected => selected.UnitAssetKey == part.UnitAssetKey)))))
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "CustomModelOwnershipConflict", "自定义组件 ID 同时被其他素体使用；请先解决 ID 归属冲突，避免互相覆盖。"));
		mappings.AddRange(CustomModelTransferPlanner.Build(customTargets, sourceParts, bodyVariantPreference,
			manualMappings, manualSuppressions, manualMode, issues));
	}

	private async ValueTask EnsureCurrentSchemaAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await Sqlite.SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	private static void WritePlannerDebugLog(IReadOnlyList<string> lines)
	{
		try
		{
			var directory = Path.Combine(AppContext.BaseDirectory, "data", "indexes");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, "cross-armor-planner-debug.log");
			File.AppendAllLines(path, new[] { $"===== {DateTimeOffset.UtcNow:O} =====" }.Concat(lines));
		}
		catch (IOException)
		{
			// Temporary diagnostics must never block plan generation.
		}
		catch (UnauthorizedAccessException)
		{
			// Temporary diagnostics must never block plan generation.
		}
	}

	private static UnitMeshBodyVariant PreferredBodyVariant(CrossArmorBodyVariantPreference preference)
		=> preference == CrossArmorBodyVariantPreference.Slim ? UnitMeshBodyVariant.Slim : UnitMeshBodyVariant.Stocky;

	private sealed record TargetUse(EquipmentUnitCatalogEntry Entry, EquipmentUnitPart Part);
	private sealed record TargetUnitGroup(AssetKey UnitAssetKey, TargetUse Representative, IReadOnlyList<TargetUse> Uses);
	private static async ValueTask<IReadOnlyDictionary<AssetKey, IReadOnlyList<string>>> ReadSharedArchiveIdsAsync(SqliteConnection connection, IReadOnlyList<AssetKey> unitKeys, CancellationToken cancellationToken)
	{
		if (unitKeys.Count == 0) return new Dictionary<AssetKey, IReadOnlyList<string>>();
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT p.unit_type_id,p.unit_file_id,p.archive_id
FROM game_data_unit_parts p
JOIN archives a ON a.archive_id=p.archive_id
WHERE (lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%')
  AND p.unit_type_id=$unitType
  AND p.unit_file_id IN (SELECT value FROM json_each($unitIds))
GROUP BY p.unit_type_id,p.unit_file_id,p.archive_id
ORDER BY p.unit_file_id,p.archive_id;";
		command.Parameters.AddWithValue("$unitType", unchecked((long)AdaptationPatchUnitMeshReader.UnitTypeId));
		command.Parameters.AddWithValue("$unitIds", JsonSerializer.Serialize(unitKeys.Select(key => unchecked((long)key.FileId))));
		var result = new Dictionary<AssetKey, List<string>>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var key = new AssetKey(unchecked((ulong)reader.GetInt64(0)), unchecked((ulong)reader.GetInt64(1)));
			if (!result.TryGetValue(key, out var archives)) result[key] = archives = new List<string>();
			archives.Add(reader.GetString(2));
		}
		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
	}
}

internal static class EquipmentUnitPartGeometryOrdering
{
	public static IOrderedEnumerable<T> ApplyGeometryOrder<T>(this IEnumerable<T> source, Func<T, EquipmentUnitPart> selector)
		=> source
			.OrderByDescending(item => UnitGeometryRanker.GetRank(selector(item).GeometryQuality))
			.ThenByDescending(item => selector(item).TriangleCount)
			.ThenByDescending(item => selector(item).VertexCount)
			.ThenByDescending(item => selector(item).StoredBytes);
}
