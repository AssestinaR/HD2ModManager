using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Ensures source facts preserve real current Mesh indices instead of trusting stale catalog geometry fields.
public sealed class ModEquipmentSourceFactsReaderTests
{
	[Fact]
	public async Task ReadAsync_RebindsSingleCatalogPartToCurrentRenderableMesh()
	{
		var node = new ModNode(ModNodeId.New(), "source", new ModNodeMetadata("Source", null, DateTimeOffset.UtcNow, null), [], []);
		var patchPath = Path.Combine(Path.GetTempPath(), "source-facts-" + Guid.NewGuid().ToString("N") + ".patch_0");
		var unitKey = new CoreAssetKey(PatchUnitMeshReader.UnitTypeId, 0x1234UL);
		var inventory = new ModContentFacts(
			node.Id,
			node.RelativePath,
			"generation",
			DateTimeOffset.UtcNow,
			[
				new ModPatchGroupFact(
					new ModPatchGroupId(node.Id, "abc", 0),
					0,
					[new ModPatchGroupFileFact(PatchSidecarKind.Base, patchPath, Path.GetFileName(patchPath), 1, DateTimeOffset.UtcNow)],
					new HashSet<CoreAssetKey> { unitKey },
					[])
			],
			[]);
		var sourceEntry = new AdaptationPatchTocEntry(new AdaptationAssetKey(unitKey.TypeId, unitKey.FileId), patchPath, Path.GetFileName(patchPath));
		var sourceUnit = new ModSourceUnitFacts(
			patchPath,
			unitKey,
			1,
			0,
			0,
			true,
			true,
			false,
			"VisibleMeshHasRealGeometry",
			[
				new ModSourceUnitMeshFact(3, 77, 0, true, false, UnitMeshGeometryQuality.RenderableLod0, 100, 200, "g_torso_undergarment_male", "Torso", "Undergarment", "", "")
			]);
		var catalog = new[]
		{
			new EquipmentUnitCatalogEntry(
				"armor-a",
				"Armor",
				"Armor A",
				[new EquipmentUnitPart(unitKey, 9, 1, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Stocky, "g_torso_undergarment_male", 100, [])])
		};
		var reader = new StubReader(
			new PatchWorkspaceIndex(patchPath, [sourceEntry], [1]),
			new ModSourceUnitFactsSnapshot(patchPath, [sourceUnit]));
		var service = new ModEquipmentSourceFactsReader(
			new FakeInformationCenter(inventory),
			reader,
			new StubEquipmentCatalog(catalog));

		var result = await service.ReadAsync(
			node,
			Path.GetTempPath(),
			new ModInformationRequest(
				ModInformationKind.AssetInventory,
				"Test",
				Context: ModInformationRequestContext.Create(ModInformationCacheScope.None))
			{
				Property = ModInformationPropertyKind.UnitPartMapping
			});

		var candidate = Assert.Single(result.SourceCandidates);
		var part = Assert.Single(candidate.Parts);
		Assert.Equal(3, part.MeshInfoIndex);
		Assert.Equal(77U, part.MeshId);
		Assert.Equal(100, part.VertexCount);
		Assert.Equal(200, part.TriangleCount);
		Assert.Equal(UnitMeshGeometryQuality.RenderableLod0, part.GeometryQuality);
		Assert.Single(result.Units);
		Assert.Single(result.GetPreparedEntries(patchPath));
	}

	private static ModInformationPropertyState Fresh(ModInformationPropertyKind kind)
		=> new(kind, ModInformationPropertyStatus.Fresh, ModInformationValueSource.Producer);

	private sealed class StubReader(PatchWorkspaceIndex index, ModSourceUnitFactsSnapshot sourceFacts) : IModInformationReader
	{
		public ValueTask<ModInformationPropertyResult<PatchWorkspaceIndex>> ReadPatchIndexAsync(ModInformationReadRequest request, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new ModInformationPropertyResult<PatchWorkspaceIndex>(index, Fresh(ModInformationPropertyKind.PatchCatalog)));

		public ValueTask<ModInformationPropertyResult<ModSourceUnitFactsSnapshot>> ReadSourceUnitFactsAsync(PatchWorkspaceIndex _, ModInformationReadRequest request, PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new ModInformationPropertyResult<ModSourceUnitFactsSnapshot>(sourceFacts, Fresh(ModInformationPropertyKind.UnitGeometrySummary)));

		public ValueTask<ModInformationPropertyResult<HD2ModCore.Domain.PatchEntryPayload>> ReadPatchPayloadAsync(AdaptationPatchTocEntry entry, ModInformationReadRequest request, CancellationToken cancellationToken = default)
			=> ValueTask.FromException<ModInformationPropertyResult<HD2ModCore.Domain.PatchEntryPayload>>(new NotSupportedException());

		public ValueTask<ModInformationPropertyResult<PatchUnitMesh>> ReadUnitAsync(AdaptationPatchTocEntry entry, IReadOnlyList<AdaptationPatchTocEntry>? patchEntries, PatchUnitDependencyPolicy dependencyPolicy, ModInformationReadRequest request, bool canonicalSource = false, CancellationToken cancellationToken = default)
			=> ValueTask.FromException<ModInformationPropertyResult<PatchUnitMesh>>(new NotSupportedException());

		public ValueTask<ModInformationPropertyResult<ModUnitStructureSummary>> ReadUnitSummaryAsync(AdaptationPatchTocEntry entry, IReadOnlyList<AdaptationPatchTocEntry>? patchEntries, PatchUnitDependencyPolicy dependencyPolicy, ModInformationReadRequest request, CancellationToken cancellationToken = default)
			=> ValueTask.FromException<ModInformationPropertyResult<ModUnitStructureSummary>>(new NotSupportedException());

		public void ClearOperation(Guid operationId) { }
		public void InvalidateNode(ModNodeId nodeId) { }
		public void ClearSession(Guid sessionId) { }
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class StubEquipmentCatalog(IReadOnlyList<EquipmentUnitCatalogEntry> entries) : IEquipmentUnitCatalogService
	{
		public ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesAsync(IReadOnlySet<CoreAssetKey>? unitAssetKeys = null, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(entries);
		public ValueTask<CrossArmorTransferPlan> CreatePlanAsync(IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates, IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates, string? selectedSourceArchiveId, UnitMeshBodyVariant? selectedSourceBodyVariant, CrossArmorBodyVariantPreference bodyVariantPreference, CrossArmorLayerPreference layerPreference, IReadOnlyCollection<string> selectedTargetArchiveIds, IReadOnlyList<CrossArmorManualMapping>? manualMappings = null, IReadOnlyList<CrossArmorManualSuppression>? manualSuppressions = null, bool manualMode = false, IReadOnlyCollection<string>? additionalSourceArchiveIds = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
