using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// 作用：验证派生索引的垃圾回收——以库中仍存在的节点为准，回收已删除节点的缓存、索引、事实行与备份。
// Purpose: Verifies derived-index garbage collection against the live node set.
public sealed class ModIndexGarbageCollectorTests
{
	private const string GenerationA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private const string GenerationB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

	[Fact]
	public async Task CollectAsync_RemovesPerNodeInformationCacheOnlyForDeadNodes()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var dead = ModNodeId.New();
			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			Directory.CreateDirectory(info);
			var liveFile = Path.Combine(info, $"Thumbnail_{live.Value:N}_{GenerationA}.json");
			var deadFile = Path.Combine(info, $"Thumbnail_{dead.Value:N}_{GenerationA}.json");
			File.WriteAllText(liveFile, "{}");
			File.WriteAllText(deadFile, "{}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(1, result.RemovedCacheFiles);
			Assert.True(File.Exists(liveFile));
			Assert.False(File.Exists(deadFile));
		}
		finally { DeleteRoot(root); }
	}

	// 2026-11-06 实测：201 个节点留下 245 个 AssetInventory 缓存，44 个是旧 generation 残留。
	// 旧逻辑只按"节点是否还在库中"删，永远碰不到这些文件——它们会一直留在 data/indexes 里。
	[Fact]
	public async Task CollectAsync_ForALiveNode_KeepsOnlyTheNewestGeneration()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			Directory.CreateDirectory(info);
			var older = Path.Combine(info, $"AssetInventory_{live.Value:N}_{GenerationA}.json");
			var newer = Path.Combine(info, $"AssetInventory_{live.Value:N}_{GenerationB}.json");
			File.WriteAllText(older, "{}");
			File.WriteAllText(newer, "{}");
			File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
			// 另一个种类的旧代也应被回收（同一节点、不同 kind 各自保留最新）
			var otherKind = Path.Combine(info, $"ReferenceGraph_{live.Value:N}_{GenerationA}.json");
			File.WriteAllText(otherKind, "{}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(1, result.RemovedCacheFiles);
			Assert.False(File.Exists(older));
			Assert.True(File.Exists(newer));
			Assert.True(File.Exists(otherKind));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task CollectAsync_KeepsSnapshotCacheCoveringAnyLiveNode()	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var dead = ModNodeId.New();
			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			Directory.CreateDirectory(info);
			var coveringLive = Path.Combine(info, GenerationA + ".json");
			var onlyDead = Path.Combine(info, GenerationB + ".json");
			File.WriteAllText(coveringLive, $"{{\"filesByNode\":{{\"{live.Value:N}\":[],\"{dead.Value:N}\":[]}}}}");
			File.WriteAllText(onlyDead, $"{{\"filesByNode\":{{\"{dead.Value:N}\":[]}}}}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(1, result.RemovedCacheFiles);
			Assert.True(File.Exists(coveringLive));
			Assert.False(File.Exists(onlyDead));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task CollectAsync_KeepsUnrecognizedCacheShape()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			Directory.CreateDirectory(info);
			var unknown = Path.Combine(info, GenerationA + ".json");
			File.WriteAllText(unknown, "{\"somethingElse\":true}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([]);

			Assert.Equal(0, result.RemovedCacheFiles);
			Assert.True(File.Exists(unknown));
		}
		finally { DeleteRoot(root); }
	}

	// mod-data-index 已没有任何读写方（详情页那一行改查 SQL 索引）。旧库上它还会留着
	// 一堆文件，这里锁住"回收时会整目录清掉"。
	[Fact]
	public async Task CollectAsync_RemovesObsoleteModDataIndexDirectory()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var obsolete = Path.Combine(paths.IndexDirectory, "mod-data-index");
			Directory.CreateDirectory(obsolete);
			File.WriteAllText(Path.Combine(obsolete, $"{live.Value:N}.json"), "{}");
			File.WriteAllText(Path.Combine(obsolete, $"{ModNodeId.New().Value:N}.json"), "{}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(2, result.RemovedCacheFiles);
			Assert.False(Directory.Exists(obsolete));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task CollectAsync_RemovesDeadReplacementBackups()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var dead = ModNodeId.New();
			var backups = Path.Combine(paths.DataDirectory, "mod-replacement-backups");
			var liveDir = Path.Combine(backups, live.Value.ToString("N"), "20260101-000000");
			var deadDir = Path.Combine(backups, dead.Value.ToString("N"), "20260101-000000");
			Directory.CreateDirectory(liveDir);
			Directory.CreateDirectory(deadDir);
			File.WriteAllText(Path.Combine(liveDir, "a.patch_0"), "x");
			File.WriteAllText(Path.Combine(deadDir, "a.patch_0"), "x");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(1, result.RemovedBackupDirectories);
			Assert.True(Directory.Exists(Path.Combine(backups, live.Value.ToString("N"))));
			Assert.False(Directory.Exists(Path.Combine(backups, dead.Value.ToString("N"))));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task CollectAsync_RemovesOnlyStaleTemporaryFiles()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.IndexDirectory);
			var stale = Path.Combine(paths.IndexDirectory, "mod-data-index.json.deadbeef.tmp");
			var fresh = Path.Combine(paths.IndexDirectory, "mod-data-index.json.cafebabe.tmp");
			File.WriteAllText(stale, "x");
			File.WriteAllText(fresh, "x");
			File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([]);

			Assert.Equal(1, result.RemovedTemporaryFiles);
			Assert.False(File.Exists(stale));
			Assert.True(File.Exists(fresh));
		}
		finally { DeleteRoot(root); }
	}

	// 这些事实现在只活在会话内存里，文件没人读了。以前这里只修剪它的行、把文件永远留着；
	// 既然不读了，整个删掉才叫回收——21.7 MB 不该为了"以后也许有用"一直躺着。
	[Fact]
	public async Task CollectAsync_ReclaimsTheObsoleteFactsDatabase()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.IndexDirectory);
			await File.WriteAllTextAsync(paths.ModFactsDbPath, "stale");
			await File.WriteAllTextAsync(paths.ModFactsDbPath + "-wal", "stale");
			await File.WriteAllTextAsync(paths.ModFactsDbPath + "-shm", "stale");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([ModNodeId.New()]);

			Assert.Equal(1, result.RemovedFactRows);
			Assert.False(File.Exists(paths.ModFactsDbPath));
			Assert.False(File.Exists(paths.ModFactsDbPath + "-wal"));
			Assert.False(File.Exists(paths.ModFactsDbPath + "-shm"));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task CollectAsync_WithNothingToDo_IsEmptyAndReportsNoFailures()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var result = await new ModIndexGarbageCollector(paths).CollectAsync([ModNodeId.New()]);

			Assert.True(result.IsEmpty);
			Assert.Empty(result.Failures);
		}
		finally { DeleteRoot(root); }
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-index-gc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteRoot(string root)
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(root)) Directory.Delete(root, true);
	}
}
