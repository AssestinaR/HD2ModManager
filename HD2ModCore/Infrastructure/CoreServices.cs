using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using System.Net.Http;

namespace HD2ModCore.Infrastructure;

// 作用：提供 Core 服务实现的简单工厂方法，便于上层快速组装。
// Purpose: Simple factory helpers for core service implementations.
public static class CoreServices
{
	public static IPatchFileNameParser CreatePatchFileNameParser() => new PatchFileNameParser();
	public static IPatchTocFileCollector CreatePatchTocFileCollector() => new PatchTocFileCollector();
	public static IPatchFileIndexBuilder CreatePatchFileIndexBuilder()
		=> new PatchFileIndexBuilder(CreatePatchFileNameParser());
	public static IModFileFactsProducer CreateModFileFactsProducer()
		=> new ModFileFactsProducer(CreatePatchFileIndexBuilder());
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationReader；此无读取器便捷工厂仅保留给测试和隔离场景。")]
	public static IModInformationCenter CreateModInformationCenter(StoragePaths paths)
	{
		var reader = CreateModInformationReader();
		return CreateModInformationCenter(paths, reader, reader);
	}

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
			ownedInformationReader);
	}
	public static IModInformationReader CreateModInformationReader()
		=> new ModInformationReader();
	[Obsolete("迁移状态：请传入共享 IModInformationReader，避免来源 Unit 读取脱离应用级缓存。")]
	public static IModEquipmentSourceFactsReader CreateModEquipmentSourceFactsReader(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IEquipmentUnitCatalogService? equipmentCatalog = null)
		=> CreateModEquipmentSourceFactsReader(paths, informationCenter, CreateModInformationReader(), equipmentCatalog);
	public static IModEquipmentSourceFactsReader CreateModEquipmentSourceFactsReader(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader reader,
		IEquipmentUnitCatalogService? equipmentCatalog = null)
		=> new ModEquipmentSourceFactsReader(
			informationCenter,
			reader ?? throw new ArgumentNullException(nameof(reader)),
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
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 或 IPatchGroupAnalysisProvider 的重载。")]
	public static IAdvancedUnitAnalysisProducer CreateAdvancedUnitAnalysisProducer(StoragePaths paths)
		=> new AdvancedUnitAnalysisProducer(new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), HD2ModAdaptation.Analysis.PatchAnalysisDepth.Full));
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
	public static IPatchTocScanner CreatePatchTocScanner() => new PatchTocScanner();
 	public static IPatchEntryPayloadReader CreatePatchEntryPayloadReader()
		=> new PatchEntryPayloadReader();
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IModSameKeyReconstructionService CreateModSameKeyReconstructionService(StoragePaths paths)
	{
		var reader = CreateModInformationReader();
		return CreateModSameKeyReconstructionService(paths, CreateModInformationCenter(paths, reader), reader);
	}
	[Obsolete("迁移状态：请传入共享 IModInformationReader，避免 Canonical 来源读取脱离应用级缓存。")]
	public static IModSameKeyReconstructionService CreateModSameKeyReconstructionService(
		StoragePaths paths,
		IModInformationCenter informationCenter)
		=> CreateModSameKeyReconstructionService(paths, informationCenter, CreateModInformationReader());
	public static IModSameKeyReconstructionService CreateModSameKeyReconstructionService(
		StoragePaths paths,
		IModInformationCenter informationCenter,
		IModInformationReader informationReader)
		=> new CanonicalSameKeyReconstructionService(
			CreatePatchFileNameParser(),
			CreateAssetArchiveIndexService(paths),
			CreateFileSystemArchiveHashesProvider(paths),
			informationReader: informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IModRepairBatchService CreateModRepairBatchService(StoragePaths paths)
	{
		var reader = CreateModInformationReader();
		return CreateModRepairBatchService(paths, CreateModInformationCenter(paths, reader), reader);
	}
	[Obsolete("迁移状态：请传入共享 IModInformationReader，避免修复后的缓存失效遗漏读取器会话。")]
	public static IModRepairBatchService CreateModRepairBatchService(
		StoragePaths paths,
		IModInformationCenter informationCenter)
		=> CreateModRepairBatchService(paths, informationCenter, CreateModInformationReader());
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
	public static IGameDataLocator CreateGameDataLocator(IGameDataSettings settings)
		=> new GameDataLocator(settings);
	public static IArchiveHashesProvider CreateFileSystemArchiveHashesProvider(StoragePaths paths)
		=> new FileSystemArchiveHashesProvider(paths);
	public static IAssetMetadataCatalogProvider CreateAssetMetadataCatalogProvider(StoragePaths paths)
		=> new FileSystemAssetMetadataCatalogProvider(paths);
	public static ModAssetSummaryProjector CreateModAssetSummaryProjector(StoragePaths paths)
		=> new ModAssetSummaryProjector(CreateGameDataMappingFactsService(paths), CreateAssetMetadataCatalogProvider(paths));
	public static IAssetMetadataSyncService CreateAssetMetadataSyncService(StoragePaths paths)
		=> new GitHubAssetMetadataSyncService(new HttpClient(), paths);
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 的重载，避免独立 Patch 分析链路。")]
	public static IPatchGroupAnalysisProvider CreatePatchGroupAnalysisProvider(StoragePaths paths)
		=> new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.DependencyGraph);
	public static IPatchGroupAnalysisProvider CreatePatchGroupAnalysisProvider(IModInformationReader informationReader)
		=> new ModInformationPatchGroupAnalysisProvider(
			informationReader ?? throw new ArgumentNullException(nameof(informationReader)),
			depth: PatchAnalysisDepth.DependencyGraph);
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 或 IPatchGroupAnalysisProvider 的重载。")]
	public static IAssetInventoryProducer CreateAssetInventoryProducer(StoragePaths paths)
		// AssetInventory is the shared lightweight directory of Patch groups and
		// AssetKeys.  Dependency edges are requested separately through the
		// information reader, so listing a Mod never needs to parse its graph.
		=> new ModContentFactsService(CreatePatchFileNameParser(), new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.Inventory));
	public static IAssetInventoryProducer CreateAssetInventoryProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new ModContentFactsService(CreatePatchFileNameParser(), analysisProvider);
	public static IAssetInventoryProducer CreateAssetInventoryProducer(IModInformationReader informationReader)
		=> CreateAssetInventoryProducer(CreateInventoryAnalysisProvider(informationReader));
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 或 IPatchGroupAnalysisProvider 的重载。")]
	public static IAssetInventoryProducer CreateAssetInventoryProducer()
		=> new ModContentFactsService(CreatePatchFileNameParser(), new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.Inventory));
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 的重载。")]
	public static IPatchGroupAnalysisProvider CreateInventoryAnalysisProvider()
		=> new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.Inventory);
	public static IPatchGroupAnalysisProvider CreateInventoryAnalysisProvider(IModInformationReader informationReader)
		=> new ModInformationPatchGroupAnalysisProvider(
			informationReader ?? throw new ArgumentNullException(nameof(informationReader)),
			depth: PatchAnalysisDepth.Inventory);
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 或 IPatchGroupAnalysisProvider 的重载。")]
	public static IUnitVersionInformationProducer CreateUnitVersionInformationProducer(StoragePaths paths)
		=> new UnitVersionInformationProducer(
			new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.DependencyGraph));
	public static IUnitVersionInformationProducer CreateUnitVersionInformationProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new UnitVersionInformationProducer(analysisProvider);
	public static IUnitVersionInformationProducer CreateUnitVersionInformationProducer(IModInformationReader informationReader)
		=> new UnitVersionInformationProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 或 IPatchGroupAnalysisProvider 的重载。")]
	public static IReferenceGraphProducer CreateReferenceGraphProducer(StoragePaths paths)
		=> new ReferenceGraphProducer(
			new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.DependencyGraph));
	public static IReferenceGraphProducer CreateReferenceGraphProducer(IPatchGroupAnalysisProvider analysisProvider)
		=> new ReferenceGraphProducer(analysisProvider);
	public static IReferenceGraphProducer CreateReferenceGraphProducer(IModInformationReader informationReader)
		=> new ReferenceGraphProducer(informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	public static IReferenceGraphQueryIndex CreateReferenceGraphQueryIndex(StoragePaths paths)
		=> new SqliteModFactsStore(paths);
	public static IModAssetRoleFactsService CreateModAssetRoleFactsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ModAssetRoleFactsService(informationCenter, CreateReferenceGraphQueryIndex(paths), CreateGameDataMappingFactsService(paths));
	public static IMaintenanceAnalysisProducer CreateMaintenanceAnalysisProducer(StoragePaths paths)
		=> new MaintenanceAnalysisProducer(CreateModCompatibilityAnalyzer(paths));
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IAdvancedModAnalysisService CreateAdvancedModAnalysisService(StoragePaths paths)
		=> CreateAdvancedModAnalysisService(paths, CreateModInformationCenter(paths));
	public static IAdvancedModAnalysisService CreateAdvancedModAnalysisService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new AdvancedModAnalysisService(informationCenter);
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 的重载。")]
	public static IPatchGroupAnalysisProvider CreateDependencyGraphAnalysisProvider()
		=> new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), HD2ModAdaptation.Analysis.PatchAnalysisDepth.DependencyGraph);

	public static IPatchGroupAnalysisProvider CreateDependencyGraphAnalysisProvider(IModInformationReader informationReader)
		=> new ModInformationPatchGroupAnalysisProvider(
			informationReader ?? throw new ArgumentNullException(nameof(informationReader)),
			depth: HD2ModAdaptation.Analysis.PatchAnalysisDepth.DependencyGraph);
	[Obsolete("迁移状态：请使用接收共享 IModInformationReader 的重载。")]
	public static IPatchGroupAnalysisProvider CreateFullPatchAnalysisProvider()
		=> new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), HD2ModAdaptation.Analysis.PatchAnalysisDepth.Full);
	public static IPatchGroupAnalysisProvider CreateFullPatchAnalysisProvider(IModInformationReader informationReader)
		=> new ModInformationPatchGroupAnalysisProvider(
			informationReader ?? throw new ArgumentNullException(nameof(informationReader)),
			depth: HD2ModAdaptation.Analysis.PatchAnalysisDepth.Full);
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationReader；此无读取器便捷工厂仅保留给测试和隔离场景。")]
	public static IPatchGraphDiagnosticsService CreatePatchGraphDiagnosticsService()
		=> new PatchGraphDiagnosticsService(
			new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.DependencyGraph),
			new AdaptationPatchGroupAnalysisProvider(CreatePatchFileNameParser(), new PatchGroupAnalyzer(), PatchAnalysisDepth.Full));
	public static IPatchGraphDiagnosticsService CreatePatchGraphDiagnosticsService(IModInformationReader informationReader)
		=> new PatchGraphDiagnosticsService(informationReader);
	public static IGameDataMappingFactsService CreateGameDataMappingFactsService(StoragePaths paths)
		=> new GameDataMappingFactsService(CreateAssetArchiveIndexService(paths), CreateAssetMetadataCatalogProvider(paths), paths);
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IProfileOverrideGraphService CreateProfileOverrideGraphService(StoragePaths paths)
		=> CreateProfileOverrideGraphService(paths, CreateModInformationCenter(paths));
	public static IProfileOverrideGraphService CreateProfileOverrideGraphService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ProfileOverrideGraphService(informationCenter, CreateGameDataMappingFactsService(paths), CreateReferenceGraphQueryIndex(paths));
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IProfileMaterialDiagnosticsService CreateProfileMaterialDiagnosticsService(StoragePaths paths)
		=> CreateProfileMaterialDiagnosticsService(paths, CreateModInformationCenter(paths));
	public static IProfileMaterialDiagnosticsService CreateProfileMaterialDiagnosticsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new ProfileMaterialDiagnosticsService(informationCenter, CreateGameDataMappingFactsService(paths), CreateAssetArchiveIndexService(paths), CreateReferenceGraphQueryIndex(paths));
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IMaterialDeliveryFactsService CreateMaterialDeliveryFactsService(StoragePaths paths)
		=> CreateMaterialDeliveryFactsService(paths, CreateModInformationCenter(paths));
	public static IMaterialDeliveryFactsService CreateMaterialDeliveryFactsService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new MaterialDeliveryFactsService(informationCenter, paths, CreateGameDataMappingFactsService(paths), CreateReferenceGraphQueryIndex(paths));
	public static IEquipmentUnitCatalogService CreateEquipmentUnitCatalogService(StoragePaths paths)
		=> new EquipmentUnitCatalogService(paths);
	[Obsolete("迁移状态：请传入共享 IModInformationReader，避免跨护甲来源读取脱离应用级缓存。")]
	public static CanonicalCrossArmorOrchestrator CreateCanonicalCrossArmorOrchestrator()
		=> new(informationReader: CreateModInformationReader());
	public static CanonicalCrossArmorOrchestrator CreateCanonicalCrossArmorOrchestrator(IModInformationReader informationReader)
		=> new(informationReader: informationReader ?? throw new ArgumentNullException(nameof(informationReader)));
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IAdvancedModAssetQueryService CreateAdvancedModAssetQueryService(StoragePaths paths)
		=> CreateAdvancedModAssetQueryService(paths, CreateModInformationCenter(paths));
	public static IAdvancedModAssetQueryService CreateAdvancedModAssetQueryService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new AdvancedModAssetQueryService(informationCenter, paths, CreateReferenceGraphQueryIndex(paths), CreateGameDataMappingFactsService(paths), CreateAssetArchiveIndexService(paths));
	public static IMaterialPackagingApplicationService CreateMaterialPackagingApplicationService()
		=> new MaterialPackagingApplicationService(CreatePatchFileNameParser());
	public static IMaterialPackagingApplicationService CreateMaterialPackagingApplicationService(IModInformationCenter informationCenter)
		=> new MaterialPackagingApplicationService(CreatePatchFileNameParser(), informationCenter: informationCenter);
	public static IPatchResourceRepackagingService CreatePatchResourceRepackagingService()
		=> new PatchResourceRepackagingService(CreatePatchFileNameParser());
	public static IMaterialDependencyValidator CreateMaterialDependencyValidator()
		=> new MaterialDependencyValidator(CreatePatchEntryPayloadReader(), new StingrayMaterialReferenceReader());
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static ILibraryDerivedDataService CreateLibraryDerivedDataService(StoragePaths paths)
		=> CreateLibraryDerivedDataService(paths, CreateModInformationCenter(paths));
	public static ILibraryDerivedDataService CreateLibraryDerivedDataService(StoragePaths paths, IModInformationCenter informationCenter)
	{
		return new LibraryDerivedDataService(informationCenter, CreateModAssetSummaryProjector(paths));
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
	public static IApplyPlanner CreateApplyPlanner()
		=> new ApplyPlanner(CreatePatchFileNameParser());
	public static ILuaDeploymentService CreateLuaDeploymentService()
		=> new LuaDeploymentService();
	public static IActivationStateStore CreateActivationStateStore()
		=> new JsonActivationStateStore();
	public static IDeployedOverrideGraphService CreateDeployedOverrideGraphService()
		=> new DeployedOverrideGraphService(CreateActivationStateStore(), CreatePatchFileNameParser());
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IModUserStatusService CreateModUserStatusService(StoragePaths paths)
	{
		var center = CreateModInformationCenter(paths);
		return new ModUserStatusService(center, CreateProfileOverrideGraphService(paths, center), CreateDeployedOverrideGraphService());
	}
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IGameDataArchiveBrowserService CreateGameDataArchiveBrowserService(StoragePaths paths)
		=> CreateGameDataArchiveBrowserService(paths, CreateModInformationCenter(paths));
	public static IGameDataArchiveBrowserService CreateGameDataArchiveBrowserService(StoragePaths paths, IModInformationCenter informationCenter)
		=> new GameDataArchiveBrowserService(CreateAssetArchiveIndexService(paths), informationCenter, CreateGameDataMappingFactsService(paths), CreateDeployedOverrideGraphService());
	public static IApplyExecutor CreateApplyExecutor()
		=> new ApplyExecutor(CreatePatchStateScanner(), CreatePatchFileNameParser(), CreateActivationStateStore());
	public static OptionActivationStore CreateOptionActivationStore(StoragePaths paths)
		=> new(Path.Combine(paths.ModsDirectory, "option-activations.json"));
	public static DeploymentCapabilityService CreateDeploymentCapabilityService() => new();
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IProfileApplyService CreateProfileApplyService(StoragePaths paths)
		=> CreateProfileApplyService(paths, CreateModInformationCenter(paths));
	public static IProfileApplyService CreateProfileApplyService(StoragePaths paths, IModInformationCenter informationCenter, OptionActivationStore? optionActivations = null)
		=> new ProfileApplyService(informationCenter, CreateApplyPlanner(), CreateApplyExecutor(), CreateDeploymentCapabilityService(), paths, optionActivations, CreateLuaDeploymentService());
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationCenter；此无中心便捷工厂仅保留给测试和隔离场景。")]
	public static IProfileDeploymentCoordinator CreateProfileDeploymentCoordinator(StoragePaths paths, Func<string?> gameDataDirectoryProvider, IDeploymentDelay? delay = null, TimeSpan? bufferDuration = null)
		=> CreateProfileDeploymentCoordinator(paths, gameDataDirectoryProvider, CreateModInformationCenter(paths), delay, bufferDuration);
	public static IProfileDeploymentCoordinator CreateProfileDeploymentCoordinator(StoragePaths paths, Func<string?> gameDataDirectoryProvider, IModInformationCenter informationCenter, IDeploymentDelay? delay = null, TimeSpan? bufferDuration = null, OptionActivationStore? optionActivations = null)
		=> new ProfileDeploymentCoordinator(
			CreateModLibraryManager(paths),
			CreateProfileApplyService(paths, informationCenter, optionActivations),
			CreateApplyExecutor(),
			paths,
			gameDataDirectoryProvider,
			delay,
			bufferDuration);
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationReader；此无读取器便捷工厂仅保留给测试和隔离场景。")]
	public static IAssetKeySetProvider CreateAssetKeySetProvider(StoragePaths paths)
		=> new AssetKeySetProvider(CreatePatchGroupAnalysisProvider(paths));
	public static IAssetKeySetProvider CreateAssetKeySetProvider(IModInformationReader informationReader)
		=> new AssetKeySetProvider(informationReader);
	[Obsolete("迁移状态：生产组合根应传入共享 IModInformationReader；此无读取器便捷工厂仅保留给测试和隔离场景。")]
	public static IConflictDetector CreateConflictDetector(StoragePaths paths)
		=> new ConflictDetector(CreateAssetKeySetProvider(paths));
	public static IConflictDetector CreateConflictDetector(IModInformationReader informationReader)
		=> new ConflictDetector(CreateAssetKeySetProvider(informationReader));
   public static IModLibraryStore CreateModLibraryStore(StoragePaths paths)
		=> new JsonModLibraryStore(paths);
	[Obsolete("Use the overload accepting IModInformationCenter so the application can share one center.")]
   public static IModLibraryImporter CreateModLibraryImporter(StoragePaths paths)
	{
		var reader = CreateModInformationReader();
		return CreateModLibraryImporter(paths, CreateModInformationCenter(paths, reader), reader);
	}
	[Obsolete("迁移状态：请传入共享 IModInformationReader，确保导入提交能失效当前读取会话。")]
	public static IModLibraryImporter CreateModLibraryImporter(
		StoragePaths paths,
		IModInformationCenter informationCenter)
		=> CreateModLibraryImporter(paths, informationCenter, CreateModInformationReader());
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
   public static IModManifestImporter CreateModManifestImporter(StoragePaths paths)
		=> new ModManifestImporter(paths, CreateObjectTreeImporter(), CreateModLibraryStore(paths));
}
