namespace HD2ModCore.Domain;

// Purpose: Describes a generated patch group that participates in deployment but
// does not belong to a persisted ModNode (for example the merged Lua patch).
public sealed record GeneratedPatchGroup(
	string ArchiveHex16,
	int SourcePatchIndex,
	IReadOnlyList<GeneratedPatchFile> Files,
	string Description,
	ModNodeId? OwnerNodeId = null,
	int? PlacementOrder = null);

public sealed record GeneratedPatchFile(
	PatchSidecarKind SidecarKind,
	string FilePath,
	string FileName,
	long Length,
	DateTimeOffset LastWriteTimeUtc);
