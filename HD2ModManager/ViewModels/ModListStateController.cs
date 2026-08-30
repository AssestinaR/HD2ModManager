using System.Collections;
using System.Windows.Threading;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// A projection source is intentionally not an ObservableCollection.  ModListPanel
// owns the only WPF-visible collection and receives one immutable target snapshot
// per transaction.  This prevents a source-side Reset from forcing WPF to tear
// down every realized ListBoxItem before the transition controller can animate it.
public interface IModListProjectionSource : IEnumerable
{
    event EventHandler<ModListProjectionChangedEventArgs>? ProjectionChanged;
}

public sealed class ModListProjectionChangedEventArgs(IReadOnlyList<object> items) : EventArgs
{
    public IReadOnlyList<object> Items { get; } = items;
}

public sealed class ModListProjectionSource<TItem> : IReadOnlyList<TItem>, IModListProjectionSource
{
    private IReadOnlyList<TItem> _items = Array.Empty<TItem>();

    public event EventHandler<ModListProjectionChangedEventArgs>? ProjectionChanged;

    public int Count => _items.Count;
    public TItem this[int index] => _items[index];

    internal void Publish(IReadOnlyList<TItem> items)
    {
        _items = items.ToArray();
        ProjectionChanged?.Invoke(this, new ModListProjectionChangedEventArgs(_items.Cast<object>().ToArray()));
    }

    public IEnumerator<TItem> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// Owns one logical Mod-list projection. Hosts submit immutable-ish row states;
// this type keeps row view-model instances stable, coalesces bursts, and is the
// only writer of the collection observed by ModListPanel.
public sealed class ModListStateController<TState, TItem> : IDisposable
{
    private readonly ModListProjectionSource<TItem> _items;
    private readonly Dictionary<string, CachedRow> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TState, string> _keySelector;
    private readonly Func<TState, string> _versionSelector;
    private readonly Func<TState, TItem> _createItem;
    private readonly Action<TItem, TState> _applyState;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounceTimer;
    private IReadOnlyList<PendingState>? _pendingSnapshot;
    private IReadOnlyCollection<string>? _pendingRetainedKeys;
    private readonly Dictionary<string, PendingState> _pendingContentStates = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _presentedKeys = Array.Empty<string>();
    private readonly HashSet<string> _presentedKeySet = new(StringComparer.OrdinalIgnoreCase);
    private int _visibleCount;
    private bool _disposed;

