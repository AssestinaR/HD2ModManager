using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 组表的结构性编辑：新建 / 改名 / 删除 / 设互斥组。
/// <para>
/// <b>全是纯函数。</b>输入一份文档、输出一份新文档，不碰文件、不碰界面 ——
/// 这样"点一下到底改了什么"能离线测，而落盘与刷新由调用方决定。
/// </para>
/// <para>
/// <b>为什么单独抽出来。</b>这三件事都会改动 <c>components.json</c>，而它同时是
/// "用户建的组"和"转换器识别出来的组"的容器。把规则写在一处，界面、CLI、将来的拆件器
/// 才不会各写一份然后对不上（尤其是 id 生成与成员归一）。
/// </para>
/// </summary>
public static class SectionGroupEditor
{
	/// <summary>名字长度上限。超过就截断而不是报错——用户粘一大段进来时不该丢操作。</summary>
	public const int MaximumNameLength = 64;

	/// <summary>
	/// 新建一个组。<paramref name="members"/> 会被归一（同 unit 合并、节去重排序、空节丢弃）；
	/// 归一后一个节都不剩就<b>什么都不做</b>并返回 <c>null</c>。
	/// </summary>
	/// <returns>新文档与新组；没有可用的成员时返回 <c>null</c>。</returns>
	public static (SectionGroupDocument Document, SectionGroup Group)? Create(
		SectionGroupDocument document,
		string name,
		IReadOnlyList<SectionGroupTarget> members,
		SectionGroupSource source = SectionGroupSource.Manual,
		string? exclusiveGroup = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(members);

		var normalized = Normalize(members);
		if (normalized.Count == 0)
			return null;

		var trimmed = TrimName(name);
		if (trimmed.Length == 0)
			trimmed = "新组";

		var group = new SectionGroup(trimmed, string.Empty, [], normalized)
		{
			Id = UniqueId(document, SectionGroupIdentity.Derive(trimmed, normalized)),
			Source = source,
			ExclusiveGroup = string.IsNullOrWhiteSpace(exclusiveGroup) ? null : exclusiveGroup.Trim(),
		};

		return (document with { Groups = [.. document.Groups, group] }, group);
	}

	/// <summary>改名。**id 不变**——它是界面选中态与互斥关系的锚。</summary>
	/// <returns>改完的文档；id 找不到或名字为空时返回 <c>null</c>。</returns>
	public static SectionGroupDocument? Rename(SectionGroupDocument document, string id, string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(id);

		var trimmed = TrimName(name);
		if (trimmed.Length == 0)
			return null;
		var index = IndexOf(document, id);
		if (index < 0)
			return null;

		var groups = document.Groups.ToArray();
		// 先固化 id：旧格式读进来的组 Id 是空的，改完名之后必须有个真 id 落盘，
		// 否则"改名后 id 跟着变"这条又回来了。
		groups[index] = groups[index] with { Id = groups[index].EffectiveId, Name = trimmed };
		return document with { Groups = groups };
	}

	/// <summary>
	/// 删除一个组。<b>只删定义，不动几何</b> —— 它的成员 section 会回到"默认可见"，
	/// 因为开关状态只记"哪些被关掉"，而删组不会往那里面加任何东西。
	/// </summary>
	/// <returns>删完的文档；id 找不到时返回 <c>null</c>。</returns>
	public static SectionGroupDocument? Remove(SectionGroupDocument document, string id)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(id);

		var index = IndexOf(document, id);
		if (index < 0)
			return null;
		return document with
		{
			Groups = [.. document.Groups.Where((_, i) => i != index)],
		};
	}

	/// <summary>设置（或清除）互斥组名。<c>null</c>/空白 = 不参与互斥。</summary>
	public static SectionGroupDocument? SetExclusiveGroup(SectionGroupDocument document, string id, string? exclusiveGroup)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(id);

		var index = IndexOf(document, id);
		if (index < 0)
			return null;

		var groups = document.Groups.ToArray();
		groups[index] = groups[index] with
		{
			Id = groups[index].EffectiveId,
			ExclusiveGroup = string.IsNullOrWhiteSpace(exclusiveGroup) ? null : exclusiveGroup.Trim(),
		};
		return document with { Groups = groups };
	}

	/// <summary>
	/// 成员归一：同 unit 的节合并、去重、排序；空 unit 键或空节列表丢弃。
	/// <para>
	/// 必须归一，否则"同一个 unit 出现两条成员记录"会让 <see cref="SectionGroup.SectionCount"/> 重复计数，
	/// 而 <see cref="SectionGroupIdentity.Derive"/> 会因此算出不同的 id。
	/// </para>
	/// </summary>
	public static IReadOnlyList<SectionGroupTarget> Normalize(IReadOnlyList<SectionGroupTarget> members)
	{
		ArgumentNullException.ThrowIfNull(members);
		var byUnit = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
		foreach (var target in members)
		{
			if (string.IsNullOrWhiteSpace(target.Unit) || target.Sections.Count == 0)
				continue;
			var unit = NormalizeUnit(target.Unit);
			if (!byUnit.TryGetValue(unit, out var sections))
			{
				sections = [];
				byUnit[unit] = sections;
			}

			foreach (var section in target.Sections)
			{
				if (section >= 0)
					sections.Add(section);
			}
		}

		return
		[
			.. byUnit
				.Where(pair => pair.Value.Count != 0)
				.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => new SectionGroupTarget(pair.Key, [.. pair.Value])),
		];
	}

	private static int IndexOf(SectionGroupDocument document, string id)
	{
		for (var index = 0; index < document.Groups.Count; index++)
		{
			if (string.Equals(document.Groups[index].EffectiveId, id, StringComparison.Ordinal))
				return index;
		}

		return -1;
	}

	/// <summary>id 撞了就加后缀。撞 id 只可能是"名字与成员都相同"，那时给用户两个一模一样的组也没意义，但仍然允许。</summary>
	private static string UniqueId(SectionGroupDocument document, string candidate)
	{
		var existing = document.Groups
			.Select(group => group.EffectiveId)
			.ToHashSet(StringComparer.Ordinal);
		if (!existing.Contains(candidate))
			return candidate;
		for (var suffix = 2; ; suffix++)
		{
			var next = candidate + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (!existing.Contains(next))
				return next;
		}
	}

	private static string TrimName(string name)
	{
		var trimmed = (name ?? string.Empty).Trim();
		return trimmed.Length <= MaximumNameLength ? trimmed : trimmed[..MaximumNameLength];
	}

	private static string NormalizeUnit(string unit)
	{
		var text = unit.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			text = text[2..];
		return text.ToLowerInvariant();
	}
}
