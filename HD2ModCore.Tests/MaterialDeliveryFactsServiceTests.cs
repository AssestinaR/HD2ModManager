using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies persisted Patch facts classify safe material delivery paths without payload re-reading.
public sealed class MaterialDeliveryFactsServiceTests
{
	private static readonly AssetKey Unit = new(0xe0a48d0be9a7453f, 1);
	private static readonly AssetKey Material = new(0xeac0b497876adedf, 2);
	private static readonly AssetKey Texture = new(0xcd4238c6a0c69e32, 3);

	[Fact]
	public async Task GetAsync_ClassifiesCompleteEmbeddedClosure()
	{
		var node = CreateNode("Embedded");
		var facts = Entry(node, [Unit, Material, Texture], [new PatchAssetReference(Unit, Material, PatchReferenceKind.UnitMaterial, 0), new PatchAssetReference(Material, Texture, PatchReferenceKind.MaterialTexture, 0)]);
		var result = await new MaterialDeliveryFactsService(new FakeInformationCenter(new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> { [node.Id] = facts.Analyses }), new StoragePaths(Path.GetTempPath())).GetAsync(node.Id, Snapshot(node));

		Assert.Equal(MaterialDeliveryMode.EmbeddedComplete, result.Mode);
		Assert.True(result.CanRebuildAsWhole);
		Assert.Equal(1, result.EmbeddedMaterialCount);
	}

	[Fact]
	public async Task GetAsync_ResolvesSingleExternalMaterialProvider()
	{
		var model = CreateNode("Model");
		var materials = CreateNode("Materials");
		var entries = new[] {
			Entry(model, [Unit], [new PatchAssetReference(Unit, Material, PatchReferenceKind.UnitMaterial, 0)]),
			Entry(materials, [Material, Texture], [new PatchAssetReference(Material, Texture, PatchReferenceKind.MaterialTexture, 0)])
		};
		var center = new FakeInformationCenter(entries.ToDictionary(entry => entry.NodeId, entry => (IReadOnlyList<PatchGroupAnalysis>)entry.Analyses));
		var result = await new MaterialDeliveryFactsService(center, new StoragePaths(Path.GetTempPath())).GetAsync(model.Id, Snapshot(model, materials), includeCandidates: true);

		Assert.Equal(MaterialDeliveryMode.ExternalResolved, result.Mode);
		Assert.True(result.CanRebuildModelOnly);
		var candidate = Assert.Single(result.Candidates);
		Assert.Equal(materials.Metadata.Name, candidate.Name);
		Assert.True(candidate.IsComplete);
	}

	[Fact]
	public async Task GetAsync_WithoutCandidates_LeavesExternalMaterialUnresolved()
	{
		var model = CreateNode("Model");
		var materials = CreateNode("Materials");
		var entries = new[] {
			Entry(model, [Unit], [new PatchAssetReference(Unit, Material, PatchReferenceKind.UnitMaterial, 0)]),
			Entry(materials, [Material, Texture], [new PatchAssetReference(Material, Texture, PatchReferenceKind.MaterialTexture, 0)])
		};
		var center = new FakeInformationCenter(entries.ToDictionary(entry => entry.NodeId, entry => (IReadOnlyList<PatchGroupAnalysis>)entry.Analyses));
		var result = await new MaterialDeliveryFactsService(center, new StoragePaths(Path.GetTempPath())).GetAsync(model.Id, Snapshot(model, materials), includeCandidates: false);

		Assert.Equal(MaterialDeliveryMode.ExternalUnresolved, result.Mode);
		Assert.Empty(result.Candidates);
	}

	[Fact]
	public async Task GetAsync_ClassifiesMaterialOnlyModAndSelfReferences()
	{
		var node = CreateNode("MaterialsOnly");
		var facts = Entry(node, [Material, Texture], [new PatchAssetReference(Material, Texture, PatchReferenceKind.MaterialTexture, 0)]);
		var center = new FakeInformationCenter(new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> { [node.Id] = facts.Analyses });

		var result = await new MaterialDeliveryFactsService(center, new StoragePaths(Path.GetTempPath())).GetAsync(node.Id, Snapshot(node));

		Assert.Equal(MaterialDeliveryMode.MaterialOnly, result.Mode);
		Assert.True(result.IsMaterialOnly);
		var selfReference = Assert.Single(result.SelfMaterialReferences!);
		Assert.Equal(PatchReferenceKind.MaterialTexture, selfReference.Kind);
	}

	[Fact]
	public async Task GetAsync_WithoutGameDataMapping_DoesNotRequireMappingServiceAndKeepsClassification()
	{
		var node = CreateNode("EmbeddedWithoutMapping");
		var facts = Entry(node, [Unit, Material, Texture], [new PatchAssetReference(Unit, Material, PatchReferenceKind.UnitMaterial, 0), new PatchAssetReference(Material, Texture, PatchReferenceKind.MaterialTexture, 0)]);
		var center = new FakeInformationCenter(new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> { [node.Id] = facts.Analyses });

		var result = await new MaterialDeliveryFactsService(center, new StoragePaths(Path.GetTempPath()), new ThrowingMappingService())
			.GetAsync(node.Id, Snapshot(node), includeGameDataMapping: false);

		Assert.Equal(MaterialDeliveryMode.EmbeddedComplete, result.Mode);
		Assert.Empty(result.GameDataMappedMaterialKeys!);
	}

	// 只构造测试真正需要的东西：节点身份 + 该节点的分析结果。
	// 这里曾经包一层已废弃的 PatchGroupAnalysisCacheEntry，那个类型没有任何生产者。
	private static (ModNodeId NodeId, IReadOnlyList<PatchGroupAnalysis> Analyses) Entry(ModNode node, IReadOnlyCollection<AssetKey> assets, IReadOnlyCollection<PatchAssetReference> references)
	{
		var analysis = new PatchGroupAnalysis(
			new PatchGroupInput($"{node.Metadata.Name}.patch_0"),
			assets.Select(key => new PatchAssetFact(key, $"{node.Metadata.Name}.patch_0", 0, 0, 0, key.TypeId == 0xe0a48d0be9a7453f, false, key.TypeId == 0xeac0b497876adedf, key.TypeId == 0xcd4238c6a0c69e32)).ToArray(),
			references.ToArray(),
			Array.Empty<PatchAnalysisIssue>(),
			DateTimeOffset.UtcNow,
			"test");
		return (node.Id, [analysis]);
	}

	private static ModNode CreateNode(string name) => new(ModNodeId.New(), name, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());
	private static LibrarySnapshot Snapshot(params ModNode[] nodes) => new(1, DateTimeOffset.UtcNow, nodes.ToDictionary(node => node.Id), Array.Empty<Profile>());

	private sealed class ThrowingMappingService : IGameDataMappingFactsService
	{
		public ValueTask<GameDataMappingFacts> MapAsync(IReadOnlySet<HD2ModCore.Domain.AssetKey> assetKeys, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Mapping service should not be called when mapping is disabled.");
	}

}