    public ModListStateController(
        Func<TState, string> keySelector,
        Func<TState, string> versionSelector,
        Func<TState, TItem> createItem,
        Action<TItem, TState> applyState,
        Func<TItem, string?> itemKeySelector,
        TimeSpan? debounceInterval = null,
        Dispatcher? dispatcher = null)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _versionSelector = versionSelector ?? throw new ArgumentNullException(nameof(versionSelector));
        _createItem = createItem ?? throw new ArgumentNullException(nameof(createItem));
        _applyState = applyState ?? throw new ArgumentNullException(nameof(applyState));
        ArgumentNullException.ThrowIfNull(itemKeySelector);

        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _items = new ModListProjectionSource<TItem>();
        Items = _items;
        // Projection commits must not sit behind a long queue of background
        // maintenance callbacks.  Input is still allowed to run first, while a
        // settled projection reaches the visual list before the next background
        // burst can starve the renderer.
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = debounceInterval ?? TimeSpan.FromMilliseconds(40),
        };
        _debounceTimer.Tick += OnDebounceTimerTick;
    }

    public ModListProjectionSource<TItem> Items { get; }

    // The target count changes as soon as the host submits a projection. This lets
    // header text stay truthful even while a visual transition is finishing.
    public int VisibleCount => _visibleCount;
    public int CachedRowCount => _cache.Count;
    public bool HasPendingCommit => _debounceTimer.IsEnabled;

    public event EventHandler<ModListStateCommittedEventArgs>? Committed;

    public void Submit(
        IEnumerable<TState> states,
        IEnumerable<string>? retainedKeys = null,
        bool commitImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(states);
        var normalized = Normalize(states);
        var retained = retainedKeys is null
            ? null
            : retainedKeys.Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => Submit(normalized.Select(entry => entry.State), retained, commitImmediately));
            return;
        }

        ThrowIfDisposed();
        _pendingSnapshot = normalized;
        _pendingRetainedKeys = retained;
        _visibleCount = normalized.Count;

        if (commitImmediately)
        {
            _debounceTimer.Stop();
            CommitPending();
            return;
        }

        // A new complete snapshot supersedes all previous uncommitted snapshots.
        // The timer is deliberately restarted so import/status bursts settle into
        // one UI transaction instead of repeatedly rebuilding the visual tree.
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // Content-only updates deliberately do not rebuild the logical projection.
    // They are used for facts/status/thumbnail changes that cannot affect
    // membership or order, keeping the UI thread free for active transitions.
    public void SubmitContent(IEnumerable<TState> states, bool commitImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(states);
        var normalized = Normalize(states);
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => SubmitContent(normalized.Select(entry => entry.State), commitImmediately));
            return;
        }

        ThrowIfDisposed();
        foreach (var entry in normalized)
            _pendingContentStates[entry.Key] = entry;

        if (commitImmediately)
        {
            CommitContentImmediately(normalized);
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Flush()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(Flush);
            return;
        }

        if (_disposed) return;
        _debounceTimer.Stop();
        CommitPending();
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        CommitPending();
    }

    private void CommitPending()
    {
        if (_disposed) return;

        var pendingSnapshot = _pendingSnapshot;
        var retainedKeys = _pendingRetainedKeys;
        var pendingContent = _pendingContentStates.Values.ToArray();
        using var perf = UiPerformanceDiagnostics.Measure(
            "ModListState.Commit",
            thresholdMilliseconds: 16,
            details: () => $"state={typeof(TState).Name}; snapshot={(pendingSnapshot?.Count ?? -1)}; content={pendingContent.Length}; presented={Items.Count}; cached={_cache.Count}");
        _pendingSnapshot = null;
        _pendingRetainedKeys = null;
        _pendingContentStates.Clear();

        if (pendingSnapshot is null)
        {
            CommitContent(pendingContent);
            return;
        }

        if (pendingContent.Length != 0)
            pendingSnapshot = MergeContentIntoSnapshot(pendingSnapshot, pendingContent);

        var orderedItems = new List<TItem>(pendingSnapshot.Count);
        var orderedKeys = new List<string>(pendingSnapshot.Count);
        var stateUpdates = new List<(CachedRow Row, PendingState State)>();
        var changedRows = 0;
        foreach (var entry in pendingSnapshot)
        {
            if (_cache.TryGetValue(entry.Key, out var cached))
            {
                if (!string.Equals(cached.Version, entry.Version, StringComparison.Ordinal))
                {
                    stateUpdates.Add((cached, entry));
                }
                orderedItems.Add(cached.Item);
            }
            else
            {
                var item = _createItem(entry.State);
                _cache.Add(entry.Key, new CachedRow(item, entry.Version));
                orderedItems.Add(item);
                changedRows++;
            }
            orderedKeys.Add(entry.Key);
        }

        var structureChanged = !_presentedKeys.SequenceEqual(orderedKeys, StringComparer.OrdinalIgnoreCase);
        if (structureChanged)
        {
            // The projection source is not bound directly to the ListBox.  Its
            // custom event gives ModListPanel a complete desired order while the
            // panel retains the actual WPF collection and applies Move/Insert/
            // Remove operations instead of a destructive Reset.
            _items.Publish(orderedItems);
            _presentedKeys = orderedKeys;
            _presentedKeySet.Clear();
            _presentedKeySet.UnionWith(orderedKeys);
        }

        foreach (var update in stateUpdates)
        {
            _applyState(update.Row.Item, update.State.State);
            update.Row.Version = update.State.Version;
            changedRows++;
        }

        if (retainedKeys is not null)
            PruneCache(retainedKeys);

        Committed?.Invoke(this, new ModListStateCommittedEventArgs(
            VisibleCount,
            structureChanged,
            changedRows,
            Items.Count,
            _cache.Count));
    }

    private void CommitContent(IReadOnlyCollection<PendingState> pendingContent)
    {
        var changedRows = 0;
        foreach (var entry in pendingContent)
        {
            if (!_presentedKeySet.Contains(entry.Key)
                || !_cache.TryGetValue(entry.Key, out var cached)
                || string.Equals(cached.Version, entry.Version, StringComparison.Ordinal))
                continue;

            _applyState(cached.Item, entry.State);
            cached.Version = entry.Version;
            changedRows++;
        }

        Committed?.Invoke(this, new ModListStateCommittedEventArgs(
            VisibleCount,
            StructureChanged: false,
            changedRows,
            Items.Count,
            _cache.Count));
    }

    private void CommitContentImmediately(IReadOnlyCollection<PendingState> immediateContent)
    {
        if (_disposed || immediateContent.Count == 0) return;

        foreach (var entry in immediateContent)
            _pendingContentStates.Remove(entry.Key);

        using var perf = UiPerformanceDiagnostics.Measure(
            "ModListState.ContentCommit",
            thresholdMilliseconds: 8,
            details: () => $"content={immediateContent.Count}; pendingSnapshot={_pendingSnapshot?.Count ?? -1}; queuedContent={_pendingContentStates.Count}; presented={Items.Count}; cached={_cache.Count}");

        // A structural snapshot may already be waiting for the debounce
        // boundary.  Apply the content now for the currently presented rows and
        // also merge it into that future snapshot so it cannot overwrite the
        // just-clicked selection when the structural commit eventually lands.
        if (_pendingSnapshot is not null)
            _pendingSnapshot = MergeContentIntoSnapshot(_pendingSnapshot, immediateContent);

        CommitContent(immediateContent);
    }

    private static IReadOnlyList<PendingState> MergeContentIntoSnapshot(
        IReadOnlyList<PendingState> snapshot,
        IReadOnlyCollection<PendingState> content)
    {
        if (content.Count == 0) return snapshot;
        var latestByKey = content.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        return snapshot
            .Select(entry => latestByKey.TryGetValue(entry.Key, out var updated) ? updated : entry)
            .ToArray();
    }

    private IReadOnlyList<PendingState> Normalize(IEnumerable<TState> states)
    {
        var result = new List<PendingState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            var key = _keySelector(state)?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Mod 列表状态缺少稳定键，不能提交到列表系统。");

            if (!seen.Add(key))
            {
                LogService.Warn($"Mod 列表状态出现重复稳定键，已忽略重复行：{key}");
                continue;
            }

            result.Add(new PendingState(key, _versionSelector(state) ?? string.Empty, state));
        }
        return result;
    }

    private void PruneCache(IReadOnlyCollection<string> retainedKeys)
    {
        foreach (var key in _cache.Keys.Where(key => !retainedKeys.Contains(key)).ToArray())
            _cache.Remove(key);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModListStateController<TState, TItem>));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTimerTick;
        _cache.Clear();
        _pendingSnapshot = null;
        _pendingRetainedKeys = null;
        _pendingContentStates.Clear();
        _presentedKeySet.Clear();
    }

    private sealed class CachedRow(TItem item, string version)
    {
        public TItem Item { get; } = item;
        public string Version { get; set; } = version;
    }

    private sealed record PendingState(string Key, string Version, TState State);
}

