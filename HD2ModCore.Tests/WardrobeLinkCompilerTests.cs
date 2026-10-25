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

	private static GameDataAuthorityKit Kit(uint itemId, params (uint Slot, uint PieceType, ulong Unit)[] pieces) => new(
		itemId,
		0,
		0,
		GameDataEquipmentKitKind.Armor,
		"aaaaaaaaaaaaaaaa",
		"kit-" + itemId,
		pieces.Select(piece => new GameDataAuthorityPiece(
			RawBodyType: 3,
			BodyVariant: UnitMeshBodyVariant.Any,
			Slot: piece.Slot,
			PartKind: UnitMeshPartKind.Unknown,
			PieceType: piece.PieceType,
			Weight: 1,
			ToneVariations: 0,
			UnitAssetKey: new AssetKey(UnitTypeId, piece.Unit),
			Resources: Array.Empty<GameDataAuthorityResourceReference>())).ToArray());
}
