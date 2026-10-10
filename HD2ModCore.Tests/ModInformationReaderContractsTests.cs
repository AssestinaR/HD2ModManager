using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Application;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using MaterialDependencyResolver = HD2ModAdaptation.PatchReconstruction.MaterialDependencyResolver;

namespace HD2ModCore.Tests;

// Purpose: Keeps the new unified-reader contracts stable while legacy producers are migrated gradually.
public sealed class ModInformationReaderContractsTests
{
	[Fact]
	public void SelectorCacheKey_IsStableAcrossInputOrder()
	{
		var first = new ModInformationSelector(
			[ new PatchGroupKey("B", 2), new PatchGroupKey("a", 1) ],
			[ new AssetKey(2, 8), new AssetKey(1, 9) ],
			[ 7, 3 ],
			[ "B", "a" ],
			IncludeDependencies: true,
			IncludeReverseReferences: true);
		var second = new ModInformationSelector(
			[ new PatchGroupKey("a", 1), new PatchGroupKey("B", 2) ],
			[ new AssetKey(1, 9), new AssetKey(2, 8) ],
			[ 3, 7 ],
			[ "a", "B" ],
			IncludeDependencies: true,
			IncludeReverseReferences: true);

		Assert.Equal(first.ToCacheKey(), second.ToCacheKey());
	}

	[Fact]
	public void ContentRevision_ChangesEffectiveRevisionWhenOverwriteChanges()
	{
		var first = ModContentRevision.Create("source", "overwrite-one");
		var second = ModContentRevision.Create("source", "overwrite-two");

		Assert.Equal("source", first.SourceRevision);
		Assert.NotEqual(first.EffectiveRevision, second.EffectiveRevision);
		Assert.NotEqual(first.CacheKey, second.CacheKey);
	}

	[Fact]
	public async Task OperationCache_DoesNotRetainItemsLargerThanItsBudget()
	{
		await using var cache = new InMemoryModInformationOperationCache(Guid.NewGuid(), capacityBytes: 8);
		var key = CreateKey("source", ModInformationPropertyKind.GpuPayload);

		await cache.SetAsync(key, new byte[16]);

		Assert.Null(await cache.TryGetAsync<byte[]>(key));
		Assert.Equal(0, cache.UsedBytes);
	}

	[Fact]
	public async Task OperationCache_EvictsLeastRecentlyUsedUnprotectedEntry()
	{
		await using var cache = new InMemoryModInformationOperationCache(Guid.NewGuid(), capacityBytes: 10);
		var first = CreateKey("first", ModInformationPropertyKind.UnitStructure);
		var second = CreateKey("second", ModInformationPropertyKind.UnitStructure);
		var third = CreateKey("third", ModInformationPropertyKind.UnitStructure);

		await cache.SetAsync(first, "one", estimatedBytes: 4);
		await cache.SetAsync(second, "two", estimatedBytes: 4);
		Assert.NotNull(await cache.TryGetAsync<string>(first));
		await cache.SetAsync(third, "three", estimatedBytes: 4);

		Assert.NotNull(await cache.TryGetAsync<string>(first));
		Assert.Null(await cache.TryGetAsync<string>(second));
		Assert.NotNull(await cache.TryGetAsync<string>(third));
	}

	[Fact]
	public void LegacyRequest_MapsToFineGrainedPropertyWithoutBreakingExistingConstruction()
	{
		var request = new ModInformationRequest(ModInformationKind.AssetInventory, "Test");

		Assert.Equal(ModInformationPropertyKind.AssetInventory, request.EffectiveProperty);
		Assert.Equal(ModInformationCacheScope.Persistent, request.EffectiveContext.CacheScope);
		Assert.Same(request.EffectiveContext, request.EffectiveContext);
	}

	[Fact]
	public async Task Reader_ReusesPatchIndexWithinTheSameOperation()
	{
		var reader = new ModInformationReader(new CountingWorkspaceReader());
		var context = ModInformationRequestContext.Create(ModInformationCacheScope.Operation, operationId: Guid.NewGuid());
		var request = new ModInformationReadRequest("reader-test.patch", context);

		var first = await reader.ReadPatchIndexAsync(request);
		var second = await reader.ReadPatchIndexAsync(request);

		Assert.Equal(ModInformationPropertyStatus.Fresh, first.State.Status);
		Assert.Equal(ModInformationPropertyStatus.Cached, second.State.Status);
		Assert.Equal(ModInformationValueSource.OperationCache, second.State.Source);
		Assert.Same(first.Data, second.Data);
		await reader.DisposeAsync();
	}

