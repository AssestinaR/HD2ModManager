using System.Globalization;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>多目标专属（P5）里一个没能进编译的目标：哪条记录、为什么。</summary>
public sealed record SectionalTargetFailure(uint ItemId, string Name, string Reason)
{
	public string Title => string.IsNullOrWhiteSpace(Name)
		? ItemId.ToString(CultureInfo.InvariantCulture)
		: $"{Name}（{ItemId.ToString(CultureInfo.InvariantCulture)}）";
}

/// <summary>
/// 把多个目标各自的专属规划合成一份写出计划（P5，规划 §6.1 / §3.2）：
/// 每个目标按 P4a/P4b 各自规划，承载件被多个目标选中时只写一次（比如同名组共用的左肩），规则按内容去重。
/// 纯函数，不碰磁盘；预览与编译共用，口径一致。
/// </summary>
internal sealed class SectionalExclusiveCombiner
{
	/// <summary>一次合并写出：来源 unit 们（宿主在前）合成一个 unit，落到这些承载件上。</summary>
	internal sealed record MergeGroup(IReadOnlyList<string> SourceFileIds, IReadOnlyList<ulong> Carriers);

	/// <summary>被接受的目标（按 item_id 升序）。</summary>
	public required IReadOnlyList<SectionalExclusivePlan> Accepted { get; init; }

	/// <summary>因为承载件被别的目标以不同内容占用而被拒绝的目标。</summary>
	public required IReadOnlyList<SectionalTargetFailure> Conflicts { get; init; }

	/// <summary>去重后的整体替换表（来源 → 承载件），含合并承载件的宿主那一条。</summary>
	public required IReadOnlyList<SectionalReplacement> Replacements { get; init; }

	/// <summary>去重后的逐格改名（不含合并承载件）。</summary>
	public required IReadOnlyList<SectionalReplacement> Renames { get; init; }

	/// <summary>去重后的合并产物：来源集合相同的合并只做一次，写到并集的承载件上。</summary>
	public required IReadOnlyList<MergeGroup> Merges { get; init; }

	/// <summary>写进 <c>Overwrite/</c> 的承载件份数（去重后的不同 unit 键）。</summary>
	public int CarrierCount { get; init; }

	private static string Join(IEnumerable<string> ids) => string.Join(",", ids);

	/// <summary>按 item_id 升序依次接受各目标；承载件已被别的目标以不同内容占用的目标被拒绝。</summary>
	public static SectionalExclusiveCombiner Combine(IEnumerable<SectionalExclusivePlan> plans)
	{
		ArgumentNullException.ThrowIfNull(plans);
		var accepted = new List<SectionalExclusivePlan>();
		var conflicts = new List<SectionalTargetFailure>();
		var claims = new Dictionary<ulong, string>();

		foreach (var plan in plans.Where(plan => plan.Succeeded).DistinctBy(plan => plan.ItemId).OrderBy(plan => plan.ItemId))
		{
			var mergedCarriers = plan.Merges.Select(merge => merge.CarrierFileId).ToHashSet();
			var mine = new List<(ulong Carrier, string Claim)>();
			foreach (var replacement in plan.Replacements.Where(item => !mergedCarriers.Contains(item.Target.Key.FileId)))
				mine.Add((replacement.Target.Key.FileId, "src:" + replacement.Source.FileId));
			foreach (var merge in plan.Merges)
				mine.Add((merge.CarrierFileId, "merge:" + Join(merge.SourceFileIds)));

			var clash = mine.FirstOrDefault(pair => claims.TryGetValue(pair.Carrier, out var existing)
				&& !string.Equals(existing, pair.Claim, StringComparison.Ordinal));
			if (clash.Claim is not null)
			{
				conflicts.Add(new SectionalTargetFailure(
					plan.ItemId, plan.Name,
					$"承载件 0x{clash.Carrier.ToString("x16", CultureInfo.InvariantCulture)} 已被前面选中的目标用不同的模型部位占用，两个目标不能同时写它"));
				continue;
			}

			foreach (var (carrier, claim) in mine) claims[carrier] = claim;
			accepted.Add(plan);
		}

		var allMergedCarriers = accepted.SelectMany(plan => plan.Merges).Select(merge => merge.CarrierFileId).ToHashSet();
		var replacements = accepted
			.SelectMany(plan => plan.Replacements)
			.DistinctBy(item => (item.Source.FileId, item.Target.Key))
			.ToArray();
		var renames = replacements.Where(item => !allMergedCarriers.Contains(item.Target.Key.FileId)).ToArray();

		var groups = new Dictionary<string, (IReadOnlyList<string> Sources, SortedSet<ulong> Carriers)>(StringComparer.Ordinal);
		foreach (var merge in accepted.SelectMany(plan => plan.Merges))
		{
			var key = Join(merge.SourceFileIds);
			if (!groups.TryGetValue(key, out var group)) groups[key] = group = (merge.SourceFileIds, new SortedSet<ulong>());
			group.Carriers.Add(merge.CarrierFileId);
		}

		return new SectionalExclusiveCombiner
		{
			Accepted = accepted,
			Conflicts = conflicts,
			Replacements = replacements,
			Renames = renames,
			Merges = [.. groups.Values.Select(group => new MergeGroup(group.Sources, [.. group.Carriers]))],
			CarrierCount = renames.Select(item => item.Target.Key.FileId).Concat(allMergedCarriers).Distinct().Count(),
		};
	}
}
