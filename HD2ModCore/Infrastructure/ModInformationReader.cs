using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// 作用：统一协调低层 Patch reader，并根据请求范围将结果放入流程级内存缓存。
// Purpose: Coordinates low-level Patch readers and applies explicit operation-cache policy.
public sealed class ModInformationReader : IModInformationReader, IModInformationInvalidationSource
{
	private readonly IPatchWorkspaceReader _workspaceReader;
	private readonly HD2ModAdaptation.PatchReconstruction.IPatchEntryPayloadReader _payloadReader;
	private readonly PatchUnitMeshReader _unitReader;
	private readonly ConcurrentDictionary<Guid, InMemoryModInformationOperationCache> _operationCaches = new();
	private readonly ConcurrentDictionary<Guid, InMemoryModInformationOperationCache> _sessionCaches = new();
	private readonly ConcurrentDictionary<ModInformationCacheKey, InFlightEntry> _inFlight = new();
	private readonly ConcurrentDictionary<ModNodeId, long> _nodeEpochs = new();
	private readonly CancellationTokenSource _shutdown = new();
	private bool _disposed;

	public event Action<ModNodeId>? NodeInvalidated;

	public ModInformationReader(
		IPatchWorkspaceReader? workspaceReader = null,
		HD2ModAdaptation.PatchReconstruction.IPatchEntryPayloadReader? payloadReader = null,
		PatchUnitMeshReader? unitReader = null)
	{
		_workspaceReader = workspaceReader ?? new PatchWorkspaceReader();
		_payloadReader = payloadReader ?? new HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader();
		_unitReader = unitReader ?? new PatchUnitMeshReader(_payloadReader);
	}

	public async ValueTask<ModInformationPropertyResult<T>> RequestAsync<T>(
		FactKey key,
		ModInformationReadRequest request,
		FactReadOperation<T> operation,
		CancellationToken cancellationToken = default) where T : class
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(operation);
		Validate(request, key.Kind);
		var path = FullPath(operation.RevisionPath);
		var revision = ResolveRevision(request, path);
		var cacheKey = CreateKey(key, request, revision, path, operation.ScopeSuffix);
		var nodeEpoch = CaptureNodeEpoch(request.NodeId);
		var cached = await TryGetCacheAsync<T>(cacheKey, request, revision, cancellationToken).ConfigureAwait(false);
		if (cached is not null) return cached;

