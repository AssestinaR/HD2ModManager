using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

// Purpose: Verifies cross-armor planning follows the maximum-size source and explicit manual-mode rules.
public sealed class EquipmentUnitCatalogServiceTests
{
	private static readonly AssetKey SourceUnit = new(0xe0a48d0be9a7453f, 0x100);
	private static readonly AssetKey AdditionalSourceUnit = new(0xe0a48d0be9a7453f, 0x101);
	private static readonly AssetKey TargetUnit = new(0xe0a48d0be9a7453f, 0x200);

	[Fact]
	public async Task CreatePlanAsync_AutomaticModeChoosesLargestSourceOfMatchingPart()
	{
		var smaller = Part(SourceUnit, 1, "small") with { StoredBytes = 64 };
		var larger = Part(SourceUnit, 2, "large") with { StoredBytes = 1024 };
		var target = Part(TargetUnit, 3, "target");

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", smaller, larger)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		var mapping = Assert.Single(plan.Mappings);
		Assert.Equal(larger, mapping.Source);
		Assert.Equal("命中", mapping.Reason);
	}

	[Fact]
	public async Task CreatePlanAsync_AutomaticModeMatchesSlimSourceToSlimTarget()
	{
		var slim = Part(SourceUnit, 1, "slim") with { BodyVariant = UnitMeshBodyVariant.Slim, StoredBytes = 64 };
		var stocky = Part(SourceUnit, 2, "stocky") with { BodyVariant = UnitMeshBodyVariant.Stocky, StoredBytes = 1024 };
		var target = Part(TargetUnit, 3, "slim-target") with { BodyVariant = UnitMeshBodyVariant.Slim };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slim, stocky)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Stocky, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(slim, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_AutomaticModeMatchesStockySourceToStockyTarget()
	{
		var slim = Part(SourceUnit, 1, "slim") with { BodyVariant = UnitMeshBodyVariant.Slim, StoredBytes = 1024 };
		var stocky = Part(SourceUnit, 2, "stocky") with { BodyVariant = UnitMeshBodyVariant.Stocky, StoredBytes = 64 };
		var target = Part(TargetUnit, 3, "stocky-target") with { BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slim, stocky)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(stocky, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_AutomaticModeAllowsAnySourceForEitherTargetBodyVariant()
	{
		var any = Part(SourceUnit, 1, "any") with { BodyVariant = UnitMeshBodyVariant.Any };
		var target = Part(TargetUnit, 2, "stocky-target") with { BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", any)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(any, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_TargetAnyUsesPreferredSourceVariantConsistently()
	{
		var slim = Part(SourceUnit, 1, "slim") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim, StoredBytes = 64 };
		var stocky = Part(SourceUnit, 2, "stocky") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky, StoredBytes = 1024 };
		var target = Part(TargetUnit, 3, "any-target") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slim, stocky)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(slim, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_SameLayerPairWinsOverLargerAnyTarget()
	{
		var slimSource = Part(SourceUnit, 1, "slim") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockySource = Part(AdditionalSourceUnit, 2, "stocky") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };
		var anyTarget = Part(TargetUnit, 3, "any") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any, StoredBytes = 4096 };
		var slimTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 4, "slim") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim, StoredBytes = 1024 };
		var stockyTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 5, "stocky") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky, StoredBytes = 1024 };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimSource, stockySource)], [Entry("target", anyTarget, slimTarget, stockyTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.WillReplace));
		Assert.Null(plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == anyTarget.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_RealAnySourceDoesNotFixConcreteTargetVariants()
	{
		var anyArm = Part(SourceUnit, 1, "any-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };
		var slimChest = Part(new AssetKey(SourceUnit.TypeId, 0x102), 3, "slim-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyChest = Part(new AssetKey(SourceUnit.TypeId, 0x103), 4, "stocky-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };
		var anyArmTarget = Part(TargetUnit, 5, "any-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };
		var slimChestTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 6, "slim-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyChestTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 7, "stocky-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", anyArm, slimChest, stockyChest)],
			[Entry("target", anyArmTarget, slimChestTarget, stockyChestTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(anyArm, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == anyArmTarget.UnitAssetKey).Source);
		Assert.Equal(slimChest, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == slimChestTarget.UnitAssetKey).Source);
		Assert.Equal(stockyChest, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == stockyChestTarget.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_ConcreteSourceForAnyTargetFixesTheWholeArchiveVariant()
	{
		var slimTorso = Part(SourceUnit, 1, "slim-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyTorso = Part(AdditionalSourceUnit, 2, "stocky-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };
		var slimArm = Part(new AssetKey(SourceUnit.TypeId, 0x102), 3, "slim-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyArm = Part(new AssetKey(SourceUnit.TypeId, 0x103), 4, "stocky-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };
		var anyTorsoTarget = Part(TargetUnit, 5, "any-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Any };
		var slimArmTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 6, "slim-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyArmTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 7, "stocky-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimTorso, stockyTorso, slimArm, stockyArm)],
			[Entry("target", anyTorsoTarget, slimArmTarget, stockyArmTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(slimTorso, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == anyTorsoTarget.UnitAssetKey).Source);
		Assert.Equal(slimArm, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == slimArmTarget.UnitAssetKey).Source);
		Assert.Equal(slimArm, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == stockyArmTarget.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_ConcreteSourceForAnyTargetFixesSharedPhysicalTargets()
	{
		var slimTorso = Part(SourceUnit, 1, "slim-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyTorso = Part(AdditionalSourceUnit, 2, "stocky-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };
		var slimArm = Part(new AssetKey(SourceUnit.TypeId, 0x102), 3, "slim-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyArm = Part(new AssetKey(SourceUnit.TypeId, 0x103), 4, "stocky-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };
		var anyTorsoTarget = Part(TargetUnit, 5, "any-torso") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Any };
		var sharedSlimArmTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 6, "shared-slim-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var sharedStockyArmTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 7, "shared-stocky-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimTorso, stockyTorso, slimArm, stockyArm)],
			[Entry("target-1", anyTorsoTarget, sharedSlimArmTarget, sharedStockyArmTarget), Entry("target-2", sharedSlimArmTarget, sharedStockyArmTarget)],
			"source", UnitMeshBodyVariant.Any, CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target-1", "target-2"]);

		var sharedStockyMapping = plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == sharedStockyArmTarget.UnitAssetKey);
		Assert.Equal(2, sharedStockyMapping.UsedByArchiveIds.Count);
		Assert.Equal(slimArm, sharedStockyMapping.Source);
	}

	[Fact]
	public async Task CreatePlanAsync_TargetAnyKeepsUniversalSourceWithoutForcingTheArmorFallback()
	{
		var universalArm = Part(SourceUnit, 1, "any-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };
		var slimChest = Part(SourceUnit, 2, "slim-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyChest = Part(SourceUnit, 3, "stocky-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };
		var anyArm = Part(TargetUnit, 4, "any-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };
		var targetStockyChest = Part(new AssetKey(TargetUnit.TypeId, 0x201), 5, "stocky-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", universalArm, slimChest, stockyChest)], [Entry("target", anyArm, targetStockyChest)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(universalArm, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == TargetUnit).Source);
		Assert.Equal(stockyChest, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == targetStockyChest.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_UnmatchedTargetAnyDoesNotForceFallbackForTheArmor()
	{
		var slim = Part(SourceUnit, 1, "slim") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var matchingTarget = Part(TargetUnit, 2, "slim-target") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var unmatchedAny = Part(new AssetKey(TargetUnit.TypeId, 0x201), 3, "any-target") with { PartKind = UnitMeshPartKind.RightLeg, BodyVariant = UnitMeshBodyVariant.Any };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slim)], [Entry("target", matchingTarget, unmatchedAny)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Stocky, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(slim, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == TargetUnit).Source);
		Assert.Null(plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == unmatchedAny.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_SourceAnyCanFillBothTargetVariants()
	{
		var source = Part(SourceUnit, 1, "any") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Any };
		var slim = Part(TargetUnit, 2, "slim-target") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stocky = Part(new AssetKey(TargetUnit.TypeId, 0x201), 3, "stocky-target") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", slim, stocky)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.Source == source));
	}

	[Fact]
	public async Task CreatePlanAsync_SingleConcreteSourceRemainsEffectiveAny()
	{
		var source = Part(SourceUnit, 1, "g_arm_l") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Stocky
		};
		var slim = Part(TargetUnit, 2, "g_arm_l") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Slim
		};
		var stocky = Part(new AssetKey(TargetUnit.TypeId, 0x201), 3, "g_arm_l") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Stocky
		};

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", slim, stocky)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(source, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == slim.UnitAssetKey).Source);
		Assert.Equal(source, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == stocky.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_SeparatesUniversalArmFromConcreteTorsoSleeve()
	{
		var stockySleeveSource = Part(SourceUnit, 1, "g_torso_arm_l_male") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Stocky,
			StoredBytes = 1024
		};
		var universalArmSource = Part(AdditionalSourceUnit, 2, "g_arm_l") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Any,
			StoredBytes = 2048
		};
		var stockySleeveTarget = Part(TargetUnit, 3, "g_torso_arm_l_male") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Stocky
		};
		var slimSleeveTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 4, "g_torso_arm_l_female") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Slim
		};
		var universalArmTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 5, "g_arm_l") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			BodyVariant = UnitMeshBodyVariant.Any
		};

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", stockySleeveSource, universalArmSource)],
			[Entry("target", stockySleeveTarget, slimSleeveTarget, universalArmTarget)],
			"source", UnitMeshBodyVariant.Any, CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(stockySleeveSource, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == stockySleeveTarget.UnitAssetKey).Source);
		Assert.Equal(universalArmSource, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == universalArmTarget.UnitAssetKey).Source);
		Assert.Null(plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == slimSleeveTarget.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_TorsoSourceUnitsAreNotExpandedIntoExtraTorsoHits()
	{
		var slimSource = Part(SourceUnit, 1, "slim-torso") with
		{
			PartKind = UnitMeshPartKind.Torso,
			Layer = UnitMeshPartLayer.Undergarment,
			BodyVariant = UnitMeshBodyVariant.Slim
		};
		var stockySource = Part(AdditionalSourceUnit, 2, "stocky-torso") with
		{
			PartKind = UnitMeshPartKind.Torso,
			Layer = UnitMeshPartLayer.Undergarment,
			BodyVariant = UnitMeshBodyVariant.Stocky
		};
		var targetParts = new[]
		{
			Part(new AssetKey(TargetUnit.TypeId, 0x200), 3, "slim-torso") with { PartKind = UnitMeshPartKind.Torso, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Slim },
			Part(new AssetKey(TargetUnit.TypeId, 0x201), 4, "stocky-torso") with { PartKind = UnitMeshPartKind.Torso, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Stocky },
			Part(new AssetKey(TargetUnit.TypeId, 0x202), 5, "slim-accessory") with { PartKind = UnitMeshPartKind.Torso, Layer = UnitMeshPartLayer.Accessory, BodyVariant = UnitMeshBodyVariant.Slim },
			Part(new AssetKey(TargetUnit.TypeId, 0x203), 6, "stocky-accessory") with { PartKind = UnitMeshPartKind.Torso, Layer = UnitMeshPartLayer.Accessory, BodyVariant = UnitMeshBodyVariant.Stocky }
		};

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimSource, stockySource)], [Entry("target", targetParts)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.WillReplace && mapping.Target.PartKind == UnitMeshPartKind.Torso));
	}

	[Fact]
	public async Task CreatePlanAsync_SameLayerPartialShapeIsCompletedFromAnotherLayer()
	{
		var slimSource = Part(SourceUnit, 1, "slim") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			Layer = UnitMeshPartLayer.Undergarment,
			BodyVariant = UnitMeshBodyVariant.Slim,
			StoredBytes = 1024
		};
		var stockySource = Part(AdditionalSourceUnit, 2, "stocky") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			Layer = UnitMeshPartLayer.Undergarment,
			BodyVariant = UnitMeshBodyVariant.Stocky,
			StoredBytes = 1024
		};
		var stockyTarget = Part(TargetUnit, 3, "stocky-target") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			Layer = UnitMeshPartLayer.Undergarment,
			BodyVariant = UnitMeshBodyVariant.Stocky,
			StoredBytes = 2048
		};
		var slimTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 4, "slim-target") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			Layer = UnitMeshPartLayer.Armor,
			BodyVariant = UnitMeshBodyVariant.Slim,
			StoredBytes = 2000
		};
		var anyTarget = Part(new AssetKey(TargetUnit.TypeId, 0x202), 5, "any-target") with
		{
			PartKind = UnitMeshPartKind.LeftArm,
			Layer = UnitMeshPartLayer.Armor,
			BodyVariant = UnitMeshBodyVariant.Any,
			StoredBytes = 500
		};

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimSource, stockySource)],
			[Entry("target", stockyTarget, slimTarget, anyTarget)],
			"source", UnitMeshBodyVariant.Any, CrossArmorBodyVariantPreference.Slim,
			CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.WillReplace));
		Assert.Equal(stockySource, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == stockyTarget.UnitAssetKey).Source);
		Assert.Equal(slimSource, plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == slimTarget.UnitAssetKey).Source);
		Assert.Null(plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == anyTarget.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_SourceMeshCanBeReusedForEveryCompatibleTarget()
	{
		var smaller = Part(SourceUnit, 1, "small") with { StoredBytes = 64 };
		var larger = Part(SourceUnit, 2, "large") with { StoredBytes = 1024 };
		var largeTarget = Part(TargetUnit, 3, "large-target") with { StoredBytes = 2048 };
		var smallTarget = Part(TargetUnit, 4, "small-target") with { StoredBytes = 128 };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", smaller, larger)], [Entry("target", largeTarget, smallTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"]);

		Assert.Single(plan.Mappings);
		Assert.Equal(larger, plan.Mappings.Single().Source);
	}

	[Fact]
	public async Task CreatePlanAsync_ExactLayerExcludesLargerFallbackLayer()
	{
		var exact = Part(SourceUnit, 1, "armor") with { Layer = UnitMeshPartLayer.Armor, StoredBytes = 64 };
		var fallback = Part(SourceUnit, 2, "undergarment") with { Layer = UnitMeshPartLayer.Undergarment, StoredBytes = 4096 };
		var target = Part(TargetUnit, 3, "target") with { Layer = UnitMeshPartLayer.Armor };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", exact, fallback)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Undergarment, ["target"]);

		Assert.Equal(exact, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_MissingExactLayerUsesLargestOtherLayer()
	{
		var smallerPreferred = Part(SourceUnit, 1, "undergarment") with { Layer = UnitMeshPartLayer.Undergarment, StoredBytes = 64 };
		var largerOther = Part(SourceUnit, 2, "accessory") with { Layer = UnitMeshPartLayer.Accessory, StoredBytes = 4096 };
		var target = Part(TargetUnit, 3, "target") with { Layer = UnitMeshPartLayer.Armor };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", smallerPreferred, largerOther)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Undergarment, ["target"]);

		Assert.Equal(largerOther, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_SharedVariantConflictFlipsEveryMappingOfMinorityArmor()
	{
		var slimArm = Part(SourceUnit, 1, "slim-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyArm = Part(SourceUnit, 2, "stocky-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Stocky };
		var slimChest = Part(SourceUnit, 3, "slim-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockyChest = Part(SourceUnit, 4, "stocky-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };
		var sharedArm = Part(TargetUnit, 5, "shared-arm") with { PartKind = UnitMeshPartKind.LeftArm, BodyVariant = UnitMeshBodyVariant.Slim };
		var firstChest = Part(new AssetKey(TargetUnit.TypeId, 0x201), 6, "first-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Slim };
		var secondChest = Part(new AssetKey(TargetUnit.TypeId, 0x202), 7, "second-chest") with { PartKind = UnitMeshPartKind.Torso, BodyVariant = UnitMeshBodyVariant.Stocky };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimArm, stockyArm, slimChest, stockyChest)],
			[Entry("target-1", sharedArm, firstChest), Entry("target-2", sharedArm with { BodyVariant = UnitMeshBodyVariant.Stocky }, secondChest)],
			"source", UnitMeshBodyVariant.Any, CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target-1", "target-2"]);

		Assert.Equal(slimArm, plan.Mappings.Single(mapping => mapping.Target.MeshInfoIndex == 5).Source);
		Assert.Equal(slimChest, plan.Mappings.Single(mapping => mapping.Target.MeshInfoIndex == 7).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_EachSelectedArmorAddsOneBaseHitPerPart()
	{
		var source = Part(SourceUnit, 1, "arm") with { PartKind = UnitMeshPartKind.LeftArm, StoredBytes = 1024 };
		var firstTarget = Part(TargetUnit, 2, "first") with { PartKind = UnitMeshPartKind.LeftArm, StoredBytes = 2048 };
		var secondTarget = Part(new AssetKey(TargetUnit.TypeId, 0x201), 3, "second") with { PartKind = UnitMeshPartKind.LeftArm, StoredBytes = 1024 };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target-1", firstTarget), Entry("target-2", secondTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target-1", "target-2"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.WillReplace));
	}

	[Fact]
	public async Task CreatePlanAsync_SharedPhysicalTargetConsumesEveryAvailableTargetHit()
	{
		var source = Part(SourceUnit, 1, "source") with { StoredBytes = 1024 };
		var sharedTarget = Part(TargetUnit, 2, "shared") with { StoredBytes = 2048 };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target-1", sharedTarget), Entry("target-2", sharedTarget)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target-1", "target-2"]);

		var mapping = Assert.Single(plan.Mappings);
		Assert.Equal(2, mapping.UsedByArchiveIds.Count);
		Assert.Equal(2, mapping.HitCount);
	}

	[Fact]
	public async Task CreatePlanAsync_SharedSlimStockyPairPreventsAdditionalAnyTargetInLaterArchive()
	{
		var slimSource = Part(SourceUnit, 1, "slim-source") with { PartKind = UnitMeshPartKind.LeftArm, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Slim };
		var stockySource = Part(AdditionalSourceUnit, 2, "stocky-source") with { PartKind = UnitMeshPartKind.LeftArm, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Stocky };
		var sharedSlim = Part(TargetUnit, 3, "shared-slim") with { PartKind = UnitMeshPartKind.LeftArm, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Slim };
		var sharedStocky = Part(new AssetKey(TargetUnit.TypeId, 0x201), 4, "shared-stocky") with { PartKind = UnitMeshPartKind.LeftArm, Layer = UnitMeshPartLayer.Undergarment, BodyVariant = UnitMeshBodyVariant.Stocky };
		var secondArchiveAny = Part(new AssetKey(TargetUnit.TypeId, 0x202), 5, "second-any") with { PartKind = UnitMeshPartKind.LeftArm, Layer = UnitMeshPartLayer.Armor, BodyVariant = UnitMeshBodyVariant.Any };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", slimSource, stockySource)],
			[Entry("target-1", sharedSlim, sharedStocky), Entry("target-2", sharedSlim, sharedStocky, secondArchiveAny)],
			"source", UnitMeshBodyVariant.Any, CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target-1", "target-2"]);

		Assert.Equal(2, plan.Mappings.Count(mapping => mapping.WillReplace && mapping.Target.PartKind == UnitMeshPartKind.LeftArm));
		Assert.Null(plan.Mappings.Single(mapping => mapping.Target.UnitAssetKey == secondArchiveAny.UnitAssetKey).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_ManualModeDoesNotCreateAutomaticMapping()
	{
		var source = Part(SourceUnit, 1, "source") with { StoredBytes = 1024 };
		var target = Part(TargetUnit, 2, "target");

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"], manualMode: true);

		var mapping = Assert.Single(plan.Mappings);
		Assert.Null(mapping.Source);
		Assert.Equal("隐藏", mapping.Reason);
	}

	[Fact]
	public async Task CreatePlanAsync_ManualMappingProducesForcedHit()
	{
		var source = Part(SourceUnit, 1, "source");
		var target = Part(TargetUnit, 2, "target");

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"],
			manualMappings: [new CrossArmorManualMapping(new CrossArmorPhysicalTargetKey(TargetUnit), SourceUnit)], manualMode: true);

		var mapping = Assert.Single(plan.Mappings);
		Assert.Equal(source, mapping.Source);
		Assert.True(mapping.IsManual);
		Assert.Equal("强制命中", mapping.Reason);
	}

	[Fact]
	public async Task CreatePlanAsync_ManualMappingAllowsDifferentPart()
	{
		var source = Part(SourceUnit, 1, "left-arm-source") with { PartKind = UnitMeshPartKind.LeftArm };
		var target = Part(TargetUnit, 2, "right-leg-target") with { PartKind = UnitMeshPartKind.RightLeg };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"],
			manualMappings: [new CrossArmorManualMapping(new CrossArmorPhysicalTargetKey(TargetUnit), SourceUnit)], manualMode: true);

		var mapping = Assert.Single(plan.Mappings);
		Assert.Equal(source, mapping.Source);
		Assert.True(mapping.IsManual);
		Assert.Equal("强制命中", mapping.Reason);
	}

	[Fact]
	public async Task CreatePlanAsync_AdditionalSelectedSourceIsIncluded()
	{
		var armorSource = Part(SourceUnit, 1, "armor") with { PartKind = UnitMeshPartKind.LeftArm, StoredBytes = 1024 };
		var helmetSource = Part(AdditionalSourceUnit, 2, "helmet") with { PartKind = UnitMeshPartKind.RightLeg, StoredBytes = 2048 };
		var target = Part(TargetUnit, 3, "target") with { PartKind = UnitMeshPartKind.RightLeg };

		var plan = await CreateService().CreatePlanAsync(
			[Entry("armor", armorSource), Entry("helmet", helmetSource)], [Entry("target", target)], "armor", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"], additionalSourceArchiveIds: ["helmet"]);

		Assert.Equal(helmetSource, Assert.Single(plan.Mappings).Source);
	}

	[Fact]
	public async Task CreatePlanAsync_ManualSuppressionProducesForcedHide()
	{
		var source = Part(SourceUnit, 1, "source");
		var target = Part(TargetUnit, 2, "target");

		var plan = await CreateService().CreatePlanAsync(
			[Entry("source", source)], [Entry("target", target)], "source", UnitMeshBodyVariant.Any,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, ["target"],
			manualSuppressions: [new CrossArmorManualSuppression(new CrossArmorPhysicalTargetKey(TargetUnit))]);

		var mapping = Assert.Single(plan.Mappings);
		Assert.True(mapping.IsSuppressed);
		Assert.Equal("强制隐藏", mapping.Reason);
	}

	private static EquipmentUnitCatalogService CreateService() => new(new StoragePaths(Path.GetTempPath()));
	[Fact]
	public async Task CustomTargets_ReuseAnyAcrossShapesButNotExtraOverlappingSlots()
	{
		var first = Part(SourceUnit, 1, "unrelated-source-family") with { BodyVariant = UnitMeshBodyVariant.Any, StoredBytes = 1024 };
		var second = Part(AdditionalSourceUnit, 2, "extra-family") with { BodyVariant = UnitMeshBodyVariant.Any };
		var stocky = Part(TargetUnit, 0, "virtual") with { BodyVariant = UnitMeshBodyVariant.Stocky, PieceType = "0" };
		var slim = stocky with { UnitAssetKey = new(TargetUnit.TypeId, 0x201), BodyVariant = UnitMeshBodyVariant.Slim };
		var extra = stocky with { UnitAssetKey = new(TargetUnit.TypeId, 0x202), BodyVariant = UnitMeshBodyVariant.Any, PieceType = "1" };
		var custom = Entry("custom-model:test", stocky, slim, extra) with { CustomModelKey = "test", Category = "CustomModel" };
		var plan = await CreateService().CreatePlanAsync([Entry("source", first, second)], [custom],
			"source", null, CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, [custom.ArchiveId], writePlannerDebugLog: false);
		Assert.True(plan.CanContinue);
		Assert.Equal(3, plan.Mappings.Count);
		Assert.All(plan.Mappings.Take(2), mapping => Assert.Equal(SourceUnit, mapping.Source!.UnitAssetKey));
		Assert.Equal(AdditionalSourceUnit, plan.Mappings.Last().Source!.UnitAssetKey);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CustomTargets_MissingOrSuppressedComponentsBlockOutput(bool suppressed)
	{
		var source = Part(SourceUnit, 1, "source");
		var target = Part(TargetUnit, 0, "virtual") with { PartKind = suppressed ? source.PartKind : UnitMeshPartKind.Head };
		var custom = Entry("custom-model:test", target) with { CustomModelKey = "test" };
		var plan = await CreateService().CreatePlanAsync([Entry("source", source)], [custom], "source", null,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, [custom.ArchiveId],
			manualSuppressions: suppressed ? [new(new(TargetUnit))] : [], writePlannerDebugLog: false);
		Assert.False(plan.CanContinue);
		Assert.Contains(plan.Issues, issue => issue.Code == "CustomModelComponentUnassigned");
	}

	[Fact]
	public async Task CustomTargets_CollisionWithUnselectedVanillaTargetBlocksOutput()
	{
		var source = Part(SourceUnit, 1, "source");
		var target = Part(TargetUnit, 0, "virtual");
		var custom = Entry("custom-model:test", target) with { CustomModelKey = "test" };
		var plan = await CreateService().CreatePlanAsync([Entry("source", source)], [custom, Entry("vanilla", target)], "source", null,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, [custom.ArchiveId], writePlannerDebugLog: false);
		Assert.False(plan.CanContinue);
		Assert.Contains(plan.Issues, issue => issue.Code == "CustomModelTargetInvalid");
	}

	[Fact]
	public async Task CustomTargets_ManualSameRegionAssignmentAndVanillaPlanningCoexist()
	{
		var first = Part(SourceUnit, 1, "source") with { StoredBytes = 1024 };
		var second = Part(AdditionalSourceUnit, 2, "extra");
		var target = Part(new(TargetUnit.TypeId, 0xfedcba9876543210), 0, "virtual");
		var custom = Entry("custom-model:test", target) with { CustomModelKey = "test" };
		var vanilla = Entry("vanilla", Part(TargetUnit, 0, "target"));
		var plan = await CreateService().CreatePlanAsync([Entry("source", first, second)], [custom, vanilla], "source", null,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, [custom.ArchiveId, vanilla.ArchiveId],
			manualMappings: [new(new(target.UnitAssetKey), AdditionalSourceUnit)], writePlannerDebugLog: false);
		Assert.True(plan.CanContinue);
		Assert.Equal(2, plan.Mappings.Count);
		var mapped = Assert.Single(plan.Mappings, m => m.Target.UnitAssetKey == target.UnitAssetKey);
		Assert.True(mapped.IsManual);
		Assert.Equal(AdditionalSourceUnit, mapped.Source!.UnitAssetKey);
	}
	private static EquipmentUnitCatalogEntry Entry(string id, params EquipmentUnitPart[] parts) => new(id, "Armor", id, parts);
	private static EquipmentUnitPart Part(AssetKey unit, int mesh, string name)
		=> new(unit, mesh, checked((uint)mesh), UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Slim, name, 100, Array.Empty<string>());
}
