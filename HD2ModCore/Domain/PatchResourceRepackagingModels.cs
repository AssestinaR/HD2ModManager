namespace HD2ModCore.Domain;

// 作用：描述资源重打包页面可见的一个来源 Patch 组。
public sealed record PatchResourcePatchGroup(
    ModNodeId NodeId,
    string ModName,
    string ArchiveHex,
    int PatchIndex,
    string PatchTocPath)
{
    public string DisplayName => $"{ModName} / {ArchiveHex}.patch_{PatchIndex}";
    public string PatchLabel => $"{ArchiveHex}.patch_{PatchIndex}";
}

// 作用：描述一个 AssetKey 在某一个来源 Patch 组中的具体出现。
public sealed record PatchResourceOccurrence(
    ModNodeId NodeId,
    string ModName,
    string ArchiveHex,
    int PatchIndex,
    string PatchTocPath,
    AssetKey AssetKey,
    string TypeName,
    IReadOnlyList<AssetKey> ReferenceTargets,
    uint TocBytes,
    uint StreamBytes,
    uint GpuBytes,
    string PayloadFingerprint)
{
    public string PatchLabel => $"{ArchiveHex}.patch_{PatchIndex}";
    public long TotalBytes => (long)TocBytes + StreamBytes + GpuBytes;
}

// 作用：将相同 AssetKey 且 Payload 完全一致的来源合并成一个可选变体。
// 不同指纹的变体代表真实冲突，不能同时写入同一个输出 Patch。
public sealed record PatchResourceAssetVariant(
    AssetKey AssetKey,
    string TypeName,
    IReadOnlyList<AssetKey> ReferenceTargets,
    IReadOnlyList<PatchResourceOccurrence> Occurrences,
    string PayloadFingerprint)
{
    public bool IsConflict { get; init; }
}

public sealed record PatchResourceRepackagingInspection(
    IReadOnlyList<PatchResourcePatchGroup> PatchGroups,
    IReadOnlyList<PatchResourceAssetVariant> Variants,
    IReadOnlyList<CoreIssue> Issues);

public sealed record PatchResourceRepackagingSelection(
    PatchResourceOccurrence Occurrence);
