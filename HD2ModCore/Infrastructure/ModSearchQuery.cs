using System;
using System.Collections.Generic;
using System.Linq;

namespace HD2ModCore.Infrastructure;

// Purpose: 把"自由标签 + 输入框草稿"拼成一条有效查询。没有 UI 依赖，可被单测钉住。
//
// 规则（UI 规格 S5–S7）：
// - 片段按空白切分（连续空白视作一个分隔），去首尾空白；
// - 忽略大小写去重，保留第一次出现的拼写与顺序；
// - 自由标签在前、草稿在后，用单个空格连接——正好是 ModSearchMatcher 已有的"每个词都要命中"。
public static class ModSearchQuery
{
	/// <summary>把任意批次的标签/草稿切成规范化的词：去空白、去空串、忽略大小写去重、保持顺序。</summary>
	public static IReadOnlyList<string> Normalize(IEnumerable<string?>? values)
	{
		var result = new List<string>();
		if (values is null) return result;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var value in values)
		{
			if (string.IsNullOrWhiteSpace(value)) continue;
			foreach (var word in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (seen.Add(word)) result.Add(word);
			}
		}

		return result;
	}

	/// <summary>有效查询：自由标签 + 草稿。两者都为空时返回空串（调用方据此判断"没有查询"）。</summary>
	public static string Compose(IReadOnlyList<string>? freeTags, string? draft)
	{
		var parts = new List<string>((freeTags?.Count ?? 0) + 1);
		if (freeTags is not null) parts.AddRange(freeTags);
		parts.Add(draft ?? string.Empty);
		return string.Join(' ', Normalize(parts));
	}
}
