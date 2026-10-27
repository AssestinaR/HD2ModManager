using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：把"单元格装箱"的结果投影成生产端已经在用的 CrossArmorTransferMapping。
// Purpose: Projects the cell-packing result into the CrossArmorTransferMapping the production path already consumes.
//
// 为什么不直接在生产端里算：旧规划器与装箱规划器要能 A/B 对比，两者必须产出**同一种**输出，
// 否则比较的不是算法而是数据形状。于是装箱只负责"哪个来源进哪个目标格子"，
// 投影统一在这里做，生产端一行都不用改。
internal static class CellCrossArmorPlanProjection
{
	public static CellCrossArmorPlanResult Build(
		IReadOnlyList<EquipmentUnitPart> sourceParts,
		IReadOnlyList<EquipmentUnitCatalogEntry> selectedTargets,
		CrossArmorBodyVariantPreference preference,
		bool autoHideUnmapped,
		IReadOnlyList<CrossArmorManualMapping> manualMappings,
		IReadOnlyList<CrossArmorManualSuppression> manualSuppressions,
		bool manualMode,
		ICollection<string> plannerDebug)
	{
		ArgumentNullException.ThrowIfNull(sourceParts);
		ArgumentNullException.ThrowIfNull(selectedTargets);
		ArgumentNullException.ThrowIfNull(plannerDebug);

		var issues = new List<CoreIssue>();
		var uses = BuildTargetUses(selectedTargets);
		var archiveCategories = selectedTargets
			.Where(entry => !entry.IsCustomModel)
			.GroupBy(entry => entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().Category, StringComparer.OrdinalIgnoreCase);

		// 来源必须按"装备族"拆开：护甲 kit 的格子是槽 1..9，头盔 kit 只有一个槽 0。
		// 不拆的话，头盔目标只有一个桶，护甲来源的其余部件会全部被合并进那个头盔 unit
		// ——A/B 第一次跑就出现了"头盔吞下 8 个身体部件"的荒唐结果。
		var sourceTablesByFamily = new Dictionary<string, ArmorCellTable>(StringComparer.OrdinalIgnoreCase);
		ArmorCellTable SourceTableFor(string category)
		{
			if (sourceTablesByFamily.TryGetValue(category, out var cached)) return cached;
			var table = ArmorCellTable.Build(sourceParts.Where(part => IsSameFamily(part, category)));
			sourceTablesByFamily[category] = table;
			return table;
		}

		var primarySourceTable = SourceTableFor("Armor");
		plannerDebug.Add($"[CELL-SOURCE] armourBuckets={primarySourceTable.Slots.Count} unplaced={primarySourceTable.Unplaced.Count} duplicates={primarySourceTable.Duplicates.Count}");
		foreach (var slot in primarySourceTable.Slots)
			plannerDebug.Add($"[CELL-SOURCE] Unit=0x{slot.Part.UnitAssetKey.FileId:x16} Cell={slot.Cell.Slot}/{slot.Cell.Layer} Body={slot.Body}");
		plannerDebug.Add($"[CELL-TARGET] physicalUnits={uses.Count} archives={uses.Values.SelectMany(use => use.ArchiveIds).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");

		// 装箱是"一件目标护甲"内部的事：同一件护甲里一个来源部件只能用一次，
		// 但换到另一件目标护甲时可以再用一次——那是另一份独立的输出 Unit。
		// 所以必须**按归档分别装箱**，而不是把所有目标 unit 摊成一张表。
		var assignments = new Dictionary<AssetKey, CellAssignment>();
		var placedSomewhere = new HashSet<AssetKey>();
		var droppedSomewhere = new HashSet<AssetKey>();
		if (!manualMode)
		{
			foreach (var archive in OrderArchives(uses))
			{
				var archiveUnits = uses
					.Where(pair => pair.Value.ArchiveIds.Contains(archive, StringComparer.OrdinalIgnoreCase))
					.Select(pair => pair.Value.Representative)
					.ToArray();
				if (archiveUnits.Length == 0) continue;

				var targetTable = ArmorCellTable.Build(archiveUnits);
				var category = archiveCategories.GetValueOrDefault(archive, "Armor");
				var packed = CellAssignmentPlanner.Plan(SourceTableFor(category), targetTable, preference, autoHideUnmapped: true);
				foreach (var assignment in packed.Assignments)
				{
					var key = assignment.Target!.UnitAssetKey;
					// 一个物理 Unit 被多件目标护甲共用时，先到的归档说了算——
					// 它只产出一个输出 Unit，不可能同时满足两件护甲的不同要求。
					if (assignments.ContainsKey(key)) continue;
					assignments[key] = assignment;
				}

				// "在某一件目标护甲里放不下"是正常现象（那件护甲本来就没有对应的格子），
				// 不是错误。只有**每一件目标护甲都放不下**才值得报出来。
				foreach (var assignment in packed.Assignments)
					foreach (var source in assignment.Sources)
						placedSomewhere.Add(source.UnitAssetKey);
				foreach (var part in packed.Unassigned) droppedSomewhere.Add(part.UnitAssetKey);

				issues.AddRange(packed.Issues.Where(issue => issue.Code != "CellAssignmentSourceDropped"));
				plannerDebug.Add($"[CELL-ARCHIVE] {archive} units={archiveUnits.Length} natural={packed.NaturalCount} broadcast={packed.BroadcastCount} rerouted={packed.ReroutedCount} merged={packed.MergedCount} hidden={packed.HiddenCount}");
			}

			foreach (var key in droppedSomewhere.Where(key => !placedSomewhere.Contains(key)))
			{
				var part = sourceParts.FirstOrDefault(candidate => candidate.UnitAssetKey == key);
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"CellAssignmentSourceDropped",
					$"来源部件 0x{key.FileId:x16}（{part?.Cell.Display ?? "未定位"}）在任何目标护甲里都没有可放置的格子，未写入计划。"));
			}
		}

		var manualByTarget = manualMappings.ToDictionary(mapping => mapping.Target.UnitAssetKey);
		var suppressed = manualSuppressions.Select(item => item.Target.UnitAssetKey).ToHashSet();
		var mappings = new List<CrossArmorTransferMapping>();
		var impacts = new List<CrossArmorTransferImpact>();

		foreach (var (unitKey, use) in uses.OrderBy(pair => pair.Key.FileId))
		{
			var targetPart = use.Representative;
			var usedByIds = use.ArchiveIds;
			var usedByNames = use.DisplayNames;

			IReadOnlyList<EquipmentUnitPart> sources;
			CellAssignmentKind? kind;
			var isManual = false;
			var isSuppressed = false;

			if (manualByTarget.TryGetValue(unitKey, out var manual))
			{
				var manualSource = sourceParts.FirstOrDefault(part => part.UnitAssetKey == manual.SourceUnitAssetKey);
				if (manualSource is null)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"ManualSourceUnavailable",
						$"手动来源已不在当前筛选范围：目标 Unit 0x{unitKey.FileId:x16}。"));
					sources = [];
					kind = null;
				}
				else
				{
					sources = [manualSource];
					kind = null;
					isManual = true;
				}
			}
			else if (suppressed.Contains(unitKey))
			{
				sources = [];
				kind = null;
				isSuppressed = true;
			}
			else if (assignments.TryGetValue(unitKey, out var planned))
			{
				sources = planned.Sources;
				kind = planned.Kind;
			}
			else
			{
				sources = [];
				kind = null;
			}

			// 手动模式只保留显式指定的结果；其余目标不进计划（原版保持原样）。
			if (manualMode && !isManual && !isSuppressed) continue;

			var willReplace = sources.Count != 0;
			var reason = willReplace
				? isManual ? "强制命中" : ReasonOf(kind)
				: isSuppressed ? "强制隐藏" : "隐藏";
			mappings.Add(new CrossArmorTransferMapping(
				new CrossArmorPhysicalTargetKey(unitKey),
				targetPart,
				sources.FirstOrDefault(),
				willReplace,
				reason,
				usedByIds,
				usedByNames,
				isManual,
				isSuppressed)
			{
				HitCount = usedByIds.Count,
				ExtraSources = sources.Skip(1).ToArray(),
			});

			if (!willReplace) continue;
			foreach (var sharedArchiveId in targetPart.SharedArchiveIds.Where(id => !usedByIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
			{
				var impacted = selectedTargets.FirstOrDefault(candidate => string.Equals(candidate.ArchiveId, sharedArchiveId, StringComparison.OrdinalIgnoreCase));
				impacts.Add(new CrossArmorTransferImpact(
					sharedArchiveId,
					impacted?.DisplayName ?? sharedArchiveId,
					targetPart.UnitAssetKey,
					targetPart.PartKind,
					targetPart.Layer));
			}
		}

		// 每件归档都会重复报同一条"来源放不下"，去重后再交给上层。
		var distinctIssues = issues
			.GroupBy(issue => (issue.Code, issue.Message))
			.Select(group => group.First())
			.ToList();

		plannerDebug.Add($"[CELL-RESULT] Mappings={mappings.Count} Replace={mappings.Count(m => m.WillReplace)} Merged={mappings.Count(m => m.RequiresMerge)} Hidden={mappings.Count(m => !m.WillReplace)}");
		return new CellCrossArmorPlanResult(mappings, impacts, distinctIssues, assignments.Values.ToArray());
	}

	private static IEnumerable<string> OrderArchives(IReadOnlyDictionary<AssetKey, TargetUse> uses)
		=> uses.Values
			.SelectMany(use => use.ArchiveIds)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(id => id, StringComparer.Ordinal);

	// 头盔 kit 只占槽 0；护甲 kit 占槽 1..9。来源部件按它自己的格子判断属于哪一族。
	private static bool IsSameFamily(EquipmentUnitPart part, string category)
	{
		if (!part.Cell.IsKnown) return false;
		var isHelmetPart = part.Cell.Part == ArmorPart.Helmet;
		return string.Equals(category, "Helmet", StringComparison.OrdinalIgnoreCase) == isHelmetPart;
	}

	private static string ReasonOf(CellAssignmentKind? kind) => kind switch
	{
		CellAssignmentKind.Natural => "命中",
		CellAssignmentKind.Broadcast => "广播",
		CellAssignmentKind.LayerShift => "改挂",
		CellAssignmentKind.Rerouted => "改挂",
		CellAssignmentKind.Merged => "合并",
		_ => "隐藏",
	};

	// 一个物理 Unit 可能被多件目标护甲共用：它只产出一个输出 Unit，所以这里按 Unit 归并，
	// 并把"谁在用它"记下来——计划表要显示这些，影响面也要靠它算。
	private static Dictionary<AssetKey, TargetUse> BuildTargetUses(IReadOnlyList<EquipmentUnitCatalogEntry> targets)
	{
		var result = new Dictionary<AssetKey, TargetUse>();
		foreach (var entry in targets.Where(target => !target.IsCustomModel))
		{
			foreach (var part in entry.Parts)
			{
				if (part.PartKind == UnitMeshPartKind.Unknown || part.Confidence != 100) continue;
				if (!result.TryGetValue(part.UnitAssetKey, out var use))
				{
					result.Add(part.UnitAssetKey, new TargetUse(part, [entry.ArchiveId], [entry.DisplayName]));
					continue;
				}

				result[part.UnitAssetKey] = use with
				{
					Representative = PreferRepresentative(use.Representative, part),
					ArchiveIds = use.ArchiveIds.Contains(entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
						? use.ArchiveIds
						: [.. use.ArchiveIds, entry.ArchiveId],
					DisplayNames = use.DisplayNames.Contains(entry.DisplayName, StringComparer.OrdinalIgnoreCase)
						? use.DisplayNames
						: [.. use.DisplayNames, entry.DisplayName],
				};
			}
		}

		return result;
	}

	// 同一个物理 Unit 在不同归档里可能带着不同的 MeshInfo 记录；取几何最"实"的那条当代表，
	// 与旧规划器的取代表口径一致（几何质量 → 三角形 → 顶点 → 字节）。
	private static EquipmentUnitPart PreferRepresentative(EquipmentUnitPart current, EquipmentUnitPart candidate)
	{
		var rank = UnitGeometryRanker.GetRank(candidate.GeometryQuality)
			.CompareTo(UnitGeometryRanker.GetRank(current.GeometryQuality));
		if (rank != 0) return rank > 0 ? candidate : current;
		if (candidate.TriangleCount != current.TriangleCount)
			return candidate.TriangleCount > current.TriangleCount ? candidate : current;
		if (candidate.VertexCount != current.VertexCount)
			return candidate.VertexCount > current.VertexCount ? candidate : current;
		if (candidate.StoredBytes != current.StoredBytes)
			return candidate.StoredBytes > current.StoredBytes ? candidate : current;
		return candidate.MeshInfoIndex < current.MeshInfoIndex ? candidate : current;
	}

	private sealed record TargetUse(
		EquipmentUnitPart Representative,
		IReadOnlyList<string> ArchiveIds,
		IReadOnlyList<string> DisplayNames);
}

internal sealed record CellCrossArmorPlanResult(
	IReadOnlyList<CrossArmorTransferMapping> Mappings,
	IReadOnlyList<CrossArmorTransferImpact> Impacts,
	IReadOnlyList<CoreIssue> Issues,
	IReadOnlyList<CellAssignment> Assignments);
