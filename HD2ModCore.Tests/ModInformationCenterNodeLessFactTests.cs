using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：覆盖"无节点事实"（设计文档 §22）——身份、并发合并、失效边界与失败语义。
// Purpose: Covers the node-less facts: identity, coalescing, invalidation boundary and failure semantics.
//
// 这些用例刻意断言**行为**而不是实现细节：合并看生产者被调用几次，失效边界看"按节点失效之后
// 同一批请求还能不能合并"，失败语义看中心与投影适配器各自的表现。
public sealed class ModInformationCenterNodeLessFactTests
{
	[Fact]
	public async Task RequestGameDataIndexAsync_CoalescesConcurrentRequests()
	{
		var index = new StubIndexService { Block = true };
		var center = CreateCenter(indexService: index);

		var first = center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex)).AsTask();
		await index.Started.Task;
		var second = center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex)).AsTask();
		index.Release();

		var results = await Task.WhenAll(first, second);

		Assert.Equal(1, index.FingerprintCount);
		Assert.Contains(results, result => result.WasCoalesced);
		Assert.All(results, result => Assert.Equal(ModInformationStatus.Fresh, result.Status));
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestGameDataIndexAsync_WhenIndexIsNotBuilt_ReturnsUnavailableWithoutFailure()
	{
		var center = CreateCenter(indexService: new StubIndexService { Fingerprint = null });

		var result = await center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex));

		// "还没有索引"不是错误。直读版此时返回 null，投影据此整页返回 null——两者必须语义一致。
		Assert.Equal(ModInformationStatus.Unavailable, result.Status);
		Assert.Null(result.Data);
		Assert.Empty(result.Issues);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestGameDataMappingAsync_DifferentAssetSetsProduceSeparately()
	{
		var mapping = new BlockingMappingService { Block = true };
		var center = CreateCenter(mappingService: mapping);

		var first = center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(1, 1) }, Request(ModInformationKind.GameDataMapping)).AsTask();
		await mapping.Started.Task;
		var second = center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(2, 2) }, Request(ModInformationKind.GameDataMapping)).AsTask();
		mapping.Release();

		var results = await Task.WhenAll(first, second);

		// 不同批次的资产是**不同的映射事实**：合并它们会让一页拿到另一页的资产清单。
		Assert.Equal(2, mapping.Count);
		Assert.DoesNotContain(results, result => result.WasCoalesced);
		Assert.All(results, result => Assert.NotNull(result.Data));
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestGameDataMappingAsync_SameAssetSetInAnyOrderCoalesces()
	{
		var mapping = new BlockingMappingService { Block = true };
		var center = CreateCenter(mappingService: mapping);

		var first = center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(1, 1), new(2, 2) }, Request(ModInformationKind.GameDataMapping)).AsTask();
		await mapping.Started.Task;
		var second = center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(2, 2), new(1, 1) }, Request(ModInformationKind.GameDataMapping)).AsTask();
		mapping.Release();

		var results = await Task.WhenAll(first, second);

		// 作用域令牌先排序再哈希，所以枚举顺序不影响身份。
		Assert.Equal(1, mapping.Count);
		Assert.Contains(results, result => result.WasCoalesced);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestDeployedGraphAsync_CoalescesSameDirectoryButNotDifferentOnes()
	{
		var deployed = new BlockingDeployedService { Block = true };
		var center = CreateCenter(deployedService: deployed);

		var first = center.RequestDeployedGraphAsync("data", Request(ModInformationKind.DeployedGraph)).AsTask();
		await deployed.Started.Task;
		var same = center.RequestDeployedGraphAsync("data", Request(ModInformationKind.DeployedGraph)).AsTask();
		var other = center.RequestDeployedGraphAsync("other-data", Request(ModInformationKind.DeployedGraph)).AsTask();
		deployed.Release();

		var results = await Task.WhenAll(first, same, other);

		// 同一目录合并成一次生产，另一个目录必须另算：三个请求里恰好有一个是被合并方。
		Assert.Equal(2, deployed.Count);
		Assert.Equal(1, results.Count(result => result.WasCoalesced));
		Assert.Contains(results, result => result.Data?.GameDataDirectory == "other-data");
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestArmorCoverageAsync_CoalescesTheSameArchiveSetButNotDifferentOnes()
	{
		var index = new StubIndexService { Block = true };
		var center = CreateCenter(
			indexService: index,
			mappingService: new BlockingMappingService(),
			deployedService: new BlockingDeployedService());
		var produced = new List<ModInformationDiagnostic>();
		center.DiagnosticRecorded += (_, diagnostic) =>
		{
			lock (produced) produced.Add(diagnostic);
		};

		var first = center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage)).AsTask();
		await index.Started.Task;
		var same = center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage)).AsTask();
		var other = center.RequestArmorCoverageAsync("data", new HashSet<string> { "other" }, Request(ModInformationKind.ArmorCoverage)).AsTask();
		index.Release();

		var results = await Task.WhenAll(first, same, other);

		// 判据用"覆盖事实被生产了几次"而不是"索引被读了几次"：覆盖事实内部还要请求三条上游事实，
		// 它们各自也会合并，所以只有按 Kind 过滤才能看出这一层账本是否真的按归档集合分开。
		Assert.Equal(2, produced.Count(diagnostic => diagnostic.Kind == ModInformationKind.ArmorCoverage));
		Assert.Equal(1, results.Count(result => result.WasCoalesced));
		Assert.All(results, result => Assert.NotNull(result.Data));
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestArmorCoverageAsync_WhenTheIndexIsNotBuilt_ReportsUnavailableInsteadOfNoCoverage()
	{
		var center = CreateCenter(
			indexService: new StubIndexService { Fingerprint = null },
			mappingService: new BlockingMappingService(),
			deployedService: new BlockingDeployedService());

		var result = await center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage));

		// 索引没建好时，正确的答案是"不知道"，而不是"没有任何护甲被覆盖"。
		// 后者会让护甲页把每件护甲都标成"未命中 Unit 覆盖"——一个从缺失数据推出来的否定事实。
		Assert.Equal(ModInformationStatus.Unavailable, result.Status);
		Assert.Null(result.Data);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestArmorCoverageAsync_WhenTheDeploymentCannotBeRead_FailsInsteadOfReportingNoCoverage()
	{
		var center = CreateCenter(
			indexService: new StubIndexService(),
			mappingService: new BlockingMappingService(),
			deployedService: new FailingDeployedService());

		var result = await center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage));

		// 部署图读不出来时同样不能宣称"没有覆盖"：那会把一次读取失败伪装成一个干净的空结果。
		Assert.Equal(ModInformationStatus.Failed, result.Status);
		Assert.Null(result.Data);
		Assert.True(result.RefreshFailed);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task NodeLessFacts_AreNotDisturbedByNodeInvalidation()
	{
		var index = new StubIndexService { Block = true };
		var center = CreateCenter(indexService: index);

		var first = center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex)).AsTask();
		await index.Started.Task;
		// 某个 Mod 目录变了。无节点事实的输入来自游戏目录与激活状态，Mod 目录变了不构成它们失效的理由，
		// 所以这次失效**不应**把它们进行中的任务丢掉。
		await center.InvalidateNodeAsync(ModNodeId.New());
		var second = center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex)).AsTask();
		index.Release();

		var results = await Task.WhenAll(first, second);

		Assert.Equal(1, index.FingerprintCount);
		Assert.Contains(results, result => result.WasCoalesced);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task MissingGameDataProducers_ReturnUnavailableInsteadOfThrowing()
	{
		var center = CreateCenter();

		var index = await center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex));
		var mapping = await center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(1, 1) }, Request(ModInformationKind.GameDataMapping));
		var deployed = await center.RequestDeployedGraphAsync("data", Request(ModInformationKind.DeployedGraph));
		var coverage = await center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage));

		Assert.Equal(ModInformationStatus.Unavailable, index.Status);
		Assert.Equal(ModInformationStatus.Unavailable, mapping.Status);
		Assert.Equal(ModInformationStatus.Unavailable, deployed.Status);
		Assert.Equal(ModInformationStatus.Unavailable, coverage.Status);
		Assert.Contains(index.Issues, issue => issue.Code == "GameDataIndexUnavailable");
		Assert.Contains(mapping.Issues, issue => issue.Code == "GameDataMappingUnavailable");
		Assert.Contains(deployed.Issues, issue => issue.Code == "DeployedGraphUnavailable");
		Assert.Contains(coverage.Issues, issue => issue.Code == "ArmorCoverageUnavailable");
		await center.DisposeAsync();
	}

	[Fact]
	public async Task NodeLessFacts_PublishDiagnosticsWhoseSubjectMatchesTheRegistry()
	{
		var center = CreateCenter(
			indexService: new StubIndexService(),
			mappingService: new BlockingMappingService(),
			deployedService: new BlockingDeployedService());
		var seen = new List<ModInformationDiagnostic>();
		center.DiagnosticRecorded += (_, diagnostic) => seen.Add(diagnostic);

		await center.RequestGameDataIndexAsync(Request(ModInformationKind.GameDataIndex));
		await center.RequestGameDataMappingAsync(new HashSet<AssetKey> { new(1, 1) }, Request(ModInformationKind.GameDataMapping));
		await center.RequestDeployedGraphAsync("data", Request(ModInformationKind.DeployedGraph));
		await center.RequestArmorCoverageAsync("data", new HashSet<string> { "target" }, Request(ModInformationKind.ArmorCoverage));

		// 覆盖事实内部会请求它的三条上游事实，所以诊断条数不是 4——判据是"每个 kind 都被记录过"。
		ModInformationKind[] kinds =
		[
			ModInformationKind.GameDataIndex,
			ModInformationKind.GameDataMapping,
			ModInformationKind.DeployedGraph,
			ModInformationKind.ArmorCoverage,
		];
		foreach (var kind in kinds)
			Assert.Contains(seen, diagnostic => diagnostic.Kind == kind);
		Assert.All(seen, diagnostic => Assert.Null(diagnostic.NodeId));
		Assert.All(seen, diagnostic => Assert.Equal(ModInformationStatus.Fresh, diagnostic.Status));
		Assert.All(seen, diagnostic => Assert.Equal("Test", diagnostic.Source));
		// 静态登记表声明的主体种类，必须和中心**实际**使用的 Subject 一致。
		// 没有这条断言，"登记表说它是 GameData 事实、中心却按节点给它开账"这种错位不会有任何测试发现——
		// 而登记表是全仓唯一的真相来源，错位会让依赖图与失效逻辑一起走偏。
		Assert.All(seen, diagnostic =>
		{
			var property = new ModInformationRequest(diagnostic.Kind, "Test").EffectiveProperty;
			var declared = ModInformationFactRegistry.Describe(property).SubjectKind;
			Assert.StartsWith($"{declared}:", diagnostic.OperationKey);
		});
		await center.DisposeAsync();
	}

	[Fact]
	public async Task CenterGameDataFactSource_WhenIndexIsNotBuilt_ReturnsNullInsteadOfThrowing()
	{
		var center = CreateCenter(indexService: new StubIndexService { Fingerprint = null });
		var source = new CenterGameDataFactSource(center);

		// 直读版在同样情形下返回 null，中心版必须保持一致，否则"索引未建"会变成一次异常。
		Assert.Null(await source.GetIndexAsync());
		await center.DisposeAsync();
	}

	[Fact]
	public async Task CenterGameDataFactSource_WhenProductionFails_ThrowsInsteadOfReportingEmpty()
	{
		var center = CreateCenter(deployedService: new FailingDeployedService());
		var source = new CenterGameDataFactSource(center);

		// 中心把失败表达成 Data 为 null + Issues，但投影的调用方原本期望硬失败直接传播。
		// 把失败静默当成空结果，UI 只会显示"什么都没有"——那比报错更难查。
		async Task Act() => await source.GetDeployedGraphAsync("data");
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(Act);
		Assert.Contains("DeployedGraphProductionFailed", exception.Message);
		await center.DisposeAsync();
	}

	private static ModInformationRequest Request(ModInformationKind kind) => new(kind, "Test");

	private static ModInformationCenter CreateCenter(
		IAssetArchiveIndexService? indexService = null,
		IGameDataMappingFactsService? mappingService = null,
		IDeployedOverrideGraphService? deployedService = null)
		=> new(
			new StubFileFactsProducer(),
			new StubAssetInventoryProducer(),
			assetArchiveIndexService: indexService,
			gameDataMappingFactsService: mappingService,
			deployedOverrideGraphService: deployedService);

	private sealed class StubFileFactsProducer : IModFileFactsProducer
	{
		public ValueTask<PatchFileIndex> ProduceAsync(LibrarySnapshot snapshot, string root, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class StubAssetInventoryProducer : IAssetInventoryProducer
	{
		public ValueTask<ModContentFacts> GetNodeFactsAsync(ModNode node, string root, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class StubIndexService : IAssetArchiveIndexService
	{
		public GameDataIndexFingerprint? Fingerprint { get; set; } = new("data", DateTimeOffset.UtcNow, 1, 1, 1, "index");
		public bool Block { get; set; }
		public int FingerprintCount { get; private set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Release() => _release.TrySetResult();

		public async ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default)
		{
			FingerprintCount++;
			Started.TrySetResult();
			if (Block) await _release.Task.WaitAsync(cancellationToken);
			return Fingerprint;
		}

		public ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<GameDataArchiveSummary>>([new GameDataArchiveSummary("target", "Target", "Armor", 1, "正常")]);

		public ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
		public ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(string packageName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<IReadOnlyDictionary<AssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(IReadOnlySet<AssetKey> unitAssetKeys, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> FindStreamLayoutsAsync(IReadOnlyList<GameDataStreamComponentFact> components, uint vertexStride, bool requireSkinned = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> GetStreamLayoutsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<GameDataIndexStatus> GetIndexStatusAsync(string gameDataDirectory, string archiveHashesJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask BuildOrRebuildAsync(string gameDataDirectory, string archiveHashesJson, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(IReadOnlySet<AssetKey> assetKeys, IndexFilterSettings filterSettings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class BlockingMappingService : IGameDataMappingFactsService
	{
		public int Count { get; private set; }
		public bool Block { get; set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Release() => _release.TrySetResult();

		public async ValueTask<GameDataMappingFacts> MapAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
		{
			Count++;
			Started.TrySetResult();
			if (Block) await _release.Task.WaitAsync(cancellationToken);
			return new GameDataMappingFacts(
				"mapping",
				"index",
				"metadata",
				DateTimeOffset.UtcNow,
				assetKeys.ToDictionary(key => key, key => new GameDataMappedAssetFact(key, "body", "unit", AssetTypeCategory.Model, [])),
				[]);
		}
	}

	private sealed class BlockingDeployedService : IDeployedOverrideGraphService
	{
		public int Count { get; private set; }
		public bool Block { get; set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Release() => _release.TrySetResult();

		public async ValueTask<DeployedOverrideGraph> BuildAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
		{
			Count++;
			Started.TrySetResult();
			if (Block) await _release.Task.WaitAsync(cancellationToken);
			return new DeployedOverrideGraph(gameDataDirectory, "deployed", DateTimeOffset.UtcNow, null, 0, [], [], []);
		}
	}

	private sealed class FailingDeployedService : IDeployedOverrideGraphService
	{
		public ValueTask<DeployedOverrideGraph> BuildAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("deployment scan failed");
	}
}
