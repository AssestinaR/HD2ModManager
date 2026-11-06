using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using AssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住 Mod 资产/引用事实的**会话内存**实现。
/// <para>
/// 这些事实完全由 patch 文件算出来，删掉重算即可——它们不是用户输入。所以不需要
/// schema、迁移、损坏恢复，也不需要那个 21.7 MB 的库文件。
/// </para>
/// <para>
/// 但有一件事必须钉住：**同一个管理器目录只能有一份实例**。文件时代两个入口各自
/// new 一个也能看到同一份数据；换成内存之后，两个实例就是两份互不相干的内存，
/// 写入的进不去查询的那个，所有查询都会返回空。
/// </para>
/// </summary>
public sealed class InMemoryModFactsStoreTests
{
	private static AssetKey Key(ulong fileId) => new(0x1111222233334444UL, fileId);

	private static ReferenceGraphFacts Facts(ModNodeId nodeId, params PatchGroupAnalysis[] analyses)
		=> new(nodeId, "mod-" + nodeId.Value.ToString("N"), "generation", DateTimeOffset.UnixEpoch, analyses, Array.Empty<CoreIssue>());

	private static PatchGroupAnalysis Analysis(
		string patchName,
		IReadOnlyList<PatchAssetFact> assets,
		IReadOnlyList<PatchAssetReference> references)
		=> new(
			new PatchGroupInput("/tmp/" + patchName),
			assets,
			references,
			Array.Empty<PatchAnalysisIssue>(),
			DateTimeOffset.UnixEpoch,
			"test");

	private static PatchAssetFact Asset(AssetKey key)
		=> new(key, "source", 0, 0, 0, false, false, false, IsTexture: false);

	[Fact]
	public void For_SharesOneInstancePerManagerDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-facts-share-" + Guid.NewGuid().ToString("N"));

		Assert.Same(
			InMemoryModFactsStore.For(new StoragePaths(root)),
			InMemoryModFactsStore.For(new StoragePaths(root)));
		Assert.NotSame(
			InMemoryModFactsStore.For(new StoragePaths(root)),
			InMemoryModFactsStore.For(new StoragePaths(root + "-other")));
	}

	[Fact]
	public async Task FindProviderFacts_ReturnsTheNodeThatCarriesTheAsset()
	{
		var store = new InMemoryModFactsStore();
		var nodeId = ModNodeId.New();
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0", [Asset(Key(7))], Array.Empty<PatchAssetReference>())));

		var providers = await store.FindProviderFactsAsync([Key(7)]);

		var provider = Assert.Single(providers);
		Assert.Equal(nodeId, provider.NodeId);
		Assert.Equal(Key(7).TypeId, provider.AssetKey.TypeId);
		Assert.Equal(Key(7).FileId, provider.AssetKey.FileId);
	}

	[Fact]
	public async Task FindConsumerFacts_ReturnsTheReferencingNode()
	{
		var store = new InMemoryModFactsStore();
		var nodeId = ModNodeId.New();
		var reference = new PatchAssetReference(Key(1), Key(2), PatchReferenceKind.UnitMaterial, 16);
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0", [Asset(Key(1))], [reference])));

		var consumers = await store.FindConsumerFactsAsync(Key(2));

		var consumer = Assert.Single(consumers);
		Assert.Equal(nodeId, consumer.NodeId);
		Assert.Equal(Key(1).FileId, consumer.Reference.SourceAssetKey.FileId);
	}

	[Fact]
	public async Task ReplaceNode_OverwritesThePreviousFacts()
	{
		var store = new InMemoryModFactsStore();
		var nodeId = ModNodeId.New();
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0", [Asset(Key(1))], Array.Empty<PatchAssetReference>())));
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0", [Asset(Key(2))], Array.Empty<PatchAssetReference>())));

		Assert.Empty(await store.FindProviderFactsAsync([Key(1)]));
		Assert.Single(await store.FindProviderFactsAsync([Key(2)]));
	}

	[Fact]
	public async Task DeleteNode_RemovesItsFacts()
	{
		var store = new InMemoryModFactsStore();
		var nodeId = ModNodeId.New();
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0", [Asset(Key(1))], Array.Empty<PatchAssetReference>())));

		await store.DeleteNodeAsync(nodeId);

		Assert.Empty(await store.FindProviderFactsAsync([Key(1)]));
		Assert.Equal(ProfileIndexedFacts.Empty.Assets, (await store.ReadProfileFactsAsync([nodeId])).Assets);
	}

	[Fact]
	public async Task ReadProfileFacts_ProjectsAssetsAndReferences()
	{
		var store = new InMemoryModFactsStore();
		var nodeId = ModNodeId.New();
		await store.ReplaceNodeAsync(Facts(nodeId, Analysis("a.patch_0",
			[Asset(Key(1))],
			[new PatchAssetReference(Key(1), Key(2), PatchReferenceKind.UnitMaterial, 16)])));

		var facts = await store.ReadProfileFactsAsync([nodeId]);

		Assert.Equal(Key(1).FileId, Assert.Single(facts.Assets).AssetKey.FileId);
		Assert.Equal(Key(2).FileId, Assert.Single(facts.References).TargetAssetKey.FileId);
	}

	[Fact]
	public async Task QueriesOnAnEmptyStore_ReturnNothing()
	{
		var store = new InMemoryModFactsStore();

		Assert.Empty(await store.FindProviderFactsAsync([Key(1)]));
		Assert.Empty(await store.FindConsumerFactsAsync(Key(1)));
		Assert.Empty((await store.ReadProfileFactsAsync([ModNodeId.New()])).Assets);
	}
}
