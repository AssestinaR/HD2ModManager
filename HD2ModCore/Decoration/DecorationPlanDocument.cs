using System;
using System.Collections.Generic;

namespace HD2ModCore.Decoration;

// Stable, editable contract for a decoration package before the mesh append engine is connected.
public sealed class DecorationPlanDocument
{
    public string Format { get; set; } = "HD2ModManager.Decoration";
    public int Version { get; set; } = 3;
    public string Name { get; set; } = string.Empty;
    // PatchSnapshot is the current self-contained format. Payloads remains for legacy .bin packages only.
    public string SourceStorageMode { get; set; } = "PatchSnapshot";
    public List<DecorationSourceUnit> SourceUnits { get; set; } = new();
    public List<DecorationPayloadFile> Payloads { get; set; } = new();
    public DecorationAttachmentPlan Plan { get; set; } = new();
}

public sealed class DecorationAttachmentPlan
{
    public List<string> TargetModGuids { get; set; } = new();
    public string TargetPart { get; set; } = "LeftArm";
    public string TargetBodyVariant { get; set; } = "Dual";
    public string DualVariantMode { get; set; } = "AutoAssign";
    // Replacement stays deliberately strict: a mapped host is cleared only when its
    // original semantic part/layer appears in this portable source summary.
    public bool ReplaceWhenSourcePartLayerMatches { get; set; } = true;
    public List<string> SourcePartLayers { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DecorationPayloadFile
{
    public string BodyVariant { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
}

public sealed class DecorationSourceUnit
{
    // A visible selection identifies a logical Unit; snapshot extraction retains its full LOD group.
    // A culling selection remains an explicit, single-mesh opt-in.
    public ulong TypeId { get; set; }
    public ulong FileId { get; set; }
    public int MeshInfoIndex { get; set; }
    public string BodyVariant { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public bool IsCulling { get; set; }
}
