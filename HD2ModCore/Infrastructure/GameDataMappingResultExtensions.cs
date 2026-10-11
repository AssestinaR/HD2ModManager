using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：把信息中心的无节点映射结果收敛成调用方可以直接消费的映射事实。
// Purpose: Collapses the center's node-less mapping result into mapping facts a caller can consume directly.
//
// 为什么需要它（设计文档 §28）：中心把"数据"与"诊断"分开表达——ModInformationResult<T>.Data 为 null
// 表示"索引还没建"或"生产失败"，失败原因在 Issues 里。而改造前各业务服务直调 MapAsync 时，
// 绝大多数失败已被 MapAsync 内部吞成"空映射 + issues"，只有灾难性失败才会抛出来。
// 所以这里给出等价形态：空映射 + 原样带上中心的 issues，而不是让 6 个调用方各自判空、
// 各自决定"失败了算什么"——那正是本次收口要消掉的重复。
//
// 注意：Data 为 null 时 MappingGeneration 只能是 "unavailable" 这个哨兵值，
// 它不等于"映射没变"。因此调用方**不得**把它当作"内容未变"的证据来做长期缓存决策。
internal static class GameDataMappingResultExtensions
{
	internal const string UnavailableGeneration = "unavailable";

	internal static GameDataMappingFacts AsMappingFacts(this ModInformationResult<GameDataMappingFacts> result)
		=> result.Data ?? new GameDataMappingFacts(
			result.Generation ?? UnavailableGeneration,
			UnavailableGeneration,
			UnavailableGeneration,
			DateTimeOffset.UtcNow,
			new Dictionary<AssetKey, GameDataMappedAssetFact>(),
			result.Issues);
}
