using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Infrastructure;

// Purpose: Produces readable Mod summaries solely from stable content facts and Game Data mapping projections.
public sealed class ModAssetSummaryProjector
{
	private readonly IModInformationCenter informationCenter;
	private readonly IAssetMetadataCatalogProvider? fallbackCatalogProvider;
	private readonly RuntimeArmorRegistryService? registry;

	public ModAssetSummaryProjector(IModInformationCenter informationCenter, IAssetMetadataCatalogProvider? fallbackCatalogProvider = null,
		RuntimeArmorRegistryService? registry = null)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		this.fallbackCatalogProvider = fallbackCatalogProvider;
		this.registry = registry;
	}

	public async ValueTask<ModAssetSummary> ProjectAsync(ModNode node, ModContentFacts facts, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(facts);
		var sourceByKey = BuildSourceByKey(facts);
		var mapping = await BuildMappingAsync(sourceByKey.Keys.ToHashSet(), cancellationToken).ConfigureAwait(false);
		return Project(node, sourceByKey, mapping, await ResolveCatalogAsync(mapping, cancellationToken).ConfigureAwait(false));
	}

	public async ValueTask<ModAssetStructureFacts> ProjectStructureAsync(ModNode node, ModContentFacts facts, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(facts);
		var mapping = await BuildMappingAsync(facts.Structure.Assets.Select(asset => asset.AssetKey).ToHashSet(), cancellationToken).ConfigureAwait(false);
		return ProjectStructure(facts.Structure, mapping);
	}

	public async ValueTask<IReadOnlyDictionary<ModNodeId, ModAssetSummary>> ProjectManyAsync(IReadOnlyDictionary<ModNode, ModContentFacts> factsByNode, CancellationToken cancellationToken = default)
		=> (await ProjectManyWithGenerationAsync(factsByNode, cancellationToken).ConfigureAwait(false)).Summaries;

	public async ValueTask<ModAssetSummaryProjection> ProjectManyWithGenerationAsync(IReadOnlyDictionary<ModNode, ModContentFacts> factsByNode, CancellationToken cancellationToken = default)
	{
		var sourceByNode = factsByNode.ToDictionary(pair => pair.Key, pair => BuildSourceByKey(pair.Value));
		var mapping = await BuildMappingAsync(sourceByNode.Values.SelectMany(value => value.Keys).ToHashSet(), cancellationToken).ConfigureAwait(false);
		var catalog = await ResolveCatalogAsync(mapping, cancellationToken).ConfigureAwait(false);
		var summaries = sourceByNode.ToDictionary(pair => pair.Key.Id, pair => Project(pair.Key, pair.Value, mapping, catalog));
		var structures = factsByNode.ToDictionary(pair => pair.Key.Id, pair => ProjectStructure(pair.Value.Structure, mapping));
		return new ModAssetSummaryProjection(mapping.MappingGeneration, summaries, structures);
	}

	public async ValueTask<string> GetMappingGenerationAsync(CancellationToken cancellationToken = default)
		=> (await BuildMappingAsync(new HashSet<AssetKey>(), cancellationToken).ConfigureAwait(false)).MappingGeneration;

	// 作用：取回映射事实，再把"运行时护甲注册表里的 Unit 标签"叠加上去。
	// Purpose: Fetches mapping facts, then overlays the Unit labels from the runtime armor registry.
	//
	// 注意这里**不是**直调映射服务：统一身份、并发合并、诊断事件只在信息中心实现（设计文档 §28）。
	private async ValueTask<GameDataMappingFacts> BuildMappingAsync(IReadOnlySet<AssetKey> keys, CancellationToken token)
	{
		var mapping = (await informationCenter.RequestGameDataMappingAsync(
			keys,
			new ModInformationRequest(ModInformationKind.GameDataMapping, "ModAssetSummary"),
			token).ConfigureAwait(false)).AsMappingFacts();
		var models = registry is null ? Array.Empty<RuntimeArmorRegistryModel>()
			: await registry.ReadWorkspaceModelsAsync(token).ConfigureAwait(false);
		// Include reserved/disabled IDs: these still identify physical resources in a Patch.
		var owners = models.SelectMany(model => (model.Pieces ?? []).Select(piece => (model, piece)))
			.Where(item => ulong.TryParse(item.piece.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
			.GroupBy(item => ulong.Parse(item.piece.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
			.ToDictionary(group => group.Key, group => group.Select(item =>
				new ArchiveMetadata("custom-model:" + item.model.Key, "CustomModel", item.model.Name ?? item.model.Key))
				.Distinct().ToArray());
		var assets = mapping.Assets.ToDictionary(pair => pair.Key, pair => pair.Value);
		foreach (var key in keys.Where(key => key.TypeId == 0xe0a48d0be9a7453f))
			if (owners.TryGetValue(key.FileId, out var archives)
				&& (!assets.TryGetValue(key, out var existing) || existing.TargetArchives.Count == 0))
				assets[key] = new(key, $"0x{key.FileId:x16}", "Unit", AssetTypeCategory.Model, archives);
		var identity = string.Join("\n", models.OrderBy(m => m.Key, StringComparer.Ordinal)
			.Select(m => $"{m.Key}|{m.Name}|{string.Join(",", (m.Pieces ?? []).Select(p => p.Unit).Order(StringComparer.Ordinal))}"));
		var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
		return mapping with { Assets = assets, MappingGeneration = mapping.MappingGeneration + ":unit-labels-v2:" + version };
	}

	private async ValueTask<AssetMetadataCatalog> ResolveCatalogAsync(GameDataMappingFacts mapping, CancellationToken cancellationToken)
		=> mapping.Catalog
			?? (fallbackCatalogProvider is null
				? AssetMetadataCatalog.Empty
				: await fallbackCatalogProvider.LoadAsync(cancellationToken).ConfigureAwait(false));

	private static Dictionary<AssetKey, SourceInfo> BuildSourceByKey(ModContentFacts facts)
		=> facts.Structure.Assets.ToDictionary(
			asset => asset.AssetKey,
			asset => new SourceInfo(
				asset.Occurrences.Select(occurrence => occurrence.PatchGroupId.SourceArchiveHex).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
				asset.Occurrences.Select(occurrence => occurrence.SourceFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()));

	private static ModAssetStructureFacts ProjectStructure(ModAssetStructureFacts source, GameDataMappingFacts mapping)
	{
		var assets = source.Assets
			.Select(asset => asset with
			{
				TargetArchives = mapping.Assets.TryGetValue(asset.AssetKey, out var mapped)
					? mapped.TargetArchives
					: Array.Empty<ArchiveMetadata>(),
			})
			.ToArray();
		var targetArchives = assets
			.SelectMany(asset => asset.MappedTargetArchives.Select(archive => (archive, asset.AssetKey)))
			.GroupBy(item => item.archive.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.Select(group => new ModAssetStructureTargetArchiveFact(
				group.First().archive,
				group.Select(item => item.AssetKey).Distinct().OrderBy(key => key.TypeId).ThenBy(key => key.FileId).ToArray()))
			.OrderBy(item => item.Archive.CategoryOrder)
			.ThenBy(item => item.Archive.ArchiveOrder)
			.ThenBy(item => item.Archive.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return source with
		{
			Assets = assets,
			TargetArchives = targetArchives,
			MappingGeneration = mapping.MappingGeneration,
			Issues = source.Issues.Concat(mapping.Issues).Distinct().ToArray(),
		};
	}

	private static ModAssetSummary Project(ModNode node, IReadOnlyDictionary<AssetKey, SourceInfo> sourceByKey, GameDataMappingFacts mapping, AssetMetadataCatalog catalog)
	{
		var assets = sourceByKey
			.OrderBy(pair => pair.Key.TypeId)
			.ThenBy(pair => pair.Key.FileId)
			.Select(pair => CreateEntry(pair.Key, pair.Value, mapping.Assets.GetValueOrDefault(pair.Key), catalog))
			.ToArray();
		// Unit-bearing patches describe armor replacements by Unit ownership, not by
		// retained materials/textures or the source container's name. Keep dependencies
		// in Assets for inspection, including genuine material-only patches.
		var hasCustomUnits = assets.Any(asset => asset.Key.TypeId == 0xe0a48d0be9a7453f
			&& asset.ArchiveCategory.Equals("CustomModel", StringComparison.OrdinalIgnoreCase));
		var targets = assets.Where(asset => !hasCustomUnits || asset.Key.TypeId == 0xe0a48d0be9a7453f
			|| !(asset.ArchiveCategory.Equals("Armor", StringComparison.OrdinalIgnoreCase)
				|| asset.ArchiveCategory.Equals("Helmet", StringComparison.OrdinalIgnoreCase))).ToArray();
		var tags = targets.SelectMany(asset => asset.DerivedTags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
		return new ModAssetSummary(node.Id, node.Metadata.Name, assets, tags, BuildTargetGroups(targets));
	}

	private static PatchAssetEntry CreateEntry(AssetKey key, SourceInfo source, GameDataMappedAssetFact? mapped, AssetMetadataCatalog catalog)
	{
		var archives = mapped?.TargetArchives ?? Array.Empty<ArchiveMetadata>();
		var archive = archives.FirstOrDefault();
		var fallbackArchive = key.TypeId == 0xe0a48d0be9a7453f ? null : source.SourceArchiveHexes
			.Select(catalog.FindArchive)
			.FirstOrDefault(archive => archive is not null);
		var category = archive?.Category ?? fallbackArchive?.Category ?? "Unknown";
		var displayName = archive?.DisplayName ?? fallbackArchive?.DisplayName ?? "Mod 私有资源";
		var type = mapped?.TypeDisplayName ?? catalog.FindType(key.TypeId)?.Name ?? TypeName(key.TypeId);
		var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		if (category != "Unknown") tags.Add(category.ToLowerInvariant());
		if (key.TypeId == 0xe0a48d0be9a7453f || key.TypeId == 0xc4f0f4be7fb0c8d6) tags.Add("model");
		if (key.TypeId == 0xeac0b497876adedf) tags.Add("material");
		if (key.TypeId == 0xcd4238c6a0c69e32) tags.Add("texture");
		if (archive is not null) tags.Add(archive.DisplayName);
		return new PatchAssetEntry(
			new PatchAssetKey("stable-facts", key.TypeId, key.FileId), displayName, category, int.MaxValue, int.MaxValue,
			mapped?.FileDisplayName ?? catalog.FindFile(key.FileId)?.FriendlyName ?? $"0x{key.FileId:x16}", type, mapped?.TypeCategory ?? catalog.FindType(key.TypeId)?.Category ?? AssetTypeCategory.Unknown,
			tags.ToArray(), source.FileNames, archives.Select(item => item.ArchiveId).ToArray());
	}

	private static IReadOnlyList<ModAssetTargetGroup> BuildTargetGroups(IReadOnlyList<PatchAssetEntry> assets)
		=> assets
			.Where(asset => !string.Equals(asset.ArchiveCategory, "Unknown", StringComparison.OrdinalIgnoreCase))
			.GroupBy(asset => asset.ArchiveCategory, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.Select((group, index) => new ModAssetTargetGroup(group.Key, index,
				group.GroupBy(asset => asset.ArchiveDisplayName, StringComparer.OrdinalIgnoreCase)
					.OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
					.Select(target => new ModAssetTargetItem(target.Key, int.MaxValue, target.SelectMany(asset => asset.SemanticTargetArchiveIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), target.Select(asset => asset.TypeDisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), target.Count()))
					.ToArray(), group.Count()))
			.ToArray();

	private static string TypeName(ulong typeId) => typeId switch
	{
		0xe0a48d0be9a7453f => "Unit",
		0xc4f0f4be7fb0c8d6 => "Composite Unit",
		0xeac0b497876adedf => "Material",
		0xcd4238c6a0c69e32 => "Texture",
		_ => $"0x{typeId:x16}",
	};

	private sealed record SourceInfo(IReadOnlyList<string> SourceArchiveHexes, IReadOnlyList<string> FileNames);
}

// Purpose: Separates stable Mod content facts from the versioned Game Data label projection.
public sealed record ModAssetSummaryProjection(
	string MappingGeneration,
	IReadOnlyDictionary<ModNodeId, ModAssetSummary> Summaries,
	IReadOnlyDictionary<ModNodeId, ModAssetStructureFacts>? Structures = null);
