using System.Globalization;
using HD2ModCore.Domain;
using HD2SourceMigration.VpkConversion;

namespace HD2ModCli;

// 把"源侧的组件开关包"翻译成 HD2 的 section 掩码。
//
// 源侧的开关是**材质层**的：装上一份 `$no_draw` 覆盖，某个材质就不再绘制，用到它的几何随之消失
// （Manuka 用 `opt/` 影子目录，橘雪莉直接覆盖同名路径）。HD2 侧没有材质层开关，等价物是
// section 开关：把 section 的 numIndices 写成 0。
//
// 所以翻译只需要一件事：**这个开关藏的那些材质，落在哪几个 section 上**。
// 数据全在转换产物里——`sections.json` 的 sourceMaterial 就是源材质裸名，一次 join 就够。
//
// 两个刻意不做的取舍：
//   · 不按名字猜"这两个开关是不是同一件事"，只按掩码判。共用一个材质的两个部件本来就会一起关，
//     按名字合并会把两件不同的东西并成一个。
//   · 不从 bodypart 推导开关。merge 模式下配件已经并进身躯槽位，几何层面的"这个 bodypart
//     用哪些材质"并不能回答"哪些 section 属于它"；而作者那批 `XXX关` 包本来就是同一批组件的
//     1:1 枚举（62 轮实测），按包识别既准确又不重复。

internal static class CliVpkComponentSwitches
{
	/// <summary>
	/// 把一批开关包解析成掩码。<paramref name="sections"/>
	/// 必须来自<b>同一个</b> patch，否则"材质在哪几个 section 上"这个 join 没有意义。
	/// </summary>
	public static VpkComponentSwitchDocument Resolve(
		IReadOnlyList<VpkComponentSwitch> switches,
		WardrobeSectionMapDocument sections)
	{
		ArgumentNullException.ThrowIfNull(switches);
		ArgumentNullException.ThrowIfNull(sections);

		// 源材质裸名 → 它在 sections.json 里出现的那些 (unit, section)。
		// 按**名字**匹配而不是按材质 id：id 是哈希，而"同名不同目录"与"多个源材质并进一个 id"
		// 都会让 id 层面的对应关系不唯一；名字是转换时逐行写进 sections.json 的，一一对应。
		var targetsByMaterial = new Dictionary<string, List<VpkComponentSwitchTarget>>(StringComparer.OrdinalIgnoreCase);
		foreach (var unit in sections.Units)
		{
			foreach (var section in unit.Sections)
			{
				var name = section.SourceMaterial;
				if (string.IsNullOrWhiteSpace(name))
					continue;
				if (!targetsByMaterial.TryGetValue(name, out var list))
				{
					list = [];
					targetsByMaterial[name] = list;
				}

				var existing = list.FirstOrDefault(target => string.Equals(target.Unit, unit.FileId, StringComparison.Ordinal));
				if (existing is null)
					list.Add(new VpkComponentSwitchTarget(unit.FileId, [section.Index]));
				else
					list[list.IndexOf(existing)] = existing with { Sections = [.. existing.Sections, section.Index] };
			}
		}

		var entries = new List<VpkComponentSwitchEntry>();
		var unmatched = new List<VpkComponentSwitchUnmatched>();
		// 掩码 → 已合并的那一条。键是排好序的 "unit:idx" 列表，与遍历顺序无关。
		var byMask = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var item in switches)
		{
			var stems = item.MaterialStems
				.Select(stem => Path.GetFileNameWithoutExtension(stem))
				.Where(stem => !string.IsNullOrWhiteSpace(stem))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var disabled = new List<VpkComponentSwitchTarget>();
			foreach (var stem in stems)
			{
				if (!targetsByMaterial.TryGetValue(stem, out var found))
					continue;
				foreach (var target in found)
				{
					var existing = disabled.FirstOrDefault(candidate => string.Equals(candidate.Unit, target.Unit, StringComparison.Ordinal));
					if (existing is null)
						disabled.Add(target with { Sections = [.. target.Sections.Order()] });
					else
						disabled[disabled.IndexOf(existing)] = existing with
						{
							Sections = [.. existing.Sections.Concat(target.Sections).Distinct().Order()],
						};
				}
			}

			if (disabled.Count == 0)
			{
				unmatched.Add(new VpkComponentSwitchUnmatched(
					item.DisplayName,
					Path.GetFileName(item.VpkPath),
					$"它要隐藏的材质（{string.Join("、", stems)}）在这个 patch 里找不到——"
					+ "多半是它属于另一个角色/另一个材质的包，或者这个包只带材质没带模型。"));
				continue;
			}

			var mask = string.Join(';', disabled
				.OrderBy(target => target.Unit, StringComparer.Ordinal)
				.Select(target => target.Unit + ":" + string.Join(',', target.Sections)));
			var entry = new VpkComponentSwitchEntry(
				item.DisplayName,
				Path.GetFileName(item.VpkPath),
				stems,
				[.. disabled.OrderBy(target => target.Unit, StringComparer.Ordinal)]);
			if (byMask.TryGetValue(mask, out var index))
			{
				// 同一个掩码 = 同一件事（两个包藏的是同一组材质）。合并而不是并列：
				// 并列会让用户看到两个开关，点哪一个都关掉同一批几何。
				var merged = entries[index];
				entries[index] = merged with
				{
					Materials = [.. merged.Materials.Concat(stems).Distinct(StringComparer.OrdinalIgnoreCase)],
					SourceVpk = merged.SourceVpk + "、" + entry.SourceVpk,
				};
				continue;
			}

			byMask[mask] = entries.Count;
			entries.Add(entry);
		}

		return new VpkComponentSwitchDocument(
			VpkComponentSwitchDocument.CurrentSchema,
			[.. entries.OrderBy(entry => entry.Name, StringComparer.CurrentCulture)],
			unmatched);
	}

	/// <summary>一句话结论，给转换报告与日志用。</summary>
	public static string Summarize(VpkComponentSwitchDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.Switches.Count == 0 && document.Unmatched.Count == 0)
			return "没有组件开关";
		var sections = document.Switches.Sum(item => item.SectionCount);
		var text = $"组件开关 {document.Switches.Count.ToString(CultureInfo.InvariantCulture)} 个"
			+ $"（共隐藏 {sections.ToString(CultureInfo.InvariantCulture)} 块几何）";
		if (document.Unmatched.Count != 0)
			text += $"，{document.Unmatched.Count.ToString(CultureInfo.InvariantCulture)} 个没匹配上";
		return text;
	}
}
