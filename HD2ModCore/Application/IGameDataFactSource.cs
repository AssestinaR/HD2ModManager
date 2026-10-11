using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：浏览器投影所需的三类 game-data 事实的来源。有了它，投影代码不必认识任何旁路服务。
// Purpose: The source of the three game-data facts a browser projection needs, so projection code never names a bypass service.
//
// 两种实现（设计文档 §22）：
//   · 中心版（`CenterGameDataFactSource`）——走 `IModInformationCenter`：统一身份、并发合并、诊断事件；
//   · 直读版（`DirectGameDataFactSource`）——直接问旁路服务，刻意**不创建也不刷新任何中心缓存**，
//     供 CLI 的只读检查命令使用（`gamedata browser` 不该为了渲染一个投影去写派生事实）。
//
// Two implementations: a center-backed one for the Manager, and a direct one for the CLI's read-only
// inspection commands, which must not create or refresh any information-center cache.
public interface IGameDataFactSource
{
	// 作用：读资产索引指纹与全部归档摘要；索引不存在时返回 null（不是错误——只是还没建索引）。
	ValueTask<GameDataIndexFacts?> GetIndexAsync(CancellationToken cancellationToken = default);

	// 作用：把一批资产键映射到它们的目标归档。
	ValueTask<GameDataMappingFacts> GetMappingAsync(
		IReadOnlySet<AssetKey> assetKeys,
		CancellationToken cancellationToken = default);

	// 作用：读实际部署图（Data 目录里的 patch 文件与激活状态对账后的结果）。
	ValueTask<DeployedOverrideGraph> GetDeployedGraphAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default);
}
