using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

internal sealed class FakeInformationCenter : IModInformationCenter
{
	private readonly IReadOnlyDictionary<ModNodeId, ModContentFacts> _facts;
	private readonly IReadOnlyDictionary<ModNodeId, AdvancedUnitAnalysisFacts> _advancedFacts = new Dictionary<ModNodeId, AdvancedUnitAnalysisFacts>();
	private readonly IReadOnlyDictionary<ModNodeId, ReferenceGraphFacts> _referenceFacts = new Dictionary<ModNodeId, ReferenceGraphFacts>();
	public List<ModNodeId> InvalidatedNodeIds { get; } = [];

	// 作用：用例可注入"资产键集合 → 映射事实"来替代过去的假 IGameDataMappingFactsService。
	// Purpose: Lets a case inject key-set → mapping facts, replacing the former fake mapping services.
	//
	// 为什么要有"未注入"这一档：生产中心在"索引还没建 / 生产失败"时返回 Data = null，
	// 调用方必须能容忍。未注入即代表这一档，所以默认返回 Data = null 而不是抛异常
	// （MaterialDeliveryFactsServiceTests 用"注入一个必抛的委托"来表达"这条路径不该被走到"）。
	public Func<IReadOnlySet<AssetKey>, GameDataMappingFacts>? GameDataMapping { get; set; }

	public FakeInformationCenter(IReadOnlyDictionary<ModNodeId, ModContentFacts> facts) => _facts = facts;
	public FakeInformationCenter(ModContentFacts facts) : this(new Dictionary<ModNodeId, ModContentFacts> { [facts.NodeId] = facts }) { }
	public FakeInformationCenter(AdvancedUnitAnalysisFacts facts)
	{
		_facts = new Dictionary<ModNodeId, ModContentFacts>();
		_advancedFacts = new Dictionary<ModNodeId, AdvancedUnitAnalysisFacts> { [facts.NodeId] = facts };
	}
	public FakeInformationCenter(IReadOnlyDictionary<ModNodeId, AdvancedUnitAnalysisFacts> facts)
	{
		_facts = new Dictionary<ModNodeId, ModContentFacts>();
		_advancedFacts = facts;
	}
	public FakeInformationCenter(IReadOnlyDictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> facts)
	{
		_facts = new Dictionary<ModNodeId, ModContentFacts>();
		_referenceFacts = facts.ToDictionary(pair => pair.Key, pair => new ReferenceGraphFacts(pair.Key, "test", "test", DateTimeOffset.UtcNow, pair.Value, []));
	}
	public FakeInformationCenter(IReadOnlyDictionary<ModNodeId, ModContentFacts> facts, IReadOnlyDictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> referenceFacts)
	{
		_facts = facts;
		_referenceFacts = referenceFacts.ToDictionary(pair => pair.Key, pair => new ReferenceGraphFacts(pair.Key, "test", "test", DateTimeOffset.UtcNow, pair.Value, []));
	}
	public FakeInformationCenter(ModContentFacts facts, IReadOnlyDictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> referenceFacts)
		: this(new Dictionary<ModNodeId, ModContentFacts> { [facts.NodeId] = facts }, referenceFacts) { }
	// 接口要求这两个事件存在，但这个测试替身从不发布它们。
	// 显式实现 + 空访问器既满足契约，又不会触发 CS0067（"声明了却从不使用"）。
	event EventHandler<ModInformationDiagnostic>? IModInformationCenter.DiagnosticRecorded { add { } remove { } }
	event EventHandler<ModInformationProductionStarted>? IModInformationCenter.ProductionStarted { add { } remove { } }
	public ValueTask<ModInformationResult<PatchFileIndex>> RequestFileFactsAsync(LibrarySnapshot snapshot, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public ValueTask<ModInformationResult<ModContentFacts>> RequestAssetInventoryAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		_facts.TryGetValue(node.Id, out var facts);
		return ValueTask.FromResult(new ModInformationResult<ModContentFacts>(facts, facts is null ? ModInformationStatus.Failed : ModInformationStatus.Fresh, request.Kind, facts?.ContentGeneration, facts?.Issues ?? []));
	}
	public ValueTask<ModInformationResult<ReferenceGraphFacts>> RequestReferenceGraphAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		_referenceFacts.TryGetValue(node.Id, out var facts);
		return ValueTask.FromResult(new ModInformationResult<ReferenceGraphFacts>(facts, facts is null ? ModInformationStatus.Failed : ModInformationStatus.Fresh, request.Kind, facts?.Generation, facts?.Issues ?? []));
	}
	public ValueTask<ModInformationResult<MaintenanceAnalysisFacts>> RequestMaintenanceAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public ValueTask<ModInformationResult<ModUnitVersionFacts>> RequestUnitVersionAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public ValueTask<ModInformationResult<AdvancedUnitAnalysisFacts>> RequestAdvancedUnitAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		_advancedFacts.TryGetValue(node.Id, out var facts);
		return ValueTask.FromResult(new ModInformationResult<AdvancedUnitAnalysisFacts>(facts, facts is null ? ModInformationStatus.Failed : ModInformationStatus.Fresh, request.Kind, facts?.Generation, facts?.Issues ?? []));
	}
	public ValueTask<ModInformationResult<ModThumbnailFacts>> RequestThumbnailAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	// 无节点事实（设计文档 §22）里只有"映射"这一条被业务服务消费，所以这里实现它；
	// 其余无节点事实由 ModInformationCenterNodeLessFactTests 覆盖，本替身仍按未实现处理。
	// Node-less facts are covered by ModInformationCenterNodeLessFactTests; this double only serves the mapping one.
	public ValueTask<ModInformationResult<GameDataMappingFacts>> RequestGameDataMappingAsync(IReadOnlySet<AssetKey> assetKeys, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		if (GameDataMapping is null)
			return ValueTask.FromResult(new ModInformationResult<GameDataMappingFacts>(null, ModInformationStatus.Failed, request.Kind, null, []));
		var facts = GameDataMapping(assetKeys);
		return ValueTask.FromResult(new ModInformationResult<GameDataMappingFacts>(facts, ModInformationStatus.Fresh, request.Kind, facts.MappingGeneration, facts.Issues));
	}
	public ValueTask<ModInformationResult<GameDataIndexFacts>> RequestGameDataIndexAsync(ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public ValueTask<ModInformationResult<DeployedOverrideGraph>> RequestDeployedGraphAsync(string gameDataDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public ValueTask<ModInformationResult<ArmorCoverageFacts>> RequestArmorCoverageAsync(string gameDataDirectory, IReadOnlySet<string> archiveIds, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	       public ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(IReadOnlyCollection<AssetKey> assetKeys, ModNodeId? excludedNodeId = null, CancellationToken cancellationToken = default)
		       => ValueTask.FromResult(new ModDataIndexSummary(ModDataIndexStatus.Unavailable, 0, 0));
	public ValueTask InvalidateNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		InvalidatedNodeIds.Add(nodeId);
		return ValueTask.CompletedTask;
	}
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
