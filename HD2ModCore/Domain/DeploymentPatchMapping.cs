namespace HD2ModCore.Domain;

// Purpose: One resolved patch-sidecar source and its final game-data destination.
// The deployment executor consumes operations derived exclusively from this map.
public sealed record DeploymentPatchMapping(
	ModNodeId? NodeId,
	string SourcePath,
	string TargetPath,
	string ArchiveHex16,
	int SourcePatchIndex,
	int TargetPatchIndex,
	PatchSidecarKind SidecarKind);
