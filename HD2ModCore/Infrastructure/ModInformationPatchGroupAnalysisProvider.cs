using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using PatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Infrastructure;

// 作用：将 PatchGroupAnalysis 的元数据、payload 与 Unit 读取统一收口到 IModInformationReader。
// Purpose: Produces Adaptation-owned patch analyses while keeping every Patch read behind IModInformationReader.
public sealed class ModInformationPatchGroupAnalysisProvider : IPatchGroupAnalysisProvider
{
	private const string FullAnalyzerVersion = "patch-group-v6-streamless-compatibility";
	private const string InventoryAnalyzerVersion = "patch-group-v6-streamless-compatibility";
	private const string DependencyGraphAnalyzerVersion = "patch-group-v7-streamless-compatibility";

	private readonly SharedState _state;
	private readonly PatchAnalysisDepth _depth;
	private static readonly ConditionalWeakTable<IModInformationReader, SharedState> SharedStates = new();

	public ModInformationPatchGroupAnalysisProvider(
		IModInformationReader informationReader,
		IPatchFileNameParser? fileNameParser = null,
		PatchAnalysisDepth depth = PatchAnalysisDepth.Inventory,
		IUnitMaterialReferenceReader? unitMaterialReader = null,
		StingrayMaterialReferenceReader? materialReader = null,
		ModInformationRequestContext? catalogContext = null)
	{
		ArgumentNullException.ThrowIfNull(informationReader);
		var sharesDefaultReaderState = fileNameParser is null
			&& unitMaterialReader is null
			&& materialReader is null
			&& catalogContext is null;
		_state = sharesDefaultReaderState
			? SharedStates.GetValue(informationReader, CreateDefaultSharedState)
			: new SharedState(
				informationReader,
				fileNameParser ?? new PatchFileNameParser(),
				unitMaterialReader ?? new UnitMaterialReferenceReader(),
				materialReader ?? new StingrayMaterialReferenceReader(),
				catalogContext ?? ModInformationRequestContext.Create(
					ModInformationCacheScope.Session,
					operationName: "PatchGroupAnalysisCatalog",
					memoryBudgetBytes: 64L * 1024L * 1024L));
		_depth = depth;
	}

	private ModInformationPatchGroupAnalysisProvider(SharedState state, PatchAnalysisDepth depth)
	{
		_state = state;
		_depth = depth;
	}

	// 作用：返回共享同一读取器和 Session 缓存、但按指定深度工作的 provider。
	// Purpose: Returns a depth-specific provider which shares the same reader and session cache.
	public ModInformationPatchGroupAnalysisProvider ForDepth(PatchAnalysisDepth depth)
		=> depth == _depth ? this : new ModInformationPatchGroupAnalysisProvider(_state, depth);

	// 作用：供非标准读取器或测试显式清理会话分析缓存；生产读取器会在 InvalidateNode 时自动广播。
	// Purpose: Explicit invalidation hook for tests/custom readers; the production reader broadcasts this automatically.
	public void InvalidateNode(ModNodeId nodeId)
		=> _state.AnalysisCache.InvalidateNode(nodeId);

	private static SharedState CreateDefaultSharedState(IModInformationReader reader)
		=> new(
			reader,
			new PatchFileNameParser(),
			new UnitMaterialReferenceReader(),
			new StingrayMaterialReferenceReader(),
			ModInformationRequestContext.Create(
				ModInformationCacheScope.Session,
				operationName: "PatchGroupAnalysisCatalog",
				memoryBudgetBytes: 64L * 1024L * 1024L));

	public async ValueTask<IReadOnlyList<PatchGroupAnalysis>> AnalyzeNodeAsync(
		ModNode node,
		string modsRootDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);

		var nodeDirectory = Path.Combine(modsRootDirectory, node.RelativePath);
		if (!Directory.Exists(nodeDirectory))
		{
			return Array.Empty<PatchGroupAnalysis>();
		}

