using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sectional;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// P4b 规划器（合并模式）合同测试：纯函数，不碰真数据。
/// 槽位：0 头、1 披风、2 躯干、3 胯、4 左腿、5 右腿、6 左臂、7 右臂、8 左肩；raw_body_type 0 = 健壮、1 = 纤细、3 = 通用。
/// </summary>
public sealed class SectionalExclusiveMergePlannerTests
{
	private const ulong UnitType = 0xe0a48d0be9a7453f;

	private static GameDataAuthorityPiece P(uint slot, uint layer, uint raw, ulong file)
		=> new(
			raw,
			raw switch { 0 => UnitMeshBodyVariant.Stocky, 1 => UnitMeshBodyVariant.Slim, _ => UnitMeshBodyVariant.Any },
			slot, default, layer, 0, 0, new CoreAssetKey(UnitType, file), []);

	private static GameDataAuthorityKit Kit(uint item, string name, GameDataEquipmentKitKind kind, params GameDataAuthorityPiece[] pieces)
		=> new(item, 0, item * 10, kind, $"{item:x16}", name, pieces);

	private static IReadOnlyList<SectionalSourceUnit> Source(bool withHead = false)
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

	/// <summary>另一套护甲，把给定的单元全占上（让那些单元不再「独有」）。</summary>
	private static GameDataAuthorityKit Squatter(uint item, params ulong[] files)
		=> Kit(item, $"S{item}", GameDataEquipmentKitKind.Armor, [.. files.Select(file => P(2, 1, 3, file))]);

	private static SectionalExclusivePlan Merge(
		IReadOnlyList<GameDataAuthorityKit> kits, bool headMerge = false, bool head = false, uint item = 1)
		=> SectionalExclusivePlanner.BuildWithMerge(SectionalExclusivePlanner.BuildIndex(kits), Source(head), item, headMerge);

	/// <summary>目标 T：躯干、胯、双腿、双臂各自独有（健壮 + 纤细），再加披风与左肩。</summary>
	private static List<GameDataAuthorityPiece> Own() =>
	[
		P(1, 0, 3, 0xcafe),
		P(2, 1, 0, 0x2001), P(2, 1, 1, 0x2101),
		P(3, 1, 0, 0x3001), P(3, 1, 1, 0x3101),
		P(4, 1, 0, 0x4001), P(4, 1, 1, 0x4101),
		P(5, 1, 0, 0x5001), P(5, 1, 1, 0x5101),
		P(6, 1, 0, 0x6001), P(6, 1, 1, 0x6101),
		P(7, 1, 0, 0x7001), P(7, 1, 1, 0x7101),
		P(8, 0, 0, 0x8001),
	];

	private static GameDataAuthorityKit T(Func<List<GameDataAuthorityPiece>, IEnumerable<GameDataAuthorityPiece>> edit)
		=> Kit(1, "T", GameDataEquipmentKitKind.Armor, [.. edit(Own())]);

	[Fact]
	public void WhatPhase4aCanCarryIsReturnedUnchanged()
	{
		var kits = new List<GameDataAuthorityKit> { T(own => own) };
		var index = SectionalExclusivePlanner.BuildIndex(kits);
		var strict = SectionalExclusivePlanner.Build(index, Source(), 1, headMerge: false);
		var merged = SectionalExclusivePlanner.BuildWithMerge(index, Source(), 1, headMerge: false);

		Assert.True(strict.Succeeded, strict.FailureText);
		Assert.True(merged.Succeeded);
		Assert.False(merged.IsMerged);
		Assert.Empty(merged.GroupMates);
		Assert.Equal(strict.Replacements.Select(r => (r.Source.FileId, r.Target.Key.FileId)), merged.Replacements.Select(r => (r.Source.FileId, r.Target.Key.FileId)));
		Assert.Equal(strict.Hide, merged.Hide);
		Assert.Equal(strict.Signature, merged.Signature);
		Assert.Equal(strict.Warnings, merged.Warnings);
		Assert.Equal(strict.Info, merged.Info);
	}

