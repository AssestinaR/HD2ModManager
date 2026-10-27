using HD2ModCore.Domain;
using HD2ModCore.Application;

namespace HD2ModCore.Tests;

// 作用：验证信息中心对 FileFacts 请求的并发合并和业务取消隔离。
// Purpose: Verifies FileFacts request coalescing and caller-cancellation isolation.
public sealed class ModInformationCenterTests
{
	[Fact]
	public async Task RequestFileFactsAsync_CoalescesSameGeneration()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-information-center-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var node = new ModNode(new ModNodeId(Guid.NewGuid()), "mod", new ModNodeMetadata("Test", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [], []);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [node.Id] = node }, [], null);
			var producer = new CountingFileFactsProducer(new HD2ModCore.Infrastructure.PatchFileIndexBuilder(new HD2ModCore.Infrastructure.PatchFileNameParser()));
			var center = new HD2ModCore.Infrastructure.ModInformationCenter(
				producer,
				new ThrowingAssetInventoryProducer());
			var request = new ModInformationRequest(ModInformationKind.FileFacts, "Test");
			var first = center.RequestFileFactsAsync(snapshot, root, request).AsTask();
			var second = center.RequestFileFactsAsync(snapshot, root, request).AsTask();
			producer.Release();
			var results = await Task.WhenAll(first, second);
			Assert.Equal(1, producer.Count);
			Assert.Contains(results, result => result.WasCoalesced);
			await center.DisposeAsync();
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task RequestFileFactsAsync_ReturnsStructuredFailure()
	{
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(),
			new ThrowingAssetInventoryProducer());
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [], null);
		var result = await center.RequestFileFactsAsync(snapshot, Path.GetTempPath(), new ModInformationRequest(ModInformationKind.FileFacts, "Test"));
		Assert.Equal(ModInformationStatus.Failed, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "FileFactsProductionFailed");
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestAssetInventoryAsync_ProductionFailureReturnsLatestStaleCache()
	{
		var node = new ModNode(new ModNodeId(Guid.NewGuid()), "mod", new ModNodeMetadata("Test", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [], []);
		var stale = new ModContentFacts(node.Id, node.RelativePath, "old-generation", DateTimeOffset.UtcNow, [], []);
		var cache = new LatestOnlyInformationCache(stale);
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(),
			new ThrowingAssetInventoryProducer(),
			informationCache: cache);

		var result = await center.RequestAssetInventoryAsync(node, Path.GetTempPath(), new ModInformationRequest(ModInformationKind.AssetInventory, "Test", "new-generation", true));

		Assert.Equal(ModInformationStatus.Stale, result.Status);
		Assert.True(result.RefreshFailed);
		Assert.Equal("old-generation", result.Generation);
		Assert.Same(stale, result.Data);
		Assert.Contains(result.Issues, issue => issue.Code == "AssetInventoryProductionFailed");
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestAssetInventoryAsync_DoesNotPublishAfterNodeInvalidation()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-information-center-invalidation-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var node = new ModNode(new ModNodeId(Guid.NewGuid()), "mod", new ModNodeMetadata("Test", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [], []);
			var producer = new BlockingAssetInventoryProducer(node);
			var cache = new RecordingInformationCache();
			var index = new RecordingModDataIndex();
			var center = new HD2ModCore.Infrastructure.ModInformationCenter(
				new FailingFileFactsProducer(), producer, informationCache: cache, modDataIndex: index);
			var request = center.RequestAssetInventoryAsync(node, root, new ModInformationRequest(ModInformationKind.AssetInventory, "Test", "generation", true)).AsTask();
			await producer.Started.Task;

			await center.InvalidateNodeAsync(node.Id);
			producer.Release();
			var result = await request;

			Assert.NotEqual(ModInformationStatus.Fresh, result.Status);
			Assert.Empty(cache.SavedGenerations);
			Assert.Empty(index.UpdatedNodeIds);

			producer.AllowNextRequest();
			var replacement = await center.RequestAssetInventoryAsync(node, root, new ModInformationRequest(ModInformationKind.AssetInventory, "Test", "generation-2", true));
			Assert.Equal(ModInformationStatus.Fresh, replacement.Status);
			Assert.Equal(2, producer.Count);
            Assert.Equal("generation-2", Assert.Single(cache.SavedGenerations));
			Assert.Equal(node.Id, Assert.Single(index.UpdatedNodeIds));
			await center.DisposeAsync();
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task RequestAssetInventoryAsync_NullGeneration_SecondRequestHitsExactCache()
	{
		var node = CreateNode();
		var producer = new CountingAssetInventoryProducer(node, "stable-generation");
		var cache = new RecordingInformationCache();
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(), producer, informationCache: cache);

		var first = await center.RequestAssetInventoryAsync(node, Path.GetTempPath(), new ModInformationRequest(ModInformationKind.AssetInventory, "Test"));
		var second = await center.RequestAssetInventoryAsync(node, Path.GetTempPath(), new ModInformationRequest(ModInformationKind.AssetInventory, "Test"));

		Assert.Equal(ModInformationStatus.Fresh, first.Status);
		Assert.Equal(ModInformationStatus.Cached, second.Status);
		Assert.True(second.CacheHit);
		Assert.Equal(1, producer.Count);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestAssetInventoryAsync_InvalidatedCacheHit_DoesNotUpdateIndex()
	{
		var node = CreateNode();
		var cached = new ModContentFacts(node.Id, node.RelativePath, "generation", DateTimeOffset.UtcNow, [], []);
		var cache = new BlockingInformationCache(cached);
		var index = new RecordingModDataIndex();
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(), new ThrowingAssetInventoryProducer(), informationCache: cache, modDataIndex: index);

		var request = center.RequestAssetInventoryAsync(node, Path.GetTempPath(), new ModInformationRequest(ModInformationKind.AssetInventory, "Test", "generation")).AsTask();
		await cache.Started.Task;
		await center.InvalidateNodeAsync(node.Id);
		cache.Release();

		var result = await request;
		Assert.NotEqual(ModInformationStatus.Cached, result.Status);
		Assert.Empty(index.UpdatedNodeIds);
		await center.DisposeAsync();
	}

	[Fact]
	public async Task RequestAssetInventoryAsync_CorruptJsonCache_FallsBackToProduction()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-information-corrupt-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var node = CreateNode();
			var cache = new HD2ModCore.Infrastructure.JsonModInformationCache(new HD2ModCore.Infrastructure.StoragePaths(root));
			await cache.SaveAsync(ModInformationKind.AssetInventory, node.Id, "corrupt-generation", "not facts");
			var producer = new CountingAssetInventoryProducer(node, "corrupt-generation");
			var center = new HD2ModCore.Infrastructure.ModInformationCenter(new FailingFileFactsProducer(), producer, informationCache: cache);

			var result = await center.RequestAssetInventoryAsync(node, root, new ModInformationRequest(ModInformationKind.AssetInventory, "Test", "corrupt-generation"));

			Assert.Equal(ModInformationStatus.Fresh, result.Status);
			Assert.Equal(1, producer.Count);
			Assert.NotNull(result.Data);
			await center.DisposeAsync();
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task RequestReferenceGraphAsync_WritesIndex_AndInvalidationDeletesNode()
	{
		var node = CreateNode();
		var writer = new RecordingReferenceGraphIndexWriter();
		var producer = new RecordingReferenceGraphProducer(node);
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(),
			new ThrowingAssetInventoryProducer(),
			referenceGraphProducer: producer,
			referenceGraphIndexWriter: writer);

		var result = await center.RequestReferenceGraphAsync(
			node,
			Path.GetTempPath(),
			new ModInformationRequest(ModInformationKind.ReferenceGraph, "Test", "generation", true));

		Assert.Equal(ModInformationStatus.Fresh, result.Status);
		Assert.Same(result.Data, Assert.Single(writer.Replaced));
		await center.InvalidateNodeAsync(node.Id);
		Assert.Equal(node.Id, Assert.Single(writer.Deleted));
		await center.DisposeAsync();
	}

	// 一个缓存失效失败不能吞掉其余缓存：以前它们是顺序 await 的，FileFacts 抛异常就跳过了
	// 信息缓存 / 数据索引 / 引用图索引，删除 Mod 后这些数据永久留在磁盘上。
	// One failing cache must not swallow the others: they used to be awaited in sequence, so a
	// FileFacts failure skipped the remaining three and left their data on disk forever.
	[Fact]
	public async Task InvalidateNodeAsync_WhenOneCacheThrows_StillInvalidatesTheOthers()
	{
		var node = CreateNode();
		var information = new RecordingInformationCache();
		var index = new RecordingModDataIndex();
		var writer = new RecordingReferenceGraphIndexWriter();
		var center = new HD2ModCore.Infrastructure.ModInformationCenter(
			new FailingFileFactsProducer(),
			new ThrowingAssetInventoryProducer(),
			fileFactsCache: new ThrowingFileFactsCache(),
			informationCache: information,
			modDataIndex: index,
			referenceGraphIndexWriter: writer);

		var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await center.InvalidateNodeAsync(node.Id));

		Assert.NotNull(failure);
		Assert.Equal(node.Id, Assert.Single(information.Deleted));
		Assert.Equal(node.Id, Assert.Single(index.Removed));
		Assert.Equal(node.Id, Assert.Single(writer.Deleted));
		await center.DisposeAsync();
	}

	private static ModNode CreateNode()
	{
		return new ModNode(new ModNodeId(Guid.NewGuid()), "mod", new ModNodeMetadata("Test", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [], []);
	}

	private sealed class CountingFileFactsProducer(HD2ModCore.Application.IPatchFileIndexBuilder builder) : HD2ModCore.Application.IModFileFactsProducer
	{
		public int Count { get; private set; }
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public void Release() => _release.TrySetResult();
		public async ValueTask<PatchFileIndex> ProduceAsync(LibrarySnapshot snapshot, string root, CancellationToken cancellationToken = default)
		{
			Count++;
			await _release.Task.WaitAsync(cancellationToken);
			return await builder.BuildAsync(snapshot, root, cancellationToken);
		}
	}

	private sealed class ThrowingAssetInventoryProducer : HD2ModCore.Application.IAssetInventoryProducer
	{
		public ValueTask<ModContentFacts> GetNodeFactsAsync(ModNode node, string root, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FailingFileFactsProducer : HD2ModCore.Application.IModFileFactsProducer
	{
		public ValueTask<PatchFileIndex> ProduceAsync(LibrarySnapshot snapshot, string root, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("test failure");
	}

	private sealed class BlockingAssetInventoryProducer(ModNode node) : HD2ModCore.Application.IAssetInventoryProducer
	{
		public int Count { get; private set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<ModContentFacts> GetNodeFactsAsync(ModNode requestedNode, string root, CancellationToken cancellationToken = default)
		{
			Count++;
			Started.TrySetResult();
			await _release.Task;
			return new ModContentFacts(node.Id, node.RelativePath, "new-generation", DateTimeOffset.UtcNow, [], []);
		}

		public void Release() => _release.TrySetResult();
		public void AllowNextRequest() => Release();
	}

	private sealed class CountingAssetInventoryProducer(ModNode node, string generation) : HD2ModCore.Application.IAssetInventoryProducer, HD2ModCore.Application.IAssetInventoryGenerationProvider
	{
		public int Count { get; private set; }
		public string ComputeGeneration(ModNode requestedNode, string root) => generation;
		public ValueTask<ModContentFacts> GetNodeFactsAsync(ModNode requestedNode, string root, CancellationToken cancellationToken = default)
		{
			Count++;
			return ValueTask.FromResult(new ModContentFacts(node.Id, node.RelativePath, generation, DateTimeOffset.UtcNow, [], []));
		}
	}

	private sealed class RecordingInformationCache : IModInformationCache
	{
		public List<string> SavedGenerations { get; } = [];
		public List<ModNodeId> Deleted { get; } = [];
		private readonly Dictionary<(ModInformationKind Kind, ModNodeId NodeId, string Generation), object> _values = [];
		public ValueTask<T?> TryLoadAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(_values.TryGetValue((kind, nodeId, generation), out var value) ? (T?)value : default);
		public ValueTask<ModInformationCacheEntry<T>?> TryLoadLatestAsync<T>(ModInformationKind kind, ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.FromResult<ModInformationCacheEntry<T>?>(default);
		public ValueTask SaveAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, T data, CancellationToken cancellationToken = default)
		{
			SavedGenerations.Add(generation);
			_values[(kind, nodeId, generation)] = data!;
			return ValueTask.CompletedTask;
		}
		public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default) { Deleted.Add(nodeId); return ValueTask.CompletedTask; }
	}

	private sealed class BlockingInformationCache(ModContentFacts cached) : IModInformationCache
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ValueTask<T?> TryLoadAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, CancellationToken cancellationToken = default)
		{
			Started.TrySetResult();
			return new ValueTask<T?>(WaitAsync<T>());
		}
		private async Task<T?> WaitAsync<T>()
		{
			await _release.Task;
			return typeof(T) == typeof(ModContentFacts) ? (T?)(object)cached : default;
		}
		public void Release() => _release.TrySetResult();
		public ValueTask<ModInformationCacheEntry<T>?> TryLoadLatestAsync<T>(ModInformationKind kind, ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.FromResult<ModInformationCacheEntry<T>?>(default);
		public ValueTask SaveAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, T data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
	}

	private sealed class RecordingModDataIndex : HD2ModCore.Application.IModDataIndex
	{
		public List<ModNodeId> UpdatedNodeIds { get; } = [];
		public List<ModNodeId> Removed { get; } = [];
		public ValueTask<IReadOnlyList<ModDataIndexEntry>> FindProvidersAsync(AssetKey assetKey, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ModDataIndexEntry>>([]);
		public ValueTask<IReadOnlyList<ModDataIndexEntry>> FindConsumersAsync(AssetKey assetKey, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ModDataIndexEntry>>([]);
		public ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(IReadOnlyCollection<AssetKey> assetKeys, ModNodeId? excludedNodeId = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ModDataIndexSummary(ModDataIndexStatus.Unavailable, 0, 0));
		public ValueTask<ModDataIndexEntry?> ResolveFinalProviderAsync(AssetKey assetKey, Profile profile, CancellationToken cancellationToken = default) => ValueTask.FromResult<ModDataIndexEntry?>(null);
		public ValueTask RemoveNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default) { Removed.Add(nodeId); return ValueTask.CompletedTask; }
		public void Update(ModContentFacts inventory) => UpdatedNodeIds.Add(inventory.NodeId);
		public void Update(ReferenceGraphFacts graph) { }
	}

	private sealed class ThrowingFileFactsCache : HD2ModCore.Application.IModFileFactsCache
	{
		public ValueTask<PatchFileIndex?> TryLoadAsync(string generation, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<PatchFileIndex?>(null);
		public ValueTask SaveAsync(string generation, PatchFileIndex facts, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;
		public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
			=> throw new UnauthorizedAccessException("cache file is locked");
	}

	private sealed class LatestOnlyInformationCache(ModContentFacts stale) : IModInformationCache
	{
		public ValueTask<T?> TryLoadAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<T?>(default);

		public ValueTask<ModInformationCacheEntry<T>?> TryLoadLatestAsync<T>(ModInformationKind kind, ModNodeId nodeId, CancellationToken cancellationToken = default)
			=> kind == ModInformationKind.AssetInventory && typeof(T) == typeof(ModContentFacts)
				? ValueTask.FromResult<ModInformationCacheEntry<T>?>(new ModInformationCacheEntry<T>(stale.ContentGeneration, (T)(object)stale, stale.BuiltUtc))
				: ValueTask.FromResult<ModInformationCacheEntry<T>?>(default);

		public ValueTask SaveAsync<T>(ModInformationKind kind, ModNodeId nodeId, string generation, T data, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;

		public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;
	}

	private sealed class RecordingReferenceGraphProducer(ModNode node) : IReferenceGraphProducer
	{
		public ValueTask<ReferenceGraphFacts> ProduceAsync(ModNode requestedNode, string root, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new ReferenceGraphFacts(node.Id, node.RelativePath, "generation", DateTimeOffset.UtcNow, [], []));
	}

	private sealed class RecordingReferenceGraphIndexWriter : IReferenceGraphIndexWriter
	{
		public List<ReferenceGraphFacts> Replaced { get; } = [];
		public List<ModNodeId> Deleted { get; } = [];
		public ValueTask ReplaceNodeAsync(ReferenceGraphFacts facts, CancellationToken cancellationToken = default) { Replaced.Add(facts); return ValueTask.CompletedTask; }
		public ValueTask ReplaceNodeAsync(AdvancedUnitAnalysisFacts facts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default) { Deleted.Add(nodeId); return ValueTask.CompletedTask; }
	}
}
