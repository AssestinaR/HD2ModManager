namespace HD2ModCore.Domain;

// Purpose: One validated source-to-game-bin mapping for an external component.
public sealed record ExternalFileMapping(
	ModNodeId NodeId,
	string SourcePath,
	string TargetPath,
	ExternalInstallTargetRoot TargetRoot,
	string TargetRelativePath,
	long Length,
	string Sha256)
{
	// Retained in activation-state and plan artifacts so callers can distinguish
	// executable add-ons from non-executable ReShade presets without inferring
	// intent from a filename. The default keeps activation-state files written
	// before this property existed readable.
	public ExternalInstallFileKind Kind { get; init; } = ExternalInstallFileKind.ReShadePlugin;
}
