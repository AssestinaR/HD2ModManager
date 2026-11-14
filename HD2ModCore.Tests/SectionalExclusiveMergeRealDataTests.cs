using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

/// <summary>
/// P4b 规划器在真实装备权威表（Release 索引）上的核对。<c>HD2_TEST_MANAGER_ROOT</c> 指向带
/// <c>data/indexes/asset-index.sqlite</c> 的管理器目录；缺样本时静默跳过（本仓库真数据测试的约定）。
/// 来源取「雫」那样的标准 7 件：头、躯干、胯、左右腿、左右臂（不含肩）。
/// </summary>
public sealed class SectionalExclusiveMergeRealDataTests(ITestOutputHelper output)
{
	private static IReadOnlyList<SectionalSourceUnit> Source()
	=>
	[
		new("0000000000000001", new ArmorCell(0, 0)),
		new("0000000000000002", new ArmorCell(2, 0)),
		new("0000000000000003", new ArmorCell(3, 0)),
		new("0000000000000004", new ArmorCell(4, 0)),
		new("0000000000000005", new ArmorCell(5, 0)),
		new("0000000000000006", new ArmorCell(6, 0)),
		new("0000000000000007", new ArmorCell(7, 0)),
	];

	private static async Task<GameDataAuthoritySnapshot?> LoadAsync()
	{
		var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(managerRoot)) return null;
		if (!File.Exists(Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite"))) return null;
		return await ArmorAuthorityIndexReader.TryReadAsync(new StoragePaths(managerRoot));
	}

	/// <summary>P4a 能逐格承载的记录：BuildWithMerge 的结果必须与 Build 逐项一致（规划 §3.1 第 1 点）。</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Phase4aRecordsKeepExactlyTheirResult(bool headMerge)
	{
		var snapshot = await LoadAsync();
		if (snapshot is null) return;
		var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);

		var checkedCount = 0;
		foreach (var kit in snapshot.Kits.Where(kit => kit.Kind == GameDataEquipmentKitKind.Armor))
		{
			var strict = SectionalExclusivePlanner.Build(index, Source(), kit.ItemId, headMerge);
			var merged = SectionalExclusivePlanner.BuildWithMerge(index, Source(), kit.ItemId, headMerge);
			if (!strict.Succeeded)
			{
				Assert.False(strict.Succeeded);
				continue;
			}

			checkedCount++;
			Assert.True(merged.Succeeded, $"{kit.ItemId}");
			Assert.False(merged.IsMerged, $"{kit.ItemId}");
			Assert.Equal(strict.Replacements.Select(r => (r.Source.FileId, r.Target.Key.FileId)), merged.Replacements.Select(r => (r.Source.FileId, r.Target.Key.FileId)));
			Assert.Equal(strict.Placements, merged.Placements);
			Assert.Equal(strict.Hide, merged.Hide);
			Assert.Equal(strict.Signature, merged.Signature);
			Assert.Equal(strict.HelmetNotes, merged.HelmetNotes);
			Assert.Equal(strict.Info, merged.Info);
			Assert.Equal(strict.Warnings, merged.Warnings);
		}

		output.WriteLine($"headMerge={headMerge}: P4a 能逐格承载的记录 {checkedCount} 条，结果全部不变。");
		Assert.True(checkedCount > 0);
	}

	[Fact]
	public async Task CoverageAndTheWaitingList()
	{
		var snapshot = await LoadAsync();
		if (snapshot is null) return;
		var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);
		var armors = snapshot.Kits.Where(kit => kit.Kind == GameDataEquipmentKitKind.Armor).ToArray();

		var strictOk = 0;
		var torsoMerge = new List<string>();
		var otherMerge = new List<string>();
		var failed = new List<string>();
		foreach (var kit in armors)
		{
			var strict = SectionalExclusivePlanner.Build(index, Source(), kit.ItemId, false);
			var plan = SectionalExclusivePlanner.BuildWithMerge(index, Source(), kit.ItemId, false);
			var line = $"{kit.ItemId} {kit.DisplayName} set={kit.SetId} dlc={kit.DlcId}";
			if (strict.Succeeded) strictOk++;
			else if (!plan.Succeeded) failed.Add($"{line} :: {string.Join("；", plan.Gaps)}");
			else if (plan.Merges.All(merge => merge.IsTorso))
				torsoMerge.Add($"{line} :: 合并 {plan.Merges.Count} 条 组{plan.GroupMates.Count} 告警{plan.Warnings.Count}");
			else
				otherMerge.Add($"{line} :: {string.Join("/", plan.Merges.Select(merge => merge.CarrierPart).Distinct())} 组{plan.GroupMates.Count}");
		}

