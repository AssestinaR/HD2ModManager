namespace HD2ModCore.Domain.Manifest;

// 作用：导出用 manifest（用于跨管理器/跨机器携带），仅保存用户维护的节点元数据。
// Purpose: Export manifest for portability/interoperability with user-maintained node metadata.
public sealed record ExportManifest(
	int Version,
	string RootName,
	DateTimeOffset ExportedUtc,
	IReadOnlyList<ExportManifestNode> Nodes,
	string? Guid = null,
	string? Name = null,
	string? Description = null,
	string? IconPath = null,
	IReadOnlyList<ExportManifestOption>? Options = null);
