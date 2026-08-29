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
    private INotifyCollectionChanged? _observedItems;
    private IReadOnlyList<object> _desired = Array.Empty<object>();
    private IReadOnlyList<object>? _queuedDesired;
    private Dictionary<string, RowSnapshot> _before = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<object>? _pendingDesired;
    private bool _transitionPending;
    private bool _layoutDirty;
    private TransitionPhase _phase;
    private bool _disposed;

    private static TimeSpan Duration(double milliseconds)
        => TimeSpan.FromMilliseconds(milliseconds);

    public ModListTransitionController(FrameworkElement owner, ItemsControl itemsList, Canvas overlay, BulkObservableCollection<object> presentedItems)
    {
        _owner = owner;
        _itemsList = itemsList;
        _overlay = overlay;
        PresentedItems = presentedItems;
        _animationTimer.Tick += OnAnimationTimerTick;
    }

    public BulkObservableCollection<object> PresentedItems { get; }
    public bool IsPlaying => _phase == TransitionPhase.Playing;
    public event Action<bool>? TransitionStateChanged;

    public void Attach(IEnumerable? source)
    {
        DetachSource();
        Cancel();
        if (_disposed) return;
        _observedSource = source;
        _observedItems = source as INotifyCollectionChanged;
        if (_observedItems is not null) _observedItems.CollectionChanged += OnSourceCollectionChanged;
        _desired = source?.Cast<object>().ToArray() ?? Array.Empty<object>();
        ReplacePresentedImmediately(_desired);
    }

    public void Detach()
    {
        DetachSource();
        Cancel();
        PresentedItems.ReplaceWith(Array.Empty<object>(), ListTransitionKind.Refresh);
        _desired = Array.Empty<object>();
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
        if (_observedItems is not null) _observedItems.CollectionChanged -= OnSourceCollectionChanged;
        _observedSource = null;
        _observedItems = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        var desired = _observedSource?.Cast<object>().ToArray() ?? Array.Empty<object>();
        _desired = desired;
        UiPerformanceDiagnostics.RecordBurst(
            "ModList.SourceChanged",
            $"action={args.Action}; phase={_phase}; desired={desired.Length}",
            threshold: 12);

        if (_phase == TransitionPhase.Playing)
        {
            _queuedDesired = desired;
            return;
        }

        if (!HasStructuralDifference(PresentedItems, desired))
        {
            ReplacePresentedImmediately(desired);
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
        }

        _pendingDesired = desired;
        SetPhase(TransitionPhase.AwaitingLayout);
        ReplacePresentedImmediately(desired);
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
        using var perf = UiPerformanceDiagnostics.Measure(
            "ModList.PlayTransition",
            thresholdMilliseconds: 20,
            details: () => $"before={_before.Count}; after={_itemsList.Items.Count}; phase={_phase}");
        _transitionPending = false;
        _pendingDesired = null;

        var before = _before;
        _before = new Dictionary<string, RowSnapshot>(StringComparer.OrdinalIgnoreCase);
        var after = CaptureRows();
        var animated = false;

        foreach (var pair in after)
        {
            if (before.TryGetValue(pair.Key, out var old))
                animated |= AnimateMovedContainer(pair.Value.Container, old.Position.Y - pair.Value.Position.Y);
            else
                animated |= AnimateAddedContainer(pair.Value.Container);
        }

        foreach (var removed in before.Values.Where(snapshot => !after.ContainsKey(snapshot.Key)))
            animated |= AnimateRemovedRow(removed);

        if (!animated)
        {
            FinishRound();
            return;
        }

        SetPhase(TransitionPhase.Playing);
        _animationTimer.Stop();
        _animationTimer.Interval = Duration(230);
        _animationTimer.Start();
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
            ReplacePresentedImmediately(next);
        }

        SetPhase(TransitionPhase.Idle);
    }

    private void SetPhase(TransitionPhase value)
    {
        if (_phase == value) return;
        var wasLocked = _phase != TransitionPhase.Idle;
        _phase = value;
        var isLocked = _phase != TransitionPhase.Idle;
        if (wasLocked != isLocked) TransitionStateChanged?.Invoke(isLocked);
    }

    private void ReplacePresentedImmediately(IEnumerable<object> items)
        => PresentedItems.ReplaceWith(items, ListTransitionKind.Refresh);

    private void Cancel()
    {
        _animationTimer.Stop();
        _itemsList.LayoutUpdated -= OnLayoutUpdated;
        _overlay.Children.Clear();
        _before.Clear();
        _queuedDesired = null;
        _pendingDesired = null;
        _transitionPending = false;
        _layoutDirty = false;
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

    private bool AnimateRemovedRow(RowSnapshot snapshot)
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
        var animation = new DoubleAnimation(0, Duration(140)) { BeginTime = Duration(20) };
        animation.Completed += (_, _) => _overlay.Children.Remove(presenter);
        presenter.BeginAnimation(UIElement.OpacityProperty, animation);
        return true;
    }

    private static bool AnimateMovedContainer(FrameworkElement container, double deltaY)
    {
        if (Math.Abs(deltaY) < 0.5) return false;
        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.Opacity = 1;
        var transform = new TranslateTransform(0, deltaY);
        container.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Duration(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        return true;
    }

    private static bool AnimateAddedContainer(FrameworkElement container)
    {
        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.Opacity = 0;
        var transform = new TranslateTransform(0, 5);
        container.RenderTransform = transform;
        container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Duration(120)));
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Duration(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        return true;
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
        foreach (var propertyName in new[] { "SelectionKey", "Guid", "ModId", "Id" })
        {
            if (item.GetType().GetProperty(propertyName)?.GetValue(item)?.ToString() is { Length: > 0 } value)
                return value;
        }
        var mod = item.GetType().GetProperty("Mod")?.GetValue(item);
        return mod?.GetType().GetProperty("Guid")?.GetValue(mod)?.ToString();
    }

    private sealed record RowSnapshot(string Key, object Item, FrameworkElement Container, Point Position, Size Size);

    private enum TransitionPhase
    {
        Idle,
        AwaitingLayout,
        Playing,
    }
}
