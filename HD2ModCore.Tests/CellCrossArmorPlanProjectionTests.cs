using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：钉住"按装备族拆分来源"这条护栏。
// 现场：A/B 第一次跑时，头盔目标只有一个桶，护甲来源的其余部件被全部合并进那个头盔 unit，
// 结果是"头盔吞下 8 个身体部件"。原因是来源表没有按 护甲/头盔 拆开。
public sealed class CellCrossArmorPlanProjectionTests
{
	private const int HelmetSlot = 0;
	private const int TorsoSlot = 2;
	private const int HipsSlot = 3;

	[Fact]
	public void AHelmetTargetOnlyReceivesHelmetPartsEvenWhenTheSourceAlsoHasArmourParts()
	{
		var helmetSource = Part(HelmetSlot, 0, UnitMeshBodyVariant.Any);
		var torsoSource = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim);
		var hipsSource = Part(HipsSlot, 0, UnitMeshBodyVariant.Slim);
		var helmetTarget = Part(HelmetSlot, 0, UnitMeshBodyVariant.Any, target: true);

		var result = CellCrossArmorPlanProjection.Build(
			[helmetSource, torsoSource, hipsSource],
			[Entry("helmet", "Helmet", helmetTarget)],
			CrossArmorBodyVariantPreference.Slim,
			autoHideUnmapped: true,
			[],
			[],
			manualMode: false,
			new List<string>());

		var mapping = Assert.Single(result.Mappings);
		Assert.True(mapping.WillReplace);
		Assert.Equal("命中", mapping.Reason);
		Assert.Equal(helmetSource.UnitAssetKey, mapping.Source!.UnitAssetKey);
		// 身体部件绝不能被合并进头盔 unit。
		Assert.Empty(mapping.ExtraSources);
	}

	[Fact]
	public void AnArmourTargetReceivesOnlyArmourParts()
	{
		var helmetSource = Part(HelmetSlot, 0, UnitMeshBodyVariant.Any);
		var torsoSource = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim);
		var torsoTarget = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim, target: true);

		var result = CellCrossArmorPlanProjection.Build(
			[helmetSource, torsoSource],
			[Entry("armour", "Armor", torsoTarget)],
			CrossArmorBodyVariantPreference.Slim,
			autoHideUnmapped: true,
			[],
			[],
			manualMode: false,
			new List<string>());

		var mapping = Assert.Single(result.Mappings);
		Assert.Equal(torsoSource.UnitAssetKey, mapping.Source!.UnitAssetKey);
	}

	[Fact]
	public void ASourcePartIsReusedAcrossDifferentTargetKitsButOnlyOnceInsideOneKit()
	{
		// 同一件目标护甲里一个来源部件只能用一次；换一件护甲可以再用一次——那是另一份输出 Unit。
		var torsoSource = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim);
		var firstTarget = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim, target: true, variant: 1);
		var secondTarget = Part(TorsoSlot, 0, UnitMeshBodyVariant.Slim, target: true, variant: 2);

		var result = CellCrossArmorPlanProjection.Build(
			[torsoSource],
			[
				Entry("armour-a", "Armor", firstTarget),
				Entry("armour-b", "Armor", secondTarget),
			],
			CrossArmorBodyVariantPreference.Slim,
			autoHideUnmapped: true,
			[],
			[],
			manualMode: false,
			new List<string>());

		Assert.Equal(2, result.Mappings.Count);
		Assert.All(result.Mappings, mapping => Assert.Equal(torsoSource.UnitAssetKey, mapping.Source!.UnitAssetKey));
	}

	private static EquipmentUnitCatalogEntry Entry(string archiveId, string category, params EquipmentUnitPart[] parts)
		=> new(archiveId, category, archiveId, parts);

	private static EquipmentUnitPart Part(int slot, int layer, UnitMeshBodyVariant body, bool target = false, int variant = 0)
	{
		// 目标与来源用不同的 FileId 段，variant 用来区分"不同护甲各自的同名部件"。
		var fileId = (ulong)((target ? 5000 : 0) + variant * 1000 + slot * 100 + layer * 10 + (int)body + 1);
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
