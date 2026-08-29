namespace HD2ModCore.Domain;

// Purpose: Public completed deployment state persisted atomically after verification.
public sealed record ActivationState(
	int Version,
	ProfileId? ProfileId,
	long ProfileRevision,
	DateTimeOffset AppliedUtc,
	bool Completed,
	IReadOnlyList<ActivationStateFileEntry> Files,
	IReadOnlyList<CoreIssue> Issues)
{
	// External files (for example ReShade addons) live outside GameData and are
	// tracked separately so patch graph readers never mistake them for patches.
	public IReadOnlyList<ExternalFileMapping> ExternalFiles { get; init; } = Array.Empty<ExternalFileMapping>();
}
