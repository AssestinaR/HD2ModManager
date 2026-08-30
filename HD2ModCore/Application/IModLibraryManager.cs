using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：对库进行高级管理操作（删除导入项、更新节点元数据/标签、维护 Profile 等）。
// Purpose: Higher-level library management operations (delete imports, update node metadata/tags, maintain profiles, etc.).
public interface IModLibraryManager
{
	ValueTask<LibrarySnapshot> LoadOrCreateAsync(CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> UpsertNodeAsync(ModNode node, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> DeleteNodeAsync(ModNodeId nodeId, bool deleteStoredFiles, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> DeleteNodesAsync(IReadOnlyList<ModNodeId> nodeIds, bool deleteStoredFiles, CancellationToken cancellationToken = default);
	ValueTask<ModLibraryDeletionResult> DeleteNodesWithResultAsync(IReadOnlyList<ModNodeId> nodeIds, bool deleteStoredFiles, CancellationToken cancellationToken = default);
	ValueTask<ModStoragePendingCleanupResult> RecoverPendingStoredDeletionsAsync(CancellationToken cancellationToken = default);
	ValueTask<IReadOnlySet<string>> GetPendingStoredDeletionSynchronizationExclusionsAsync(CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> UpsertProfileAsync(Profile profile, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> DeleteProfileAsync(ProfileId profileId, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> RenameProfileAsync(ProfileId profileId, string newName, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> SetActiveProfileAsync(ProfileId? profileId, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> AddProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> AddProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> RemoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> RemoveProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> MoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, int direction, CancellationToken cancellationToken = default);
	ValueTask<LibrarySnapshot> UpdateNodeMetadataAsync(ModNodeId nodeId, ModNodeMetadata metadata, CancellationToken cancellationToken = default);
}
