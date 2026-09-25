using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Virtual slots describe output assets, not meshes to look up in GameData.
internal static class CustomModelTransferPlanner
{
	public static IReadOnlyList<CrossArmorTransferMapping> Build(
		IReadOnlyList<EquipmentUnitCatalogEntry> targets,
		IReadOnlyList<EquipmentUnitPart> sources,
		CrossArmorBodyVariantPreference preference,
		IReadOnlyList<CrossArmorManualMapping>? manual,
		IReadOnlyList<CrossArmorManualSuppression>? suppressions,
		bool manualMode,
		List<CoreIssue> issues)
	{
		var result = new List<CrossArmorTransferMapping>();
		foreach (var target in targets)
		{
			// A source can feed both body shapes, but not two overlapping slots.
			var used = new HashSet<(AssetKey, UnitMeshBodyVariant)>();
			if (target.Parts.Count == 0)
				issues.Add(new(CoreIssueSeverity.Error, "CustomModelEmpty", $"{target.DisplayName} 没有启用的组件。"));
			foreach (var part in target.Parts.OrderBy(p => p.PartKind)
				.ThenBy(p => uint.TryParse(p.PieceType, out var n) ? n : uint.MaxValue)
				.ThenBy(p => p.BodyVariant))
			{
				var explicitMapping = manual?.SingleOrDefault(m => m.Target.UnitAssetKey == part.UnitAssetKey);
				var suppressed = suppressions?.Any(s => s.Target.UnitAssetKey == part.UnitAssetKey) == true;
				var candidates = sources.Where(s => s.PartKind == part.PartKind);
				EquipmentUnitPart? source = null;
				if (!suppressed && explicitMapping is not null)
					source = candidates.FirstOrDefault(s => s.UnitAssetKey == explicitMapping.SourceUnitAssetKey);
				else if (!suppressed && !manualMode)
					source = candidates
						.Where(s => !used.Contains((s.UnitAssetKey, part.BodyVariant))
							&& !used.Contains((s.UnitAssetKey, UnitMeshBodyVariant.Any))
							&& (part.BodyVariant != UnitMeshBodyVariant.Any || !used.Any(u => u.Item1 == s.UnitAssetKey)))
						.OrderBy(s => s.BodyVariant == part.BodyVariant ? 0 : s.BodyVariant == UnitMeshBodyVariant.Any ? 1 :
							s.BodyVariant == (preference == CrossArmorBodyVariantPreference.Slim ? UnitMeshBodyVariant.Slim : UnitMeshBodyVariant.Stocky) ? 2 : 3)
						.ThenBy(s => s.Layer == part.Layer ? 0 : 1)
						.ThenByDescending(s => s.StoredBytes)
						.ThenBy(s => s.UnitAssetKey.FileId)
						.FirstOrDefault();
				if (source is null)
					issues.Add(new(CoreIssueSeverity.Error, "CustomModelComponentUnassigned",
						$"{target.DisplayName} 的 {part.PartKind}/{part.BodyVariant}（{part.UnitAssetKey.FileId:x16}）没有来源；请手动指定同部位来源，或在素体中禁用不需要的组件后重新打开计划。自定义 ID 不能读取原版隐藏模板。"));
				else
					used.Add((source.UnitAssetKey, part.BodyVariant));
				result.Add(new(new(part.UnitAssetKey), part, source, source is not null,
					source is null ? "缺少来源" : "生成自定义组件", [target.ArchiveId], [target.DisplayName],
					explicitMapping is not null, suppressed) { HitCount = source is null ? 0 : 1 });
			}
		}
		return result;
	}
}
