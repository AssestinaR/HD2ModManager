using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Infrastructure;

// Purpose: Produces readable Mod summaries solely from stable content facts and Game Data mapping projections.
public sealed class ModAssetSummaryProjector
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;

	private static readonly IReadOnlyDictionary<ulong, ArchiveMetadata> NoOwnLabels
		= new Dictionary<ulong, ArchiveMetadata>();

	private readonly IModInformationCenter informationCenter;
	private readonly IAssetMetadataCatalogProvider? fallbackCatalogProvider;
	private readonly RuntimeArmorRegistryService? registry;
	private readonly string? modsRootDirectory;

	public ModAssetSummaryProjector(IModInformationCenter informationCenter, IAssetMetadataCatalogProvider? fallbackCatalogProvider = null,
		RuntimeArmorRegistryService? registry = null, string? modsRootDirectory = null)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		this.fallbackCatalogProvider = fallbackCatalogProvider;
		this.registry = registry;
		this.modsRootDirectory = string.IsNullOrWhiteSpace(modsRootDirectory) ? null : modsRootDirectory;
	}

	public async ValueTask<ModAssetSummary> ProjectAsync(ModNode node, ModContentFacts facts, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(facts);
		var sourceByKey = BuildSourceByKey(facts);
		var labels = await ReadRegistryLabelsAsync(cancellationToken).ConfigureAwait(false);
		var mapping = await BuildMappingAsync(sourceByKey.Keys.ToHashSet(), labels, cancellationToken).ConfigureAwait(false);
		var catalog = await ResolveCatalogAsync(mapping, cancellationToken).ConfigureAwait(false);
		return Project(node, sourceByKey, mapping, catalog, OwnUnitLabels(node, labels));
	}

	public async ValueTask<ModAssetStructureFacts> ProjectStructureAsync(ModNode node, ModContentFacts facts, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(facts);
		var labels = await ReadRegistryLabelsAsync(cancellationToken).ConfigureAwait(false);
		var mapping = await BuildMappingAsync(facts.Structure.Assets.Select(asset => asset.AssetKey).ToHashSet(), labels, cancellationToken).ConfigureAwait(false);
		return ProjectStructure(facts.Structure, mapping);
	}

	public async ValueTask<IReadOnlyDictionary<ModNodeId, ModAssetSummary>> ProjectManyAsync(IReadOnlyDictionary<ModNode, ModContentFacts> factsByNode, CancellationToken cancellationToken = default)
		=> (await ProjectManyWithGenerationAsync(factsByNode, cancellationToken).ConfigureAwait(false)).Summaries;

	public async ValueTask<ModAssetSummaryProjection> ProjectManyWithGenerationAsync(IReadOnlyDictionary<ModNode, ModContentFacts> factsByNode, CancellationToken cancellationToken = default)
	{
		var sourceByNode = factsByNode.ToDictionary(pair => pair.Key, pair => BuildSourceByKey(pair.Value));
		var labels = await ReadRegistryLabelsAsync(cancellationToken).ConfigureAwait(false);
		var mapping = await BuildMappingAsync(sourceByNode.Values.SelectMany(value => value.Keys).ToHashSet(), labels, cancellationToken).ConfigureAwait(false);
		var catalog = await ResolveCatalogAsync(mapping, cancellationToken).ConfigureAwait(false);
		var summaries = sourceByNode.ToDictionary(pair => pair.Key.Id,
			pair => Project(pair.Key, pair.Value, mapping, catalog, OwnUnitLabels(pair.Key, labels)));
		var structures = factsByNode.ToDictionary(pair => pair.Key.Id, pair => ProjectStructure(pair.Value.Structure, mapping));
		return new ModAssetSummaryProjection(mapping.MappingGeneration, summaries, structures);
	}

	public async ValueTask<string> GetMappingGenerationAsync(CancellationToken cancellationToken = default)
		=> (await BuildMappingAsync(new HashSet<AssetKey>(), await ReadRegistryLabelsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)).MappingGeneration;

	/// <summary>
	/// 运行时注册表里的素体，以及"哪个 unit 属于哪具素体"。
	/// <para>
	/// <b>为什么要有 <see cref="ByModel"/>。</b>标注 unit 时有两种口径，差别很大：
	/// 游戏数据查不到这个 unit 时用任意素体兜底（解包出来的 patch 也能读懂），
	/// 但<b>这个 Mod 自己的素体</b>拥有这个 unit 时要以它为准——它压过的可能是真实的游戏护甲名。
	/// </para>
	/// </summary>
	private sealed record RegistryLabels(
		IReadOnlyDictionary<ulong, IReadOnlyList<RegistryUnitLabel>> Units,
		IReadOnlyDictionary<string, IReadOnlyDictionary<ulong, ArchiveMetadata>> ByModel,
		string Generation);

	private sealed record RegistryUnitLabel(string ModelKey, ArchiveMetadata Archive);

	// 作用：取回映射事实，再把"运行时护甲注册表里的 Unit 标签"叠加上去。
	// Purpose: Fetches mapping facts, then overlays the Unit labels from the runtime armor registry.
	//
	// 注意这里**不是**直调映射服务：统一身份、并发合并、诊断事件只在信息中心实现（设计文档 §28）。
	private async ValueTask<GameDataMappingFacts> BuildMappingAsync(IReadOnlySet<AssetKey> keys, RegistryLabels labels, CancellationToken token)
	{
		var mapping = (await informationCenter.RequestGameDataMappingAsync(
			keys,
			new ModInformationRequest(ModInformationKind.GameDataMapping, "ModAssetSummary"),
			token).ConfigureAwait(false)).AsMappingFacts();
		// Include reserved/disabled IDs: these still identify physical resources in a Patch.
		var assets = mapping.Assets.ToDictionary(pair => pair.Key, pair => pair.Value);
		foreach (var key in keys.Where(key => key.TypeId == UnitTypeId))
			if (labels.Units.TryGetValue(key.FileId, out var unitLabels)
				&& (!assets.TryGetValue(key, out var existing) || existing.TargetArchives.Count == 0))
				assets[key] = new(key, $"0x{key.FileId:x16}", "Unit", AssetTypeCategory.Model,
					unitLabels.Select(label => label.Archive).Distinct().ToArray());
		return mapping with { Assets = assets, MappingGeneration = mapping.MappingGeneration + ":unit-labels-v2:" + labels.Generation };
	}

	private async ValueTask<RegistryLabels> ReadRegistryLabelsAsync(CancellationToken token)
	{
		var models = registry is null ? Array.Empty<RuntimeArmorRegistryModel>()
			: await registry.ReadWorkspaceModelsAsync(token).ConfigureAwait(false);
		var units = new Dictionary<ulong, List<RegistryUnitLabel>>();
		var byModel = new Dictionary<string, Dictionary<ulong, ArchiveMetadata>>(StringComparer.OrdinalIgnoreCase);
		foreach (var model in models)
		{
			var archive = new ArchiveMetadata("custom-model:" + model.Key, "CustomModel", model.Name ?? model.Key);
			if (!byModel.TryGetValue(model.Key, out var own))
				byModel[model.Key] = own = [];
			foreach (var piece in model.Pieces ?? [])
			{
				if (!ulong.TryParse(piece.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unit))
					continue;
				if (!units.TryGetValue(unit, out var list))
					units[unit] = list = [];
				list.Add(new RegistryUnitLabel(model.Key, archive));
				own[unit] = archive;
			}
		}

		var identity = string.Join("\n", models.OrderBy(m => m.Key, StringComparer.Ordinal)
			.Select(m => $"{m.Key}|{m.Name}|{string.Join(",", (m.Pieces ?? []).Select(p => p.Unit).Order(StringComparer.Ordinal))}"));
		return new RegistryLabels(
			units.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RegistryUnitLabel>)pair.Value),
			byModel.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<ulong, ArchiveMetadata>)pair.Value, StringComparer.OrdinalIgnoreCase),
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
	}

	/// <summary>
	/// 这个 Mod <b>自己</b>登记的素体拥有的 unit：<c>custom-model.json</c> 的 key / 名字 / 件。
	/// <para>
	/// <b>先读文件，再查注册表。</b>文件是归属凭证，注册表只是它的投影；转换刚写完文件时注册表可能
	/// 还没同步到（同步时机由 app 启动/库刷新决定），那时只查注册表就会把一具素体答成"原版护甲"。
	/// 只有文件里没有件（旧文件、或只有 key）时才退回注册表。
	/// </para>
	/// <para>
	/// <b>为什么必须限定"自己的"。</b>sectional 产物把**真实游戏 unit id** 登记进了自己的素体里
	/// （它复用了这些 unit 壳）。若按"任何素体拥有"就覆盖，那么任何替换同一件护甲的手写 Mod
	/// 都会被标成别人的素体名——那比"显示成游戏护甲"更糟。
	/// </para>
	/// </summary>
	private IReadOnlyDictionary<ulong, ArchiveMetadata> OwnUnitLabels(ModNode node, RegistryLabels labels)
	{
		if (modsRootDirectory is null)
			return NoOwnLabels;

		string directory;
		try
		{
			directory = Path.Combine(modsRootDirectory, node.RelativePath);
		}
		catch (ArgumentException)
		{
			return NoOwnLabels;
		}

		var model = VpkCustomModelFile.TryReadModel(directory);
		if (model is not null)
		{
			var archive = new ArchiveMetadata("custom-model:" + model.Key, "CustomModel", model.Name ?? model.Key);
			var own = new Dictionary<ulong, ArchiveMetadata>();
			foreach (var piece in model.Pieces ?? [])
			{
				if (ulong.TryParse(piece.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unit))
					own[unit] = archive;
			}

			if (own.Count != 0)
				return own;
		}

		var key = model?.Key ?? VpkCustomModelFile.TryReadKey(directory);
		return string.IsNullOrWhiteSpace(key) || !labels.ByModel.TryGetValue(key, out var fromRegistry)
			? NoOwnLabels
			: fromRegistry;
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

	private static ModAssetSummary Project(ModNode node, IReadOnlyDictionary<AssetKey, SourceInfo> sourceByKey, GameDataMappingFacts mapping, AssetMetadataCatalog catalog,
		IReadOnlyDictionary<ulong, ArchiveMetadata> ownUnitLabels)
	{
		var assets = sourceByKey
			.OrderBy(pair => pair.Key.TypeId)
			.ThenBy(pair => pair.Key.FileId)
			.Select(pair => CreateEntry(pair.Key, pair.Value, mapping.Assets.GetValueOrDefault(pair.Key), catalog,
				pair.Key.TypeId == UnitTypeId ? ownUnitLabels.GetValueOrDefault(pair.Key.FileId) : null))
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

	private static PatchAssetEntry CreateEntry(AssetKey key, SourceInfo source, GameDataMappedAssetFact? mapped, AssetMetadataCatalog catalog,
		ArchiveMetadata? ownArchive)
	{
		// 本 Mod 自己的素体拥有这个 unit ⇒ 它的身份是那具素体，不是"游戏里谁在用这个 unit 壳"。
		// 两条路必须分开看：vpk2patch 的 unit 键是自造键，游戏数据里根本没有（走下面那层的兜底）；
		// sectional 的 unit 键是真实游戏键，游戏数据答得出护甲名——而那个名字只是复用壳的副作用。
		var archives = ownArchive is not null
			? new[] { ownArchive }
			: mapped?.TargetArchives ?? Array.Empty<ArchiveMetadata>();
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
