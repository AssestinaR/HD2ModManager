using System.Collections.Concurrent;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：编排 FileFacts 与 AssetInventory 请求并合并相同的进行中任务，不承载 Patch 专业解析。
// Purpose: Orchestrates FileFacts and AssetInventory requests while coalescing identical work.
public sealed class ModInformationCenter : IModInformationCenter, IAsyncDisposable
{
	private readonly IModFileFactsProducer _fileFactsProducer;
	private readonly IAssetInventoryProducer _assetInventoryProducer;
	private readonly IReferenceGraphProducer? _referenceGraphProducer;
	private readonly IMaintenanceAnalysisProducer? _maintenanceProducer;
	private readonly IUnitVersionInformationProducer? _unitVersionProducer;
	private readonly IAdvancedUnitAnalysisProducer? _advancedUnitAnalysisProducer;
	private readonly IModThumbnailProducer? _thumbnailProducer;
	private readonly IModFileFactsCache? _fileFactsCache;
	private readonly IModInformationCache? _informationCache;
	private readonly IModDataIndex? _modDataIndex;
	private readonly IReferenceGraphIndexWriter? _referenceGraphIndexWriter;
	private readonly IAsyncDisposable? _ownedInformationReader;
	// 作用：进行中的生产任务按"事实身份 + 输入指纹"登记，取代旧的 "{nodeId}|{kind}|{generation}" 字符串键。
	// Purpose: In-flight production is registered by (FactKey, revision) instead of the old concatenated string key.
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<PatchFileIndex>>>> _fileTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<ModContentFacts>>>> _assetTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<ReferenceGraphFacts>>>> _referenceTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<MaintenanceAnalysisFacts>>>> _maintenanceTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<ModUnitVersionFacts>>>> _unitVersionTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<AdvancedUnitAnalysisFacts>>>> _advancedUnitAnalysisTasks = new();
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<ModThumbnailFacts>>>> _thumbnailTasks = new();
	private readonly ConcurrentDictionary<ModNodeId, byte> _invalidatedNodes = new();
	private readonly ConcurrentDictionary<ModNodeId, CancellationTokenSource> _nodeCancellations = new();
	private readonly CancellationTokenSource _shutdown = new();
	private readonly object _nodeStateGate = new();
	public event EventHandler<ModInformationDiagnostic>? DiagnosticRecorded;
	public event EventHandler<ModInformationProductionStarted>? ProductionStarted;

	public ModInformationCenter(IModFileFactsProducer fileFactsProducer, IAssetInventoryProducer assetInventoryProducer, IModFileFactsCache? fileFactsCache = null, IReferenceGraphProducer? referenceGraphProducer = null, IMaintenanceAnalysisProducer? maintenanceProducer = null, IUnitVersionInformationProducer? unitVersionProducer = null, IModInformationCache? informationCache = null, IAdvancedUnitAnalysisProducer? advancedUnitAnalysisProducer = null, IModThumbnailProducer? thumbnailProducer = null, IModDataIndex? modDataIndex = null, IReferenceGraphIndexWriter? referenceGraphIndexWriter = null, IAsyncDisposable? ownedInformationReader = null)
	{
		_fileFactsProducer = fileFactsProducer ?? throw new ArgumentNullException(nameof(fileFactsProducer));
		_assetInventoryProducer = assetInventoryProducer ?? throw new ArgumentNullException(nameof(assetInventoryProducer));
		_fileFactsCache = fileFactsCache;
		_informationCache = informationCache;
		_referenceGraphProducer = referenceGraphProducer;
		_maintenanceProducer = maintenanceProducer;
		_unitVersionProducer = unitVersionProducer;
		_advancedUnitAnalysisProducer = advancedUnitAnalysisProducer;
		_thumbnailProducer = thumbnailProducer;
		_modDataIndex = modDataIndex;
		_referenceGraphIndexWriter = referenceGraphIndexWriter;
		_ownedInformationReader = ownedInformationReader;
	}

