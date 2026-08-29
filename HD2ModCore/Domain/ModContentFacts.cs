using System.Text.Json.Serialization;

namespace HD2ModCore.Domain;

// Purpose: Authoritative immutable content snapshot for one flat internal mod.
public sealed record ModContentFacts(
	ModNodeId NodeId,
	string RelativePath,
	string ContentGeneration,
	DateTimeOffset BuiltUtc,
	IReadOnlyList<ModPatchGroupFact> PatchGroups,
	IReadOnlyList<CoreIssue> Issues,
	ModAssetStructureFacts? AssetStructure = null,
	IReadOnlyList<ExternalInstallFileFact>? ExternalInstallFiles = null)
{
	// The structure snapshot is the canonical reusable asset view. The fallback
	// keeps caches written by older manager versions useful until this Mod is
	// analyzed again by the current lightweight producer.
	[JsonIgnore]
	public ModAssetStructureFacts Structure
		=> AssetStructure ?? ModAssetStructureFacts.FromLegacy(this);

	[JsonIgnore]
	public bool ContainsLua => Structure.ContainsLua;

	[JsonIgnore]
	public int LuaEntryCount => Structure.LuaEntryCount;

	[JsonIgnore]
	public LuaMergeState LuaMergeState => Structure.LuaMergeState;

	[JsonIgnore]
	public IReadOnlyList<ExternalInstallFileFact> ExternalFiles => ExternalInstallFiles ?? Array.Empty<ExternalInstallFileFact>();

	[JsonIgnore]
	public bool ContainsReShade => ExternalFiles.Any(file => file.Kind is
		ExternalInstallFileKind.ReShadePlugin or ExternalInstallFileKind.ReShadePreset);

	public IReadOnlyList<IndexedPatchFile> ToPatchFileIndex()
		=> PatchGroups
			.SelectMany(group => group.Files.Select(file => new IndexedPatchFile(
				NodeId,
				file.FilePath,
				file.FileName,
				group.Id.SourceArchiveHex,
				group.Id.SourcePatchIndex,
				group.NormalizedOrder,
				file.SidecarKind,
				file.Length,
				file.LastWriteTimeUtc)))
			.OrderBy(file => file.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ThenBy(file => file.NormalizedOrder)
			.ThenBy(file => file.SidecarKind)
			.ToList();
}
