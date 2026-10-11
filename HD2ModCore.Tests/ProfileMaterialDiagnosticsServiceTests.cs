using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies winner-first material diagnostics do not retain overridden Material texture references.
public sealed class ProfileMaterialDiagnosticsServiceTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;
	private const ulong MaterialType = 0xeac0b497876adedf;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	[Fact]
	public async Task BuildAsync_ReportsMissingMaterialForEffectiveUnit()
	{
		var model = CreateNode("Model");
		var material = new AdaptationAssetKey(MaterialType, 2);
		var analysis = Analysis([new AdaptationAssetKey(UnitType, 1)], [Reference(new AdaptationAssetKey(UnitType, 1), material, PatchReferenceKind.UnitMaterial)]);
		var profile = ProfileFor(model);
		var service = new ProfileMaterialDiagnosticsService(CenterWithEmptyMapping(new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> { [model.Id] = [analysis] }), null, new EmptyReferenceGraphQueryIndex());

		var result = await service.BuildAsync(profile, Snapshot(profile, model), "unused");

        Assert.Equal(2, result.Items.Count);
        var issue = Assert.Single(result.Items, item => item.AssetKey.FileId == material.FileId);
        Assert.Equal(ProfileMaterialDiagnosticKind.MissingMaterial, issue.Kind);
        var aggregate = Assert.Single(result.Items, item => item.AssetKey.FileId == 1 && item.Summary == "无可用材质");
        Assert.Equal(ProfileMaterialDiagnosticKind.MissingMaterial, aggregate.Kind);
	}

	[Fact]
	public async Task BuildAsync_ReportsOverriddenVariantTextureAsUnreachable()
	{
		var model = CreateNode("Model");
		var red = CreateNode("Red");
		var blue = CreateNode("Blue");
		var unit = new AdaptationAssetKey(UnitType, 1);
		var material = new AdaptationAssetKey(MaterialType, 2);
		var redTexture = new AdaptationAssetKey(TextureType, 3);
		var blueTexture = new AdaptationAssetKey(TextureType, 4);
		var profile = new Profile(ProfileId.New(), "P", DateTimeOffset.UtcNow, null, [new ProfileEntry(model.Id, 0), new ProfileEntry(red.Id, 1), new ProfileEntry(blue.Id, 2)]);
		var data = new Dictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>>
		{
			[model.Id] = [Analysis([unit], [Reference(unit, material, PatchReferenceKind.UnitMaterial)])],
			[red.Id] = [Analysis([material, redTexture], [Reference(material, redTexture, PatchReferenceKind.MaterialTexture)])],
			[blue.Id] = [Analysis([material, blueTexture], [Reference(material, blueTexture, PatchReferenceKind.MaterialTexture)])],
		};
		var service = new ProfileMaterialDiagnosticsService(CenterWithEmptyMapping(data), null, new EmptyReferenceGraphQueryIndex());

		var result = await service.BuildAsync(profile, Snapshot(profile, model, red, blue), "unused");

		var unreachable = Assert.Single(result.Items);
		Assert.Equal(ProfileMaterialDiagnosticKind.UnreachableResource, unreachable.Kind);
		Assert.Equal(red.Id, unreachable.NodeId);
		Assert.Equal(redTexture.FileId, unreachable.AssetKey.FileId);
	}

	private static ModNode CreateNode(string name) => new(ModNodeId.New(), name, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), [], []);
	private static Profile ProfileFor(ModNode node) => new(ProfileId.New(), "P", DateTimeOffset.UtcNow, null, [new ProfileEntry(node.Id, 0)]);
	private static LibrarySnapshot Snapshot(Profile profile, params ModNode[] nodes) => new(1, DateTimeOffset.UtcNow, nodes.ToDictionary(node => node.Id), [profile], profile.Id);
	private static PatchGroupAnalysis Analysis(IReadOnlyList<AdaptationAssetKey> assets, IReadOnlyList<PatchAssetReference> references)
		=> new(new PatchGroupInput("unused.patch_0"), assets.Select(key => new PatchAssetFact(key, "unused.patch_0", 0, 0, 0, key.TypeId == UnitType, false, key.TypeId == MaterialType, key.TypeId == TextureType)).ToArray(), references, [], DateTimeOffset.UtcNow, "patch-group-v2");
	private static PatchAssetReference Reference(AdaptationAssetKey source, AdaptationAssetKey target, PatchReferenceKind kind) => new(source, target, kind, 0);

	// 映射事实现在由信息中心提供（设计文档 §28）。这个替身表达"游戏数据里什么都没映射到"。
	private static FakeInformationCenter CenterWithEmptyMapping(IReadOnlyDictionary<ModNodeId, IReadOnlyList<PatchGroupAnalysis>> analyses)
		=> new(analyses)
		{
			GameDataMapping = assetKeys => new GameDataMappingFacts("test", "test", "test", DateTimeOffset.UtcNow,
				assetKeys.ToDictionary(key => key, key => new GameDataMappedAssetFact(key, key.FileId.ToString(), key.TypeId.ToString(), AssetTypeCategory.Unknown, [])), []),
		};
}
