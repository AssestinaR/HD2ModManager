using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ModLibrarySynchronizerTests
{
	[Fact]
	public async Task SynchronizeAsync_NewLogicalDirectory_ReusesMarkerNodeId()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var mods = Path.Combine(root, "mods");
			var logical = Path.Combine(mods, "logical");
			Directory.CreateDirectory(logical);
			var markerId = ModNodeId.New();
			ModNodeStorageProtocol.WriteLogicalMarker(logical, markerId);

			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>(),
				Array.Empty<Profile>());
			var result = await new ModLibrarySynchronizer(new PatchFileNameParser()).SynchronizeAsync(snapshot, mods);

			var added = Assert.Single(result.AddedNodeIds);
			Assert.Equal(markerId, added);
			Assert.True(result.Snapshot.Nodes[markerId].Metadata.IsLogicalContainer);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task SynchronizeAsync_RemovedLogicalMarker_ClearsLogicalContainerState()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var mods = Path.Combine(root, "mods");
			var logical = Path.Combine(mods, "logical");
			Directory.CreateDirectory(logical);
			var nodeId = ModNodeId.New();
			ModNodeStorageProtocol.WriteLogicalMarker(logical, nodeId);
			var node = new ModNode(
				nodeId,
				"logical",
				new ModNodeMetadata("逻辑", null, DateTimeOffset.UtcNow, null, ModNodeKind.Standard, IsLogicalContainer: true),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>());
			ModNodeStorageProtocol.DeleteLogicalMarker(logical);

			var result = await new ModLibrarySynchronizer(new PatchFileNameParser()).SynchronizeAsync(snapshot, mods);

			Assert.Contains(nodeId, result.ChangedNodeIds);
			Assert.False(result.Snapshot.Nodes[nodeId].Metadata.IsLogicalContainer);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task SynchronizeAsync_LogicalMarkerAlongsidePatch_KeepsPatchDeployable()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var mods = Path.Combine(root, "mods");
			var directory = Path.Combine(mods, "marker-with-patch");
			Directory.CreateDirectory(directory);
			ModNodeStorageProtocol.WriteLogicalMarker(directory, ModNodeId.New());
			await File.WriteAllBytesAsync(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), [0x01]);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>());

			var result = await new ModLibrarySynchronizer(new PatchFileNameParser()).SynchronizeAsync(snapshot, mods);

			var node = Assert.Single(result.Snapshot.Nodes.Values);
			Assert.False(node.Metadata.IsLogicalContainer);
			Assert.Single(node.PatchGroups);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task SynchronizeAsync_PendingDeletionPath_IsNotReintroducedAsExternalMod()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var mods = Path.Combine(root, "mods");
			var stale = Path.Combine(mods, "stale");
			Directory.CreateDirectory(stale);
			await File.WriteAllTextAsync(Path.Combine(stale, "9ba626afa44a3aa3.patch_0"), "test");
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>());
			var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());

			var result = await synchronizer.SynchronizeAsync(
				snapshot,
				mods,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stale" });

			Assert.Empty(result.AddedNodeIds);
			Assert.Empty(result.Snapshot.Nodes);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
