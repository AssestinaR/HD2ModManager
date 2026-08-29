using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using System.Text.Json;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies unified flat-mod content facts, stable patch-group identity and per-mod generation invalidation.
public sealed class ModContentFactsServiceTests
{
	[Fact]
	public async Task GetNodeFactsAsync_GroupsSidecarsAndPreservesStableIdentity()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("mod-a", "Mod A");
			var directory = Path.Combine(root, node.RelativePath);
			Directory.CreateDirectory(directory);
			var basePath = Path.Combine(directory, "0123456789abcdef.patch_7");
			await File.WriteAllBytesAsync(basePath, [1]);
			await File.WriteAllBytesAsync(basePath + ".stream", [2]);
			await File.WriteAllBytesAsync(basePath + ".gpu_resources", [3]);
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[basePath] = [new AdaptationAssetKey(10, 20)],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			var group = Assert.Single(facts.PatchGroups);
			Assert.Equal(new ModPatchGroupId(node.Id, "0123456789abcdef", 7), group.Id);
			Assert.Equal(3, group.Files.Count);
			Assert.Contains(new HD2ModCore.Domain.AssetKey(10, 20), group.AssetKeys);
			Assert.True(group.IsValid);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task GetNodeFactsAsync_ChangesOnlyModifiedNodeGeneration_AndIgnoresBak()
	{
		var root = CreateTempRoot();
		try
		{
			var first = CreateNode("mod-a", "Mod A");
			var second = CreateNode("mod-b", "Mod B");
			var firstBase = await CreatePatchGroupAsync(root, first, "0123456789abcdef.patch_0");
			var secondBase = await CreatePatchGroupAsync(root, second, "fedcba9876543210.patch_0");
			var backupDirectory = Path.Combine(root, first.RelativePath, "bak", "20260714");
			Directory.CreateDirectory(backupDirectory);
			await File.WriteAllBytesAsync(Path.Combine(backupDirectory, "0123456789abcdef.patch_9"), [9]);
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[firstBase] = [],
				[secondBase] = [],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);
			var beforeFirst = await service.GetNodeFactsAsync(first, root);
			var beforeSecond = await service.GetNodeFactsAsync(second, root);

			await File.WriteAllBytesAsync(firstBase + ".stream", [1, 2, 3]);
			var afterFirst = await service.GetNodeFactsAsync(first, root);
			var afterSecond = await service.GetNodeFactsAsync(second, root);

			Assert.NotEqual(beforeFirst.ContentGeneration, afterFirst.ContentGeneration);
			Assert.Equal(beforeSecond.ContentGeneration, afterSecond.ContentGeneration);
			Assert.Single(afterFirst.PatchGroups);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task GetNodeFactsAsync_RetainsBrokenGroupIssueWithoutBlockingOtherGroups()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("mod-a", "Mod A");
			var directory = Path.Combine(root, node.RelativePath);
			Directory.CreateDirectory(directory);
			var validBase = Path.Combine(directory, "0123456789abcdef.patch_0");
			await File.WriteAllBytesAsync(validBase, [1]);
			await File.WriteAllBytesAsync(Path.Combine(directory, "fedcba9876543210.patch_4.stream"), [2]);
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[validBase] = [new AdaptationAssetKey(1, 2)],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			Assert.Equal(2, facts.PatchGroups.Count);
			Assert.Contains(facts.Issues, issue => issue.Code == "SidecarWithoutBase");
			Assert.Contains(facts.PatchGroups, group => group.IsValid && group.AssetKeys.Contains(new HD2ModCore.Domain.AssetKey(1, 2)));
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task GetNodeFactsAsync_ProjectsLuaFactsAndExposesModSummary()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("lua-mod", "Lua Mod");
			var directory = Path.Combine(root, node.RelativePath);
			Directory.CreateDirectory(directory);
			var basePath = Path.Combine(directory, "9ba626afa44a3aa3.patch_0");
			await File.WriteAllBytesAsync(basePath, [1]);
			var luaKey = new AdaptationAssetKey(LuaAssetFacts.BootTypeId, LuaAssetFacts.BootFileId);
			var provider = new FixedAnalysisProvider(basePath, new PatchAssetFact(
				luaKey, basePath, 326, 0, 0, false, false, false, false, new PatchLuaAssetFact()));
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			Assert.True(facts.ContainsLua);
			Assert.Equal(1, facts.LuaEntryCount);
			Assert.Equal(LuaMergeState.Detected, facts.LuaMergeState);
			var asset = Assert.Single(facts.Structure.Assets);
			Assert.True(asset.IsLua);
			Assert.Equal("Boot", asset.Lua!.Kind);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public void ModAssetStructureAssetFact_ReadsOlderJsonWithoutLuaProperty()
	{
		const string json = """
			{"assetKey":{"typeId":10,"fileId":20},"isUnit":false,"isCompositeUnit":false,"isMaterial":false,"isTexture":false,"occurrences":[]}
			""";

		var asset = JsonSerializer.Deserialize<ModAssetStructureAssetFact>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

		Assert.NotNull(asset);
		Assert.Null(asset!.Lua);
		Assert.False(asset.IsLua);
	}

	private static async Task<string> CreatePatchGroupAsync(string root, ModNode node, string fileName)
	{
		var directory = Path.Combine(root, node.RelativePath);
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, fileName);
		await File.WriteAllBytesAsync(path, [1]);
		await File.WriteAllBytesAsync(path + ".stream", [1]);
		return path;
	}

