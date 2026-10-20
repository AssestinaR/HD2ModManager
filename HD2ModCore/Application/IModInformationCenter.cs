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

	// 作用：护甲覆盖视图（设计文档 §4 #23）。它是**派生记忆**：由上面三条无节点事实组合而来，
	// 只对"被问到的那批归档"作答。
	// Purpose: The armor coverage view. A derived memo composed of the three node-less facts above,
	//          answering only for the archives it was asked about.
	//
	// 为什么要把它做成一次中心请求，而不是让页面自己组合三条事实：
	//   1. 组合规则（谁算赢家、哪些归档算"存在"）只该有一份实现；
	//   2. 页面若自己组合，就会去请求"全部 Mod 的资产键集合"来喂映射——那需要逐节点资产清单，
	//      正是护甲页首开卡顿的根因。本方法内部只映射"已部署且有归属"的资产键。
	// 返回 Data 为 null 表示"索引还没建"或"生产失败"，两种情况页面都按"未知"处理，与改造前一致。
	ValueTask<ModInformationResult<ArmorCoverageFacts>> RequestArmorCoverageAsync(
		string gameDataDirectory,
		IReadOnlySet<string> archiveIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	/// <summary>一批 unit 的 section 结构（衣柜编辑器与 section 映射表的共同来源）。</summary>
	ValueTask<ModInformationResult<UnitSectionStructureFacts>> RequestUnitSectionStructureAsync(
		string gameDataDirectory,
		IReadOnlySet<string> unitIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	/// <summary>一批护甲条目的 unit 表（来源：游戏注册文件）。</summary>
	ValueTask<ModInformationResult<ArmourUnitTableFacts>> RequestArmourUnitTableAsync(
		string gameDataDirectory,
		IReadOnlySet<uint> itemIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(
		       IReadOnlyCollection<AssetKey> assetKeys,
		       ModNodeId? excludedNodeId = null,
		       CancellationToken cancellationToken = default);

	ValueTask InvalidateNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default);
}