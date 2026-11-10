using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把用户对上一份组表做过的**改动**套到这一次新生成的组表上。
/// <para>
/// <b>为什么需要它。</b>组表（<c>components.json</c>）是用户可以改的，而重建/重转换会重新生成一份
/// 默认组表。以前只有"手建组"被保留，其余改动一律被冲掉：改名被冲回默认名、删掉的组又回来、
/// 设过的互斥丢空。
/// </para>
/// <para>
/// <b>靠什么认"还是那个组"：<see cref="SectionGroup.Key"/>。</b>生成器给每个自动组一个稳定键
/// （<c>source:尾巴</c> / <c>component:裙子</c>）。名字不能用（用户会改），成员也不能用
/// （段下标每次重建都会漂；unit 集合又太粗——同一个 unit 里可能同时有"本体""尾巴""鞋子"三个组，
/// 按 unit 匹配会把名字互相串掉，2026-11-10 实测就是这样）。旧文件里的组没有键，就按**同名的**兜底。
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
			foreach (var key in KeysOf(group.Key, group.Name))
				previousByKey.TryAdd(key, group);
		}

		var hiddenKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var tombstone in hidden ?? [])
		{
			foreach (var key in KeysOf(tombstone.Key, tombstone.Name))
				hiddenKeys.Add(key);
		}

		var groups = new List<SectionGroup>(fresh.Count);
		var suppressed = 0;
		foreach (var group in fresh)
		{
			var keys = KeysOf(group.Key, group.Name).ToArray();
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

	/// <summary>
	/// 匹配键：稳定键优先，名字兜底（旧文件没有 <c>key</c> 字段）。
	/// <b>成员一律不参与</b>——见类注释里那次"名字串掉"的实测。
	/// </summary>
	private static IEnumerable<string> KeysOf(string? key, string? name)
	{
		if (!string.IsNullOrWhiteSpace(key))
			yield return "key:" + key.Trim();
		if (!string.IsNullOrWhiteSpace(name))
			yield return "name:" + name.Trim();
	}

	/// <summary>把一个组的删除记进隐藏表（同一个键/名字的旧记录替换掉，避免越攒越多）。</summary>
	public static SectionGroupDocument WithHidden(SectionGroupDocument document, SectionGroup group)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(group);
		var tombstone = new SectionGroupTombstone(group.Name, group.Key);
		return document with
		{
			Hidden =
			[
				.. document.Hidden.Where(item =>
					!string.Equals(item.Name, tombstone.Name, StringComparison.Ordinal)
					&& !string.Equals(item.Key, tombstone.Key, StringComparison.Ordinal)),
				tombstone,
			],
		};
	}

	/// <summary>新建/改名成某个名字时，把同名（或同键）的删除记录撤掉——用户显式要它回来。</summary>
	public static SectionGroupDocument WithoutHidden(SectionGroupDocument document, string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (string.IsNullOrWhiteSpace(name))
			return document;
		var trimmed = name.Trim();
		return document.Hidden.Any(item => string.Equals(item.Name, trimmed, StringComparison.Ordinal))
			? document with
			{
				Hidden = [.. document.Hidden.Where(item => !string.Equals(item.Name, trimmed, StringComparison.Ordinal))],
			}
			: document;
	}
}