		output.WriteLine($"护甲记录 {armors.Length} 条：P4a 逐格 {strictOk}；躯干合并 {torsoMerge.Count}；非躯干合并 {otherMerge.Count}；不可 {failed.Count}");
		output.WriteLine("-- 躯干合并 --");
		foreach (var line in torsoMerge) output.WriteLine(line);
		output.WriteLine("-- 非躯干合并 --");
		foreach (var line in otherMerge) output.WriteLine(line);
		output.WriteLine("-- 不可 --");
		foreach (var line in failed) output.WriteLine(line);
	}

	/// <summary>玩家神射手：同名组放宽后，只有左肩独有，模型合并进左肩（两种身形各一个承载件）。</summary>
	[Fact]
	public async Task PlayerSharpshooterMergesIntoTheLeftShoulderThroughItsNameGroup()
	{
		var snapshot = await LoadAsync();
		if (snapshot is null) return;
		var sharpshooter = snapshot.Kits.SingleOrDefault(kit => kit.ItemId == 3970674111 && kit.Kind == GameDataEquipmentKitKind.Armor);
		if (sharpshooter is null) return;   // 别的游戏版本没有这条记录：跳过
		var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);

		Assert.False(SectionalExclusivePlanner.Build(index, Source(), 3970674111, false).Succeeded);   // P4a 做不了
		var plan = SectionalExclusivePlanner.BuildWithMerge(index, Source(), 3970674111, false);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Equal(["左肩"], plan.Merges.Select(merge => merge.CarrierPart).Distinct());
		// 左肩 健壮/纤细：归档 id 前 32 位是 90b57e54 / 755f0bcd。
		Assert.Equal([0x755f0bcdUL, 0x90b57e54UL], plan.Merges.Select(merge => merge.CarrierFileId >> 32).Order());
		Assert.Contains(plan.GroupMates, mate => mate.Contains("1490803405", StringComparison.Ordinal));
		Assert.Contains(plan.Warnings, warning => warning.Contains("第一人称", StringComparison.Ordinal));
		Assert.Contains(plan.Warnings, warning => warning.Contains("同名记录", StringComparison.Ordinal));
		// 民主官有自己的左肩，不受影响：承载件里没有它的独有件。
		var democracy = snapshot.Kits.Single(kit => kit.ItemId == 124623533);
		var carriers = plan.Merges.Select(merge => merge.CarrierFileId).ToHashSet();
		Assert.DoesNotContain(democracy.Pieces, piece => carriers.Contains(piece.UnitAssetKey.FileId));
		// 头：头盔 unit ffc28ef4… 被 8 条同名头盔记录共用，放宽后头落在它上面，并提示同名头盔都会显示。
		Assert.Contains(plan.Placements, placement => placement.HelmetName is not null && placement.TargetFileId == 0xffc28ef4b7c57c38UL);
		Assert.Contains(plan.HelmetNotes, note => note.Contains("8 条头盔记录都会显示这个头", StringComparison.Ordinal));
		Assert.DoesNotContain(plan.Warnings.Concat(plan.HelmetNotes), line => line.Contains("124623533", StringComparison.Ordinal));   // 预览不写民主官相关内容
		output.WriteLine(string.Join(Environment.NewLine, plan.HelmetNotes.Concat(plan.Info).Concat(plan.Warnings)));
	}

	/// <summary>
	/// 三类的数量与规划 §2 的表一致（单条记录口径）：能走 P4a 的全部在 P4a 里；其余要么合并、要么不可。
	/// 具体数字随游戏版本会变，所以只钉不变量：三类互不重叠、合并 + P4a + 不可 = 全部。
	/// </summary>
	[Fact]
	public async Task EveryRecordIsExactlyOneOfCellMergedOrImpossible()
	{
		var snapshot = await LoadAsync();
		if (snapshot is null) return;
		var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);
		var candidates = SectionalExclusivePlanner.Evaluate(index, Source(), headMerge: false);

		var cell = candidates.Count(candidate => candidate.Succeeded && !candidate.Merged);
		var merged = candidates.Count(candidate => candidate.Succeeded && candidate.Merged);
		var impossible = candidates.Count(candidate => !candidate.Succeeded);
		Assert.Equal(candidates.Count, cell + merged + impossible);
		Assert.All(candidates.Where(candidate => !candidate.Succeeded), candidate => Assert.StartsWith("不可：", candidate.Summary, StringComparison.Ordinal));
		Assert.All(candidates.Where(candidate => candidate.Merged), candidate => Assert.StartsWith("可专属·合并（实验）", candidate.Summary, StringComparison.Ordinal));
		output.WriteLine($"P4a 逐格 {cell}、合并 {merged}、不可 {impossible}（共 {candidates.Count}）");
	}
}
