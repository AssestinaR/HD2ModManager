using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Aggregates archive-level library, expected and deployed facts behind one asynchronous Core query.
public sealed class GameDataArchiveBrowserService : IGameDataArchiveBrowserService
{
	private readonly IAssetArchiveIndexService _indexService;
	private readonly IModInformationCenter? _informationCenter;
	private readonly IAssetInventoryProducer? _directInventory;
	private readonly IGameDataMappingFactsService _mappingFactsService;
	private readonly IDeployedOverrideGraphService _deployedGraphService;

	public GameDataArchiveBrowserService(IAssetArchiveIndexService indexService, IModInformationCenter informationCenter, IGameDataMappingFactsService mappingFactsService, IDeployedOverrideGraphService deployedGraphService)
	{
		_indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
		_informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		_mappingFactsService = mappingFactsService ?? throw new ArgumentNullException(nameof(mappingFactsService));
		_deployedGraphService = deployedGraphService ?? throw new ArgumentNullException(nameof(deployedGraphService));
	}

	// The CLI uses this overload because its inspection commands must not create
	// or refresh Manager caches merely to render a GameData browser projection.
	// The producer reads Patch facts directly and keeps the browser's overlay
	// semantics identical to the cached desktop path.
	public GameDataArchiveBrowserService(IAssetArchiveIndexService indexService, IAssetInventoryProducer directInventory, IGameDataMappingFactsService mappingFactsService, IDeployedOverrideGraphService deployedGraphService)
	{
		_indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
		_directInventory = directInventory ?? throw new ArgumentNullException(nameof(directInventory));
		_mappingFactsService = mappingFactsService ?? throw new ArgumentNullException(nameof(mappingFactsService));
		_deployedGraphService = deployedGraphService ?? throw new ArgumentNullException(nameof(deployedGraphService));
	}

	public async ValueTask<GameDataArchiveBrowserSnapshot?> BuildAsync(LibrarySnapshot snapshot, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default)
	{
		var fingerprint = await _indexService.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		if (fingerprint is null) return null;
		var archives = await _indexService.GetArchiveSummariesAsync(cancellationToken).ConfigureAwait(false);
		var content = await GetAssetInventoryAsync(snapshot, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		var allKeys = content.Values.SelectMany(facts => facts.PatchGroups).SelectMany(group => group.AssetKeys).ToHashSet();
		var mapping = await _mappingFactsService.MapAsync(allKeys, cancellationToken).ConfigureAwait(false);
		var deployed = await _deployedGraphService.BuildAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
		var activeIds = snapshot.ActiveProfileId is { } activeId
			? snapshot.Profiles.FirstOrDefault(profile => profile.Id == activeId)?.Entries.Select(entry => entry.NodeId).ToHashSet() ?? []
			: [];
		var modsByArchive = new Dictionary<string, HashSet<ModNodeId>>(StringComparer.OrdinalIgnoreCase);
		var activeByArchive = new Dictionary<string, HashSet<ModNodeId>>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in content)
		{
			foreach (var assetKey in pair.Value.PatchGroups.SelectMany(group => group.AssetKeys).Distinct())
			{
				if (!mapping.Assets.TryGetValue(assetKey, out var mapped)) continue;
				foreach (var archive in mapped.TargetArchives)
				{
					GetSet(modsByArchive, archive.ArchiveId).Add(pair.Key);
					if (activeIds.Contains(pair.Key)) GetSet(activeByArchive, archive.ArchiveId).Add(pair.Key);
				}
			}
		}

		var effectiveByArchive = new Dictionary<string, HashSet<ModNodeId>>(StringComparer.OrdinalIgnoreCase);
		var indexesByArchive = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
		var effectiveAssetsByArchive = new Dictionary<string, List<GameDataEffectiveAsset>>(StringComparer.OrdinalIgnoreCase);
		var competitionArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var chain in deployed.AssetChains)
		{
			if (!mapping.Assets.TryGetValue(chain.AssetKey, out var mapped)) continue;
			foreach (var archive in mapped.TargetArchives)
			{
				if (chain.Winner.NodeId is { } winner) GetSet(effectiveByArchive, archive.ArchiveId).Add(winner);
				GetSet(indexesByArchive, archive.ArchiveId).Add(chain.Winner.TargetPatchIndex);
				GetList(effectiveAssetsByArchive, archive.ArchiveId).Add(new GameDataEffectiveAsset(chain.AssetKey, chain.Winner.NodeId, chain.Winner.TargetPatchIndex, chain.IsCompetition));
				if (chain.IsCompetition) competitionArchives.Add(archive.ArchiveId);
			}
		}

		var items = archives.Select(archive => new GameDataArchiveBrowserItem(archive, new GameDataArchiveOverlay(
			archive.PackageName,
			GetValues(modsByArchive, archive.PackageName),
			GetValues(activeByArchive, archive.PackageName),
			GetValues(effectiveByArchive, archive.PackageName),
			GetValues(indexesByArchive, archive.PackageName),
			GetValues(effectiveAssetsByArchive, archive.PackageName),
			competitionArchives.Contains(archive.PackageName),
			deployed.Issues.Where(issue => issue.NodeId is { } nodeId && GetValues(modsByArchive, archive.PackageName).Contains(nodeId)).ToList()))).ToList();
		var names = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value.Metadata.Name);
		var contentIssues = content.Values.SelectMany(facts => facts.Issues).ToList();
		return new GameDataArchiveBrowserSnapshot(fingerprint, snapshot.ActiveProfileId, items, names, contentIssues.Concat(mapping.Issues).Concat(deployed.Issues).ToList());
	}

	private async ValueTask<IReadOnlyDictionary<ModNodeId, ModContentFacts>> GetAssetInventoryAsync(LibrarySnapshot snapshot, string modsRootDirectory, CancellationToken cancellationToken)
	{
		var result = new Dictionary<ModNodeId, ModContentFacts>();
		foreach (var node in snapshot.Nodes.Values)
		{
			if (_directInventory is not null)
			{
				result[node.Id] = await _directInventory.GetNodeFactsAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
				continue;
			}
			var inventory = await _informationCenter!.RequestAssetInventoryAsync(node, modsRootDirectory, new ModInformationRequest(ModInformationKind.AssetInventory, "GameDataArchiveBrowser"), cancellationToken).ConfigureAwait(false);
			if (inventory.Data is not null) result[node.Id] = inventory.Data;
		}
		return result;
	}

	private static HashSet<T> GetSet<T>(IDictionary<string, HashSet<T>> dictionary, string key) where T : notnull
	{
		if (!dictionary.TryGetValue(key, out var set)) dictionary[key] = set = [];
		return set;
	}

	private static IReadOnlyList<T> GetValues<T>(IReadOnlyDictionary<string, HashSet<T>> dictionary, string key) where T : notnull
		=> dictionary.TryGetValue(key, out var set) ? set.ToList() : [];

	private static List<T> GetList<T>(IDictionary<string, List<T>> dictionary, string key)
	{
		if (!dictionary.TryGetValue(key, out var list)) dictionary[key] = list = [];
		return list;
	}

	private static IReadOnlyList<T> GetValues<T>(IReadOnlyDictionary<string, List<T>> dictionary, string key)
		=> dictionary.TryGetValue(key, out var list) ? list : [];
}
