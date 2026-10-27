using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

// 作用：验证 FileFacts JSON 缓存的原子写入与读取。
// Purpose: Verifies atomic JSON FileFacts cache write and read behavior.
public sealed class JsonModFileFactsCacheTests
{
	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsFacts()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-file-facts-cache-" + Guid.NewGuid().ToString("N"));
		var cache = new HD2ModCore.Infrastructure.JsonModFileFactsCache(new HD2ModCore.Infrastructure.StoragePaths(root));
		var nodeId = ModNodeId.New();
		var facts = new PatchFileIndex(DateTimeOffset.UtcNow, new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>> { [nodeId] = [] }, []);
		try
		{
			await cache.SaveAsync("generation:test", facts);
			var loaded = await cache.TryLoadAsync("generation:test");
			Assert.NotNull(loaded);
			Assert.True(loaded!.FilesByNode.ContainsKey(nodeId));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task DeleteNodeAsync_IgnoresEmptyOrMalformedCacheEntries()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-file-facts-cache-delete-" + Guid.NewGuid().ToString("N"));
		var paths = new HD2ModCore.Infrastructure.StoragePaths(root);
		var cache = new HD2ModCore.Infrastructure.JsonModFileFactsCache(paths);
		var nodeId = ModNodeId.New();
		try
		{
			await cache.SaveAsync("valid", new PatchFileIndex(DateTimeOffset.UtcNow, new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>> { [nodeId] = [] }, []));
			Directory.CreateDirectory(paths.IndexDirectory + "\\mod-information");
			File.WriteAllText(Path.Combine(paths.IndexDirectory, "mod-information", "empty.json"), "{\"facts\":null}");
			File.WriteAllText(Path.Combine(paths.IndexDirectory, "mod-information", "broken.json"), "not-json");

			await cache.DeleteNodeAsync(nodeId);

			Assert.False(File.Exists(Path.Combine(paths.IndexDirectory, "mod-information", "valid.json")));
			Assert.True(File.Exists(Path.Combine(paths.IndexDirectory, "mod-information", "empty.json")));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task DeleteNodeAsync_LeavesGenerationsThatCoverOnlyOtherNodes()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-file-facts-cache-isolate-" + Guid.NewGuid().ToString("N"));
		var paths = new HD2ModCore.Infrastructure.StoragePaths(root);
		var cache = new HD2ModCore.Infrastructure.JsonModFileFactsCache(paths);
		var first = ModNodeId.New();
		var second = ModNodeId.New();
		try
		{
			await cache.SaveAsync("genfirst", Facts(first));
			await cache.SaveAsync("gensecond", Facts(second));

			await cache.DeleteNodeAsync(first);

			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			Assert.False(File.Exists(Path.Combine(info, "genfirst.json")));
			Assert.True(File.Exists(Path.Combine(info, "gensecond.json")));
			Assert.Null(await cache.TryLoadAsync("genfirst"));
			Assert.NotNull(await cache.TryLoadAsync("gensecond"));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task DeleteNodeAsync_WithoutOwnershipIndex_FallsBackToScanningOnceAndRebuildsIt()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-file-facts-cache-legacy-" + Guid.NewGuid().ToString("N"));
		var paths = new HD2ModCore.Infrastructure.StoragePaths(root);
		var cache = new HD2ModCore.Infrastructure.JsonModFileFactsCache(paths);
		var nodeId = ModNodeId.New();
		try
		{
			await cache.SaveAsync("gen-legacy", Facts(nodeId));
			var info = Path.Combine(paths.IndexDirectory, "mod-information");
			// 模拟旧版本留下的缓存：没有 owners.index，只能靠全量扫描。
			// Simulate a legacy cache: no ownership index, so the fallback scan is the only option.
			File.Delete(Path.Combine(info, "owners.index"));

			await cache.DeleteNodeAsync(nodeId);

			Assert.False(File.Exists(Path.Combine(info, "gen-legacy.json")));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	private static PatchFileIndex Facts(ModNodeId nodeId)
		=> new(DateTimeOffset.UtcNow, new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>> { [nodeId] = [] }, []);
}
