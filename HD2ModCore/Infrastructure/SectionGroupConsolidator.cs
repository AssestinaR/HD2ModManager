using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一次「把 section 组对齐到新几何」的结果，只为把话说清楚（日志/报告用）。</summary>
/// <param name="Document">对齐后的文档；<c>Groups</c> 为空表示这个 Mod 现在没有任何开关。</param>
/// <param name="Refreshed">成员位置被重算过、且确实变了的组数。</param>
/// <param name="Dropped">要隐藏的材质在新几何里已经找不到、被丢掉的组数。</param>
/// <param name="KeptManual">保留下来并重算过的手动组数。</param>
public sealed record SectionGroupConsolidation(
	SectionGroupDocument Document,
	int Refreshed,
	int Dropped,
	int KeptManual);

/// <summary>
/// 把 `components.json`（section 组）对齐到**刚转出来的** `sections.json`。
/// <para>
/// <b>为什么必须有这一步。</b>组的成员是 <c>(unit, section 下标)</c>，而
/// <c>sections.json</c> 每次重建都会变（材质 id、section 边界、三角形数）。所以
/// "组"和"树"必须**在同一次转换里**产生，否则下标迟早漂到别的部件上 ——
/// 2026-11-09 实测：Lilium 的 12 组里 7 组指错节（「手套Glove」记 <c>0006:2</c>，
/// 而那个材质现在落在 <c>0006:10</c> 与 <c>0007:10</c>），点开关关掉的是别的东西。
/// </para>
/// <para>
/// <b>做法：不信下标，只信名字。</b>组里已经记着它要控制哪些**源材质裸名**
/// （<see cref="SectionGroup.Materials"/>），而新 `sections.json` 的每个 section 都带着
/// 自己的 <c>sourceMaterial</c>。一次 join 就能把成员重算出来，索引漂移因此彻底消失。
/// </para>
/// <para>
/// <b>三类组的取舍：</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>这次转出来的组</b>（源侧 <c>$no_draw</c> 开关包翻译而来）是权威：直接用它，
/// 只在同名旧组存在时沿用它的 <c>id</c>（开关的选中态、互斥关系都按 id 记，不能让改名换 id）。
/// </item>
/// <item>
/// <b>手建组</b>（<see cref="SectionGroupSource.Manual"/>）转换器永远不会产出，
/// 所以从旧文件里捞出来、重算成员后保留。
/// </item>
/// <item>
/// <b>旧文件里的自动组</b>：只有<em>这次一个组都没转出来</em>时才保留（开关包不在手边，
/// 它是"这个 Mod 有哪些开关"的唯一记录）；否则视为上一轮残余丢掉，免得同一个开关出现两次。
/// </item>
/// </list>
/// </summary>
public static class SectionGroupConsolidator
{
	/// <param name="fresh">这次转换写出来的组文档（<c>convert/&lt;vpk&gt;/components.json</c>）；没有就是 <c>null</c>。</param>
	/// <param name="previous">盘上已有的那份（换载荷时被保留下来）；没有就是 <c>null</c>。</param>
	/// <param name="sections">**刚转出来的** <c>sections.json</c>。</param>
	public static SectionGroupConsolidation Consolidate(
		SectionGroupDocument? fresh,
		SectionGroupDocument? previous,
		WardrobeSectionMapDocument sections)
	{
		ArgumentNullException.ThrowIfNull(sections);

		var index = BuildMaterialIndex(sections);
		var groups = new List<SectionGroup>();
		var unmatched = new List<SectionGroupUnmatched>(fresh?.Unmatched ?? []);
		var refreshed = 0;
		var dropped = 0;
		var keptManual = 0;

		// ① 这次转出来的：权威。同名旧组存在时沿用它的 id。
		foreach (var group in fresh?.Groups ?? [])
		{
			var identity = FindByName(previous, group.Name);
			var resolved = Resolve(group, (identity?.Id) is { Length: > 0 } id ? id : null, index, out var changed);
			if (resolved is null)
			{
				dropped++;
				unmatched.Add(Explained(group, "它要隐藏的材质在这份新几何里一个都没找到（源包换了？）"));
				continue;
			}

			if (changed) refreshed++;
			groups.Add(resolved);
		}

		// ② 手建组：转换器不会产出它们，只能从旧文件捞。
		foreach (var group in previous?.Groups ?? [])
		{
			if (group.Source != SectionGroupSource.Manual) continue;
			if (groups.Any(existing => string.Equals(existing.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
				continue;

			var resolved = Resolve(group, StableId(group), index, out _);
			if (resolved is null)
			{
				dropped++;
				unmatched.Add(Explained(group, "手建的组：它包含的材质在这份新几何里一个都没找到"));
				continue;
			}

			keptManual++;
			groups.Add(resolved);
		}

		// ③ 旧文件里的自动组：只有在这次没转出任何组时才保留（并重算），否则是上一轮残余。
		if (fresh is not { Groups.Count: > 0 })
		{
			foreach (var group in previous?.Groups ?? [])
			{
				if (group.Source == SectionGroupSource.Manual) continue;
				if (groups.Any(existing => string.Equals(existing.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
					continue;

				var resolved = Resolve(group, StableId(group), index, out var changed);
				if (resolved is null)
				{
					dropped++;
					unmatched.Add(Explained(group, "源侧开关包不在这次转换里，而它要隐藏的材质也找不到了"));
					continue;
				}

				if (changed) refreshed++;
				groups.Add(resolved);
			}
		}

		var document = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			groups,
			unmatched);
		// 用户对自动组做过的改动（改名 / 互斥 / 删除）在这里落地：
		// 成员与来源以这次转出来的为准，名字、互斥、"删过"以用户的为准。
		var customized = SectionGroupCustomizations.Apply(document.Groups, previous?.Groups, previous?.Hidden);
		return new SectionGroupConsolidation(
			document with { Groups = customized.Groups, Hidden = previous?.Hidden ?? [] },
			refreshed,
			dropped,
			keptManual);
	}

	/// <summary>源材质裸名 → 它在**这份** sections.json 里出现过的 <c>(unit, section 下标)</c>。</summary>
	private static Dictionary<string, List<SectionGroupTarget>> BuildMaterialIndex(WardrobeSectionMapDocument sections)
	{
		var index = new Dictionary<string, List<SectionGroupTarget>>(StringComparer.OrdinalIgnoreCase);
		foreach (var unit in sections.Units)
		{
			foreach (var section in unit.Sections)
			{
				var material = section.SourceMaterial;
				if (string.IsNullOrWhiteSpace(material)) continue;
				if (!index.TryGetValue(material, out var targets))
				{
					targets = [];
					index[material] = targets;
				}

				var existing = targets.FirstOrDefault(target => string.Equals(target.Unit, unit.FileId, StringComparison.Ordinal));
				if (existing is null) targets.Add(new SectionGroupTarget(unit.FileId, [section.Index]));
				else targets[targets.IndexOf(existing)] = existing with { Sections = [.. existing.Sections, section.Index] };
			}
		}

		// 每张表内部按 section 下标排好：输出稳定，diff 才有意义。
		foreach (var targets in index.Values)
		{
			for (var i = 0; i < targets.Count; i++)
				targets[i] = targets[i] with { Sections = [.. targets[i].Sections.Distinct().Order()] };
		}

		return index;
	}

	/// <summary>按材质名重算成员；一个都找不到时返回 <c>null</c>（这组已经没有意义）。</summary>
	private static SectionGroup? Resolve(
		SectionGroup group,
		string? stableId,
		Dictionary<string, List<SectionGroupTarget>> index,
		out bool changed)
	{
		changed = false;
		var merged = new List<SectionGroupTarget>();
		foreach (var material in group.Materials)
		{
			var bare = Path.GetFileNameWithoutExtension(material);
			if (string.IsNullOrWhiteSpace(bare) || !index.TryGetValue(bare, out var targets)) continue;
			foreach (var target in targets)
			{
				var existing = merged.FirstOrDefault(candidate => string.Equals(candidate.Unit, target.Unit, StringComparison.Ordinal));
				if (existing is null) merged.Add(target);
				else merged[merged.IndexOf(existing)] = existing with
				{
					Sections = [.. existing.Sections.Concat(target.Sections).Distinct().Order()],
				};
			}
		}

		if (merged.Count == 0) return null;

		var members = merged.OrderBy(target => target.Unit, StringComparer.Ordinal).ToArray();
		changed = !SameMembers(group.Members, members);
		return group with
		{
			Members = members,
			Id = stableId ?? group.Id,
		};
	}

	private static bool SameMembers(IReadOnlyList<SectionGroupTarget> left, IReadOnlyList<SectionGroupTarget> right)
	{
		if (left.Count != right.Count) return false;
		for (var i = 0; i < left.Count; i++)
		{
			if (!string.Equals(left[i].Unit, right[i].Unit, StringComparison.Ordinal)) return false;
			if (!left[i].Sections.SequenceEqual(right[i].Sections)) return false;
		}

		return true;
	}

	private static SectionGroup? FindByName(SectionGroupDocument? document, string name)
		=> document?.Groups.FirstOrDefault(group =>
			string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 旧组的稳定 id：有显式 id 用它；没有（旧格式写的）就把它**当时**算出来的那个固化下来。
	/// <para>
	/// 为什么不能留给 <c>WriteAsync</c> 现算：现算是按 <c>(名字, 成员掩码)</c> 算的，
	/// 而这次成员位置刚好变了 —— 不固化的话，用户存在 <c>wardrobe.json</c> 里的"这个开关是关着的"
	/// 会在重建之后对不上号（开关悄悄变回打开）。
	/// </para>
	/// </summary>
	private static string StableId(SectionGroup group)
		=> group.Id is { Length: > 0 } id ? id : group.EffectiveId;

	private static SectionGroupUnmatched Explained(SectionGroup group, string reason)
		=> new(group.Name, group.SourceVpk, reason);
}
