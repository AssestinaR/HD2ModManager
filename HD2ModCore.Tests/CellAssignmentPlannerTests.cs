using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

// 作用：钉住"替换护甲 = 装箱"这套语义：格子身份、桶、以及 P1~P5 的放置顺序。
// 背景与实测数据见 docs/armor-registry-page-perf-20261027.md 与 docs/armor-cell-plan-20261027.md。
public sealed class CellAssignmentPlannerTests
{
	private const int HelmetSlot = 0;
	private const int CapeSlot = 1;
	private const int TorsoSlot = 2;
	private const int HipsSlot = 3;
	private const int LeftLegSlot = 4;
	private const int RightLegSlot = 5;
	private const int LeftArmSlot = 6;
	private const int LeftShoulderSlot = 8;

	// ---- 格子身份 -----------------------------------------------------------

	[Fact]
	public void ACellMapsItsSlotToThePartCatalogue()
	{
		Assert.Equal(ArmorPart.Torso, new ArmorCell(TorsoSlot, 0).Part);
		Assert.Equal(ArmorPart.Cape, new ArmorCell(CapeSlot, 0).Part);
		Assert.Equal(ArmorPart.Helmet, new ArmorCell(HelmetSlot, 0).Part);
		Assert.Equal(ArmorPart.LeftShoulder, new ArmorCell(LeftShoulderSlot, 0).Part);
	}

	[Fact]
	public void OnlyTorsoAndHelmetAreHiddenInFirstPerson()
	{
		Assert.True(new ArmorCell(TorsoSlot, 1).IsFirstPersonHidden);
		Assert.True(new ArmorCell(HelmetSlot, 0).IsFirstPersonHidden);
		Assert.False(new ArmorCell(HipsSlot, 1).IsFirstPersonHidden);
		Assert.False(new ArmorCell(LeftArmSlot, 1).IsFirstPersonHidden);
		Assert.False(new ArmorCell(LeftShoulderSlot, 0).IsFirstPersonHidden);
	}

	[Fact]
	public void TheLayerNameIsNotTheSlotName()
	{
		// 躯干槽位 2 的三个层分别是 内层 / 护甲 / 装饰。混用两者会让 5/6/7 档错位。
		var torso = ArmorStructure.Display(ArmorPart.Torso);
		Assert.Equal($"{torso} · 内层", new ArmorCell(TorsoSlot, 0).Display);
		Assert.Equal($"{torso} · 护甲", new ArmorCell(TorsoSlot, 1).Display);
		Assert.Equal($"{torso} · 装饰", new ArmorCell(TorsoSlot, 2).Display);
		Assert.Equal("未定位", ArmorCell.Unknown.Display);
	}

	// ---- 格子表 -------------------------------------------------------------

	[Fact]
	public void TheTableGroupsPartsIntoOneSlotPerCellAndBody()
	{
		var table = ArmorCellTable.Build(
		[
			Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim),
			Part(TorsoSlot, 0, UnitMeshBodyVariant.Stocky),
			Part(TorsoSlot, 1, UnitMeshBodyVariant.Any),
		]);

