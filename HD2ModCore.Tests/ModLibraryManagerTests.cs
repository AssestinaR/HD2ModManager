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
	public async Task UpsertProfile_AllowsNoPatchStandardEntries_AndCanReorderThem()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var manager = new ModLibraryManager(paths, store);
			var first = CreateNode(ModNodeId.New(), "first");
			var second = CreateNode(ModNodeId.New(), "second");
			var now = DateTimeOffset.UtcNow;
			var profile = new Profile(
				ProfileId.New(),
				"p",
				now,
				now,
				[new ProfileEntry(first.Id, 0, now), new ProfileEntry(second.Id, 1, now)]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [first.Id] = first, [second.Id] = second },
				[profile]);
			await store.SaveAsync(snapshot);

			var reordered = await manager.UpsertProfileAsync(profile with
			{
				Entries = [new ProfileEntry(second.Id, 0, now), new ProfileEntry(first.Id, 1, now)],
			});

			Assert.Equal([second.Id, first.Id], reordered.Profiles.Single().Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeleteNodeAsync_RemovesEmptyAffectedProfileHost_ButLeavesTheLibraryNode()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var manager = new ModLibraryManager(paths, store);
			var now = DateTimeOffset.UtcNow;
			var host = CreateNode(ModNodeId.New(), "empty-host") with
			{
				Metadata = new ModNodeMetadata("empty-host", null, now, now, ModNodeKind.Standard, IsLogicalContainer: true),
			};
			var child = new ModNode(
				ModNodeId.New(),
				"child",
				new ModNodeMetadata("child", null, now, now, ModNodeKind.Option, [host.Id.Value.ToString("N")]),
				[new PatchGroupKey("0123456789abcdef", 0)],
				[]);
			var profile = new Profile(ProfileId.New(), "p", now, now, [new ProfileEntry(host.Id, 0, now)]);
			await store.SaveAsync(new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [host.Id] = host, [child.Id] = child },
				[profile]));

			var result = await manager.DeleteNodeAsync(child.Id, deleteStoredFiles: false);

			Assert.True(result.Nodes.ContainsKey(host.Id));
			Assert.Empty(result.Profiles.Single().Entries);
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
	public async Task DeleteNodesWithResultAsync_RemovingHost_PrunesSurvivingOptionRelationsAndSidecars()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var manager = new ModLibraryManager(paths, store);
			var now = DateTimeOffset.UtcNow;
			var hostA = CreateNode(ModNodeId.New(), "host-a") with
			{
				PatchGroups = [new PatchGroupKey("1111111111111111", 0)],
			};
			var hostB = CreateNode(ModNodeId.New(), "host-b") with
			{
				PatchGroups = [new PatchGroupKey("2222222222222222", 0)],
			};
			var shared = new ModNode(
				ModNodeId.New(),
				"shared-option",
				new ModNodeMetadata(
					"共享选项", null, now, now, ModNodeKind.Option,
					[hostA.Id.Value.ToString("N"), hostB.Id.Value.ToString("N")],
					OptionOrder: 0),
				[new PatchGroupKey("3333333333333333", 0)],
				[]);
			var logicalOnly = new ModNode(
				ModNodeId.New(),
				"logical-option",
				new ModNodeMetadata(
					"仅逻辑选项", null, now, now, ModNodeKind.Option,
					[hostA.Id.Value.ToString("N")],
					IsLogicalContainer: true),
				[],
				[]);
			foreach (var node in new[] { hostA, hostB, shared, logicalOnly })
			{
				var directory = Path.Combine(paths.ModsDirectory, node.RelativePath);
				Directory.CreateDirectory(directory);
				foreach (var patch in node.PatchGroups)
					await File.WriteAllBytesAsync(Path.Combine(directory, $"{patch.ArchiveHex16}.patch_{patch.PatchIndex}"), [0x01]);
				ModNodeStorageProtocol.SynchronizeNodeMetadata(directory, node);
			}
			await store.SaveAsync(new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode>
				{
					[hostA.Id] = hostA,
					[hostB.Id] = hostB,
					[shared.Id] = shared,
					[logicalOnly.Id] = logicalOnly,
				},
				Array.Empty<Profile>()));

			var result = await manager.DeleteNodesWithResultAsync([hostA.Id], deleteStoredFiles: true);

			Assert.Contains(hostA.Id, result.RemovedNodeIds);
			var retainedShared = result.Snapshot.Nodes[shared.Id];
			Assert.Equal(ModNodeKind.Option, retainedShared.Metadata.Kind);
			Assert.Equal([hostB.Id.Value.ToString("N")], retainedShared.Metadata.HostModGuids);
			var sharedSidecar = await File.ReadAllTextAsync(ModNodeStorageProtocol.OptionRelationPath(Path.Combine(paths.ModsDirectory, shared.RelativePath)));
			Assert.Contains(hostB.Id.Value.ToString("N"), sharedSidecar, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(hostA.Id.Value.ToString("N"), sharedSidecar, StringComparison.OrdinalIgnoreCase);

			var restored = result.Snapshot.Nodes[logicalOnly.Id];
			Assert.Equal(ModNodeKind.Standard, restored.Metadata.Kind);
			Assert.Null(restored.Metadata.HostModGuids);
			Assert.True(restored.Metadata.IsLogicalContainer);
			var logicalDirectory = Path.Combine(paths.ModsDirectory, logicalOnly.RelativePath);
			Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(logicalDirectory)));
			Assert.False(File.Exists(ModNodeStorageProtocol.OptionRelationPath(logicalDirectory)));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// 直接删一个选项（宿主还在）必须是可行且干净的：选项节点和它的目录一起消失，
	// 宿主节点的元数据、patch 组和 sidecar 一律不动——所有权存在子节点上，父节点
	// 从来没有"我有哪些选项"的清单，所以删子节点不需要回写父节点。
	// Deleting an Option while its host survives must work and must not touch the host.
	[Fact]
	public async Task DeleteNodesWithResultAsync_RemovingOption_KeepsHostUntouched()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var manager = new ModLibraryManager(paths, store);
			var now = DateTimeOffset.UtcNow;
			var host = CreateNode(ModNodeId.New(), "host") with
			{
				PatchGroups = [new PatchGroupKey("4444444444444444", 0)],
			};
			var option = new ModNode(
				ModNodeId.New(),
				"host-option",
				new ModNodeMetadata(
					"宿主选项", null, now, now, ModNodeKind.Option,
					[host.Id.Value.ToString("N")],
					OptionOrder: 0),
				[new PatchGroupKey("5555555555555555", 0)],
				[]);
			foreach (var node in new[] { host, option })
			{
				var directory = Path.Combine(paths.ModsDirectory, node.RelativePath);
				Directory.CreateDirectory(directory);
				foreach (var patch in node.PatchGroups)
					await File.WriteAllBytesAsync(Path.Combine(directory, $"{patch.ArchiveHex16}.patch_{patch.PatchIndex}"), [0x02]);
				ModNodeStorageProtocol.SynchronizeNodeMetadata(directory, node);
			}
			await store.SaveAsync(new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode>
				{
					[host.Id] = host,
					[option.Id] = option,
				},
				Array.Empty<Profile>()));

			var result = await manager.DeleteNodesWithResultAsync([option.Id], deleteStoredFiles: true);

			Assert.Equal([option.Id], result.RemovedNodeIds);
			Assert.False(result.HasFailures);
			Assert.DoesNotContain(option.Id, result.Snapshot.Nodes.Keys);
			Assert.False(Directory.Exists(Path.Combine(paths.ModsDirectory, option.RelativePath)));

			var survivingHost = result.Snapshot.Nodes[host.Id];
			Assert.Equal(ModNodeKind.Standard, survivingHost.Metadata.Kind);
			Assert.Null(survivingHost.Metadata.HostModGuids);
			Assert.Equal([new PatchGroupKey("4444444444444444", 0)], survivingHost.PatchGroups);
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, host.RelativePath, "4444444444444444.patch_0")));
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