	[Fact]
	public async Task Reader_CoalescesConcurrentPatchIndexReadsBeforeCacheIsPopulated()
	{
		var workspace = new DelayedCountingWorkspaceReader();
		await using var reader = new ModInformationReader(workspace);
		var context = ModInformationRequestContext.Create(ModInformationCacheScope.None);
		var request = new ModInformationReadRequest("coalesced-reader-test.patch", context);

		var results = await Task.WhenAll(Enumerable.Range(0, 8)
			.Select(_ => reader.ReadPatchIndexAsync(request).AsTask()));

		Assert.Equal(1, workspace.Count);
		Assert.Contains(results, result => result.WasCoalesced);
	}

	[Fact]
	public async Task Reader_SourceUnitFacts_ReportsEmptyPatchWithoutLoadingPayloads()
	{
		var index = new PatchWorkspaceIndex("source-facts.patch", [], [1, 2, 3]);
		var reader = new ModInformationReader(new FixedWorkspaceReader(index));
		var request = new ModInformationReadRequest("source-facts.patch", ModInformationRequestContext.Create(ModInformationCacheScope.Operation));

		var result = await reader.ReadSourceUnitFactsAsync(index, request);

		Assert.True(result.HasValue);
		Assert.Empty(result.Data!.Units);
		Assert.Equal(ModInformationPropertyStatus.Fresh, result.State.Status);
		await reader.DisposeAsync();
	}

	[Fact]
	public async Task Reader_InvalidateNode_DropsMatchingOperationFacts()
	{
		var workspace = new CountingWorkspaceReader();
		var reader = new ModInformationReader(workspace);
		var nodeId = ModNodeId.New();
		var context = ModInformationRequestContext.Create(ModInformationCacheScope.Operation, operationId: Guid.NewGuid());
		var request = new ModInformationReadRequest("invalidate-node.patch", context, NodeId: nodeId);

		var first = await reader.ReadPatchIndexAsync(request);
		reader.InvalidateNode(nodeId);
		var second = await reader.ReadPatchIndexAsync(request);

		Assert.Equal(ModInformationPropertyStatus.Fresh, first.State.Status);
		Assert.Equal(ModInformationPropertyStatus.Fresh, second.State.Status);
		Assert.Equal(2, workspace.Count);
		await reader.DisposeAsync();
	}

	[Fact]
	public async Task Reader_InvalidationDuringInFlightIndex_DoesNotWriteOldResultBackToSessionCache()
	{
		var workspace = new BlockingWorkspaceReader();
		await using var reader = new ModInformationReader(workspace);
		var nodeId = ModNodeId.New();
		var context = ModInformationRequestContext.Create(ModInformationCacheScope.Session, sessionId: Guid.NewGuid());
		var request = new ModInformationReadRequest("inflight-invalidate.patch", context, NodeId: nodeId);

		var firstTask = reader.ReadPatchIndexAsync(request).AsTask();
		await workspace.ReadStarted.Task;
		reader.InvalidateNode(nodeId);
		workspace.ReleaseRead.TrySetResult(true);

		var first = await firstTask;
		var second = await reader.ReadPatchIndexAsync(request);

		Assert.Equal(ModInformationPropertyStatus.Stale, first.State.Status);
		Assert.Equal(ModInformationPropertyStatus.Fresh, second.State.Status);
		Assert.Equal(2, workspace.Count);
	}

