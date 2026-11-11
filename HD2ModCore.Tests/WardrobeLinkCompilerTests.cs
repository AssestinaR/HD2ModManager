using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住编译器最关键的性质：**link 的源键集只由"目标护甲有哪些件"决定，与外观来源无关**。
///
/// 这不是洁癖。运行时按源键做增量 diff，而删除的映射按契约永不释放（释放会让已实例化的
/// 护甲悬空并崩溃）。键集若随来源抖动，每次换外观都会在引擎覆盖表里留下陈旧项，
/// 累积到表容量就整页空白并崩溃——2026-10-23 现场实测到 2004 条且每次换源增长 15~23 条。
/// </summary>
public sealed class WardrobeLinkCompilerTests
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;

	[Fact]
	public void Compile_KeepsTheSameSourceKeysWhenOnlyTheAppearanceChanges()
	{
		// 目标有胸/裆两件；来源 B 只有胸（裆要靠隐藏 unit 补），来源 C 两件都有。
		var snapshot = Snapshot(
			Kit(100, (2, 0, 0xAAAA_0001), (3, 1, 0xAAAA_0002)),
			Kit(200, (2, 0, 0xBBBB_0001)),
			Kit(201, (2, 0, 0xCCCC_0001), (3, 1, 0xCCCC_0002)));

		var fromB = WardrobeLinkCompiler.Compile(snapshot, [new RuntimeArmorRegistryMapping(100, 200)], 1, true);
		var fromC = WardrobeLinkCompiler.Compile(snapshot, [new RuntimeArmorRegistryMapping(100, 201)], 2, true);

		Assert.Equal(SourceKeys(fromB.Text), SourceKeys(fromC.Text));
		// 两件都要有 link：来源 B 缺的裆用隐藏 unit 补，而不是"这条不发了"。
		Assert.Equal(2, fromB.LinkCount);
		Assert.Equal(2, fromC.LinkCount);
		Assert.Equal(1, fromB.HiddenCount);
		Assert.Equal(0, fromC.HiddenCount);
	}

	[Fact]
	public void Compile_EmitsASelfLinkWhenTheSourcePieceIsTheSameUnit()
	{
		var snapshot = Snapshot(
			Kit(100, (2, 0, 0xAAAA_0001)),
			Kit(200, (2, 0, 0xAAAA_0001)));

		var compiled = WardrobeLinkCompiler.Compile(snapshot, [new RuntimeArmorRegistryMapping(100, 200)], 1, true);

		// 自链接必须照样发出来，否则键集会随来源抖动。
		Assert.Equal(1, compiled.LinkCount);
		Assert.Contains("link=00000000aaaa0001,00000000aaaa0001", compiled.Text);
	}

	[Fact]
	public void Compile_ResolvesCustomModelPiecesAsSource()
	{
		// 自定义素体没有 kit：件表直接来自工作区模型定义，unit 是全新 id（不在注册表里）。
		// 之前这里被 `!mapping.IsModelMapping` 过滤掉，等于素体在运行时链路里完全不生效。
		var snapshot = Snapshot(Kit(100, (2, 0, 0xAAAA_0001), (3, 1, 0xAAAA_0002)));
		var model = new RuntimeArmorRegistryModel(
			"custom/x",
			null,
			[
				new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 2, 0),
				new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000002", 3, 1),
			]);

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot,
			[new RuntimeArmorRegistryMapping(100, null, "custom/x")],
			1,
			true,
			[model]);

		Assert.Equal(2, compiled.LinkCount);
		Assert.Equal(0, compiled.HiddenCount);
		Assert.Contains("link=00000000aaaa0001,c0ffee0000000001", compiled.Text);
		Assert.Contains("link=00000000aaaa0002,c0ffee0000000002", compiled.Text);
		// 素体映射没有来源物品 id，不写 map= 行（它只是管理器的意图记录）。
		Assert.DoesNotContain("map=", compiled.Text);
	}

	[Fact]
	public void Compile_PadsMissingModelPiecesWithHiddenUnit()
	{
		// 素体只给了一件时，目标另一件要用隐藏 unit 补，键集不能因此变少。
		var snapshot = Snapshot(Kit(100, (2, 0, 0xAAAA_0001), (3, 1, 0xAAAA_0002)));
		var model = new RuntimeArmorRegistryModel(
			"custom/y",
			null,
			[new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 2, 0)]);

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot,
			[new RuntimeArmorRegistryMapping(100, null, "custom/y")],
			1,
			true,
			[model]);

		Assert.Equal(2, compiled.LinkCount);
		Assert.Equal(1, compiled.HiddenCount);
	}

	/// <summary>
	/// **身形编码必须先换算。** 素体件存的是注册表原始编码（Stocky=0 / Slim=1 / Any=3），
	/// 而目标件的键取自权威表的 <see cref="UnitMeshBodyVariant"/>（Slim=1 / Stocky=2 / Any=3）。
	/// 两套编号在 Stocky 上错位：直接比较会让 Stocky 件永远对不上。
	/// 2026-11-11 实机症状：一条素体映射 24 件里只有 2 件命中，**只有头盔能显示、护甲主体全隐藏**
	/// （头盔与 Any 恰好都是 3，Slim 恰好都是 1，只有 Stocky 错位）。
	/// </summary>
	[Fact]
	public void Compile_ConvertsTheModelBodyEncodingBeforeMatching()
	{
		// 素体按原始编码声明 Stocky=0；目标护甲的行是 UnitMeshBodyVariant.Stocky=2。
		var snapshot = Snapshot(KitWithBody(100, UnitMeshBodyVariant.Stocky, (2, 0, 0xAAAA_0001)));
		var model = new RuntimeArmorRegistryModel(
			"custom/stocky",
			null,
			[new RuntimeArmorRegistryModelPiece(0, "c0ffee0000000001", 2, 0)]);   // 0 = 原始 Stocky

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot,
			[new RuntimeArmorRegistryMapping(100, null, "custom/stocky")],
			1,
			true,
			[model]);

		Assert.Equal(0, compiled.HiddenCount);
		Assert.Contains("link=00000000aaaa0001,c0ffee0000000001", compiled.Text);
	}

	/// <summary>
	/// 目标是共用的「Any」行时，来源按身形拆成两份也能用——前提是两份指向同一个 unit。
	/// 原生把腿、披风、臂内层放在共用行（网格不随身形变），而素体可能按身形各声明一份；
	/// 共用格表达不了"两份不同的网格"，所以不同的那两份必须隐藏，绝不挑一份塞给两种身形
	/// （那会让一半身形穿错网格）。
	/// </summary>
	[Fact]
	public void Compile_AnyTargetAcceptsPerShapeSourceOnlyWhenBothShapesAgree()
	{
		var sameUnit = Snapshot(KitWithBody(100, UnitMeshBodyVariant.Any, (4, 0, 0xAAAA_0001)));
		var modelSame = new RuntimeArmorRegistryModel(
			"custom/same",
			null,
			[
				new RuntimeArmorRegistryModelPiece(0, "c0ffee0000000001", 4, 0),   // Stocky
				new RuntimeArmorRegistryModelPiece(1, "c0ffee0000000001", 4, 0),   // Slim，同一 unit
			]);
		var agreed = WardrobeLinkCompiler.Compile(
			sameUnit, [new RuntimeArmorRegistryMapping(100, null, "custom/same")], 1, true, [modelSame]);
		Assert.Equal(0, agreed.HiddenCount);
		Assert.Contains("link=00000000aaaa0001,c0ffee0000000001", agreed.Text);

		// 两份指向不同 unit ⇒ 不能挑一份，这一层隐藏。
		var modelDifferent = new RuntimeArmorRegistryModel(
			"custom/diff",
			null,
			[
				new RuntimeArmorRegistryModelPiece(0, "c0ffee0000000001", 4, 0),
				new RuntimeArmorRegistryModelPiece(1, "c0ffee0000000002", 4, 0),
			]);
		var disagreed = WardrobeLinkCompiler.Compile(
			sameUnit, [new RuntimeArmorRegistryMapping(100, null, "custom/diff")], 1, true, [modelDifferent]);
		Assert.Equal(1, disagreed.HiddenCount);
	}

	/// <summary>
	/// **整片对不上时最底层借用。** 原生 138 套里，胯有 16 套只有 <c>pt1</c>（甚至 <c>pt1+pt2</c>）、
	/// 左腿 9 套、右腿 11 套、右臂 12 套同样只有 <c>pt1</c>；而素体的件只按 <c>pt0</c> 声明。
	/// 严格按层号对齐会把这些部位整片隐藏，表现为"这套护甲缺胯/缺腿/缺手"。
	/// </summary>
	[Fact]
	public void Compile_BorrowsTheLowestLayerWhenTheWholePartMisses()
	{
		// 目标胯部只有 pt1，素体只有 pt0。
		var snapshot = Snapshot(KitWithBody(100, UnitMeshBodyVariant.Any, (3, 1, 0xAAAA_0001)));
		var model = new RuntimeArmorRegistryModel(
			"custom/hips",
			null,
			[new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 3, 0)]);

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(100, null, "custom/hips")], 1, true, [model]);

		Assert.Equal(0, compiled.HiddenCount);
		Assert.Contains("link=00000000aaaa0001,c0ffee0000000001", compiled.Text);
	}

	/// <summary>
	/// 只在「整片对不上」时借用：部位里有一层已经对上时，其余层照旧隐藏，
	/// 免得同一个网格被铺到两层上互相打架。
	/// </summary>
	[Fact]
	public void Compile_DoesNotBorrowWhenThePartIsAlreadyCovered()
	{
		// 目标胸部分两层：pt0（素体有）与 pt1（素体没有）。
		var snapshot = Snapshot(KitWithBody(
			100, UnitMeshBodyVariant.Any, (2, 0, 0xAAAA_0001), (2, 1, 0xAAAA_0002)));
		var model = new RuntimeArmorRegistryModel(
			"custom/torso",
			null,
			[new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 2, 0)]);

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(100, null, "custom/torso")], 1, true, [model]);

		Assert.Equal(2, compiled.LinkCount);
		Assert.Equal(1, compiled.HiddenCount);
		Assert.Contains("link=00000000aaaa0001,c0ffee0000000001", compiled.Text);
		Assert.Contains("link=00000000aaaa0002," + WardrobeLinkCompiler.HiddenUnit, compiled.Text);
	}

	/// <summary>
	/// 素体没有的部位（披风、左右肩）仍然隐藏——这是对的：那些件模型里本来就没有几何。
	/// 一并钉住"不要因为修了身形编码就把没来源的部位也硬塞一个 unit"。
	/// </summary>
	[Fact]
	public void Compile_KeepsPartsTheModelDoesNotHaveHidden()
	{
		var snapshot = Snapshot(KitWithBody(
			100, UnitMeshBodyVariant.Any,
			(1, 0, 0xAAAA_0001),   // 披风
			(2, 0, 0xAAAA_0002),   // 胸部
			(8, 0, 0xAAAA_0003),   // 左肩
			(9, 0, 0xAAAA_0004))); // 右肩
		var model = new RuntimeArmorRegistryModel(
			"custom/body",
			null,
			[new RuntimeArmorRegistryModelPiece(3, "c0ffee0000000001", 2, 0)]);

		var compiled = WardrobeLinkCompiler.Compile(
			snapshot, [new RuntimeArmorRegistryMapping(100, null, "custom/body")], 1, true, [model]);

		Assert.Equal(4, compiled.LinkCount);
		Assert.Equal(3, compiled.HiddenCount);
		Assert.Contains("link=00000000aaaa0002,c0ffee0000000001", compiled.Text);
	}

	private static HashSet<string> SourceKeys(string text) =>
		text.Split('\n')
			.Where(line => line.StartsWith("link=", StringComparison.Ordinal))
			.Select(line => line[5..].Split(',')[0])
			.ToHashSet(StringComparer.Ordinal);

	private static GameDataAuthoritySnapshot Snapshot(params GameDataAuthorityKit[] kits) => new(
		GameDataAuthoritySourceKind.PlaintextDataLibrary,
		"armor",
		"fingerprint",
		DateTimeOffset.UtcNow,
		kits,
		Array.Empty<CoreIssue>());

	private static GameDataAuthorityKit Kit(uint itemId, params (uint Slot, uint PieceType, ulong Unit)[] pieces)
		=> KitWithBody(itemId, UnitMeshBodyVariant.Any, pieces);

	private static GameDataAuthorityKit KitWithBody(
		uint itemId,
		UnitMeshBodyVariant body,
		params (uint Slot, uint PieceType, ulong Unit)[] pieces) => new(
		itemId,
		0,
		0,
		GameDataEquipmentKitKind.Armor,
		"aaaaaaaaaaaaaaaa",
		"kit-" + itemId,
		pieces.Select(piece => new GameDataAuthorityPiece(
			RawBodyType: ArmorStructure.ToRawBody(body) ?? 3,
			BodyVariant: body,
			Slot: piece.Slot,
			PartKind: UnitMeshPartKind.Unknown,
			PieceType: piece.PieceType,
			Weight: 1,
			ToneVariations: 0,
			UnitAssetKey: new AssetKey(UnitTypeId, piece.Unit),
			Resources: Array.Empty<GameDataAuthorityResourceReference>())).ToArray());
}
