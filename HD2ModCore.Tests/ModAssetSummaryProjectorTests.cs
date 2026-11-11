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

	/// <summary>
	/// sectional 产物把**真实游戏 unit id** 登记进了自己的素体（它复用了这些 unit 壳），
	/// 于是游戏数据答得出"这是 RS-100 的件"。那个名字是壳的副作用，不是这具素体的身份：
	/// 本 Mod 自己的素体拥有这个 unit 时，标签必须是素体名。
	/// <para>
	/// 同一批 unit 键、同样的游戏映射，**没有**登记素体的那个 Mod 仍然显示护甲名——
	/// 判据只能是"这个 Mod 目录里的 custom-model.json 就是它"，不能是"任何素体拥有这个 unit"。
	/// </para>
	/// </summary>
	[Fact]
	public async Task OwnCustomModel_WinsOverTheGameArmorThatOwnsTheSameUnitShells()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-summary-own-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var registry = new RuntimeArmorRegistryService(new StoragePaths(root), Path.Combine(root, "runtime"));
			var unit = new AssetKey(0xe0a48d0be9a7453f, 101);
			var material = new AssetKey(0xeac0b497876adedf, 200);
			await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(0, false, [],
				[new("custom-own", null, [new(3, unit.FileId.ToString("x16"), 0, 0)], "水墨牛牛")]));

			var ownedDirectory = Path.Combine(root, "owned");
			Directory.CreateDirectory(ownedDirectory);
			await File.WriteAllTextAsync(
				Path.Combine(ownedDirectory, "custom-model.json"),
				"""{ "key": "custom-own", "name": "水墨牛牛" }""");
			Directory.CreateDirectory(Path.Combine(root, "plain"));

			var projector = new ModAssetSummaryProjector(
				CenterFor(NativeUnitMapping(unit, material)), new SourceCatalogProvider(), registry, root);

			var ownedNode = Node("owned");
			var owned = await projector.ProjectAsync(ownedNode, Facts(ownedNode, unit, material));

			var group = Assert.Single(owned.TargetGroups);
			Assert.Equal("CustomModel", group.Category);
			Assert.Equal("水墨牛牛", Assert.Single(group.Items).DisplayName);
			Assert.Contains("水墨牛牛", owned.DerivedTags);
			// 标签里出现的是完整护甲名（`RS-100“制裁者”`），所以按子串判。
			Assert.DoesNotContain(owned.DerivedTags, tag => tag.Contains("RS-100", StringComparison.Ordinal));
			// 依赖事实仍然留在资产表里，只是不再参与"这个 Mod 是什么"。
			Assert.Equal(2, owned.Assets.Count);

			var plainNode = Node("plain");
			var plain = await projector.ProjectAsync(plainNode, Facts(plainNode, unit, material));
			Assert.Contains(plain.DerivedTags, tag => tag.Contains("RS-100", StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	/// <summary>
	/// 注册表还没同步到这具素体时，标签也必须答"这是一具自定义素体"。
	/// <para>
	/// 这条守的是实机事故（2026-11-10）：重构建刚写完 <c>custom-model.json</c>（新 key），
	/// 而工作区注册表要等下一次同步才认识它；只查注册表就把整份产物答成"原版护甲 RS-100"。
	/// 归属凭证是文件，注册表只是投影。
	/// </para>
	/// </summary>
	[Fact]
	public async Task OwnCustomModel_IsLabeledFromTheModFileEvenBeforeTheRegistryKnowsIt()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-summary-file-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var unit = new AssetKey(0xe0a48d0be9a7453f, 101);
			var material = new AssetKey(0xeac0b497876adedf, 200);
			var model = Path.Combine(root, "owned");
			Directory.CreateDirectory(model);
			await File.WriteAllTextAsync(Path.Combine(model, "custom-model.json"), $$"""
				{
				  "key": "custom-brand-new",
				  "name": "水墨牛牛",
				  "pieces": [ { "body": 1, "unit": "{{unit.FileId:x16}}", "slot": 2, "pieceType": 0 } ]
				}
				""");

			// 注册表里**没有**这具素体（连工作区文件都没有）。
			var registry = new RuntimeArmorRegistryService(new StoragePaths(root), Path.Combine(root, "runtime"));
			var projector = new ModAssetSummaryProjector(
				CenterFor(NativeUnitMapping(unit, material)), new SourceCatalogProvider(), registry, root);

			var node = Node("owned");
			var summary = await projector.ProjectAsync(node, Facts(node, unit, material));

			var group = Assert.Single(summary.TargetGroups);
			Assert.Equal("CustomModel", group.Category);
			Assert.Equal("水墨牛牛", Assert.Single(group.Items).DisplayName);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	private static ModNode Node(string relativePath)
		=> new(ModNodeId.New(), relativePath, new ModNodeMetadata(relativePath, null, DateTimeOffset.UtcNow, null), [], []);

	private static ModContentFacts Facts(ModNode node, params AssetKey[] keys)
		=> new(node.Id, node.RelativePath, "facts", DateTimeOffset.UtcNow,
			[new(new ModPatchGroupId(node.Id, "0123456789abcdef", 0), 0, [], keys.ToHashSet(), [])], []);

	/// <summary>游戏数据**认识**这些键：unit 落在 RS-100 那套护甲里。</summary>
	private static Func<IReadOnlySet<AssetKey>, GameDataMappingFacts> NativeUnitMapping(AssetKey unit, AssetKey material)
		=> assetKeys => new GameDataMappingFacts("mapping", "index", "metadata", DateTimeOffset.UtcNow,
			assetKeys.ToDictionary(key => key, key => new GameDataMappedAssetFact(key, "body",
				key.TypeId == unit.TypeId ? "Unit" : "Material", AssetTypeCategory.Model,
				[new ArchiveMetadata("0123456789abcdef", "Armor", "RS-100“制裁者”")])), []);

	private static Func<IReadOnlySet<AssetKey>, GameDataMappingFacts> DependencyMapping(AssetKey material, AssetKey nativeUnit)		=> assetKeys => new GameDataMappingFacts("mapping", "index", "metadata", DateTimeOffset.UtcNow,
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
