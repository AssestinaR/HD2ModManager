using HD2ModCore.Domain;
using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证高级分析服务通过信息中心请求派生缓存，而不是直接读取缓存存储。
public sealed class AdvancedModAnalysisServiceTests
{
	[Fact]
	public async Task GetStateAsync_RequestsAdvancedFactsFromInformationCenter()
	{
		var node = new ModNode(ModNodeId.New(), "Test", new ModNodeMetadata("Test", null, DateTimeOffset.UtcNow, null), [], []);
		var facts = new HD2ModCore.Application.AdvancedUnitAnalysisFacts(node.Id, node.RelativePath, "generation", DateTimeOffset.UtcNow, [], []);
		var service = new AdvancedModAnalysisService(new FakeInformationCenter(facts));

		var state = await service.GetStateAsync(node, "mods");

		Assert.True(state.IsReady);
		Assert.True(state.IsCurrent);
	}

	[Fact]
	public async Task AnalyzeAsync_RequestsFreshAdvancedFacts()
	{
		var node = new ModNode(ModNodeId.New(), "Test", new ModNodeMetadata("Test", null, DateTimeOffset.UtcNow, null), [], []);
		var facts = new HD2ModCore.Application.AdvancedUnitAnalysisFacts(node.Id, node.RelativePath, "generation", DateTimeOffset.UtcNow, [], []);
		var center = new RecordingInformationCenter(facts);
		var service = new AdvancedModAnalysisService(center);

		var state = await service.AnalyzeAsync(node, "mods");

		Assert.True(state.IsReady);
		Assert.True(center.LastRequest!.RequireFresh);
	}

	private sealed class RecordingInformationCenter(HD2ModCore.Application.AdvancedUnitAnalysisFacts facts) : IModInformationCenter
	{
		public ModInformationRequest? LastRequest { get; private set; }
		// 与 FakeInformationCenter 同理：契约要求事件存在，但这个替身从不发布它们。
		event EventHandler<ModInformationDiagnostic>? IModInformationCenter.DiagnosticRecorded { add { } remove { } }
		event EventHandler<ModInformationProductionStarted>? IModInformationCenter.ProductionStarted { add { } remove { } }
		public ValueTask<ModInformationResult<PatchFileIndex>> RequestFileFactsAsync(LibrarySnapshot snapshot, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<ModContentFacts>> RequestAssetInventoryAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<ReferenceGraphFacts>> RequestReferenceGraphAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<MaintenanceAnalysisFacts>> RequestMaintenanceAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<ModUnitVersionFacts>> RequestUnitVersionAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<AdvancedUnitAnalysisFacts>> RequestAdvancedUnitAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
		{
			LastRequest = request;
			return ValueTask.FromResult(new ModInformationResult<AdvancedUnitAnalysisFacts>(facts, ModInformationStatus.Cached, request.Kind, facts.Generation, facts.Issues, false, false, true));
		}
		public ValueTask<ModInformationResult<ModThumbnailFacts>> RequestThumbnailAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<GameDataIndexFacts>> RequestGameDataIndexAsync(ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<GameDataMappingFacts>> RequestGameDataMappingAsync(IReadOnlySet<AssetKey> assetKeys, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModInformationResult<DeployedOverrideGraph>> RequestDeployedGraphAsync(string gameDataDirectory, ModInformationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(IReadOnlyCollection<AssetKey> assetKeys, ModNodeId? excludedNodeId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask InvalidateNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}