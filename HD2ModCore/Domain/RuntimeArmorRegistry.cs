namespace HD2ModCore.Domain;

public sealed record RuntimeArmorRegistryMapping(uint TargetItemId, uint SourceItemId);

public sealed record RuntimeArmorRegistryDocument(
	int Revision,
	bool Enabled,
	IReadOnlyList<RuntimeArmorRegistryMapping> Mappings);

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
