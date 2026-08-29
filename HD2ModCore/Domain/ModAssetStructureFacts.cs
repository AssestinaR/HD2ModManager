using HD2ModAdaptation.Analysis;
using System.Text.Json.Serialization;

namespace HD2ModCore.Domain;

// 作用：保存一个 Mod 的轻量资产结构事实。它只包含 TOC/引用/映射元数据，不保留 GPU、Stream 或顶点字节。
// Purpose: Stores a lightweight structural snapshot of a Mod without retaining GPU, Stream, or vertex bytes.
public sealed record ModAssetStructureFacts(
	string ContentGeneration,
	DateTimeOffset BuiltUtc,
	IReadOnlyList<ModAssetStructureAssetFact> Assets,
	IReadOnlyList<ModAssetStructureSourceArchiveFact> SourceArchives,
	IReadOnlyList<ModAssetStructureTargetArchiveFact> TargetArchives,
	IReadOnlyList<ModAssetStructureReferenceFact> References,
	IReadOnlyList<CoreIssue> Issues,
	string? MappingGeneration = null)
{
	[JsonIgnore]
	public bool ContainsLua => Assets.Any(asset => asset.IsLua);

	[JsonIgnore]
	public int LuaEntryCount => Assets
		.Where(asset => asset.IsLua)
		.Sum(asset => asset.Occurrences.Count == 0 ? 1 : asset.Occurrences.Count);

	[JsonIgnore]
	public LuaMergeState LuaMergeState => ContainsLua ? LuaMergeState.Detected : LuaMergeState.None;

	public static ModAssetStructureFacts Empty(string contentGeneration, DateTimeOffset builtUtc)
		=> new(contentGeneration, builtUtc, [], [], [], [], []);

	// Old AssetInventory cache entries predate the structure projection. Reconstructing
	// this minimal view keeps them readable without forcing an immediate full rescan.
	public static ModAssetStructureFacts FromLegacy(ModContentFacts facts)
	{
		ArgumentNullException.ThrowIfNull(facts);
		var assets = facts.PatchGroups
			.SelectMany(group => group.AssetKeys.Select(key => new ModAssetStructureAssetFact(
				key,
				key.TypeId == 0xe0a48d0be9a7453f,
				key.TypeId == 0xc4f0f4be7fb0c8d6,
				key.TypeId == 0xeac0b497876adedf,
				key.TypeId == 0xcd4238c6a0c69e32,
				[new ModAssetOccurrenceFact(group.Id, group.Files.FirstOrDefault(file => file.SidecarKind == PatchSidecarKind.Base)?.FilePath ?? string.Empty,
					group.Files.FirstOrDefault(file => file.SidecarKind == PatchSidecarKind.Base)?.FileName ?? group.Id.ToString(),
					0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)],
				Lua: LuaAssetFacts.TryIdentify(new HD2ModAdaptation.PatchReconstruction.AssetKey(key.TypeId, key.FileId)))))
			.GroupBy(asset => asset.AssetKey)
			.Select(group => MergeAssets(group.Key, group))
			.OrderBy(asset => asset.AssetKey.TypeId)
			.ThenBy(asset => asset.AssetKey.FileId)
			.ToArray();

		var sourceArchives = facts.PatchGroups
			.GroupBy(group => group.Id.SourceArchiveHex, StringComparer.OrdinalIgnoreCase)
			.Select(group => new ModAssetStructureSourceArchiveFact(
				group.Key,
				group.Select(item => item.Id).OrderBy(item => item.SourcePatchIndex).ToArray(),
				group.SelectMany(item => item.AssetKeys).Distinct().OrderBy(key => key.TypeId).ThenBy(key => key.FileId).ToArray()))
			.OrderBy(archive => archive.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new ModAssetStructureFacts(facts.ContentGeneration, facts.BuiltUtc, assets, sourceArchives, [], [], facts.Issues);
	}

	private static ModAssetStructureAssetFact MergeAssets(
		AssetKey key,
		IEnumerable<ModAssetStructureAssetFact> assets)
	{
		var values = assets.ToArray();
		return new ModAssetStructureAssetFact(
			key,
			values.Any(value => value.IsUnit),
			values.Any(value => value.IsCompositeUnit),
			values.Any(value => value.IsMaterial),
			values.Any(value => value.IsTexture),
			values.SelectMany(value => value.Occurrences).ToArray(),
			values.SelectMany(value => value.TargetArchives ?? []).DistinctBy(archive => archive.ArchiveId, StringComparer.OrdinalIgnoreCase).ToArray(),
			values.FirstOrDefault(value => value.Lua is not null)?.Lua);
	}
}

public enum LuaMergeState
{
	None,
	Detected,
}

// One logical AssetKey may occur in several Patch groups. Keep every occurrence so
// reused AssetKeys do not lose their per-group source information.
public sealed record ModAssetStructureAssetFact(
	AssetKey AssetKey,
	bool IsUnit,
	bool IsCompositeUnit,
	bool IsMaterial,
	bool IsTexture,
	IReadOnlyList<ModAssetOccurrenceFact> Occurrences,
	IReadOnlyList<ArchiveMetadata>? TargetArchives = null,
	PatchLuaAssetFact? Lua = null)
{
	[JsonIgnore]
	public IReadOnlyList<ArchiveMetadata> MappedTargetArchives => TargetArchives ?? [];

	[JsonIgnore]
	public bool IsLua => Lua is not null
		|| LuaAssetFacts.IsBootEntry(new HD2ModAdaptation.PatchReconstruction.AssetKey(AssetKey.TypeId, AssetKey.FileId));
}

public sealed record ModAssetOccurrenceFact(
	ModPatchGroupId PatchGroupId,
	string SourceFilePath,
	string SourceFileName,
	uint EntryIndex,
	ulong TocDataOffset,
	ulong StreamOffset,
	ulong GpuResourceOffset,
	ulong Unknown1,
	ulong Unknown2,
	uint TocDataSize,
	uint StreamSize,
	uint GpuResourceSize,
	uint Unknown3,
	uint Unknown4);

public sealed record ModAssetStructureSourceArchiveFact(
	string ArchiveHex16,
	IReadOnlyList<ModPatchGroupId> PatchGroups,
	IReadOnlyList<AssetKey> AssetKeys);

// This is the versioned GameData-side projection. Source archive IDs and GameData
// archive IDs deliberately use separate records so callers cannot confuse them.
public sealed record ModAssetStructureTargetArchiveFact(
	ArchiveMetadata Archive,
	IReadOnlyList<AssetKey> AssetKeys);

public sealed record ModAssetStructureReferenceFact(
	ModPatchGroupId PatchGroupId,
	AssetKey SourceAssetKey,
	AssetKey TargetAssetKey,
	PatchReferenceKind Kind,
	uint PayloadRelativeOffset,
	uint? SlotId = null,
	int? ReferenceIndex = null,
	int? MeshInfoIndex = null,
	bool IsPlaceholderMesh = false);
