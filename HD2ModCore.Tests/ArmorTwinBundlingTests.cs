using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

public sealed class ArmorTwinBundlingTests
{
	private static GameDataAuthorityPiece Piece(ulong fileId, uint slot = 2, uint pieceType = 0, uint rawBodyType = 0)
		=> new(
			rawBodyType,
			rawBodyType == 1 ? UnitMeshBodyVariant.Slim : UnitMeshBodyVariant.Stocky,
			slot,
			UnitMeshPartKind.Unknown,
			pieceType,
			0,
			1,
			new AssetKey(1, fileId),
			[]);

	private static GameDataAuthorityKit Kit(
		uint id,
		string name,
		GameDataEquipmentKitKind kind,
		params GameDataAuthorityPiece[] pieces)
		=> new(id, 1, id, kind, $"0x{id:x8}", name, pieces);

	private static RuntimeArmorCatalogEntry Entry(
		uint id,
		string name,
		GameDataEquipmentKitKind kind,
		uint setId,
		uint dlcId = 1)
		=> new(id, $"0x{id:x8}", name, kind, setId, dlcId);

	[Fact]
	public void BuildTwinIndex_GroupsIdenticalNonCapeLayouts_AndExcludesHexNonPlayerAndDemocracyOfficer()
	{
		var kits = new[]
		{
			// 玩家孪生护甲组：B-01（多版本，其中一条与 B-16 同构）+ B-16
			Kit(1001u, "B-01“战术”", GameDataEquipmentKitKind.Armor, Piece(0xA1, 2), Piece(0xC1, 1)),
			Kit(1002u, "B-01“战术”", GameDataEquipmentKitKind.Armor, Piece(0xA9, 2), Piece(0xC2, 1)),
			Kit(1003u, "B-16“百战老兵”", GameDataEquipmentKitKind.Armor, Piece(0xA1, 2), Piece(0xC3, 1)),

			// 非同构（多一个部件）不进组
			Kit(1004u, "CM-09“碎骨杀手”", GameDataEquipmentKitKind.Armor, Piece(0xB1, 2), Piece(0xB2, 3)),
			Kit(1005u, "AF-02“危害大师”", GameDataEquipmentKitKind.Armor, Piece(0xB1, 2)),

			// 拿不到的非玩家记录、无名 0x…、民主官 NPC 不进组
			Kit(2107035846u, "CM-09“碎骨杀手”", GameDataEquipmentKitKind.Armor, Piece(0xB1, 2)),
			Kit(1006u, "0xdeadbeef", GameDataEquipmentKitKind.Armor, Piece(0xA1, 2)),
			Kit(KitNonPlayerArmors.DemocracyOfficerArmorId, "FS-05“神射手”", GameDataEquipmentKitKind.Armor, Piece(0xA1, 2)),

			// 头盔孪生组
			Kit(2001u, "CM-14“医师”", GameDataEquipmentKitKind.Helmet, Piece(0xD1, 0)),
			Kit(2002u, "TR-117“阿尔法指挥官”", GameDataEquipmentKitKind.Helmet, Piece(0xD1, 0)),
			Kit(1303420068u, "CM-09“碎骨杀手”", GameDataEquipmentKitKind.Helmet, Piece(0xD1, 0)),
		};

		var twinIndex = ArmorTwinBundling.BuildTwinIndex(kits);

		var armorGroup = Assert.Single(twinIndex.ArmorGroups);
		Assert.Equal(["B-01“战术”", "B-16“百战老兵”"], armorGroup.DisplayNames);
		Assert.Equal([1001u, 1002u, 1003u], armorGroup.MemberItemIds);
		Assert.False(twinIndex.GroupByItemId.ContainsKey(KitNonPlayerArmors.DemocracyOfficerArmorId));
		Assert.False(twinIndex.GroupByItemId.ContainsKey(1004u));
		Assert.False(twinIndex.GroupByItemId.ContainsKey(1005u));
		Assert.False(twinIndex.GroupByItemId.ContainsKey(1006u));

		var helmetGroup = Assert.Single(twinIndex.HelmetGroups);
		Assert.Equal(["CM-14“医师”", "TR-117“阿尔法指挥官”"], helmetGroup.DisplayNames);
		Assert.Equal([2001u, 2002u], helmetGroup.MemberItemIds);
		Assert.False(twinIndex.GroupByItemId.ContainsKey(1303420068u));
	}

	[Fact]
	public void BuildBaseCatalog_BundlesMatchedTwinSetsArmorsAndHelmetsIntoSingleRow()
	{
		var kits = new[]
		{
			Kit(101u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
			Kit(102u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
			Kit(201u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Helmet, Piece(0x201, 0)),
			Kit(202u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Helmet, Piece(0x201, 0)),
		};
		var twinIndex = ArmorTwinBundling.BuildTwinIndex(kits);
		var catalog = new[]
		{
			Entry(101u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Armor, 10),
			Entry(102u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Armor, 20),
			Entry(201u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Helmet, 10),
			Entry(202u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Helmet, 20),
		};

		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog, twinIndex, twinBundlingEnabled: true);

		var setRow = Assert.Single(baseCatalog.Sets);
		Assert.True(setRow.IsTwinBundle);
		Assert.Equal(2, setRow.TwinSetCount);
		Assert.Equal(ArmorTwinBundling.TwinBundleReason, setRow.TwinBundleNote);
		Assert.Equal([101u, 102u], setRow.ArmorMemberIds);
		Assert.Equal([201u, 202u], setRow.HelmetMemberIds);
		Assert.Equal([101u, 102u, 201u, 202u], setRow.MemberItemIds);

		var armorRow = Assert.Single(baseCatalog.Armors);
		Assert.True(armorRow.IsTwinBundle);
		Assert.Equal([101u, 102u], armorRow.MemberItemIds);

		var helmetRow = Assert.Single(baseCatalog.Helmets);
		Assert.True(helmetRow.IsTwinBundle);
		Assert.Equal([201u, 202u], helmetRow.MemberItemIds);

		// 关闭开关后恢复单独映射
		var unbundled = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog, twinIndex, twinBundlingEnabled: false);
		Assert.Equal(2, unbundled.Sets.Count);
		Assert.Equal(2, unbundled.Armors.Count);
		Assert.Equal(2, unbundled.Helmets.Count);
		Assert.All(unbundled.Sets, row => Assert.False(row.IsTwinBundle));
	}

