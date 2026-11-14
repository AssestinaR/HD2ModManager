using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// 专属多选（P5）的合同测试：多目标合成、承载件去重、规则上限、旧文件兼容、全选与搜索。纯函数 + 临时目录。
/// </summary>
public sealed class KitExclusiveMultiTargetTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;

	private static GameDataAuthorityPiece P(uint slot, uint layer, uint raw, ulong file)
		=> new(
			raw,
			raw switch { 0 => UnitMeshBodyVariant.Stocky, 1 => UnitMeshBodyVariant.Slim, _ => UnitMeshBodyVariant.Any },
			slot, default, layer, 0, 0, new CoreAssetKey(UnitType, file), []);

	private static GameDataAuthorityKit Kit(uint item, uint set, string name, params GameDataAuthorityPiece[] pieces)
		=> new(item, 0, set, GameDataEquipmentKitKind.Armor, $"{item:x16}", name, pieces);

	private static IReadOnlyList<SectionalSourceUnit> Source() =>
	[
		new("0000000000000002", new ArmorCell(2, 0)),
		new("0000000000000003", new ArmorCell(3, 0)),
		new("0000000000000004", new ArmorCell(4, 0)),
		new("0000000000000005", new ArmorCell(5, 0)),
		new("0000000000000006", new ArmorCell(6, 0)),
		new("0000000000000007", new ArmorCell(7, 0)),
	];

	/// <summary>一套每个身体格子都有独有部件的护甲（P4a 逐格）。<paramref name="baseId"/> 决定部件 id，不同套互不相同。</summary>
	private static GameDataAuthorityKit Unique(uint item, string name, ulong baseId)
		=> Kit(item, item,
			name,
			P(2, 1, 0, baseId + 0x20), P(2, 1, 1, baseId + 0x21),
			P(3, 1, 3, baseId + 0x30), P(4, 1, 3, baseId + 0x40), P(5, 1, 3, baseId + 0x50),
			P(6, 1, 3, baseId + 0x60), P(7, 1, 3, baseId + 0x70),
			P(8, 1, 3, baseId + 0x80));   // 没有来源的肩：进隐藏集，所以这套需要规则

	private static SectionalExclusivePlan Plan(IReadOnlyList<GameDataAuthorityKit> kits, uint item)
		=> SectionalExclusivePlanner.BuildWithMerge(SectionalExclusivePlanner.BuildIndex(kits), Source(), item, headMerge: false);

	// ── 多目标合成 ────────────────────────────────────────────────

	[Fact]
	public void TwoDisjointTargets_DoNotOverlap_AndCarriersAddUp()
	{
		var kits = new[] { Unique(1, "A", 0x10000), Unique(2, "B", 0x20000) };
		var a = Plan(kits, 1);
		var b = Plan(kits, 2);
		Assert.True(a.Succeeded && b.Succeeded);

		var combined = SectionalExclusiveCombiner.Combine([b, a]);   // 传入顺序不影响结果顺序

		Assert.Empty(combined.Conflicts);
		Assert.Equal([1u, 2u], combined.Accepted.Select(plan => plan.ItemId));
		var carriers = combined.Renames.Select(item => item.Target.Key.FileId).ToArray();
		Assert.Equal(carriers.Length, carriers.Distinct().Count());
		Assert.Equal(a.Replacements.Count + b.Replacements.Count, combined.Renames.Count);
		Assert.Equal(carriers.Length, combined.CarrierCount);
		Assert.Empty(a.Replacements.Select(item => item.Target.Key.FileId).Intersect(b.Replacements.Select(item => item.Target.Key.FileId)));
	}

	[Fact]
	public void SingleTarget_CombinesToExactlyItsOwnPlan()
	{
		var kits = new[] { Unique(1, "A", 0x10000) };
		var plan = Plan(kits, 1);

		var combined = SectionalExclusiveCombiner.Combine([plan]);

		Assert.Equal(plan.Replacements, combined.Replacements);
		Assert.Equal(plan.Replacements, combined.Renames);
		Assert.Empty(combined.Merges);
		Assert.Equal(plan.Replacements.Count, combined.CarrierCount);
		var seed = KitExclusiveService.SeedOf(plan);
		Assert.Equal(plan.Signature.Take(1).Select(id => id.ToString("x16")), seed.All);
		Assert.Equal(plan.Hide.Select(id => id.ToString("x16")), seed.Hide);
		// 单目标写出的记录没有目标列表：和 P4b 之前逐字段相同。
		var record = KitExclusiveWorkspace.BuildRecord(new ModNodeId(Guid.NewGuid()), Path.GetTempPath(), [seed]);
		Assert.NotNull(record);
		Assert.Null(record!.Targets);
		Assert.Equal(1u, record.TargetItemId);
	}

	/// <summary>同名组：两条记录共用一个左肩承载件，选中两条时承载件、合并产物只写一次。</summary>
	[Fact]
	public void SameNameGroup_SharedCarrierIsWrittenOnce_ButBothKeepTheirOwnRules()
	{
		// 身体各部位被别的护甲 99 共用；只有 0x8001（左肩）归组内两条记录。
		GameDataAuthorityKit Member(uint item) => Kit(item, item, "G",
			P(2, 1, 3, 0x2001), P(3, 1, 3, 0x3001), P(4, 1, 3, 0x4001), P(5, 1, 3, 0x5001), P(6, 1, 3, 0x6001), P(7, 1, 3, 0x7001),
			P(8, 1, 3, 0x8001), P(8, 0, 3, 0x9000 + item));
		var kits = new[]
		{
			Member(11), Member(12),
			Kit(99, 99, "Other", P(2, 1, 3, 0x2001), P(3, 1, 3, 0x3001), P(4, 1, 3, 0x4001), P(5, 1, 3, 0x5001), P(6, 1, 3, 0x6001), P(7, 1, 3, 0x7001)),
		};
		var a = Plan(kits, 11);
		var b = Plan(kits, 12);
		Assert.True(a.Succeeded && b.Succeeded, a.FailureText + b.FailureText);
		Assert.True(a.IsMerged && b.IsMerged);

		var combined = SectionalExclusiveCombiner.Combine([a, b]);

		Assert.Empty(combined.Conflicts);
		Assert.Equal(2, combined.Accepted.Count);
		var merge = Assert.Single(combined.Merges);              // 来源集合相同的合并只做一次
		Assert.Contains(0x8001UL, merge.Carriers);
		Assert.Equal(a.Merges.Select(item => item.CarrierFileId).Concat(b.Merges.Select(item => item.CarrierFileId)).Distinct().Count(), combined.CarrierCount);
		Assert.True(combined.CarrierCount < a.Replacements.Count + b.Replacements.Count);

		// 各自的规则：规则名带各自的 item_id，隐藏集各写各的。
		var node = new ModNodeId(Guid.NewGuid());
		var seeds = new[] { KitExclusiveService.SeedOf(a), KitExclusiveService.SeedOf(b) };
		var record = KitExclusiveWorkspace.BuildRecord(node, Path.GetTempPath(), seeds);
		Assert.NotNull(record);
		Assert.Equal(2, record!.AllTargets.Count);
		var names = record.ToRules().Select(rule => rule.Name).ToArray();
		Assert.Contains(KitExclusiveWorkspace.RuleNameFor(node, 11), names);
		Assert.Contains(KitExclusiveWorkspace.RuleNameFor(node, 12), names);
	}

	[Fact]
	public void CarrierClaimedByDifferentSources_RejectsTheLaterTarget()
	{
		static SectionalReplacement R(string source, ulong target)
			=> new(new SectionalSourceUnit(source, new ArmorCell(2, 0)), new SectionalTargetUnit(new CoreAssetKey(UnitType, target), new ArmorCell(2, 1)));
		SectionalExclusivePlan Manual(uint item, params SectionalReplacement[] replacements)
			=> new(true, item, $"T{item}", [], replacements, [], [], [0x1], SectionalHeadMergePlan.Off, [], [], []);

		var combined = SectionalExclusiveCombiner.Combine([Manual(2, R("0000000000000009", 0xAA)), Manual(1, R("0000000000000002", 0xAA))]);

		Assert.Equal([1u], combined.Accepted.Select(plan => plan.ItemId));
		var conflict = Assert.Single(combined.Conflicts);
		Assert.Equal(2u, conflict.ItemId);
		Assert.Contains("0x00000000000000aa", conflict.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void IdenticalRulesAcrossTargets_AreDeduplicated()
	{
		var shared = new KitInstanceWorkspaceTarget(1, "vpk-1-aaaaaaaa", 0.3, ["0000000000000001"], [], ["0000000000000010"]);
		var same = shared with { TargetItemId = 2, RuleName = "vpk-2-aaaaaaaa" };
		var other = shared with { TargetItemId = 3, RuleName = "vpk-3-aaaaaaaa", Hide = ["0000000000000011"] };
		var record = new KitInstanceWorkspaceRecord(
			1, shared.RuleName, 0.3, shared.All, [], shared.Hide, Targets: [shared, same, other]);

		Assert.Equal(["vpk-1-aaaaaaaa", "vpk-3-aaaaaaaa"], record.ToRules().Select(rule => rule.Name));
	}

	// ── Hider 上限 ────────────────────────────────────────────────

	private static KitExclusiveSeed Seed(uint item, int hideCount = 1, int extraAll = 0)
		=> new(item, $"T{item}", 0.3, [$"{item:x16}"], [.. Enumerable.Range(0, extraAll).Select(i => $"{(ulong)(0xE000 + i):x16}")],
			[.. Enumerable.Range(0, hideCount).Select(i => $"{(ulong)(0x100000 + item * 1000 + i):x16}")]);

	[Fact]
	public void RuleLimits_AllowAFewTargets_AndBlockMoreThanSixtyFourRules()
	{
		var node = new ModNodeId(Guid.NewGuid());
		var dir = Path.GetTempPath();

		Assert.Null(KitExclusiveWorkspace.CheckRuleLimits(node, dir, [Seed(1), Seed(2), Seed(3)], KitInstanceWorkspace.Empty));

		var tooMany = Enumerable.Range(1, 65).Select(i => Seed((uint)i)).ToArray();
		var error = KitExclusiveWorkspace.CheckRuleLimits(node, dir, tooMany, KitInstanceWorkspace.Empty);
		Assert.NotNull(error);
		Assert.Contains("64", error, StringComparison.Ordinal);

		// 备用签名也算规则：32 个目标 × 3 条 = 96 条。
		var extras = Enumerable.Range(1, 32).Select(i => Seed((uint)i, extraAll: 2)).ToArray();
		Assert.NotNull(KitExclusiveWorkspace.CheckRuleLimits(node, dir, extras, KitInstanceWorkspace.Empty));
	}

	[Fact]
	public void RuleLimits_CountOtherModsRules_ButIgnoreThisNodesOldRecord()
	{
		var node = new ModNodeId(Guid.NewGuid());
		var other = new ModNodeId(Guid.NewGuid());
		var dir = Path.GetTempPath();
		KitInstanceWorkspaceRecord Foreign(uint item) => new(item, $"x-{item}", 0.3, [$"{item:x16}"], [], ["0000000000000010"]);
		var workspace = Enumerable.Range(1, 62).Aggregate(
			KitInstanceWorkspace.Empty.With(node, Foreign(999)),
			(current, i) => current.With(new ModNodeId(Guid.NewGuid()), Foreign((uint)i)));

		Assert.Null(KitExclusiveWorkspace.CheckRuleLimits(node, dir, [Seed(1), Seed(2)], workspace));   // 62 + 2 = 64，本节点旧记录不算
		var error = KitExclusiveWorkspace.CheckRuleLimits(node, dir, [Seed(1), Seed(2), Seed(3)], workspace.With(other, Foreign(500)));
		Assert.NotNull(error);
		Assert.Contains("别的 Mod", error, StringComparison.Ordinal);
	}

	[Fact]
	public void RuleLimits_BlockWhenTheRulesFileWouldExceed64KiB()
	{
		var node = new ModNodeId(Guid.NewGuid());
		// 每条 128 个隐藏项 ≈ 2.2 KB：40 条 ≈ 88 KB，条数没超（≤64）但文件超 64 KiB。
		var seeds = Enumerable.Range(1, 40).Select(i => Seed((uint)i, hideCount: 128)).ToArray();

		var error = KitExclusiveWorkspace.CheckRuleLimits(node, Path.GetTempPath(), seeds, KitInstanceWorkspace.Empty);

		Assert.NotNull(error);
		Assert.Contains("字节", error, StringComparison.Ordinal);
	}

	// ── 存储与兼容 ────────────────────────────────────────────────

	[Fact]
	public async Task OldWardrobeFile_WithOnlyTargetItemId_ReadsAsAOneItemList_AndRoundTrips()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2-multi-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			// 旧版写的 wardrobe.json：只有 targetItemId + exclusive，没有 targets。
			await File.WriteAllTextAsync(VpkModWardrobeService.GetStatePath(directory),
				"""{"schema":"hd2mod.vpk-wardrobe/1","targetArmorArchiveId":"abc","targetArmorName":"FS-05","targetItemId":42,"exclusive":true,"disabled":{}}""");
			var old = await VpkModWardrobeService.ReadAsync(directory);
			var single = Assert.Single(old.ExclusiveTargets);
			Assert.Equal(42u, single.ItemId);
			Assert.False(old.AutoSkipFailedTargets);

			// 写回多目标：单个字段保留第一项，旧代码读到的仍是合理的单目标状态。
			var multi = old with
			{
				Targets = [new VpkWardrobeTarget(42, "A"), new VpkWardrobeTarget(7, "B"), new VpkWardrobeTarget(9, "C")],
				AutoSkipFailedTargets = true,
			};
			await VpkModWardrobeService.WriteAsync(directory, multi);
			var raw = await File.ReadAllTextAsync(VpkModWardrobeService.GetStatePath(directory));
			Assert.Contains("\"targetItemId\": 42", raw, StringComparison.Ordinal);
			var again = await VpkModWardrobeService.ReadAsync(directory);
			Assert.Equal([42u, 7u, 9u], again.ExclusiveTargets.Select(item => item.ItemId));
			Assert.Equal(42u, again.TargetItemId);
			Assert.True(again.AutoSkipFailedTargets);

			// 清掉记忆 / 关专属：目标列表一并清掉。
			await VpkModWardrobeService.WriteAsync(directory, again with { AutoSkipFailedTargets = false, Exclusive = false });
			var off = await VpkModWardrobeService.ReadAsync(directory);
			Assert.Empty(off.ExclusiveTargets);
			Assert.False(off.AutoSkipFailedTargets);
			Assert.Null(off.TargetItemId);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void WorkspaceRecords_OldSingleTargetReadsAsOne_MultiRoundTripsWithFirstTargetAtTopLevel()
	{
		var node = new ModNodeId(Guid.NewGuid());
		var oldJson = "{\"schema\":1,\"records\":{\"" + node.Value.ToString("D")
			+ "\":{\"targetItemId\":5,\"ruleName\":\"vpk-5-aaaaaaaa\",\"radius\":0.3,\"all\":[\"0000000000000001\"],\"none\":[],\"hide\":[\"0000000000000010\"],\"source\":\"vpk-exclusive\"}}}";
		var parsed = KitInstanceWorkspaceStore.Parse(oldJson);
		var record = parsed.Records[node];
		Assert.Null(record.Targets);
		var only = Assert.Single(record.AllTargets);
		Assert.Equal(5u, only.TargetItemId);
		Assert.Equal(["vpk-5-aaaaaaaa"], record.ToRules().Select(rule => rule.Name));

		var seeds = new[] { Seed(5, 2), Seed(6, 3, extraAll: 1) };
		var multi = KitExclusiveWorkspace.BuildRecord(node, Path.GetTempPath(), seeds)!;
		var round = KitInstanceWorkspaceStore.Parse(KitInstanceWorkspaceStore.Serialize(KitInstanceWorkspace.Empty.With(node, multi))).Records[node];

		Assert.Equal(5u, round.TargetItemId);                              // 顶层 = 第一个目标
		Assert.Equal(["vpk-5-" + node.Value.ToString("N")[..8]], new[] { round.RuleName });
		Assert.Equal([5u, 6u], round.AllTargets.Select(item => item.TargetItemId));
		Assert.Equal(
			[KitExclusiveWorkspace.RuleNameFor(node, 5), KitExclusiveWorkspace.RuleNameFor(node, 6), KitExclusiveWorkspace.RuleNameFor(node, 6) + "-s2"],
			round.ToRules().Select(rule => rule.Name));
	}

	[Fact]
	public async Task SyncTargets_WritesOneRecordForTheNode_ReplacesItAndRemovesItWhenEmpty()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-multi-" + Guid.NewGuid().ToString("N"));
		var nodeDirectory = Path.Combine(root, "mod");
		Directory.CreateDirectory(nodeDirectory);
		try
		{
			var node = new ModNodeId(Guid.NewGuid());
			var written = await KitExclusiveWorkspace.SyncTargetsAsync(node, root, nodeDirectory, [Seed(1), Seed(2), Seed(3)]);
			Assert.True(written.Ok, written.Error);
			var workspace = await KitInstanceWorkspaceStore.ReadAsync(root);
			var record = Assert.Single(workspace.Records).Value;
			Assert.True(KitExclusiveWorkspace.IsExclusiveRecord(record));
			Assert.Equal([1u, 2u, 3u], record.AllTargets.Select(item => item.TargetItemId));

			// 去掉两个再同步：整条记录被替换，不留旧目标的规则。
			await KitExclusiveWorkspace.SyncTargetsAsync(node, root, nodeDirectory, [Seed(2)]);
			var shrunk = Assert.Single((await KitInstanceWorkspaceStore.ReadAsync(root)).Records).Value;
			Assert.Null(shrunk.Targets);
			Assert.Equal([2u], shrunk.AllTargets.Select(item => item.TargetItemId));

			await KitExclusiveWorkspace.SyncTargetsAsync(node, root, nodeDirectory, []);
			Assert.Empty((await KitInstanceWorkspaceStore.ReadAsync(root)).Records);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// ── 非玩家表、全选、搜索 ──────────────────────────────────────

	[Fact]
	public void NonPlayerTable_ListsExactlyTheThreeKnownRecords()
	{
		Assert.Equal([124623533u, 2107035846u, 1969314276u], KitNonPlayerArmors.Entries.Select(entry => entry.ItemId));
		Assert.True(KitNonPlayerArmors.TryGetLabel(124623533, out var npc));
		Assert.Equal("非玩家（民主官 NPC）", npc);
		Assert.True(KitNonPlayerArmors.TryGetLabel(2107035846, out var unobtainable));
		Assert.Equal("非玩家（拿不到）", unobtainable);
		Assert.False(KitNonPlayerArmors.Contains(3970674111));
	}

	private static SectionalExclusiveCandidate C(uint item, string name, bool ok = true)
		=> new(item, $"{item:x16}", name, ok, ok ? "可专属" : "不可：x");

	[Fact]
	public void Labels_NumberSameNamePlayerVersionsByAscendingItemId_AndMarkNonPlayers()
	{
		var options = KitExclusiveService.LabelOptions(
		[
			C(3970674111, "FS-05“神射手”"), C(124623533, "FS-05“神射手”"), C(1490803405, "FS-05“神射手”"),
			C(5, "SC-30"), C(1969314276, "SC-30"),            // 只剩一条玩家记录：不编号
			C(7, "0xdeadbeef"),                                // 无名记录不列
			C(9, "B-01", ok: false), C(8, "B-01"),
		]);

		Assert.DoesNotContain(options, option => option.ItemId == 7);
		var fs = options.Where(option => option.Name.StartsWith("FS-05", StringComparison.Ordinal)).OrderBy(option => option.ItemId).ToArray();
		Assert.Equal(124623533u, fs[0].ItemId);
		Assert.Equal("非玩家（民主官 NPC）", fs[0].Tag);
		Assert.Null(fs[0].Version);
		Assert.Equal(1, fs.Single(option => option.ItemId == 1490803405).Version);   // 升序：1490803405 < 3970674111
		Assert.Equal(2, fs.Single(option => option.ItemId == 3970674111).Version);
		Assert.Equal("版本 2", fs.Single(option => option.ItemId == 3970674111).Tag);
		Assert.Null(options.Single(option => option.ItemId == 5).Version);
		Assert.Equal("非玩家（拿不到）", options.Single(option => option.ItemId == 1969314276).Tag);
		Assert.Equal([1, 2], options.Where(option => option.Name == "B-01").OrderBy(option => option.ItemId).Select(option => option.Version!.Value));
	}

	[Fact]
	public void SelectAll_PicksAvailablePlayerArmors_SkippingDisabledAndNonPlayers_IncludingAllSameNameVersions()
	{
		var options = KitExclusiveService.LabelOptions(
		[
			C(1490803405, "FS-05“神射手”"), C(3970674111, "FS-05“神射手”"), C(124623533, "FS-05“神射手”"),   // 民主官 NPC 跳过
			C(10, "AD-26"), C(11, "B-01", ok: false), C(12, "B-01"), C(13, "B-01"),                           // 禁用项跳过，同名可用版本全选
			C(2107035846, "CM-09"), C(14, "CM-09"),
		]);

		var picked = KitExclusiveSelection.SelectAll(options).Order().ToArray();

		Assert.Equal([10u, 12u, 13u, 14u, 1490803405u, 3970674111u], picked);
		// 非玩家记录可以手动勾（它们仍是可用的选项），只是不进全选。
		Assert.Contains(options, option => option.ItemId == 124623533 && option.Available && option.IsNonPlayer);
	}

	[Theory]
	[InlineData("神射手", true)]
	[InlineData("3970674111", true)]
	[InlineData("397067", true)]
	[InlineData("fs-05 神射手", true)]
	[InlineData("版本 2", true)]
	[InlineData("AD-26", false)]
	[InlineData("", true)]
	public void Search_MatchesNameAndItemId_CaseInsensitively(string query, bool expected)
	{
		var option = KitExclusiveService.LabelOptions([C(1490803405, "FS-05“神射手”"), C(3970674111, "FS-05“神射手”")])
			.Single(item => item.ItemId == 3970674111);

		Assert.Equal(expected, KitExclusiveSelection.Matches(option, query));
	}
}
