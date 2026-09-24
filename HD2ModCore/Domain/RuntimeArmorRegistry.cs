namespace HD2ModCore.Domain;

public sealed record RuntimeArmorRegistryModel(string Key, uint? SourceItemId);

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
	IReadOnlyList<RuntimeArmorRegistryModel>? Models = null)
{
	public IReadOnlyList<RuntimeArmorRegistryModel> ModelDefinitions { get; init; }
		= Models ?? Array.Empty<RuntimeArmorRegistryModel>();
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
	IReadOnlyDictionary<string, string> LocatorCache);

public sealed record RuntimeArmorRegistryApplyResult(
	RuntimeArmorRegistryDocument Document,
	string Fingerprint,
	bool RuntimeProcessRunning,
	bool RuntimeAcknowledged);
