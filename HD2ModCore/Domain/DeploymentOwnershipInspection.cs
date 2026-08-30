namespace HD2ModCore.Domain;

// Purpose: The ownership state of the top-level patch namespace in a GameData directory.
public enum DeploymentOwnershipStatus
{
	CleanBootstrap,
	Managed,
	Blocked,
}

// Purpose: One physical, parseable patch file found directly in GameData.
public sealed record DeploymentOwnershipPatch(
	string Path,
	string ArchiveHex16,
	int PatchIndex,
	PatchSidecarKind SidecarKind,
	bool IsReparsePoint = false);

// Purpose: Strict, read-only ownership result used to decide whether a deployment may safely replace managed patches.
public sealed record DeploymentOwnershipInspection(
	DeploymentOwnershipStatus Status,
	string? GameDataDirectory,
	ActivationState? ActivationState,
	IReadOnlyList<DeploymentOwnershipPatch> ActualPatches,
	IReadOnlyList<ActivationStateFileEntry> SafeDeletionTargets,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool IsCleanBootstrap => Status == DeploymentOwnershipStatus.CleanBootstrap;

	public bool IsManaged => Status == DeploymentOwnershipStatus.Managed;

	public bool IsBlocked => Status == DeploymentOwnershipStatus.Blocked;
}
