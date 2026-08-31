namespace HD2ModCore.Domain.Manifest;

// 作用：导出 manifest 中的递归选项描述，兼容管理器的标准 manifest 选项树。
// Purpose: Recursive option description in an exported manifest, compatible with the manager's standard manifest option tree.
public sealed record ExportManifestOption(
	string? Name,
	string? Description,
	string? Image,
	IReadOnlyList<string> Include,
	IReadOnlyList<ExportManifestOption> SubOptions);
