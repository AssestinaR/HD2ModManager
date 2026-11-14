using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 产物的 unit 身份（<c>sections.json</c> 的 slot / pieceType / bodyType）必须**由拆件计划写下来**。
/// <para>
/// 来源 unit 自己不带 customization（legacy patch 里整批 unit 的这三个串都是空的），只读字节的话
/// 表里就是三个空串，于是同一份产物在 vpk2patch 下能开的组件树在 sectional 下是一棵空树，
/// 换甲编译也把它们当成"认不出部位"。这里同时守住"写入"与"写入之后树看得见"这两半。
/// </para>
/// </summary>
public sealed class SectionalUnitIdentityTests
{
	private const string Helmet = "a0cc3d2318d9e237";
	private const string TorsoSlim = "4ebbfde8692577ff";
	private const string TorsoStocky = "ecc8df6ccc7721bd";

	/// <summary>修复前的形状：表里只有 fileId 与 section，身份三个字段全空。</summary>
	private static WardrobeSectionMapDocument UndocumentedMap()
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("/tmp/x.patch_0", "abc", 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			[
				.. new[] { Helmet, TorsoSlim, TorsoStocky }.Select(fileId => new WardrobeSectionMapUnit(
					fileId, string.Empty, string.Empty, string.Empty, string.Empty, 2, true,
					[
						new WardrobeSectionMapEntry(0, "aabbccddeeff0011", 10, "aabbccddeeff0011-10", []),
						new WardrobeSectionMapEntry(1, "aabbccddeeff0011", 12, "aabbccddeeff0011-12", []),
					])),
			]);

	private static SectionalSplitPlan Plan(params SectionalSplitTarget[] targets)
		=> new(targets, [], [], []);

	private static SectionalSplitPlan StandardPlan()
		=> Plan(
			new SectionalSplitTarget("Helmet", "Any", Helmet, []),
			new SectionalSplitTarget("Torso", "Slim", TorsoSlim, [Helmet]),
			new SectionalSplitTarget("Torso", "Stocky", TorsoStocky, []));

	[Fact]
	public void BuildUnitIdentities_TakesSlotBodyAndLayerFromThePlan()
	{
		var identities = SectionalSplitter.BuildUnitIdentities(StandardPlan());

		Assert.Equal(3, identities.Count);
		Assert.Equal(new WardrobeSectionMapUnitIdentity("Helmet", "Undergarment", "Any"), identities[Helmet]);
		Assert.Equal(new WardrobeSectionMapUnitIdentity("Torso", "Undergarment", "Slim"), identities[TorsoSlim]);
		Assert.Equal(new WardrobeSectionMapUnitIdentity("Torso", "Undergarment", "Stocky"), identities[TorsoStocky]);
	}

	[Fact]
	public void BuildUnitIdentities_SkipsTargetsWithoutHost()
	{
		var identities = SectionalSplitter.BuildUnitIdentities(
			Plan(new SectionalSplitTarget("Torso", "Any", string.Empty, [])));

		Assert.Empty(identities);
	}

	/// <summary>
	/// 层的写法与素体定义必须一致（<see cref="SectionalModelDefinition.RegistryPieceLayer"/>）：
	/// 组件树按 <c>Undergarment</c> 认"基础 7 槽"，换甲编译按同一串算层号。
	/// </summary>
	[Fact]
	public void BuildUnitIdentities_KeepsTheLayerInStepWithTheCustomModelDefinition()
	{
		var identities = SectionalSplitter.BuildUnitIdentities(StandardPlan());
		var expected = SectionalModelDefinition.PieceTypeName(SectionalModelDefinition.RegistryPieceLayer);

		Assert.Equal("Undergarment", expected);
		Assert.All(identities.Values, identity => Assert.Equal(expected, identity.PieceType));
	}

	[Fact]
	public void ApplyUnitIdentities_MatchesLooseKeysAndLeavesUnknownUnitsAlone()
	{
		var document = UndocumentedMap();
		var applied = WardrobeSectionMapFile.ApplyUnitIdentities(document, new Dictionary<string, WardrobeSectionMapUnitIdentity>
		{
			// 键的写法不该决定命中与否：大写 + 0x 前缀都认。
			["0x" + Helmet.ToUpperInvariant()] = new("Helmet", "Undergarment", "Any"),
		});

		Assert.Equal("Helmet", applied.FindUnit(Helmet)!.Slot);
		Assert.Equal("Undergarment", applied.FindUnit(Helmet)!.PieceType);
		Assert.Equal("Any", applied.FindUnit(Helmet)!.BodyType);
		// 没给身份的 unit 一个字段都不许被顺手改掉。
		Assert.Empty(applied.FindUnit(TorsoSlim)!.Slot);
		Assert.Empty(applied.FindUnit(TorsoSlim)!.PieceType);
		// 原文档不可变：这份表还要在别处被读。
		Assert.Empty(document.FindUnit(Helmet)!.Slot);
	}

	[Fact]
	public void ApplyUnitIdentities_IsANoOpWithoutIdentities()
	{
		var document = UndocumentedMap();

		Assert.Same(document, WardrobeSectionMapFile.ApplyUnitIdentities(document, identities: null));
		Assert.Same(document, WardrobeSectionMapFile.ApplyUnitIdentities(document, new Dictionary<string, WardrobeSectionMapUnitIdentity>()));
	}

	/// <summary>
	/// 这一条就是用户报的"sectional mod 的 section 树不显示"：空身份 ⇒ 一个基础槽都不命中 ⇒ 空树。
	/// 写下身份之后，两个身形分支各有自己的部位。
	/// </summary>
	[Fact]
	public void Identities_MakeTheComponentTreeSeeTheProducedSlots()
	{
		var undocumented = UndocumentedMap();
		Assert.Empty(VpkModWardrobeService.BuildTree("VPK 测试", undocumented).Children);

		var documented = WardrobeSectionMapFile.ApplyUnitIdentities(
			undocumented, SectionalSplitter.BuildUnitIdentities(StandardPlan()));
		var tree = VpkModWardrobeService.BuildTree("VPK 测试", documented);

		// Any 的 unit 展开到两个身形分支；Slim / Stocky 各一支。
		Assert.Equal(2, tree.Children.Count);
		Assert.All(tree.Children, body =>
		{
			Assert.Equal(2, body.Children.Count);
			Assert.Contains(body.Children, slot => slot.Label == ArmorStructure.Display(ArmorPart.Helmet));
			Assert.Contains(body.Children, slot => slot.Label == ArmorStructure.Display(ArmorPart.Torso));
		});
	}

	[Fact]
	public void DeriveUnitIdPrefix_IsDeterministic12HexDigitsAndDistinctPerModelKey()
	{
		var first = SectionalSplitter.DeriveUnitIdPrefix("custom-model-alpha");
		var second = SectionalSplitter.DeriveUnitIdPrefix("custom-model-alpha");
		var other = SectionalSplitter.DeriveUnitIdPrefix("custom-model-beta");

		Assert.Equal(12, first.Length);
		Assert.Equal(first, second);
		Assert.NotEqual(first, other);
		Assert.True(ulong.TryParse(first + "0001", System.Globalization.NumberStyles.HexNumber, null, out _));
	}
}