		Assert.Equal(3, table.Slots.Count);
		Assert.NotNull(table.FindExact(new ArmorCell(TorsoSlot, 0), UnitMeshBodyVariant.Slim));
		Assert.NotNull(table.FindAny(new ArmorCell(TorsoSlot, 1)));
		Assert.Null(table.FindExact(new ArmorCell(TorsoSlot, 2), UnitMeshBodyVariant.Slim));
	}

	[Fact]
	public void PartsWithoutAnAuthoritativeCellAreKeptAsideInsteadOfDropped()
	{
		var stray = Part(0, 0, UnitMeshBodyVariant.Slim) with { Cell = ArmorCell.Unknown };
		var table = ArmorCellTable.Build([Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim), stray]);

		Assert.Single(table.Slots);
		var unplaced = Assert.Single(table.Unplaced);
		Assert.Equal(stray.UnitAssetKey, unplaced.UnitAssetKey);
	}

	[Fact]
	public void TwoUnitsOnTheSameCellAndBodyAreReportedRatherThanSilentlyPicked()
	{
		var first = Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim);
		var second = Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim) with { TriangleCount = 99 };
		var table = ArmorCellTable.Build([first, second]);

		Assert.Single(table.Slots);
		Assert.Single(table.Duplicates);
		// 几何更实的那个当代表。
		Assert.Equal(99, table.Slots[0].Part.TriangleCount);
	}

	// ---- P1：自然命中 / 广播 ------------------------------------------------

	[Fact]
	public void MatchingCellsHitOneToOne()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim)),
			Target(Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim);

		var assignment = Assert.Single(result.Assignments);
		Assert.Equal(CellAssignmentKind.Natural, assignment.Kind);
		Assert.Single(assignment.Sources);
		Assert.Empty(result.Unassigned);
	}

	[Fact]
	public void AUniversalSourcePartServesBothTargetBodiesWithoutBeingConsumed()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(Part(LeftLegSlot, 1, UnitMeshBodyVariant.Any)),
			Target(
				Part(LeftLegSlot, 1, UnitMeshBodyVariant.Slim),
				Part(LeftLegSlot, 1, UnitMeshBodyVariant.Stocky)),
			CrossArmorBodyVariantPreference.Slim);

		Assert.Equal(2, result.Assignments.Count);
		Assert.All(result.Assignments, assignment => Assert.Equal(CellAssignmentKind.Broadcast, assignment.Kind));
		Assert.Single(result.Assignments[0].Sources);
		Assert.Empty(result.Unassigned);
	}

	[Fact]
	public void ASourceWithOnlyOneConcreteBodyBroadcastsToTheOtherBody()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(Part(HipsSlot, 1, UnitMeshBodyVariant.Slim)),
			Target(
				Part(HipsSlot, 1, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 1, UnitMeshBodyVariant.Stocky)),
			CrossArmorBodyVariantPreference.Stocky);

		Assert.Equal(2, result.Assignments.Count);
		// 同身形的那个是精确命中，另一个是广播。
		Assert.Equal(CellAssignmentKind.Natural, result.Assignments[0].Kind);
		Assert.Equal(CellAssignmentKind.Broadcast, result.Assignments[1].Kind);
		Assert.Empty(result.Unassigned);
	}

	[Fact]
	public void APairedSourceIntoAUniversalTargetKeepsThePreferredBodyAndLeavesTheOtherAsSurplus()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim),
				Part(TorsoSlot, 1, UnitMeshBodyVariant.Stocky)),
			Target(Part(TorsoSlot, 1, UnitMeshBodyVariant.Any)),
			CrossArmorBodyVariantPreference.Stocky,
			autoHideUnmapped: true,
			allowReroute: false,
			allowMerge: false);

		var assignment = Assert.Single(result.Assignments);
		Assert.Equal(UnitMeshBodyVariant.Stocky, assignment.Sources[0].BodyVariant);
		Assert.Single(result.Unassigned);
	}

	// ---- P2 / P3：改挂 ------------------------------------------------------

	[Fact]
	public void ASurplusPartFillsAFreeBucketInTheSameSlotFirst()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 2, UnitMeshBodyVariant.Slim)),
			Target(
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 1, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim);

		Assert.Equal(2, result.Assignments.Count);
		var shifted = result.Assignments.Single(a => a.Kind == CellAssignmentKind.LayerShift);
		Assert.Equal(1, shifted.Cell.Layer);
		Assert.Empty(result.Unassigned);
	}

	[Fact]
	public void ASurplusPartLeavesTheRegionOnlyWhenNothingCloserIsFree()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 1, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 2, UnitMeshBodyVariant.Slim)),
			Target(
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 1, UnitMeshBodyVariant.Slim),
				Part(LeftLegSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim);

		var rerouted = result.Assignments.Single(a => a.Kind == CellAssignmentKind.Rerouted);
		// 胯的部件应该挂到腿，而不是被合并。
		Assert.Equal(LeftLegSlot, rerouted.Cell.Slot);
		Assert.Equal(UnitMeshBodyVariant.Slim, rerouted.Sources[0].BodyVariant);
	}

	[Fact]
	public void ATorsoSurplusPartIsNeverRerouted()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim),
				Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim),
				Part(TorsoSlot, 2, UnitMeshBodyVariant.Slim)),
			Target(
				Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim),
				Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim),
				Part(LeftLegSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim);

		// 胸部件不能外挂（第一人称会冒出来），所以腿上的空桶必须保持"没有来源"。
		Assert.DoesNotContain(result.Assignments, a => a.Cell.Slot == LeftLegSlot && !a.IsHidden);
		var merged = result.Assignments.Single(a => a.Kind == CellAssignmentKind.Merged);
		Assert.Equal(TorsoSlot, merged.Cell.Slot);
		// 往高处并：多余的第 2 层并进第 1 层，不是第 0 层。
		Assert.Equal(1, merged.Cell.Layer);
		Assert.Equal(2, merged.Sources.Count);
	}

	[Fact]
	public void ACapeIsExcludedInBothDirections()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(CapeSlot, 0, UnitMeshBodyVariant.Any),
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 1, UnitMeshBodyVariant.Slim)),
			Target(
				Part(CapeSlot, 0, UnitMeshBodyVariant.Any),
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim);

		// 目标的披风桶既不分配也不隐藏；来源的披风件不参与，也不该被算成"放不下"。
		Assert.DoesNotContain(result.Assignments, a => a.Cell.Slot == CapeSlot);
		Assert.DoesNotContain(result.Unassigned, part => part.Cell.Slot == CapeSlot);
		// 胯的第 1 层多余件没有空桶，会被合并或报出来，但绝不会落到披风槽。
		Assert.DoesNotContain(result.Assignments, a => a.Cell.Slot == CapeSlot);
	}

	// ---- P4 / P5 -----------------------------------------------------------

	[Fact]
	public void ALeftoverPartMergesIntoAnOccupiedBucketWhenNothingIsFree()
	{
		var result = CellAssignmentPlanner.Plan(
			Source(
				Part(LeftArmSlot, 0, UnitMeshBodyVariant.Slim),
				Part(LeftArmSlot, 1, UnitMeshBodyVariant.Slim)),
			Target(Part(LeftArmSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim,
			allowReroute: false);

		var merged = result.Assignments.Single(a => a.Kind == CellAssignmentKind.Merged);
		Assert.Equal(0, merged.Cell.Layer);
		Assert.Equal(2, merged.Sources.Count);
		Assert.Empty(result.Unassigned);
	}

	[Fact]
	public void AnUnmatchedTargetBucketIsHiddenOnlyWhenAutoHideIsOn()
	{
		var hidden = CellAssignmentPlanner.Plan(
			Source(Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim)),
			Target(
				Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim,
			autoHideUnmapped: true);

		Assert.Contains(hidden.Assignments, a => a.Cell.Slot == HipsSlot && a.IsHidden);

		var untouched = CellAssignmentPlanner.Plan(
			Source(Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim)),
			Target(
				Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim),
				Part(HipsSlot, 0, UnitMeshBodyVariant.Slim)),
			CrossArmorBodyVariantPreference.Slim,
			autoHideUnmapped: false);

		// 关掉自动隐藏时，未命中的桶**根本不进计划**——原版保持原样，不写 Patch。
		Assert.DoesNotContain(untouched.Assignments, a => a.Cell.Slot == HipsSlot);
	}

	[Fact]
	public void PlanningIsDeterministicForTheSameInput()
	{
		var source = Source(
			Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
			Part(HipsSlot, 2, UnitMeshBodyVariant.Slim),
			Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim));
		var target = Target(
			Part(HipsSlot, 0, UnitMeshBodyVariant.Slim),
			Part(HipsSlot, 1, UnitMeshBodyVariant.Slim),
			Part(TorsoSlot, 1, UnitMeshBodyVariant.Slim));

		var first = CellAssignmentPlanner.Plan(source, target, CrossArmorBodyVariantPreference.Slim);
		var second = CellAssignmentPlanner.Plan(source, target, CrossArmorBodyVariantPreference.Slim);

		Assert.Equal(first.Assignments.Count, second.Assignments.Count);
		for (var index = 0; index < first.Assignments.Count; index++)
		{
			Assert.Equal(first.Assignments[index].Cell, second.Assignments[index].Cell);
			Assert.Equal(first.Assignments[index].Kind, second.Assignments[index].Kind);
			Assert.Equal(
				first.Assignments[index].Sources.Select(part => part.UnitAssetKey),
				second.Assignments[index].Sources.Select(part => part.UnitAssetKey));
		}
	}

	// ---- helpers -----------------------------------------------------------

	private static ArmorCellTable Source(params EquipmentUnitPart[] parts) => ArmorCellTable.Build(parts);

	private static ArmorCellTable Target(params EquipmentUnitPart[] parts) => ArmorCellTable.Build(parts);

	private static EquipmentUnitPart Part(int slot, int layer, UnitMeshBodyVariant body)
	{
		// 用 (slot, layer, body) 编出一个稳定且唯一的 FileId，方便断言时区分部件。
		var fileId = (ulong)(slot * 1000 + layer * 10 + (int)body + 1);
		return new EquipmentUnitPart(
			new AssetKey(PatchUnitMeshReader.UnitTypeId, fileId),
			0,
			1,
			ArmorStructure.ToPartKind(ArmorStructure.FromSlot((uint)slot)),
			layer == 0 ? UnitMeshPartLayer.Undergarment : UnitMeshPartLayer.Armor,
			body,
			$"part_{slot}_{layer}_{body}",
			100,
			[])
		{
			Cell = new ArmorCell(slot, layer),
			TriangleCount = 10,
			VertexCount = 10,
			StoredBytes = 100,
			GeometryQuality = UnitMeshGeometryQuality.Renderable,
		};
	}
}