		var (outcome, wasCoalesced) = await ReadCoalescedAsync(cacheKey, operation.Produce, cancellationToken).ConfigureAwait(false);
		return (await CompleteAsync(cacheKey, request, outcome.Data, key.Kind, revision, outcome.EstimatedBytes, nodeEpoch, cancellationToken, outcome.Status, outcome.Issues).ConfigureAwait(false))
			with { WasCoalesced = wasCoalesced };
	}

	public ValueTask<ModInformationPropertyResult<PatchWorkspaceIndex>> ReadPatchIndexAsync(
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return RequestAsync(
			FactKey.ForNode(request.NodeId, ModInformationPropertyKind.PatchCatalog),
			request,
			new FactReadOperation<PatchWorkspaceIndex>(
				request.SourcePath,
				async token =>
				{
					var index = await _workspaceReader.ReadIndexAsync(FullPath(request.SourcePath), token).ConfigureAwait(false);
					return new FactReadOutcome<PatchWorkspaceIndex>(index, Estimate(index));
				}),
			cancellationToken);
	}

	public ValueTask<ModInformationPropertyResult<HD2ModCore.Domain.PatchEntryPayload>> ReadPatchPayloadAsync(
		AdaptationPatchTocEntry entry,
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(request);
		var effectiveRequest = request with { Selector = MergeEntrySelector(request.EffectiveSelector, entry.AssetKey) };
		return RequestAsync(
			FactKey.ForNode(effectiveRequest.NodeId, ModInformationPropertyKind.PatchEntryPayload),
			effectiveRequest,
			new FactReadOperation<HD2ModCore.Domain.PatchEntryPayload>(
				entry.SourceFilePath,
				async token =>
				{
					var payload = ToCorePayload(await _payloadReader.ReadPayloadAsync(entry, token).ConfigureAwait(false));
					return new FactReadOutcome<HD2ModCore.Domain.PatchEntryPayload>(payload, Estimate(payload));
				}),
			cancellationToken);
	}

	public ValueTask<ModInformationPropertyResult<PatchUnitMesh>> ReadUnitAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry>? patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy,
		ModInformationReadRequest request,
		bool canonicalSource = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(request);
		var effectiveRequest = request with { Selector = MergeEntrySelector(request.EffectiveSelector, entry.AssetKey) };
		return RequestAsync(
			FactKey.ForNode(effectiveRequest.NodeId, ModInformationPropertyKind.UnitStructure),
			effectiveRequest,
			new FactReadOperation<PatchUnitMesh>(
				entry.SourceFilePath,
				async token =>
				{
					var entries = patchEntries ?? await ReadEntriesThroughIndexAsync(entry.SourceFilePath, request, token).ConfigureAwait(false);
					var unit = canonicalSource
						? await _unitReader.ReadCanonicalSourceAsync(entry, entries, dependencyPolicy, token).ConfigureAwait(false)
						: await _unitReader.ReadAsync(entry, entries, dependencyPolicy, token).ConfigureAwait(false);
					return new FactReadOutcome<PatchUnitMesh>(unit, Estimate(unit));
				},
				$"policy={dependencyPolicy};canonical={canonicalSource}"),
			cancellationToken);
	}

	public ValueTask<ModInformationPropertyResult<ModUnitStructureSummary>> ReadUnitSummaryAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry>? patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy,
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(request);
		var effectiveRequest = request with { Selector = MergeEntrySelector(request.EffectiveSelector, entry.AssetKey) };
		// The summary route intentionally does not call ReadUnitAsync: retaining a
		// complete decoded Unit in an operation cache defeats a planner's purpose.
		return RequestAsync(
			FactKey.ForNode(effectiveRequest.NodeId, ModInformationPropertyKind.UnitGeometrySummary),
			effectiveRequest,
			new FactReadOperation<ModUnitStructureSummary>(
				entry.SourceFilePath,
				async token =>
				{
					var entries = patchEntries ?? await ReadEntriesThroughIndexAsync(entry.SourceFilePath, request, token).ConfigureAwait(false);
					var unit = await _unitReader.ReadCanonicalSourceAsync(entry, entries, dependencyPolicy, token).ConfigureAwait(false);
					var summary = ModUnitStructureSummary.Create(unit) with { EstimatedPayloadBytes = EstimateDeclaredPayload(entry, entries, unit.Dependencies) };
					return new FactReadOutcome<ModUnitStructureSummary>(summary, Estimate(summary));
				},
				$"policy={dependencyPolicy}"),
			cancellationToken);
	}

	public ValueTask<ModInformationPropertyResult<ModSourceUnitFactsSnapshot>> ReadSourceUnitFactsAsync(
		PatchWorkspaceIndex index,
		ModInformationReadRequest request,
		PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(request);
		return RequestAsync(
			FactKey.ForNode(request.NodeId, ModInformationPropertyKind.UnitGeometrySummary),
			request,
			new FactReadOperation<ModSourceUnitFactsSnapshot>(
				index.SourcePatchTocPath,
				async token =>
				{
					var read = await ReadSourceUnitFactsCoreAsync(index, FullPath(index.SourcePatchTocPath), request, dependencyPolicy, token).ConfigureAwait(false);
					return new FactReadOutcome<ModSourceUnitFactsSnapshot>(
						read.Snapshot,
						Estimate(read.Snapshot),
						read.Issues.Count == 0 ? ModInformationPropertyStatus.Fresh : ModInformationPropertyStatus.Partial,
						read.Issues);
				},
				$"source-facts;policy={dependencyPolicy}"),
			cancellationToken);
	}

	public void ClearOperation(Guid operationId)
	{
		if (_operationCaches.TryRemove(operationId, out var cache))
			_ = cache.DisposeAsync();
	}

	public void InvalidateNode(ModNodeId nodeId)
	{
		if (nodeId.Value != Guid.Empty)
			_nodeEpochs.AddOrUpdate(nodeId, 1, static (_, epoch) => unchecked(epoch + 1));
		foreach (var cache in _operationCaches.Values)
			cache.RemoveNode(nodeId);
		foreach (var cache in _sessionCaches.Values)
			cache.RemoveNode(nodeId);
		foreach (var key in _inFlight.Keys.Where(key => key.BelongsTo(nodeId)).ToArray())
			_inFlight.TryRemove(key, out _);
		try { NodeInvalidated?.Invoke(nodeId); }
		catch { }
	}

	public void ClearSession(Guid sessionId)
	{
		if (_sessionCaches.TryRemove(sessionId, out var cache))
			_ = cache.DisposeAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed) return;
		_disposed = true;
		_shutdown.Cancel();
		foreach (var pair in _operationCaches)
			await pair.Value.DisposeAsync().ConfigureAwait(false);
		_operationCaches.Clear();
		foreach (var pair in _sessionCaches)
			await pair.Value.DisposeAsync().ConfigureAwait(false);
		_sessionCaches.Clear();
		_inFlight.Clear();
		_shutdown.Dispose();
	}

	private async ValueTask<(T Data, bool WasCoalesced)> ReadCoalescedAsync<T>(
		ModInformationCacheKey key,
		Func<CancellationToken, Task<T>> producer,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		var candidate = new InFlightEntry(owner => ProduceAndRemoveAsync(key, owner, producer));
		var active = _inFlight.GetOrAdd(key, candidate);
		var value = await active.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		return ((T)value, ReferenceEquals(candidate, active) is false);
	}

	private async Task<object> ProduceAndRemoveAsync<T>(
		ModInformationCacheKey key,
		InFlightEntry owner,
		Func<CancellationToken, Task<T>> producer)
	{
		try
		{
			var value = await producer(_shutdown.Token).ConfigureAwait(false);
			return value is null
				? throw new InvalidDataException("统一 Mod 信息读取器收到空属性值。")
				: value;
		}
		finally
		{
			if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
				_inFlight.TryRemove(key, out _);
		}
	}

	private sealed class InFlightEntry
	{
		private readonly Lazy<Task<object>> _task;

		public InFlightEntry(Func<InFlightEntry, Task<object>> factory)
		{
			ArgumentNullException.ThrowIfNull(factory);
			_task = new Lazy<Task<object>>(
				() => factory(this),
				LazyThreadSafetyMode.ExecutionAndPublication);
		}

		public Task<object> Task => _task.Value;
	}

	private async Task<SourceUnitFactsRead> ReadSourceUnitFactsCoreAsync(
		PatchWorkspaceIndex index,
		string sourcePath,
		ModInformationReadRequest request,
		PatchUnitDependencyPolicy dependencyPolicy,
		CancellationToken cancellationToken)
	{
		var selectedKeys = request.EffectiveSelector.SelectedAssetKeys.ToHashSet();
		var entries = index.Entries
			.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			.Where(entry => selectedKeys.Count == 0 || selectedKeys.Contains(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)))
			.OrderBy(entry => entry.AssetKey.FileId)
			.ToArray();
		var facts = new List<ModSourceUnitFacts>(entries.Length);
		var issues = new List<CoreIssue>();
		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var unit = await _unitReader.ReadCanonicalSourceAsync(entry, index.Entries, dependencyPolicy, cancellationToken).ConfigureAwait(false);
				facts.Add(CreateSourceUnitFacts(sourcePath, unit));
			}
			catch (Exception exception) when (IsExpectedUnitReadFailure(exception))
			{
				var unitKey = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
				facts.Add(new ModSourceUnitFacts(
					sourcePath,
					unitKey,
					0,
					0,
					0,
					false,
					false,
					false,
					"ReadFailed",
					Array.Empty<ModSourceUnitMeshFact>(),
					exception.Message));
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"SourceUnitReadFailed",
					$"来源 Unit 0x{unitKey.FileId:x16} 无法读取，因此未将其判定为不可见或不可用。",
					sourcePath,
					request.NodeId,
					exception.ToString()));
			}
		}

		return new SourceUnitFactsRead(new ModSourceUnitFactsSnapshot(sourcePath, facts), issues);
	}

	private async ValueTask<ModInformationPropertyResult<T>?> TryGetCacheAsync<T>(
		ModInformationCacheKey key,
		ModInformationReadRequest request,
		ModContentRevision revision,
		CancellationToken cancellationToken)
	{
		if (!request.EffectiveContext.AllowsMemoryCache) return null;
		var cache = GetMemoryCache(request.EffectiveContext);
		var cached = await cache.TryGetAsync<T>(key, cancellationToken).ConfigureAwait(false);
		return cached is null
			? null
			: new ModInformationPropertyResult<T>(cached.Data,
				new ModInformationPropertyState(key.Key.Kind, ModInformationPropertyStatus.Cached, CacheSource(request.EffectiveContext),
					revision, cached.CreatedUtc));
	}

	private async ValueTask<ModInformationPropertyResult<T>> CompleteAsync<T>(
		ModInformationCacheKey key,
		ModInformationReadRequest request,
		T data,
		ModInformationPropertyKind property,
		ModContentRevision revision,
		long estimatedBytes,
		long nodeEpoch,
		CancellationToken cancellationToken,
		ModInformationPropertyStatus status = ModInformationPropertyStatus.Fresh,
		IReadOnlyList<CoreIssue>? issues = null)
	{
		var invalidated = !IsNodeEpochCurrent(request.NodeId, nodeEpoch);
		if (request.EffectiveContext.AllowsMemoryCache && !invalidated)
		{
			var cache = GetMemoryCache(request.EffectiveContext);
			await cache.SetAsync(key, data, estimatedBytes, cancellationToken).ConfigureAwait(false);
		}
		var effectiveIssues = invalidated
			? (issues ?? Array.Empty<CoreIssue>()).Append(new CoreIssue(
				CoreIssueSeverity.Warning,
				"ReaderResultInvalidated",
				"读取在 Mod 内容失效后完成，结果没有写入会话缓存。",
				request.SourcePath,
				request.NodeId)).ToArray()
			: issues;
		return new ModInformationPropertyResult<T>(data,
			new ModInformationPropertyState(
				property,
				invalidated ? ModInformationPropertyStatus.Stale : status,
				ModInformationValueSource.Producer,
				revision,
				DateTimeOffset.UtcNow,
				effectiveIssues));
	}

	private long CaptureNodeEpoch(ModNodeId? nodeId)
	{
		if (!nodeId.HasValue || nodeId.Value.Value == Guid.Empty)
			return 0;
		return _nodeEpochs.GetOrAdd(nodeId.Value, 0);
	}

	private bool IsNodeEpochCurrent(ModNodeId? nodeId, long capturedEpoch)
	{
		if (!nodeId.HasValue || nodeId.Value.Value == Guid.Empty)
			return true;
		return _nodeEpochs.GetOrAdd(nodeId.Value, 0) == capturedEpoch;
	}

	private InMemoryModInformationOperationCache GetMemoryCache(ModInformationRequestContext context)
	{
		var capacity = context.MemoryBudgetBytes is > 0 ? context.MemoryBudgetBytes.Value : InMemoryModInformationOperationCache.DefaultCapacityBytes;
		return context.AllowsSessionCache
			? _sessionCaches.GetOrAdd(context.SessionId, _ => new InMemoryModInformationOperationCache(context.SessionId, capacity))
			: _operationCaches.GetOrAdd(context.OperationId, _ => new InMemoryModInformationOperationCache(context.OperationId, capacity));
	}

	private static void Validate(ModInformationReadRequest request, ModInformationPropertyKind expected)
	{
		ArgumentNullException.ThrowIfNull(request);
		request.Validate();
		// 该属性是否允许持久化，由 ModInformationFactRegistry 单一声明，避免"新增 kind 时忘了改这里"。
		// Whether a property may persist is declared once in ModInformationFactRegistry, so a new kind cannot silently bypass the rule.
		if (request.EffectiveContext.CacheScope == ModInformationCacheScope.Persistent
			&& ModInformationFactRegistry.Describe(expected).CacheScope < ModInformationCacheScope.Persistent)
			throw new ArgumentException($"读取器不允许把 {expected} 当作默认持久化属性。请显式指定 Operation/Session/None。", nameof(request));
	}

	private static ModInformationCacheKey CreateKey(FactKey key, ModInformationReadRequest request, ModContentRevision revision, string path, string suffix = "")
	{
		var selector = request.EffectiveSelector;
		var selectorKey = selector.ToCacheKey() + $"|path={path.ToLowerInvariant()}|{suffix}";
		return new ModInformationCacheKey(key, revision.CacheKey, request.ContentView, selectorKey);
	}

	private static ModInformationSelector MergeEntrySelector(ModInformationSelector selector, AdaptationAssetKey key)
	{
		// An explicit entry read must never share a cache key with another entry in
		// an unrestricted or multi-Unit selector. The caller already chose the
		// entry, so narrow just the AssetKey dimension while retaining other scope.
		return selector with { AssetKeys = [new CoreAssetKey(key.TypeId, key.FileId)] };
	}

	private async ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesThroughIndexAsync(
		string sourcePath,
		ModInformationReadRequest request,
		CancellationToken cancellationToken)
	{
		var indexRequest = request with { SourcePath = sourcePath };
		var index = await ReadPatchIndexAsync(indexRequest, cancellationToken).ConfigureAwait(false);
		if (index.Data is not null)
			return index.Data.Entries;

		var detail = index.State.Diagnostics.FirstOrDefault()?.Message ?? "无法读取 Patch TOC。";
		throw new InvalidDataException(detail);
	}

	private static string FullPath(string path) => Path.GetFullPath(path);

	private static ModContentRevision ResolveRevision(ModInformationReadRequest request, string path)
		=> request.Revision ?? ModContentRevision.Create(ComputeFileRevision(path));

	private static string ComputeFileRevision(string path)
	{
		var fullPath = FullPath(path);
		var details = new[] { fullPath, fullPath + ".stream", fullPath + ".gpu_resources" }
			.Select(candidate =>
			{
				var info = new FileInfo(candidate);
				return $"{Path.GetFileName(candidate)}:{(info.Exists ? info.Length : -1)}:{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
			});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', details)))).ToLowerInvariant();
	}

	private static HD2ModCore.Domain.PatchEntryPayload ToCorePayload(HD2ModAdaptation.PatchReconstruction.PatchEntryPayload payload)
		=> new(ToCoreEntry(payload.Entry), payload.TocData, payload.StreamData, payload.GpuResourceData);

	private static HD2ModCore.Domain.PatchTocEntry ToCoreEntry(AdaptationPatchTocEntry entry)
		=> new(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId), entry.SourceFilePath, entry.SourceFileName, entry.TocDataOffset, entry.StreamOffset, entry.GpuResourceOffset, entry.Unknown1, entry.Unknown2, entry.TocDataSize, entry.StreamSize, entry.GpuResourceSize, entry.Unknown3, entry.Unknown4, entry.EntryIndex);

	private static long Estimate(PatchWorkspaceIndex index)
		=> index.HeaderTemplateTocData.LongLength + index.Entries.Count * 80L;

	private static long Estimate(HD2ModCore.Domain.PatchEntryPayload payload)
		=> payload.TocData.LongLength + payload.StreamData.LongLength + payload.GpuResourceData.LongLength;

	private static long Estimate(PatchUnitMesh unit)
	{
		var payloadBytes = unit.Payload.TocData.LongLength + unit.Payload.StreamData.LongLength + unit.Payload.GpuResourceData.LongLength;
		var geometryBytes = unit.Model.RawMeshData.Sum(raw =>
			64L
			+ raw.Vertices.Count * 96L
			+ UnitGeometryFactsBuilder.CountTriangles(raw) * 12L);
		return Math.Max(1L, payloadBytes + geometryBytes);
	}

	private static long Estimate(ModUnitStructureSummary summary)
		=> 512L + summary.Meshes.Count * 160L;

	private static long Estimate(ModSourceUnitFactsSnapshot snapshot)
		=> 512L + snapshot.Units.Sum(unit => 256L + unit.Meshes.Count * 256L + (unit.ReadError?.Length ?? 0) * 2L);

	private static long EstimateDeclaredPayload(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry> entries,
		PatchUnitDependencyResolution? dependencies)
	{
		long size = entry.TocDataSize + entry.StreamSize + entry.GpuResourceSize;
		if (dependencies?.CompositeReference is not { } compositeReference || compositeReference == 0)
			return size;
		var composite = entries.FirstOrDefault(candidate => candidate.AssetKey.TypeId == PatchUnitMeshReader.CompositeUnitTypeId && candidate.AssetKey.FileId == compositeReference);
		return composite is null ? size : size + composite.TocDataSize + composite.StreamSize + composite.GpuResourceSize;
	}

	private static ModInformationValueSource CacheSource(ModInformationRequestContext context)
		=> context.AllowsSessionCache ? ModInformationValueSource.SessionCache : ModInformationValueSource.OperationCache;

	private static ModSourceUnitFacts CreateSourceUnitFacts(string sourcePath, PatchUnitMesh unit)
	{
		var geometry = UnitGeometryFactsBuilder.Analyze(unit.Model);
		var visibility = UnitSourceVisibilityClassifier.Classify(unit);
		var meshes = unit.Model.Meshes.Select(mesh =>
		{
			var fact = geometry.FindMesh(mesh.Index);
			var semantic = mesh.SemanticInfo;
			return new ModSourceUnitMeshFact(
				mesh.Index,
				mesh.MeshId,
				mesh.LodIndex,
				semantic.IsVisualMesh,
				semantic.IsCullingBody,
				fact?.Quality ?? UnitMeshGeometryQuality.Unreadable,
				fact?.VertexCount ?? 0,
				fact?.TriangleCount ?? 0,
				semantic.Name,
				semantic.Slot,
				semantic.PieceType,
				semantic.BodyType,
				semantic.Weight);
		}).ToArray();
		return new ModSourceUnitFacts(
			sourcePath,
			new CoreAssetKey(unit.Entry.AssetKey.TypeId, unit.Entry.AssetKey.FileId),
			unit.Model.Version,
			unit.Model.BonesRef,
			unit.Model.CompositeRef,
			unit.Dependencies?.IsBoneResolvedFromPatch ?? false,
			unit.Dependencies?.IsCompositeResolvedFromPatch ?? false,
			visibility.IsHidden,
			visibility.Reason,
			meshes);
	}

	private static bool IsExpectedUnitReadFailure(Exception exception)
		=> exception is IOException
			or InvalidDataException
			or EndOfStreamException
			or UnauthorizedAccessException
			or OverflowException
			or KeyNotFoundException;

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(ModInformationReader));
	}

	private sealed record SourceUnitFactsRead(
		ModSourceUnitFactsSnapshot Snapshot,
		IReadOnlyList<CoreIssue> Issues);
}