		// 载荷（根 + vpk2patch/）+ Overwrite 顶替：部署计划要靠这里的分析结果判断
		// "这个 patch 能不能用"。只枚举顶层的话，VPK 版 Mod 的分析结果是空的，
		// 部署会被 PatchAnalysisMissing 拦下——表现是"装进游戏什么都没变"。
		var results = new List<PatchGroupAnalysis>();
		foreach (var path in ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDirectory).Values
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var fileName = Path.GetFileName(path);
			if (!_state.FileNameParser.TryParse(fileName, out var parsed) || parsed is null || parsed.SidecarKind != PatchSidecarKind.Base)
			{
				continue;
			}

			var input = new PatchGroupInput(
				path,
				File.Exists(path + ".stream") ? path + ".stream" : null,
				File.Exists(path + ".gpu_resources") ? path + ".gpu_resources" : null);
			results.Add(await ReadOrAnalyzePatchAsync(node, input, cancellationToken).ConfigureAwait(false));
		}

		return results;
	}

	private async ValueTask<PatchGroupAnalysis> ReadOrAnalyzePatchAsync(
		ModNode node,
		PatchGroupInput input,
		CancellationToken cancellationToken)
	{
		var revision = ComputePatchRevision(input);
		var requestedKey = new AnalysisCacheKey(node.Id, Path.GetFullPath(input.PatchTocFilePath), revision, _depth);
		if (_state.AnalysisCache.TryGet(requestedKey, out var cached))
		{
			return cached;
		}

		var reusable = _state.AnalysisCache.TryGetSatisfying(
			node.Id,
			requestedKey.PatchPath,
			revision,
			_depth);
		if (reusable is not null)
		{
			return reusable;
		}

		var inFlight = _state.AnalysisCache.GetOrAddInFlight(
			requestedKey,
			() => AnalyzePatchAsync(node, input, CancellationToken.None).AsTask());
		try
		{
			return await inFlight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			if (inFlight.Task.IsCompleted)
			{
				_state.AnalysisCache.RemoveInFlight(inFlight);
			}
		}
	}

	private async ValueTask<PatchGroupAnalysis> AnalyzePatchAsync(
		ModNode node,
		PatchGroupInput input,
		CancellationToken cancellationToken)
	{
		var operationContext = ModInformationRequestContext.Create(
			ModInformationCacheScope.Operation,
			operationName: $"PatchGroupAnalysis:{_depth}",
			memoryBudgetBytes: 256L * 1024L * 1024L);
		var issues = new List<PatchAnalysisIssue>();
		var assets = new List<PatchAssetFact>();
		var references = new List<PatchAssetReference>();
		var preparedSourceUnits = new List<SourceUnitPreparation>();
		IReadOnlyList<PatchTocEntry> entries = Array.Empty<PatchTocEntry>();
		var tocPath = Path.GetFullPath(input.PatchTocFilePath);
		if (!File.Exists(tocPath))
		{
			issues.Add(new PatchAnalysisIssue("MissingToc", $"Patch TOC was not found: {tocPath}", tocPath));
			return CreateResult(input, assets, references, issues);
		}

		// Patch indexes are compact enough to reuse for the application session. Full
		// payloads and decoded Units below remain request-local and are never retained
		// by this provider after an analysis completes.
		PatchWorkspaceIndex index;
		try
		{
			var indexResult = await _state.InformationReader.ReadPatchIndexAsync(
				new ModInformationReadRequest(tocPath, _state.CatalogContext, NodeId: node.Id),
				cancellationToken).ConfigureAwait(false);
			if (indexResult.Data is null)
			{
				issues.Add(CreateUnavailableIssue("PatchIndexUnavailable", "Patch TOC index could not be read.", tocPath, indexResult.State));
				return CreateResult(input, assets, references, issues);
			}

			index = indexResult.Data;
			AppendReaderIssues(indexResult.State, issues, tocPath);
			entries = index.Entries;
			AppendEmptyStreamCompatibilityIssue(entries, issues, tocPath);
			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var typeId = entry.AssetKey.TypeId;
				assets.Add(new PatchAssetFact(
					entry.AssetKey,
					entry.SourceFilePath,
					entry.TocDataSize,
					entry.StreamSize,
					entry.GpuResourceSize,
					typeId == PatchUnitMeshReader.UnitTypeId,
					typeId == PatchUnitMeshReader.CompositeUnitTypeId,
					typeId == MaterialDependencyResolver.MaterialTypeId,
					typeId == MaterialDependencyResolver.TextureTypeId,
					LuaAssetFacts.TryIdentify(entry.AssetKey)));

				if (_depth is PatchAnalysisDepth.DependencyGraph or PatchAnalysisDepth.Full)
				{
					await ReadMaterialTextureReferencesAsync(node, entry, references, issues, operationContext, cancellationToken).ConfigureAwait(false);
				}
			}

			if (_depth == PatchAnalysisDepth.DependencyGraph)
			{
				await ReadDirectUnitMaterialReferencesAsync(node, entries, references, issues, operationContext, cancellationToken).ConfigureAwait(false);
			}
			else if (_depth == PatchAnalysisDepth.Full)
			{
				preparedSourceUnits.AddRange(await ReadUnitMaterialReferencesAsync(node, entries, references, issues, operationContext, cancellationToken).ConfigureAwait(false));
			}
		}
		catch (Exception exception) when (IsExpectedPatchReadFailure(exception))
		{
			issues.Add(new PatchAnalysisIssue("InvalidToc", exception.Message, tocPath));
		}
		finally
		{
			_state.InformationReader.ClearOperation(operationContext.OperationId);
		}

		return CreateResult(input, assets, references, issues, _depth, entries, preparedSourceUnits);
	}

	private static void AppendEmptyStreamCompatibilityIssue(
		IReadOnlyList<PatchTocEntry> entries,
		ICollection<PatchAnalysisIssue> issues,
		string tocPath)
	{
		var sentinel = PatchStreamCompatibility.GetEmptyStreamSentinel(entries);
		if (!sentinel.HasValue) return;
		issues.Add(new PatchAnalysisIssue(
			"NonStandardEmptyStreamSentinel",
			$"Patch 使用空或省略的 .stream 与统一 StreamSize 占位值 0x{sentinel.Value:x8}；已按无 Stream 资源兼容读取。",
			tocPath));
	}

	private async ValueTask ReadMaterialTextureReferencesAsync(
		ModNode node,
		PatchTocEntry entry,
		ICollection<PatchAssetReference> references,
		ICollection<PatchAnalysisIssue> issues,
		ModInformationRequestContext operationContext,
		CancellationToken cancellationToken)
	{
		if (entry.AssetKey.TypeId != MaterialDependencyResolver.MaterialTypeId || entry.TocDataSize < 136)
		{
			return;
		}

		try
		{
			var payload = await ReadPayloadAsync(node, entry, operationContext, cancellationToken).ConfigureAwait(false);
			var textureIds = _state.MaterialReader.ReadTextureIds(payload.TocData);
			for (var index = 0; index < textureIds.Count; index++)
			{
				if (textureIds[index] == 0)
				{
					continue;
				}

				references.Add(new PatchAssetReference(
					entry.AssetKey,
					new AdaptationAssetKey(MaterialDependencyResolver.TextureTypeId, textureIds[index]),
					PatchReferenceKind.MaterialTexture,
					checked((uint)(136 + textureIds.Count * 4 + index * 8)),
					ReferenceIndex: index));
			}
		}
		catch (Exception exception) when (IsExpectedPayloadReadFailure(exception))
		{
			issues.Add(new PatchAnalysisIssue("InvalidMaterialTextureReferences", exception.Message, entry.SourceFilePath, entry.AssetKey));
		}
	}

	private async ValueTask ReadDirectUnitMaterialReferencesAsync(
		ModNode node,
		IReadOnlyList<PatchTocEntry> entries,
		ICollection<PatchAssetReference> references,
		ICollection<PatchAnalysisIssue> issues,
		ModInformationRequestContext operationContext,
		CancellationToken cancellationToken)
	{
		foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			await ReadLegacyUnitMaterialReferencesAsync(node, entry, references, issues, operationContext, cancellationToken).ConfigureAwait(false);
		}
	}

	private async ValueTask<IReadOnlyList<SourceUnitPreparation>> ReadUnitMaterialReferencesAsync(
		ModNode node,
		IReadOnlyList<PatchTocEntry> entries,
		ICollection<PatchAssetReference> references,
		ICollection<PatchAnalysisIssue> issues,
		ModInformationRequestContext operationContext,
		CancellationToken cancellationToken)
	{
		var prepared = new List<SourceUnitPreparation>();
		foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var unitResult = await _state.InformationReader.ReadUnitAsync(
					entry,
					entries,
					PatchUnitDependencyPolicy.AllowExternalCompositeReference,
					CreateOperationRequest(entry.SourceFilePath, node.Id, operationContext),
					canonicalSource: false,
					cancellationToken).ConfigureAwait(false);
				if (unitResult.Data is null)
				{
					throw new InvalidDataException(CreateUnavailableIssue("UnitUnavailable", "Unit could not be decoded.", entry.SourceFilePath, unitResult.State).Message);
				}

				var unit = unitResult.Data;
				var bindings = unit.Model.Materials
					.Where(binding => binding.MaterialId != 0)
					.GroupBy(binding => binding.SectionId)
					.ToDictionary(group => group.Key, group => group.Select(binding => binding.MaterialId).Distinct().ToArray());
				foreach (var mesh in unit.Model.Meshes)
				{
					var raw = unit.Model.RawMeshData.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
					var isPlaceholder = raw is not null && raw.Vertices.Count <= 3 && raw.Triangles.Count <= 1;
					for (var sectionIndex = 0; sectionIndex < mesh.Sections.Count; sectionIndex++)
					{
						var section = mesh.Sections[sectionIndex];
						if (!bindings.TryGetValue(section.MaterialSlotId, out var materialIds) || materialIds.Length != 1)
						{
							continue;
						}

						references.Add(new PatchAssetReference(
							entry.AssetKey,
							new AdaptationAssetKey(MaterialDependencyResolver.MaterialTypeId, materialIds[0]),
							PatchReferenceKind.UnitMaterial,
							0,
							section.MaterialSlotId,
							sectionIndex,
							mesh.Index,
							isPlaceholder));
					}
				}

				prepared.Add(CreateSourceUnitPreparation(entry, unit));
			}
			catch (Exception exception) when (IsExpectedPayloadReadFailure(exception))
			{
				await ReadLegacyUnitMaterialReferencesAsync(node, entry, references, issues, operationContext, cancellationToken).ConfigureAwait(false);
				prepared.Add(new SourceUnitPreparation(entry, null, Array.Empty<SourceMeshPreparation>(), exception.Message));
			}
		}

		return prepared;
	}

	private async ValueTask ReadLegacyUnitMaterialReferencesAsync(
		ModNode node,
		PatchTocEntry entry,
		ICollection<PatchAssetReference> references,
		ICollection<PatchAnalysisIssue> issues,
		ModInformationRequestContext operationContext,
		CancellationToken cancellationToken)
	{
		try
		{
			if (entry.TocDataSize < 0x74)
			{
				return;
			}

			var payload = await ReadPayloadAsync(node, entry, operationContext, cancellationToken).ConfigureAwait(false);
			foreach (var binding in _state.UnitMaterialReader.ReadReferenceBindings(payload.TocData).Where(binding => binding.MaterialId != 0))
			{
				references.Add(new PatchAssetReference(
					entry.AssetKey,
					new AdaptationAssetKey(MaterialDependencyResolver.MaterialTypeId, binding.MaterialId),
					PatchReferenceKind.UnitMaterial,
					binding.MaterialIdPayloadRelativeOffset,
					binding.SectionId));
			}
		}
		catch (Exception exception) when (IsExpectedPayloadReadFailure(exception))
		{
			issues.Add(new PatchAnalysisIssue("InvalidUnitMaterialReferences", exception.Message, entry.SourceFilePath, entry.AssetKey));
		}
	}

	private async ValueTask<HD2ModCore.Domain.PatchEntryPayload> ReadPayloadAsync(
		ModNode node,
		PatchTocEntry entry,
		ModInformationRequestContext operationContext,
		CancellationToken cancellationToken)
	{
		var result = await _state.InformationReader.ReadPatchPayloadAsync(
			entry,
			CreateOperationRequest(entry.SourceFilePath, node.Id, operationContext),
			cancellationToken).ConfigureAwait(false);
		if (result.Data is null)
		{
			throw new InvalidDataException(CreateUnavailableIssue("PayloadUnavailable", "Patch payload could not be read.", entry.SourceFilePath, result.State).Message);
		}

		return result.Data;
	}

	private static SourceUnitPreparation CreateSourceUnitPreparation(PatchTocEntry entry, PatchUnitMesh unit)
	{
		var materialIdsBySlot = unit.Model.Materials
			.Where(binding => binding.MaterialId != 0)
			.GroupBy(binding => binding.SectionId)
			.ToDictionary(group => group.Key, group => group.Select(binding => binding.MaterialId).Distinct().OrderBy(id => id).ToArray());
		var meshes = unit.Model.Meshes.Select(mesh =>
		{
			var raw = unit.Model.RawMeshData.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
			var stream = raw is null ? null : unit.Model.Streams.FirstOrDefault(candidate => candidate.Index == raw.StreamIndex);
			var semantic = mesh.SemanticInfo;
			var isPlaceholder = raw is not null && !UnitGeometryFactsBuilder.HasRenderableGeometry(raw);
			var slots = mesh.MaterialSlotIds.Distinct().ToArray();
			var materialIds = slots.Where(materialIdsBySlot.ContainsKey).SelectMany(slot => materialIdsBySlot[slot]).Distinct().OrderBy(id => id).ToArray();
			return new SourceMeshPreparation(
				mesh.Index,
				mesh.MeshId,
				mesh.LodIndex,
				semantic.IsVisualMesh,
				raw is not null && IsSdkDefaultImportable(semantic) && !isPlaceholder,
				semantic.Name,
				semantic.BodyType,
				semantic.Slot,
				semantic.PieceType,
				raw is null ? 0U : checked((uint)raw.Vertices.Count),
				raw is null ? 0U : checked((uint)raw.Triangles.Count),
				checked((uint)mesh.Sections.Count),
				stream?.VertexStride ?? 0,
				slots,
				materialIds);
		}).ToArray();
		return new SourceUnitPreparation(
			entry,
			unit.Model.CompositeRef == 0 ? null : new AdaptationAssetKey(PatchUnitMeshReader.CompositeUnitTypeId, unit.Model.CompositeRef),
			meshes);
	}

	private static bool IsSdkDefaultImportable(UnitMeshSemanticInfo semantic)
		=> semantic.IsVisualMesh && semantic.LodIndex == 0 && !semantic.IsLod && !semantic.IsCullingBody && !semantic.IsStaticMesh;

	private static ModInformationReadRequest CreateOperationRequest(
		string sourcePath,
		ModNodeId nodeId,
		ModInformationRequestContext operationContext)
		=> new(
			sourcePath,
			operationContext,
			NodeId: nodeId);

	private static bool IsExpectedPatchReadFailure(Exception exception)
		=> exception is IOException
			or InvalidDataException
			or EndOfStreamException
			or UnauthorizedAccessException
			or OverflowException;

	private static bool IsExpectedPayloadReadFailure(Exception exception)
		=> IsExpectedPatchReadFailure(exception)
			|| exception is KeyNotFoundException;

	private static void AppendReaderIssues(
		ModInformationPropertyState state,
		ICollection<PatchAnalysisIssue> issues,
		string fallbackPath)
	{
		foreach (var issue in state.Diagnostics)
		{
			issues.Add(new PatchAnalysisIssue(issue.Code, issue.Message, issue.FilePath ?? fallbackPath));
		}
	}

	private static PatchAnalysisIssue CreateUnavailableIssue(
		string code,
		string fallbackMessage,
		string sourcePath,
		ModInformationPropertyState state)
	{
		var diagnostic = state.Diagnostics.FirstOrDefault();
		return new PatchAnalysisIssue(
			diagnostic?.Code ?? code,
			diagnostic?.Message ?? fallbackMessage,
			diagnostic?.FilePath ?? sourcePath);
	}

	private static PatchGroupAnalysis CreateResult(
		PatchGroupInput input,
		IReadOnlyList<PatchAssetFact> assets,
		IReadOnlyList<PatchAssetReference> references,
		IReadOnlyList<PatchAnalysisIssue> issues,
		PatchAnalysisDepth depth = PatchAnalysisDepth.Inventory,
		IReadOnlyList<PatchTocEntry>? entries = null,
		IReadOnlyList<SourceUnitPreparation>? sourceUnits = null)
		=> new(
			input,
			assets,
			references,
			issues,
			DateTimeOffset.UtcNow,
			depth switch
			{
				PatchAnalysisDepth.Inventory => InventoryAnalyzerVersion,
				PatchAnalysisDepth.DependencyGraph => DependencyGraphAnalyzerVersion,
				_ => FullAnalyzerVersion,
			},
			depth,
			entries,
			sourceUnits);

	private sealed class SharedState
	{
		public SharedState(
			IModInformationReader informationReader,
			IPatchFileNameParser fileNameParser,
			IUnitMaterialReferenceReader unitMaterialReader,
			StingrayMaterialReferenceReader materialReader,
			ModInformationRequestContext catalogContext)
		{
			InformationReader = informationReader;
			FileNameParser = fileNameParser;
			UnitMaterialReader = unitMaterialReader;
			MaterialReader = materialReader;
			CatalogContext = catalogContext;
			if (InformationReader is IModInformationInvalidationSource invalidationSource)
			{
				invalidationSource.NodeInvalidated += AnalysisCache.InvalidateNode;
			}
		}

		public IModInformationReader InformationReader { get; }
		public IPatchFileNameParser FileNameParser { get; }
		public IUnitMaterialReferenceReader UnitMaterialReader { get; }
		public StingrayMaterialReferenceReader MaterialReader { get; }
		public ModInformationRequestContext CatalogContext { get; }
		public PatchGroupAnalysisCache AnalysisCache { get; } = new();
	}

	private sealed record AnalysisCacheKey(
		ModNodeId NodeId,
		string PatchPath,
		string Revision,
		PatchAnalysisDepth Depth);

	// 作用：只暂存已投影的分析事实，不保存完整 Unit/Payload；Full 可供较浅深度直接消费。
	// Purpose: Retains projected analysis facts only, never raw payloads/Units; deeper facts satisfy shallower requests.
	private sealed class PatchGroupAnalysisCache
	{
		private const int Capacity = 96;
		private readonly object _gate = new();
		private readonly Dictionary<AnalysisCacheKey, CacheEntry> _entries = [];
		private readonly ConcurrentDictionary<AnalysisInFlightKey, Lazy<Task<PatchGroupAnalysis>>> _inFlight = new();
		private readonly ConcurrentDictionary<ModNodeId, long> _nodeEpochs = new();

		public PatchGroupAnalysisCache()
		{
		}

		public bool TryGet(AnalysisCacheKey key, out PatchGroupAnalysis result)
		{
			lock (_gate)
			{
				if (_entries.TryGetValue(key, out var entry))
				{
					entry.LastAccessUtc = DateTimeOffset.UtcNow;
					result = entry.Result;
					return true;
				}
			}

			result = null!;
			return false;
		}

		public PatchGroupAnalysis? TryGetSatisfying(
			ModNodeId nodeId,
			string patchPath,
			string revision,
			PatchAnalysisDepth requestedDepth)
		{
			lock (_gate)
			{
				var match = _entries
					.Where(pair => pair.Key.NodeId == nodeId
						&& string.Equals(pair.Key.PatchPath, patchPath, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(pair.Key.Revision, revision, StringComparison.Ordinal)
						&& pair.Key.Depth >= requestedDepth)
					.OrderBy(pair => pair.Key.Depth)
					.FirstOrDefault();
				if (!match.Equals(default(KeyValuePair<AnalysisCacheKey, CacheEntry>)))
				{
					match.Value.LastAccessUtc = DateTimeOffset.UtcNow;
					return match.Value.Result;
				}
			}

			return null;
		}

		public AnalysisInFlightLease GetOrAddInFlight(
			AnalysisCacheKey key,
			Func<Task<PatchGroupAnalysis>> factory)
		{
			var epoch = CurrentEpoch(key.NodeId);
			var inFlightKey = new AnalysisInFlightKey(key, epoch);
			Lazy<Task<PatchGroupAnalysis>>? candidate = null;
			candidate = new Lazy<Task<PatchGroupAnalysis>>(
				() => ProduceAndRemoveAsync(inFlightKey, key, epoch, candidate!, factory),
				LazyThreadSafetyMode.ExecutionAndPublication);
			var active = _inFlight.GetOrAdd(inFlightKey, candidate);
			return new AnalysisInFlightLease(inFlightKey, active);
		}

		public void RemoveInFlight(AnalysisInFlightLease lease)
		{
			if (_inFlight.TryGetValue(lease.Key, out var current) && ReferenceEquals(current, lease.Entry))
			{
				_inFlight.TryRemove(lease.Key, out _);
			}
		}

		public void InvalidateNode(ModNodeId nodeId)
		{
			_nodeEpochs.AddOrUpdate(nodeId, 1, static (_, current) => unchecked(current + 1));
			lock (_gate)
			{
				foreach (var key in _entries.Keys.Where(key => key.NodeId == nodeId).ToArray())
				{
					_entries.Remove(key);
				}
			}
			foreach (var key in _inFlight.Keys.Where(key => key.AnalysisKey.NodeId == nodeId).ToArray())
			{
				_inFlight.TryRemove(key, out _);
			}
		}

		private async Task<PatchGroupAnalysis> ProduceAndRemoveAsync(
			AnalysisInFlightKey inFlightKey,
			AnalysisCacheKey key,
			long epoch,
			Lazy<Task<PatchGroupAnalysis>> owner,
			Func<Task<PatchGroupAnalysis>> factory)
		{
			try
			{
				var result = await factory().ConfigureAwait(false);
				if (CurrentEpoch(key.NodeId) != epoch)
				{
					return result;
				}
				lock (_gate)
				{
					if (CurrentEpoch(key.NodeId) != epoch)
					{
						return result;
					}
					_entries[key] = new CacheEntry(result);
					while (_entries.Count > Capacity)
					{
						var oldest = _entries.OrderBy(pair => pair.Value.LastAccessUtc).First();
						_entries.Remove(oldest.Key);
					}
				}
				return result;
			}
			finally
			{
				if (_inFlight.TryGetValue(inFlightKey, out var current) && ReferenceEquals(current, owner))
				{
					_inFlight.TryRemove(inFlightKey, out _);
				}
			}
		}

		private long CurrentEpoch(ModNodeId nodeId)
			=> _nodeEpochs.GetOrAdd(nodeId, 0);

		private sealed class CacheEntry(PatchGroupAnalysis result)
		{
			public PatchGroupAnalysis Result { get; } = result;
			public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
		}

		public sealed record AnalysisInFlightLease(
			AnalysisInFlightKey Key,
			Lazy<Task<PatchGroupAnalysis>> Entry)
		{
			public Task<PatchGroupAnalysis> Task => Entry.Value;
		}
	}

	private sealed record AnalysisInFlightKey(AnalysisCacheKey AnalysisKey, long Epoch);

	private static string ComputePatchRevision(PatchGroupInput input)
	{
		var files = new[] { input.PatchTocFilePath, input.StreamFilePath, input.GpuResourcesFilePath }
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path =>
			{
				var info = new FileInfo(path!);
				return $"{Path.GetFullPath(path!)}:{(info.Exists ? info.Length : -1)}:{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
			});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', files)))).ToLowerInvariant();
	}
}
