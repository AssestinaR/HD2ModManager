using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Domain;

public sealed record ArmorNormalizationInspection(
    ModNodeId NodeId,
    IReadOnlyList<string> SourcePatchPaths,
    IReadOnlyList<ArmorNormalizationArmor> Armors,
    IReadOnlyList<CoreIssue> Issues)
{
    public bool IsReady => Armors.Count != 0 && !Issues.Any(i => i.Severity == CoreIssueSeverity.Error);
}

public sealed record ArmorNormalizationArmor(
    string PatchFileName,
    string ArchiveId,
    string DisplayName,
    IReadOnlyList<ArmorNormalizationTarget> Targets,
    IReadOnlyList<ArmorNormalizationSource> Sources,
    IReadOnlyList<string> HiddenUnitAssetKeys,
    string BaselineUnitAssetKey,
    bool HasGpuReuseEvidence,
    bool IsLossy,
    IReadOnlyList<string> Warnings);

public sealed record ArmorNormalizationTarget(string Role, string BodyCoverage, string AssetKey, int MeshInfoIndex, string Layer);
public sealed record ArmorNormalizationSource(string Role, string BodyVariant, string AssetKey, int MeshInfoIndex, string TargetRoles);

public sealed record ArmorNormalizationResult(bool IsSuccessful, string OutputDirectory, int ProcessedPatches, int RebuiltUnits, int HiddenUnits, IReadOnlyList<CoreIssue> Issues);
