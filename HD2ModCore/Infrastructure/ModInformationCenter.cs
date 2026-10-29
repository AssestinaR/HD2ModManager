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
	// 作用：无节点事实（设计文档 §22）的生产者。三者都是可选的——只读场景下中心可以完全不配置它们，
	// 请求会得到 Unavailable 而不是抛异常，与其它可选生产者的行为一致。
	// Purpose: Producers of the node-less facts. All three are optional so a read-only host can omit them.
	private readonly IAssetArchiveIndexService? _assetArchiveIndexService;
	private readonly IGameDataMappingFactsService? _gameDataMappingFactsService;
	private readonly IDeployedOverrideGraphService? _deployedOverrideGraphService;
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
	// 作用：无节点事实共用一本进行中任务账。因为 FactRevisionKey 里已经含属性，三类事实不会互相撞键，
	// 所以不需要三本字典；代价只是取出时要按目标类型还原（Unwrap）。
	// Purpose: One in-flight ledger for every node-less fact. The key already carries the property, so the three
	// fact families cannot collide; the only cost is restoring the concrete type on the way out (Unwrap).
	private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<object>>>> _gameDataTasks = new();
	private readonly ConcurrentDictionary<ModNodeId, byte> _invalidatedNodes = new();
	private readonly ConcurrentDictionary<ModNodeId, CancellationTokenSource> _nodeCancellations = new();
	private readonly CancellationTokenSource _shutdown = new();
	private readonly object _nodeStateGate = new();
	public event EventHandler<ModInformationDiagnostic>? DiagnosticRecorded;
	public event EventHandler<ModInformationProductionStarted>? ProductionStarted;

	/// <summary>索引数据库的位置：护甲→unit 这类权威事实一律从这里读，不读游戏注册表文件。</summary>
	private readonly StoragePaths? _storagePaths;

	public ModInformationCenter(IModFileFactsProducer fileFactsProducer, IAssetInventoryProducer assetInventoryProducer, IModFileFactsCache? fileFactsCache = null, IReferenceGraphProducer? referenceGraphProducer = null, IMaintenanceAnalysisProducer? maintenanceProducer = null, IUnitVersionInformationProducer? unitVersionProducer = null, IModInformationCache? informationCache = null, IAdvancedUnitAnalysisProducer? advancedUnitAnalysisProducer = null, IModThumbnailProducer? thumbnailProducer = null, IModDataIndex? modDataIndex = null, IReferenceGraphIndexWriter? referenceGraphIndexWriter = null, IAsyncDisposable? ownedInformationReader = null, IAssetArchiveIndexService? assetArchiveIndexService = null, IGameDataMappingFactsService? gameDataMappingFactsService = null, IDeployedOverrideGraphService? deployedOverrideGraphService = null, StoragePaths? storagePaths = null)
	{
		_storagePaths = storagePaths;
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
		_assetArchiveIndexService = assetArchiveIndexService;
		_gameDataMappingFactsService = gameDataMappingFactsService;
		_deployedOverrideGraphService = deployedOverrideGraphService;
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
		// 无节点事实（_gameDataTasks）刻意**不**在这里清理：它们的输入是游戏目录、激活状态与资产索引，
		// 没有一项来自 Mod 目录，所以"某个 Mod 变了"不构成它们失效的理由。清掉只会让下一次请求白做一遍。
		// Node-less facts are deliberately not touched here: none of their inputs come from a Mod directory.
		//
		// 每个持久化缓存必须**独立**失效。以前它们是顺序 await 的，任何一处抛异常（例如某个缓存文件
		// 被另一个线程占用时 File.Delete 抛 UnauthorizedAccessException）都会让后面的缓存整片漏掉，
		// 删除 Mod 后磁盘上留下永久残留。现在逐个尝试、逐个收集，最后一次性汇报。
		// Each persisted cache must be invalidated **independently**: a failure in one used to skip
		// every later cache and leave permanent residue on disk after a Mod deletion.
		var failures = new List<Exception>(4);
		try
		{
			// FileFacts is cached as a snapshot-wide product, so a node mutation must
			// remove every persisted snapshot that contains this node as well.  Without
			// this call a repair can replace Patch files while RequestFileFactsAsync
			// continues returning the old snapshot under the same generation.
			await TryInvalidateAsync(
				() => _fileFactsCache?.DeleteNodeAsync(nodeId, cancellationToken) ?? ValueTask.CompletedTask,
				failures).ConfigureAwait(false);
			await TryInvalidateAsync(
				() => _informationCache?.DeleteNodeAsync(nodeId, cancellationToken) ?? ValueTask.CompletedTask,
				failures).ConfigureAwait(false);
			await TryInvalidateAsync(
				() => _modDataIndex?.RemoveNodeAsync(nodeId, cancellationToken) ?? ValueTask.CompletedTask,
				failures).ConfigureAwait(false);
			await TryInvalidateAsync(
				() => _referenceGraphIndexWriter?.DeleteNodeAsync(nodeId, cancellationToken) ?? ValueTask.CompletedTask,
				failures).ConfigureAwait(false);
		}
		finally
		{
			nodeCancellation?.Dispose();
		}

		if (failures.Count != 0)
		{
			throw failures.Count == 1
				? failures[0]
				: new AggregateException($"节点 {nodeId.Value:N} 的信息缓存失效未全部成功。", failures);
		}
	}

	private static async ValueTask TryInvalidateAsync(Func<ValueTask> invalidate, List<Exception> failures)
	{
		try
		{
			await invalidate().ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			// 失效是"尽力而为但必须逐个尝试"：单个缓存的失败不能掩盖其它缓存尚未失效的事实。
			// Best effort, but every cache must still be attempted.
			failures.Add(exception);
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

	// ===== 无节点事实（设计文档 §22） =====
	// 与节点事实的三点差别，都是"没有节点"直接推出来的，不是新策略：
	//   1. 没有节点级取消令牌——用中心自己的关闭令牌，这样某个调用方取消不会连累同一批的其它合并方；
	//   2. 不参与 InvalidateNodeAsync——它们的输入是游戏目录与激活状态，Mod 目录变了影响不到它们；
	//   3. 不落盘——新鲜度由生产者自己的来源指纹保证（索引 source_fingerprint、部署图 DeployedGraphFactCache）。
	// 共同点：仍然按 FactRevisionKey 合并进行中的任务，并发布与节点事实同形的诊断事件。

	public async ValueTask<ModInformationResult<GameDataIndexFacts>> RequestGameDataIndexAsync(
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.GameDataIndex) throw new ArgumentException("Request kind must be GameDataIndex.", nameof(request));
		if (_assetArchiveIndexService is null)
			return CreateUnavailable<GameDataIndexFacts>(ModInformationKind.GameDataIndex, "GameDataIndexUnavailable", "Asset archive index service is not configured.", request.Generation);
		var key = new FactRevisionKey(new FactKey(FactSubject.GameData("index"), request.EffectiveProperty), request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceGameDataIndexAndRemoveAsync(key, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<GameDataIndexFacts>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	public async ValueTask<ModInformationResult<GameDataMappingFacts>> RequestGameDataMappingAsync(
		IReadOnlySet<AssetKey> assetKeys,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(assetKeys);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.GameDataMapping) throw new ArgumentException("Request kind must be GameDataMapping.", nameof(request));
		if (_gameDataMappingFactsService is null)
			return CreateUnavailable<GameDataMappingFacts>(ModInformationKind.GameDataMapping, "GameDataMappingUnavailable", "Game Data mapping service is not configured.", request.Generation);
		// 作用域里带资产键集合的摘要：不同批次的资产是**不同的映射事实**，不能合并成同一次生产。
		var key = new FactRevisionKey(new FactKey(FactSubject.GameData($"mapping:{ComputeAssetKeySetToken(assetKeys)}"), request.EffectiveProperty), request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceGameDataMappingAndRemoveAsync(key, assetKeys, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<GameDataMappingFacts>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	public async ValueTask<ModInformationResult<DeployedOverrideGraph>> RequestDeployedGraphAsync(
		string gameDataDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.DeployedGraph) throw new ArgumentException("Request kind must be DeployedGraph.", nameof(request));
		if (_deployedOverrideGraphService is null)
			return CreateUnavailable<DeployedOverrideGraph>(ModInformationKind.DeployedGraph, "DeployedGraphUnavailable", "Deployed override graph service is not configured.", request.Generation);
		var key = new FactRevisionKey(new FactKey(FactSubject.GameData($"deployed:{gameDataDirectory}"), request.EffectiveProperty), request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceDeployedGraphAndRemoveAsync(key, gameDataDirectory, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<DeployedOverrideGraph>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	// 作用：护甲覆盖视图（设计文档 §4 #23）。它自己不读任何外部输入——先把上游事实请求回来，再做纯投影。
	// Purpose: The armor coverage view. It reads nothing itself: it requests its upstream facts, then projects.
	//
	// 关键点：喂给映射的资产键集合是"**已部署且有归属**的资产键"，而不是"全部 Mod 的资产键"。
	// 后者需要为每个 Mod 请求一次资产清单（逐包解析 patch）——那正是护甲页首开卡顿的根因。
	// 覆盖结论只可能来自已部署的资产，所以这个更小的键集合既够用又便宜。
	public async ValueTask<ModInformationResult<ArmorCoverageFacts>> RequestArmorCoverageAsync(
		string gameDataDirectory,
		IReadOnlySet<string> archiveIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentNullException.ThrowIfNull(archiveIds);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.ArmorCoverage) throw new ArgumentException("Request kind must be ArmorCoverage.", nameof(request));
		if (_assetArchiveIndexService is null || _gameDataMappingFactsService is null || _deployedOverrideGraphService is null)
			return CreateUnavailable<ArmorCoverageFacts>(ModInformationKind.ArmorCoverage, "ArmorCoverageUnavailable", "Game Data index, mapping or deployed graph service is not configured.", request.Generation);
		// 作用域里带归档集合的摘要：不同批护甲是**不同的覆盖事实**，合并它们会让一页拿到另一页的归档集合。
		var key = new FactRevisionKey(
			new FactKey(FactSubject.GameData($"armor-cover:{ArmorCoverageProjection.ScopeToken(archiveIds)}"), request.EffectiveProperty),
			request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceArmorCoverageAndRemoveAsync(key, gameDataDirectory, archiveIds, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<ArmorCoverageFacts>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	// 一批 unit 的 section 结构。与护甲覆盖同一套做法：无节点事实、按"请求的那批 unit"分作用域、
	// 用中心自己的任务账合并并发请求——于是编辑器、CLI、以及将来任何需求方
	// 问同一批 unit 时只会解析一次。
	//
	// 为什么由中心来生产而不是让调用方自己算：谁都能自己扫一遍 patch，但那样每个需求方
	// 都会造一套自己的轮子，而且各自缓存互不可见。放进信息中心之后，"这里可能有你想要的"
	// 这件事对后来者是可见的——这正是信息中心存在的意义。
	public async ValueTask<ModInformationResult<UnitSectionStructureFacts>> RequestUnitSectionStructureAsync(
		string gameDataDirectory,
		IReadOnlySet<string> unitIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentNullException.ThrowIfNull(unitIds);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.UnitSectionStructure)
			throw new ArgumentException("Request kind must be UnitSectionStructure.", nameof(request));
		// 作用域里带 unit 集合的摘要：不同批 unit 是**不同的结构事实**，
		// 合并它们会让一件护甲拿到另一件护甲的 section 数。
		var key = new FactRevisionKey(
			new FactKey(FactSubject.GameData($"unit-sections:{UnitSectionStructureScope(unitIds)}"), request.EffectiveProperty),
			request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceUnitSectionStructureAndRemoveAsync(key, gameDataDirectory, unitIds, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<UnitSectionStructureFacts>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	/// <summary>作用域 token：排过序的 unit 键，保证"同一批 unit"永远得到同一个作用域。</summary>
	internal static string UnitSectionStructureScope(IReadOnlySet<string> unitIds)
	{
		ArgumentNullException.ThrowIfNull(unitIds);
		var normalized = new List<string>(unitIds.Count);
		foreach (var unit in unitIds)
		{
			if (WardrobeSectionFingerprint.TryNormalizeUnit(unit, out var value))
				normalized.Add(value);
			else if (!string.IsNullOrWhiteSpace(unit))
				normalized.Add(unit.Trim().ToLowerInvariant());
		}
		normalized.Sort(StringComparer.Ordinal);
		return string.Join(',', normalized);
	}

	private async Task<ModInformationResult<object>> ProduceUnitSectionStructureAndRemoveAsync(
		FactRevisionKey key,
		string gameDataDirectory,
		IReadOnlySet<string> unitIds,
		ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceUnitSectionStructureAsync(gameDataDirectory, unitIds, request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private static async Task<ModInformationResult<object>> ProduceUnitSectionStructureAsync(
		string gameDataDirectory,
		IReadOnlySet<string> unitIds,
		ModInformationRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			var resolution = await WardrobeSectionSource
				.ResolveAsync(gameDataDirectory, unitIds, archiveHint: null, cancellationToken)
				.ConfigureAwait(false);
			var issues = new List<CoreIssue>();
			if (resolution.MissingUnitIds.Count > 0)
			{
				// 缺 unit 必须是**可见的诊断**，不能静默少几行——用户看到的是"树少了东西"，
				// 却没有任何地方告诉他为什么。
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"UnitSectionStructureMissingUnits",
					$"有 {resolution.MissingUnitIds.Count} 个 unit 在部署的 patch 与原版数据包里都没找到：{string.Join(", ", resolution.MissingUnitIds)}",
					gameDataDirectory));
			}

			var units = resolution.Units.ToDictionary(
				unit => unit.FileId,
				UnitSectionStructureFact.From,
				StringComparer.Ordinal);
			var generation = $"{units.Count}:{UnitSectionStructureScope(unitIds)}:{string.Join('|', resolution.ScannedPatches)}";
			var facts = new UnitSectionStructureFacts(generation, units, resolution.MissingUnitIds, issues);
			return new ModInformationResult<object>(
				facts,
				ModInformationStatus.Fresh,
				ModInformationKind.UnitSectionStructure,
				request.Generation ?? generation,
				issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.UnitSectionStructure, request.Generation,
				[new CoreIssue(CoreIssueSeverity.Warning, "UnitSectionStructureCanceled", "section 结构因信息中心关闭而取消。", gameDataDirectory)], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.UnitSectionStructure, request.Generation,
				[new CoreIssue(CoreIssueSeverity.Error, "UnitSectionStructureProductionFailed", exception.Message, gameDataDirectory, null, exception.ToString())], false, true);
		}
	}

	// 一批护甲条目的 unit 表。与 section 结构同一套做法：无节点、按"请求的那批条目"分作用域、
	// 用中心自己的任务账合并并发请求。来源是游戏注册文件，读一次就能服务整页。
	public async ValueTask<ModInformationResult<ArmourUnitTableFacts>> RequestArmourUnitTableAsync(
		string gameDataDirectory,
		IReadOnlySet<uint> itemIds,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentNullException.ThrowIfNull(itemIds);
		ArgumentNullException.ThrowIfNull(request);
		if (request.Kind != ModInformationKind.ArmourUnitTable)
			throw new ArgumentException("Request kind must be ArmourUnitTable.", nameof(request));
		var key = new FactRevisionKey(
			new FactKey(FactSubject.GameData($"armour-units:{ArmourUnitTableScope(itemIds)}"), request.EffectiveProperty),
			request.Generation ?? "auto");
		var entry = new Lazy<Task<ModInformationResult<object>>>(
			() => ProduceArmourUnitTableAndRemoveAsync(key, gameDataDirectory, itemIds, request),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var existing = _gameDataTasks.GetOrAdd(key, entry);
		return Unwrap<ArmourUnitTableFacts>(await AwaitEntryAsync(existing, ReferenceEquals(entry, existing), cancellationToken).ConfigureAwait(false));
	}

	/// <summary>作用域 token：排过序的护甲条目 id，保证"同一批条目"永远得到同一个作用域。</summary>
	internal static string ArmourUnitTableScope(IReadOnlySet<uint> itemIds)
	{
		ArgumentNullException.ThrowIfNull(itemIds);
		return string.Join(',', itemIds.Order().Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
	}

	private async Task<ModInformationResult<object>> ProduceArmourUnitTableAndRemoveAsync(
		FactRevisionKey key,
		string gameDataDirectory,
		IReadOnlySet<uint> itemIds,
		ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceArmourUnitTableAsync(gameDataDirectory, itemIds, request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<object>> ProduceArmourUnitTableAsync(
		string gameDataDirectory,
		IReadOnlySet<uint> itemIds,
		ModInformationRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			// 数据源是索引数据库，**不是**游戏注册表文件：真映射会改写后者，读它可能拿到
			// 被我们改过的护甲→unit 关系（替换护甲因此会换到错误的护甲上）。索引构建是
			// 唯一允许读原件/备份的地方，它把结论写进数据库，这里只查数据库。
			if (_storagePaths is null)
			{
				return new ModInformationResult<object>(null, ModInformationStatus.Unavailable, ModInformationKind.ArmourUnitTable,
					request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "ArmourUnitTableIndexUnavailable",
						"信息中心没有拿到索引数据库路径，无法读取护甲→unit 表。", gameDataDirectory)]);
			}

			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(_storagePaths, cancellationToken).ConfigureAwait(false);
			if (snapshot is null)
			{
				// 数据库里没有装备关系表：如实说"不知道"，既不要宣称"这件护甲没有 unit"，
				// 也不要退回读注册表。
				return new ModInformationResult<object>(null, ModInformationStatus.Unavailable, ModInformationKind.ArmourUnitTable,
					request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "ArmourUnitTableIndexEmpty",
						"索引数据库里没有装备关系表（护甲→unit 的数据源）。请先在管理器「设置与资产」里建立 / 重建资产索引。",
						_storagePaths.DbPath)]);
			}

			if (!snapshot.IsAvailable)
			{
				// 读不到注册文件就说"不知道"，不要宣称"这件护甲没有 unit"。
				return new ModInformationResult<object>(null, ModInformationStatus.Unavailable, ModInformationKind.ArmourUnitTable,
					request.Generation, snapshot.Issues);
			}

			var unitsByItem = itemIds.ToDictionary(
				id => id,
				id => ArmourUnitTableSource.ReadUnitIds(snapshot, [id]));
			var missing = ArmourUnitTableSource.FindMissingItems(snapshot, itemIds);
			var issues = new List<CoreIssue>(snapshot.Issues);
			if (missing.Count > 0)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"ArmourUnitTableMissingKits",
					$"这些护甲条目在游戏注册文件里没有 kit：{string.Join(", ", missing)}。",
					gameDataDirectory));
			}

			var generation = $"{snapshot.SourceFingerprint}:{ArmourUnitTableScope(itemIds)}";
			var facts = new ArmourUnitTableFacts(generation, unitsByItem, missing, issues);
			return new ModInformationResult<object>(facts, ModInformationStatus.Fresh, ModInformationKind.ArmourUnitTable, request.Generation ?? generation, issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmourUnitTable, request.Generation,
				[new CoreIssue(CoreIssueSeverity.Warning, "ArmourUnitTableCanceled", "护甲 unit 表因信息中心关闭而取消。", gameDataDirectory)], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmourUnitTable, request.Generation,
				[new CoreIssue(CoreIssueSeverity.Error, "ArmourUnitTableProductionFailed", exception.Message, gameDataDirectory, null, exception.ToString())], false, true);
		}
	}

	private async Task<ModInformationResult<object>> ProduceGameDataIndexAndRemoveAsync(FactRevisionKey key, ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceGameDataIndexAsync(request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<object>> ProduceGameDataIndexAsync(ModInformationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			var fingerprint = await _assetArchiveIndexService!.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
			// 索引还没建好时返回 Unavailable 而不是失败：这不是错误，只是"还没有索引"。
			// 直读版（DirectGameDataFactSource）此时返回 null，两者语义一致。
			if (fingerprint is null)
				return new ModInformationResult<object>(null, ModInformationStatus.Unavailable, ModInformationKind.GameDataIndex, request.Generation, []);
			var archives = await _assetArchiveIndexService.GetArchiveSummariesAsync(cancellationToken).ConfigureAwait(false);
			var facts = new GameDataIndexFacts(fingerprint, archives);
			return new ModInformationResult<object>(facts, ModInformationStatus.Fresh, ModInformationKind.GameDataIndex, request.Generation ?? fingerprint.SourceFingerprint, []);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.GameDataIndex, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "GameDataIndexCanceled", "资产索引读取因信息中心关闭而取消。")], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.GameDataIndex, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "GameDataIndexProductionFailed", exception.Message, ExceptionMessage: exception.ToString())], false, true);
		}
	}

	private async Task<ModInformationResult<object>> ProduceGameDataMappingAndRemoveAsync(FactRevisionKey key, IReadOnlySet<AssetKey> assetKeys, ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceGameDataMappingAsync(assetKeys, request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<object>> ProduceGameDataMappingAsync(IReadOnlySet<AssetKey> assetKeys, ModInformationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			var facts = await _gameDataMappingFactsService!.MapAsync(assetKeys, cancellationToken).ConfigureAwait(false);
			return new ModInformationResult<object>(facts, ModInformationStatus.Fresh, ModInformationKind.GameDataMapping, request.Generation ?? facts.MappingGeneration, facts.Issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.GameDataMapping, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "GameDataMappingCanceled", "资产映射因信息中心关闭而取消。")], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.GameDataMapping, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "GameDataMappingProductionFailed", exception.Message, ExceptionMessage: exception.ToString())], false, true);
		}
	}

	private async Task<ModInformationResult<object>> ProduceArmorCoverageAndRemoveAsync(FactRevisionKey key, string gameDataDirectory, IReadOnlySet<string> archiveIds, ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceArmorCoverageAsync(gameDataDirectory, archiveIds, request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<object>> ProduceArmorCoverageAsync(string gameDataDirectory, IReadOnlySet<string> archiveIds, ModInformationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			// 三条上游事实都通过中心自己的请求取得，于是它们与页面直接请求时共用同一本合并账：
			// 浏览器页与护甲页同时打开时，索引/映射/部署图各自只会被生产一次。
			var index = await RequestGameDataIndexAsync(
				new ModInformationRequest(ModInformationKind.GameDataIndex, request.Source, request.Generation),
				cancellationToken).ConfigureAwait(false);
			// 索引没建好时整条覆盖事实都是"未知"。这与改造前 BuildAsync 返回 null 的行为一致：
			// 拿不到索引就说"不知道"，而不是宣称"没有任何护甲被覆盖"。
			if (index.Data is null)
				return new ModInformationResult<object>(null, ModInformationStatus.Unavailable, ModInformationKind.ArmorCoverage, request.Generation, index.Issues);

			var deployed = await RequestDeployedGraphAsync(
				gameDataDirectory,
				new ModInformationRequest(ModInformationKind.DeployedGraph, request.Source, request.Generation),
				cancellationToken).ConfigureAwait(false);
			if (deployed.Data is null)
				return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmorCoverage, request.Generation, deployed.Issues, false, true);

			var winnerKeys = deployed.Data.AssetChains
				.Where(chain => chain.Winner.NodeId is not null)
				.Select(chain => chain.AssetKey)
				.ToHashSet();
			var mapping = await RequestGameDataMappingAsync(
				winnerKeys,
				new ModInformationRequest(ModInformationKind.GameDataMapping, request.Source, request.Generation),
				cancellationToken).ConfigureAwait(false);
			if (mapping.Data is null)
				return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmorCoverage, request.Generation, mapping.Issues, false, true);

			// 「已命中」要按**超过一半的 Unit 被替换**判定，所以必须把分母一起取来。
			// 取不到（索引不可用）时传 null，投影会退回"有赢家就算命中"。
			IReadOnlyDictionary<string, int>? unitTotals;
			var indexService = _assetArchiveIndexService;
			try
			{
				unitTotals = indexService is null
					? null
					: await indexService.GetUnitCountsAsync(archiveIds, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
			{
				// 分母只是"严格程度"，读不到不该让整个覆盖视图失败。
				unitTotals = null;
			}
			var facts = ArmorCoverageProjection.Project(index.Data, mapping.Data, deployed.Data, archiveIds, unitTotals);
			var issues = facts.Issues.Concat(deployed.Data.Issues).ToList();
			return new ModInformationResult<object>(facts, ModInformationStatus.Fresh, ModInformationKind.ArmorCoverage, request.Generation ?? facts.CoverageGeneration, issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmorCoverage, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "ArmorCoverageCanceled", "护甲覆盖视图因信息中心关闭而取消。", gameDataDirectory)], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.ArmorCoverage, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "ArmorCoverageProductionFailed", exception.Message, gameDataDirectory, null, exception.ToString())], false, true);
		}
	}

	private async Task<ModInformationResult<object>> ProduceDeployedGraphAndRemoveAsync(FactRevisionKey key, string gameDataDirectory, ModInformationRequest request)
	{
		var started = DateTimeOffset.UtcNow;
		RecordProductionStarted(new ModInformationProductionStarted(request.Source, request.Kind, null, request.Generation, started, key.ToString()));
		ModInformationResult<object>? completed = null;
		try
		{
			completed = await ProduceDeployedGraphAsync(gameDataDirectory, request, _shutdown.Token).ConfigureAwait(false);
			return completed;
		}
		finally
		{
			RecordDiagnostic(CreateNodeLessDiagnostic(request, key, started, completed));
			_gameDataTasks.TryRemove(key, out _);
		}
	}

	private async Task<ModInformationResult<object>> ProduceDeployedGraphAsync(string gameDataDirectory, ModInformationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			var graph = await _deployedOverrideGraphService!.BuildAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
			return new ModInformationResult<object>(graph, ModInformationStatus.Fresh, ModInformationKind.DeployedGraph, request.Generation ?? graph.DeploymentGeneration, graph.Issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.DeployedGraph, request.Generation, [new CoreIssue(CoreIssueSeverity.Warning, "DeployedGraphCanceled", "实际部署图因信息中心关闭而取消。", gameDataDirectory)], false, true);
		}
		catch (Exception exception)
		{
			return new ModInformationResult<object>(null, ModInformationStatus.Failed, ModInformationKind.DeployedGraph, request.Generation, [new CoreIssue(CoreIssueSeverity.Error, "DeployedGraphProductionFailed", exception.Message, gameDataDirectory, null, exception.ToString())], false, true);
		}
	}

	// 作用：把共用账本里的 object 结果还原成具体类型。Data 为 null 时保持 null（索引未建 / 生产失败）。
	private static ModInformationResult<T> Unwrap<T>(ModInformationResult<object> result) where T : class
		=> new(result.Data as T, result.Status, result.Kind, result.Generation, result.Issues, result.WasCoalesced, result.RefreshFailed, result.CacheHit);

	private static ModInformationResult<T> CreateUnavailable<T>(ModInformationKind kind, string code, string message, string? generation)
		=> new(default, ModInformationStatus.Unavailable, kind, generation, [new CoreIssue(CoreIssueSeverity.Warning, code, message)]);

	private static ModInformationDiagnostic CreateNodeLessDiagnostic(ModInformationRequest request, FactRevisionKey key, DateTimeOffset started, ModInformationResult<object>? result)
		=> result is null
			? new ModInformationDiagnostic(request.Source, request.Kind, null, request.Generation, started, DateTimeOffset.UtcNow, false, false, ModInformationStatus.Failed, []) { OperationKey = key.ToString() }
			: new ModInformationDiagnostic(request.Source, request.Kind, null, result.Generation, started, DateTimeOffset.UtcNow, result.CacheHit, result.WasCoalesced, result.Status, result.Issues) { OperationKey = key.ToString() };

	// 作用：把一批资产键压成一个稳定的短令牌，作为"映射作用域"的一部分。
	// 先排序再哈希，所以同一批资产无论以什么顺序传来都会落到同一个身份上——合并才有意义。
	private static string ComputeAssetKeySetToken(IReadOnlySet<AssetKey> assetKeys)
	{
		var builder = new System.Text.StringBuilder();
		foreach (var key in assetKeys.OrderBy(key => key.TypeId).ThenBy(key => key.FileId))
			builder.Append(key.TypeId).Append(':').Append(key.FileId).Append(';');
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

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
			var paths = ModNodePayloadLayout.EnumeratePayloadFiles(directory)
				.Concat(Directory.Exists(ModNodePayloadLayout.GetOverwriteDirectory(directory))
					? Directory.EnumerateFiles(ModNodePayloadLayout.GetOverwriteDirectory(directory), "*", SearchOption.TopDirectoryOnly)
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
		foreach (var path in ModNodePayloadLayout.EnumeratePayloadFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			var info = new FileInfo(path);
			var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
			builder.Append(relative.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
		}
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	// 缩略图事实的新鲜度只看源图像本身，与 Mod 目录里其它文件无关，
	// 所以这里用的是"节点级缩略图指纹"而不是 ComputeNodeGeneration（设计文档 §27.3）。
	private static string ComputeThumbnailGeneration(ModNode node, string root)
		=> ThumbnailFingerprint.OfNode(node.Id, ModIconLocator.TryResolve(Path.Combine(root, node.RelativePath)));

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
		_gameDataTasks.Clear();
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
