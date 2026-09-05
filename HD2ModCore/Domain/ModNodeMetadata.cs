namespace HD2ModCore.Domain;

// 作用：对象节点元数据（用于人类辨识、筛选、展示）。
// Purpose: Object node metadata (for human identification, filtering and display).
public sealed record ModNodeMetadata(
	string Name,
	string? Notes,
	DateTimeOffset CreatedUtc,
	DateTimeOffset? ModifiedUtc,
	ModNodeKind Kind = ModNodeKind.Standard,
	IReadOnlyList<string>? HostModGuids = null,
	string? SourcePackageGuid = null,
	string? SourcePackagePath = null,
	int? OptionOrder = null,
	bool HelmetToArmorMerged = false,
	bool SuppressDefaultDirectOptionActivation = false,
	// A logical container has a durable library directory but no deployable Patch
	// payload of its own. It is allowed to act as a structural host for Options.
	bool IsLogicalContainer = false);
