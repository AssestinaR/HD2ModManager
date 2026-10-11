namespace HD2ModCore.Infrastructure;

// 作用：游戏数据侧事实的进程内记忆，键完全由"来源指纹"构成。
// Purpose: An in-process memo for game-data facts whose keys are built entirely from source fingerprints.
//
// 契约（这是设计文档 §20 的硬约束，改动前请先读那一条）：
//   1. 键必须由来源指纹构成——来源一变，键就变，旧值自然不可达。因此"游戏更新了却还返回旧数据"在结构上不可能发生。
//   2. 它只是记忆，不是真相。任何时刻清空它，下一次请求都会回到磁盘与加密归档重新读取。
//      重建路径（AssetArchiveIndexService.BuildOrRebuildAsync）**不读**这里，永远直接从游戏目录重扫。
//   3. 有界：条目数达到上限时整体清空。这是缓存，丢光只是慢一点，不会丢数据。
//
// Contract: keys are source fingerprints (so a changed source can never hit a stale entry); the store is
// only a memo (dropping everything costs time, never data); and it is bounded.
internal static class GameDataFactCache
{
	// 上限取小值：这里缓存的是"整包映射结果/整个元数据目录"这类大对象，
	// 宁可偶尔清空重算，也不要让进程内存随使用时长增长。
	private const int Capacity = 16;

	private static readonly object Gate = new();
	private static readonly Dictionary<string, object> Entries = new(StringComparer.Ordinal);

	public static bool TryGet<T>(string key, out T value)
	{
		lock (Gate)
		{
			if (Entries.TryGetValue(key, out var cached) && cached is T typed)
			{
				value = typed;
				return true;
			}
		}

		value = default!;
		return false;
	}

	public static void Set<T>(string key, T value)
	{
		if (value is null) return;
		lock (Gate)
		{
			if (Entries.Count >= Capacity) Entries.Clear();
			Entries[key] = value;
		}
	}
}