	private static string CreateTempRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-content-facts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static ModNode CreateNode(string relativePath, string name)
		=> new(ModNodeId.New(), relativePath, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());

	private static void DeleteQuietly(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	private sealed class CountingProvider : IPatchGroupAnalysisProvider
	{
		private readonly IReadOnlyDictionary<string, IReadOnlyList<AdaptationAssetKey>> _assetsByBasePath;

		public CountingProvider(IReadOnlyDictionary<string, IReadOnlyList<AdaptationAssetKey>> assetsByBasePath)
		{
			_assetsByBasePath = assetsByBasePath;
		}

		public ValueTask<IReadOnlyList<PatchGroupAnalysis>> AnalyzeNodeAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
		{
			var analyses = _assetsByBasePath
				.Where(pair => string.Equals(Path.GetDirectoryName(pair.Key), Path.Combine(modsRootDirectory, node.RelativePath), StringComparison.OrdinalIgnoreCase))
				.Where(pair => File.Exists(pair.Key))
				.Select(pair => new PatchGroupAnalysis(
					new PatchGroupInput(pair.Key, File.Exists(pair.Key + ".stream") ? pair.Key + ".stream" : null, File.Exists(pair.Key + ".gpu_resources") ? pair.Key + ".gpu_resources" : null),
					pair.Value.Select(key => new PatchAssetFact(key, pair.Key, 0, 0, 0, false, false, false, false)).ToList(),
					Array.Empty<PatchAssetReference>(),
					Array.Empty<PatchAnalysisIssue>(),
					DateTimeOffset.UtcNow,
					"test"))
				.ToList();
			return ValueTask.FromResult<IReadOnlyList<PatchGroupAnalysis>>(analyses);
		}
	}

	private sealed class FixedAnalysisProvider : IPatchGroupAnalysisProvider
	{
		private readonly string basePath;
		private readonly PatchAssetFact asset;

		public FixedAnalysisProvider(string basePath, PatchAssetFact asset)
		{
			this.basePath = basePath;
			this.asset = asset;
		}

		public ValueTask<IReadOnlyList<PatchGroupAnalysis>> AnalyzeNodeAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
		{
			var analysis = new PatchGroupAnalysis(
				new PatchGroupInput(basePath),
				[asset],
				Array.Empty<PatchAssetReference>(),
				Array.Empty<PatchAnalysisIssue>(),
				DateTimeOffset.UtcNow,
				"test");
			return ValueTask.FromResult<IReadOnlyList<PatchGroupAnalysis>>([analysis]);
		}
	}
}
