using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证库管理器可删除节点并同步清理 Profile 引用。
// Purpose: Verifies library manager can delete nodes and cleans up profile references.
public sealed class ModLibraryManagerTests
{
	[Fact]
	public async Task ProfileEntryOperations_AddMoveRemove_NormalizeOrder()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var mgr = new ModLibraryManager(paths, store);

			var firstId = ModNodeId.New();
			var secondId = ModNodeId.New();
			var firstNode = CreateNode(firstId, "first");
			var secondNode = CreateNode(secondId, "second");
			var profileId = ProfileId.New();
			var profile = new Profile(profileId, "p", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			var snapshot = new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [firstId] = firstNode, [secondId] = secondNode }, new[] { profile });
			await store.SaveAsync(snapshot);

			var updated = await mgr.AddProfileEntryAsync(profileId, firstId);
			updated = await mgr.AddProfileEntryAsync(profileId, secondId);
			Assert.Equal(new[] { firstId, secondId }, updated.Profiles[0].Entries.Select(e => e.NodeId));
			Assert.Equal(2, updated.Profiles[0].Revision);

			updated = await mgr.MoveProfileEntryAsync(profileId, secondId, -1);
			Assert.Equal(new[] { secondId, firstId }, updated.Profiles[0].Entries.OrderBy(e => e.LoadOrder).Select(e => e.NodeId));
			Assert.Equal(new[] { 0, 1 }, updated.Profiles[0].Entries.OrderBy(e => e.LoadOrder).Select(e => e.LoadOrder));

			updated = await mgr.RemoveProfileEntryAsync(profileId, secondId);
			var entry = Assert.Single(updated.Profiles[0].Entries);
			Assert.Equal(firstId, entry.NodeId);
			Assert.Equal(0, entry.LoadOrder);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeleteNodeAsync_RemovesNode_AndProfileEntries()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var mgr = new ModLibraryManager(paths, store);

			var nodeId = ModNodeId.New();
			var node = new ModNode(
				Id: nodeId,
				RelativePath: Path.Combine("import1", "obj"),
				Metadata: new ModNodeMetadata("obj", null, DateTimeOffset.UtcNow, null),
				PatchGroups: Array.Empty<PatchGroupKey>(),
				Children: Array.Empty<ModNodeId>());

			var profile = new Profile(ProfileId.New(), "p", DateTimeOffset.UtcNow, null, new[] { new ProfileEntry(nodeId, 0) });
			var snapshot = new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, new[] { profile });
			await store.SaveAsync(snapshot);

			var updated = await mgr.DeleteNodeAsync(nodeId, deleteStoredFiles: false);
			Assert.False(updated.Nodes.ContainsKey(nodeId));
			Assert.Empty(updated.Profiles[0].Entries);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeleteNodesWithResultAsync_ClearsReadOnlyStoredFiles_ThenRemovesNode()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var mgr = new ModLibraryManager(paths, store);
			var nodeId = ModNodeId.New();
			var relativePath = Path.Combine("import1", "readonly-mod");
			var storedDirectory = Path.Combine(paths.ModsDirectory, relativePath);
			Directory.CreateDirectory(storedDirectory);
			var patchPath = Path.Combine(storedDirectory, "9ba626afa44a3aa3.patch_0");
			await File.WriteAllTextAsync(patchPath, "test");
			File.SetAttributes(patchPath, File.GetAttributes(patchPath) | FileAttributes.ReadOnly);

			var node = new ModNode(
				Id: nodeId,
				RelativePath: relativePath,
				Metadata: new ModNodeMetadata("readonly-mod", null, DateTimeOffset.UtcNow, null),
				PatchGroups: Array.Empty<PatchGroupKey>(),
				Children: Array.Empty<ModNodeId>());
			await store.SaveAsync(new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>()));

			var result = await mgr.DeleteNodesWithResultAsync([nodeId], deleteStoredFiles: true);

			Assert.Contains(nodeId, result.RemovedNodeIds);
			Assert.Empty(result.FailedNodeIds);
			Assert.Equal(ModStorageDeletionState.DeletedImmediately, Assert.Single(result.StorageResults).State);
			Assert.False(Directory.Exists(storedDirectory));
			Assert.False((await store.TryLoadAsync())!.Nodes.ContainsKey(nodeId));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeleteNodesWithResultAsync_UnsafeStoredPath_PreservesSnapshotAndReportsFailure()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var mgr = new ModLibraryManager(paths, store);
			var nodeId = ModNodeId.New();
			var unsafeDirectory = Path.Combine(root, "outside-library");
			Directory.CreateDirectory(unsafeDirectory);
			var node = new ModNode(
				Id: nodeId,
				RelativePath: Path.Combine("..", "outside-library"),
				Metadata: new ModNodeMetadata("outside-library", null, DateTimeOffset.UtcNow, null),
				PatchGroups: Array.Empty<PatchGroupKey>(),
				Children: Array.Empty<ModNodeId>());
			await store.SaveAsync(new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>()));

			var result = await mgr.DeleteNodesWithResultAsync([nodeId], deleteStoredFiles: true);

			Assert.Empty(result.RemovedNodeIds);
			Assert.Contains(nodeId, result.FailedNodeIds);
			Assert.Equal(ModStorageDeletionState.Failed, Assert.Single(result.StorageResults).State);
			Assert.True(Directory.Exists(unsafeDirectory));
			Assert.True((await store.TryLoadAsync())!.Nodes.ContainsKey(nodeId));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ActiveProfile_IsUniquePersisted_AndClearedWhenDeleted()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var mgr = new ModLibraryManager(paths, store);
			var first = new Profile(ProfileId.New(), "first", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			var second = new Profile(ProfileId.New(), "second", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			await store.SaveAsync(new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), new[] { first, second }));

			var updated = await mgr.SetActiveProfileAsync(first.Id);
			Assert.Equal(first.Id, updated.ActiveProfileId);
			updated = await mgr.SetActiveProfileAsync(second.Id);
			Assert.Equal(second.Id, updated.ActiveProfileId);
			Assert.Equal(second.Id, (await store.TryLoadAsync())!.ActiveProfileId);

			updated = await mgr.DeleteProfileAsync(second.Id);
			Assert.Null(updated.ActiveProfileId);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static ModNode CreateNode(ModNodeId id, string name)
	{
		return new ModNode(
			Id: id,
			RelativePath: Path.Combine("import1", name),
			Metadata: new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null),
			PatchGroups: Array.Empty<PatchGroupKey>(),
			Children: Array.Empty<ModNodeId>());
	}
}