	[Fact]
	public async Task PatchGroupProvider_ReusesDeeperAnalysisAndClearsItWhenReaderInvalidatesNode()
	{
		var directory = Path.Combine(Path.GetTempPath(), "HD2ModCoreTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var patchPath = Path.Combine(directory, "0123456789abcdef.patch_0");
			await File.WriteAllBytesAsync(patchPath, []);
			var node = new ModNode(ModNodeId.New(), string.Empty, new ModNodeMetadata("Test", null, DateTimeOffset.UtcNow, null), [], []);
			var workspace = new FixedCountingWorkspaceReader(patchPath);
			await using var reader = new ModInformationReader(workspace);
			var full = new ModInformationPatchGroupAnalysisProvider(reader, depth: HD2ModAdaptation.Analysis.PatchAnalysisDepth.Full);
			var inventory = new ModInformationPatchGroupAnalysisProvider(reader, depth: HD2ModAdaptation.Analysis.PatchAnalysisDepth.Inventory);

			var fullAnalysis = Assert.Single(await full.AnalyzeNodeAsync(node, directory));
			var inventoryAnalysis = Assert.Single(await inventory.AnalyzeNodeAsync(node, directory));

			Assert.Same(fullAnalysis, inventoryAnalysis);
			Assert.Equal(1, workspace.Count);

			reader.InvalidateNode(node.Id);
			var rebuilt = Assert.Single(await inventory.AnalyzeNodeAsync(node, directory));

			Assert.NotSame(fullAnalysis, rebuilt);
			Assert.Equal(2, workspace.Count);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task PatchGroupProvider_CoalescesSameDepthInFlightAnalysis()
	{
		var directory = Path.Combine(Path.GetTempPath(), "HD2ModCoreTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var patchPath = Path.Combine(directory, "0123456789abcdef.patch_0");
			await File.WriteAllBytesAsync(patchPath, []);
			var node = new ModNode(ModNodeId.New(), string.Empty, new ModNodeMetadata("Test", null, DateTimeOffset.UtcNow, null), [], []);
			var workspace = new BlockingWorkspaceReader(patchPath);
			await using var reader = new ModInformationReader(workspace);
			var provider = new ModInformationPatchGroupAnalysisProvider(reader, depth: HD2ModAdaptation.Analysis.PatchAnalysisDepth.Inventory);

			var firstTask = provider.AnalyzeNodeAsync(node, directory).AsTask();
			await workspace.ReadStarted.Task;
			var secondTask = provider.AnalyzeNodeAsync(node, directory).AsTask();
			Assert.Equal(1, workspace.Count);
			workspace.ReleaseRead.TrySetResult(true);

			var first = Assert.Single(await firstTask);
			var second = Assert.Single(await secondTask);
			Assert.Same(first, second);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static ModInformationCacheKey CreateKey(string revision, ModInformationPropertyKind property)
		=> ModInformationCacheKey.Create(
			FactKey.ForNode(new ModNodeId(Guid.NewGuid()), property),
			ModContentRevision.Create(revision));

	private sealed class CountingWorkspaceReader : IPatchWorkspaceReader
	{
		private int _count;
		public int Count => _count;
		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<AdaptationPatchTocEntry>>([]);
		public ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
		{
			_count++;
			return ValueTask.FromResult(new PatchWorkspaceIndex(sourcePatchTocPath, [], [(byte)_count]));
		}
		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(sourcePatchTocPath, [], []));
	}

	private sealed class FixedWorkspaceReader(PatchWorkspaceIndex index) : IPatchWorkspaceReader
	{
		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(index.Entries);
		public ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(index);
		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(index.SourcePatchTocPath, [], index.HeaderTemplateTocData));
	}

	private sealed class DelayedCountingWorkspaceReader : IPatchWorkspaceReader
	{
		private int _count;
		public int Count => Volatile.Read(ref _count);

		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<AdaptationPatchTocEntry>>([]);

		public async ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _count);
			await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
			return new PatchWorkspaceIndex(sourcePatchTocPath, [], [1]);
		}

		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(sourcePatchTocPath, [], []));
	}

	private sealed class FixedCountingWorkspaceReader(string patchPath) : IPatchWorkspaceReader
	{
		private int _count;
		public int Count => _count;
		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<AdaptationPatchTocEntry>>([CreateEntry(patchPath)]);
		public ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
		{
			_count++;
			return ValueTask.FromResult(new PatchWorkspaceIndex(patchPath, [CreateEntry(patchPath)], [(byte)_count]));
		}
		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(patchPath, [], []));
	}

	private sealed class BlockingWorkspaceReader(string? patchPath = null) : IPatchWorkspaceReader
	{
		private readonly string _patchPath = patchPath ?? "blocking.patch";
		private int _count;
		public int Count => _count;
		public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<AdaptationPatchTocEntry>>([CreateEntry(_patchPath)]);
		public async ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
		{
			_count++;
			ReadStarted.TrySetResult(true);
			await ReleaseRead.Task.WaitAsync(cancellationToken);
			return new PatchWorkspaceIndex(_patchPath, [CreateEntry(_patchPath)], [(byte)_count]);
		}
		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(_patchPath, [], []));
	}

	private static AdaptationPatchTocEntry CreateEntry(string patchPath)
		=> new(new HD2ModAdaptation.PatchReconstruction.AssetKey(MaterialDependencyResolver.TextureTypeId, 1), patchPath, Path.GetFileName(patchPath));
}
