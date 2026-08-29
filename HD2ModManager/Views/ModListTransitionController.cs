using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.Services;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// The panel never binds its ListBox directly to the caller's mutable source.
// This controller owns the presented collection and commits the latest desired
// state only at a transition boundary.
internal sealed class ModListTransitionController : IDisposable
{
    private readonly FrameworkElement _owner;
    private readonly ItemsControl _itemsList;
    private readonly Canvas _overlay;
    private readonly DispatcherTimer _animationTimer = new();
    private IEnumerable? _observedSource;
    private IModListProjectionSource? _projectionSource;
    private INotifyCollectionChanged? _observedItems;
    private IReadOnlyList<object> _desired = Array.Empty<object>();
    private IReadOnlyList<object>? _queuedDesired;
    private Dictionary<string, RowSnapshot> _before = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<object>? _pendingDesired;
    private bool _transitionPending;
    private bool _layoutDirty;
    private IEnumerable? _suspendedSource;
    private bool _isSuspended;
    private EventHandler? _pendingAnimationStartHandler;
    private long _layoutWaitStartedTimestamp;
    private TransitionPhase _phase;
    private bool _disposed;

    private static TimeSpan Duration(double milliseconds)
        => TimeSpan.FromMilliseconds(milliseconds);

    public ModListTransitionController(FrameworkElement owner, ItemsControl itemsList, Canvas overlay, ObservableCollection<object> presentedItems)
    {
        _owner = owner;
        _itemsList = itemsList;
        _overlay = overlay;
        PresentedItems = presentedItems;
        _animationTimer.Tick += OnAnimationTimerTick;
    }

    public ObservableCollection<object> PresentedItems { get; }
    public bool IsPlaying => _phase == TransitionPhase.Playing;
    public event Action<bool>? TransitionStateChanged;

    public void Attach(IEnumerable? source)
    {
        var canReusePresentation = _isSuspended && ReferenceEquals(_suspendedSource, source);
        DetachSource();
        Cancel();
        if (_disposed) return;
        _observedSource = source;
        _projectionSource = source as IModListProjectionSource;
        if (_projectionSource is not null)
        {
            _projectionSource.ProjectionChanged += OnProjectionChanged;
        }
        _observedItems = source as INotifyCollectionChanged;
        if (_projectionSource is null && _observedItems is not null) _observedItems.CollectionChanged += OnSourceCollectionChanged;
        _desired = source?.Cast<object>().ToArray() ?? Array.Empty<object>();
        _suspendedSource = null;
        _isSuspended = false;

        if (canReusePresentation)
            SynchronizePresentedItems(_desired);
        else
            ReplacePresentedInitial(_desired);
    }

    // A page transition temporarily removes a cached panel from one ContentHost
    // and inserts it into another.  Keeping the controller's presentation while
    // detached prevents a fresh ListBox population for every navigation, but we
    // still release source subscriptions while the panel is not on screen.
    public void Suspend()
    {
        if (_disposed || _isSuspended) return;
        _suspendedSource = _observedSource;
        _isSuspended = true;
        DetachSource();
        Cancel();
    }

    public void Detach()
    {
        DetachSource();
        Cancel();
        PresentedItems.Clear();
        _desired = Array.Empty<object>();
        _suspendedSource = null;
        _isSuspended = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _animationTimer.Tick -= OnAnimationTimerTick;
    }

    private void DetachSource()
    {
        if (_projectionSource is not null) _projectionSource.ProjectionChanged -= OnProjectionChanged;
        if (_observedItems is not null) _observedItems.CollectionChanged -= OnSourceCollectionChanged;
        _observedSource = null;
        _projectionSource = null;
        _observedItems = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        var desired = _observedSource?.Cast<object>().ToArray() ?? Array.Empty<object>();
        HandleDesiredChanged(desired, args.Action.ToString());
    }

    private void OnProjectionChanged(object? sender, ModListProjectionChangedEventArgs args)
        => HandleDesiredChanged(args.Items, "projection");

