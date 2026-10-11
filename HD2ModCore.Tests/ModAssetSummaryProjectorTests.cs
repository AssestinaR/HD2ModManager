using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies readable library summaries are projected from stable content facts without reopening Patch files.
public sealed class ModAssetSummaryProjectorTests
{
	[Fact]
	public async Task CustomUnits_AreLabeledByWorkspace_NotSourceContainerOrMaterialDependencies()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-summary-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var registry = new RuntimeArmorRegistryService(new StoragePaths(root), Path.Combine(root, "runtime"));
			var unit = new AssetKey(0xe0a48d0be9a7453f, 101);
			var material = new AssetKey(0xeac0b497876adedf, 200);
			var nativeUnit = new AssetKey(unit.TypeId, 102);
			var node = new ModNode(ModNodeId.New(), "model", new ModNodeMetadata("Model", null, DateTimeOffset.UtcNow, null), [], []);
			ModContentFacts Facts(params AssetKey[] keys) => new(node.Id, node.RelativePath, "facts", DateTimeOffset.UtcNow,
				[new(new(node.Id, "0123456789abcdef", 0), 0, [], keys.ToHashSet(), [])], []);
			var document = new RuntimeArmorRegistryDocument(0, false, [], [new("custom-test", null,
				[new(3, unit.FileId.ToString("x16"), 2, 0, Enabled: false)], "Custom outfit")]);
			await registry.SaveWorkspaceAsync(document);
			var projector = new ModAssetSummaryProjector(CenterFor(DependencyMapping(material, nativeUnit)), new SourceCatalogProvider(), registry);
			var summary = await projector.ProjectAsync(node, Facts(unit, material));
			Assert.Equal("CustomModel", Assert.Single(summary.TargetGroups).Category);
			Assert.Contains("Custom outfit", summary.DerivedTags);
			Assert.DoesNotContain("Source armor", summary.DerivedTags);
			Assert.Equal(2, summary.Assets.Count); // Do not delete dependency facts.
			var oldGeneration = await projector.GetMappingGenerationAsync();
			await registry.SaveWorkspaceAsync(document with { ModelDefinitions = [document.ModelDefinitions[0] with { Name = "Renamed" }] });
			Assert.NotEqual(oldGeneration, await projector.GetMappingGenerationAsync());
			Assert.Contains("Renamed", (await projector.ProjectAsync(node, Facts(unit, material))).DerivedTags);
			var mixed = await projector.ProjectAsync(node, Facts(unit, nativeUnit, material));
			Assert.Contains(mixed.TargetGroups, group => group.Category == "Armor");
			var materialOnly = await projector.ProjectAsync(node, Facts(material));
			Assert.Contains("Source armor", materialOnly.DerivedTags);
			var nativeAndMaterial = await projector.ProjectAsync(node, Facts(nativeUnit, material));
			Assert.Contains("Source armor", nativeAndMaterial.DerivedTags);
			var unknown = await projector.ProjectAsync(node, Facts(new AssetKey(unit.TypeId, 999)));
			Assert.Empty(unknown.TargetGroups); // No container fallback for unknown Units.
		}
		finally { Directory.Delete(root, true); }
	}

	private static Func<IReadOnlySet<AssetKey>, GameDataMappingFacts> DependencyMapping(AssetKey material, AssetKey nativeUnit)
		=> assetKeys => new GameDataMappingFacts("mapping", "index", "metadata", DateTimeOffset.UtcNow,
			assetKeys.ToDictionary(key => key, key => new GameDataMappedAssetFact(key, "asset",
				key == material ? "Material" : "Unit", AssetTypeCategory.Model,
				key == material || key == nativeUnit ? [new("0123456789abcdef", "Armor", "Source armor")] : [])), []);

	private sealed class SourceCatalogProvider : IAssetMetadataCatalogProvider
	{
		public ValueTask<AssetMetadataCatalog> LoadAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(AssetMetadataCatalog.Empty with
			{
				Archives = new Dictionary<string, ArchiveMetadata> { ["0123456789abcdef"] = new("0123456789abcdef", "Armor", "Source armor") }
			});
	}

	[Fact]
	public async Task ProjectAsync_UsesStableFactsAndGameDataMapping()
	{
		var node = new ModNode(ModNodeId.New(), "model", new ModNodeMetadata("Model", null, DateTimeOffset.UtcNow, null), [], []);
		var key = new AssetKey(0xe0a48d0be9a7453f, 100);
		var group = new ModPatchGroupFact(new ModPatchGroupId(node.Id, "0123456789abcdef", 0), 0, [], new HashSet<AssetKey> { key }, []);
		var facts = new ModContentFacts(node.Id, node.RelativePath, "facts", DateTimeOffset.UtcNow, [group], []);
		var projector = new ModAssetSummaryProjector(CenterFor(Mapping(key)), new EmptyCatalogProvider());

		var summary = await projector.ProjectAsync(node, facts);

		var asset = Assert.Single(summary.Assets);
		Assert.Equal("B-08 Light Gunner", asset.ArchiveDisplayName);
		Assert.Equal("body", asset.FileDisplayName);
		Assert.Contains("armor", summary.DerivedTags);
		Assert.Contains("model", summary.DerivedTags);
	}

	private static Func<IReadOnlySet<AssetKey>, GameDataMappingFacts> Mapping(AssetKey key)
		=> assetKeys =>
		{
			var assets = assetKeys.ToDictionary(asset => asset, asset => asset == key
				? new GameDataMappedAssetFact(asset, "body", "Unit", AssetTypeCategory.Model, [new ArchiveMetadata("armor", "Armor", "B-08 Light Gunner")])
				: new GameDataMappedAssetFact(asset, $"0x{asset.FileId:x16}", "Unknown", AssetTypeCategory.Unknown, []));
			return new GameDataMappingFacts("mapping", "index", "metadata", DateTimeOffset.UtcNow, assets, []);
		};

	// 投影器现在只认识信息中心（设计文档 §28），所以用例通过替身中心的映射注入点来喂映射事实。
	private static FakeInformationCenter CenterFor(Func<IReadOnlySet<AssetKey>, GameDataMappingFacts> mapping)
		=> new(new Dictionary<ModNodeId, ModContentFacts>()) { GameDataMapping = mapping };

	private sealed class EmptyCatalogProvider : IAssetMetadataCatalogProvider
	{
		public ValueTask<AssetMetadataCatalog> LoadAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(AssetMetadataCatalog.Empty);
	}
}
