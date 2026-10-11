using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：中心版 game-data 事实来源——走信息中心，享受统一身份、并发合并与诊断事件。
// Purpose: The center-backed game-data fact source, so the projection goes through the information center.
//
// 这一层存在的意义是**解耦**：投影代码只需要 IGameDataFactSource，不必认识
// IAssetArchiveIndexService / IGameDataMappingFactsService / IDeployedOverrideGraphService 中的任何一个。
// 于是"业务服务不得直调 game-data 旁路服务"这条护栏才有可能成立（设计文档 §7 护栏 1、§22）。
internal sealed class CenterGameDataFactSource : IGameDataFactSource
{
	private const string Source = "GameDataFactSource";
	private readonly IModInformationCenter _center;

	public CenterGameDataFactSource(IModInformationCenter center)
		=> _center = center ?? throw new ArgumentNullException(nameof(center));

	public async ValueTask<GameDataIndexFacts?> GetIndexAsync(CancellationToken cancellationToken = default)
	{
		var result = await _center
			.RequestGameDataIndexAsync(new ModInformationRequest(ModInformationKind.GameDataIndex, Source), cancellationToken)
			.ConfigureAwait(false);
		// 索引还没建时中心返回 Unavailable——那不是错误，与直读路径返回 null 的语义一致。
		return result.Status == ModInformationStatus.Unavailable ? null : Require(result, "资产索引");
	}

	public async ValueTask<GameDataMappingFacts> GetMappingAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
		=> Require(
			await _center.RequestGameDataMappingAsync(assetKeys, new ModInformationRequest(ModInformationKind.GameDataMapping, Source), cancellationToken).ConfigureAwait(false),
			"资产映射");

	public async ValueTask<DeployedOverrideGraph> GetDeployedGraphAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
		=> Require(
			await _center.RequestDeployedGraphAsync(gameDataDirectory, new ModInformationRequest(ModInformationKind.DeployedGraph, Source), cancellationToken).ConfigureAwait(false),
			"实际部署图");

	// 作用：中心把"生产失败"表达成 Data 为 null + Issues，而不是抛异常。
	// 投影的调用方原本期望硬失败直接传播（旁路服务就是这么做的），所以这里把它还原成异常。
	// 反过来把失败静默当成空结果，会让 UI 显示成"什么都没有"——那比报错更难查。
	private static T Require<T>(ModInformationResult<T> result, string what) where T : class
	{
		if (result.Data is not null) return result.Data;
		var detail = result.Issues.Count == 0
			? "无诊断信息"
			: string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
		throw new InvalidOperationException($"{what}生产失败（状态 {result.Status}）：{detail}");
	}
}
