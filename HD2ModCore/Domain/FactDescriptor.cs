namespace HD2ModCore.Domain;

// 作用：标识一条事实由谁生产，便于诊断与后续阶段的分层归位。
// Purpose: Identifies who produces a fact, for diagnostics and later layer assignment.
public enum FactProducerKind
{
	// 直接来自来源文件或来源状态，本身就是真相。
	Source,
	PatchCatalogReader,
	PatchEntryPayloadReader,
	UnitMeshReader,
	AssetInventoryService,
	ReferenceGraphService,
	UnitVersionService,
	MaintenanceAnalysisService,
	ThumbnailService,
	GameDataMappingService,
	// 游戏数据侧的输入生产者：SQLite 资产索引与社区元数据目录。
	// The game-data input producers: the SQLite asset index and the community metadata catalog.
	GameDataIndexService,
	AssetMetadataCatalogReader,
	// 部署侧的生产者：读 Data 目录的 patch 文件与激活状态，得出实际部署图。
	// The deployment-side producer: reads Data patch files and activation state to derive the actual graph.
	DeployedOverrideGraphService,
	// 由其它事实组合得出。
	Composite,
}

// 作用：一条事实的静态声明：主体种类、允许的最长缓存生命周期、生产者，以及静态上游依赖。
// Purpose: Static declaration of a fact: subject kind, longest allowed cache lifetime, producer, and static upstream dependencies.
public sealed record FactDescriptor(
	FactSubjectKind SubjectKind,
	ModInformationCacheScope CacheScope,
	FactProducerKind Producer,
	IReadOnlyList<ModInformationPropertyKind>? DependsOn = null)
{
	public IReadOnlyList<ModInformationPropertyKind> Dependencies
		=> DependsOn ?? Array.Empty<ModInformationPropertyKind>();
}
