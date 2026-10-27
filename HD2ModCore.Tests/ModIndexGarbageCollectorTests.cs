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

	[Fact]
	public async Task CollectAsync_KeepsSnapshotCacheCoveringAnyLiveNode()
	{
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

	[Fact]
	public async Task CollectAsync_RemovesDeadDataIndexNodesAndLegacyFile()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var live = ModNodeId.New();
			var dead = ModNodeId.New();
			var nodes = Path.Combine(paths.IndexDirectory, "mod-data-index");
			Directory.CreateDirectory(nodes);
			File.WriteAllText(Path.Combine(nodes, live.Value.ToString("N") + ".json"), "[]");
			File.WriteAllText(Path.Combine(nodes, dead.Value.ToString("N") + ".json"), "[]");
			var legacy = Path.Combine(paths.IndexDirectory, "mod-data-index.json");
			File.WriteAllText(legacy, "{}");

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			// 一个死节点文件 + 旧版单文件
			Assert.Equal(2, result.RemovedIndexNodes);
			Assert.True(File.Exists(Path.Combine(nodes, live.Value.ToString("N") + ".json")));
			Assert.False(File.Exists(Path.Combine(nodes, dead.Value.ToString("N") + ".json")));
			Assert.False(File.Exists(legacy));
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

	[Fact]
	public async Task CollectAsync_RemovesFactRowsForDeadNodes()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.IndexDirectory);
			var live = ModNodeId.New();
			var dead = ModNodeId.New();
			using (var connection = new SqliteConnection($"Data Source={paths.ModFactsDbPath}"))
			{
				connection.Open();
				using var create = connection.CreateCommand();
				create.CommandText = "CREATE TABLE patch_groups(node_id TEXT NOT NULL, group_id TEXT NOT NULL)";
				create.ExecuteNonQuery();
				foreach (var id in new[] { live, dead })
				{
					using var insert = connection.CreateCommand();
					insert.CommandText = "INSERT INTO patch_groups(node_id, group_id) VALUES($n, 'g')";
					insert.Parameters.AddWithValue("$n", id.Value.ToString("N"));
					insert.ExecuteNonQuery();
				}
			}
			SqliteConnection.ClearAllPools();

			var result = await new ModIndexGarbageCollector(paths).CollectAsync([live]);

			Assert.Equal(1, result.RemovedFactRows);
			using var verify = new SqliteConnection($"Data Source={paths.ModFactsDbPath}");
			verify.Open();
			using var count = verify.CreateCommand();
			count.CommandText = "SELECT node_id FROM patch_groups";
			using var reader = count.ExecuteReader();
			Assert.True(reader.Read());
			Assert.Equal(live.Value.ToString("N"), reader.GetString(0));
			Assert.False(reader.Read());
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
