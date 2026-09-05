using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：描述一次模组库文件系统对账结果。
// Purpose: Describes the result of one mod-library filesystem reconciliation.
public sealed record ModLibrarySynchronizationResult(
	LibrarySnapshot Snapshot,
	IReadOnlySet<ModNodeId> AddedNodeIds,
	IReadOnlySet<ModNodeId> ChangedNodeIds,
	IReadOnlySet<ModNodeId> MissingNodeIds,
	bool FilesystemChanged)
{
	/// <summary>
	/// Whether <see cref="Snapshot"/> differs in node data from the source
	/// snapshot. Missing storage is observable but is intentionally not an
	/// implicit node deletion or snapshot mutation.
	/// </summary>
	public bool HasSnapshotChanges { get; init; }
}

/// <summary>
/// A pure top-level library scan. <see cref="Directories"/> contains folders
/// with at least one base Patch or a valid logical-container marker;
/// <see cref="SeenRelativePaths"/> also includes folders with no usable base
/// Patch so callers can distinguish a missing directory from a known Mod whose
/// Patch payload disappeared.
/// </summary>
public sealed record ModLibrarySynchronizationInspection(
	IReadOnlyList<ModLibrarySynchronizationDirectory> Directories,
	IReadOnlySet<string> SeenRelativePaths,
	IReadOnlySet<string> IgnoredRelativePaths);

public sealed record ModLibrarySynchronizationDirectory(
	string RelativePath,
	string Name,
	IReadOnlyList<PatchGroupKey> PatchGroups,
	string ContentFingerprint,
	bool IsLogicalContainer = false,
	ModNodeId? LogicalNodeId = null);
