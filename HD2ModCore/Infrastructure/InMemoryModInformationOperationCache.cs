using System.Collections.Concurrent;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：流程级、带容量预算的内存缓存；默认只活在调用方持有的操作上下文中。
// Purpose: Operation-scoped in-memory cache with a bounded budget and explicit lifetime.
public sealed class InMemoryModInformationOperationCache : IModInformationOperationCache
{
	public const long DefaultCapacityBytes = 256L * 1024L * 1024L;

	private readonly ConcurrentDictionary<ModInformationCacheKey, Entry> _entries = new();
	private readonly object _gate = new();
	private readonly long _capacityBytes;
	private long _usedBytes;
	private bool _disposed;

	public InMemoryModInformationOperationCache(Guid operationId, long capacityBytes = DefaultCapacityBytes)
	{
		if (operationId == Guid.Empty) throw new ArgumentException("操作 ID 不能为 Guid.Empty。", nameof(operationId));
		if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
		OperationId = operationId;
		_capacityBytes = capacityBytes;
	}

	public Guid OperationId { get; }
	public long CapacityBytes => _capacityBytes;
	public long UsedBytes
	{
		get { lock (_gate) return _usedBytes; }
	}

	public ValueTask<ModInformationMemoryCacheEntry<T>?> TryGetAsync<T>(ModInformationCacheKey key, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_entries.TryGetValue(key, out var entry) || entry.Value is not T value)
				return ValueTask.FromResult<ModInformationMemoryCacheEntry<T>?>(null);
			entry.LastAccessUtc = DateTimeOffset.UtcNow;
			return ValueTask.FromResult<ModInformationMemoryCacheEntry<T>?>(new ModInformationMemoryCacheEntry<T>(
				value,
				entry.CreatedUtc,
				entry.LastAccessUtc,
				entry.EstimatedBytes));
		}
	}

	public ValueTask SetAsync<T>(ModInformationCacheKey key, T value, long? estimatedBytes = null, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(value);
		var bytes = Math.Max(1L, estimatedBytes ?? EstimateBytes(value));
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_entries.TryRemove(key, out var previous))
				_usedBytes -= previous.EstimatedBytes;
			if (bytes > _capacityBytes)
				return ValueTask.CompletedTask;

			var now = DateTimeOffset.UtcNow;
			_entries[key] = new Entry(value!, bytes, now);
			_usedBytes += bytes;
			EvictUntilWithinBudget(key);
		}
		return ValueTask.CompletedTask;
	}

	public bool Remove(ModInformationCacheKey key)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_entries.TryRemove(key, out var entry)) return false;
			_usedBytes -= entry.EstimatedBytes;
			return true;
		}
	}

	// 作用：移除某个 Mod 节点的所有属性，供内容提交后释放旧版本的流程/会话数据。
	// Purpose: Removes every property for one Mod node after a content commit.
	public int RemoveNode(ModNodeId nodeId)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			var removed = 0;
			foreach (var key in _entries.Keys.Where(key => key.BelongsTo(nodeId)).ToArray())
			{
				if (!_entries.TryRemove(key, out var entry)) continue;
				_usedBytes -= entry.EstimatedBytes;
				removed++;
			}
			return removed;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			_entries.Clear();
			_usedBytes = 0;
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed) return ValueTask.CompletedTask;
			_disposed = true;
			_entries.Clear();
			_usedBytes = 0;
		}
		return ValueTask.CompletedTask;
	}

	private void EvictUntilWithinBudget(ModInformationCacheKey protectedKey)
	{
		while (_usedBytes > _capacityBytes && _entries.Count > 0)
		{
			var oldest = _entries
				.Where(pair => !pair.Key.Equals(protectedKey))
				.OrderBy(pair => pair.Value.LastAccessUtc)
				.FirstOrDefault();
			if (oldest.Equals(default(KeyValuePair<ModInformationCacheKey, Entry>)))
				break;
			if (_entries.TryRemove(oldest.Key, out var removed))
				_usedBytes -= removed.EstimatedBytes;
		}
	}

	private static long EstimateBytes<T>(T value)
	{
		return value switch
		{
			byte[] bytes => bytes.LongLength,
			ReadOnlyMemory<byte> memory => memory.Length,
			string text => Encoding.UTF8.GetByteCount(text),
			Array array => Math.Max(1L, array.LongLength * 16L),
			System.Collections.ICollection collection => Math.Max(1L, collection.Count * 128L),
			_ => 1L,
		};
	}

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(InMemoryModInformationOperationCache));
	}

	private sealed class Entry(object value, long estimatedBytes, DateTimeOffset createdUtc)
	{
		public object Value { get; } = value;
		public long EstimatedBytes { get; } = estimatedBytes;
		public DateTimeOffset CreatedUtc { get; } = createdUtc;
		public DateTimeOffset LastAccessUtc { get; set; } = createdUtc;
	}
}
