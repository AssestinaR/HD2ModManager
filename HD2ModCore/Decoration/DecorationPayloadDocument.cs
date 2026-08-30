using System.Collections.Generic;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModCore.Decoration;

// Neutral, non-deployable mesh data. No source Unit AssetKey is retained here.
public sealed class DecorationPayloadDocument
{
    public string Format { get; set; } = "HD2ModManager.DecorationPayload";
    public int Version { get; set; } = 2;
    public string BodyVariant { get; set; } = string.Empty;
    // Source layers guide host selection. Empty means a legacy payload with no layer metadata.
    public List<string> SourceLayers { get; set; } = new();
    public List<DecorationMeshFragment> Fragments { get; set; } = new();
}

public sealed class DecorationMeshFragment
{
    public UnitMeshInfo Mesh { get; set; } = null!;
    public UnitRawMeshData RawMesh { get; set; } = null!;
    public UnitStreamInfo Stream { get; set; } = null!;
    public List<UnitMaterialBinding> Materials { get; set; } = new();
    public List<UnitBoneInfo> BoneInfos { get; set; } = new();
    public UnitTransformInfo TransformInfo { get; set; } = UnitTransformInfo.Empty;
    public List<uint> TransformNameHashes { get; set; } = new();
}