	[Fact]
	public void EvaluateProjection_SharedTwinHelmetOnly_BlocksInThreeSlot_AndIsIndependentInTwoSlot()
	{
		var kits = new[]
		{
			Kit(101u, "CM-14“医师”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
			Kit(102u, "TR-117“阿尔法指挥官”", GameDataEquipmentKitKind.Armor, Piece(0x102, 2)),
			Kit(201u, "CM-14“医师”", GameDataEquipmentKitKind.Helmet, Piece(0x201, 0)),
			Kit(202u, "TR-117“阿尔法指挥官”", GameDataEquipmentKitKind.Helmet, Piece(0x201, 0)),
		};
		var twinIndex = ArmorTwinBundling.BuildTwinIndex(kits);
		var catalog = new[]
		{
			Entry(101u, "CM-14“医师”", GameDataEquipmentKitKind.Armor, 10),
			Entry(102u, "TR-117“阿尔法指挥官”", GameDataEquipmentKitKind.Armor, 20),
			Entry(201u, "CM-14“医师”", GameDataEquipmentKitKind.Helmet, 10),
			Entry(202u, "TR-117“阿尔法指挥官”", GameDataEquipmentKitKind.Helmet, 20),
		};

		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog, twinIndex, twinBundlingEnabled: true);
		Assert.Equal(2, baseCatalog.Sets.Count);
		Assert.All(baseCatalog.Sets, row => Assert.True(row.HasSharedTwinHelmetOnly));

		// 3 槽下：选中 CM-14 套装时，TR-117 套装因头盔孪生冲突置灰
		var cm14Key = baseCatalog.Sets.Single(row => row.PrimaryItemId == 101u).SelectionKey;
		var threeSlot = ArmorMappingPickerPlanner.EvaluateProjection(
			baseCatalog,
			new Dictionary<uint, string>(),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cm14Key },
			isTwoSlotCompressedSourceOpen: false);
		var tr117ThreeSlot = threeSlot.Sets.Single(row => row.PrimaryItemId == 102u);
		Assert.True(tr117ThreeSlot.IsBlocked);

		// 2 槽下：即使 CM-14 已选中且头盔 201/202 已映射，TR-117 套装仍只带护甲、互不阻塞
		var twoSlot = ArmorMappingPickerPlanner.EvaluateProjection(
			baseCatalog,
			new Dictionary<uint, string> { [201u] = "已映射到 X", [202u] = "已映射到 X" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cm14Key },
			isTwoSlotCompressedSourceOpen: true);
		var cm14TwoSlot = twoSlot.Sets.Single(row => row.PrimaryItemId == 101u);
		var tr117TwoSlot = twoSlot.Sets.Single(row => row.PrimaryItemId == 102u);
		Assert.Equal([101u], cm14TwoSlot.MemberItemIds);
		Assert.Empty(cm14TwoSlot.HelmetMemberIds);
		Assert.False(tr117TwoSlot.IsBlocked);
	}

	[Fact]
	public void BuildTargetGroupPlans_MergesTwinTargetsIntoOneRow()
	{
		var kits = new[]
		{
			Kit(101u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
			Kit(102u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
		};
		var twinIndex = ArmorTwinBundling.BuildTwinIndex(kits);
		var targets = new[]
		{
			Entry(101u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Armor, 10),
			Entry(102u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Armor, 20),
		};

		var merged = ArmorMappingPickerPlanner.BuildTargetGroupPlans(targets, twinIndex, twinBundlingEnabled: true);
		var row = Assert.Single(merged);
		Assert.True(row.IsTwinBundle);
		Assert.Equal([101u, 102u], row.MemberItemIds);
	}

	[Fact]
	public void CompletePartialTwinMappings_AutoCompletesPartialGroup_AndRespectsToggle()
	{
		var kits = new[]
		{
			Kit(101u, "DP-40“联邦英雄”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
			Kit(102u, "TR-9“民主骑兵”", GameDataEquipmentKitKind.Armor, Piece(0x101, 2)),
		};
		var twinIndex = ArmorTwinBundling.BuildTwinIndex(kits);
		var partial = new[]
		{
			new RuntimeArmorRegistryMapping(101u, null, "model-a"),
		};

		var offResult = ArmorTwinBundling.CompletePartialTwinMappings(partial, twinIndex, twinBundlingEnabled: false);
		Assert.Equal(0, offResult.AddedCount);
		Assert.Single(offResult.Mappings);

		var onResult = ArmorTwinBundling.CompletePartialTwinMappings(partial, twinIndex, twinBundlingEnabled: true);
		Assert.Equal(1, onResult.AddedCount);
		Assert.Equal(0, onResult.RealignedCount);
		Assert.Equal(2, onResult.Mappings.Count);
		Assert.All(onResult.Mappings, mapping => Assert.Equal("model-a", mapping.ModelKey));
	}
}