public sealed record ModListStateCommittedEventArgs(
    int VisibleCount,
    bool StructureChanged,
    int ChangedRows,
    int PresentedCount,
    int CachedRowCount);

// Pages that already own stable row view-model instances still need the same
// snapshot, debounce, and presentation boundary as the main library/profile
// lists. This adapter keeps those pages out of direct mutable collection
// binding without forcing each one to duplicate a state DTO.
public sealed class ModListItemProjectionController<TItem> : IDisposable
    where TItem : class
{
    private readonly Func<TItem, string> _keySelector;
    private readonly ModListStateController<TItem, TItem> _controller;

    public ModListItemProjectionController(
        Func<TItem, string> keySelector,
        Func<TItem, string>? versionSelector = null,
        Action<TItem, TItem>? applyState = null,
        TimeSpan? debounceInterval = null,
        Dispatcher? dispatcher = null)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _controller = new ModListStateController<TItem, TItem>(
            _keySelector,
            versionSelector ?? _keySelector,
            item => item,
            applyState ?? static (_, _) => { },
            _keySelector,
            debounceInterval,
            dispatcher);
    }

    public ModListProjectionSource<TItem> Items => _controller.Items;
    public int VisibleCount => _controller.VisibleCount;

    public void Submit(
        IEnumerable<TItem> visibleItems,
        IEnumerable<TItem>? retainedItems = null,
        bool commitImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(visibleItems);
        var visible = visibleItems as IReadOnlyList<TItem> ?? visibleItems.ToArray();
        var retainedKeys = retainedItems?
            .Select(_keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();
        _controller.Submit(visible, retainedKeys, commitImmediately);
    }

    public void SubmitContent(IEnumerable<TItem> items, bool commitImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        _controller.SubmitContent(items, commitImmediately);
    }

    public void Flush() => _controller.Flush();

    public void Dispose() => _controller.Dispose();
}