	public ValueTask<ModInformationResult<PatchFileIndex>> RequestFileFactsAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.FileFacts) throw new ArgumentException("Request kind must be FileFacts.", nameof(request));
		var nodeStates = snapshot.Nodes.Keys.ToDictionary(nodeId => nodeId, GetNodeCancellation);
		// 快照级事实的主体是"库 + 该快照覆盖的节点集合"，这样 InvalidateNodeAsync 仍能按节点精确命中。
		// The snapshot fact's subject is the library plus the covered node set, so per-node invalidation still matches exactly.
		var key = new FactRevisionKey(
			new FactKey(FactSubject.LibrarySnapshot(snapshot.Nodes.Keys), ModInformationPropertyKind.PatchCatalog),
			request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<PatchFileIndex>>>(
			() => ProduceFileFactsAndRemoveAsync(key, snapshot, modsRootDirectory, request, nodeStates),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _fileTasks.GetOrAdd(key, entry);
		return new ValueTask<ModInformationResult<PatchFileIndex>>(AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken));
	}

	public async ValueTask<ModInformationResult<ModContentFacts>> RequestAssetInventoryAsync(
		ModNode node,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.AssetInventory) throw new ArgumentException("Request kind must be AssetInventory.", nameof(request));
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation
			?? (_assetInventoryProducer is IAssetInventoryGenerationProvider generationProvider
				? generationProvider.ComputeGeneration(node, modsRootDirectory)
				: node.ContentFingerprint ?? ComputeNodeGeneration(node, modsRootDirectory));
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<ModContentFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null && IsNodeRequestCurrent(node.Id, nodeCancellation))
				{
					_modDataIndex?.Update(cached);
					return new ModInformationResult<ModContentFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
				}
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
			{
				_ = exception;
			}
		}
		if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
			nodeCancellation = BeginNodeRequest(node.Id);
		var entry = new Lazy<Task<ModInformationResult<ModContentFacts>>>(
			() => ProduceAssetInventoryAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _assetTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<ModInformationResult<ReferenceGraphFacts>> RequestReferenceGraphAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.ReferenceGraph) throw new ArgumentException("Request kind must be ReferenceGraph.", nameof(request));
		if (_referenceGraphProducer is null)
			return new ModInformationResult<ReferenceGraphFacts>(null, ModInformationStatus.Unavailable, ModInformationKind.ReferenceGraph, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "ReferenceGraphUnavailable", "ReferenceGraph producer is not configured.", node.RelativePath, node.Id)]);
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation ?? ComputeNodeGeneration(node, modsRootDirectory);
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<ReferenceGraphFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null && IsNodeRequestCurrent(node.Id, nodeCancellation))
				{
					_modDataIndex?.Update(cached);
					return new ModInformationResult<ReferenceGraphFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
				}
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { _ = exception; }
		}
		if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
			nodeCancellation = BeginNodeRequest(node.Id);
		var entry = new Lazy<Task<ModInformationResult<ReferenceGraphFacts>>>(() => ProduceReferenceGraphAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation), LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _referenceTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask InvalidateNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		CancellationTokenSource? nodeCancellation;
		lock (_nodeStateGate)
		{
			_invalidatedNodes[nodeId] = 0;
			_nodeCancellations.TryRemove(nodeId, out nodeCancellation);
			nodeCancellation?.Cancel();
		}
		var subject = FactSubject.Node(nodeId);
		foreach (var pair in _assetTasks.Where(pair => pair.Key.Key.Subject == subject))
			_assetTasks.TryRemove(pair.Key, out _);
		// FileFacts is keyed by the complete snapshot rather than one node.  Drop
		// any in-flight snapshot containing the changed node so a subsequent read
		// cannot coalesce onto a task that started before the commit.
		var nodeToken = nodeId.Value.ToString("N");
		foreach (var pair in _fileTasks.Where(pair => pair.Key.Key.Subject.Kind == FactSubjectKind.Library
			&& pair.Key.Key.Subject.Value.Contains(nodeToken, StringComparison.OrdinalIgnoreCase)))
			_fileTasks.TryRemove(pair.Key, out _);
		foreach (var pair in _referenceTasks.Where(pair => pair.Key.Key.Subject == subject))
			_referenceTasks.TryRemove(pair.Key, out _);
		foreach (var pair in _maintenanceTasks.Where(pair => pair.Key.Key.Subject == subject))
			_maintenanceTasks.TryRemove(pair.Key, out _);
		foreach (var pair in _unitVersionTasks.Where(pair => pair.Key.Key.Subject == subject))
			_unitVersionTasks.TryRemove(pair.Key, out _);
		foreach (var pair in _advancedUnitAnalysisTasks.Where(pair => pair.Key.Key.Subject == subject))
			_advancedUnitAnalysisTasks.TryRemove(pair.Key, out _);
		foreach (var pair in _thumbnailTasks.Where(pair => pair.Key.Key.Subject == subject))
			_thumbnailTasks.TryRemove(pair.Key, out _);
		// FileFacts is cached as a snapshot-wide product, so a node mutation must
		// remove every persisted snapshot that contains this node as well.  Without
		// this call a repair can replace Patch files while RequestFileFactsAsync
		// continues returning the old snapshot under the same generation.
		if (_fileFactsCache is not null)
			await _fileFactsCache.DeleteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
		if (_informationCache is not null)
			await _informationCache.DeleteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
		if (_modDataIndex is not null)
			await _modDataIndex.RemoveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
		if (_referenceGraphIndexWriter is not null)
			await _referenceGraphIndexWriter.DeleteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
		if (nodeCancellation is not null)
		{
			nodeCancellation.Dispose();
		}
	}

	public async ValueTask<ModInformationResult<AdvancedUnitAnalysisFacts>> RequestAdvancedUnitAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.AdvancedUnitAnalysis) throw new ArgumentException("Request kind must be AdvancedUnitAnalysis.", nameof(request));
		if (_advancedUnitAnalysisProducer is null)
			return new ModInformationResult<AdvancedUnitAnalysisFacts>(null, ModInformationStatus.Unavailable, request.Kind, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "AdvancedUnitAnalysisUnavailable", "Advanced Unit analysis producer is not configured.", node.RelativePath, node.Id)]);
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation ?? AdvancedUnitAnalysisProducer.ComputeGeneration(node, modsRootDirectory);
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<AdvancedUnitAnalysisFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null) return new ModInformationResult<AdvancedUnitAnalysisFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { _ = exception; }
		}
		var entry = new Lazy<Task<ModInformationResult<AdvancedUnitAnalysisFacts>>>(() => ProduceAdvancedUnitAnalysisAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation), LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _advancedUnitAnalysisTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<ModInformationResult<ModThumbnailFacts>> RequestThumbnailAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.Thumbnail) throw new ArgumentException("Request kind must be Thumbnail.", nameof(request));
		if (_thumbnailProducer is null)
			return new ModInformationResult<ModThumbnailFacts>(null, ModInformationStatus.Unavailable, request.Kind, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "ThumbnailUnavailable", "Thumbnail producer is not configured.", node.RelativePath, node.Id)]);
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation ?? ComputeThumbnailGeneration(node, modsRootDirectory);
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<ModThumbnailFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null) return new ModInformationResult<ModThumbnailFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { _ = exception; }
		}
		var entry = new Lazy<Task<ModInformationResult<ModThumbnailFacts>>>(() => ProduceThumbnailAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation), LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _thumbnailTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	public ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(IReadOnlyCollection<AssetKey> assetKeys, ModNodeId? excludedNodeId = null, CancellationToken cancellationToken = default)
		=> _modDataIndex is null
			? ValueTask.FromResult(new ModDataIndexSummary(ModDataIndexStatus.Unavailable, 0, 0))
			: _modDataIndex.GetAssetRelationSummaryAsync(assetKeys, excludedNodeId, cancellationToken);

	private CancellationTokenSource GetNodeCancellation(ModNodeId nodeId)
		=> _nodeCancellations.GetOrAdd(nodeId, static _ => new CancellationTokenSource());

	private CancellationTokenSource BeginNodeRequest(ModNodeId nodeId)
	{
		lock (_nodeStateGate)
		{
			_invalidatedNodes.TryRemove(nodeId, out _);
			return GetNodeCancellation(nodeId);
		}
	}

	private bool IsNodeRequestCurrent(ModNodeId nodeId, CancellationTokenSource nodeCancellation)
		=> !_invalidatedNodes.ContainsKey(nodeId)
			&& _nodeCancellations.TryGetValue(nodeId, out var current)
			&& ReferenceEquals(current, nodeCancellation);

	public async ValueTask<ModInformationResult<ModUnitVersionFacts>> RequestUnitVersionAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.UnitVersion) throw new ArgumentException("Request kind must be UnitVersion.", nameof(request));
		if (_unitVersionProducer is null)
			return new ModInformationResult<ModUnitVersionFacts>(null, ModInformationStatus.Unavailable, ModInformationKind.UnitVersion, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "UnitVersionUnavailable", "Unit version producer is not configured.", node.RelativePath, node.Id)]);
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation ?? ComputeNodeGeneration(node, modsRootDirectory);
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<ModUnitVersionFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null) return new ModInformationResult<ModUnitVersionFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { _ = exception; }
		}
		var entry = new Lazy<Task<ModInformationResult<ModUnitVersionFacts>>>(() => ProduceUnitVersionAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation), LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _unitVersionTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<ModInformationResult<MaintenanceAnalysisFacts>> RequestMaintenanceAnalysisAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.MaintenanceAnalysis) throw new ArgumentException("Request kind must be MaintenanceAnalysis.", nameof(request));
		if (_maintenanceProducer is null)
			return new ModInformationResult<MaintenanceAnalysisFacts>(null, ModInformationStatus.Unavailable, ModInformationKind.MaintenanceAnalysis, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "MaintenanceAnalysisUnavailable", "Maintenance analysis producer is not configured.", node.RelativePath, node.Id)]);
		var nodeCancellation = BeginNodeRequest(node.Id);
		var generation = request.Generation ?? ComputeNodeGeneration(node, modsRootDirectory);
		var effectiveRequest = request with { Generation = generation };
		var key = new FactRevisionKey(new FactKey(FactSubject.Node(node.Id), effectiveRequest.EffectiveProperty), generation);
		if (!effectiveRequest.RequireFresh && _informationCache is not null)
		{
			try
			{
				var cached = await _informationCache.TryLoadAsync<MaintenanceAnalysisFacts>(effectiveRequest.Kind, node.Id, generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null) return new ModInformationResult<MaintenanceAnalysisFacts>(cached, ModInformationStatus.Cached, effectiveRequest.Kind, generation, cached.Issues, false, false, true);
			}
			catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { _ = exception; }
		}
		var entry = new Lazy<Task<ModInformationResult<MaintenanceAnalysisFacts>>>(() => ProduceMaintenanceAndRemoveAsync(key, node, modsRootDirectory, effectiveRequest, nodeCancellation), LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _maintenanceTasks.GetOrAdd(key, entry);
		return await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false);
	}

	private async Task<ModInformationResult<PatchFileIndex>> ProduceFileFactsAndRemoveAsync(FactRevisionKey key, LibrarySnapshot snapshot, string root, ModInformationRequest request, IReadOnlyDictionary<ModNodeId, CancellationTokenSource> nodeStates)
	{
		var started = DateTimeOffset.UtcNow;
		ModInformationResult<PatchFileIndex>? completed = null;
		try
		{
			completed = await ProduceFileFactsAsync(key, snapshot, root, request, nodeStates, _shutdown.Token, started).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(completed is null
				? new ModInformationDiagnostic(request.Source, request.Kind, null, request.Generation, started, DateTimeOffset.UtcNow, false, false, ModInformationStatus.Failed, []) { OperationKey = key.ToString() }
				: new ModInformationDiagnostic(request.Source, request.Kind, null, completed.Generation, started, DateTimeOffset.UtcNow, completed.CacheHit, completed.WasCoalesced, completed.Status, completed.Issues) { OperationKey = key.ToString() });
			_fileTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<PatchFileIndex>> ProduceFileFactsAsync(FactRevisionKey key, LibrarySnapshot snapshot, string root, ModInformationRequest request, IReadOnlyDictionary<ModNodeId, CancellationTokenSource> nodeStates, CancellationToken cancellationToken, DateTimeOffset started)
	{
		var generation = request.Generation ?? ComputeFileGeneration(snapshot, root);
		if (!request.RequireFresh && _fileFactsCache is not null)
		{
			try
			{
				var cached = await _fileFactsCache.TryLoadAsync(generation, cancellationToken).ConfigureAwait(false);
				if (cached is not null && snapshot.Nodes.Keys.All(nodeId => IsNodeRequestCurrent(nodeId, nodeStates[nodeId])))
					return new ModInformationResult<PatchFileIndex>(cached, ModInformationStatus.Cached, ModInformationKind.FileFacts, generation, cached.Issues, false, false, true);
			}
			catch (Exception exception)
			{
				// 缓存读取失败不应阻断 FileFacts 的直接生产。
				_ = exception;
			}
		}
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, generation, started, key.ToString()));
		try
		{
			var data = await _fileFactsProducer.ProduceAsync(snapshot, root, cancellationToken).ConfigureAwait(false);
			var requestStillCurrent = snapshot.Nodes.Keys.All(nodeId => IsNodeRequestCurrent(nodeId, nodeStates[nodeId]));
			if (!requestStillCurrent)
			{
				// A snapshot-wide read may finish after one of its nodes was committed.
				// Never return that pre-commit result to a caller or persist it again.
				return new ModInformationResult<PatchFileIndex>(
					null,
					ModInformationStatus.Failed,
					ModInformationKind.FileFacts,
					generation,
					[new CoreIssue(CoreIssueSeverity.Warning, "FileFactsInvalidated", "FileFacts production completed after a Mod content commit and was discarded.", root)],
					false,
					true);
			}
			if (_fileFactsCache is not null && requestStillCurrent)
			{
				try { await _fileFactsCache.SaveAsync(generation, data, cancellationToken).ConfigureAwait(false); }
				catch (Exception exception)
				{
					data = data with { Issues = [.. data.Issues, CreateCacheSaveIssue(ModInformationKind.FileFacts, generation, root, exception)] };
				}
			}
			return new ModInformationResult<PatchFileIndex>(data, ModInformationStatus.Fresh, ModInformationKind.FileFacts, generation, data.Issues, false, false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<PatchFileIndex>(null, ModInformationStatus.Failed, ModInformationKind.FileFacts, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "FileFactsCanceled", "FileFacts production was canceled.", root)], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<PatchFileIndex>(null, ModInformationStatus.Failed, ModInformationKind.FileFacts, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "FileFactsProductionFailed", exception.Message, root, null, exception.ToString())], false, true);
		}
	}

	private static string ComputeFileGeneration(LibrarySnapshot snapshot, string root)
	{
		var builder = new System.Text.StringBuilder();
		foreach (var node in snapshot.Nodes.Values.OrderBy(node => node.Id.Value))
		{
			var directory = Path.Combine(root, node.RelativePath);
			builder.Append(node.Id.Value.ToString("N")).Append(':').Append(node.RelativePath).AppendLine();
			if (!Directory.Exists(directory)) continue;
			var paths = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
				.Concat(Directory.Exists(Path.Combine(directory, "Overwrite"))
					? Directory.EnumerateFiles(Path.Combine(directory, "Overwrite"), "*", SearchOption.TopDirectoryOnly)
					: Array.Empty<string>());
			foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
			{
				var info = new FileInfo(path);
				var relative = Path.GetRelativePath(directory, path);
				builder.Append(relative.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
			}
		}
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	private static string ComputeNodeGeneration(ModNode node, string root)
	{
		var directory = Path.Combine(root, node.RelativePath);
		if (!Directory.Exists(directory)) return string.Empty;
		var builder = new System.Text.StringBuilder();
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			var info = new FileInfo(path);
			builder.Append(info.Name.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
		}
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	private static string ComputeThumbnailGeneration(ModNode node, string root)
	{
		var source = ModIconLocator.TryResolve(Path.Combine(root, node.RelativePath));
		if (source is null) return ComputeNodeGeneration(node, root);
		var info = new FileInfo(source);
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{node.Id.Value:N}|{info.Name.ToLowerInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
	}

	private async Task<ModInformationResult<ModContentFacts>> ProduceAssetInventoryAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<ModContentFacts>? completed = null;
		try
		{
			completed = await ProduceAssetInventoryAsync(node, root, request, nodeCancellation).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_assetTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<ModContentFacts>> ProduceAssetInventoryAsync(ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var cancellationToken = nodeCancellation.Token;
		ModInformationCacheEntry<ModContentFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<ModContentFacts>(ModInformationKind.AssetInventory, node.Id, cancellationToken).ConfigureAwait(false);
			var data = await _assetInventoryProducer.GetNodeFactsAsync(node, root, cancellationToken).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return CreateFailedOrStale(stale, ModInformationKind.AssetInventory, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "AssetInventoryInvalidated", "AssetInventory production completed after the node was invalidated.", node.RelativePath, node.Id));
			if (_informationCache is not null)
			{
				try { await _informationCache.SaveAsync(ModInformationKind.AssetInventory, node.Id, request.Generation ?? data.ContentGeneration, data, cancellationToken).ConfigureAwait(false); }
				catch (Exception exception)
				{
					data = data with { Issues = [.. data.Issues, CreateCacheSaveIssue(ModInformationKind.AssetInventory, request.Generation ?? data.ContentGeneration, node.RelativePath, exception)] };
				}
			}
			_modDataIndex?.Update(data);
			return new ModInformationResult<ModContentFacts>(data, ModInformationStatus.Fresh, ModInformationKind.AssetInventory, request.Generation ?? data.ContentGeneration, data.Issues, false, false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return CreateFailedOrStale(stale, ModInformationKind.AssetInventory, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "AssetInventoryCanceled", "AssetInventory production was canceled.", node.RelativePath, node.Id));
		}
		catch (Exception exception)
		{
			return CreateFailedOrStale(stale, ModInformationKind.AssetInventory, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "AssetInventoryProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
	}

	private async Task<ModInformationResult<ReferenceGraphFacts>> ProduceReferenceGraphAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<ReferenceGraphFacts>? completed = null;
		ModInformationCacheEntry<ReferenceGraphFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<ReferenceGraphFacts>(ModInformationKind.ReferenceGraph, node.Id, nodeCancellation.Token).ConfigureAwait(false);
			var data = await _referenceGraphProducer!.ProduceAsync(node, root, nodeCancellation.Token).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return completed = CreateFailedOrStale(stale, ModInformationKind.ReferenceGraph, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "ReferenceGraphInvalidated", "ReferenceGraph production completed after the node was invalidated.", node.RelativePath, node.Id));
			var result = new ModInformationResult<ReferenceGraphFacts>(data, ModInformationStatus.Fresh, ModInformationKind.ReferenceGraph, data.Generation, data.Issues);
			if (_informationCache is not null) await _informationCache.SaveAsync(ModInformationKind.ReferenceGraph, node.Id, request.Generation ?? data.Generation, data, nodeCancellation.Token).ConfigureAwait(false);
			if (_referenceGraphIndexWriter is not null) await _referenceGraphIndexWriter.ReplaceNodeAsync(data, nodeCancellation.Token).ConfigureAwait(false);
			_modDataIndex?.Update(data);
			completed = result;
			return completed;
		}
		catch (OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.ReferenceGraph, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "ReferenceGraphInvalidated", "ReferenceGraph production was canceled because the node was invalidated.", node.RelativePath, node.Id));
		}
		catch (Exception exception)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.ReferenceGraph, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "ReferenceGraphProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_referenceTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<MaintenanceAnalysisFacts>> ProduceMaintenanceAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<MaintenanceAnalysisFacts>? completed = null;
		ModInformationCacheEntry<MaintenanceAnalysisFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<MaintenanceAnalysisFacts>(ModInformationKind.MaintenanceAnalysis, node.Id, nodeCancellation.Token).ConfigureAwait(false);
			var inventoryRequest = new ModInformationRequest(ModInformationKind.AssetInventory, $"{request.Source}:MaintenancePrerequisite", request.Generation, request.RequireFresh);
			var inventory = await RequestAssetInventoryAsync(node, root, inventoryRequest, nodeCancellation.Token).ConfigureAwait(false);
			if (inventory.Data is null)
			{
				return completed = new ModInformationResult<MaintenanceAnalysisFacts>(null, ModInformationStatus.Failed, ModInformationKind.MaintenanceAnalysis, inventory.Generation, inventory.Issues, inventory.CacheHit, true);
			}
			var data = await _maintenanceProducer!.ProduceAsync(node, inventory.Data, nodeCancellation.Token).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return completed = CreateFailedOrStale(stale, ModInformationKind.MaintenanceAnalysis, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "MaintenanceAnalysisInvalidated", "Maintenance analysis completed after the node was invalidated.", node.RelativePath, node.Id));
			var result = new ModInformationResult<MaintenanceAnalysisFacts>(data, ModInformationStatus.Fresh, ModInformationKind.MaintenanceAnalysis, request.Generation ?? data.Generation, data.Issues);
			if (_informationCache is not null) await _informationCache.SaveAsync(ModInformationKind.MaintenanceAnalysis, node.Id, request.Generation ?? data.Generation, data, nodeCancellation.Token).ConfigureAwait(false);
			completed = result;
			return completed;
		}
		catch (OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.MaintenanceAnalysis, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "MaintenanceAnalysisInvalidated", "Maintenance analysis was canceled because the node was invalidated.", node.RelativePath, node.Id));
		}
		catch (Exception exception)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.MaintenanceAnalysis, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "MaintenanceAnalysisProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_maintenanceTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<ModUnitVersionFacts>> ProduceUnitVersionAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<ModUnitVersionFacts>? completed = null;
		ModInformationCacheEntry<ModUnitVersionFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<ModUnitVersionFacts>(ModInformationKind.UnitVersion, node.Id, nodeCancellation.Token).ConfigureAwait(false);
			var data = await _unitVersionProducer!.ProduceAsync(node, root, nodeCancellation.Token).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return completed = CreateFailedOrStale(stale, ModInformationKind.UnitVersion, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "UnitVersionInvalidated", "Unit version production completed after the node was invalidated.", node.RelativePath, node.Id));
			var result = new ModInformationResult<ModUnitVersionFacts>(data, ModInformationStatus.Fresh, ModInformationKind.UnitVersion, request.Generation ?? data.Generation, data.Issues);
			if (_informationCache is not null) await _informationCache.SaveAsync(ModInformationKind.UnitVersion, node.Id, request.Generation ?? data.Generation, data, nodeCancellation.Token).ConfigureAwait(false);
			completed = result;
			return completed;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.UnitVersion, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "UnitVersionProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
		catch (OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, ModInformationKind.UnitVersion, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "UnitVersionInvalidated", "Unit version production was canceled because the node was invalidated.", node.RelativePath, node.Id));
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_unitVersionTasks.TryRemove(key, out _);
		}
	}

	private static async Task<ModInformationResult<T>> AwaitEntryAsync<T>(Lazy<Task<ModInformationResult<T>>> entry, bool owner, CancellationToken cancellationToken)
	{
		var result = await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
		return owner ? result : result with { WasCoalesced = true };
	}

	public async ValueTask DisposeAsync()
	{
		_shutdown.Cancel();
		_shutdown.Dispose();
		_fileTasks.Clear();
		_assetTasks.Clear();
		_referenceTasks.Clear();
		_maintenanceTasks.Clear();
		_unitVersionTasks.Clear();
		_advancedUnitAnalysisTasks.Clear();
		_thumbnailTasks.Clear();
		_invalidatedNodes.Clear();
		foreach (var cancellation in _nodeCancellations.Values)
		{
			cancellation.Cancel();
			cancellation.Dispose();
		}
		_nodeCancellations.Clear();
		if (_ownedInformationReader is not null)
			await _ownedInformationReader.DisposeAsync().ConfigureAwait(false);
	}

	private async Task<ModInformationResult<ModThumbnailFacts>> ProduceThumbnailAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<ModThumbnailFacts>? completed = null;
		ModInformationCacheEntry<ModThumbnailFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<ModThumbnailFacts>(ModInformationKind.Thumbnail, node.Id, nodeCancellation.Token).ConfigureAwait(false);
			var data = await _thumbnailProducer!.ProduceAsync(node, root, nodeCancellation.Token).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "ThumbnailInvalidated", "Thumbnail production completed after the node was invalidated.", node.RelativePath, node.Id));
			if (_informationCache is not null) await _informationCache.SaveAsync(ModInformationKind.Thumbnail, node.Id, request.Generation ?? data.Generation, data, nodeCancellation.Token).ConfigureAwait(false);
			var result = new ModInformationResult<ModThumbnailFacts>(data, ModInformationStatus.Fresh, request.Kind, request.Generation ?? data.Generation, data.Issues);
			completed = result;
			return completed;
		}
		catch (OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "ThumbnailInvalidated", "Thumbnail production was canceled.", node.RelativePath, node.Id));
		}
		catch (Exception exception)
		{
			return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "ThumbnailProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_thumbnailTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<AdvancedUnitAnalysisFacts>> ProduceAdvancedUnitAnalysisAndRemoveAsync(FactRevisionKey key, ModNode node, string root, ModInformationRequest request, CancellationTokenSource nodeCancellation)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, node.Id, request.Generation, started, key.ToString()));
		ModInformationResult<AdvancedUnitAnalysisFacts>? completed = null;
		ModInformationCacheEntry<AdvancedUnitAnalysisFacts>? stale = null;
		try
		{
			stale = await TryLoadStaleAsync<AdvancedUnitAnalysisFacts>(ModInformationKind.AdvancedUnitAnalysis, node.Id, nodeCancellation.Token).ConfigureAwait(false);
			var data = await _advancedUnitAnalysisProducer!.ProduceAsync(node, root, nodeCancellation.Token).ConfigureAwait(false);
			if (!IsNodeRequestCurrent(node.Id, nodeCancellation))
				return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "AdvancedUnitAnalysisInvalidated", "Advanced Unit analysis completed after the node was invalidated.", node.RelativePath, node.Id));
			if (_informationCache is not null) await _informationCache.SaveAsync(ModInformationKind.AdvancedUnitAnalysis, node.Id, request.Generation ?? data.Generation, data, nodeCancellation.Token).ConfigureAwait(false);
			var result = new ModInformationResult<AdvancedUnitAnalysisFacts>(data, ModInformationStatus.Fresh, request.Kind, request.Generation ?? data.Generation, data.Issues);
			completed = result;
			return completed;
		}
		catch (OperationCanceledException)
		{
			return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Warning, "AdvancedUnitAnalysisInvalidated", "Advanced Unit analysis was canceled.", node.RelativePath, node.Id));
		}
		catch (Exception exception)
		{
			return completed = CreateFailedOrStale(stale, request.Kind, request.Generation, new CoreIssue(CoreIssueSeverity.Error, "AdvancedUnitAnalysisProductionFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}
		finally
		{
			RecordDiagnostic(CreateDiagnostic(request, node.Id, key, started, completed));
			_advancedUnitAnalysisTasks.TryRemove(key, out _);
		}
	}

	private async ValueTask<ModInformationCacheEntry<T>?> TryLoadStaleAsync<T>(ModInformationKind kind, ModNodeId nodeId, CancellationToken cancellationToken)
	{
		if (_informationCache is null) return null;
		try { return await _informationCache.TryLoadLatestAsync<T>(kind, nodeId, cancellationToken).ConfigureAwait(false); }
		catch (IOException) { return default; }
		catch (JsonException) { return default; }
		catch (UnauthorizedAccessException) { return default; }
		catch (OperationCanceledException) { return default; }
	}

	private static ModInformationResult<T> CreateFailedOrStale<T>(ModInformationCacheEntry<T>? stale, ModInformationKind kind, string? requestedGeneration, CoreIssue issue)
		=> stale is null
			? new ModInformationResult<T>(default, ModInformationStatus.Failed, kind, requestedGeneration, [issue], false, true)
			: new ModInformationResult<T>(stale.Data, ModInformationStatus.Stale, kind, stale.Generation, [issue], false, true, true);

	private static ModInformationDiagnostic CreateDiagnostic<T>(ModInformationRequest request, ModNodeId nodeId, FactRevisionKey key, DateTimeOffset started, ModInformationResult<T>? result)
		=> result is null
			? new ModInformationDiagnostic(request.Source, request.Kind, nodeId, request.Generation, started, DateTimeOffset.UtcNow, false, false, ModInformationStatus.Failed, []) { OperationKey = key.ToString() }
			: new ModInformationDiagnostic(request.Source, request.Kind, nodeId, result.Generation, started, DateTimeOffset.UtcNow, result.CacheHit, result.WasCoalesced, result.Status, result.Issues) { OperationKey = key.ToString() };

	private static CoreIssue CreateCacheSaveIssue(ModInformationKind kind, string generation, string path, Exception exception)
		=> new(CoreIssueSeverity.Warning, "InformationCacheSaveFailed", $"内容已生成，但 {kind} 缓存保存失败（generation: {generation}）：{exception.Message}", path, ExceptionMessage: exception.ToString());

	private void RecordProductionStarted(ModInformationProductionStarted value)
	{
		try { ProductionStarted?.Invoke(this, value); }
		catch { }
	}

	private void RecordDiagnostic(ModInformationDiagnostic value)
	{
		try { DiagnosticRecorded?.Invoke(this, value); }
		catch { }
	}
}
