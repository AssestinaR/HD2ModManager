namespace HD2ModCore.Infrastructure;

/// <summary>一次并发批处理里，某一项最终落在哪个状态。</summary>
public enum ParallelBatchItemState
{
	/// <summary>跑完了（成不成功由调用方自己写在结果里，这里只说"跑完了"）。</summary>
	Completed,

	/// <summary>抛了没人接的异常（调用方自己 catch 掉的不算）。</summary>
	Failed,

	/// <summary>没跑、或跑到一半被取消 —— 这一项的状态是未知的。</summary>
	Skipped,
}

/// <param name="Error">仅在 <see cref="ParallelBatchItemState.Failed"/> 时有值。</param>
public sealed record ParallelBatchItemResult<T>(
	int Index,
	T Item,
	ParallelBatchItemState State,
	string? Error);

public sealed class ParallelBatchResult<T>
{
	public ParallelBatchResult(IReadOnlyList<ParallelBatchItemResult<T>> items, bool canceled)
	{
		Items = items;
		Canceled = canceled;
	}

	/// <summary>与输入<b>同序</b>：第 i 项永远是输入里第 i 个。</summary>
	public IReadOnlyList<ParallelBatchItemResult<T>> Items { get; }

	/// <summary>是否因为取消而提前收场（<c>true</c> 时后面那些项是 <see cref="ParallelBatchItemState.Skipped"/>）。</summary>
	public bool Canceled { get; }

	public int CompletedCount => Items.Count(item => item.State == ParallelBatchItemState.Completed);
	public int FailedCount => Items.Count(item => item.State == ParallelBatchItemState.Failed);
	public int SkippedCount => Items.Count(item => item.State == ParallelBatchItemState.Skipped);
}

/// <summary>
/// 把一个列表按固定并发数跑完。
/// <para>
/// <b>为什么单独抽出来。</b>「一键重构所有 VPK 模组」这类批量活的三个要求，都不在业务代码里：
/// 并发数必须真的被守住（否则默认 5 只是心理安慰）、单项炸掉不能带走整批、
/// 取消之后没开始的项必须能明确指出"这项没做"（而不是假装成功）。
/// 这三条都能离线测，所以放在 Core 里而不是管理器里 —— 管理器没有测试工程。
/// </para>
/// <para>
/// <b>不做重试。</b>转换失败几乎都不是瞬时的（源包坏了、缺参数），重试只会让同一份错
/// 多跑几分钟；该由调用方把原因报出来让人决定。
/// </para>
/// </summary>
public static class ParallelBatchRunner
{
	/// <param name="work">
	/// 一项的活。抛 <see cref="OperationCanceledException"/> 且 <c>cancellationToken</c> 已取消
	/// ⇒ 这一项记 <see cref="ParallelBatchItemState.Skipped"/>；抛别的异常 ⇒ 记 Failed 并继续跑下一项。
	/// </param>
	/// <param name="onItemFinished">
	/// 每完成一项回调一次。<b>可能在多个线程上并发到达</b>（同时在跑几项），
	/// 更新界面时调用方自己切回 UI 线程。
	/// </param>
	public static async Task<ParallelBatchResult<T>> RunAsync<T>(
		IReadOnlyList<T> items,
		int concurrency,
		Func<T, int, CancellationToken, Task> work,
		Action<ParallelBatchItemResult<T>>? onItemFinished = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(work);

		if (items.Count == 0)
			return new ParallelBatchResult<T>(Array.Empty<ParallelBatchItemResult<T>>(), cancellationToken.IsCancellationRequested);

		// 并发数：小于 1 按 1（界面上绑了个空值或 0 不该炸），大于项数按项数（多出来的 worker 没有活可干）。
		var workers = Math.Clamp(concurrency, 1, items.Count);
		var results = new ParallelBatchItemResult<T>?[items.Count];
		var next = -1;

		async Task RunWorkerAsync()
		{
			while (true)
			{
				var index = Interlocked.Increment(ref next);
				if (index >= items.Count) return;

				if (cancellationToken.IsCancellationRequested)
				{
					results[index] = new ParallelBatchItemResult<T>(index, items[index], ParallelBatchItemState.Skipped, null);
					continue;
				}

				ParallelBatchItemResult<T> result;
				try
				{
					await work(items[index], index, cancellationToken).ConfigureAwait(false);
					result = new ParallelBatchItemResult<T>(index, items[index], ParallelBatchItemState.Completed, null);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					result = new ParallelBatchItemResult<T>(index, items[index], ParallelBatchItemState.Skipped, null);
				}
				catch (Exception exception)
				{
					result = new ParallelBatchItemResult<T>(index, items[index], ParallelBatchItemState.Failed, exception.Message);
				}

				results[index] = result;
				onItemFinished?.Invoke(result);
			}
		}

		// 固定数量的 worker 各自"取下一项、做完再取下一项"，所以同时开着的活**恰好**等于并发数：
		// 这个上界是结构性保证，不依赖任何计数器的正确性。Task.Run 是为了让首个 await 之前的
		// 同步部分也不要跑在 UI 线程上。
		var threads = new Task[workers];
		for (var i = 0; i < workers; i++)
			threads[i] = Task.Run(RunWorkerAsync, CancellationToken.None);
		await Task.WhenAll(threads).ConfigureAwait(false);

		var ordered = new List<ParallelBatchItemResult<T>>(items.Count);
		for (var i = 0; i < items.Count; i++)
		{
			ordered.Add(results[i] ?? new ParallelBatchItemResult<T>(i, items[i], ParallelBatchItemState.Skipped, null));
		}

		return new ParallelBatchResult<T>(ordered, cancellationToken.IsCancellationRequested);
	}
}
