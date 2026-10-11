using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：把"缓存不得取代真实性"这条硬约束固化成测试。
// Purpose: Pins the hard constraint that the memo must never become the source of truth.
//
// 三件事必须同时成立，缺一不可：
//   1. 同一份来源 → 只算一次（否则缓存没有意义）；
//   2. 来源一变 → 必须重算（否则"游戏更新了却返回旧数据"）；
//   3. 重建路径从不读这张记忆（否则缓存就真的成了真相）。
public sealed class GameDataFactCacheTests
{
	// ── 1. 同一份来源只算一次 ──────────────────────────────────────────────────

	[Fact]
	public async Task AssetMetadataCatalogIsParsedOncePerSourceFingerprint()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");

			// 生产里这 6 个消费者各拿一个独立实例，所以"共享"必须发生在进程级，
			// 而不是某个实例的字段里。两个互不相识的实例必须命中同一条记忆。
			var first = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();
			var second = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();

			Assert.Same(first, second);
			Assert.Equal("content/armor/body", second.Files[20].FriendlyName);
		}
		finally
		{
			TryDelete(root);
		}
	}

	[Fact]
	public async Task RepeatedIdenticalMappingRequestsAreServedFromTheMemo()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");
			var index = new CountingIndexService();
			var service = new GameDataMappingFactsService(index, new FileSystemAssetMetadataCatalogProvider(paths), paths);
			var keys = new HashSet<AssetKey> { new(10, 20) };

			var first = await service.MapAsync(keys);
			var second = await service.MapAsync(keys);

			// FindAssetArchivesAsync 对每个资产键各发一条 SQL；命中记忆时这条路径根本不该被走到。
			Assert.Equal(1, index.FindCalls);
			Assert.Same(first, second);
		}
		finally
		{
			TryDelete(root);
		}
	}

	// ── 2. 来源一变就必须重算 ──────────────────────────────────────────────────

	[Fact]
	public async Task ChangingAMetadataFileForcesAGenuineReload()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");
			var first = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();
			Assert.Equal("content/armor/body", first.Files[20].FriendlyName);

			// 同时改内容与长度：指纹必然变化，旧条目不得再被命中。
			await File.WriteAllTextAsync(paths.FriendlyNamesPath, "20 content/armor/body_extended\n");
			var second = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();

			Assert.NotSame(first, second);
			Assert.Equal("content/armor/body_extended", second.Files[20].FriendlyName);
		}
		finally
		{
			TryDelete(root);
		}
	}

	[Fact]
	public async Task RemovingAMetadataFileForcesTheMemoToStopServingIt()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");
			var first = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();
			Assert.NotEmpty(first.Files);

			// 记忆不是真相：源文件没了，读出来的就必须是"没有"，而不是记忆里的旧值。
			File.Delete(paths.FriendlyNamesPath);
			var second = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();

			Assert.Empty(second.Files);
		}
		finally
		{
			TryDelete(root);
		}
	}

	[Fact]
	public async Task ChangingTheIndexGenerationForcesAMappingRecompute()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");
			var index = new CountingIndexService { Generation = "index-1" };
			var service = new GameDataMappingFactsService(index, new FileSystemAssetMetadataCatalogProvider(paths), paths);
			var keys = new HashSet<AssetKey> { new(10, 20) };

			var first = await service.MapAsync(keys);
			index.Generation = "index-2";
			var second = await service.MapAsync(keys);

			Assert.Equal(2, index.FindCalls);
			Assert.NotEqual(first.MappingGeneration, second.MappingGeneration);
		}
		finally
		{
			TryDelete(root);
		}
	}

	// 不同选择器必须拿到各自的结果——否则页面之间会互相"串味"。
	[Fact]
	public async Task DifferentSelectorsDoNotShareAMappingEntry()
	{
		var root = CreateRoot();
		try
		{
			var paths = await WriteMetadataAsync(root, friendlyName: "content/armor/body");
			var index = new CountingIndexService();
			var service = new GameDataMappingFactsService(index, new FileSystemAssetMetadataCatalogProvider(paths), paths);

			var onlyTwenty = await service.MapAsync(new HashSet<AssetKey> { new(10, 20) });
			var alsoThirty = await service.MapAsync(new HashSet<AssetKey> { new(10, 20), new(10, 30) });

			Assert.Equal(2, index.FindCalls);
			Assert.Single(onlyTwenty.Assets);
			Assert.Equal(2, alsoThirty.Assets.Count);
		}
		finally
		{
			TryDelete(root);
		}
	}

	// ── 3. 重建路径从不读这张记忆 ─────────────────────────────────────────────

	// 用户给出的硬约束："重建功能需要能完整从资产文件、加密的注册表文件中重新获取数据，
	// 避免有了缓存却丢失了真实性。" 这条断言把那个口子焊死在源码层面。
	[Fact]
	public void RebuildNeverConsumesTheFactCache()
	{
		var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModCore", "Infrastructure", "AssetArchiveIndexService.cs"));
		var member = Assert.Single(
			source.Split("\n\tpublic ", StringSplitOptions.RemoveEmptyEntries),
			candidate => candidate.Contains("BuildOrRebuildAsync(", StringComparison.Ordinal));

		Assert.DoesNotContain("GameDataFactCache", member);
		// 它必须仍然走"从安装目录重扫"这条路。
		Assert.Contains("_archiveIndexer.BuildAsync(", member);
		Assert.Contains("ComputeSourceFingerprintAsync(", member);
	}

	// ── 记忆本身的行为 ────────────────────────────────────────────────────────

	[Fact]
	public void CacheIsTypedSoAKeyOfOneKindNeverLeaksIntoAnother()
	{
		GameDataFactCache.Set("test-typed-key", "text");

		Assert.True(GameDataFactCache.TryGet("test-typed-key", out string value));
		Assert.Equal("text", value);
		Assert.False(GameDataFactCache.TryGet("test-typed-key", out int _));
		Assert.False(GameDataFactCache.TryGet("test-missing-key", out string _));
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-gamedata-facts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static async Task<StoragePaths> WriteMetadataAsync(string root, string friendlyName)
	{
		var paths = new StoragePaths(root);
		Directory.CreateDirectory(paths.ResourcesDirectory);
		await File.WriteAllTextAsync(paths.FriendlyNamesPath, $"20 {friendlyName}\n");
		await File.WriteAllTextAsync(paths.TypeHashesPath, "000000000000000a unit\n");
		await File.WriteAllTextAsync(paths.ArchiveHashesPath, "{}");
		return paths;
	}

	private static void TryDelete(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}

	private sealed class CountingIndexService : IAssetArchiveIndexService
	{
		public int FindCalls { get; private set; }
		public string Generation { get; set; } = "index-1";

		public ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

		public ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<GameDataIndexFingerprint?>(new GameDataIndexFingerprint("data", DateTimeOffset.UtcNow, 2, 2, 1, Generation));

		public ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<GameDataArchiveSummary>>([]);

		public ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(string packageName, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<GameDataArchiveDetails?>(null);

		public ValueTask<IReadOnlyDictionary<AssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(IReadOnlySet<AssetKey> unitAssetKeys, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyDictionary<AssetKey, IReadOnlyList<GameDataUnitPartFact>>>(new Dictionary<AssetKey, IReadOnlyList<GameDataUnitPartFact>>());

		public ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> FindStreamLayoutsAsync(IReadOnlyList<GameDataStreamComponentFact> components, uint vertexStride, bool requireSkinned = false, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<GameDataStreamLayoutFact>>([]);

		public ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> GetStreamLayoutsAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<GameDataStreamLayoutFact>>([]);

		public ValueTask<GameDataIndexStatus> GetIndexStatusAsync(string gameDataDirectory, string archiveHashesJson, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new GameDataIndexStatus(GameDataIndexState.Current, null, gameDataDirectory, Generation));

		public ValueTask BuildOrRebuildAsync(string gameDataDirectory, string archiveHashesJson, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;

		public ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
		{
			FindCalls++;
			IReadOnlyList<AssetArchiveMatch> matches = assetKeys
				.OrderBy(key => key.TypeId).ThenBy(key => key.FileId)
				.Select(key => new AssetArchiveMatch(key, [new ArchiveMetadata("target-a", "Armor", "Armor A", 0, 0)]))
				.ToList();
			return ValueTask.FromResult(matches);
		}

		public ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(IReadOnlySet<AssetKey> assetKeys, IndexFilterSettings filterSettings, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
	}
}
