namespace HD2ModCore.Domain;

// Purpose: One validated source-to-game-bin mapping for an external component.
public sealed record ExternalFileMapping(
	ModNodeId NodeId,
	string SourcePath,
	string TargetPath,
	ExternalInstallTargetRoot TargetRoot,
	string TargetRelativePath,
	long Length,
	string Sha256);
