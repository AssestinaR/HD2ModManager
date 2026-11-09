using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：将文件夹/压缩包导入并落库到程序目录的 mods/ 下，同时更新并保存 LibrarySnapshot。
// Purpose: Imports a folder/archive into the library under app-local mods/, updates and persists the LibrarySnapshot.
public interface IModLibraryImporter
{
	ValueTask<ImportResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default);
	ValueTask<ImportResult> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default);

	/// <summary>
	/// 导入一个**本来就在 Mod 库根下**的文件夹（外部包认领用）。
	/// <para>
	/// 与 <see cref="ImportFolderAsync"/> 的区别只有两点：源与目标必然同卷，所以默认走硬链接
	/// （一个包十几个选项、单个 <c>gpu_resources</c> 几百 MB，复制一遍纯属浪费）；
	/// 且调用方随后会把源目录移走，所以这里不碰源目录本身。
	/// </para>
	/// </summary>
	ValueTask<ImportResult> ImportLibraryResidentFolderAsync(
		string folderPath,
		bool preferHardLinks,
		CancellationToken cancellationToken = default);
}
