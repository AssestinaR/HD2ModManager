using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Application;
using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Tests;

// 作用：验证跨 Mod 资产提供者/消费者索引及节点删除隔离。
// Purpose: Verifies cross-Mod provider/consumer indexing and node-removal isolation.
public sealed class ModDataIndexTests
{
	[Fact]
	public async Task UpdateAndRemoveNode_KeepProviderAndConsumerRelationsSeparate()
	{
		var index = new ModDataIndex();
		var providerNode = new ModNodeId(Guid.NewGuid());
		var consumerNode = new ModNodeId(Guid.NewGuid());
		var asset = new AssetKey(1, 2);
		var providerInventory = new ModContentFacts(
			providerNode,
			"provider",
			"provider-generation",
			DateTimeOffset.UtcNow,
			[new ModPatchGroupFact(new ModPatchGroupId(providerNode, "archive", 3), 0, [], new HashSet<AssetKey> { asset }, [])],
			[]);
		var consumerGraph = new ReferenceGraphFacts(
			consumerNode,
			"consumer",
			"consumer-generation",
			DateTimeOffset.UtcNow,
			[new PatchGroupAnalysis(new PatchGroupInput("consumer.toc"), [], [new PatchAssetReference(new HD2ModAdaptation.PatchReconstruction.AssetKey(asset.TypeId, asset.FileId), new HD2ModAdaptation.PatchReconstruction.AssetKey(asset.TypeId, asset.FileId), PatchReferenceKind.MaterialTexture, 0)], [], DateTimeOffset.UtcNow, "test")],
			[]);

		index.Update(providerInventory);
		index.Update(consumerGraph);

		Assert.Single(await index.FindProvidersAsync(asset));
		Assert.Single(await index.FindConsumersAsync(asset));
		var summary = await index.GetAssetRelationSummaryAsync([asset], consumerNode);
		Assert.Equal(ModDataIndexStatus.Partial, summary.Status);
		Assert.Equal(1, summary.ProviderCount);
		Assert.Equal(0, summary.ConsumerCount);

		await index.RemoveNodeAsync(providerNode);
		Assert.Empty(await index.FindProvidersAsync(asset));
		Assert.Single(await index.FindConsumersAsync(asset));
	}

	[Fact]
	public async Task EmptyIndex_IsUnavailableRatherThanZeroSummary()
	{
		var index = new ModDataIndex();
		var summary = await index.GetAssetRelationSummaryAsync([new AssetKey(1, 2)]);

		Assert.Equal(ModDataIndexStatus.Unavailable, summary.Status);
		Assert.Equal(0, summary.ProviderCount);
		Assert.Equal(0, summary.ConsumerCount);
	}

	[Fact]
	public async Task UpdateOnlyIndex_IsPartialUntilPersisted()
	{
		var index = new ModDataIndex();
		index.Update(CreateInventory(new ModNodeId(Guid.NewGuid()), new AssetKey(1, 2), "partial"));

		var summary = await index.GetAssetRelationSummaryAsync([new AssetKey(1, 2)]);

		Assert.Equal(ModDataIndexStatus.Partial, summary.Status);
	}

	[Fact]
	public async Task PersistentIndex_RestoresAndResolvesLastProfileProvider()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-data-index-" + Guid.NewGuid().ToString("N"));
		try
		{
			var paths = new StoragePaths(root);
			var first = new ModDataIndex(paths);
			var earlier = new ModNodeId(Guid.NewGuid());
			var later = new ModNodeId(Guid.NewGuid());
			var asset = new AssetKey(3, 4);
			first.Update(CreateInventory(earlier, asset, "earlier"));
			first.Update(CreateInventory(later, asset, "later"));

			var restored = new ModDataIndex(paths);
			var profile = new Profile(ProfileId.New(), "test", DateTimeOffset.UtcNow, null, [new ProfileEntry(earlier, 0), new ProfileEntry(later, 1)]);
			var provider = await restored.ResolveFinalProviderAsync(asset, profile);
			var summary = await restored.GetAssetRelationSummaryAsync([asset]);

			Assert.NotNull(provider);
			Assert.Equal(later, provider.NodeId);
			Assert.Equal(2, (await restored.FindProvidersAsync(asset)).Count);
			Assert.Equal(ModDataIndexStatus.Ready, summary.Status);
			Assert.Equal(2, summary.ProviderCount);
			Assert.Equal(0, summary.ConsumerCount);
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task RemoveNodeAsync_DeletesOnlyThatNodesPersistedFile()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-data-index-pernode-" + Guid.NewGuid().ToString("N"));
		try
		{
			var paths = new StoragePaths(root);
			var kept = new ModNodeId(Guid.NewGuid());
			var removed = new ModNodeId(Guid.NewGuid());
			var first = new ModDataIndex(paths);
			first.Update(CreateInventory(kept, new AssetKey(3, 4), "kept"));
			first.Update(CreateInventory(removed, new AssetKey(5, 6), "removed"));

			var nodesDirectory = Path.Combine(paths.IndexDirectory, "mod-data-index");
			Assert.True(File.Exists(Path.Combine(nodesDirectory, kept.Value.ToString("N") + ".json")));
			Assert.True(File.Exists(Path.Combine(nodesDirectory, removed.Value.ToString("N") + ".json")));

			await first.RemoveNodeAsync(removed);

			Assert.True(File.Exists(Path.Combine(nodesDirectory, kept.Value.ToString("N") + ".json")));
			Assert.False(File.Exists(Path.Combine(nodesDirectory, removed.Value.ToString("N") + ".json")));

			var restored = new ModDataIndex(paths);
			Assert.Single(await restored.FindProvidersAsync(new AssetKey(3, 4)));
			Assert.Empty(await restored.FindProvidersAsync(new AssetKey(5, 6)));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task Constructor_MigratesLegacySingleFileLayout()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-data-index-migrate-" + Guid.NewGuid().ToString("N"));
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.IndexDirectory);
			var nodeId = new ModNodeId(Guid.NewGuid());
			var legacy = new
			{
				schemaVersion = 1,
				builtUtc = DateTimeOffset.UtcNow,
				entries = new[]
				{
					new
					{
						nodeId = new { value = nodeId.Value },
						relativePath = "legacy",
						sourceArchiveHex = "9ba626afa44a3aa3",
						patchIndex = 0,
						assetKey = new { typeId = 7UL, fileId = 8UL },
						relation = "Provider",
					},
				},
			};
			var legacyPath = Path.Combine(paths.IndexDirectory, "mod-data-index.json");
			File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(
				legacy,
				new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

			var index = new ModDataIndex(paths);

			Assert.False(File.Exists(legacyPath));
			Assert.True(File.Exists(Path.Combine(paths.IndexDirectory, "mod-data-index", nodeId.Value.ToString("N") + ".json")));
			Assert.Single(await index.FindProvidersAsync(new AssetKey(7, 8)));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	private static ModContentFacts CreateInventory(ModNodeId nodeId, AssetKey asset, string path)
		=> new(nodeId, path, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
			[new ModPatchGroupFact(new ModPatchGroupId(nodeId, path, 0), 0, [], new HashSet<AssetKey> { asset }, [])], []);
}