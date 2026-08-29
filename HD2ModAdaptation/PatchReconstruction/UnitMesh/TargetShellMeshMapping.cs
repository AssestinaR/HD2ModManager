namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Shared source-to-target MeshInfo contract for Canonical same-key rebuilding.
// It intentionally lives apart from the retired TargetShell writer so current
// reconstruction code does not inherit that legacy implementation boundary.
public sealed record TargetShellMeshMapping(
    AssetKey SourceUnitAssetKey,
    int SourceMeshInfoIndex,
    int TargetMeshInfoIndex);
