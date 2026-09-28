namespace HD2ModCore.Domain;

public sealed record RuntimeArmorRegistryModel(
	string Key,
	uint? SourceItemId,
	IReadOnlyList<RuntimeArmorRegistryModelPiece>? Pieces = null,
	string? Name = null);

public sealed record RuntimeArmorRegistryModelPiece(
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	byte Tone = 0,
	bool Enabled = true,
	bool IsDefaultBase = false,
	bool IsReserved = false,
	uint? OriginalBody = null);

public sealed record RuntimeArmorRegistryPieceOverride(
	uint OwnerItemId,
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	bool Enabled = true);

public sealed record RuntimeArmorRegistryTargetMode(
	uint TargetItemId,
	bool FollowSource = true);

public sealed record RuntimeArmorRegistryMapping(
	uint TargetItemId,
	uint? SourceItemId = null,
	string? ModelKey = null)
{
	public bool IsModelMapping => !string.IsNullOrWhiteSpace(ModelKey);
}

public sealed record RuntimeArmorRegistryDocument(
	int Revision,
	bool Enabled,
	IReadOnlyList<RuntimeArmorRegistryMapping> Mappings,
	IReadOnlyList<RuntimeArmorRegistryModel>? Models = null,
	IReadOnlyList<RuntimeArmorRegistryPieceOverride>? PieceOverrides = null,
	IReadOnlyList<RuntimeArmorRegistryTargetMode>? TargetModes = null,
	string CompositionMode = "full")
{
	public IReadOnlyList<RuntimeArmorRegistryModel> ModelDefinitions { get; init; }
		= Models ?? Array.Empty<RuntimeArmorRegistryModel>();
	public IReadOnlyList<RuntimeArmorRegistryPieceOverride> PieceOverrideDefinitions { get; init; }
		= PieceOverrides ?? Array.Empty<RuntimeArmorRegistryPieceOverride>();
	public IReadOnlyList<RuntimeArmorRegistryTargetMode> TargetModeDefinitions { get; init; }
		= TargetModes ?? Array.Empty<RuntimeArmorRegistryTargetMode>();
}

public sealed record RuntimeArmorCatalogEntry(
	uint ItemId,
	string ArchiveId,
	string DisplayName);

public sealed record RuntimeArmorRegistrySnapshot(
	string RegistryRoot,
	string ProtocolPath,
	string Fingerprint,
	bool ProtocolExists,
	RuntimeArmorRegistryDocument Document,
	bool RuntimeStatusExists,
	bool RuntimeProcessRunning,
	bool RuntimeAcknowledged,
	IReadOnlyDictionary<string, string> RuntimeStatus,
	bool LocatorCacheExists,
	IReadOnlyDictionary<string, string> LocatorCache)
{
	public bool HasUnappliedChanges { get; init; }
}

public sealed record RuntimeArmorRegistryApplyResult(
	RuntimeArmorRegistryDocument Document,
	string Fingerprint,
	bool RuntimeProcessRunning,
	bool RuntimeAcknowledged);
