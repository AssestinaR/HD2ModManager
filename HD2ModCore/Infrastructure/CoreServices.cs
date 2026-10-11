using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using System.Net.Http;

namespace HD2ModCore.Infrastructure;

// 作用：提供 Core 服务实现的简单工厂方法，便于上层快速组装。
// Purpose: Simple factory helpers for core service implementations.
public static class CoreServices
{
	public static IPatchFileNameParser CreatePatchFileNameParser() => new PatchFileNameParser();
	public static IModLibraryResidualCleanupService CreateModLibraryResidualCleanupService()
		=> new ModLibraryResidualCleanupService(CreatePatchFileNameParser());
	public static IPatchFileIndexBuilder CreatePatchFileIndexBuilder()
		=> new PatchFileIndexBuilder(CreatePatchFileNameParser());
	public static IModFileFactsProducer CreateModFileFactsProducer()
		=> new ModFileFactsProducer(CreatePatchFileIndexBuilder());
	// 作用：组合根将同一读取器交给信息产品，避免引用图、版本和高级 Unit 分析各自重新扫描 Patch。
	// Purpose: Shares one reader across information products so graph/version/full-unit analysis do not re-scan Patch files independently.
	public static IModInformationCenter CreateModInformationCenter(StoragePaths paths, IModInformationReader informationReader)
		=> CreateModInformationCenter(paths, informationReader, ownedInformationReader: null);

