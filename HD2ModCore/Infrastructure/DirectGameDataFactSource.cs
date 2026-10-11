using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：直读版 game-data 事实来源——直接问旁路服务，不经过信息中心。
// Purpose: The direct game-data fact source: asks the bypass services, never the information center.
//
// 这是给 CLI 只读检查用的（`CreateDirectGameDataArchiveBrowserService`）。
// 它刻意不创建也不刷新任何中心缓存，这样 `gamedata browser` 仍然是一条纯粹的读命令。
// 它也是**唯一**允许直接提到这三个旁路服务类型的地方——投影代码通过 IGameDataFactSource 与它们解耦，
// 因此"业务服务不得直调旁路服务"这条护栏才可能成立（设计文档 §7 护栏 1）。
internal sealed class DirectGameDataFactSource : IGameDataFactSource
{
	private readonly IAssetArchiveIndexService _indexService;
	private readonly IGameDataMappingFactsService _mappingService;
	private readonly IDeployedOverrideGraphService _deployedGraphService;

	public DirectGameDataFactSource(
		IAssetArchiveIndexService indexService,
		IGameDataMappingFactsService mappingService,
		IDeployedOverrideGraphService deployedGraphService)
	{
		_indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
		_mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
		_deployedGraphService = deployedGraphService ?? throw new ArgumentNullException(nameof(deployedGraphService));
	}

	public async ValueTask<GameDataIndexFacts?> GetIndexAsync(CancellationToken cancellationToken = default)
	{
		var fingerprint = await _indexService.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		if (fingerprint is null) return null;
		var archives = await _indexService.GetArchiveSummariesAsync(cancellationToken).ConfigureAwait(false);
		return new GameDataIndexFacts(fingerprint, archives);
	}

	public ValueTask<GameDataMappingFacts> GetMappingAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
		=> _mappingService.MapAsync(assetKeys, cancellationToken);

	public ValueTask<DeployedOverrideGraph> GetDeployedGraphAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
		=> _deployedGraphService.BuildAsync(gameDataDirectory, cancellationToken);
}
