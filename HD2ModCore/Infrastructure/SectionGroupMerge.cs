using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 重转换之后，组表（<c>components.json</c>）怎么合。
/// <para>
/// <b>为什么不能"新产物直接覆盖"。</b>组表是**用户可以改的**（改名、新建、删除），
/// 而重转换会重新生成一份默认组表。整份覆盖 = 用户改过的部件树没了；完全不覆盖 = 用户永远看不到
/// 新规则生成的组（2026-11-10 实机就是这样：重构建之后界面还是上一轮的 21 个组，
/// 其中 9 个组的 unit 早就不在产物里）。
/// </para>
/// <para>
/// <b>口径：</b>
/// </para>
/// <list type="number">
/// <item><b>这次转出来的组是权威</b>——它们是按这次产物现算的，名字与成员都对得上。</item>
/// <item><b>手建组</b>（<see cref="SectionGroupSource.Manual"/>）保留，成员按这次产物重算；
/// 一块几何都不剩的组丢掉，并说明原因（否则界面上会留下一个点了没反应的复选框）。</item>
/// <item>旧文件里的**自动组**一律丢掉：它们要么被这次的新组取代，要么对应的来源这次没进产物
/// （那种情况由转换器自己写进 <see cref="SectionGroupDocument.Unmatched"/>）。</item>
/// </list>
/// </summary>
public static class SectionGroupMerge
{
	public static SectionGroupDocument Merge(
		SectionGroupDocument fresh,
		SectionGroupDocument? previous,
		WardrobeSectionMapDocument sections)
	{
		ArgumentNullException.ThrowIfNull(fresh);
		ArgumentNullException.ThrowIfNull(sections);

		var groups = new List<SectionGroup>(fresh.Groups.Count + (previous?.Groups.Count ?? 0));
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var group in fresh.Groups)
		{
			groups.Add(group);
			names.Add(group.Name);
		}

		var unmatched = new List<SectionGroupUnmatched>(fresh.Unmatched);
		foreach (var group in previous?.Groups ?? [])
		{
			if (group.Source != SectionGroupSource.Manual) continue;
			if (!names.Add(group.Name)) continue;

			var pruned = Prune(group, sections);
			if (pruned is null)
			{
				unmatched.Add(new SectionGroupUnmatched(group.Name, group.SourceVpk,
					"手建的组：它包含的几何在这次产物里一个都没找到（来源换过？），已跳过。"));
				continue;
			}

			groups.Add(pruned);
		}

		// 用户对**自动组**做过的改动（改名 / 互斥设置 / 删除）在这里落地：
		// 成员以这一次为准，名字与互斥与"删过"以用户的为准。
		var customized = SectionGroupCustomizations.Apply(groups, previous?.Groups, previous?.Hidden);
		return new SectionGroupDocument(SectionGroupDocument.CurrentSchema, customized.Groups, unmatched)
		{
			Hidden = previous?.Hidden ?? [],
		};
	}

	/// <summary>
	/// 把组成员剪到"这次产物里真的存在"的 (unit, section) 上。一个都不剩返回 <c>null</c>。
	/// <para>
	/// 只剪不猜：unit 不在产物里就丢掉那一条，section 下标越界就丢掉那一段——
	/// 换过来源之后旧下标会指向别的几何，留着比丢掉更危险（关掉的会是别的部件）。
	/// </para>
	/// </summary>
	private static SectionGroup? Prune(SectionGroup group, WardrobeSectionMapDocument sections)
	{
		var members = new List<SectionGroupTarget>(group.Members.Count);
		foreach (var target in group.Members)
		{
			var unit = sections.FindUnit(target.Unit);
			if (unit is null) continue;
			var existing = unit.Sections.Select(section => section.Index).ToHashSet();
			var kept = target.Sections.Where(existing.Contains).Distinct().Order().ToArray();
			if (kept.Length == 0) continue;
			members.Add(new SectionGroupTarget(unit.FileId, kept));
		}

		return members.Count == 0 ? null : group with { Members = members };
	}
}
