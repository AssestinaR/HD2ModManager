using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// 专属切换（P4a）规划器与记录的合同测试：纯函数 + 临时目录，不碰真数据。
/// 形状模仿真实索引：raw_body_type 0 = 健壮、1 = 纤细、3 = 通用；披风 slot 1 永远不碰。
/// </summary>
public sealed class SectionalExclusivePlannerTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;

	private static GameDataAuthorityPiece P(uint slot, uint layer, uint raw, ulong file)
		=> new(
			raw,
			raw switch { 0 => UnitMeshBodyVariant.Stocky, 1 => UnitMeshBodyVariant.Slim, _ => UnitMeshBodyVariant.Any },
			slot, default, layer, 0, 0, new CoreAssetKey(UnitType, file), []);

	private static GameDataAuthorityKit Kit(uint item, uint set, GameDataEquipmentKitKind kind, params GameDataAuthorityPiece[] pieces)
		=> new(item, 0, set, kind, $"{item:x16}", $"K{item}", pieces);

	private static IReadOnlyList<SectionalSourceUnit> Source(bool withHead = true)
	{
		var list = new List<SectionalSourceUnit>();
		if (withHead) list.Add(new("0000000000000001", new ArmorCell(0, 0)));
		list.Add(new("0000000000000002", new ArmorCell(2, 0)));
		list.Add(new("0000000000000003", new ArmorCell(3, 0)));
		list.Add(new("0000000000000004", new ArmorCell(4, 0)));
		list.Add(new("0000000000000005", new ArmorCell(5, 0)));
		list.Add(new("0000000000000006", new ArmorCell(6, 0)));
		list.Add(new("0000000000000007", new ArmorCell(7, 0)));
		return list;
	}

	/// <summary>目标 T（item 1）：每个身体格子都有独有部件，另有共用件、肩、披风。</summary>
	private static List<GameDataAuthorityKit> World(Func<List<GameDataAuthorityPiece>, List<GameDataAuthorityPiece>>? edit = null)
	{
		var t = new List<GameDataAuthorityPiece>
		{
			P(1, 0, 3, 0xcafe),
			P(2, 0, 0, 0x2000), P(2, 1, 0, 0x2001), P(2, 0, 1, 0x2100), P(2, 1, 1, 0x2101),
			P(3, 1, 0, 0x3001), P(3, 1, 1, 0x3101),
			P(4, 1, 3, 0x4001), P(5, 1, 3, 0x5001),
			P(6, 0, 3, 0x6000), P(6, 1, 0, 0x6001), P(6, 1, 1, 0x6101),
			P(7, 0, 3, 0x7000), P(7, 1, 0, 0x7001), P(7, 1, 1, 0x7101),
			P(8, 0, 0, 0x8001),
		};
		if (edit is not null) t = edit(t);
		return
		[
			Kit(1, 100, GameDataEquipmentKitKind.Armor, [.. t]),
			// 另一套护甲：共用 T 的 0x2000 与 0x6000，以及同一个披风。
			Kit(2, 200, GameDataEquipmentKitKind.Armor, P(1, 0, 3, 0xcafe), P(2, 0, 0, 0x2000), P(6, 0, 3, 0x6000)),
			// T 的头盔：独有 0x1001；同套另一顶头盔的件被别的记录共用。
			Kit(3, 100, GameDataEquipmentKitKind.Helmet, P(0, 0, 3, 0x1001)),
			Kit(4, 100, GameDataEquipmentKitKind.Helmet, P(0, 0, 3, 0x1002)),
			Kit(5, 300, GameDataEquipmentKitKind.Helmet, P(0, 0, 3, 0x1002)),
		];
	}

	private static SectionalExclusivePlan Plan(
		IReadOnlyList<GameDataAuthorityKit> kits, bool headMerge = false, bool head = true)
		=> SectionalExclusivePlanner.Build(SectionalExclusivePlanner.BuildIndex(kits), Source(head), 1, headMerge);

	[Fact]
	public void PlacesEverySourceOnTheOutermostUniqueUnitOfEachBodyCell()
	{
		var plan = Plan(World());

		Assert.True(plan.Succeeded, plan.FailureText);
		var targets = plan.Replacements.ToDictionary(r => r.Target.Key.FileId, r => r.Source.FileId);
		// 躯干：每种身形最外层独有件（第 1 层）；共用的第 0 层 0x2000 不当承载件。
		Assert.Equal("0000000000000002", targets[0x2001]);
		Assert.Equal("0000000000000002", targets[0x2101]);
		// 通用（raw 3）腿：两种身形共用同一承载件，只写一份。
		Assert.Single(plan.Replacements, r => r.Target.Key.FileId == 0x4001);
		// 手臂：外层 pc1 的身形件是承载件；通用的 pc0 件 0x6000 不是。
		Assert.Equal("0000000000000006", targets[0x6001]);
		Assert.Equal("0000000000000006", targets[0x6101]);
		Assert.DoesNotContain(plan.Replacements, r => r.Target.Key.FileId == 0x6000);
		// 头：同套头盔记录 3 有独有部件。
		Assert.Equal("0000000000000001", targets[0x1001]);
		// 每个承载件只承载一个来源。
		Assert.Equal(plan.Replacements.Count, plan.Replacements.Select(r => r.Target.Key.FileId).Distinct().Count());
	}

	[Fact]
	public void HideSetIsEveryNonCapeUnitOfTheTargetMinusCarriers_AndNeverWritesShells()
	{
		var plan = Plan(World());

		var carriers = plan.Replacements.Select(r => r.Target.Key.FileId).ToHashSet();
		Assert.DoesNotContain(plan.Hide, carriers.Contains);
		Assert.Contains(0x2000UL, plan.Hide);     // 共用内层：交给 Hider
		Assert.Contains(0x6000UL, plan.Hide);
		Assert.Contains(0x8001UL, plan.Hide);     // 没有来源的肩
		Assert.DoesNotContain(0xcafeUL, plan.Hide);  // 披风永远不碰
		Assert.DoesNotContain(0x1002UL, plan.Hide);  // 没替换的头盔保持原版，不隐藏
		Assert.Equal(plan.Hide.Distinct().Count(), plan.Hide.Count);
	}

	[Fact]
	public void SignatureIsASingleUnitSharedByBothBodiesWhenThereIsOne()
	{
		var plan = Plan(World());

		// 腿的承载件两种身形共用：一个 all 就够；取 ID 最小的。
		Assert.Equal([0x4001UL], plan.Signature);
		Assert.Equal(SectionalExclusivePlanner.Radius, 0.3);
	}

	[Fact]
	public void BodySpecificCarriersOnlyGetOneSignaturePerBody()
	{
		// 去掉所有通用承载件：腿改成身形专属。
		var kits = World(t => [.. t.Where(piece => piece.Slot is not (4 or 5)),
			P(4, 1, 0, 0x4001), P(4, 1, 1, 0x4101), P(5, 1, 0, 0x5001), P(5, 1, 1, 0x5101)]);

		var plan = Plan(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Equal(2, plan.Signature.Count);   // 健壮一条、纤细一条：一个 all 只会对一种身形生效
		Assert.Equal(plan.Signature.Count, plan.Signature.Distinct().Count());
	}

	[Fact]
	public void ACellWithoutAUniqueUnitFailsAndListsEveryGap()
	{
		// 髋的健壮格子、通用腿都改成共用件。
		var kits = World(t => [.. t.Where(piece => piece.Slot is not (3 or 4)),
			P(3, 1, 0, 0x3000), P(3, 1, 1, 0x3101), P(4, 1, 3, 0x4000)]);
		kits.Add(Kit(9, 900, GameDataEquipmentKitKind.Armor, P(3, 1, 0, 0x3000), P(4, 1, 3, 0x4000)));

		var plan = Plan(kits);

		Assert.False(plan.Succeeded);
		Assert.Contains(plan.Gaps, gap => gap.Contains("没有独有部件", StringComparison.Ordinal) && gap.Contains("健壮身形", StringComparison.Ordinal));
		// 通用腿两种身形都缺：两条缺口，不是只报第一处。
		Assert.True(plan.Gaps.Count >= 3, string.Join("；", plan.Gaps));
		Assert.Empty(plan.Replacements);
	}

	[Fact]
	public void AMissingBodyPartFails()
	{
		var plan = Plan(World(t => [.. t.Where(piece => piece.Slot != 7)]));

		Assert.False(plan.Succeeded);
		Assert.Contains(plan.Gaps, gap => gap.Contains("没有", StringComparison.Ordinal));
	}

	[Fact]
	public void HelmetWithoutAUniqueUnitKeepsTheOriginalHelmetAndDoesNotFail()
	{
		var kits = World();
		kits.RemoveAll(kit => kit.ItemId == 3);   // 只剩一顶共用件的头盔

		var plan = Plan(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.DoesNotContain(plan.Replacements, r => r.Source.FileId == "0000000000000001");
		Assert.Contains(plan.HelmetNotes, note => note.Contains("保留原版", StringComparison.Ordinal));
		Assert.DoesNotContain(0x1002UL, plan.Hide);
	}

	[Fact]
	public void AHelmetWithADifferentSetIdButTheSameNameStillCarriesTheHead()
	{
		var kits = World();
		kits.RemoveAll(kit => kit.ItemId is 3 or 4);
		// 同名、套装号不同的头盔记录（DS-42 的情形）。
		kits.Add(new GameDataAuthorityKit(7, 0, 999, GameDataEquipmentKitKind.Helmet, "7", "K1", [P(0, 0, 3, 0x1777)]));

		var plan = Plan(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Contains(plan.Replacements, r => r.Target.Key.FileId == 0x1777 && r.Source.FileId == "0000000000000001");
		Assert.Contains(plan.HelmetNotes, note => note.Contains("模型的头落在", StringComparison.Ordinal) && note.Contains("只有戴这顶头盔时才显示", StringComparison.Ordinal));
	}

	[Fact]
	public void HeadMergeNeedsTwoUniqueTorsoUnitsPerBody()
	{
		// 默认躯干每种身形只有 1 个独有部件（第 1 层）。
		var plan = Plan(World(), headMerge: true);
		Assert.False(plan.Succeeded);
		Assert.Contains(plan.Gaps, gap => gap.Contains("头身合并需要 2 个", StringComparison.Ordinal));
	}

	[Fact]
	public void HeadMergePutsTheHeadOnTheSecondUniqueTorsoLayerAndHidesTheHelmetRecords()
	{
		var kits = World(t => [.. t.Where(piece => piece.Slot != 2),
			P(2, 2, 0, 0x2002), P(2, 1, 0, 0x2001), P(2, 2, 1, 0x2102), P(2, 1, 1, 0x2101), P(2, 0, 0, 0x2000)]);

		var plan = Plan(kits, headMerge: true);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Equal(SectionalHeadMergePlan.IntoArmorTorso, plan.HeadMerge);
		var targets = plan.Replacements.ToDictionary(r => r.Target.Key.FileId, r => r.Source.FileId);
		Assert.Equal("0000000000000002", targets[0x2002]);   // 身体在最外层
		Assert.Equal("0000000000000001", targets[0x2001]);   // 头在次外层
		Assert.Equal("0000000000000002", targets[0x2102]);
		Assert.Equal("0000000000000001", targets[0x2101]);
		// 头盔套件的件全部隐藏，不再有头盔承载。
		Assert.Contains(0x1001UL, plan.Hide);
		Assert.Contains(0x1002UL, plan.Hide);
		Assert.DoesNotContain(plan.Replacements, r => r.Target.Key.FileId is 0x1001 or 0x1002);
	}

	[Fact]
	public void MoreThanTheProtocolLimitOfHiddenUnitsFails()
	{
		var extra = Enumerable.Range(0, 130).Select(i => P(8, 0, 3, 0x90000UL + (ulong)i)).ToArray();
		var plan = Plan(World(t => [.. t, .. extra]));

		Assert.False(plan.Succeeded);
		Assert.Contains("128", plan.FailureText, StringComparison.Ordinal);
	}

	[Fact]
	public void EvaluateMarksTheFullyExclusiveRecordAvailableAndTheOthersNot()
	{
		var index = SectionalExclusivePlanner.BuildIndex(World());

		var candidates = SectionalExclusivePlanner.Evaluate(index, Source(), headMerge: false);

		Assert.Equal([1U, 2U], candidates.Select(c => c.ItemId).Order());   // 只列护甲记录
		Assert.True(candidates.Single(c => c.ItemId == 1).Succeeded);
		var other = candidates.Single(c => c.ItemId == 2);
		Assert.False(other.Succeeded);
		Assert.StartsWith("不可：", other.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void PreviewListsPlacementsHiddenAndHelmetHandling()
	{
		var kits = World();
		var plan = Plan(kits);

		var preview = KitExclusiveService.Describe(plan, kits);

		Assert.True(preview.Succeeded);
		var text = string.Join('\n', preview.Lines);
		Assert.Contains("落点", text, StringComparison.Ordinal);
		Assert.Contains("隐藏", text, StringComparison.Ordinal);
		Assert.Contains("被取代的外观", text, StringComparison.Ordinal);
		Assert.Contains("头盔", text, StringComparison.Ordinal);
		Assert.Contains("2001", text, StringComparison.Ordinal);

		var failed = KitExclusiveService.Describe(Plan(World(t => [.. t.Where(p => p.Slot != 7)])), kits);
		Assert.False(failed.Succeeded);
		Assert.Contains("缺口", string.Join('\n', failed.Lines), StringComparison.Ordinal);
	}

	// ── 记录与规则 ──────────────────────────────────────────────────────────

	private static KitExclusiveSeed Seed(params string[] extra)
		=> new(1, "K1", 0.3, ["0000000000004001"], extra, ["0000000000002000", "0000000000006000"]);

	[Fact]
	public async Task SyncWritesAnExclusiveRecordAndRemovesItAgain()
	{
		var root = Directory.CreateTempSubdirectory("hd2-excl-").FullName;
		try
		{
			var node = new ModNodeId(Guid.NewGuid());
			var nodeDirectory = Path.Combine(root, "node");
			Directory.CreateDirectory(nodeDirectory);

			var written = await KitExclusiveWorkspace.SyncAsync(node, root, nodeDirectory, Seed("0000000000005001"));
			Assert.True(written.Ok, written.Error);

			var workspace = await KitInstanceWorkspaceStore.ReadAsync(root);
			var record = workspace.Records[node];
			Assert.True(KitExclusiveWorkspace.IsExclusiveRecord(record));
			Assert.Equal($"vpk-1-{node.Value.ToString("N")[..8]}", record.RuleName);
			Assert.Null(record.DerivedFiles);   // 没有派生 patch：P3 撤销按钮不显示，由切换管理
			Assert.Equal(["0000000000005001"], record.ExtraAll);

			// 部署编译：主规则 + 备用签名各一条，共用同一份隐藏集。
			var compiled = KitInstanceRulesCompiler.Compile([node], workspace, 0);
			Assert.Equal(2, compiled.Document.Rules.Count);
			Assert.All(compiled.Document.Rules, rule => Assert.Equal(record.Hide, rule.Hide));
			Assert.Equal(["0000000000005001"], compiled.Document.Rules.Single(rule => rule.Name.EndsWith("-s2", StringComparison.Ordinal)).All);

			var removed = await KitExclusiveWorkspace.SyncAsync(node, root, nodeDirectory, null);
			Assert.True(removed.Ok);
			Assert.Empty((await KitInstanceWorkspaceStore.ReadAsync(root)).Records);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task SyncNeverTouchesAForeignRecord()
	{
		var root = Directory.CreateTempSubdirectory("hd2-excl-").FullName;
		try
		{
			var node = new ModNodeId(Guid.NewGuid());
			var nodeDirectory = Path.Combine(root, "node");
			Directory.CreateDirectory(nodeDirectory);
			var foreign = new KitInstanceWorkspaceRecord(
				2, "kit-2-aaaaaaaa", 0.3, ["0000000000000001"], [], ["0000000000000002"], null, "P3", ["x.patch_0"]);
			await KitInstanceWorkspaceStore.WriteAsync(root, KitInstanceWorkspace.Empty.With(node, foreign));

			var refused = await KitExclusiveWorkspace.SyncAsync(node, root, nodeDirectory, Seed());
			Assert.False(refused.Ok);
			var ignored = await KitExclusiveWorkspace.SyncAsync(node, root, nodeDirectory, null);
			Assert.True(ignored.Ok);

			var after = (await KitInstanceWorkspaceStore.ReadAsync(root)).Records[node];
			Assert.Equal(foreign.RuleName, after.RuleName);
			Assert.Equal(foreign.DerivedFiles, after.DerivedFiles);
			Assert.Equal(foreign.Hide, after.Hide);
			Assert.Null(after.Source);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void OldWorkspaceFilesWithoutTheNewFieldsStillRead()
	{
		var id = Guid.NewGuid();
		var json = $$"""
			{ "schema": 1, "records": { "{{id:D}}": {
				"targetItemId": 7, "ruleName": "kit-7-aaaaaaaa", "radius": 0.3,
				"all": ["0000000000000001"], "none": [], "hide": ["0000000000000002"] } } }
			""";

		var record = KitInstanceWorkspaceStore.Parse(json).Records[new ModNodeId(id)];

		Assert.Null(record.Source);
		Assert.Null(record.ExtraAll);
		Assert.False(KitExclusiveWorkspace.IsExclusiveRecord(record));
		Assert.Single(record.ToRules());
	}

	[Fact]
	public async Task WardrobeJsonReadsOldFilesAsNonExclusiveAndRoundTripsTheNewFields()
	{
		var directory = Directory.CreateTempSubdirectory("hd2-wardrobe-").FullName;
		try
		{
			await File.WriteAllTextAsync(VpkModWardrobeService.GetStatePath(directory),
				"""{ "schema": "hd2mod.vpk-wardrobe/1", "targetArmorArchiveId": "abcd", "targetArmorName": "X", "disabled": {} }""");
			var old = await VpkModWardrobeService.ReadAsync(directory);
			Assert.False(old.Exclusive);
			Assert.Null(old.TargetItemId);

			await VpkModWardrobeService.WriteAsync(directory, old with { TargetItemId = 42, Exclusive = true });
			var again = await VpkModWardrobeService.ReadAsync(directory);
			Assert.True(again.Exclusive);
			Assert.Equal(42u, again.TargetItemId);

			// 关掉专属：targetItemId 不留着，避免旧值被当成还在专属。
			await VpkModWardrobeService.WriteAsync(directory, again with { Exclusive = false });
			var off = await VpkModWardrobeService.ReadAsync(directory);
			Assert.False(off.Exclusive);
			Assert.Null(off.TargetItemId);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void NormalPlannerIsUntouchedByTheExclusivePath()
	{
		// 回归锁：专属关闭时走的仍是老规划器，它对同一份形状的结果（披风豁免、肩隐藏、其余替换/隐藏）保持不变。
		var source = Source();
		var targets = World()[0].Pieces
			.Select(piece => new SectionalTargetUnit(
				new CoreAssetKey(UnitType, piece.UnitAssetKey.FileId),
				ArmorCell.FromRegistryCoordinates((int)piece.Slot, (int)piece.PieceType)))
			.ToArray();

		var plan = SectionalCompilePlanner.Build(source, targets, headMerge: false);

		Assert.DoesNotContain(plan.Hidden, target => target.Key.FileId == 0xcafe);
		Assert.Contains(plan.Hidden, target => target.Cell.Slot == 8);
		Assert.NotEmpty(plan.Replacements);
		Assert.True(plan.HiddenCount > 0);   // 老做法会写全局空壳；专属做法恒为 0（见上面的用例）
	}
}
