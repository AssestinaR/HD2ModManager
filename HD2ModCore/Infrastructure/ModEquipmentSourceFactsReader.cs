using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：为替换护甲、装饰计划等来源选择流程组合统一 Mod 信息与真实 Unit 几何。
// Purpose: Composes unified Mod information with real Unit geometry for cross-armor and decoration source selection.
public sealed class ModEquipmentSourceFactsReader : IModEquipmentSourceFactsReader
{
	private readonly IModInformationCenter? informationCenter;
	private readonly IAssetInventoryProducer? directInventoryProducer;
	private readonly IModInformationReader reader;
	private readonly IEquipmentUnitCatalogService equipmentCatalog;
	private readonly bool readEquipmentCatalogOnly;

	public ModEquipmentSourceFactsReader(
		IModInformationCenter informationCenter,
		IModInformationReader reader,
		IEquipmentUnitCatalogService equipmentCatalog)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
		this.equipmentCatalog = equipmentCatalog ?? throw new ArgumentNullException(nameof(equipmentCatalog));
		readEquipmentCatalogOnly = false;
	}

	/// <summary>
	/// Creates an operation-only reader for command-line planning. It produces
	/// the shallow inventory directly instead of asking ModInformationCenter,
	/// so no persistent manager cache or relation index is updated merely by an
	/// inspect/plan request.
	/// </summary>
	public ModEquipmentSourceFactsReader(
		IAssetInventoryProducer directInventoryProducer,
		IModInformationReader reader,
		IEquipmentUnitCatalogService equipmentCatalog,
		bool readEquipmentCatalogOnly = true)
	{
		this.directInventoryProducer = directInventoryProducer ?? throw new ArgumentNullException(nameof(directInventoryProducer));
		this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
		this.equipmentCatalog = equipmentCatalog ?? throw new ArgumentNullException(nameof(equipmentCatalog));
		this.readEquipmentCatalogOnly = readEquipmentCatalogOnly;
	}

	public async ValueTask<ModEquipmentSourceFacts> ReadAsync(
		ModNode source,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default)
		=> await ReadAsync(source, modsRootDirectory, request, includeUnitGeometry: true, cancellationToken).ConfigureAwait(false);

	/// <param name="includeUnitGeometry">
	/// false = 只读"清单级"事实：来源 Patch 路径、TOC、装备目录、以及 Patch 里的全部 Unit 键，
	/// **不解析任何网格**。这一步是页面能瞬间打开的前提。
	/// true = 再逐个解析来源 Unit 的网格，判断可转移/极小化，并绑定到装备目录（旧行为）。
	/// </param>
	public async ValueTask<ModEquipmentSourceFacts> ReadAsync(
		ModNode source,
		string modsRootDirectory,
		ModInformationRequest request,
		bool includeUnitGeometry,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		ArgumentNullException.ThrowIfNull(request);

		var issues = new List<CoreIssue>();
		var inventoryRequest = request with
		{
			Kind = ModInformationKind.AssetInventory,
			Property = ModInformationPropertyKind.AssetInventory
		};
		ModContentFacts? inventoryData;
		string? inventoryGeneration;
		if (informationCenter is not null)
		{
			var inventory = await informationCenter.RequestAssetInventoryAsync(
				source,
				modsRootDirectory,
				inventoryRequest,
				cancellationToken).ConfigureAwait(false);
			issues.AddRange(inventory.Issues);
			inventoryData = inventory.Data;
			inventoryGeneration = inventory.Generation;
		}
		else
		{
			try
			{
				inventoryData = await directInventoryProducer!.GetNodeFactsAsync(source, modsRootDirectory, cancellationToken).ConfigureAwait(false);
				inventoryGeneration = inventoryData.ContentGeneration;
				issues.AddRange(inventoryData.Issues);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DirectoryNotFoundException or FileNotFoundException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"SourceAssetInventoryUnavailable",
					$"来源 Mod 的 Patch 目录尚不可用，无法读取装备来源事实：{exception.Message}",
					source.RelativePath,
					source.Id,
					exception.ToString()));
				return Empty(source.Id, null, issues);
			}
		}
		if (inventoryData is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"SourceAssetInventoryUnavailable",
				"来源 Mod 的 Patch 目录尚不可用，无法读取装备来源事实。",
				source.RelativePath,
				source.Id));
			return Empty(source.Id, inventoryGeneration, issues);
		}

		var sourcePatchPaths = inventoryData.PatchGroups
			.SelectMany(group => group.Files)
			.Where(file => file.SidecarKind == PatchSidecarKind.Base)
			.Select(file => Path.GetFullPath(file.FilePath))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (sourcePatchPaths.Length == 0)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"SourcePatchMissing",
				"来源 Mod 没有可读取的 Patch 主文件。",
				source.RelativePath,
				source.Id));
			return Empty(source.Id, inventoryGeneration ?? inventoryData.ContentGeneration, issues);
		}

		// GameData mapping is an indexed fact.  It labels Unit parts but never decides
		// whether the source's current Mesh is real or transferable.
		var targetCandidates = readEquipmentCatalogOnly
			? await equipmentCatalog.GetEntriesReadOnlyAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
			: await equipmentCatalog.GetEntriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		if (targetCandidates.Count == 0)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"EquipmentPartMappingUnavailable",
				"GameData 装备部件目录为空。",
				modsRootDirectory,
				source.Id));
		}

		var mappedUnitKeys = targetCandidates
			.SelectMany(entry => entry.Parts)
			.Select(part => part.UnitAssetKey)
			.ToHashSet();
		var preparedEntries = new Dictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>>(StringComparer.OrdinalIgnoreCase);
		var unitFacts = new List<ModSourceUnitFacts>();
		var allSourceUnitKeys = new List<AssetKey>();
		var seenSourceUnitKeys = new HashSet<AssetKey>();
		var readerContext = ToReaderContext(request.EffectiveContext);
		var revision = ModContentRevision.FromLegacyGeneration(inventoryGeneration ?? inventoryData.ContentGeneration);

		foreach (var sourcePatchPath in sourcePatchPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var indexRequest = new ModInformationReadRequest(
				sourcePatchPath,
				readerContext,
				revision,
				ContentView: ModInformationContentView.Source,
				NodeId: source.Id);
			try
			{
				var indexResult = await reader.ReadPatchIndexAsync(indexRequest, cancellationToken).ConfigureAwait(false);
				issues.AddRange(indexResult.State.Diagnostics);
				if (!indexResult.HasValue || indexResult.Data is null)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"SourcePatchIndexUnavailable",
						$"来源 Patch 无法建立 TOC 目录：{Path.GetFileName(sourcePatchPath)}。",
						sourcePatchPath,
						source.Id));
					continue;
				}

				preparedEntries[sourcePatchPath] = indexResult.Data.Entries;
				var patchUnitKeys = indexResult.Data.Entries
					.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
					.Select(entry => new AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId))
					.Distinct()
					.ToArray();
				foreach (var key in patchUnitKeys)
					if (seenSourceUnitKeys.Add(key)) allSourceUnitKeys.Add(key);
				// 清单级读取到此为止：不解析网格，页面就能立刻列出"这个 mod 带了哪些 unit"。
				if (!includeUnitGeometry) continue;
				var requestedUnitKeys = request.EffectiveSelector.SelectedAssetKeys;
				var selectedUnitKeys = requestedUnitKeys.Count != 0
					? patchUnitKeys.Where(requestedUnitKeys.Contains).ToArray()
					// Cross-armor only needs Units that can be labelled by the equipment
					// catalog. Other consumers (for example decoration planning) request
					// UnitGeometrySummary and receive every source Unit instead.
					: request.EffectiveProperty == ModInformationPropertyKind.UnitPartMapping
						? patchUnitKeys.Where(mappedUnitKeys.Contains).ToArray()
						: patchUnitKeys;
				if (selectedUnitKeys.Length == 0)
					continue;

				var factsRequest = indexRequest with
				{
					Selector = new ModInformationSelector(AssetKeys: selectedUnitKeys)
				};
				var sourceFacts = await reader.ReadSourceUnitFactsAsync(
					indexResult.Data,
					factsRequest,
					PatchUnitDependencyPolicy.RequirePatchLocalComposite,
					cancellationToken).ConfigureAwait(false);
				issues.AddRange(sourceFacts.State.Diagnostics);
				if (sourceFacts.Data is not null)
					unitFacts.AddRange(sourceFacts.Data.Units);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"SourcePatchReadFailed",
					$"来源 Patch 无法读取：{Path.GetFileName(sourcePatchPath)}。",
					sourcePatchPath,
					source.Id,
					exception.ToString()));
			}
		}

		var sourceCandidates = includeUnitGeometry
			? EquipmentUnitSourcePartBinder.Bind(targetCandidates, unitFacts)
			: Array.Empty<EquipmentUnitCatalogEntry>();
		return new ModEquipmentSourceFacts(
			source.Id,
			inventoryGeneration ?? inventoryData.ContentGeneration,
			sourcePatchPaths,
			sourceCandidates,
			targetCandidates,
			preparedEntries,
			unitFacts,
			issues)
		{
			SourceUnitKeys = allSourceUnitKeys,
		};
	}

	public void ClearOperation(Guid operationId) => reader.ClearOperation(operationId);

	private static ModEquipmentSourceFacts Empty(ModNodeId nodeId, string? generation, IReadOnlyList<CoreIssue> issues)
		=> new(
			nodeId,
			generation,
			Array.Empty<string>(),
			Array.Empty<EquipmentUnitCatalogEntry>(),
			Array.Empty<EquipmentUnitCatalogEntry>(),
			new Dictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>>(StringComparer.OrdinalIgnoreCase),
			Array.Empty<ModSourceUnitFacts>(),
			issues);

	private static ModInformationRequestContext ToReaderContext(ModInformationRequestContext context)
		=> context.CacheScope == ModInformationCacheScope.Persistent
			? context with { CacheScope = ModInformationCacheScope.Operation }
			: context;
}