    private void HandleDesiredChanged(IReadOnlyList<object> desired, string action)
    {
        if (_disposed) return;
        _desired = desired;
        UiPerformanceDiagnostics.RecordBurst(
            "ModList.SourceChanged",
            $"action={action}; phase={_phase}; desired={desired.Count}",
            threshold: 12);

        if (_phase == TransitionPhase.Playing)
        {
            _queuedDesired = desired;
            return;
        }

        if (!HasStructuralDifference(PresentedItems, desired))
        {
            SynchronizePresentedItems(desired);
            return;
        }

        BeginTransition(desired);
    }

    private void BeginTransition(IReadOnlyList<object> desired)
    {
        if (!_transitionPending)
        {
            _before = CaptureRows();
            _transitionPending = true;
            _layoutWaitStartedTimestamp = UiPerformanceDiagnostics.IsEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
        }

        _pendingDesired = desired;
        SetPhase(TransitionPhase.AwaitingLayout);
        SynchronizePresentedItems(desired);
        _layoutDirty = true;
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        _itemsList.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_layoutDirty || _disposed || _phase != TransitionPhase.AwaitingLayout) return;
        if (_itemsList.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            return;

        _layoutDirty = false;
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        PlayPending();
    }

    private void PlayPending()
    {
        if (_disposed || !_transitionPending || !_owner.IsLoaded) return;
        UiPerformanceDiagnostics.RecordElapsed(
            "ModList.TransitionLayoutWait",
            _layoutWaitStartedTimestamp,
            thresholdMilliseconds: 40);
        _layoutWaitStartedTimestamp = 0;
        using var perf = UiPerformanceDiagnostics.Measure(
            "ModList.PlayTransition",
            thresholdMilliseconds: 20,
            details: () => $"before={_before.Count}; after={_itemsList.Items.Count}; phase={_phase}");
        _transitionPending = false;
        _pendingDesired = null;

        var before = _before;
        _before = new Dictionary<string, RowSnapshot>(StringComparer.OrdinalIgnoreCase);
        var after = CaptureRows();
        var animationStarts = new List<Action>();

        foreach (var pair in after)
        {
            if (before.TryGetValue(pair.Key, out var old))
                StageMovedContainer(pair.Value.Container, old.Position.Y - pair.Value.Position.Y, animationStarts);
            else
                StageAddedContainer(pair.Value.Container, animationStarts);
        }

        foreach (var removed in before.Values.Where(snapshot => !after.ContainsKey(snapshot.Key)))
            StageRemovedRow(removed, animationStarts);

        if (animationStarts.Count == 0)
        {
            FinishRound();
            return;
        }

        SetPhase(TransitionPhase.Playing);
        QueueAnimationStart(animationStarts);
    }

    private void OnAnimationTimerTick(object? sender, EventArgs e)
    {
        _animationTimer.Stop();
        FinishRound();
    }

    private void FinishRound()
    {
        if (_queuedDesired is { } next)
        {
            _queuedDesired = null;
            if (HasStructuralDifference(PresentedItems, next))
            {
                BeginTransition(next);
                return;
            }
            SynchronizePresentedItems(next);
        }

        SetPhase(TransitionPhase.Idle);
    }

    private void SetPhase(TransitionPhase value)
    {
        if (_phase == value) return;
        var wasPlaying = _phase == TransitionPhase.Playing;
        _phase = value;
        var isPlaying = _phase == TransitionPhase.Playing;
        if (wasPlaying != isPlaying) TransitionStateChanged?.Invoke(isPlaying);
    }

    private void ReplacePresentedInitial(IEnumerable<object> items)
    {
        PresentedItems.Clear();
        foreach (var item in items) PresentedItems.Add(item);
    }

