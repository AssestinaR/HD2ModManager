using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace HD2ModManager.Services;

/// <summary>
/// Low-overhead diagnostics for UI stalls. This class observes the UI and records
/// only slow synchronous work, visible frame gaps, and short-lived event bursts.
/// It intentionally does not change scheduling, throttling, or collection behavior.
///
/// 默认关闭，设 <c>HD2_UI_DIAG=1</c> 才启动：它是查卡顿时才需要打开的工具，不是常驻功能。
/// 打开后写 trace 通道（logs/trace_*.log），不污染用户日志 manager_*.log。
/// </summary>
public static class UiPerformanceDiagnostics
{
    private const double FrameStallThresholdMilliseconds = 100d;
    private const double FrameStallLogCooldownMilliseconds = 750d;
    private const double BurstWindowMilliseconds = 1000d;

    private static readonly object BurstLock = new();
    private static readonly Dictionary<string, BurstState> Bursts = new(StringComparer.Ordinal);
    private static long _lastRenderTimestamp;
    private static long _lastFrameStallLogTimestamp;
    private static int _started;

    public static bool IsEnabled => Volatile.Read(ref _started) != 0;

    /// <summary>
    /// 只有显式要求才采集。没有这个开关时，每一次正常游玩都会留下几十行
    /// frame-stall / event-burst，而它们只对写这段代码的人有意义。
    /// </summary>
    private static bool IsRequestedByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("HD2_UI_DIAG");
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static void Start()
    {
        if (!IsRequestedByEnvironment()) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _lastRenderTimestamp = Stopwatch.GetTimestamp();
        _lastFrameStallLogTimestamp = 0;
        CompositionTarget.Rendering += OnRendering;
        LogService.Trace("UIPerf", $"diagnostics-started; uiThread={Environment.CurrentManagedThreadId}; frequency={Stopwatch.Frequency}.");
    }

    public static void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        CompositionTarget.Rendering -= OnRendering;
        lock (BurstLock) Bursts.Clear();
        LogService.Trace("UIPerf", "diagnostics-stopped.");
    }

    public static IDisposable Measure(
        string operation,
        double thresholdMilliseconds = 50d,
        Func<string?>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!IsEnabled) return NoopScope.Instance;
        return new TimingScope(operation, thresholdMilliseconds, details);
    }

    /// <summary>
    /// Allocation-free fast path for very hot synchronous helpers. Callers pass
    /// zero when diagnostics were disabled at the beginning of the operation.
    /// </summary>
    public static void RecordElapsed(string operation, long startedTimestamp, double thresholdMilliseconds = 50d)
    {
        if (!IsEnabled || startedTimestamp == 0) return;
        var elapsed = ElapsedMilliseconds(startedTimestamp, Stopwatch.GetTimestamp());
        if (elapsed < thresholdMilliseconds) return;
        var ui = Application.Current?.Dispatcher?.CheckAccess() == true;
        LogService.Trace("UIPerf", $"slow-operation; name={operation}; elapsed={elapsed:0}ms; threshold={thresholdMilliseconds:0}ms; ui={ui}; thread={Environment.CurrentManagedThreadId}; {MemorySnapshot()}.");
    }

    /// <summary>
    /// Counts a high-frequency event and logs only when it reaches another burst
    /// threshold inside one second. This is used for refresh/notification storms.
    /// </summary>
    public static void RecordBurst(string category, string? details = null, int threshold = 8)
    {
        if (!IsEnabled || threshold <= 0) return;

        var now = Stopwatch.GetTimestamp();
        var shouldLog = false;
        var count = 0;
        var windowMilliseconds = 0d;
        lock (BurstLock)
        {
            if (!Bursts.TryGetValue(category, out var state)
                || ElapsedMilliseconds(state.StartTimestamp, now) > BurstWindowMilliseconds)
            {
                state = new BurstState(now);
            }

            state.Count++;
            Bursts[category] = state;
            count = state.Count;
            windowMilliseconds = ElapsedMilliseconds(state.StartTimestamp, now);
            shouldLog = count >= threshold && (count == threshold || count % threshold == 0);
        }

        if (!shouldLog) return;
        var ui = Application.Current?.Dispatcher?.CheckAccess() == true;
        LogService.Trace("UIPerf", $"event-burst; category={category}; count={count}; window={windowMilliseconds:0}ms; ui={ui}; thread={Environment.CurrentManagedThreadId}; {MemorySnapshot()}{FormatDetails(details)}.");
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastRenderTimestamp, now);
        if (previous == 0) return;

        var gapMilliseconds = ElapsedMilliseconds(previous, now);
        if (gapMilliseconds < FrameStallThresholdMilliseconds) return;

        var lastLog = Volatile.Read(ref _lastFrameStallLogTimestamp);
        if (lastLog != 0 && ElapsedMilliseconds(lastLog, now) < FrameStallLogCooldownMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastFrameStallLogTimestamp, now, lastLog) != lastLog) return;

        LogService.Trace("UIPerf", $"frame-stall; gap={gapMilliseconds:0}ms; uiThread={Environment.CurrentManagedThreadId}; {MemorySnapshot()}.");
    }

    private static string FormatDetails(string? details)
        => string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}";

    private static string MemorySnapshot()
    {
        try
        {
            var managed = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d;
            var workingSet = Environment.WorkingSet / 1024d / 1024d;
            return $"managed={managed:0}MB; workingSet={workingSet:0}MB";
        }
        catch
        {
            return "memory=unavailable";
        }
    }

    private static double ElapsedMilliseconds(long start, long end)
        => (end - start) * 1000d / Stopwatch.Frequency;

    private sealed class TimingScope : IDisposable
    {
        private readonly string _operation;
        private readonly double _thresholdMilliseconds;
        private readonly Func<string?>? _details;
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private readonly int _startThread = Environment.CurrentManagedThreadId;
        private readonly bool _startedOnUi = Application.Current?.Dispatcher?.CheckAccess() == true;
        private int _disposed;

        public TimingScope(string operation, double thresholdMilliseconds, Func<string?>? details)
        {
            _operation = operation;
            _thresholdMilliseconds = thresholdMilliseconds;
            _details = details;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var elapsed = ElapsedMilliseconds(_startedTimestamp, Stopwatch.GetTimestamp());
            if (elapsed < _thresholdMilliseconds) return;

            string? detail = null;
            try { detail = _details?.Invoke(); } catch { }
            var endedOnUi = Application.Current?.Dispatcher?.CheckAccess() == true;
            LogService.Trace("UIPerf", $"slow-operation; name={_operation}; elapsed={elapsed:0}ms; threshold={_thresholdMilliseconds:0}ms; startUi={_startedOnUi}; endUi={endedOnUi}; startThread={_startThread}; endThread={Environment.CurrentManagedThreadId}; {MemorySnapshot()}{FormatDetails(detail)}.");
        }
    }

    private sealed class BurstState
    {
        public BurstState(long startTimestamp) => StartTimestamp = startTimestamp;
        public long StartTimestamp { get; }
        public int Count { get; set; }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
