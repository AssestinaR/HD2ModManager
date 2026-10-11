using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies advanced asset targets prefer exact Mod Unit consumers over coincident Game Data mappings.
public sealed class AdvancedModAssetQueryServiceTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;
	private const ulong MaterialType = 0xeac0b497876adedf;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	[Fact]
	public async Task QueryAsync_PrefersReverseModUnitConsumers_ForMaterialAndTexture()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-advanced-facts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var paths = new StoragePaths(root);
			var model = Node("Model");
			var material = Node("Material pack");
			var unit = new AdaptationAssetKey(UnitType, 1);
			var materialKey = new AdaptationAssetKey(MaterialType, 2);
			var texture = new AdaptationAssetKey(TextureType, 3);
			var index = new StubReferenceIndex(
				new ModAssetConsumerFact(model.Id, model.RelativePath, Reference(unit, materialKey, PatchReferenceKind.UnitMaterial)),
				new ModAssetConsumerFact(material.Id, material.RelativePath, Reference(materialKey, texture, PatchReferenceKind.MaterialTexture)));
			var library = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [model.Id] = model, [material.Id] = material }, [], null);
			var modelFacts = Facts(unit, [Reference(unit, materialKey, PatchReferenceKind.UnitMaterial)]);
			var materialFacts = Facts(materialKey, texture, [Reference(materialKey, texture, PatchReferenceKind.MaterialTexture)]);
			var center = new FakeInformationCenter(
				new Dictionary<ModNodeId, ModContentFacts> { [model.Id] = Inventory(model, unit), [material.Id] = Inventory(material, materialKey, texture) },
				new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>>
			{
				[model.Id] = [modelFacts],
				[material.Id] = [materialFacts]
				});
			center.GameDataMapping = StubMapping(materialKey, texture);
			var service = new AdvancedModAssetQueryService(center, paths, index, new StubIndexService());

			var rows = await service.QueryAsync(material.Id, library, null, null);

			Assert.Contains(rows, row => row.AssetKey == new HD2ModCore.Domain.AssetKey(materialKey.TypeId, materialKey.FileId) && row.TargetSummary == "Mod 引用：Model / Unit 0x0000000000000001");
			Assert.Contains(rows, row => row.AssetKey == new HD2ModCore.Domain.AssetKey(texture.TypeId, texture.FileId) && row.TargetSummary == "Mod 引用：Model / Unit 0x0000000000000001");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task QueryAsync_MapsUnitPartFactsFromGameDataByExactAssetKey()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-part-facts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var paths = new StoragePaths(root);
			var node = Node("Armor");
			var unit = new AdaptationAssetKey(UnitType, 1);
			var index = new StubReferenceIndex();
			var library = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [node.Id] = node }, [], null);
			var unitKey = new HD2ModCore.Domain.AssetKey(UnitType, 1);
			var part = new GameDataUnitPartFact("armor", unitKey, 0, 42, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Stocky, "Torso_Armor_Stocky_lod0", 100, true, false, "test");

			var center = new FakeInformationCenter(Inventory(node, unit), new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> { [node.Id] = [Facts(unit, [])] });
			center.GameDataMapping = StubMapping();
			var rows = await new AdvancedModAssetQueryService(center, paths, index, new StubIndexService([part])).QueryAsync(node.Id, library, null, null);

			Assert.Contains(rows, row => row.AssetKey == new HD2ModCore.Domain.AssetKey(UnitType, 1) && row.PartSummary == "胸口－护甲－健壮");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static ModNode Node(string name) => new(ModNodeId.New(), name, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), [], []);
	private static ModContentFacts Inventory(ModNode node, params AdaptationAssetKey[] assets)
		=> new(node.Id, node.RelativePath, "test", DateTimeOffset.UtcNow, [new ModPatchGroupFact(new ModPatchGroupId(node.Id, "archive", 0), 0, [], assets.Select(key => new HD2ModCore.Domain.AssetKey(key.TypeId, key.FileId)).ToHashSet(), [], null)], []);
	private static PatchGroupAnalysis Facts(AdaptationAssetKey first, IReadOnlyList<PatchAssetReference> references) => Facts([first], references);
	private static PatchGroupAnalysis Facts(AdaptationAssetKey first, AdaptationAssetKey second, IReadOnlyList<PatchAssetReference> references) => Facts([first, second], references);
	private static PatchGroupAnalysis Facts(IReadOnlyList<AdaptationAssetKey> assets, IReadOnlyList<PatchAssetReference> references)
		=> new(new PatchGroupInput(Guid.NewGuid() + ".patch_0"), assets.Select(key => new PatchAssetFact(key, "facts.patch_0", 1, 0, 0, key.TypeId == UnitType, false, key.TypeId == MaterialType, key.TypeId == TextureType)).ToArray(), references, [], DateTimeOffset.UtcNow, "patch-group-v2");
	private static PatchAssetReference Reference(AdaptationAssetKey source, AdaptationAssetKey target, PatchReferenceKind kind) => new(source, target, kind, 0);

	private sealed class StubReferenceIndex(params ModAssetConsumerFact[] facts) : IReferenceGraphQueryIndex
	{
		public ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(AdaptationAssetKey targetAssetKey, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<ModAssetConsumerFact>>(facts.Where(fact => fact.Reference.TargetAssetKey == targetAssetKey).ToArray());
	}

	// 映射事实现在由信息中心提供，所以假映射服务换成一个"键集合 → 事实"的委托，
	// 直接注入 FakeInformationCenter（设计文档 §28：业务服务不得直调映射服务）。
	private static Func<IReadOnlySet<HD2ModCore.Domain.AssetKey>, GameDataMappingFacts> StubMapping(params AdaptationAssetKey[] mappedKeys)
		=> assetKeys =>
		{
			var mapped = assetKeys.ToDictionary(
				key => key,
				key => new GameDataMappedAssetFact(key, "GameData name", "Material", AssetTypeCategory.Material, mappedKeys.Contains(new AdaptationAssetKey(key.TypeId, key.FileId)) ? [new ArchiveMetadata("unrelated", "Armor", "Unrelated armor")] : []));
			return new GameDataMappingFacts("mapping", "index", "metadata", DateTimeOffset.UtcNow, mapped, []);
		};

	private sealed class StubIndexService(params GameDataUnitPartFact[] parts) : IAssetArchiveIndexService
	{
		public ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
		public ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<GameDataIndexFingerprint?>(null);
		public ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<GameDataArchiveSummary>>([]);
		public ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(string packageName, CancellationToken cancellationToken = default) => ValueTask.FromResult<GameDataArchiveDetails?>(null);
		public ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> FindStreamLayoutsAsync(IReadOnlyList<HD2ModCore.Domain.GameDataStreamComponentFact> components, uint vertexStride, bool requireSkinned = false, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>>([]);
		public ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> GetStreamLayoutsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>>([]);
		public ValueTask<IReadOnlyDictionary<HD2ModCore.Domain.AssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(IReadOnlySet<HD2ModCore.Domain.AssetKey> unitAssetKeys, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyDictionary<HD2ModCore.Domain.AssetKey, IReadOnlyList<GameDataUnitPartFact>>>(parts.Where(part => unitAssetKeys.Contains(part.UnitAssetKey)).GroupBy(part => part.UnitAssetKey).ToDictionary(group => group.Key, group => (IReadOnlyList<GameDataUnitPartFact>)group.ToArray()));
		public ValueTask<GameDataIndexStatus> GetIndexStatusAsync(string gameDataDirectory, string archiveHashesJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask BuildOrRebuildAsync(string gameDataDirectory, string archiveHashesJson, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(IReadOnlySet<HD2ModCore.Domain.AssetKey> assetKeys, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AssetArchiveMatch>>([]);
		public ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(IReadOnlySet<HD2ModCore.Domain.AssetKey> assetKeys, IndexFilterSettings filterSettings, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
	}
}