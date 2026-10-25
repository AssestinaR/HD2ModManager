using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 「这件护甲包含哪些 unit」来自游戏注册文件。
///
/// 这里锁的是取键的两条规则：**组合护甲要按成员逐个取**（只查第一个 item 会漏掉其余成员），
/// 以及**只取 unit 类资产**（件表里也可能挂材质引用）。
/// </summary>
public sealed class ArmourUnitTableSourceTests
{
	private const uint Armour = 820333399;
	private const uint Helmet = 1745464610;
	private const ulong UnitType = PatchUnitMeshReader.UnitTypeId;

	private static GameDataAuthorityPiece Piece(string unit, uint slot = 1)
		=> new(
			RawBodyType: 0,
			BodyVariant: UnitMeshBodyVariant.Any,
			Slot: slot,
			PartKind: UnitMeshPartKind.Helmet,
			PieceType: 0,
			Weight: 0,
			ToneVariations: 0,
			UnitAssetKey: new AssetKey(UnitType, Convert.ToUInt64(unit, 16)),
			Resources: Array.Empty<GameDataAuthorityResourceReference>());

	private static GameDataAuthorityKit Kit(uint itemId, params GameDataAuthorityPiece[] pieces)
		=> new(itemId, 0, 0, GameDataEquipmentKitKind.Armor, "archive", $"item {itemId}", pieces);

	private static GameDataAuthoritySnapshot Snapshot(params GameDataAuthorityKit[] kits)
		=> new(
			GameDataAuthoritySourceKind.PlaintextDataLibrary,
			"path",
			"fingerprint",
			DateTimeOffset.UnixEpoch,
			kits,
			Array.Empty<CoreIssue>());

	[Fact]
	public void ReadUnitIds_UnionsEveryMemberKit()
	{
		var snapshot = Snapshot(
			Kit(Armour, Piece("3c09e08493ee44b8"), Piece("226dfb2e738ae56e")),
			Kit(Helmet, Piece("af431352d9b95e83")));

		// 组合护甲必须把成员 kit 的 unit 都算上——只查 Armour 会漏掉头盔那个 unit。
		var units = ArmourUnitTableSource.ReadUnitIds(snapshot, [Armour, Helmet]);
		Assert.Equal(new[] { "226dfb2e738ae56e", "3c09e08493ee44b8", "af431352d9b95e83" }, units);
	}

	[Fact]
	public void ReadUnitIds_IgnoresNonUnitAssets()
	{
		var material = new GameDataAuthorityPiece(
			0, UnitMeshBodyVariant.Any, 1, UnitMeshPartKind.Helmet, 0, 0, 0,
			new AssetKey(0x1111111111111111, 0x2222222222222222),
			Array.Empty<GameDataAuthorityResourceReference>());
		var snapshot = Snapshot(Kit(Armour, Piece("3c09e08493ee44b8"), material));

		// 件表里可能挂材质之类的引用；它们不是 unit，不能进树。
		Assert.Equal(new[] { "3c09e08493ee44b8" }, ArmourUnitTableSource.ReadUnitIds(snapshot, [Armour]));
	}

	[Fact]
	public void ReadUnitIds_DeduplicatesSharedUnits()
	{
		var snapshot = Snapshot(
			Kit(Armour, Piece("3c09e08493ee44b8")),
			Kit(Helmet, Piece("3c09e08493ee44b8")));
		var units = ArmourUnitTableSource.ReadUnitIds(snapshot, [Armour, Helmet]);
		Assert.Equal("3c09e08493ee44b8", Assert.Single(units));
	}

	[Fact]
	public void ReadUnitIds_EmptyWhenNoItemMatches()
	{
		var snapshot = Snapshot(Kit(Armour, Piece("3c09e08493ee44b8")));
		Assert.Empty(ArmourUnitTableSource.ReadUnitIds(snapshot, [999]));
	}

	[Fact]
	public void FindMissingItems_DistinguishesMissingKitFromEmptyKit()
	{
		// 头盔套在注册文件里**没有 kit**（实测 RE-2310 就是这样）与"kit 存在但没 unit"
		// 是两种不同的坏法：前者是数据缺失，后者是这件护甲确实没有几何。
		var snapshot = Snapshot(Kit(Armour, Piece("3c09e08493ee44b8")), Kit(Helmet));
		var missing = ArmourUnitTableSource.FindMissingItems(snapshot, [Armour, Helmet, 42]);
		Assert.Equal(new uint[] { 42 }, missing);
	}
}
