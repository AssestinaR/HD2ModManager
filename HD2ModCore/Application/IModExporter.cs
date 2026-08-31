using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：将 Mod 库中的某个主体及其递归选项树导出为可重新导入的 zip（包含标准 manifest.json）。
// Purpose: Exports a host and its recursive Option tree from the Mod library as a re-importable zip with a standard manifest.json.
public interface IModExporter
{
	ValueTask<string> ExportToZipAsync(
		ModNodeId rootNodeId,
		LibrarySnapshot snapshot,
		string destinationDirectory,
		CancellationToken cancellationToken = default);
}
