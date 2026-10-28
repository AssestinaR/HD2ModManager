using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：把库中某个主体及其选项树导出成"文件夹结构"的 zip——**不含 manifest.json**，
// 选项就是文件夹名、选项的选项就是子文件夹，诚实翻译嵌套结构。
// Purpose: Exports a host and its Option tree as a plain folder-structure zip (no manifest.json),
// with each Option becoming a same-named folder so the nesting is translated literally.
public interface IModFolderTreeExporter
{
	ValueTask<ModFolderTreeExportResult> ExportAsync(
		ModNodeId rootNodeId,
		LibrarySnapshot snapshot,
		string destinationZipPath,
		bool includeOverwrite,
		CancellationToken cancellationToken = default);
}

/// <summary>一次导出的结果。</summary>
/// <param name="ZipPath">写出的 zip 绝对路径。</param>
/// <param name="NodeCount">写进包里的节点数（主体 + 全部选项）。</param>
/// <param name="FileCount">zip 里的文件条目数。</param>
/// <param name="Warnings">不影响成功、但用户该知道的事（例如"某节点没有任何可导出文件"）。</param>
public sealed record ModFolderTreeExportResult(
	string ZipPath,
	int NodeCount,
	int FileCount,
	IReadOnlyList<string> Warnings);
