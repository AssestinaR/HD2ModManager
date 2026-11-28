using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 把民主官当**映射目标**时的两条硬要求（2026-11-24）：
/// 「正确地隐藏头」和「正确地附加模型」。
/// <para>
/// 做法不是新增一条"按实例藏头"的通路，而是把他的头（unit <c>387f271c7f49614f</c>，不属于任何护甲记录）
/// 补成他那套的 **slot0 件**（<see cref="KitNonPlayerArmors.WithOfficerHead"/>）。补上之后
/// "映射民主官"就和映射普通护甲完全同构，结果由既有的 <c>link=</c> 换件给出：
/// 来源有 slot0 ⇒ 换成来源的头（引擎按正常换件挂上去）；来源没有 ⇒ 换成隐藏占位（头消失）。
/// </para>
/// </summary>
public sealed class OfficerHeadMappingTests
{
	private const uint OfficerKit = KitNonPlayerArmors.DemocracyOfficerArmorId;
	private const ulong UnitTypeId = 0xe0a48d0be9a7453fUL;
	private const string Head = "387f271c7f49614f";
	private const string Hidden = "def7dc54e86d6054";
	/// <summary>他身上的普通部件（slot4 腿），只用来衬托"头没被动过"。</summary>
	private const string Leg = "1111111111111111";
	/// <summary>视觉来源的头 / 腿。</summary>
	private const string SourceHead = "eeeeeeeeeeee0000";
	private const string SourceLeg = "dddddddddddd0004";

	[Fact]
	public void WithOfficerHead_AddsSlotZeroPieceAndIsIdempotent()
	{
		var once = KitNonPlayerArmors.WithOfficerHead([Officer(), Player()]);

		var officer = Assert.Single(once, kit => kit.ItemId == OfficerKit);
		var head = Assert.Single(officer.Pieces, piece => piece.Slot == 0u);
		Assert.Equal(UnitTypeId, head.UnitAssetKey.TypeId);
		Assert.Equal(Head, head.UnitAssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal(UnitMeshBodyVariant.Any, head.BodyVariant);
		Assert.Equal(UnitMeshPartKind.Helmet, head.PartKind);

		// 幂等：再来一次不会多出一件。
		var twice = KitNonPlayerArmors.WithOfficerHead(once);
		Assert.Single(twice.Single(kit => kit.ItemId == OfficerKit).Pieces, piece => piece.Slot == 0u);
		// 别的套一点没动。
		Assert.Equal(Player().Pieces.Count, twice.Single(kit => kit.ItemId == PlayerKit).Pieces.Count);
		// 没有民主官那套时原样返回（同一个实例，不做无谓的复制）。
		var withoutOfficer = once.Where(kit => kit.ItemId != OfficerKit).ToArray();
		Assert.Same(withoutOfficer, KitNonPlayerArmors.WithOfficerHead(withoutOfficer));
	}

	[Fact]
	public void Compile_ModelWithHead_LinksTheOfficersHeadToTheModelHead()
	{
		var compiled = Compile("with-head");

		// 附加模型：头被换成来源的头 —— 引擎按正常换件挂到他头上，不需要我们克隆/绑骨。
		Assert.Contains($"link={Head},{SourceHead}", compiled.Text);
		// 腿照旧。
		Assert.Contains($"link={Leg},{SourceLeg}", compiled.Text);
	}

	[Fact]
	public void Compile_ModelWithoutHead_LinksTheOfficersHeadToTheHiddenUnit()
	{
		var compiled = Compile("no-head");

		// 隐藏头：来源没有 slot0 的件 ⇒ 他的头指向隐藏占位（"头已并进身体"的素体就该这样）。
		Assert.Contains($"link={Head},{Hidden}", compiled.Text);
		Assert.Equal(1, compiled.HiddenCount);
	}

	private static uint PlayerKit => 100u;

	private static WardrobeLinkCompiler.Result Compile(string modelKey)
		=> WardrobeLinkCompiler.Compile(
			Snapshot(KitNonPlayerArmors.WithOfficerHead([Officer(), Player()])),
			[new RuntimeArmorRegistryMapping(OfficerKit, null, modelKey)],
			1,
			true,
			Models());

	private static IReadOnlyList<RuntimeArmorRegistryModel> Models() =>
	[
		new("with-head", null,
		[
			new RuntimeArmorRegistryModelPiece(3u, SourceLeg, 4u, 0u),
			new RuntimeArmorRegistryModelPiece(3u, SourceHead, 0u, 0u),
		]),
		new("no-head", null,
		[
			new RuntimeArmorRegistryModelPiece(3u, SourceLeg, 4u, 0u),
		]),
	];

	private static GameDataAuthoritySnapshot Snapshot(IReadOnlyList<GameDataAuthorityKit> kits) =>
		new(GameDataAuthoritySourceKind.PlaintextDataLibrary, "armor", "fingerprint", DateTimeOffset.UnixEpoch, kits, []);

	private static GameDataAuthorityKit Officer() =>
		new(OfficerKit, 0u, 0u, GameDataEquipmentKitKind.Armor, "arch", "FS-05", [Piece(4u, Leg)]);

	private static GameDataAuthorityKit Player() =>
		new(PlayerKit, 0u, 0u, GameDataEquipmentKitKind.Armor, "arch", "AC-1", [Piece(4u, "aaaaaaaaaaaaaaaa")]);

	private static GameDataAuthorityPiece Piece(uint slot, string unitHex) =>
		new(3u, UnitMeshBodyVariant.Any, slot, UnitMeshPartKind.Torso, 0u, 1u, 0,
			new AssetKey(UnitTypeId, Convert.ToUInt64(unitHex, 16)), []);
}
