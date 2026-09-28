using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModAdaptation.Analysis;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorModelStructureTests
{
	[Fact]
	public void MergeSplit_RetainsBothIdsAndTheirEnabledState()
	{
		RuntimeArmorRegistryModelPiece[] pieces = [
			new(0, "0000000000000001", 2, 0),
			new(1, "0000000000000002", 2, 0, Enabled: false)];
		var merged = RuntimeArmorModelStructure.Merge(pieces, 2, 0);
		Assert.Equal(2, merged.Count);
		var any = Assert.Single(RuntimeArmorModelStructure.Active(merged));
		Assert.Equal(3u, any.Body);
		Assert.Equal(pieces[0].Unit, any.Unit);
		var split = RuntimeArmorModelStructure.Split(merged, 2, 0, () => throw new Exception("Should reuse IDs"));
		Assert.Equal(pieces, split.OrderBy(p => p.Body));
		var mergedAgain = RuntimeArmorModelStructure.Merge(split, 2, 0);
		Assert.Equal(any, Assert.Single(RuntimeArmorModelStructure.Active(mergedAgain)));
	}

	[Fact]
	public void SlimOnly_MergesAndRestoresSlimId()
	{
		var merged = RuntimeArmorModelStructure.Merge([new(1, "0000000000000002", 4, 5)], 4, 5);
		var split = RuntimeArmorModelStructure.Split(merged, 4, 5, () => "0000000000000001");
		Assert.Equal("0000000000000002", split.Single(p => p.Body == 1).Unit);
		Assert.Equal("0000000000000001", split.Single(p => p.Body == 0).Unit);
		Assert.Equal(2, split.Count);
	}

	[Fact]
	public void LegacyAny_WithDisabledConcrete_IsNotAnExtraTarget()
	{
		var active = RuntimeArmorModelStructure.Active([
			new(3, "0000000000000001", 2, 0),
			new(1, "0000000000000002", 2, 0, Enabled: false)]);
		Assert.Equal(3u, Assert.Single(active).Body);
	}

	[Fact]
	public void ReservedId_IsAbsentFromProtocolButNotWorkspaceSource()
	{
		var pieces = RuntimeArmorModelStructure.Merge([
			new(0, "0000000000000001", 2, 0),
			new(1, "0000000000000002", 2, 0)], 2, 0);
		var source = new RuntimeArmorRegistryDocument(0, false, [], [new("test", null, pieces)]);
		var text = RuntimeArmorRegistryService.SerializeProtocol(source);
		Assert.DoesNotContain("0000000000000002", text);
		Assert.Equal(2, source.ModelDefinitions[0].Pieces!.Count);
	}

	[Fact]
	public void ResourceFilter_PreservesSourceAndRemovesMissingRuntimePieces()
	{
		var source = new RuntimeArmorRegistryDocument(0, true, [new(42, null, "test")],
			[new("test", null, [new(3, "0000000000000001", 2, 0), new(3, "0000000000000002", 3, 0)])]);
		var compiled = RuntimeArmorResourceFilter.Compile(source, new HashSet<ulong> { 1 });
		Assert.Equal(2, source.ModelDefinitions.Single().Pieces!.Count);
		Assert.Equal("0000000000000001", Assert.Single(compiled.ModelDefinitions.Single().Pieces!).Unit);
		Assert.Throws<InvalidDataException>(() => RuntimeArmorResourceFilter.Compile(source, new HashSet<ulong>()));
	}

	[Fact]
	public void SourceSelection_SharedUnitsAloneDoNotQualify()
	{
		var a = new EquipmentUnitCatalogEntry("a", "Armor", "A", []);
		var b = new EquipmentUnitCatalogEntry("b", "Helmet", "B", []);
		var shared = new AssetArchiveMatch(new(1, 1), [new("a", "Armor", "A"), new("b", "Helmet", "B")]);
		Assert.Empty(CustomModelConversionService.SelectSources([a, b], [shared]));
		var exclusive = new AssetArchiveMatch(new(1, 2), [new("b", "Helmet", "B")]);
		Assert.Equal("b", Assert.Single(CustomModelConversionService.SelectSources([a, b], [shared, exclusive])).ArchiveId);
	}

	[Fact]
	public void ConversionPlan_CombinesArmorAndHelmetAndRejectsTwoArmors()
	{
		EquipmentUnitPart Part(ulong id, UnitMeshPartKind kind) => new(new(1, id), 0, 1, kind,
			UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any, kind.ToString(), 100, []);
		var armor = new EquipmentUnitCatalogEntry("a", "Armor", "Body", [Part(1, UnitMeshPartKind.Torso)]);
		var helmet = new EquipmentUnitCatalogEntry("h", "Helmet", "Head", [Part(2, UnitMeshPartKind.Head)]);
		var id = 100UL;
		var plan = CustomModelConversionService.BuildPlan([armor, helmet], "Outfit", "test", () => (++id).ToString("x16"));
		Assert.Equal(2, plan.Model.Pieces!.Count);
		Assert.Contains(plan.Model.Pieces, p => p.Slot == 0 && p.Body == 3);
		Assert.Contains(plan.Model.Pieces, p => p.Slot == 2);
		Assert.Equal(2, plan.Transfer.Mappings.Count);
		Assert.Throws<InvalidDataException>(() => CustomModelConversionService.BuildPlan(
			[armor, armor with { ArchiveId = "b" }], "Invalid", "test", () => "1"));
		Assert.Throws<InvalidDataException>(() => CustomModelConversionService.BuildPlan(
			Array.Empty<EquipmentUnitCatalogEntry>(), "Invalid", "test", () => "1"));
	}

	[Fact]
	public void MappingPairing_SeparatesArmorAndHelmetMembersAndSupportsVirtualSource()
	{
		RuntimeArmorProviderMember[] combined = [
			new(10, GameDataEquipmentKitKind.Armor),
			new(11, GameDataEquipmentKitKind.Helmet)
		];
		Assert.Equal(10u, RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			combined, GameDataEquipmentKitKind.Armor));
		Assert.Equal(11u, RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			combined, GameDataEquipmentKitKind.Helmet));
		Assert.Null(RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			combined, GameDataEquipmentKitKind.Cape));
		Assert.Null(RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			[new(10, GameDataEquipmentKitKind.Armor)], GameDataEquipmentKitKind.Helmet));
		Assert.Equal(20u, RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			[new(20, GameDataEquipmentKitKind.Unknown, IsCustomModel: true)],
			GameDataEquipmentKitKind.Helmet));
		Assert.Null(RuntimeArmorMappingPairing.FindUniqueSourceForTarget(
			[
				new(20, GameDataEquipmentKitKind.Unknown, IsCustomModel: true),
				new(21, GameDataEquipmentKitKind.Unknown, IsCustomModel: true)
			],
			GameDataEquipmentKitKind.Helmet));
	}

	[Fact]
	public void ConversionPlan_DeduplicatesWholeUnitsAndOrdersBySize()
	{
		EquipmentUnitPart Part(ulong id, long size) => new(new(1, id), 0, 1, UnitMeshPartKind.Torso,
			UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any, "torso", 100, []) { StoredBytes = size };
		var source = new EquipmentUnitCatalogEntry("a", "Armor", "A", [Part(1, 10), Part(2, 100), Part(2, 100)]);
		var counter = 100UL;
		var plan = CustomModelConversionService.BuildPlan(source, "Model", "test", () => (++counter).ToString("x16"));
		Assert.Equal(2, plan.Model.Pieces!.Count);
		Assert.Equal(2UL, plan.Transfer.Mappings[0].Source!.UnitAssetKey.FileId);
		Assert.Equal(new uint[] { 0, 1 }, plan.Model.Pieces.Select(p => p.PieceType));
		Assert.All(plan.Model.Pieces, p => Assert.Equal(2u, p.Slot));
		Assert.All(plan.Transfer.Mappings, m => Assert.True(m.WillReplace));
	}
}
