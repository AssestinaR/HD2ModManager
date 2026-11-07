using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies archive/type based category mapping, multi-category mods and the unscanned state.
public sealed class ModCategoryClassifierTests
{
	[Fact]
	public void Classify_NullOrEmptySummary_IsNonePending()
	{
		Assert.Equal(ModCategory.None, ModCategoryClassifier.Classify(null));
		Assert.Equal(ModCategory.None, ModCategoryClassifier.Classify(Summary()));
	}

	[Theory]
	[InlineData("Armor", ModCategory.Armor)]
	[InlineData("CustomModel", ModCategory.Armor)]
	[InlineData("Helmet", ModCategory.Armor)]
	[InlineData("Cape", ModCategory.Cape)]
	[InlineData("Weapon customization", ModCategory.Weapon)]
	public void Classify_ArchiveCategoryWinsOverAssetType(string archiveCategory, ModCategory expected)
	{
		var summary = Summary(Asset(archiveCategory, AssetTypeCategory.Material));
		Assert.Equal(expected, ModCategoryClassifier.Classify(summary));
	}

	[Theory]
	[InlineData(AssetTypeCategory.Audio, ModCategory.Audio)]
	[InlineData(AssetTypeCategory.Texture, ModCategory.Visual)]
	[InlineData(AssetTypeCategory.Material, ModCategory.Visual)]
	[InlineData(AssetTypeCategory.UI, ModCategory.Ui)]
	[InlineData(AssetTypeCategory.Animation, ModCategory.Animation)]
	[InlineData(AssetTypeCategory.Unknown, ModCategory.Other)]
	public void Classify_FallsBackToAssetType(AssetTypeCategory type, ModCategory expected)
	{
		Assert.Equal(expected, ModCategoryClassifier.Classify(Summary(Asset("Unknown", type))));
	}

	[Fact]
	public void Classify_MixedContent_CombinesFlags()
	{
		var summary = Summary(Asset("Armor", AssetTypeCategory.Model), Asset("Helmet", AssetTypeCategory.Model), Asset("Unknown", AssetTypeCategory.Audio));
		var result = ModCategoryClassifier.Classify(summary);
		Assert.Equal(ModCategory.Armor, result);
	}

	[Fact]
	public void Classify_ArmorWithAudioAndEffects_IsArmorOnly()
	{
		var summary = Summary(Asset("Armor", AssetTypeCategory.Model), Asset("Unknown", AssetTypeCategory.Audio), Asset("Unknown", AssetTypeCategory.Animation), Asset("Unknown", AssetTypeCategory.Unknown));
		Assert.Equal(ModCategory.Armor, ModCategoryClassifier.Classify(summary));
	}

	[Fact]
	public void Classify_WeaponBeatsNonClothingButLosesToClothing()
	{
		Assert.Equal(ModCategory.Weapon, ModCategoryClassifier.Classify(Summary(Asset("Weapon", AssetTypeCategory.Model), Asset("Unknown", AssetTypeCategory.Audio))));
		Assert.Equal(ModCategory.Cape, ModCategoryClassifier.Classify(Summary(Asset("Weapon", AssetTypeCategory.Model), Asset("Cape", AssetTypeCategory.Model))));
	}

	[Fact]
	public void Classify_UnrecognizedAssetsDoNotDiluteRealCategory()
	{
		var summary = Summary(Asset("Unknown", AssetTypeCategory.Audio), Asset("Unknown", AssetTypeCategory.Unknown));
		Assert.Equal(ModCategory.Audio, ModCategoryClassifier.Classify(summary));
	}

	private static PatchAssetEntry Asset(string archiveCategory, AssetTypeCategory type)
		=> new(new PatchAssetKey("archive", 1, 2), "a", archiveCategory, 0, 0, "f", "t", type, Array.Empty<string>(), Array.Empty<string>());

	private static ModAssetSummary Summary(params PatchAssetEntry[] assets)
		=> new(ModNodeId.New(), "Mod", assets, Array.Empty<string>(), Array.Empty<ModAssetTargetGroup>());
}
