namespace HD2ModCore.Infrastructure;

// 作用：一份"按来源指纹键控"的进程内记忆。它是记忆，不是真相——具体契约由各持有者各自声明。
// Purpose: A bounded in-process memo keyed by source fingerprints. It is a memo, never the source of truth.
//
// 三个必须同时成立的约束（设计文档 §20.3 的硬约束，改动前请先读那一条）：
//   1. 键由来源指纹构成 → 来源一变键就变，旧值不可达。"来源更新了却还返回旧数据"在结构上不可能发生。
//   2. 不落盘 → 清空它只损失时间，不丢数据。
//   3. 有界 → 条目达到上限时整体清空，进程内存不随使用时长增长。
//
// 之所以做成实例而不是静态类：不同族的事实需要**各自独立的有界预算**。合用一个字典时，
// 体积大、访问节奏不同的那一族会把另一族挤出去；分开之后各自的预算才是稳定的。
internal sealed class SourceFingerprintMemo
{
	private readonly int _capacity;
	private readonly object _gate = new();
	private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);

	public SourceFingerprintMemo(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
		_capacity = capacity;
	}

	// 作用：按键取值；类型不匹配一律当作未命中（同一个键不可能属于两种事实，但类型判断是免费的第二道锁）。
	public bool TryGet<T>(string key, out T value)
	{
		lock (_gate)
		{
			if (_entries.TryGetValue(key, out var cached) && cached is T typed)
			{
				value = typed;
				return true;
			}
		}

		value = default!;
		return false;
	}

	public void Set<T>(string key, T value)
	{
		if (value is null) return;
		lock (_gate)
		{
			// 满了一律整体清空：这里是记忆不是真相，丢光只是慢一点，所以不需要淘汰策略那套复杂度。
			if (_entries.Count >= _capacity) _entries.Clear();
			_entries[key] = value;
		}
	}
}
