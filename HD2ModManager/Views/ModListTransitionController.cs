using System.Collections;
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
    private readonly ItemsControl _outgoingItemsList;
    private readonly Canvas _overlay;
    private readonly FrameworkElement _activePresentationLayer;
    private readonly FrameworkElement _outgoingPresentationLayer;
    private readonly bool _supportsDualPresentation;
    private readonly DispatcherTimer _animationTimer = new();
    private IEnumerable? _observedSource;
    private IModListProjectionSource? _projectionSource;
    private INotifyCollectionChanged? _observedItems;
    private IReadOnlyList<object> _desired = Array.Empty<object>();
    private IReadOnlyList<object>? _queuedDesired;
    private Dictionary<string, RowSnapshot> _before = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<object>? _pendingDesired;
    private bool _pendingDesiredDirty;
    private bool _transitionPending;
    private bool _layoutDirty;
    private IEnumerable? _suspendedSource;
    private bool _isSuspended;
    private EventHandler? _pendingAnimationStartHandler;
    private BulkVisualTransition? _bulkVisualTransition;
    private bool _bulkVisualActive;
    private long _layoutWaitStartedTimestamp;
    private TransitionPhase _phase;
    private bool _disposed;

    private static TimeSpan Duration(double milliseconds)
        => TimeSpan.FromMilliseconds(milliseconds);

    public ModListTransitionController(
        FrameworkElement owner,
        ItemsControl itemsList,
        ItemsControl outgoingItemsList,
        Canvas overlay,
        FrameworkElement activePresentationLayer,
        FrameworkElement outgoingPresentationLayer,
        BulkObservableCollection<object> presentedItems,
        BulkObservableCollection<object> outgoingPresentedItems,
        bool supportsDualPresentation = true)
    {
        _owner = owner;
        _itemsList = itemsList;
        _outgoingItemsList = outgoingItemsList;
        _overlay = overlay;
        _activePresentationLayer = activePresentationLayer;
        _outgoingPresentationLayer = outgoingPresentationLayer;
        _supportsDualPresentation = supportsDualPresentation;
        PresentedItems = presentedItems;
        OutgoingPresentedItems = outgoingPresentedItems;
        _animationTimer.Tick += OnAnimationTimerTick;
    }

    // Rich plan pages reuse the controller's row-level FLIP path but do not
    // provide a second visual host. Keep that compatibility path explicit
    // instead of making those pages pay for the Mod-list viewport transition.
    public ModListTransitionController(
        FrameworkElement owner,
        ItemsControl itemsList,
        Canvas overlay,
        BulkObservableCollection<object> presentedItems)
        : this(
            owner,
            itemsList,
            itemsList,
            overlay,
            itemsList,
            itemsList,
            presentedItems,
            new BulkObservableCollection<object>(),
            supportsDualPresentation: false)
    {
    }

    public BulkObservableCollection<object> PresentedItems { get; }
    public BulkObservableCollection<object> OutgoingPresentedItems { get; }
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

        if (_phase == TransitionPhase.AwaitingLayout)
        {
            if (!HasStructuralDifference(PresentedItems, desired))
            {
                SynchronizePresentedItems(desired);
                return;
            }

            // The next frame has not been committed visually yet. Keep only the
            // newest target and apply it at the next layout boundary instead of
            // repeatedly Resetting the active ListBox while it is materializing.
            _pendingDesired = desired;
            _pendingDesiredDirty = true;
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
            var useBulkVisualTransition = ShouldUseBulkVisualTransition(desired);
            _before = useBulkVisualTransition
                ? new Dictionary<string, RowSnapshot>(StringComparer.OrdinalIgnoreCase)
                : CaptureRows();
            _bulkVisualTransition = useBulkVisualTransition
                ? PrepareBulkVisualTransition(desired)
                : null;
            _transitionPending = true;
            _layoutWaitStartedTimestamp = UiPerformanceDiagnostics.IsEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
        }

        _pendingDesired = desired;
        _pendingDesiredDirty = false;
        SetPhase(TransitionPhase.AwaitingLayout);
        ApplyPendingDesired(desired);
        _layoutDirty = true;
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        _itemsList.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_layoutDirty || _disposed || _phase != TransitionPhase.AwaitingLayout) return;
        if (_pendingDesiredDirty && _pendingDesired is { } pendingDesired)
        {
            _pendingDesiredDirty = false;
            ApplyPendingDesired(pendingDesired);
            _layoutDirty = true;
            return;
        }
        if (!AreContainersReady(_itemsList)) return;
        if (_bulkVisualTransition is not null && !AreContainersReady(_outgoingItemsList)) return;

        _layoutDirty = false;
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        PlayPending();
    }

    private static bool AreContainersReady(ItemsControl items)
        => items.Items.Count == 0
            || items.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated;

    private void ApplyPendingDesired(IReadOnlyList<object> desired)
    {
        if (_bulkVisualTransition is not null)
            ReplacePresentedForBulkVisualTransition(desired);
        else
            SynchronizePresentedItems(desired);
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
        _pendingDesiredDirty = false;

        if (_bulkVisualTransition is { } bulkVisualTransition)
        {
            _bulkVisualTransition = null;
            _before.Clear();
            PlayBulkVisualTransition(bulkVisualTransition);
            return;
        }

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
        CompleteBulkVisualTransition();
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
        var wasTransitionActive = _phase != TransitionPhase.Idle;
        _phase = value;
        var isTransitionActive = _phase != TransitionPhase.Idle;
        if (wasTransitionActive != isTransitionActive) TransitionStateChanged?.Invoke(isTransitionActive);
    }

    private void ReplacePresentedInitial(IEnumerable<object> items)
    {
        PresentedItems.ReplaceWith(items);
    }

    // Large filters are projection replacements rather than row rearrangements.
    // Keep row-level FLIP for small changes. For a large change we prepare a
    // second, virtualized ListBox with the old stable row objects and crossfade
    // it against the active ListBox after the latter has laid out the new target.
    private bool ShouldUseBulkVisualTransition(IReadOnlyList<object> desired)
    {
        if (!_supportsDualPresentation) return false;
        const int bulkChangeThreshold = 16;
        var desiredEntries = CreateDesiredEntries(desired, reportDuplicateKeys: false);
        var desiredKeys = desiredEntries
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentKeys = PresentedItems
            .Select(GetItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = PresentedItems.Count(item =>
        {
            var key = GetItemKey(item);
            return string.IsNullOrWhiteSpace(key) || !desiredKeys.Contains(key);
        });
        var inserted = desiredEntries.Count(entry => !currentKeys.Contains(entry.Key));
        return removed + inserted >= bulkChangeThreshold;
    }

    private BulkVisualTransition PrepareBulkVisualTransition(IReadOnlyList<object> desired)
    {
        var desiredEntries = CreateDesiredEntries(desired, reportDuplicateKeys: false);
        var originalCount = PresentedItems.Count;
        var outgoingOffset = ReadVerticalOffset(_itemsList);

        // The old and new states intentionally use the same cached row objects,
        // but distinct visual hosts. WPF only realizes each host's viewport, so
        // this preserves virtualization while allowing a genuine crossfade.
        _outgoingPresentationLayer.Visibility = Visibility.Visible;
        _outgoingPresentationLayer.BeginAnimation(UIElement.OpacityProperty, null);
        _outgoingPresentationLayer.Opacity = 1;
        OutgoingPresentedItems.ReplaceWith(PresentedItems.ToArray());

        _activePresentationLayer.BeginAnimation(UIElement.OpacityProperty, null);
        _activePresentationLayer.Opacity = 0;
        _bulkVisualActive = true;
        return new BulkVisualTransition(originalCount, desiredEntries.Count, outgoingOffset);
    }

    private void ReplacePresentedForBulkVisualTransition(IReadOnlyList<object> desired)
    {
        var desiredEntries = CreateDesiredEntries(desired);
        PresentedItems.ReplaceWith(desiredEntries.Select(entry => entry.Item));
        if (UiPerformanceDiagnostics.IsEnabled)
        {
            LogService.Info(
                $"[UIPerf] mod-list-bulk-projection; before={_bulkVisualTransition?.OldCount ?? PresentedItems.Count}; desired={desiredEntries.Count}; mode=dual-host-crossfade.");
        }
    }

    private void PlayBulkVisualTransition(BulkVisualTransition transition)
    {
        RestoreOutgoingVerticalOffset(transition.OutgoingVerticalOffset);
        var animationStarts = new List<Action>
        {
            () =>
            {
                _activePresentationLayer.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(1, Duration(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
                _outgoingPresentationLayer.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(0, Duration(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                    });
            },
        };

        SetPhase(TransitionPhase.Playing);
        QueueAnimationStart(animationStarts);
    }

    private static double ReadVerticalOffset(DependencyObject root)
        => FindDescendant<ScrollViewer>(root)?.VerticalOffset ?? 0;

    private void RestoreOutgoingVerticalOffset(double sourceOffset)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(_outgoingItemsList);
        if (scrollViewer is null) return;
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(sourceOffset, 0, scrollViewer.ScrollableHeight));
    }

    private List<ModListProjectionPlanner.Entry> CreateDesiredEntries(IReadOnlyList<object> desired, bool reportDuplicateKeys = true)
        => ModListProjectionPlanner.CreateEntries(
            desired,
            GetItemKey,
            reportDuplicateKeys
                ? key => LogService.Warn($"Mod 列表投影出现重复稳定键，已忽略后续行：{key}")
                : null);

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

        var desiredEntries = CreateDesiredEntries(desired);
        var desiredKeys = desiredEntries
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            var plan = ModListProjectionPlanner.PlanDirectionalMoves(currentKeys, survivingDesiredKeys);
            forwardMoveCount = plan.ForwardMoveCount;
            backwardMoveCount = plan.BackwardMoveCount;
            strategy = plan.Strategy;

            foreach (var operation in plan.Operations)
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
        CompleteBulkVisualTransition();
        _overlay.Children.Clear();
        _before.Clear();
        _bulkVisualTransition = null;
        _queuedDesired = null;
        _pendingDesired = null;
        _pendingDesiredDirty = false;
        _transitionPending = false;
        _layoutDirty = false;
        _layoutWaitStartedTimestamp = 0;
        SetPhase(TransitionPhase.Idle);
        ResetVisibleContainers();
    }

    private void CompleteBulkVisualTransition()
    {
        if (!_bulkVisualActive) return;
        _bulkVisualActive = false;

        _activePresentationLayer.BeginAnimation(UIElement.OpacityProperty, null);
        _activePresentationLayer.Opacity = 1;
        _outgoingPresentationLayer.BeginAnimation(UIElement.OpacityProperty, null);
        _outgoingPresentationLayer.Opacity = 1;
        _outgoingPresentationLayer.Visibility = Visibility.Collapsed;
        if (OutgoingPresentedItems.Count != 0)
            OutgoingPresentedItems.ReplaceWith(Array.Empty<object>());
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

    private static T? FindDescendant<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            var child = VisualTreeHelper.GetChild(current, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
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

    private sealed record RowSnapshot(string Key, object Item, FrameworkElement Container, Point Position, Size Size);
    private sealed record BulkVisualTransition(int OldCount, int NewCount, double OutgoingVerticalOffset);

    private enum TransitionPhase
    {
        Idle,
        AwaitingLayout,
        Playing,
    }
}
