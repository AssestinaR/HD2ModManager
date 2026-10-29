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
	public void HelmetMergeSharedArmorWarning_DoesNotBlockExecution()
	{
		var armor = Part(Key(0x501), UnitMeshPartKind.Torso);
		var helmetA = Part(Key(0x601), UnitMeshPartKind.Helmet);
		var helmetB = Part(Key(0x602), UnitMeshPartKind.Helmet);
		var candidates = new[]
		{
			new HelmetToArmorMergeCandidate("patch|armor|helmet-a", "patch_0", "armor", "helmet-a", "Set", [armor], helmetA),
			new HelmetToArmorMergeCandidate("patch|armor|helmet-b", "patch_0", "armor", "helmet-b", "Set", [armor], helmetB),
		};

		var issues = HelmetToArmorMergeService.ValidateSelectedCandidates(candidates, HelmetToArmorMergeOutputMode.FirstHelmetOnly);

		var issue = Assert.Single(issues);
		Assert.Equal(CoreIssueSeverity.Warning, issue.Severity);
		Assert.Equal("HelmetArmorSharedTargetFirstHelmet", issue.Code);
		Assert.False(HelmetToArmorMergeService.HasBlockingIssues(issues));
	}

	[Fact]
	public void HelmetMergeCandidates_ReusesOnlyHelmetWhenArmorCountIsGreater()
	{
		var patchPath = Path.Combine(Path.GetTempPath(), "helmet-count-shortage-" + Guid.NewGuid().ToString("N") + ".patch_0");
		var armorA = Key(0x101);
		var armorB = Key(0x102);
		var helmet = Key(0x201);
		var facts = CreateHelmetFacts(patchPath,
			Armor("Set", "armor-a", armorA),
			Armor("Set", "armor-b", armorB),
			Helmet("Set", "helmet-a", helmet));
		var issues = new List<CoreIssue>();

		var candidates = HelmetToArmorMergeService.BuildCandidates(facts, issues);

		Assert.Equal(2, candidates.Count);
		Assert.All(candidates, candidate => Assert.Equal("helmet-a", candidate.HelmetArchiveId));
		Assert.Contains(issues, issue => issue.Code == "HelmetFallbackReused");
	}

	[Fact]
	public void HelmetMergeCandidates_IgnoresExtraHelmetArchives()
	{
		var patchPath = Path.Combine(Path.GetTempPath(), "helmet-count-extra-" + Guid.NewGuid().ToString("N") + ".patch_0");
		var armor = Key(0x301);
		var helmetA = Key(0x401);
		var helmetB = Key(0x402);
		var facts = CreateHelmetFacts(patchPath,
			Armor("Set", "armor-a", armor),
			Helmet("Set", "helmet-a", helmetA),
			Helmet("Set", "helmet-b", helmetB));
		var issues = new List<CoreIssue>();

		var candidates = HelmetToArmorMergeService.BuildCandidates(facts, issues);

		var candidate = Assert.Single(candidates);
		Assert.Equal("helmet-a", candidate.HelmetArchiveId);
		Assert.Contains(issues, issue => issue.Code == "HelmetExtraIgnored");
	}

	private static ModEquipmentSourceFacts CreateHelmetFacts(
		string patchPath,
		params EquipmentUnitCatalogEntry[] entries)
	{
		var nodeId = ModNodeId.New();
		var unitEntries = entries.SelectMany(entry => entry.Parts)
			.Select(part => new AdaptationPatchTocEntry(
				new AdaptationAssetKey(part.UnitAssetKey.TypeId, part.UnitAssetKey.FileId),
				patchPath,
				Path.GetFileName(patchPath)))
			.ToArray();
		return new ModEquipmentSourceFacts(
			nodeId,
			"test",
			[patchPath],
			entries,
			entries,
			new Dictionary<string, IReadOnlyList<AdaptationPatchTocEntry>>(StringComparer.OrdinalIgnoreCase)
			{
				[patchPath] = unitEntries
			},
			[],
			[]);
	}

	private static EquipmentUnitCatalogEntry Armor(string displayName, string archiveId, CoreAssetKey unit)
		=> new(archiveId, "Armor", displayName, [Part(unit, UnitMeshPartKind.Torso)]);

	private static EquipmentUnitCatalogEntry Helmet(string displayName, string archiveId, CoreAssetKey unit)
		=> new(archiveId, "Helmet", displayName, [Part(unit, UnitMeshPartKind.Helmet)]);

	private static EquipmentUnitPart Part(CoreAssetKey unit, UnitMeshPartKind kind)
		=> new(unit, 0, 1, kind, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any, kind.ToString(), 100, [])
		{
			GeometryQuality = UnitMeshGeometryQuality.RenderableLod0,
			StoredBytes = 100,
			PieceType = kind.ToString()
		};

	private static CoreAssetKey Key(ulong fileId)
		=> new(PatchUnitMeshReader.UnitTypeId, fileId);

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
		public ValueTask<ModInformationPropertyResult<T>> RequestAsync<T>(FactKey key, ModInformationReadRequest request, FactReadOperation<T> operation, CancellationToken cancellationToken = default) where T : class
			=> ValueTask.FromException<ModInformationPropertyResult<T>>(new NotSupportedException());

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
		public ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesReadOnlyAsync(IReadOnlySet<CoreAssetKey>? unitAssetKeys = null, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(entries);
		public ValueTask<CrossArmorTransferPlan> CreatePlanAsync(IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates, IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates, string? selectedSourceArchiveId, UnitMeshBodyVariant? selectedSourceBodyVariant, CrossArmorBodyVariantPreference bodyVariantPreference, CrossArmorLayerPreference layerPreference, IReadOnlyCollection<string> selectedTargetArchiveIds, IReadOnlyList<CrossArmorManualMapping>? manualMappings = null, IReadOnlyList<CrossArmorManualSuppression>? manualSuppressions = null, bool manualMode = false, IReadOnlyCollection<string>? additionalSourceArchiveIds = null, bool writePlannerDebugLog = true, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
