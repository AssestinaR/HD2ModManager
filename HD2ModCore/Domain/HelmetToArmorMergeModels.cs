using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Domain;

public enum HelmetToArmorMergeOutputMode
{
	FirstHelmetOnly,
	SharedArmorVariants,
}

// 作用：描述一个来源 Patch 中可将 Helmet 合并到 Torso 的完整模型集合。
// Purpose: Describes one complete Armor/Helmet model set that can be merged.
public sealed record HelmetToArmorMergeCandidate(
	string CandidateId,
	string SourcePatchFileName,
	string ArmorArchiveId,
	string HelmetArchiveId,
	string DisplayName,
	IReadOnlyList<EquipmentUnitPart> ArmorParts,
	EquipmentUnitPart HelmetPart)
{
	public IReadOnlyList<AssetKey> ArmorUnitKeys => ArmorParts
		.Select(part => part.UnitAssetKey)
		.Distinct()
		.ToArray();
	public bool HasAnyArmor => ArmorParts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Any);
	public bool HasSlimArmor => ArmorParts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Slim);
	public bool HasStockyArmor => ArmorParts.Any(part => part.BodyVariant == UnitMeshBodyVariant.Stocky);
}

// 作用：提供头盔合并向导的只读候选结果。
// Purpose: Provides a no-write inspection result for the helmet merge wizard.
public sealed record HelmetToArmorMergeInspection(
	ModNodeId NodeId,
	string? ContentGeneration,
	IReadOnlyList<string> SourcePatchPaths,
	IReadOnlyList<HelmetToArmorMergeCandidate> Candidates,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool HasUsableCandidates => Candidates.Count != 0;
}

// 作用：描述一次头盔合并输出操作。
// Purpose: Describes one helmet-to-armor merge output operation.
public sealed record HelmetToArmorMergeResult(
	bool IsSuccessful,
	string? OutputDirectory,
	int ProcessedPatchCount,
	int MergedArmorUnitCount,
	int HiddenHelmetUnitCount,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool HasWarnings => Issues.Any(issue => issue.Severity == CoreIssueSeverity.Warning);
}