	private static IModInformationCenter CreateModInformationCenter(
		StoragePaths paths,
		IModInformationReader informationReader,
		IAsyncDisposable? ownedInformationReader)
	{
		ArgumentNullException.ThrowIfNull(informationReader);
		// 启动期自检：登记表必须自洽（每个 kind 都登记、依赖边都指向已登记的 kind、依赖图无环）。
		// Startup self-check: the fact registry must be self-consistent before any production request runs.
		HD2ModCore.Domain.ModInformationFactRegistry.Validate();
		var referenceIndex = new SqliteModFactsStore(paths);
		var analysisProvider = new ModInformationPatchGroupAnalysisProvider(informationReader);
		return new ModInformationCenter(
			CreateModFileFactsProducer(),
			CreateAssetInventoryProducer(analysisProvider.ForDepth(PatchAnalysisDepth.Inventory)),
			new JsonModFileFactsCache(paths),
			CreateReferenceGraphProducer(analysisProvider.ForDepth(PatchAnalysisDepth.DependencyGraph)),
			CreateMaintenanceAnalysisProducer(paths),
			CreateUnitVersionInformationProducer(analysisProvider.ForDepth(PatchAnalysisDepth.Inventory)),
			new JsonModInformationCache(paths),
			CreateAdvancedUnitAnalysisProducer(analysisProvider.ForDepth(PatchAnalysisDepth.Full)),
			CreateModThumbnailProducer(),
			CreateModDataIndex(paths),
			referenceIndex,
			ownedInformationReader,
			// 无节点事实（设计文档 §22）的生产者：资产索引、资产映射、实际部署图。
			// 它们不挂在任何 Mod 节点上，身份是"游戏目录 + 来源指纹"，因此由中心直接持有。
			CreateAssetArchiveIndexService(paths),
			CreateGameDataMappingFactsService(paths),
			CreateDeployedOverrideGraphService());
	}
	public static IModInformationReader CreateModInformationReader()
		=> new ModInformationReader();
	public static IModEquipmentSourceFactsReader CreateModEquipmentSourceFactsReader(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader reader,
		IEquipmentUnitCatalogService? equipmentCatalog = null)
		=> new ModEquipmentSourceFactsReader(
			informationCenter,
			reader ?? throw new ArgumentNullException(nameof(reader)),
			equipmentCatalog ?? CreateEquipmentUnitCatalogService(paths));
	public static IModEquipmentSourceFactsReader CreateDirectModEquipmentSourceFactsReader(
		StoragePaths paths,
		IModInformationReader reader,
		IEquipmentUnitCatalogService? equipmentCatalog = null)
		=> new ModEquipmentSourceFactsReader(
			CreateAssetInventoryProducer(reader ?? throw new ArgumentNullException(nameof(reader))),
			reader,
			equipmentCatalog ?? CreateEquipmentUnitCatalogService(paths));
	public static IHelmetToArmorMergeService CreateHelmetToArmorMergeService(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader reader)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(informationCenter);
		ArgumentNullException.ThrowIfNull(reader);
		return new HelmetToArmorMergeService(
			CreateModEquipmentSourceFactsReader(paths, informationCenter, reader),
			reader);
	}
	public static IHelmetToArmorMergeService CreateDirectHelmetToArmorMergeService(
		StoragePaths paths,
		IModInformationReader reader)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(reader);
		return new HelmetToArmorMergeService(
			CreateDirectModEquipmentSourceFactsReader(paths, reader),
			reader);
	}
	public static IArmorNormalizationService CreateDirectArmorNormalizationService(StoragePaths paths, IModInformationReader reader)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(reader);
		return new ArmorNormalizationService(CreateDirectModEquipmentSourceFactsReader(paths, reader), reader, paths.ModsDirectory);
	}
	public static IAdvancedUnitAnalysisProducer CreateAdvancedUnitAnalysisProducer(IModInformationReader informationReader)
		=> new AdvancedUnitAnalysisProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IAdvancedUnitAnalysisProducer CreateAdvancedUnitAnalysisProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new AdvancedUnitAnalysisProducer(analysisProvider);
	public static ISourceUnitEligibilityService CreateSourceUnitEligibilityService()
		=> new SourceUnitEligibilityService();
	public static IModThumbnailProducer CreateModThumbnailProducer() => new ModThumbnailProducer();
	public static IModDataIndex CreateModDataIndex(StoragePaths paths) => new ModDataIndex(paths);
	public static IModDataIndex CreateModDataIndex() => new ModDataIndex();
	public static IPatchStateScanner CreatePatchStateScanner()
		=> new PatchStateScanner(CreatePatchFileNameParser());
 	public static IPatchEntryPayloadReader CreatePatchEntryPayloadReader()
		=> new PatchEntryPayloadReader();
	public static IModSameKeyReconstructionService CreateModSameKeyReconstructionService(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader informationReader)
		=> CreateModSameKeyReconstructionService(paths, informationReader);
	public static IModSameKeyReconstructionService CreateModSameKeyReconstructionService(
		StoragePaths paths,
		IModInformationReader informationReader)
		=> new CanonicalSameKeyReconstructionService(
			CreatePatchFileNameParser(),
			CreateAssetArchiveIndexService(paths),
			CreateFileSystemArchiveHashesProvider(paths),
			informationReader: informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IModRepairBatchService CreateModRepairBatchService(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader informationReader)
	{
		ArgumentNullException.ThrowIfNull(informationReader);
		return new ModRepairBatchService(
			paths,
			CreateModSameKeyReconstructionService(paths, informationCenter, informationReader),
			CreatePatchFileNameParser(),
			informationCenter: informationCenter,
			informationReader: informationReader);
	}
   public static IAssetArchiveIndexService CreateAssetArchiveIndexService(StoragePaths paths)
		=> new AssetArchiveIndexService(paths);
	public static IAdvancedEquipmentIndexService CreateAdvancedEquipmentIndexService(StoragePaths paths)
		=> new AdvancedEquipmentIndexService(paths);
	public static IGameDataLocator CreateGameDataLocator()
		=> new GameDataLocator();
	public static IArchiveHashesProvider CreateFileSystemArchiveHashesProvider(StoragePaths paths)
		=> new FileSystemArchiveHashesProvider(paths);
	public static IAssetMetadataCatalogProvider CreateAssetMetadataCatalogProvider(StoragePaths paths)
		=> new FileSystemAssetMetadataCatalogProvider(paths);
	public static ModAssetSummaryProjector CreateModAssetSummaryProjector(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ModAssetSummaryProjector(informationCenter, CreateAssetMetadataCatalogProvider(paths), new RuntimeArmorRegistryService(paths));
	public static IAssetMetadataSyncService CreateAssetMetadataSyncService(StoragePaths paths)
		=> new GitHubAssetMetadataSyncService(new HttpClient(), paths);
	public static IAssetInventoryProducer CreateAssetInventoryProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new ModContentFactsService(CreatePatchFileNameParser(), analysisProvider);
	public static IAssetInventoryProducer CreateAssetInventoryProducer(IModInformationReader informationReader)
		=> CreateAssetInventoryProducer(CreateInventoryAnalysisProvider(informationReader));
	public static IPatchGroupAnalysisProvider CreateInventoryAnalysisProvider(IModInformationReader informationReader)
		=> new ModInformationPatchGroupAnalysisProvider(
			informationReader ?? throw new ArgumentNullException(nameof(informationReader)),
			depth: PatchAnalysisDepth.Inventory);
	public static IUnitVersionInformationProducer CreateUnitVersionInformationProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new UnitVersionInformationProducer(analysisProvider);
	public static IUnitVersionInformationProducer CreateUnitVersionInformationProducer(IModInformationReader informationReader)
		=> new UnitVersionInformationProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IReferenceGraphProducer CreateReferenceGraphProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new ReferenceGraphProducer(analysisProvider);
	public static IReferenceGraphProducer CreateReferenceGraphProducer(IModInformationReader informationReader)
		=> new ReferenceGraphProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IReferenceGraphQueryIndex CreateReferenceGraphQueryIndex(StoragePaths paths)
		=> new SqliteModFactsStore(paths);
	public static IModAssetRoleFactsService CreateModAssetRoleFactsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ModAssetRoleFactsService(informationCenter, CreateReferenceGraphQueryIndex(paths));
	public static IMaintenanceAnalysisProducer CreateMaintenanceAnalysisProducer(StoragePaths paths)
		=> new MaintenanceAnalysisProducer(CreateModCompatibilityAnalyzer(paths));
	public static IAdvancedModAnalysisService CreateAdvancedModAnalysisService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new AdvancedModAnalysisService(informationCenter);
	public static IPatchGraphDiagnosticsService CreatePatchGraphDiagnosticsService(IModInformationReader informationReader)
		=> new PatchGraphDiagnosticsService(informationReader);
	public static IGameDataMappingFactsService CreateGameDataMappingFactsService(StoragePaths paths)
		=> new GameDataMappingFactsService(CreateAssetArchiveIndexService(paths), CreateAssetMetadataCatalogProvider(paths), paths);
	public static IProfileOverrideGraphService CreateProfileOverrideGraphService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ProfileOverrideGraphService(informationCenter, CreateReferenceGraphQueryIndex(paths));
	public static IProfileMaterialDiagnosticsService CreateProfileMaterialDiagnosticsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ProfileMaterialDiagnosticsService(informationCenter, CreateAssetArchiveIndexService(paths), CreateReferenceGraphQueryIndex(paths));
	public static IMaterialDeliveryFactsService CreateMaterialDeliveryFactsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new MaterialDeliveryFactsService(informationCenter, paths, CreateReferenceGraphQueryIndex(paths));
	public static IEquipmentUnitCatalogService CreateEquipmentUnitCatalogService(StoragePaths paths)
		=> new EquipmentUnitCatalogService(paths);
	public static RuntimeArmorRegistryService CreateRuntimeArmorRegistryService(StoragePaths paths, string? registryRoot = null)
		=> new(paths, registryRoot);
	public static CanonicalCrossArmorOrchestrator CreateCanonicalCrossArmorOrchestrator(IModInformationReader informationReader)
		=> new(informationReader: informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	/// <summary>
	/// Creates the cross-armor executor against the caller's Manager storage
	/// roots. Headless callers must use this overload so GameData-index and
	/// hidden-unit cache facts never fall back to <see cref="AppContext.BaseDirectory"/>.
	/// </summary>
	public static CanonicalCrossArmorOrchestrator CreateCanonicalCrossArmorOrchestrator(
		StoragePaths paths,
		IModInformationReader informationReader,
		HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical.ICanonicalHiddenUnitOutputCache? hiddenUnitCache = null)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(informationReader);
		return new CanonicalCrossArmorOrchestrator(
			assetIndex: CreateAssetArchiveIndexService(paths),
			archiveHashes: CreateFileSystemArchiveHashesProvider(paths),
			hiddenUnitCache: hiddenUnitCache ?? new HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical.CanonicalHiddenUnitOutputCache(Path.Combine(paths.DataDirectory, "hidden-unit-cache")),
			informationReader: informationReader);
	}
	public static IAdvancedModAssetQueryService CreateAdvancedModAssetQueryService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new AdvancedModAssetQueryService(informationCenter, paths, CreateReferenceGraphQueryIndex(paths), CreateAssetArchiveIndexService(paths));
	public static IMaterialPackagingApplicationService CreateMaterialPackagingApplicationService()
		=> new MaterialPackagingApplicationService(CreatePatchFileNameParser());
	public static IMaterialPackagingApplicationService CreateMaterialPackagingApplicationService(IModInformationCenter informationCenter)
		=> new MaterialPackagingApplicationService(CreatePatchFileNameParser(), informationCenter: informationCenter);
	public static IPatchResourceRepackagingService CreatePatchResourceRepackagingService()
		=> new PatchResourceRepackagingService(CreatePatchFileNameParser());
	public static ILibraryDerivedDataService CreateLibraryDerivedDataService(StoragePaths paths, IModInformationCenter informationCenter)
	{
		return new LibraryDerivedDataService(informationCenter, CreateModAssetSummaryProjector(paths, informationCenter));
	}
   public static IReplacementTargetDeriver CreateReplacementTargetDeriver(StoragePaths paths)
		=> new ReplacementTargetDeriver(paths, CreateAssetArchiveIndexService(paths));
   public static IModCompatibilityAnalyzer CreateModCompatibilityAnalyzer(StoragePaths paths)
		=> new ModCompatibilityAnalyzer(CreateAssetArchiveIndexService(paths));
   public static IObjectTreeImporter CreateObjectTreeImporter()
		=> new ObjectTreeImporter(CreatePatchFileNameParser());
   public static IArchiveObjectTreeImporter CreateArchiveObjectTreeImporter()
		=> new ArchiveObjectTreeImporter(CreateObjectTreeImporter());
	public static IModFileResolver CreateModFileResolver()
		=> new ModFileResolver(CreatePatchFileNameParser());
	public static DecorationPackageSnapshotService CreateDecorationPackageSnapshotService(
		IModInformationReader informationReader)
		=> new(CreateModFileResolver(), informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static DecorationHostRebuildService CreateDecorationHostRebuildService(
		StoragePaths paths,
		IModInformationReader informationReader)
		=> new(paths ?? throw new ArgumentNullException(nameof(paths)), informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static HostDerivedOverwriteService CreateHostDerivedOverwriteService(
		IDecorationHostRebuildService decorationHostRebuild,
		IHelmetToArmorMergeService helmetToArmorMerge)
		=> new(
			decorationHostRebuild ?? throw new ArgumentNullException(nameof(decorationHostRebuild)),
			helmetToArmorMerge ?? throw new ArgumentNullException(nameof(helmetToArmorMerge)),
			CreatePatchFileNameParser());
	public static IApplyPlanner CreateApplyPlanner()
		=> new ApplyPlanner(CreatePatchFileNameParser());
	public static ILuaDeploymentService CreateLuaDeploymentService(StoragePaths? paths = null)
		=> new LuaDeploymentService(
			armorRegistry: paths is null ? null : CreateRuntimeArmorRegistryService(paths));
	public static IActivationStateStore CreateActivationStateStore()
		=> new JsonActivationStateStore();
	public static IDeploymentOwnershipInspector CreateDeploymentOwnershipInspector()
		=> new DeploymentOwnershipInspector(CreateActivationStateStore(), CreatePatchFileNameParser());
	public static IDeployedOverrideGraphService CreateDeployedOverrideGraphService()
		=> new DeployedOverrideGraphService(CreateActivationStateStore(), CreatePatchFileNameParser());
	public static IGameDataArchiveBrowserService CreateGameDataArchiveBrowserService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new GameDataArchiveBrowserService(
			new CenterGameDataFactSource(informationCenter ?? throw new ArgumentNullException(nameof(informationCenter))),
			informationCenter);
	// A cache-free variant for headless inspections.  It deliberately avoids the
	// Manager information center so `gamedata browser` remains a true read-only
	// command and never writes derived Mod facts just to answer a query.
	public static IGameDataArchiveBrowserService CreateDirectGameDataArchiveBrowserService(StoragePaths paths, IModInformationReader informationReader)
		=> new GameDataArchiveBrowserService(
			new DirectGameDataFactSource(
				CreateAssetArchiveIndexService(paths),
				CreateGameDataMappingFactsService(paths),
				CreateDeployedOverrideGraphService()),
			CreateAssetInventoryProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader))));
	// 作用：为 CLI 与工具提供不依赖管理器安装、也不依赖 SQLite 索引的游戏数据直读目录服务。
	//       它只读游戏包并回答"有哪些资产、字节是什么"，因此既不需要 --manager-root，
	//       也不会写入任何派生缓存。
	// Purpose: A direct game-data catalog for the CLI and tools that needs neither a manager
	//          installation nor the SQLite index. It only reads the game packages and answers
	//          "which assets exist and what are their bytes", so it needs no --manager-root and
	//          never writes derived caches.
	public static GameDataAssetCatalogService CreateGameDataAssetCatalogService(string gameDataDirectory)
		=> new(gameDataDirectory ?? throw new ArgumentNullException(nameof(gameDataDirectory)));
	public static IApplyExecutor CreateApplyExecutor()
		=> new ApplyExecutor(CreatePatchStateScanner(), CreatePatchFileNameParser(), CreateActivationStateStore());
	public static OptionActivationStore CreateOptionActivationStore(StoragePaths paths)
		=> new(Path.Combine(paths.ModsDirectory, "option-activations.json"));
	public static DecorationActivationStore CreateDecorationActivationStore(StoragePaths paths)
		=> new(paths.DecorationActivationsPath);
	public static IOptionRelationshipWriteService CreateOptionRelationshipWriteService()
		=> new OptionRelationshipWriteService();
	public static IModRelationshipMutationService CreateModRelationshipMutationService(
		StoragePaths paths,
		OptionActivationStore? optionActivations = null)
	{
		ArgumentNullException.ThrowIfNull(paths);
		var store = CreateModLibraryStore(paths);
		return new ModRelationshipMutationService(
			paths,
			store,
			optionActivations ?? CreateOptionActivationStore(paths));
	}
	public static IProfileMutationService CreateProfileMutationService()
		=> new ProfileMutationService();
	public static ProfileStructuralMaintenanceService CreateProfileStructuralMaintenanceService()
		=> new();
	public static IModImportExecutionService CreateModImportExecutionService(StoragePaths paths)
		=> new ModImportExecutionService(paths);
	public static ILibraryWriteLeaseProvider CreateLibraryWriteLeaseProvider()
		=> new LibraryWriteLeaseProvider();
	public static IGameDataDeploymentLeaseProvider CreateGameDataDeploymentLeaseProvider()
		=> new GameDataDeploymentLeaseProvider();
	public static DeploymentCapabilityService CreateDeploymentCapabilityService() => new();
	public static IProfileApplyService CreateProfileApplyService(StoragePaths paths, IModInformationCenter informationCenter, OptionActivationStore? optionActivations = null)
		=> new ProfileApplyService(informationCenter, CreateApplyPlanner(), CreateApplyExecutor(), CreateDeploymentCapabilityService(), paths, optionActivations, CreateLuaDeploymentService(paths));
	public static IProfileDeploymentCoordinator CreateProfileDeploymentCoordinator(StoragePaths paths, Func<string?> gameDataDirectoryProvider, IModInformationCenter informationCenter, IDeploymentDelay? delay = null, TimeSpan? bufferDuration = null, OptionActivationStore? optionActivations = null, Func<LuaDeploymentPermission>? luaPermissionProvider = null)
		=> new ProfileDeploymentCoordinator(
			CreateModLibraryManager(paths),
			CreateProfileApplyService(paths, informationCenter, optionActivations),
			CreateApplyExecutor(),
			paths,
			gameDataDirectoryProvider,
			delay,
			bufferDuration,
			luaPermissionProvider);
	public static IAssetKeySetProvider CreateAssetKeySetProvider(IModInformationReader informationReader)
		=> new AssetKeySetProvider(informationReader);
	public static IConflictDetector CreateConflictDetector(IModInformationReader informationReader)
		=> new ConflictDetector(CreateAssetKeySetProvider(informationReader));
	public static IModLibraryStore CreateModLibraryStore(StoragePaths paths)
		=> new JsonModLibraryStore(paths);
	public static IModLibraryImporter CreateModLibraryImporter(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader informationReader)
		=> new ModLibraryImporter(
			paths,
			CreateObjectTreeImporter(),
			CreateArchiveObjectTreeImporter(),
			CreateModLibraryStore(paths),
			informationCenter: informationCenter,
			informationReader: informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IModImportPlanner CreateModImportPlanner()
		=> new ModImportPlanner(CreateObjectTreeImporter(), CreateArchiveObjectTreeImporter());
   public static IModLibraryManager CreateModLibraryManager(StoragePaths paths)
		=> new ModLibraryManager(paths, CreateModLibraryStore(paths));
	public static IModLibrarySynchronizer CreateModLibrarySynchronizer()
		=> new ModLibrarySynchronizer(CreatePatchFileNameParser());
   public static IModExporter CreateModExporter(StoragePaths paths)
		=> new ModExporter(paths);
}
