using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
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
		command.CommandText = @"
SELECT a.archive_id,a.category,a.display_name,
			 p.unit_type_id,p.unit_file_id,p.mesh_info_index,p.mesh_id,p.part_kind,p.part_layer,p.body_variant,p.semantic_name,p.piece_type,p.confidence,p.vertex_count,p.triangle_count,p.geometry_quality,
			 COALESCE((SELECT MAX(e.toc_data_size + e.stream_size + e.gpu_resource_size)
								 FROM archive_entries e
								 WHERE e.archive_id=a.archive_id
									 AND e.type_id=p.unit_type_id
									 AND e.file_id=p.unit_file_id), 0) AS stored_bytes
FROM archives a
JOIN game_data_unit_parts p ON p.archive_id=a.archive_id
WHERE lower(a.category) IN ('armor','helmet')
  AND p.is_visual=1 AND p.is_lod=0
  AND EXISTS(SELECT 1 FROM archive_entries e WHERE e.archive_id=a.archive_id AND e.type_id=p.unit_type_id AND e.file_id=p.unit_file_id)
	  AND ($unitIds IS NULL OR p.unit_file_id IN (SELECT value FROM json_each($unitIds)))
ORDER BY CASE lower(a.category) WHEN 'armor' THEN 0 ELSE 1 END,a.display_name,a.archive_id,p.unit_file_id,p.mesh_info_index;";
		command.Parameters.AddWithValue("$unitIds", unitIds is null ? DBNull.Value : JsonSerializer.Serialize(unitIds));

		var rawParts = new List<(string archiveId, string category, string name, EquipmentUnitPart part)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var unitKey = new AssetKey(unchecked((ulong)reader.GetInt64(3)), unchecked((ulong)reader.GetInt64(4)));
			rawParts.Add((
				reader.GetString(0), reader.GetString(1), reader.GetString(2),
				new EquipmentUnitPart(
					unitKey,
					reader.GetInt32(5),
					unchecked((uint)reader.GetInt64(6)),
					(UnitMeshPartKind)reader.GetInt32(7),
					(UnitMeshPartLayer)reader.GetInt32(8),
					(UnitMeshBodyVariant)reader.GetInt32(9),
					reader.GetString(10), reader.GetInt32(12), Array.Empty<string>())
					{
						PieceType = reader.GetString(11),
						VertexCount = reader.GetInt32(13),
						TriangleCount = reader.GetInt32(14),
						GeometryQuality = (UnitMeshGeometryQuality)reader.GetInt32(15),
						StoredBytes = reader.GetInt64(16)
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
		var assignments = new Dictionary<AssetKey, (EquipmentUnitPart? Source, bool IsManual, bool IsSuppressed, int HitCount)>();
		foreach (var target in targetsByUnit)
		{
			if (manualByTarget.TryGetValue(target.UnitAssetKey, out var manual))
			{
				var sourcePart = sourceParts.FirstOrDefault(part => part.UnitAssetKey == manual.SourceUnitAssetKey);
				if (sourcePart is null)
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ManualSourceUnavailable", $"手动来源已不在当前筛选范围：目标 Unit 0x{target.UnitAssetKey.FileId:x16}。"));
				assignments[target.UnitAssetKey] = (sourcePart, sourcePart is not null, false, target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
			}
			else if (suppressedTargets.Contains(target.UnitAssetKey))
			{
				assignments[target.UnitAssetKey] = (null, false, true, 0);
			}
		}
		if (!manualMode)
		{
			var sourceProfiles = BuildSourceBodyProfiles(sourceParts, sourceCategoryByUnit);
			foreach (var archive in targets.Select(target => target.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				plannerDebug.Add($"[ARCHIVE-BEGIN] {archive}");
				var usedSourceUnits = new HashSet<AssetKey>();
				var allArchiveTargets = targetsByUnit
					.Where(target => target.Uses.Any(use => string.Equals(use.Entry.ArchiveId, archive, StringComparison.OrdinalIgnoreCase)))
					.ToArray();
				var archiveTargets = allArchiveTargets
					.Where(target => !assignments.ContainsKey(target.UnitAssetKey))
					.ToArray();
				plannerDebug.Add($"[ARCHIVE-TARGETS] Archive={archive} All={allArchiveTargets.Length} Unassigned={archiveTargets.Length}");
				foreach (var target in archiveTargets.OrderBy(target => target.Representative.Part.PartKind).ThenBy(target => target.Representative.Part.Layer).ThenBy(target => target.UnitAssetKey.FileId))
					plannerDebug.Add($"[TARGET] Unit=0x{target.UnitAssetKey.FileId:x16} Part={target.Representative.Part.PartKind} Layer={target.Representative.Part.Layer} Variant={target.Representative.Part.BodyVariant} Bytes={target.Representative.Part.StoredBytes} Semantic={target.Representative.Part.SemanticName}");

				// Head is deliberately independent from the body state machine: one archive
				// receives its largest visible Head Unit, and nothing else competes with it.
				var headProfile = sourceProfiles.FirstOrDefault(profile => profile.PartKind == UnitMeshPartKind.Head);
				if (headProfile is not null)
				{
					var target = archiveTargets.Where(target => target.Representative.Part.PartKind == UnitMeshPartKind.Head)
						.ApplyGeometryOrder(target => target.Representative.Part)
						.FirstOrDefault();
					var headSource = SelectSourceForTarget(headProfile, target, usedSourceUnits, bodyVariantPreference, selectedSourceBodyVariant);
					if (target is not null && headSource is not null)
					{
						assignments[target.UnitAssetKey] = (headSource.Part, false, false,
							target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
						usedSourceUnits.Add(headSource.UnitAssetKey);
						plannerDebug.Add($"[ASSIGN] Archive={archive} Target=0x{target.UnitAssetKey.FileId:x16} Source=0x{headSource.UnitAssetKey.FileId:x16} Part=Head Variant={headSource.Part.BodyVariant}");
					}
				}

				var provisionalBodyTargets = new List<(SourceBodyProfile Profile, TargetUnitGroup Target)>();
				foreach (var profile in sourceProfiles.Where(profile => profile.PartKind != UnitMeshPartKind.Head))
				{
					var coverage = ReadArchiveTargetCoverage(allArchiveTargets, archive, profile, assignments);
					if (coverage.IsComplete)
					{
						plannerDebug.Add($"[COVERED] Archive={archive} Part={profile.PartKind} Coverage={coverage}");
						continue;
					}

					var candidates = archiveTargets
						.Where(target => target.Representative.Part.PartKind == profile.PartKind)
						.Where(target => SemanticFamilyKey(target.Representative.Part) == profile.SemanticFamily)
						.ToArray();
					if (coverage.MissingConcreteVariant is { } missingVariant)
					{
						var completion = SelectMissingBodyTarget(profile, candidates, missingVariant);
						if (completion is not null)
						{
							provisionalBodyTargets.Add((profile, completion));
							plannerDebug.Add($"[COVERAGE-COMPLEMENT] Archive={archive} Part={profile.PartKind} Missing={missingVariant} Target=0x{completion.UnitAssetKey.FileId:x16}");
						}
						continue;
					}

					provisionalBodyTargets.AddRange(SelectBodyTargets(profile, candidates, layerPreference).Select(target => (Profile: profile, Target: target)));
				}
				var archiveBodyVariant = ResolveArchiveBodyVariant(provisionalBodyTargets, bodyVariantPreference);
				foreach (var provisional in provisionalBodyTargets)
				{
					plannerDebug.Add($"[CANDIDATES] Archive={archive} Part={provisional.Profile.PartKind} Target=0x{provisional.Target.UnitAssetKey.FileId:x16} TargetLayer={provisional.Target.Representative.Part.Layer} TargetVariant={provisional.Target.Representative.Part.BodyVariant}");
					AppendSourceCandidateDiagnostics(plannerDebug, provisional.Profile, provisional.Target, usedSourceUnits, bodyVariantPreference, selectedSourceBodyVariant, archiveBodyVariant);
					var bodySource = SelectSourceForTarget(provisional.Profile, provisional.Target, usedSourceUnits,
						bodyVariantPreference, selectedSourceBodyVariant, archiveBodyVariant);
					if (bodySource is null) continue;
					assignments[provisional.Target.UnitAssetKey] = (bodySource.Part, false, false,
						provisional.Target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
						usedSourceUnits.Add(bodySource.UnitAssetKey);
						plannerDebug.Add($"[ASSIGN] Archive={archive} Target=0x{provisional.Target.UnitAssetKey.FileId:x16} Source=0x{bodySource.UnitAssetKey.FileId:x16} Part={bodySource.Part.PartKind} Layer={bodySource.Part.Layer} Variant={bodySource.Part.BodyVariant}");
				}

				// Deferred regions use the largest two distinct target body variants as the
				// final coverage fallback. This is intentionally after normal selection so
				// fallback never steals a better same-layer or Any target.
				foreach (var profile in sourceProfiles.Where(profile => profile.PartKind != UnitMeshPartKind.Head))
				{
					// A shared Unit selected by an earlier archive is still coverage for this
					// archive. Do not let the fallback path reintroduce an Armor/Any Unit
					// after that shared coverage already supplies an Any or Slim+Stocky pair.
					var coverage = ReadArchiveTargetCoverage(allArchiveTargets, archive, profile, assignments);
					if (coverage.IsComplete)
					{
						plannerDebug.Add($"[FALLBACK-SKIP-COVERED] Archive={archive} Part={profile.PartKind} Coverage={coverage}");
						continue;
					}

					// A body region that already received a provisional hit is complete.
					// Do not reinterpret every remaining target variant in that region as
					// another fallback hit; otherwise two source torso Units expand into
					// four target torso replacements (armor + undergarment).
					if (provisionalBodyTargets.Any(item => item.Profile.PartKind == profile.PartKind
						&& item.Profile.SemanticFamily == profile.SemanticFamily
						&& assignments.ContainsKey(item.Target.UnitAssetKey))) continue;

					var unresolved = archiveTargets
						.Where(target => !assignments.ContainsKey(target.UnitAssetKey))
						.Where(target => target.Representative.Part.PartKind == profile.PartKind)
						.Where(target => SemanticFamilyKey(target.Representative.Part) == profile.SemanticFamily)
						.ApplyGeometryOrder(target => target.Representative.Part)
						.ThenBy(target => target.UnitAssetKey.FileId)
						.ToArray();
					var fallbackTargets = unresolved
						.GroupBy(target => target.Representative.Part.BodyVariant)
						.Select(group => group.First())
						.Take(2)
						.ToArray();
					foreach (var target in fallbackTargets)
					{
						// Final fallback may only use a source Unit that is still unused in
						// this archive. Resetting the set here would turn the fallback into
						// unlimited source reuse and inflate every part's hit count.
						var fallbackSource = SelectSourceForTarget(profile, target, usedSourceUnits, bodyVariantPreference, selectedSourceBodyVariant);
						if (fallbackSource is null) continue;
						assignments[target.UnitAssetKey] = (fallbackSource.Part, false, false,
							target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
						usedSourceUnits.Add(fallbackSource.UnitAssetKey);
						plannerDebug.Add($"[FALLBACK-ASSIGN] Archive={archive} Target=0x{target.UnitAssetKey.FileId:x16} Source=0x{fallbackSource.UnitAssetKey.FileId:x16} Part={fallbackSource.Part.PartKind} Variant={fallbackSource.Part.BodyVariant}");
					}
				}
				plannerDebug.Add($"[ARCHIVE-END] {archive} UsedSources={string.Join(",", usedSourceUnits.Select(key => $"0x{key.FileId:x16}"))}");
			}

			ApplySharedArchiveVariantStrategies(
				targetsByUnit,
				assignments,
				sourceProfiles,
				bodyVariantPreference,
				selectedSourceBodyVariant,
				plannerDebug);

			// Shared-target reconciliation can change an assignment to accommodate a
			// reverse-shaped archive. Apply each archive's fixed-shape constraint last,
			// so Target=Any -> concrete-source normalization remains authoritative.
			ApplyFixedArchiveVariantStrategies(
				targetsByUnit,
				assignments,
				sourceProfiles,
				bodyVariantPreference,
				selectedSourceBodyVariant,
				plannerDebug);

			// A target Unit shared by several selected archives is one physical output.
			// The first archive that selected it owns the source assignment; later rounds
			// see it as already assigned and therefore cannot create a second mapping.
		}

		var mappings = new List<CrossArmorTransferMapping>();
		var impacts = new List<CrossArmorTransferImpact>();
		foreach (var physicalTarget in targetsByUnit)
		{
			var targetUse = physicalTarget.Representative;
			var targetPart = targetUse.Part;
			var (sourcePart, isManual, isSuppressed, hitCount) = assignments.GetValueOrDefault(physicalTarget.UnitAssetKey);
			var willReplace = sourcePart is not null;
			var usedByIds = physicalTarget.Uses.Select(item => item.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var usedByNames = physicalTarget.Uses.Select(item => item.Entry.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			mappings.Add(new CrossArmorTransferMapping(
				new CrossArmorPhysicalTargetKey(physicalTarget.UnitAssetKey), targetPart, sourcePart, willReplace,
				willReplace
					? (isManual ? "强制命中" : "命中")
					: (isSuppressed ? "强制隐藏" : "隐藏"),
				usedByIds, usedByNames, isManual, isSuppressed) { HitCount = hitCount });
			if (!willReplace) continue;
			foreach (var sharedArchiveId in targetPart.SharedArchiveIds.Where(id => !usedByIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
			{
				var impacted = sourceCandidates.FirstOrDefault(candidate => string.Equals(candidate.ArchiveId, sharedArchiveId, StringComparison.OrdinalIgnoreCase));
				if (impacted is not null) impacts.Add(new CrossArmorTransferImpact(impacted.ArchiveId, impacted.DisplayName, targetPart.UnitAssetKey, targetPart.PartKind, targetPart.Layer));
			}
		}
		if (mappings.Count == 0) issues.Add(new CoreIssue(CoreIssueSeverity.Error, "NoTargetParts", "所选目标没有可识别的可见非 LOD 部件。"));
		plannerDebug.Add($"[RESULT] Mappings={mappings.Count} Hits={mappings.Count(mapping => mapping.WillReplace)} Hidden={mappings.Count(mapping => !mapping.WillReplace)}");
		if (writePlannerDebugLog)
			WritePlannerDebugLog(plannerDebug);
		return ValueTask.FromResult(new CrossArmorTransferPlan(sourceCandidates, source, targets, mappings, impacts.Distinct().ToArray(), issues));
	}

	private static void MarkSourceUnitUsed(TargetUnitGroup target, AssetKey sourceUnitKey, IDictionary<string, HashSet<AssetKey>> usedByArchive)
	{
		foreach (var archiveId in target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!usedByArchive.TryGetValue(archiveId, out var used)) usedByArchive[archiveId] = used = new HashSet<AssetKey>();
			used.Add(sourceUnitKey);
		}
	}

	private static IReadOnlyList<SourceBodyProfile> BuildSourceBodyProfiles(
		IReadOnlyList<EquipmentUnitPart> sourceParts,
		IReadOnlyDictionary<AssetKey, string> sourceCategoryByUnit)
	{
		var sourceUnitCountByPart = sourceParts
			.GroupBy(part => part.PartKind)
			.ToDictionary(group => group.Key, group => group.Select(part => part.UnitAssetKey).Distinct().Count());
		return sourceParts
			.GroupBy(part => (part.PartKind, SemanticFamily: SemanticFamilyKey(part)))
		.Select(group =>
		{
			var parts = group.ApplyGeometryOrder(part => part).ThenBy(part => part.UnitAssetKey.FileId).ToArray();
			var concreteVariants = parts
				.Select(part => part.BodyVariant)
				.Where(variant => variant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
				.Distinct()
				.ToArray();
			var canActAsEffectiveAny = !parts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Any)
				&& concreteVariants.Length == 1
				&& sourceUnitCountByPart.GetValueOrDefault(group.Key.PartKind) == 1;
			return new SourceBodyProfile(
				group.Key.PartKind,
				group.Key.SemanticFamily,
				canActAsEffectiveAny,
				parts.Select(part => sourceCategoryByUnit.GetValueOrDefault(part.UnitAssetKey) ?? string.Empty)
					.Where(category => category.Length != 0)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray(),
				parts);
		})
		.ToArray();
	}

	private async ValueTask EnsureCurrentSchemaAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await Sqlite.SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	private static SourceBodyProfile? FindSourceProfile(
		IReadOnlyList<SourceBodyProfile> sourceProfiles,
		EquipmentUnitPart targetPart)
	{
		var samePartProfiles = sourceProfiles.Where(profile => profile.PartKind == targetPart.PartKind).ToArray();
		var exact = samePartProfiles.FirstOrDefault(profile => profile.SemanticFamily == SemanticFamilyKey(targetPart));
		if (exact is not null) return exact;

		// Keep the former part-only behavior only for an unambiguous legacy source:
		// once a source exposes several semantic families, guessing across them is more
		// dangerous than leaving the target hidden for the user to map manually.
		return samePartProfiles.Length == 1 ? samePartProfiles[0] : null;
	}

	private static string SemanticFamilyKey(EquipmentUnitPart part)
		=> SemanticFamily(part.SemanticName) ?? string.Empty;

	private static string DisplaySemanticFamily(string semanticFamily)
		=> string.IsNullOrEmpty(semanticFamily) ? "Unknown" : semanticFamily;

	private static ArchiveTargetCoverage ReadArchiveTargetCoverage(
		IReadOnlyList<TargetUnitGroup> allArchiveTargets,
		string archiveId,
		SourceBodyProfile profile,
		IReadOnlyDictionary<AssetKey, (EquipmentUnitPart? Source, bool IsManual, bool IsSuppressed, int HitCount)> assignments)
	{
		var variants = allArchiveTargets
			.Where(target => target.Uses.Any(use => string.Equals(use.Entry.ArchiveId, archiveId, StringComparison.OrdinalIgnoreCase)))
			.Where(target => target.Representative.Part.PartKind == profile.PartKind)
			.Where(target => SemanticFamilyKey(target.Representative.Part) == profile.SemanticFamily)
			.Where(target => assignments.TryGetValue(target.UnitAssetKey, out var assignment) && assignment.Source is not null)
			.Select(target => target.Representative.Part.BodyVariant)
			.ToHashSet();
		var hasAny = variants.Contains(UnitMeshBodyVariant.Any);
		var hasSlim = variants.Contains(UnitMeshBodyVariant.Slim);
		var hasStocky = variants.Contains(UnitMeshBodyVariant.Stocky);
		return new ArchiveTargetCoverage(hasAny, hasSlim, hasStocky);
	}

	private static TargetUnitGroup? SelectMissingBodyTarget(
		SourceBodyProfile profile,
		IReadOnlyList<TargetUnitGroup> candidates,
		UnitMeshBodyVariant missingVariant)
	{
		var sourceLayers = profile.Parts.Select(part => part.Layer).Where(layer => layer != UnitMeshPartLayer.Unknown).ToHashSet();
		return candidates
			.Where(target => target.Representative.Part.BodyVariant == missingVariant)
			.OrderByDescending(target => sourceLayers.Contains(target.Representative.Part.Layer))
			.ThenByDescending(target => GeometryRank(target.Representative.Part))
			.ThenByDescending(target => target.Representative.Part.TriangleCount)
			.ThenByDescending(target => target.Representative.Part.VertexCount)
			.ThenByDescending(target => target.Representative.Part.StoredBytes)
			.ThenBy(target => target.UnitAssetKey.FileId)
			.FirstOrDefault();
	}

	private static IReadOnlyList<TargetUnitGroup> SelectBodyTargets(
		SourceBodyProfile profile,
		IReadOnlyList<TargetUnitGroup> candidates,
		CrossArmorLayerPreference layerPreference)
	{
		var categoryCandidates = candidates.ToArray();
		if (categoryCandidates.Length == 0) return Array.Empty<TargetUnitGroup>();

		var sourceLayers = profile.Parts.Select(part => part.Layer).Where(layer => layer != UnitMeshPartLayer.Unknown).ToHashSet();
		var sameLayer = categoryCandidates.Where(target => sourceLayers.Contains(target.Representative.Part.Layer)).ToArray();
		var ranked = sameLayer.Length != 0 ? sameLayer : categoryCandidates;
		var slim = ranked.Where(target => target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Slim)
			.ApplyGeometryOrder(target => target.Representative.Part).ThenBy(target => target.UnitAssetKey.FileId).FirstOrDefault();
		var stocky = ranked.Where(target => target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Stocky)
			.ApplyGeometryOrder(target => target.Representative.Part).ThenBy(target => target.UnitAssetKey.FileId).FirstOrDefault();

		var any = ranked
			.Where(target => target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Any)
			.ApplyGeometryOrder(target => target.Representative.Part)
			.ThenBy(target => target.UnitAssetKey.FileId)
			.FirstOrDefault();
		// A real Any source is a universal mesh, not a spare concrete body shape.
		// In a composite source package it must reserve the matching semantic Any
		// target before the concrete-pair rule can consume a neighbouring accessory.
		if (profile.Parts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Any) && any is not null) return [any];
		if (slim is not null && stocky is not null) return [slim, stocky];
		if (any is not null) return [any];

		// A same-layer hit is only a priority, not proof that the body region is
		// complete. If the source contains both concrete body shapes, continue
		// searching all layers for the missing shape before accepting a partial hit.
		// This covers pairs such as source Undergarment(Slim+Stocky) and target
		// Undergarment(Stocky) + Armor(Slim), without changing the normal same-layer
		// preference when both shapes already exist there.
		var sourceVariants = profile.Parts
			.Select(part => part.BodyVariant)
			.Where(variant => variant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
			.Distinct()
			.ToHashSet();
		var sourceHasAny = profile.Parts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Any);
		var only = slim ?? stocky;
		if (only is not null && (sourceVariants.Count == 2 || sourceHasAny))
		{
			var missingVariant = only.Representative.Part.BodyVariant == UnitMeshBodyVariant.Slim
				? UnitMeshBodyVariant.Stocky
				: UnitMeshBodyVariant.Slim;
			var crossLayerConcrete = categoryCandidates
				.Where(target => target.Representative.Part.BodyVariant == missingVariant)
				.Where(target => target.UnitAssetKey != only.UnitAssetKey)
				.OrderByDescending(target => sourceLayers.Contains(target.Representative.Part.Layer))
				.ThenByDescending(target => GeometryRank(target.Representative.Part))
				.ThenByDescending(target => target.Representative.Part.TriangleCount)
				.ThenByDescending(target => target.Representative.Part.VertexCount)
				.ThenByDescending(target => target.Representative.Part.StoredBytes)
				.ThenBy(target => target.UnitAssetKey.FileId)
				.FirstOrDefault();
			if (crossLayerConcrete is not null)
				return [only, crossLayerConcrete];

			var crossLayerAny = categoryCandidates
				.Where(target => target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Any)
				.Where(target => target.UnitAssetKey != only.UnitAssetKey)
				.OrderByDescending(target => sourceLayers.Contains(target.Representative.Part.Layer))
				.ThenByDescending(target => GeometryRank(target.Representative.Part))
				.ThenByDescending(target => target.Representative.Part.TriangleCount)
				.ThenByDescending(target => target.Representative.Part.VertexCount)
				.ThenByDescending(target => target.Representative.Part.StoredBytes)
				.ThenBy(target => target.UnitAssetKey.FileId)
				.FirstOrDefault();
			if (crossLayerAny is not null)
				return [only, crossLayerAny];
		}

		// Layer preference is only a deterministic last ordering tie-breaker here. The
		// source layer remains authoritative whenever a same-layer target exists.
		return ranked
			.OrderByDescending(target => target.Representative.Part.Layer == ToPartLayer(layerPreference))
			.ThenByDescending(target => GeometryRank(target.Representative.Part))
			.ThenByDescending(target => target.Representative.Part.TriangleCount)
			.ThenByDescending(target => target.Representative.Part.VertexCount)
			.ThenByDescending(target => target.Representative.Part.StoredBytes)
			.Take(1)
			.ToArray();
	}

	private static SourceSelection? SelectSourceForTarget(
		SourceBodyProfile profile,
		TargetUnitGroup? target,
		IReadOnlySet<AssetKey> usedSourceUnits,
		CrossArmorBodyVariantPreference preference,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		UnitMeshBodyVariant? archiveBodyVariant = null,
		UnitMeshBodyVariant? forcedSourceVariant = null)
	{
		if (target is null) return null;
		var targetVariant = target.Representative.Part.BodyVariant;
		var available = profile.Parts
			.Where(part => selectedSourceBodyVariant is null or UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any
				|| part.BodyVariant == selectedSourceBodyVariant || part.BodyVariant == UnitMeshBodyVariant.Any)
			.Where(part => targetVariant is UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Unknown
				|| forcedSourceVariant.HasValue || IsEffectiveAny(profile) || part.BodyVariant == targetVariant || part.BodyVariant == UnitMeshBodyVariant.Any)
			.Where(part => !forcedSourceVariant.HasValue
				|| part.BodyVariant == forcedSourceVariant.Value
				|| part.BodyVariant == UnitMeshBodyVariant.Any)
			.ToArray();
		if (available.Length == 0) return null;
		var preferred = PreferredBodyVariant(preference);
		var targetLayer = target.Representative.Part.Layer;
		var exactLayer = available.Where(part => part.Layer == targetLayer).ToArray();
		if (exactLayer.Length != 0) available = exactLayer;
		var selected = available
			.OrderBy(part => forcedSourceVariant.HasValue
				? part.BodyVariant == forcedSourceVariant.Value ? 0 : 1
				: (targetVariant is UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Unknown) && archiveBodyVariant.HasValue
				? part.BodyVariant == archiveBodyVariant.Value ? 0 : part.BodyVariant == UnitMeshBodyVariant.Any ? 1 : 2
				: IsEffectiveAny(profile) ? 0 : part.BodyVariant == targetVariant ? 0 : part.BodyVariant == UnitMeshBodyVariant.Any ? 1 : part.BodyVariant == preferred ? 2 : 3)
			.ThenByDescending(part => GeometryRank(part))
			.ThenByDescending(part => part.TriangleCount)
			.ThenByDescending(part => part.VertexCount)
			.ThenByDescending(part => part.StoredBytes)
			.ThenBy(part => part.UnitAssetKey.FileId)
			.First();
		return new SourceSelection(selected.UnitAssetKey, selected);
	}

	private static void ApplySharedArchiveVariantStrategies(
		IReadOnlyList<TargetUnitGroup> targets,
		IDictionary<AssetKey, (EquipmentUnitPart? Source, bool IsManual, bool IsSuppressed, int HitCount)> assignments,
		IReadOnlyList<SourceBodyProfile> sourceProfiles,
		CrossArmorBodyVariantPreference preference,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		ICollection<string> plannerDebug)
	{
		var reverseArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var target in targets.Where(target => target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
		{
			if (!assignments.TryGetValue(target.UnitAssetKey, out var assignment) || assignment.Source is null)
				continue;
			var sourceVariant = assignment.Source.BodyVariant;
			if (sourceVariant is not (UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky))
				continue;

			foreach (var use in target.Uses)
			{
				if (use.Part.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky
					&& use.Part.BodyVariant != sourceVariant)
					reverseArchives.Add(use.Entry.ArchiveId);
			}
		}

		foreach (var target in targets.Where(target => target.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1))
		{
			var archiveId = target.Uses[0].Entry.ArchiveId;
			if (!reverseArchives.Contains(archiveId)
				|| !assignments.TryGetValue(target.UnitAssetKey, out var assignment)
				|| assignment.IsManual
				|| assignment.IsSuppressed)
				continue;

			var targetVariant = target.Representative.Part.BodyVariant;
			var forcedVariant = targetVariant switch
			{
				UnitMeshBodyVariant.Slim => UnitMeshBodyVariant.Stocky,
				UnitMeshBodyVariant.Stocky => UnitMeshBodyVariant.Slim,
				_ => PreferredBodyVariant(preference)
			};
			var profile = FindSourceProfile(sourceProfiles, target.Representative.Part);
			if (profile is null) continue;
			var source = SelectSourceForTarget(profile, target, new HashSet<AssetKey>(), preference, selectedSourceBodyVariant, forcedSourceVariant: forcedVariant);
			if (source is null) continue;
			assignments[target.UnitAssetKey] = (source.Part, assignment.IsManual, assignment.IsSuppressed, assignment.HitCount);
			plannerDebug.Add($"[SHARED-REVERSE] Archive={archiveId} Target=0x{target.UnitAssetKey.FileId:x16} TargetVariant={targetVariant} Source=0x{source.UnitAssetKey.FileId:x16} SourceVariant={source.Part.BodyVariant}");
		}
	}

	private static void ApplyFixedArchiveVariantStrategies(
		IReadOnlyList<TargetUnitGroup> targets,
		IDictionary<AssetKey, (EquipmentUnitPart? Source, bool IsManual, bool IsSuppressed, int HitCount)> assignments,
		IReadOnlyList<SourceBodyProfile> sourceProfiles,
		CrossArmorBodyVariantPreference preference,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		ICollection<string> plannerDebug)
	{
		// Target=Any can only be rendered by one concrete source shape when that
		// logical source part has no real Any mesh. That makes this archive a
		// single-shape outfit: every other body part must use the same concrete
		// source shape (or a real Any source) before shared-target reconciliation.
		var fixedVariants = targets
			.SelectMany(target => target.Uses.Select(use => (Target: target, ArchiveId: use.Entry.ArchiveId)))
			.Where(item => item.Target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Any)
			.Where(item => assignments.TryGetValue(item.Target.UnitAssetKey, out var assignment)
				&& !assignment.IsManual
				&& !assignment.IsSuppressed
				&& assignment.Source?.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
			.GroupBy(item => item.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group
					.Select(item => assignments[item.Target.UnitAssetKey].Source!.BodyVariant)
					.GroupBy(variant => variant)
					.OrderByDescending(variants => variants.Count())
					.ThenBy(variants => variants.Key == PreferredBodyVariant(preference) ? 0 : 1)
					.First().Key,
				StringComparer.OrdinalIgnoreCase);

		foreach (var (archiveId, fixedVariant) in fixedVariants)
		{
			plannerDebug.Add($"[FIXED-VARIANT] Archive={archiveId} SourceVariant={fixedVariant}");
		}

		foreach (var target in targets)
		{
			var owningArchives = target.Uses
				.Select(use => use.Entry.ArchiveId)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var requiredVariants = owningArchives
				.Where(fixedVariants.ContainsKey)
				.Select(archiveId => fixedVariants[archiveId])
				.Distinct()
				.ToArray();
			if (requiredVariants.Length == 0) continue;

			if (requiredVariants.Length > 1)
			{
				plannerDebug.Add($"[FIXED-VARIANT-SKIP] Target=0x{target.UnitAssetKey.FileId:x16} Archives={string.Join(',', owningArchives)} Reason=ConflictingArchiveVariants Variants={string.Join(',', requiredVariants)}");
				continue;
			}

			var fixedVariant = requiredVariants[0];
			var archiveLabel = string.Join(',', owningArchives.Where(fixedVariants.ContainsKey));
			if (!assignments.TryGetValue(target.UnitAssetKey, out var assignment))
			{
				plannerDebug.Add($"[FIXED-VARIANT-SKIP] Archive={archiveLabel} Target=0x{target.UnitAssetKey.FileId:x16} Reason=NoAssignment");
				continue;
			}
			if (assignment.Source is null || assignment.IsManual || assignment.IsSuppressed)
			{
				plannerDebug.Add($"[FIXED-VARIANT-SKIP] Archive={archiveLabel} Target=0x{target.UnitAssetKey.FileId:x16} Reason={(assignment.Source is null ? "NoSource" : assignment.IsManual ? "Manual" : "Suppressed")}");
				continue;
			}

			var profile = FindSourceProfile(sourceProfiles, target.Representative.Part);
			if (profile is null)
			{
				plannerDebug.Add($"[FIXED-VARIANT-SKIP] Archive={archiveLabel} Target=0x{target.UnitAssetKey.FileId:x16} Reason=NoSourceProfile");
				continue;
			}
			var source = SelectSourceForTarget(profile, target, new HashSet<AssetKey>(), preference, selectedSourceBodyVariant, forcedSourceVariant: fixedVariant);
			if (source is null)
			{
				plannerDebug.Add($"[FIXED-VARIANT-SKIP] Archive={archiveLabel} Target=0x{target.UnitAssetKey.FileId:x16} Reason=NoCompatible{fixedVariant}OrAnySource");
				continue;
			}
			assignments[target.UnitAssetKey] = (source.Part, assignment.IsManual, assignment.IsSuppressed, assignment.HitCount);
			plannerDebug.Add($"[FIXED-VARIANT-ASSIGN] Archive={archiveLabel} Target=0x{target.UnitAssetKey.FileId:x16} TargetVariant={target.Representative.Part.BodyVariant} Source=0x{source.UnitAssetKey.FileId:x16} SourceVariant={source.Part.BodyVariant}");
		}
	}

	private static void AppendSourceCandidateDiagnostics(
		ICollection<string> log,
		SourceBodyProfile profile,
		TargetUnitGroup target,
		IReadOnlySet<AssetKey> usedSourceUnits,
		CrossArmorBodyVariantPreference preference,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		UnitMeshBodyVariant? archiveBodyVariant)
	{
		foreach (var part in profile.Parts.ApplyGeometryOrder(part => part).ThenBy(part => part.UnitAssetKey.FileId))
		{
			var reasons = new List<string>();
			if (usedSourceUnits.Contains(part.UnitAssetKey)) reasons.Add("SourceAlreadyUsed");
			if (selectedSourceBodyVariant is not null and not UnitMeshBodyVariant.Unknown and not UnitMeshBodyVariant.Any
				&& part.BodyVariant != selectedSourceBodyVariant && part.BodyVariant != UnitMeshBodyVariant.Any)
				reasons.Add("SelectedSourceVariantMismatch");
			if (archiveBodyVariant.HasValue && part.BodyVariant != archiveBodyVariant.Value && part.BodyVariant != UnitMeshBodyVariant.Any)
				reasons.Add("ArchiveVariantMismatch");
			if (part.Layer != target.Representative.Part.Layer) reasons.Add("LayerMismatch");
			if (target.Representative.Part.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky
				&& !archiveBodyVariant.HasValue && !IsEffectiveAny(profile)
				&& part.BodyVariant != target.Representative.Part.BodyVariant && part.BodyVariant != UnitMeshBodyVariant.Any)
				reasons.Add("TargetVariantMismatch");
			log.Add($"[CANDIDATE] Target=0x{target.UnitAssetKey.FileId:x16} Source=0x{part.UnitAssetKey.FileId:x16} Layer={part.Layer} Variant={part.BodyVariant} Bytes={part.StoredBytes} Reasons={(reasons.Count == 0 ? "Compatible" : string.Join('|', reasons))}");
		}
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

	private static UnitMeshBodyVariant? ResolveArchiveBodyVariant(
		IReadOnlyList<(SourceBodyProfile Profile, TargetUnitGroup Target)> provisionalTargets,
		CrossArmorBodyVariantPreference preference)
	{
		var requiresConcreteVariant = provisionalTargets.Any(item =>
			item.Target.Representative.Part.BodyVariant == UnitMeshBodyVariant.Any && !item.Profile.Parts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Any));
		return requiresConcreteVariant ? PreferredBodyVariant(preference) : null;
	}

	private static UnitMeshBodyVariant? ResolveAssignedArchiveBodyVariant(
		IReadOnlyList<TargetUnitGroup> archiveTargets,
		IReadOnlyDictionary<AssetKey, (EquipmentUnitPart? Source, bool IsManual, bool IsSuppressed, int HitCount)> assignments,
		CrossArmorBodyVariantPreference preference)
	{
		var variants = archiveTargets
			.Where(target => assignments.TryGetValue(target.UnitAssetKey, out var assignment) && assignment.Source is not null)
			.Select(target => assignments[target.UnitAssetKey].Source!.BodyVariant)
			.Where(variant => variant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
			.GroupBy(variant => variant)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key == PreferredBodyVariant(preference) ? 0 : 1)
			.Select(group => (UnitMeshBodyVariant?)group.Key)
			.FirstOrDefault();
		return variants;
	}

	private static bool IsEffectiveAny(SourceBodyProfile profile)
		=> profile.CanActAsEffectiveAny;

	private static UnitMeshPartLayer ToPartLayer(CrossArmorLayerPreference preference)
		=> preference switch
		{
			CrossArmorLayerPreference.Undergarment => UnitMeshPartLayer.Undergarment,
			CrossArmorLayerPreference.Accessory => UnitMeshPartLayer.Accessory,
			_ => UnitMeshPartLayer.Armor
		};

	private static IEnumerable<TargetUnitGroup> OrderTargetsForAssignment(
		IReadOnlyList<TargetUnitGroup> targets,
		IReadOnlyDictionary<AssetKey, CrossArmorManualMapping> manualMappings)
		=> targets
			.OrderByDescending(group => manualMappings.ContainsKey(group.UnitAssetKey))
			.ThenByDescending(group => GeometryRank(group.Representative.Part))
			.ThenByDescending(group => group.Representative.Part.TriangleCount)
			.ThenByDescending(group => group.Representative.Part.VertexCount)
			.ThenByDescending(group => group.Representative.Part.StoredBytes)
			.ThenBy(group => group.UnitAssetKey.FileId)
			;

	private static IReadOnlyList<SourceHitPool> BuildSourceHitPools(
		IReadOnlyList<EquipmentUnitPart> sourceParts,
		IReadOnlyDictionary<AssetKey, string> sourceCategoryByUnit)
	{
		return sourceParts
			.GroupBy(part => new SourcePoolKey(
				part.UnitAssetKey,
				sourceCategoryByUnit.GetValueOrDefault(part.UnitAssetKey) ?? string.Empty,
				part.PartKind,
				part.BodyVariant,
				part.Layer))
			.Select(group => new SourceHitPool(
				group.Key,
				group.ApplyGeometryOrder(part => part).ThenBy(part => part.UnitAssetKey.FileId).ThenBy(part => part.MeshInfoIndex).First()))
			.ToArray();
	}

	// A Unit is the user-facing armor part. Its internal LOD/section records must
	// not become separate planning rows; the writer resolves the complete Unit later.
	private static int ScoreSourceBodyVariant(UnitMeshBodyVariant source, UnitMeshBodyVariant target, UnitMeshBodyVariant? fallback)
	{
		if (fallback is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
		{
			return source == fallback ? 0 : source == UnitMeshBodyVariant.Any ? 1 : 2;
		}
		if (target is UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Unknown)
		{
			return source == UnitMeshBodyVariant.Any ? 0 : 1;
		}
		return source == target ? 0 : source == UnitMeshBodyVariant.Any ? 1 : 2;
	}

	private static UnitMeshBodyVariant? DetermineTargetFallbackVariant(
		EquipmentUnitCatalogEntry target,
		IReadOnlyList<EquipmentUnitPart> sources,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		CrossArmorBodyVariantPreference preference)
	{
		if (selectedSourceBodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
		{
			return selectedSourceBodyVariant;
		}
		var matchableParts = target.Parts
			.Select(part => new
			{
				Part = part,
				Sources = sources.Where(source => source.PartKind == part.PartKind).ToArray()
			})
			.Where(item => item.Sources.Length != 0)
			.ToArray();
		if (matchableParts.Any(item => item.Part.BodyVariant is UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Unknown
			&& !item.Sources.Any(source => source.BodyVariant == UnitMeshBodyVariant.Any)))
		{
			// An actually replaceable Any target with no universal source has no independent
			// body shape. Keep every mapping for this armor on one preferred source shape.
			return PreferredBodyVariant(preference);
		}
		foreach (var item in matchableParts)
		{
			if (item.Part.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky
				&& !item.Sources.Any(source => source.BodyVariant is UnitMeshBodyVariant.Any || source.BodyVariant == item.Part.BodyVariant))
			{
				return PreferredBodyVariant(preference);
			}
		}
		return null;
	}

	private static UnitMeshBodyVariant? ResolveSharedTargetFallbackVariant(
		TargetUnitGroup physicalTarget,
		IReadOnlyDictionary<string, UnitMeshBodyVariant?> fallbackVariants,
		CrossArmorBodyVariantPreference preference)
	{
		var requested = physicalTarget.Uses.Select(use => fallbackVariants.GetValueOrDefault(use.Entry.ArchiveId))
			.Where(variant => variant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
			.Cast<UnitMeshBodyVariant>()
			.GroupBy(variant => variant)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key == PreferredBodyVariant(preference) ? 0 : 1)
			.Select(group => (UnitMeshBodyVariant?)group.Key)
			.FirstOrDefault();
		return requested;
	}

	private static void ReconcileSharedTargetFallbackVariants(
		IReadOnlyList<TargetUnitGroup> physicalTargets,
		IDictionary<string, UnitMeshBodyVariant?> fallbackVariants,
		CrossArmorBodyVariantPreference preference)
	{
		foreach (var physicalTarget in physicalTargets.Where(group => group.Uses.Select(use => use.Entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
		{
			var requirements = physicalTarget.Uses
				.Select(use => new
				{
					use.Entry.ArchiveId,
					Variant = (fallbackVariants.TryGetValue(use.Entry.ArchiveId, out var fallback) ? fallback : null)
						?? (use.Part.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky ? use.Part.BodyVariant : PreferredBodyVariant(preference))
				})
				.DistinctBy(item => item.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			if (requirements.Select(item => item.Variant).Distinct().Count() < 2) continue;
			var winner = requirements.GroupBy(item => item.Variant)
				.OrderByDescending(group => group.Count())
				.ThenBy(group => group.Key == PreferredBodyVariant(preference) ? 0 : 1)
				.First().Key;
			foreach (var requirement in requirements.Where(item => item.Variant != winner))
			{
				// One physical output cannot carry two source variants. Force every mapping of
				// the minority armor onto the winning route, not only this shared mesh.
				fallbackVariants[requirement.ArchiveId] = winner;
			}
		}
	}

	private static bool IsSourceVariantCompatible(UnitMeshBodyVariant source, UnitMeshBodyVariant target, UnitMeshBodyVariant? fallback)
	{
		if (source == UnitMeshBodyVariant.Any) return true;
		var required = fallback ?? (target is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky ? target : null);
		return required is null || source == required;
	}

	private static string? SemanticFamily(string semanticName)
	{
		var name = semanticName.Trim().ToLowerInvariant();
		if (name.StartsWith("g_torso_arm_l_", StringComparison.Ordinal)) return "torso-arm-l";
		if (name.StartsWith("g_torso_arm_r_", StringComparison.Ordinal)) return "torso-arm-r";
		if (name.StartsWith("g_arm_l", StringComparison.Ordinal)) return "arm-l";
		if (name.StartsWith("g_arm_r", StringComparison.Ordinal)) return "arm-r";
		if (name.StartsWith("g_legs_hips_undergarment", StringComparison.Ordinal)) return "hips-undergarment";
		if (name.StartsWith("g_legs_hips", StringComparison.Ordinal)) return "hips";
		if (name.StartsWith("g_leg_undergarment_l", StringComparison.Ordinal)) return "leg-undergarment-l";
		if (name.StartsWith("g_leg_undergarment_r", StringComparison.Ordinal)) return "leg-undergarment-r";
		if (name.StartsWith("g_leg_l", StringComparison.Ordinal)) return "leg-l";
		if (name.StartsWith("g_leg_r", StringComparison.Ordinal)) return "leg-r";
		if (name.StartsWith("g_torso_undergarment", StringComparison.Ordinal)) return "torso-undergarment";
		if (name.StartsWith("g_torso", StringComparison.Ordinal)) return "torso";
		return null;
	}

	private static UnitMeshBodyVariant PreferredBodyVariant(CrossArmorBodyVariantPreference preference)
		=> preference == CrossArmorBodyVariantPreference.Slim ? UnitMeshBodyVariant.Slim : UnitMeshBodyVariant.Stocky;

    private static int GeometryRank(EquipmentUnitPart part)
        => UnitGeometryRanker.GetRank(part.GeometryQuality);
	private sealed record TargetUse(EquipmentUnitCatalogEntry Entry, EquipmentUnitPart Part);
	private sealed record TargetUnitGroup(AssetKey UnitAssetKey, TargetUse Representative, IReadOnlyList<TargetUse> Uses);
	private sealed record SourceTargetCandidate(TargetUnitGroup Target, EquipmentUnitPart Source);
	private sealed record SourceBodyProfile(UnitMeshPartKind PartKind, string SemanticFamily, bool CanActAsEffectiveAny, IReadOnlyList<string> Categories, IReadOnlyList<EquipmentUnitPart> Parts);
	private sealed record SourceSelection(AssetKey UnitAssetKey, EquipmentUnitPart Part);
	private readonly record struct ArchiveTargetCoverage(bool HasAny, bool HasSlim, bool HasStocky)
	{
		public bool IsComplete => HasAny || (HasSlim && HasStocky);
		public UnitMeshBodyVariant? MissingConcreteVariant => !HasAny && HasSlim && !HasStocky
			? UnitMeshBodyVariant.Stocky
			: !HasAny && HasStocky && !HasSlim
				? UnitMeshBodyVariant.Slim
				: null;
		public override string ToString() => HasAny ? "Any" : HasSlim && HasStocky ? "Slim+Stocky" : HasSlim ? "Slim" : HasStocky ? "Stocky" : "None";
	}
	private sealed record SourcePoolKey(AssetKey UnitAssetKey, string Category, UnitMeshPartKind PartKind, UnitMeshBodyVariant BodyVariant, UnitMeshPartLayer Layer);
	private sealed class SourceHitPool(SourcePoolKey key, EquipmentUnitPart representative)
	{
		public SourcePoolKey Key { get; } = key;
		public EquipmentUnitPart Representative { get; } = representative;
	}

	private static async ValueTask<IReadOnlyDictionary<AssetKey, IReadOnlyList<string>>> ReadSharedArchiveIdsAsync(SqliteConnection connection, IReadOnlyList<AssetKey> unitKeys, CancellationToken cancellationToken)
	{
		if (unitKeys.Count == 0) return new Dictionary<AssetKey, IReadOnlyList<string>>();
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT p.unit_type_id,p.unit_file_id,p.archive_id
FROM game_data_unit_parts p
JOIN archives a ON a.archive_id=p.archive_id
WHERE lower(a.category) IN ('armor','helmet')
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
