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
	/// <param name="onUnitCommitted">
	/// 每提交完一个导入单元就回调一次（参数是那个单元落库的全部节点 id）。
	/// <para>
	/// <b>为什么需要它。</b>一次导入可能由多个单元组成（包本身 + 包里的压缩包），提交是逐个
	/// 进行的。第二个单元失败或用户取消时，第一个单元**已经在库里了**，而调用方拿不到任何
	/// 结果——如果它把「这次导入失败了」理解成「什么都没发生」，下一轮就会把第一个单元再导入
	/// 一遍，库里出现两份。有了这个回调，调用方能在失败路径上回滚已经提交的部分，
	/// 让「一次导入要么全进、要么全不进」真正成立。
	/// </para>
	/// </param>
	ValueTask<ImportResult> ImportLibraryResidentFolderAsync(
		string folderPath,
		bool preferHardLinks,
		Action<IReadOnlyList<ModNodeId>>? onUnitCommitted = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// 删掉一次未完成的导入已经落库的节点（连同目录与派生缓存），用于失败/取消后的回滚。
	/// <para>
	/// 与「删除 Mod」不同：这里不登记待删墓碑、不参与对账排除集——这些节点刚刚被创建，
	/// 语义上是「这次导入没发生」，而不是「用户要删掉一个 Mod」。
	/// </para>
	/// </summary>
	ValueTask RollbackImportAsync(
		IReadOnlyCollection<ModNodeId> nodeIds,
		CancellationToken cancellationToken = default);
}
