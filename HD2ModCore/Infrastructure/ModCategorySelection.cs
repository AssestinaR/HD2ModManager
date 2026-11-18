using System;
using System.Collections.Generic;
using System.Linq;

namespace HD2ModCore.Infrastructure;

// Purpose: 分类多选状态（并集语义）。没有 UI 依赖，供模组库页与配置页共用。
//
// 为什么单独放在 Core：这套"点一下加、再点一下减、一个都没选就恒真"的规则是标签式搜索里
// 最容易出错的地方，而它以前只是 ModCategoryFilter 里的一个 _selected 字段（Manager 没有
// 测试工程）。搬到 Core 之后可以直接被单测钉住。
//
// 语义（与 UI 规格 S4 / D2 一致）：
// - 空集合 = 不筛（恒真）；
// - 非空集合 = 取并集：Mod 的分类键与任一选中键相同即命中；
// - 键按加入顺序保留、不重复（忽略大小写比较，但保存第一次写入的原始拼写）。
public sealed class ModCategorySelection
{
	private readonly List<string> keys = new();

	public IReadOnlyList<string> Keys => keys;

	public bool IsActive => keys.Count != 0;

	public static string Normalize(string? key) => key?.Trim() ?? string.Empty;

	public bool Contains(string? key)
	{
		var normalized = Normalize(key);
		return normalized.Length != 0 && keys.Contains(normalized, StringComparer.OrdinalIgnoreCase);
	}

	public bool Toggle(string? key) => Contains(key) ? Remove(key) : Add(key);

	public bool Add(string? key)
	{
		var normalized = Normalize(key);
		if (normalized.Length == 0 || Contains(normalized)) return false;
		keys.Add(normalized);
		return true;
	}

	public bool Remove(string? key)
	{
		var normalized = Normalize(key);
		if (normalized.Length == 0) return false;
		var index = keys.FindIndex(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
		if (index < 0) return false;
		keys.RemoveAt(index);
		return true;
	}

	public bool Clear()
	{
		if (keys.Count == 0) return false;
		keys.Clear();
		return true;
	}

	/// <summary>
	/// 丢掉已经不存在的键（自建分类被删除后必须摘掉，否则会留下一个永远筛不到任何东西的标签）。
	/// 只应当用在"分类键的合法集合变了"的场合——内置分类、待分类、现存自建分类都要在 <paramref name="knownKeys"/> 里。
	/// </summary>
	public bool PruneKnown(IEnumerable<string> knownKeys)
	{
		if (keys.Count == 0) return false;
		var known = new HashSet<string>(
			knownKeys.Select(Normalize).Where(key => key.Length != 0),
			StringComparer.OrdinalIgnoreCase);
		return keys.RemoveAll(key => !known.Contains(key)) != 0;
	}

	/// <summary>一个 Mod 的分类键与选中集合有交集即命中；空集合恒真。</summary>
	public bool Matches(IReadOnlyCollection<string> modKeys)
	{
		if (keys.Count == 0) return true;
		foreach (var key in modKeys)
		{
			if (keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;
		}

		return false;
	}
}
