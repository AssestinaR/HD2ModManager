using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：以当前库快照为准回收派生索引里属于已删除节点的残留。派生索引全部是可重建的缓存，
// 所以"库中不存在的节点"在这里就是"该删的东西"，不需要额外判断内容是否仍然有用。
// Purpose: Reclaims derived-index residue that belongs to nodes no longer present in the library.
// Every one of these indexes is a rebuildable cache, so "not in the library" is a sufficient
// deletion criterion — there is no content that would still be useful.
public interface IModIndexGarbageCollector
{
	ValueTask<ModIndexGarbageCollectionResult> CollectAsync(
		IReadOnlyCollection<ModNodeId> liveNodeIds,
		CancellationToken cancellationToken = default);
}
