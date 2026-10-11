using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Aggregates archive-level library, expected and deployed facts behind one asynchronous Core query.
//
// 投影只认识 IGameDataFactSource，不认识任何 game-data 旁路服务——这是"业务服务不得直调旁路服务"
// 这条护栏（设计文档 §7 护栏 1）能成立的前提。两个构造重载分别对应中心路径与 CLI 直读路径。
// The projection only knows IGameDataFactSource, never a bypass service: that is what lets the
// "business services must not call game-data bypass services" guardrail hold.
public sealed class GameDataArchiveBrowserService : IGameDataArchiveBrowserService
{
	private readonly IGameDataFactSource _factSource;
	private readonly IModInformationCenter? _informationCenter;
	private readonly IAssetInventoryProducer? _directInventory;

	public GameDataArchiveBrowserService(IGameDataFactSource factSource, IModInformationCenter informationCenter)
	{
		_factSource = factSource ?? throw new ArgumentNullException(nameof(factSource));
		_informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
	}

	// The CLI uses this overload because its inspection commands must not create
	// or refresh Manager caches merely to render a GameData browser projection.
	// The producer reads Patch facts directly and keeps the browser's overlay
	// semantics identical to the cached desktop path.
	public GameDataArchiveBrowserService(IGameDataFactSource factSource, IAssetInventoryProducer directInventory)
	{
		_factSource = factSource ?? throw new ArgumentNullException(nameof(factSource));
		_directInventory = directInventory ?? throw new ArgumentNullException(nameof(directInventory));
	}

	public async ValueTask<GameDataArchiveBrowserSnapshot?> BuildAsync(LibrarySnapshot snapshot, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default)
	{
		var index = await _factSource.GetIndexAsync(cancellationToken).ConfigureAwait(false);
		if (index is null) return null;
		var content = await GetAssetInventoryAsync(snapshot, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		var allKeys = content.Values.SelectMany(facts => facts.PatchGroups).SelectMany(group => group.AssetKeys).ToHashSet();
		var mapping = await _factSource.GetMappingAsync(allKeys, cancellationToken).ConfigureAwait(false);
		var deployed = await _factSource.GetDeployedGraphAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
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

		var items = index.Archives.Select(archive => new GameDataArchiveBrowserItem(archive, new GameDataArchiveOverlay(
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
		return new GameDataArchiveBrowserSnapshot(index.Fingerprint, snapshot.ActiveProfileId, items, names, contentIssues.Concat(mapping.Issues).Concat(deployed.Issues).ToList());
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