	[Fact]
	public void MissingCellsMergeIntoTheTorsoCarrier_OthersStayOnTheirOwn()
	{
		// 双臂都被别的护甲占着：手臂没有独有部件。躯干、胯、腿仍然各自承载。
		var kits = new List<GameDataAuthorityKit>
		{
			T(own => own),
			Squatter(9, 0x6001, 0x6101, 0x7001, 0x7101),
		};

		Assert.False(SectionalExclusivePlanner.Build(SectionalExclusivePlanner.BuildIndex(kits), Source(), 1, false).Succeeded);
		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.True(plan.IsMerged);
		// 躯干两种身形各一个承载件，合并了「躯干 + 左臂 + 右臂」。
		Assert.Equal(2, plan.Merges.Count);
		Assert.All(plan.Merges, merge =>
		{
			Assert.True(merge.IsTorso);
			Assert.Equal(["0000000000000002", "0000000000000006", "0000000000000007"], merge.SourceFileIds);
		});
		Assert.Equal([0x2001UL, 0x2101UL], plan.Merges.Select(m => m.CarrierFileId).Order());
		// 胯与双腿仍是逐格承载。
		var plain = plan.Replacements.Where(r => !plan.Merges.Any(m => m.CarrierFileId == r.Target.Key.FileId)).ToArray();
		Assert.Equal(6, plain.Length);   // 胯 ×2、左腿 ×2、右腿 ×2
		Assert.DoesNotContain(plain, r => r.Source.FileId is "0000000000000006" or "0000000000000007");
		// 躯干承载，没有「第一人称」警告。
		Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("第一人称", StringComparison.Ordinal));
		// 占着手臂的别人护甲没有被牵连：承载件都是 T 独有。
		Assert.DoesNotContain(plan.Hide.Concat(plan.Replacements.Select(r => r.Target.Key.FileId)), file => file == 0x9);
		// 手臂自己的原版部件进隐藏集（它们被 Squatter 共用，所以只对 T 这个实例隐藏）。
		Assert.Contains(0x6001UL, plan.Hide);
		Assert.Contains(0x7101UL, plan.Hide);
	}

	[Fact]
	public void SignatureIsThePrimaryCarrier()
	{
		var kits = new List<GameDataAuthorityKit>
		{
			// 躯干是通用 unit（两种身形共用一个承载件），胯被占：缺口 = 胯。
			T(own => own.Where(p => p.Slot != 2).Append(P(2, 1, 3, 0x2000))),
			Squatter(9, 0x3001, 0x3101),
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		// 共用的躯干承载件：一份几何、一个承载件、两种身形。
		var merge = Assert.Single(plan.Merges);
		Assert.Equal(0x2000UL, merge.CarrierFileId);
		Assert.Equal([0, 1], merge.Bodies);
		Assert.Contains("0000000000000003", merge.SourceFileIds);
		Assert.Equal([0x2000UL], plan.Signature);   // 一个 all 就够，取主承载件，而不是 ID 最小的腿
	}

	[Fact]
	public void WithoutATorsoCarrierItFallsBackToHips_WithAFirstPersonWarning()
	{
		var kits = new List<GameDataAuthorityKit>
		{
			T(own => own),
			// 躯干与手臂都被占，只剩胯、腿有独有部件。
			Squatter(9, 0x2001, 0x2101, 0x6001, 0x6101, 0x7001, 0x7101),
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.All(plan.Merges, merge =>
		{
			Assert.Equal("胯部", merge.CarrierPart);
			Assert.False(merge.IsTorso);
			Assert.Contains("0000000000000003", merge.SourceFileIds);   // 胯自己的来源也并进去
			Assert.Contains("0000000000000002", merge.SourceFileIds);   // 躯干来源
		});
		Assert.Contains(plan.Warnings, warning => warning.Contains("第一人称", StringComparison.Ordinal));
		Assert.Equal([0x3001UL, 0x3101UL], plan.Signature.Order().ToArray());
	}

	[Fact]
	public void BodySpecificShoulderCarriersOfAGroup_GiveOneMergePerBody()
	{
		// 玩家神射手的形状：躯干、胯、腿、臂都被别的护甲共用；左肩 健壮/纤细 各一个，只属于「同名的两条记录」。
		var shared = new[] { 0x2001UL, 0x2101UL, 0x3001UL, 0x3101UL, 0x4001UL, 0x4101UL, 0x5001UL, 0x5101UL, 0x6001UL, 0x6101UL, 0x7001UL, 0x7101UL };
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, "FS-05“神射手”", GameDataEquipmentKitKind.Armor,
				[.. T(own => own).Pieces.Where(p => p.Slot != 8), P(8, 0, 0, 0x8001), P(8, 0, 1, 0x8101)]),
			Kit(2, "FS-05“神射手”", GameDataEquipmentKitKind.Armor, P(1, 0, 3, 0xcafe), P(8, 0, 0, 0x8001), P(8, 0, 1, 0x8101)),
			Squatter(9, shared),
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Equal([0x8001UL, 0x8101UL], plan.Merges.Select(m => m.CarrierFileId).Order());
		Assert.All(plan.Merges, merge => Assert.Equal("左肩", merge.CarrierPart));
		Assert.Equal(6, plan.Merges[0].SourceFileIds.Count);   // 躯干、胯、双腿、双臂全部合并
		Assert.Equal(["FS-05“神射手”（2）"], plan.GroupMates);
		Assert.Contains(plan.Warnings, warning => warning.Contains("第一人称", StringComparison.Ordinal));
		Assert.Equal(2, plan.Signature.Count);
	}

	[Fact]
	public void ARecordWhoseOnlyOwnerGroupIsItselfAndAMateListsTheMateAndWarns()
	{
		var shared = new[] { 0x2001UL, 0x2101UL, 0x3001UL, 0x3101UL, 0x4001UL, 0x4101UL, 0x5001UL, 0x5101UL, 0x6001UL, 0x6101UL, 0x7001UL, 0x7101UL };
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, "FS-05“神射手”", GameDataEquipmentKitKind.Armor,
				[.. T(own => own).Pieces.Where(p => p.Slot != 8), P(8, 0, 3, 0x8001)]),
			Kit(2, "FS-05“神射手”", GameDataEquipmentKitKind.Armor, P(1, 0, 3, 0xcafe), P(8, 0, 3, 0x8001)),
			Squatter(9, shared),
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		var merge = Assert.Single(plan.Merges);
		Assert.Equal(0x8001UL, merge.CarrierFileId);
		Assert.Equal(["FS-05“神射手”（2）"], plan.GroupMates);
		Assert.Contains(plan.Warnings, warning => warning.Contains("同名记录", StringComparison.Ordinal) && warning.Contains("也会显示", StringComparison.Ordinal));
		Assert.Contains(plan.Warnings, warning => warning.Contains("第一人称", StringComparison.Ordinal));

		// 不同名的记录不算一个组：同样的形状、名字不同 ⇒ 左肩被「别人」共用，不能承载。
		var strangers = new List<GameDataAuthorityKit>
		{
			kits[0],
			Kit(2, "Other", GameDataEquipmentKitKind.Armor, P(1, 0, 3, 0xcafe), P(8, 0, 3, 0x8001)),
			kits[2],
		};
		var blocked = Merge(strangers);
		Assert.False(blocked.Succeeded);
		Assert.Contains(blocked.Gaps, gap => gap.Contains("没有任何独有部件", StringComparison.Ordinal));
	}

	[Fact]
	public void AllShared_FailsWithAReasonPerBody()
	{
		var everything = Own().Select(piece => piece.UnitAssetKey.FileId).Where(file => file != 0xcafe).ToArray();
		var kits = new List<GameDataAuthorityKit> { T(own => own), Squatter(9, everything) };

		var plan = Merge(kits);

		Assert.False(plan.Succeeded);
		Assert.Empty(plan.Merges);
		Assert.Contains(plan.Gaps, gap => gap.Contains("健壮身形", StringComparison.Ordinal) && gap.Contains("没有任何独有部件", StringComparison.Ordinal));
		Assert.Contains(plan.Gaps, gap => gap.Contains("纤细身形", StringComparison.Ordinal));
		var candidates = SectionalExclusivePlanner.Evaluate(SectionalExclusivePlanner.BuildIndex(kits), Source(), headMerge: false);
		Assert.StartsWith("不可：", candidates.Single(candidate => candidate.ItemId == 1).Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void BodySpecificPrimariesProduceOneMergePerBody_EachWithItsOwnCarrier()
	{
		var kits = new List<GameDataAuthorityKit>
		{
			T(own => own),
			Squatter(9, 0x6001, 0x6101),   // 只有左臂缺
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.Equal(2, plan.Merges.Count);
		Assert.Equal([[0], [1]], plan.Merges.OrderBy(m => m.CarrierFileId).Select(m => m.Bodies.ToArray()).ToArray());
		Assert.Equal(2, plan.Signature.Count);   // 两种身形没有共用的承载件
	}

	[Fact]
	public void MergedCarriersAreNeverHiddenAndCapeIsNeverTouched()
	{
		var kits = new List<GameDataAuthorityKit> { T(own => own), Squatter(9, 0x6001, 0x6101) };

		var plan = Merge(kits);

		var carriers = plan.Merges.Select(m => m.CarrierFileId).Concat(plan.Replacements.Select(r => r.Target.Key.FileId)).ToHashSet();
		Assert.DoesNotContain(plan.Hide, carriers.Contains);
		Assert.DoesNotContain(0xcafeUL, plan.Hide);
		Assert.DoesNotContain(carriers, file => file == 0xcafe);
		// 每个承载件只出现一次，且只承载一个「合并后的」来源（宿主）。
		Assert.Equal(plan.Replacements.Count, plan.Replacements.Select(r => r.Target.Key.FileId).Distinct().Count());
	}

	[Fact]
	public void HeadMergeJoinsTheMergedUnitWhenTheTorsoHasNoSecondLayer()
	{
		// 躯干只有一层独有部件（头身合并需要第二层）：头并进躯干承载件。手臂各自独有。
		var kits = new List<GameDataAuthorityKit> { T(own => own) };

		var plan = Merge(kits, headMerge: true, head: true);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.True(plan.IsMerged);
		Assert.All(plan.Merges, merge =>
		{
			Assert.True(merge.IsTorso);
			Assert.Equal(["0000000000000002", "0000000000000001"], merge.SourceFileIds);
		});
	}

	[Fact]
	public void HeadMergeStillUsesTheSecondTorsoLayerWhenThereIsOne()
	{
		var kits = new List<GameDataAuthorityKit>
		{
			T(own => own.Append(P(2, 0, 0, 0x2000)).Append(P(2, 0, 1, 0x2100))),
		};

		var plan = Merge(kits, headMerge: true, head: true);

		Assert.True(plan.Succeeded, plan.FailureText);
		Assert.False(plan.IsMerged);   // P4a 就能逐格承载：完全不合并
		Assert.Contains(plan.Placements, placement => placement.Role == "头");
	}

	[Fact]
	public void OnlyTheBodiesTheTargetCoversNeedACarrier()
	{
		// 目标只有健壮身形的件：纤细身形不用找承载件。
		var kits = new List<GameDataAuthorityKit>
		{
			Kit(1, "T", GameDataEquipmentKitKind.Armor,
				P(1, 0, 3, 0xcafe), P(2, 1, 0, 0x2001), P(3, 1, 0, 0x3001), P(4, 1, 0, 0x4001), P(5, 1, 0, 0x5001), P(6, 1, 0, 0x6001), P(7, 1, 0, 0x7001)),
			Squatter(9, 0x6001),
		};

		var plan = Merge(kits);

		Assert.True(plan.Succeeded, plan.FailureText);
		var merge = Assert.Single(plan.Merges);
		Assert.Equal([0], merge.Bodies);
	}
}
