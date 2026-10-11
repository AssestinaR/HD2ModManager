using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：统一编排 Mod 派生信息请求；业务代码不直接启动派生缓存生产。
// Purpose: Orchestrates Mod-derived information requests so consumers do not start production directly.
public interface IModInformationCenter : IAsyncDisposable
{
	event EventHandler<ModInformationDiagnostic>? DiagnosticRecorded;
	event EventHandler<ModInformationProductionStarted>? ProductionStarted;

	ValueTask<ModInformationResult<PatchFileIndex>> RequestFileFactsAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<ModContentFacts>> RequestAssetInventoryAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<ReferenceGraphFacts>> RequestReferenceGraphAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<MaintenanceAnalysisFacts>> RequestMaintenanceAnalysisAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<ModUnitVersionFacts>> RequestUnitVersionAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<AdvancedUnitAnalysisFacts>> RequestAdvancedUnitAnalysisAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<ModThumbnailFacts>> RequestThumbnailAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	// 作用：无节点事实（设计文档 §22）。它们不属于任何一个 Mod 节点，身份是"游戏目录 + 来源指纹"。
	// Purpose: Node-less facts. They belong to no Mod node; their identity is the game directory plus a source fingerprint.
	//
	// 之所以仍然走信息中心而不是让调用方直连旁路服务：统一身份、并发合并、诊断事件三者只在中心这一处实现。
	// 它们与节点事实的差别只在两处——没有节点级取消令牌（用中心自己的关闭令牌），以及不参与按节点失效
	// （它们的输入不是 Mod 目录，节点变化影响不到它们）。
	ValueTask<ModInformationResult<GameDataIndexFacts>> RequestGameDataIndexAsync(
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<GameDataMappingFacts>> RequestGameDataMappingAsync(
		IReadOnlySet<AssetKey> assetKeys,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationResult<DeployedOverrideGraph>> RequestDeployedGraphAsync(
		string gameDataDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(
		       IReadOnlyCollection<AssetKey> assetKeys,
		       ModNodeId? excludedNodeId = null,
		       CancellationToken cancellationToken = default);

	ValueTask InvalidateNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default);
}