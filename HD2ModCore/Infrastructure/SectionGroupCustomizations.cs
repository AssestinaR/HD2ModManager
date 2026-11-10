using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把用户对上一份组表做过的**改动**套到这一次新生成的组表上。
/// <para>
/// <b>为什么需要它。</b>组表（<c>components.json</c>）是用户可以改的，而重建/重转换会重新生成一份
/// 默认组表。以前只有"手建组"被保留，其余改动一律被冲掉：
/// </para>
/// <list type="bullet">
/// <item><b>改名</b>：把「尾巴」改成「尾巴（大）」，重建后又变回「尾巴」；</item>
/// <item><b>删除</b>：删掉「翅膀」，重建后它又回来了（用户以为删除没生效）；</item>
/// <item><b>互斥设置</b>：给「大衣 健壮 / 大衣 纤细」设了互斥组，重建后丢空。</item>
/// </list>
/// <para>
/// <b>怎么认"还是那个组"。</b>不能用段下标（每次重建都会漂），也不能只用名字（用户改过名）。
/// 这里用两个键任一命中：<c>名字</c>、或 <c>成员 unit 集合</c>（unit 比段稳定得多）。
/// 命中上一份的组 ⇒ 沿用它的名字 / 互斥设置 / id；命中删除记录 ⇒ 这一次不生成它。
/// </para>
/// </summary>
public static class SectionGroupCustomizations
{
	/// <param name="Groups">套用后的组表（成员与来源取自 <paramref name="fresh"/>）。</param>
	/// <param name="Suppressed">被"用户删过"抑制掉的组数（不报错，那是用户要的）。</param>
	public sealed record Result(IReadOnlyList<SectionGroup> Groups, int Suppressed);

	public static Result Apply(
		IReadOnlyList<SectionGroup> fresh,
		IReadOnlyList<SectionGroup>? previous,
		IReadOnlyList<SectionGroupTombstone>? hidden)
	{
		ArgumentNullException.ThrowIfNull(fresh);

		var previousByKey = new Dictionary<string, SectionGroup>(StringComparer.Ordinal);
		foreach (var group in previous ?? [])
		{
			foreach (var key in KeysOf(group.Name, group.Members))
				previousByKey.TryAdd(key, group);
		}

		var hiddenKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var tombstone in hidden ?? [])
		{
			// 删除记录只带 unit 集合：段下标已经不在了，不该拿来当键。
			foreach (var key in KeysOf(tombstone.Name, tombstone.Members))
				hiddenKeys.Add(key);
		}

		var groups = new List<SectionGroup>(fresh.Count);
		var suppressed = 0;
		foreach (var group in fresh)
		{
			var keys = KeysOf(group.Name, group.Members).ToArray();
			if (keys.Any(hiddenKeys.Contains))
			{
				suppressed++;
				continue;
			}

			SectionGroup? matched = null;
			foreach (var key in keys)
			{
				if (previousByKey.TryGetValue(key, out matched))
					break;
				matched = null;
			}

			groups.Add(matched is null ? group : Adopt(group, matched));
		}

		return new Result(groups, suppressed);
	}

	/// <summary>
	/// 沿用上一份的"用户改动"，但**成员、来源、模块归属一律以这一次为准**：
	/// 这些是产物的属性，不是用户的设置。
	/// </summary>
	private static SectionGroup Adopt(SectionGroup fresh, SectionGroup previous)
		=> fresh with
		{
			Name = previous.Name,
			ExclusiveGroup = previous.ExclusiveGroup,
			Id = previous.Id is { Length: > 0 } id ? id : fresh.Id,
		};

	/// <summary>匹配键：名字一个、成员 unit 集合一个（段下标刻意不参与）。</summary>
	private static IEnumerable<string> KeysOf(string? name, IReadOnlyList<SectionGroupTarget>? members)
	{
		if (!string.IsNullOrWhiteSpace(name))
			yield return "name:" + name.Trim();
		var units = (members ?? [])
			.Select(member => member.Unit)
			.Where(unit => !string.IsNullOrWhiteSpace(unit))
			.Select(unit => unit.Trim().ToLowerInvariant())
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		if (units.Length != 0)
			yield return "units:" + string.Join(",", units);
	}

	/// <summary>把一个组的删除记进隐藏表（重复的替换掉，避免越攒越多）。</summary>
	public static SectionGroupDocument WithHidden(SectionGroupDocument document, SectionGroup group)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(group);
		return document with
		{
			Hidden = [.. document.Hidden.Where(item => !string.Equals(item.Name, group.Name, StringComparison.Ordinal)),
				new SectionGroupTombstone(group.Name, group.Members)],
		};
	}

	/// <summary>新建/改名成某个名字时，把同名的删除记录撤掉（用户显式要它回来）。</summary>
	public static SectionGroupDocument WithoutHidden(SectionGroupDocument document, string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (string.IsNullOrWhiteSpace(name))
			return document;
		return document.Hidden.Any(item => string.Equals(item.Name, name.Trim(), StringComparison.Ordinal))
			? document with
			{
				Hidden = [.. document.Hidden.Where(item => !string.Equals(item.Name, name.Trim(), StringComparison.Ordinal))],
			}
			: document;
	}
}
