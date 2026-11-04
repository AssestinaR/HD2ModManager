using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 批量并发执行器：三条硬要求，都在这里钉住。
/// <list type="number">
/// <item><b>并发数是真的上界。</b>「默认 5 个并发」如果只是心理安慰，用户会看到 5 个 CLI 进程
/// 变成 12 个，然后一起把内存吃光。</item>
/// <item><b>单项失败不能带走整批。</b>一百多个 VPK 里坏一个就全停，等于让用户手工一个个点。</item>
/// <item><b>取消之后没开始的项必须是"没做"。</b>把它们记成成功，用户会以为产物是新的。</item>
/// </list>
/// </summary>
public sealed class ParallelBatchRunnerTests
{
	[Fact]
	public async Task NeverExceedsRequestedConcurrency()
	{
		var items = Enumerable.Range(0, 12).ToArray();
		var running = 0;
		var peak = 0;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var startCount = 0;

		var run = ParallelBatchRunner.RunAsync(items, 3, async (item, _, token) =>
		{
			var now = Interlocked.Increment(ref running);
			InterlockedMax(ref peak, now);
			if (Interlocked.Increment(ref startCount) == 3) started.TrySetResult();
			await gate.Task.WaitAsync(token);
			Interlocked.Decrement(ref running);
		});

		// 等三项都开起来：此刻如果不守并发，后面的项会立刻也开始（peak 就会超过 3）。
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		await Task.Delay(120);
		Assert.Equal(3, Volatile.Read(ref running));

		gate.SetResult();
		var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal(12, result.CompletedCount);
		Assert.Equal(3, peak);
	}

	[Fact]
	public async Task ConcurrencyAboveItemCountRunsEveryItemOnce()
	{
		var items = new[] { "a", "b" };
		var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

		var result = await ParallelBatchRunner.RunAsync(items, 8, (item, _, _) =>
		{
			calls.AddOrUpdate(item, 1, (_, count) => count + 1);
			return Task.CompletedTask;
		});

		Assert.Equal(2, result.CompletedCount);
		Assert.Equal(1, calls["a"]);
		Assert.Equal(1, calls["b"]);
	}

	[Fact]
	public async Task ResultsKeepInputOrderEvenWhenWorkFinishesOutOfOrder()
	{
		var items = Enumerable.Range(0, 10).ToArray();
		var result = await ParallelBatchRunner.RunAsync(items, 4, async (item, _, _) =>
		{
			// 越靠前的项故意越慢 ⇒ 完成顺序与输入顺序相反。
			await Task.Delay((10 - item) * 5);
		});

		Assert.Equal(items, result.Items.Select(entry => entry.Item).ToArray());
		Assert.All(result.Items, entry => Assert.Equal(ParallelBatchItemState.Completed, entry.State));
	}

	[Fact]
	public async Task SingleFailureDoesNotStopTheBatch()
	{
		var items = Enumerable.Range(0, 6).ToArray();
		var completed = 0;

		var result = await ParallelBatchRunner.RunAsync(items, 2, (item, _, _) =>
		{
			if (item == 3) throw new InvalidOperationException("这一项坏了");
			Interlocked.Increment(ref completed);
			return Task.CompletedTask;
		});

		Assert.Equal(5, completed);
		Assert.Equal(5, result.CompletedCount);
		Assert.Equal(1, result.FailedCount);
		Assert.Equal(0, result.SkippedCount);
		var failure = Assert.Single(result.Items, entry => entry.State == ParallelBatchItemState.Failed);
		Assert.Equal(3, failure.Index);
		Assert.Contains("这一项坏了", failure.Error);
	}

	[Fact]
	public async Task CancellationMarksNotStartedItemsAsSkipped()
	{
		var items = Enumerable.Range(0, 8).ToArray();
		var started = new System.Collections.Concurrent.ConcurrentBag<int>();
		using var cancellation = new CancellationTokenSource();
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var run = ParallelBatchRunner.RunAsync(items, 1, async (item, _, token) =>
		{
			started.Add(item);
			firstStarted.TrySetResult();
			await Task.Delay(Timeout.Infinite, token);
		}, cancellationToken: cancellation.Token);

		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cancellation.Cancel();
		var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.True(result.Canceled);
		Assert.Equal(0, result.CompletedCount);
		Assert.Equal(items.Length, result.SkippedCount);
		// 只有真正开动的那些被调用过：取消之后不再开新项。
		Assert.Single(started);
	}

	[Fact]
	public async Task ItemFinishedCallbackFiresOncePerItem()
	{
		var items = Enumerable.Range(0, 5).ToArray();
		var finished = new System.Collections.Concurrent.ConcurrentBag<int>();

		var result = await ParallelBatchRunner.RunAsync(items, 2,
			(item, _, _) =>
			{
				if (item == 4) throw new InvalidOperationException("失败也要回调");
				return Task.CompletedTask;
			},
			entry => finished.Add(entry.Index));

		Assert.Equal(items.Length, result.Items.Count);
		Assert.Equal(items.OrderBy(value => value), finished.OrderBy(value => value));
	}

	[Fact]
	public async Task EmptyInputIsNotTreatedAsCancellation()
	{
		var result = await ParallelBatchRunner.RunAsync(Array.Empty<int>(), 5, (_, _, _) => Task.CompletedTask);

		Assert.Empty(result.Items);
		Assert.False(result.Canceled);
		Assert.Equal(0, result.CompletedCount);
	}

	[Fact]
	public async Task NonPositiveConcurrencyRunsSeriallyInsteadOfThrowing()
	{
		// 界面上的数字框可能读出 0 或空值：那是输入问题，不该让整批炸掉。
		var items = Enumerable.Range(0, 4).ToArray();
		var peak = 0;
		var running = 0;

		var result = await ParallelBatchRunner.RunAsync(items, 0, async (_, _, _) =>
		{
			InterlockedMax(ref peak, Interlocked.Increment(ref running));
			await Task.Yield();
			Interlocked.Decrement(ref running);
		});

		Assert.Equal(4, result.CompletedCount);
		Assert.Equal(1, peak);
	}

	private static void InterlockedMax(ref int target, int value)
	{
		while (true)
		{
			var current = Volatile.Read(ref target);
			if (value <= current) return;
			if (Interlocked.CompareExchange(ref target, value, current) == current) return;
		}
	}
}
