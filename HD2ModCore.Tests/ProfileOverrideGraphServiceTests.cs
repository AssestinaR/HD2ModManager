using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies expected profile winners use strict AssetKey identity while archive overlap remains a separate coarse signal.
public sealed class ProfileOverrideGraphServiceTests
{
	[Fact]
	public async Task BuildAsync_GroupsSameAssetKeyAcrossDifferentSourceArchives()
	{
		var firstId = ModNodeId.New();
		var secondId = ModNodeId.New();
		var assetKey = new AssetKey(10, 20);
		var first = CreateNode(firstId, "First");
		var second = CreateNode(secondId, "Second");
		var profile = new Profile(ProfileId.New(), "Profile", DateTimeOffset.UtcNow, null,
		[
			new ProfileEntry(firstId, 0),
			new ProfileEntry(secondId, 1),
		], Revision: 4);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [firstId] = first, [secondId] = second }, [profile], profile.Id);
		var content = new Dictionary<ModNodeId, ModContentFacts>
		{
			[firstId] = CreateFacts(first, "aaaaaaaaaaaaaaaa", assetKey, "gen-a"),
			[secondId] = CreateFacts(second, "bbbbbbbbbbbbbbbb", assetKey, "gen-b"),
		};
		var mapping = new FakeMappingFactsService(new GameDataMappedAssetFact(assetKey, "body", "unit", AssetTypeCategory.Model,
		[
			new ArchiveMetadata("target-a", "Armor", "Armor A"),
		]));
		var service = new ProfileOverrideGraphService(new FakeInformationCenter(content), mapping, new EmptyReferenceGraphQueryIndex());

		var graph = await service.BuildAsync(profile, snapshot, "unused");

		var chain = Assert.Single(graph.AssetChains);
		Assert.Equal(assetKey, chain.AssetKey);
		Assert.Equal(secondId, chain.Winner.NodeId);
		Assert.Equal(2, chain.Entries.Count);
		Assert.True(chain.IsCompetition);
		Assert.True(Assert.Single(graph.Coverages, coverage => coverage.NodeId == firstId).FullyOverridden);
		Assert.False(string.IsNullOrWhiteSpace(graph.GraphGeneration));
	}

	[Fact]
	public async Task BuildAsync_ReportsArchiveOverlapWithoutAssetKeyCompetition()
	{
		var firstId = ModNodeId.New();
		var secondId = ModNodeId.New();
		var firstKey = new AssetKey(10, 20);
		var secondKey = new AssetKey(10, 21);
		var first = CreateNode(firstId, "First");
		var second = CreateNode(secondId, "Second");
		var profile = new Profile(ProfileId.New(), "Profile", DateTimeOffset.UtcNow, null,
		[
			new ProfileEntry(firstId, 0),
			new ProfileEntry(secondId, 1),
		]);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [firstId] = first, [secondId] = second }, [profile]);
		var content = new Dictionary<ModNodeId, ModContentFacts>
		{
			[firstId] = CreateFacts(first, "aaaaaaaaaaaaaaaa", firstKey, "gen-a"),
			[secondId] = CreateFacts(second, "bbbbbbbbbbbbbbbb", secondKey, "gen-b"),
		};
		var target = new ArchiveMetadata("target-a", "Armor", "Armor A");
		var mapping = new FakeMappingFactsService(
			new GameDataMappedAssetFact(firstKey, "body", "unit", AssetTypeCategory.Model, [target]),
			new GameDataMappedAssetFact(secondKey, "helmet", "unit", AssetTypeCategory.Model, [target]));
		var service = new ProfileOverrideGraphService(new FakeInformationCenter(content), mapping, new EmptyReferenceGraphQueryIndex());

		var graph = await service.BuildAsync(profile, snapshot, "unused");

		Assert.Equal(2, graph.AssetChains.Count);
		Assert.All(graph.AssetChains, chain => Assert.False(chain.IsCompetition));
		var overlap = Assert.Single(graph.ArchiveOverlaps);
		Assert.Equal("target-a", overlap.ArchiveId);
		Assert.Equal(2, overlap.NodeIds.Count);
	}

	[Fact]
	public async Task BuildAsync_IncludesPrivateMaterialAssetWithoutGameDataMapping()
	{
		var firstId = ModNodeId.New();
		var secondId = ModNodeId.New();
		var material = new AssetKey(0xeac0b497876adedf, 0x1234);
		var first = CreateNode(firstId, "Red");
		var second = CreateNode(secondId, "Blue");
		var profile = new Profile(ProfileId.New(), "Profile", DateTimeOffset.UtcNow, null, [new ProfileEntry(firstId, 0), new ProfileEntry(secondId, 1)]);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [firstId] = first, [secondId] = second }, [profile]);
		var content = new Dictionary<ModNodeId, ModContentFacts>
		{
			[firstId] = CreateFacts(first, "aaaaaaaaaaaaaaaa", material, "red"),
			[secondId] = CreateFacts(second, "bbbbbbbbbbbbbbbb", material, "blue"),
		};
		var service = new ProfileOverrideGraphService(new FakeInformationCenter(content), new FakeMappingFactsService(), new EmptyReferenceGraphQueryIndex());

		var graph = await service.BuildAsync(profile, snapshot, "unused");

		var chain = Assert.Single(graph.AssetChains);
		Assert.Equal(material, chain.AssetKey);
		Assert.Equal(secondId, chain.Winner.NodeId);
		Assert.Equal("Material", chain.Entries[0].Mapping.TypeDisplayName);
		Assert.True(Assert.Single(graph.Coverages, coverage => coverage.NodeId == firstId).FullyOverridden);
	}

	[Fact]
	public async Task BuildAsync_ChangesGenerationWithProfileContentOrMappingGeneration()
	{
		var nodeId = ModNodeId.New();
		var node = CreateNode(nodeId, "Mod");
		var assetKey = new AssetKey(10, 20);
		var profile = new Profile(ProfileId.New(), "Profile", DateTimeOffset.UtcNow, null, [new ProfileEntry(nodeId, 0)], Revision: 1);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, [profile]);
		var mapping = new FakeMappingFactsService(new GameDataMappedAssetFact(assetKey, "body", "unit", AssetTypeCategory.Model, []));
		var firstService = new ProfileOverrideGraphService(new FakeInformationCenter(new Dictionary<ModNodeId, ModContentFacts>
		{
			[nodeId] = CreateFacts(node, "aaaaaaaaaaaaaaaa", assetKey, "gen-a"),
		}), mapping, new EmptyReferenceGraphQueryIndex());
		var first = await firstService.BuildAsync(profile, snapshot, "unused");
		var revised = await firstService.BuildAsync(profile with { Revision = 2 }, snapshot, "unused");
		var changedContentService = new ProfileOverrideGraphService(new FakeInformationCenter(new Dictionary<ModNodeId, ModContentFacts>
		{
			[nodeId] = CreateFacts(node, "aaaaaaaaaaaaaaaa", assetKey, "gen-b"),
		}), mapping, new EmptyReferenceGraphQueryIndex());
		var changedContent = await changedContentService.BuildAsync(profile, snapshot, "unused");
		mapping.Generation = "mapping-2";
		var changedMapping = await firstService.BuildAsync(profile, snapshot, "unused");

		Assert.NotEqual(first.GraphGeneration, revised.GraphGeneration);
		Assert.NotEqual(first.GraphGeneration, changedContent.GraphGeneration);
		Assert.NotEqual(first.GraphGeneration, changedMapping.GraphGeneration);
	}

	private static ModNode CreateNode(ModNodeId id, string name)
		=> new(id, name, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());

	private static ModContentFacts CreateFacts(ModNode node, string archive, AssetKey assetKey, string generation)
	{
		var groupId = new ModPatchGroupId(node.Id, archive, 0);
		var group = new ModPatchGroupFact(groupId, 0, [new ModPatchGroupFileFact(PatchSidecarKind.Base, archive + ".patch_0", archive + ".patch_0", 1, DateTimeOffset.UtcNow)], new HashSet<AssetKey> { assetKey }, []);
		return new ModContentFacts(node.Id, node.RelativePath, generation, DateTimeOffset.UtcNow, [group], []);
	}

	private sealed class FakeMappingFactsService : IGameDataMappingFactsService
	{
		private readonly IReadOnlyDictionary<AssetKey, GameDataMappedAssetFact> _assets;
		public string Generation { get; set; } = "mapping-1";
		public FakeMappingFactsService(params GameDataMappedAssetFact[] assets) => _assets = assets.ToDictionary(asset => asset.AssetKey);
		public ValueTask<GameDataMappingFacts> MapAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new GameDataMappingFacts(Generation, "index", "metadata", DateTimeOffset.UtcNow, _assets.Where(pair => assetKeys.Contains(pair.Key)).ToDictionary(), []));
	}
}
