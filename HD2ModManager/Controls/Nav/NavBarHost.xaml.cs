using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.Views;

namespace HD2ModManager.Controls.Nav;

// 声明的顶栏宿主。
//
// 只认识 NavItemDescriptor：文案、图标枚举、可见性、激活、角标、命令。
// 位置、外观、激活块的滑动、增删重排、滚动与两侧渐隐都在这里。
public partial class NavBarHost : UserControl
{
    private const int IndicatorMilliseconds = 240;
    private const int ReflowMilliseconds = 200;
    private const int FadeMilliseconds = 120;

    private readonly List<NavItemDescriptor> _observed = new();
    private INotifyCollectionChanged? _leadingCollection;
    private INotifyCollectionChanged? _trailingCollection;
    private CapsuleButtonGroup? _capsule;
    private bool _attached;
    private int _hideGeneration;

    public static readonly DependencyProperty LeadingItemsProperty = DependencyProperty.Register(
        nameof(LeadingItems), typeof(IEnumerable), typeof(NavBarHost), new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty TrailingItemsProperty = DependencyProperty.Register(
        nameof(TrailingItems), typeof(IEnumerable), typeof(NavBarHost), new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty CenterContentProperty = DependencyProperty.Register(
        nameof(CenterContent), typeof(object), typeof(NavBarHost), new PropertyMetadata(null, OnCenterContentChanged));

    public NavBarHost()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Attach();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PlaceIndicator(animate: false);
                UpdateFades();
            }), DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) =>
        {
            AttachCenterContent(_capsule, null);
            Detach();
        };
        ContentRoot.SizeChanged += (_, _) => PlaceIndicator(animate: false);
    }

    public IEnumerable? LeadingItems
    {
        get => (IEnumerable?)GetValue(LeadingItemsProperty);
        set => SetValue(LeadingItemsProperty, value);
    }

    public IEnumerable? TrailingItems
    {
        get => (IEnumerable?)GetValue(TrailingItemsProperty);
        set => SetValue(TrailingItemsProperty, value);
    }

    public object? CenterContent
    {
        get => GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NavBarHost host) return;
        host.Attach();
        host.ScheduleReflow();
    }

    private static void OnCenterContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NavBarHost host) return;
        host.AttachCenterContent(e.OldValue as CapsuleButtonGroup, e.NewValue as CapsuleButtonGroup);
        host.Dispatcher.BeginInvoke(new Action(() => host.PlaceIndicator(animate: false)), DispatcherPriority.Loaded);
    }

    private void AttachCenterContent(CapsuleButtonGroup? previous, CapsuleButtonGroup? next)
    {
        if (previous is not null) previous.ActiveZoneChanged -= OnCapsuleActiveZoneChanged;
        _capsule = next;
        if (next is not null) next.ActiveZoneChanged += OnCapsuleActiveZoneChanged;
    }

    private void OnCapsuleActiveZoneChanged(object? sender, EventArgs e) => PlaceIndicator(animate: true);

    private void Attach()
    {
        if (_leadingCollection is not null) _leadingCollection.CollectionChanged -= OnCollectionChanged;
        if (_trailingCollection is not null) _trailingCollection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _observed) item.PropertyChanged -= OnItemPropertyChanged;
        _observed.Clear();
        _leadingCollection = null;
        _trailingCollection = null;

        _leadingCollection = LeadingItems as INotifyCollectionChanged;
        if (_leadingCollection is not null) _leadingCollection.CollectionChanged += OnCollectionChanged;
        _trailingCollection = TrailingItems as INotifyCollectionChanged;
        if (_trailingCollection is not null) _trailingCollection.CollectionChanged += OnCollectionChanged;

        Observe(LeadingItems);
        Observe(TrailingItems);
        _attached = true;
    }

    private void Detach()
    {
        if (_leadingCollection is not null) _leadingCollection.CollectionChanged -= OnCollectionChanged;
        if (_trailingCollection is not null) _trailingCollection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _observed) item.PropertyChanged -= OnItemPropertyChanged;
        _observed.Clear();
        _leadingCollection = null;
        _trailingCollection = null;
        _attached = false;
    }

    private void Observe(IEnumerable? items)
    {
        if (items is null) return;
        foreach (var entry in items)
        {
            if (entry is not NavItemDescriptor descriptor) continue;
            descriptor.PropertyChanged += OnItemPropertyChanged;
            _observed.Add(descriptor);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnCollectionChanged(sender, e)), DispatcherPriority.DataBind);
            return;
        }

        Attach();
        ScheduleReflow();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnItemPropertyChanged(sender, e)), DispatcherPriority.DataBind);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(NavItemDescriptor.IsActive), StringComparison.Ordinal))
        {
            PlaceIndicator(animate: true);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(NavItemDescriptor.IsVisible), StringComparison.Ordinal))
        {
            ScheduleReflow();
        }
    }

    private void ScheduleReflow()
    {
        if (!_attached) return;
        var before = CaptureOffsets();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            ContentRoot.UpdateLayout();
            ApplyReflow(before);
            PlaceIndicator(animate: true);
            UpdateFades();
        }), DispatcherPriority.Loaded);
    }

    private Dictionary<object, double> CaptureOffsets()
    {
        var map = new Dictionary<object, double>(ReferenceEqualityComparer.Instance);
        if (!IsLoaded) return map;
        foreach (var host in new[] { LeadingHost, TrailingHost })
        {
            for (var index = 0; index < host.Items.Count; index++)
            {
                if (host.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter container) continue;
                map[host.Items[index]] = LeftOf(container);
            }
        }
        return map;
    }

    private void ApplyReflow(Dictionary<object, double> before)
    {
        foreach (var host in new[] { LeadingHost, TrailingHost })
        {
            for (var index = 0; index < host.Items.Count; index++)
            {
                if (host.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter container) continue;
                var item = host.Items[index];
                if (!before.TryGetValue(item, out var previousLeft)) continue;
                var delta = LeftOf(container) - previousLeft;
                if (Math.Abs(delta) < 0.5d) continue;

                if (container.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
                {
                    transform = new TranslateTransform();
                    container.RenderTransform = transform;
                }

                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = delta;
                transform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(0d, TimeSpan.FromMilliseconds(ReflowMilliseconds))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
        }
    }

    private double LeftOf(ContentPresenter container)
    {
        try
        {
            return container.TransformToAncestor(ContentRoot).Transform(new Point(0d, 0d)).X;
        }
        catch (InvalidOperationException)
        {
            return 0d;
        }
    }

    private ContentPresenter? FindActiveContainer(out NavItemDescriptor? active)
    {
        active = null;
        ContentPresenter? found = null;

        foreach (var item in _observed)
        {
            if (!item.IsActive || !item.IsVisible) continue;
            active = item;
            found = LeadingHost.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter
                    ?? TrailingHost.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            if (found is not null) break;
        }

        return found;
    }

    // 高亮块的目标：优先是激活的导航按钮；没有激活项时，落到中间胶囊当前激活的分区。
    // 胶囊的激活区只改文字颜色，底色一直由这块药丸提供——所以这里必须能接管它。
    private FrameworkElement? FindActiveTarget()
    {
        var container = FindActiveContainer(out _);
        if (container is not null) return FindVisualChild<Button>(container) ?? (FrameworkElement)container;
        return _capsule?.GetActiveButton();
    }

    private void PlaceIndicator(bool animate)
    {
        if (!IsLoaded) return;

        var target = FindActiveTarget();
        if (target is null || target.ActualWidth <= 0)
        {
            // 逻辑层是逐个改 IsActive 的，"旧项置 false"和"新项置 true"之间会有一瞬
            // 谁都不激活。此时不能立刻隐藏药丸——那会让它在移动途中消失再淡入。
            // 推到下一批数据绑定之后再确认一次。
            HideIndicatorSoon();
            return;
        }

        _hideGeneration++;

        // 高亮块必须贴着按钮本体，而不是 ContentPresenter：
        // 按钮样式里的 Margin="4,0" 会被算进容器宽度，直接用容器会左右各多出 4px。
        var point = target.TransformToAncestor(ContentRoot).Transform(new Point(0d, 0d));
        var width = target.ActualWidth;
        var height = target.ActualHeight;
        ActiveIndicator.CornerRadius = new CornerRadius(height / 2d);

        var isFirstPlacement = ActiveIndicator.ActualWidth <= 0;
        if (!animate || isFirstPlacement)
        {
            ActiveIndicator.BeginAnimation(WidthProperty, null);
            ActiveIndicator.BeginAnimation(HeightProperty, null);
            ActiveIndicator.BeginAnimation(Canvas.LeftProperty, null);
            ActiveIndicator.BeginAnimation(Canvas.TopProperty, null);
            ActiveIndicator.Width = width;
            ActiveIndicator.Height = height;
            Canvas.SetLeft(ActiveIndicator, point.X);
            Canvas.SetTop(ActiveIndicator, point.Y);
            ActiveIndicator.BeginAnimation(OpacityProperty, null);
            ActiveIndicator.Opacity = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(IndicatorMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // SnapshotAndReplace：打断一段进行中的滑动时，新动画从**当前视觉位置**继续，
        // 而不是退回基值重来。默认的 Replace 会让快速连点看起来"跳一下"。
        ActiveIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        ActiveIndicator.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        ActiveIndicator.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(point.X, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        ActiveIndicator.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(point.Y, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        ActiveIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1d, TimeSpan.FromMilliseconds(FadeMilliseconds)), HandoffBehavior.SnapshotAndReplace);
    }

    private void HideIndicatorSoon()
    {
        var generation = ++_hideGeneration;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _hideGeneration) return;
            if (FindActiveTarget() is not null) return;

            ActiveIndicator.BeginAnimation(OpacityProperty, null);
            ActiveIndicator.Opacity = 0;
        }), DispatcherPriority.DataBind);
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateFades();

    private void UpdateFades()
    {
        var scrollable = Scroller.ScrollableWidth;
        var offset = Scroller.HorizontalOffset;
        AnimateFade(LeftFade, scrollable > 0.5d && offset > 0.5d);
        AnimateFade(RightFade, scrollable > 0.5d && offset < scrollable - 0.5d);
    }

    private static void AnimateFade(UIElement element, bool visible)
    {
        var target = visible ? 1d : 0d;
        if (Math.Abs(element.Opacity - target) < 0.01d) return;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = target;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(FadeMilliseconds)));
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (Scroller.ScrollableWidth <= 0.5d) return;
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - Math.Sign(e.Delta) * 48d);
        e.Handled = true;
    }
}
