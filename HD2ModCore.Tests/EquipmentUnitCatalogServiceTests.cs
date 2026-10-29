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
		var target = Part(TargetUnit, 0, "virtual") with { PartKind = suppressed ? source.PartKind : UnitMeshPartKind.Helmet };
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
	[Fact]
	public async Task CustomSource_PiecesCarryTheirDeclaredCellAndCanBePacked()
	{
		// 自定义素体没有 GameData 归档，但它的组件声明了 (部位, 层)——那正是权威格子。
		// 补上 Cell 之后它必须能进 SourceTableFor 的护甲族、有格子可装箱。
		// 注意：这是规划器层面的不变量；虚拟 unit 能不能真的从 Patch 里读出几何，
		// 还取决于 PatchUnitDependencyPolicy（Composite 必须在同一个 Patch 里）。
		var torsoSlot = ArmorStructure.SlotOf(ArmorPart.Torso)!.Value;
		var undergarment = Part(new(SourceUnit.TypeId, 0x301), 0, "custom_torso_0") with
		{
			PartKind = UnitMeshPartKind.Torso,
			Layer = UnitMeshPartLayer.Undergarment,
			Cell = new ArmorCell((int)torsoSlot, 0),
		};
		var armor = undergarment with
		{
			UnitAssetKey = new(SourceUnit.TypeId, 0x302),
			MeshInfoIndex = 1,
			Layer = UnitMeshPartLayer.Armor,
			Cell = new ArmorCell((int)torsoSlot, 1),
		};
		var source = Entry("custom-model:test", undergarment, armor) with { CustomModelKey = "test", Category = "CustomModel" };
		var target = Entry("target", Part(TargetUnit, 0, "target_torso") with
		{
			PartKind = UnitMeshPartKind.Torso,
			Layer = UnitMeshPartLayer.Armor,
			Cell = new ArmorCell((int)torsoSlot, 1),
		});

		var plan = await CreateService().CreatePlanAsync([source], [target], source.ArchiveId, null,
			CrossArmorBodyVariantPreference.Slim, CrossArmorLayerPreference.Armor, [target.ArchiveId], writePlannerDebugLog: false);

		Assert.True(plan.CanContinue);
		var mapping = Assert.Single(plan.Mappings);
		Assert.Equal(armor.UnitAssetKey, mapping.Source!.UnitAssetKey);
		Assert.True(mapping.WillReplace);
	}

	private static EquipmentUnitCatalogEntry Entry(string id, params EquipmentUnitPart[] parts) => new(id, "Armor", id, parts);

	// 装箱规划器只认格子（Cell），不认 PartKind/Layer。合成部件也必须带格子，否则等于
	// 「没有可放置的格子」。这里按 ArmorStructure 的约定把 (部位, 层) 折成 (slot, piece_type)；
	// 用例若把 PartKind/Layer 改成别的部位，记得连 Cell 一起改（用 CellOf 即可）。
	private static ArmorCell CellOf(UnitMeshPartKind kind, UnitMeshPartLayer layer)
	{
		var slot = ArmorStructure.SlotOf(ArmorStructure.FromPartKind(kind));
		var pieceType = layer switch
		{
			UnitMeshPartLayer.Undergarment => 0,
			UnitMeshPartLayer.Armor => 1,
			UnitMeshPartLayer.Accessory => 2,
			_ => -1,
		};
		return slot is { } known ? ArmorCell.FromRegistryCoordinates((int)known, pieceType) : ArmorCell.Unknown;
	}

	private static EquipmentUnitPart Part(AssetKey unit, int mesh, string name)
		=> new(unit, mesh, checked((uint)mesh), UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Slim, name, 100, Array.Empty<string>())
		{
			Cell = CellOf(UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Armor),
		};
}