    // Keep surviving row objects in the actual ItemsControl collection. WPF can
    // then retain their containers, virtualize normally, and let the FLIP step
    // animate only the realized rows instead of rebuilding the entire ListBox.
    private void SynchronizePresentedItems(IReadOnlyList<object> desired)
    {
        // Do not naively walk from the first target row to the last target row.
        // That turns a single "move first item to the end" operation into N-1
        // ObservableCollection.Move notifications. Besides being wasteful, each
        // notification invalidates the virtualizing panel and can consume the
        // entire animation interval before WPF receives a render pass.
        //
        // We first remove rows that really disappeared, then compare two exact
        // reordering plans: a front-to-back plan and a back-to-front plan. A
        // normal drag is strongly directional, so one of those plans is the
        // minimal one for the common long-distance cases (top -> bottom and
        // bottom -> top), while both remain safe for arbitrary permutations.
        var oldCount = PresentedItems.Count;
        var removedCount = 0;
        var insertedCount = 0;
        var movedCount = 0;
        var replacedCount = 0;
        var strategy = "none";
        var forwardMoveCount = 0;
        var backwardMoveCount = 0;
        using var perf = UiPerformanceDiagnostics.Measure(
            "ModList.Reconcile",
            thresholdMilliseconds: 8,
            details: () => $"before={oldCount}; desired={desired.Count}; remove={removedCount}; insert={insertedCount}; move={movedCount}; replace={replacedCount}; strategy={strategy}; forward={forwardMoveCount}; backward={backwardMoveCount}");

        var desiredEntries = new List<KeyedItem>(desired.Count);
        var desiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in desired)
        {
            var key = GetItemKey(item);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!desiredKeys.Add(key))
            {
                LogService.Warn($"Mod 列表投影出现重复稳定键，已忽略后续行：{key}");
                continue;
            }
            desiredEntries.Add(new KeyedItem(key, item));
        }

        for (var index = PresentedItems.Count - 1; index >= 0; index--)
        {
            var key = GetItemKey(PresentedItems[index]);
            if (string.IsNullOrWhiteSpace(key) || !desiredKeys.Contains(key))
            {
                PresentedItems.RemoveAt(index);
                removedCount++;
            }
        }

        // Reorder only the survivors first. New rows are inserted after the
        // existing order is stable so their final positions never inflate the
        // number of Move notifications for the surviving rows.
        var currentKeys = PresentedItems
            .Select(GetItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToArray();
        var currentKeySet = currentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var survivingDesiredKeys = desiredEntries
            .Where(entry => currentKeySet.Contains(entry.Key))
            .Select(entry => entry.Key)
            .ToArray();

        if (!currentKeys.SequenceEqual(survivingDesiredKeys, StringComparer.OrdinalIgnoreCase))
        {
            var forward = BuildDirectionalMovePlan(currentKeys, survivingDesiredKeys, fromEnd: false);
            var backward = BuildDirectionalMovePlan(currentKeys, survivingDesiredKeys, fromEnd: true);
            forwardMoveCount = forward.Count;
            backwardMoveCount = backward.Count;
            var plan = backward.Count < forward.Count ? backward : forward;
            strategy = ReferenceEquals(plan, backward) ? "backward" : "forward";

            foreach (var operation in plan)
            {
                PresentedItems.Move(operation.FromIndex, operation.ToIndex);
                movedCount++;
            }
        }

        var presentedKeys = PresentedItems
            .Select(GetItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var targetIndex = 0; targetIndex < desiredEntries.Count; targetIndex++)
        {
            var desiredEntry = desiredEntries[targetIndex];
            if (!presentedKeys.Contains(desiredEntry.Key))
            {
                PresentedItems.Insert(targetIndex, desiredEntry.Item);
                presentedKeys.Add(desiredEntry.Key);
                insertedCount++;
                continue;
            }

            // The directional plan leaves all surviving items in their target
            // relative order. This fallback protects compatibility callers that
            // still publish an unexpected item sequence instead of silently
            // corrupting the visible projection.
            if (targetIndex >= PresentedItems.Count
                || !string.Equals(GetItemKey(PresentedItems[targetIndex]), desiredEntry.Key, StringComparison.OrdinalIgnoreCase))
            {
                var existingIndex = FindPresentedIndexByKey(desiredEntry.Key, targetIndex + 1);
                if (existingIndex >= 0)
                {
                    PresentedItems.Move(existingIndex, targetIndex);
                    movedCount++;
                    strategy = "fallback";
                }
                else
                {
                    PresentedItems.Insert(targetIndex, desiredEntry.Item);
                    presentedKeys.Add(desiredEntry.Key);
                    insertedCount++;
                    strategy = "fallback-insert";
                    continue;
                }
            }

            if (!ReferenceEquals(PresentedItems[targetIndex], desiredEntry.Item))
            {
                PresentedItems[targetIndex] = desiredEntry.Item;
                replacedCount++;
            }
        }

        while (PresentedItems.Count > desiredEntries.Count)
        {
            PresentedItems.RemoveAt(PresentedItems.Count - 1);
            removedCount++;
        }

        if (UiPerformanceDiagnostics.IsEnabled && (movedCount != 0 || insertedCount + removedCount >= 8))
        {
            LogService.Info(
                $"[UIPerf] mod-list-reconcile; before={oldCount}; desired={desiredEntries.Count}; remove={removedCount}; insert={insertedCount}; move={movedCount}; strategy={strategy}; forward={forwardMoveCount}; backward={backwardMoveCount}.");
        }
    }

    private static IReadOnlyList<MoveOperation> BuildDirectionalMovePlan(
        IReadOnlyList<string> current,
        IReadOnlyList<string> desired,
        bool fromEnd)
    {
        var working = current.ToList();
        var result = new List<MoveOperation>();
        if (!fromEnd)
        {
            for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                if (string.Equals(working[targetIndex], desired[targetIndex], StringComparison.OrdinalIgnoreCase)) continue;
                var existingIndex = FindKeyIndex(working, desired[targetIndex], targetIndex + 1, working.Count - 1);
                if (existingIndex < 0) return Array.Empty<MoveOperation>();
                Move(working, existingIndex, targetIndex);
                result.Add(new MoveOperation(existingIndex, targetIndex));
            }
            return result;
        }

        for (var targetIndex = desired.Count - 1; targetIndex >= 0; targetIndex--)
        {
            if (string.Equals(working[targetIndex], desired[targetIndex], StringComparison.OrdinalIgnoreCase)) continue;
            var existingIndex = FindKeyIndex(working, desired[targetIndex], 0, targetIndex - 1);
            if (existingIndex < 0) return Array.Empty<MoveOperation>();
            Move(working, existingIndex, targetIndex);
            result.Add(new MoveOperation(existingIndex, targetIndex));
        }
        return result;
    }

    private static void Move(List<string> items, int fromIndex, int toIndex)
    {
        var value = items[fromIndex];
        items.RemoveAt(fromIndex);
        items.Insert(toIndex, value);
    }

    private static int FindKeyIndex(IReadOnlyList<string> items, string key, int minimumIndex, int maximumIndex)
    {
        for (var index = Math.Max(0, minimumIndex); index <= Math.Min(maximumIndex, items.Count - 1); index++)
        {
            if (string.Equals(items[index], key, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private int FindPresentedIndexByKey(string key, int minimumIndex)
    {
        for (var index = Math.Max(0, minimumIndex); index < PresentedItems.Count; index++)
        {
            if (string.Equals(key, GetItemKey(PresentedItems[index]), StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private void Cancel()
    {
        if (_pendingAnimationStartHandler is not null)
        {
            CompositionTarget.Rendering -= _pendingAnimationStartHandler;
            _pendingAnimationStartHandler = null;
        }
        _animationTimer.Stop();
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        _overlay.Children.Clear();
        _before.Clear();
        _queuedDesired = null;
        _pendingDesired = null;
        _transitionPending = false;
        _layoutDirty = false;
        _layoutWaitStartedTimestamp = 0;
        SetPhase(TransitionPhase.Idle);
        ResetVisibleContainers();
    }

    private Dictionary<string, RowSnapshot> CaptureRows()
    {
        var result = new Dictionary<string, RowSnapshot>(StringComparer.OrdinalIgnoreCase);
        using var perf = UiPerformanceDiagnostics.Measure(
            "ModList.CaptureRows",
            thresholdMilliseconds: 15,
            details: () => $"items={_itemsList.Items.Count}; captured={result.Count}; phase={_phase}");
        if (!_owner.IsLoaded) return result;
        foreach (var item in _itemsList.Items)
        {
            if (_itemsList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container || !container.IsVisible)
                continue;
            var key = GetItemKey(item);
            if (key is null || container.ActualWidth <= 0 || container.ActualHeight <= 0) continue;
            var position = container.TransformToAncestor(_itemsList).Transform(new Point());
            result[key] = new RowSnapshot(key, item, container, position, new Size(container.ActualWidth, container.ActualHeight));
        }
        return result;
    }

    private void QueueAnimationStart(IReadOnlyList<Action> starts)
    {
        if (_pendingAnimationStartHandler is not null)
        {
            CompositionTarget.Rendering -= _pendingAnimationStartHandler;
            _pendingAnimationStartHandler = null;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null) CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_pendingAnimationStartHandler, handler)) _pendingAnimationStartHandler = null;
            if (_disposed || _phase != TransitionPhase.Playing) return;

            // Stage row transforms and ghost presenters first, then begin their
            // clocks at the next render boundary. Without this boundary a large
            // filter can spend most of the animation duration measuring newly
            // created ghost presenters before the first frame is ever painted.
            foreach (var start in starts) start();
            _animationTimer.Stop();
            _animationTimer.Interval = Duration(230);
            _animationTimer.Start();
        };
        _pendingAnimationStartHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void StageRemovedRow(RowSnapshot snapshot, ICollection<Action> starts)
    {
        var presenter = new ContentPresenter
        {
            Content = snapshot.Item,
            ContentTemplate = _itemsList.ItemTemplate,
            Width = snapshot.Size.Width,
            Height = snapshot.Size.Height,
            Opacity = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(presenter, snapshot.Position.X);
        Canvas.SetTop(presenter, snapshot.Position.Y);
        _overlay.Children.Add(presenter);
        starts.Add(() =>
        {
            var animation = new DoubleAnimation(0, Duration(140)) { BeginTime = Duration(20) };
            animation.Completed += (_, _) => _overlay.Children.Remove(presenter);
            presenter.BeginAnimation(UIElement.OpacityProperty, animation);
        });
    }

    private static void StageMovedContainer(FrameworkElement container, double deltaY, ICollection<Action> starts)
    {
        if (Math.Abs(deltaY) < 0.5) return;
        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.Opacity = 1;
        var transform = new TranslateTransform(0, deltaY);
        container.RenderTransform = transform;
        starts.Add(() => transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Duration(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }));
    }

    private static void StageAddedContainer(FrameworkElement container, ICollection<Action> starts)
    {
        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.Opacity = 0;
        var transform = new TranslateTransform(0, 5);
        container.RenderTransform = transform;
        starts.Add(() =>
        {
            container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Duration(120)));
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Duration(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        });
    }

    private void ResetVisibleContainers()
    {
        foreach (var item in _itemsList.Items)
        {
            if (_itemsList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;
            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.Opacity = 1;
            container.RenderTransform = Transform.Identity;
        }
    }

    private static bool HasStructuralDifference(IEnumerable<object> current, IReadOnlyList<object> desired)
    {
        var currentKeys = current.Select(GetItemKey).Where(key => key is not null).ToArray();
        var desiredKeys = desired.Select(GetItemKey).Where(key => key is not null).ToArray();
        return !currentKeys.SequenceEqual(desiredKeys, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetItemKey(object? item)
    {
        if (item is null) return null;
        if (item is IModListSelectable selectable && !string.IsNullOrWhiteSpace(selectable.SelectionKey))
            return selectable.SelectionKey;

        // Compatibility fallback for the older tool pages that have not yet been
        // migrated to ModListStateController. The library/profile pages now use
        // the typed fast path above, so normal list animation no longer reflects
        // over every realized row.
        foreach (var propertyName in new[] { "SelectionKey", "Guid", "ModId", "Id" })
        {
            if (item.GetType().GetProperty(propertyName)?.GetValue(item)?.ToString() is { Length: > 0 } value)
                return value;
        }
        var mod = item.GetType().GetProperty("Mod")?.GetValue(item);
        return mod?.GetType().GetProperty("Guid")?.GetValue(mod)?.ToString();
    }

    private sealed record KeyedItem(string Key, object Item);
    private sealed record MoveOperation(int FromIndex, int ToIndex);
    private sealed record RowSnapshot(string Key, object Item, FrameworkElement Container, Point Position, Size Size);

    private enum TransitionPhase
    {
        Idle,
        AwaitingLayout,
        Playing,
    }
}
