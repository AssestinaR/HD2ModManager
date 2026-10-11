namespace HD2ModCore.Domain;

// 作用：描述读取器可以按需提供的细粒度 Mod 属性，而不是把整包高级分析视为一个黑盒。
// Purpose: Describes fine-grained Mod properties that the reader can provide on demand.
public enum ModInformationPropertyKind
{
	Identity,
	ContentRevision,
	PatchCatalog,
	PatchEntryPayload,
	AssetInventory,
	UnitCatalog,
	UnitPartMapping,
	UnitGeometrySummary,
	UnitStructure,
	MaterialDependencyClosure,
	CompositeBoneDependencyClosure,
	ReferenceEdges,
	ReferenceClosure,
	GpuPayload,
	StreamPayload,
	TocPayload,
	GameDataMapping,
	Compatibility,
	DecorationDefinition,
	Maintenance,
	Thumbnail,
	// 游戏数据侧的两个输入事实（设计文档 §4 #8 / #9）。追加在末尾以保持既有取值的数值不变：
	// 这些取值会随诊断状态一起出现在内存对象里，改变数值会让"同一份日志"在两版之间对不上。
	// Appended at the end so existing numeric values are preserved.
	GameDataIndex,
	AssetMetadata,
}

// 作用：标识属性来自哪一层，便于 UI 和诊断区分缓存命中、生产和组合结果。
// Purpose: Identifies where a property value came from for UI and diagnostics.
public enum ModInformationValueSource
{
	None,
	PersistentCache,
	SessionCache,
	OperationCache,
	Producer,
	Composite,
	LegacyCache,
}

// 作用：表达“缺失、不可读”和“生产失败”的区别，避免把缺失误判成否定事实。
// Purpose: Distinguishes missing, unreadable, and failed values from a definitive negative fact.
public enum ModInformationPropertyStatus
{
	Missing,
	Fresh,
	Cached,
	Stale,
	Partial,
	Failed,
	Unavailable,
}

// 作用：描述一个细粒度属性及其来源、修订和诊断状态。
// Purpose: Describes the state, source, revision, and diagnostics of one fine-grained property.
public sealed record ModInformationPropertyState(
	ModInformationPropertyKind Kind,
	ModInformationPropertyStatus Status,
	ModInformationValueSource Source,
	ModContentRevision? Revision = null,
	DateTimeOffset? BuiltUtc = null,
	IReadOnlyList<CoreIssue>? Issues = null)
{
	public IReadOnlyList<CoreIssue> Diagnostics => Issues ?? Array.Empty<CoreIssue>();

	public bool HasValue => Status is not ModInformationPropertyStatus.Missing
		and not ModInformationPropertyStatus.Unavailable
		and not ModInformationPropertyStatus.Failed;
}

// 作用：承载统一读取器返回的一个属性值及可审计状态。
// Purpose: Carries one reader property value together with an auditable state.
public sealed record ModInformationPropertyResult<T>(
	T? Data,
	ModInformationPropertyState State,
	bool WasCoalesced = false)
{
	public bool HasValue => Data is not null && State.HasValue;
}
