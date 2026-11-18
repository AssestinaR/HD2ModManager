using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class VpkRebuildGuardTests
{
	private static WardrobeSectionMapDocument BuildDocument(
		params (string UnitId, (string SourceMaterial, string MaterialId)[] Sections)[] units)
	{
		var mapUnits = units.Select(unit =>
		{
			var entries = unit.Sections
				.Select((section, index) => new WardrobeSectionMapEntry(
					index,
					section.MaterialId,
					10,
					$"{section.MaterialId}:10",
					[new WardrobeSectionToggle(0, 0, index, "0x100", 30)])
				{
					SourceMaterial = section.SourceMaterial,
				})
				.ToArray();
			return new WardrobeSectionMapUnit(
				unit.UnitId, "Torso", "Undergarment", "Any", "Light", entries.Length, true, entries);
		}).ToArray();

		return new WardrobeSectionMapDocument(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("patch_0", "00", 128, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			mapUnits);
	}

	[Fact]
	public void DroppingDefaultOffBodyGroupSectionsDoesNotTriggerFalseMaterialCollapse()
	{
		var previous = BuildDocument(
			("0001", [("hair", "0x01"), ("skin", "0x02"), ("faceeffect_excited_1", "0xfe1"), ("faceeffect_excited_2", "0xfe2")]),
			("0002", [("clothes_1", "0x03"), ("clothes_2", "0x04")]));
		var fresh = BuildDocument(
			("0001", [("hair", "0x01"), ("skin", "0x02")]),
			("0002", [("clothes_1", "0x03"), ("clothes_2", "0x04")]));

		Assert.Equal(6, VpkRebuildService.CountDistinctMaterials(previous));
		Assert.Equal(4, VpkRebuildService.CountDistinctMaterials(fresh));
		Assert.False(VpkRebuildService.HasMaterialCollapse(previous, fresh));
	}

	[Fact]
	public void SurvivingMaterialsCollapsingToFallbackIdsStillTriggersGuard()
	{
		var previous = BuildDocument(
			("0001", [("hair", "0x01"), ("skin", "0x02")]),
			("0002", [("clothes_1", "0x03"), ("clothes_2", "0x04")]));
		var collapsed = BuildDocument(
			("0001", [("hair", "0xdead"), ("skin", "0xdead")]),
			("0002", [("clothes_1", "0xdead"), ("clothes_2", "0xdead")]));

		Assert.True(VpkRebuildService.HasMaterialCollapse(previous, collapsed));
	}

	[Fact]
	public void LosingAUnitWhileMaterialCountDropsTriggersGuard()
	{
		var previous = BuildDocument(
			("0001", [("hair", "0x01")]),
			("0002", [("clothes_1", "0x03")]));
		var missingUnit = BuildDocument(
			("0001", [("hair", "0x01")]));

		Assert.True(VpkRebuildService.HasMaterialCollapse(previous, missingUnit));
	}
}
