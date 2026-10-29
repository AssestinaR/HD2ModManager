using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：浮动操作簇（汉堡菜单）。
    // 契约：宿主只递交 PageActionViewModel 集合（Kind + Label + Tone + Command），
    // 图标、颜色、胶囊尺寸、列高、挤压、聚焦、拖动全部在这里。
    public partial class FloatingActionCluster : UserControl
    {
        // 列顶与可用区顶之间留的呼吸空间。
        private const double TopBoundary = 14d;
        private const double DragThreshold = 4d;

        private Effect? _pillShadow;
        // 悬停：只影响"胶囊 + 加重阴影 + 临时置顶"，不改任何位置。
        private int _hoveredIndex = -1;
        private double _pointerDistance = double.NaN;
        // 主按钮圆心到"最上方允许的圆心"的直线长度。所有圆片都排在这条线上。
        private double _lineLength;
        private double _restingOffset;
        private ListCollectionView? _view;
        private readonly List<PageActionViewModel> _observed = new();
        // 当前显示着标签的容器。只在状态真的变化时才动动画——每次布局都给所有项
        // 重启一遍动画，快速掠过时前一个的收回就会被反复打断。
        private readonly HashSet<ContentPresenter> _focusedPresenters = new();
        private FrameworkElement? _observedParent;

        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartOffset;
        private bool _suppressClick;

        public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
            nameof(Actions), typeof(IEnumerable), typeof(FloatingActionCluster), new PropertyMetadata(null, OnActionsChanged));
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
            nameof(IsExpanded), typeof(bool), typeof(FloatingActionCluster), new PropertyMetadata(false, OnIsExpandedChanged));
        public static readonly DependencyProperty MainToolTipProperty = DependencyProperty.Register(
            nameof(MainToolTip), typeof(object), typeof(FloatingActionCluster), new PropertyMetadata("操作"));
        public static readonly DependencyProperty ButtonDiameterProperty = DependencyProperty.Register(
            nameof(ButtonDiameter), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(48d, OnShapePropertyChanged));
        public static readonly DependencyProperty ActionSpacingProperty = DependencyProperty.Register(
            nameof(ActionSpacing), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(8d, OnShapePropertyChanged));
        public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
            nameof(ExpandedWidth), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(168d, OnShapePropertyChanged));
        public static readonly DependencyProperty ExpandedOuterWidthProperty = DependencyProperty.Register(
            nameof(ExpandedOuterWidth), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(184d));
        // 抽屉最长宽度 = 最短宽度的 3 倍。抽屉本身按文字长度自适应，夹在这两者之间。
        public static readonly DependencyProperty MaxExpandedWidthProperty = DependencyProperty.Register(
            nameof(MaxExpandedWidth), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(504d));
        public static readonly DependencyProperty ClusterHeightProperty = DependencyProperty.Register(
            nameof(ClusterHeight), typeof(double), typeof(FloatingActionCluster), new PropertyMetadata(48d));
        public static readonly DependencyProperty LabelDirectionProperty = DependencyProperty.Register(
            nameof(LabelDirection), typeof(string), typeof(FloatingActionCluster), new PropertyMetadata("Left", OnLabelDirectionChanged));
        public static readonly DependencyProperty ClusterHorizontalAlignmentProperty = DependencyProperty.Register(
            nameof(ClusterHorizontalAlignment), typeof(HorizontalAlignment), typeof(FloatingActionCluster), new PropertyMetadata(HorizontalAlignment.Right));

        public FloatingActionCluster()
        {
            InitializeComponent();
            UpdateShapeMetrics();
            ActionsHost.MouseMove += OnActionsHostMouseMove;
            // 键盘焦点也给同样的"放大 + 挤压"，否则键盘用户看不到焦点在哪一格。
            ActionsHost.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnItemGotKeyboardFocus), true);
            MouseLeave += OnClusterMouseLeave;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public IEnumerable? Actions { get => (IEnumerable?)GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
        public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
        public object? MainToolTip { get => GetValue(MainToolTipProperty); set => SetValue(MainToolTipProperty, value); }
        public double ButtonDiameter { get => (double)GetValue(ButtonDiameterProperty); set => SetValue(ButtonDiameterProperty, value); }
        public double ActionSpacing { get => (double)GetValue(ActionSpacingProperty); set => SetValue(ActionSpacingProperty, value); }
        public double ExpandedWidth { get => (double)GetValue(ExpandedWidthProperty); set => SetValue(ExpandedWidthProperty, value); }
        public double ExpandedOuterWidth { get => (double)GetValue(ExpandedOuterWidthProperty); private set => SetValue(ExpandedOuterWidthProperty, value); }
        public double MaxExpandedWidth { get => (double)GetValue(MaxExpandedWidthProperty); private set => SetValue(MaxExpandedWidthProperty, value); }
        public double ClusterHeight { get => (double)GetValue(ClusterHeightProperty); private set => SetValue(ClusterHeightProperty, value); }
        public string LabelDirection { get => (string)GetValue(LabelDirectionProperty); set => SetValue(LabelDirectionProperty, value); }
        public HorizontalAlignment ClusterHorizontalAlignment { get => (HorizontalAlignment)GetValue(ClusterHorizontalAlignmentProperty); private set => SetValue(ClusterHorizontalAlignmentProperty, value); }

        private static void OnLabelDirectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FloatingActionCluster cluster) return;
            cluster.ClusterHorizontalAlignment = string.Equals(cluster.LabelDirection, "Right", StringComparison.OrdinalIgnoreCase)
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }

        private static void OnShapePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => (d as FloatingActionCluster)?.UpdateShapeMetrics();

        private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FloatingActionCluster cluster) return;
            if (e.OldValue is INotifyCollectionChanged oldCollection)
                oldCollection.CollectionChanged -= cluster.OnActionsCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newCollection)
                newCollection.CollectionChanged += cluster.OnActionsCollectionChanged;
            cluster.RebuildView();
        }

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => (d as FloatingActionCluster)?.UpdateExpansionAnimation((bool)e.NewValue);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_observedParent is null && Parent is FrameworkElement parent)
            {
                _observedParent = parent;
                parent.SizeChanged += OnParentSizeChanged;
            }

            UpdateShapeMetrics();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_observedParent is null) return;
            _observedParent.SizeChanged -= OnParentSizeChanged;
            _observedParent = null;
        }

        // 列高跟着宿主可用高度走：顶栏以下的整块区域都算可用，只留上下呼吸空间。
        private void OnParentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateShapeMetrics();
            ApplyLayout(animateTranslate: false);
        }

        // ---- 集合与视图 ----

        private void RebuildView()
        {
            foreach (var action in _observed) action.PropertyChanged -= OnActionPropertyChanged;
            _observed.Clear();

            if (Actions is not null)
            {
                foreach (var action in Actions.OfType<PageActionViewModel>())
                {
                    action.PropertyChanged += OnActionPropertyChanged;
                    _observed.Add(action);
                }
            }

            // IsVisible 和 Order 以前是死属性；现在由视图负责过滤和排序，
            // 宿主可以"一次注册全部动作、按页面状态切换可见性"。
            var source = Actions as IList ?? Actions?.Cast<object>().ToList() ?? new List<object>();
            _view = new ListCollectionView(source)
            {
                Filter = candidate => candidate is not PageActionViewModel action || action.IsVisible,
            };
            _view.SortDescriptions.Add(new SortDescription(nameof(PageActionViewModel.Order), ListSortDirection.Ascending));
            ActionsHost.ItemsSource = _view;
            _focusedPresenters.Clear();
            UpdateShapeMetrics();
        }

        private void OnActionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(PageActionViewModel.IsVisible)
                || e.PropertyName == nameof(PageActionViewModel.Order))
            {
                _view?.Refresh();
                UpdateShapeMetrics();
            }
        }

        private void OnActionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildView();

        private int GetActionCount() => _view?.Count ?? 0;

        // ---- 几何 ----

        private void UpdateShapeMetrics()
        {
            // 抽屉按文字自适应：最短是 ExpandedWidth，最长是它的 3 倍。
            // 容器宽度要按最长算，否则抽屉会被容器的可用宽度夹住、长不出文字来。
            MaxExpandedWidth = Math.Max(ExpandedWidth, ExpandedWidth * 3d);
            ExpandedOuterWidth = Math.Max(MaxExpandedWidth + 16d, ButtonDiameter + 16d);

            var count = GetActionCount();
            var available = GetAvailableHeight();
            // 圆心线：从主按钮圆心向上，到"最上方允许的圆心"为止。
            _lineLength = Math.Max(ButtonDiameter, available - ButtonDiameter);

            var standard = ButtonDiameter + ActionSpacing;
            var used = count <= 0 ? 0d : Math.Min(_lineLength, standard * count);
            ClusterHeight = used + ButtonDiameter;
        }

        private double GetAvailableHeight()
        {
            if (Parent is FrameworkElement parent && parent.ActualHeight > 0)
                return parent.ActualHeight - Margin.Top - Margin.Bottom - TopBoundary;
            return 640d;
        }

        private double GetMaxDragOffset()
        {
            if (Parent is FrameworkElement parent && parent.ActualHeight > 0)
                return Math.Max(0d, parent.ActualHeight - Margin.Top - Margin.Bottom - ButtonDiameter);
            return 0d;
        }

        // ---- 布局 ----

        private void ApplyLayout(bool animateTranslate)
        {
            ActionsHost.UpdateLayout();

            var count = GetActionCount();
            if (count == 0) return;

            var centers = ComputeCenters(count);

            for (var i = 0; i < count; i++)
            {
                if (ActionsHost.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter) continue;

                if (presenter.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
                {
                    translate = new TranslateTransform();
                    presenter.RenderTransform = translate;
                }

                // 容器高度永远等于按钮直径：圆片不会被裁成鼓形，重叠只靠圆心间距实现。
                presenter.BeginAnimation(HeightProperty, null);
                presenter.Height = ButtonDiameter;

                // 层级：主按钮最高，离它越近越高，最上面那个在最下层。
                Panel.SetZIndex(presenter, count - i);

                var targetOffset = IsExpanded ? -centers[i] : 0d;
                if (animateTranslate && Math.Abs(translate.Y - targetOffset) >= 0.5)
                {
                    var currentOffset = translate.Y;
                    translate.Y = targetOffset;
                    translate.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(currentOffset, targetOffset, TimeSpan.FromMilliseconds(IsExpanded ? 260 : 190))
                        {
                            EasingFunction = IsExpanded
                                ? new QuinticEase { EasingMode = EasingMode.EaseOut }
                                : new CubicEase { EasingMode = EasingMode.EaseIn },
                            FillBehavior = FillBehavior.Stop,
                        },
                        HandoffBehavior.SnapshotAndReplace);
                }
                else
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    translate.Y = targetOffset;
                }
            }

            // 位置只跟数量和可用高度有关，悬停不改变任何位置。
            if (!double.IsNaN(_pointerDistance)) _hoveredIndex = FindNearestPill(_pointerDistance);
            UpdateFocusVisuals();
        }

        // 圆心到主按钮圆心的距离：均匀排布，间距不超过标准间距 a，整列不越出这条线。
        // 焦点不参与位置计算——凸显焦点靠阴影和层级，不靠挪位。
        private double[] ComputeCenters(int count)
        {
            var centers = new double[count];
            if (count <= 0) return centers;

            var standard = ButtonDiameter + ActionSpacing;
            // 严格不越出这条线：装不下就缩小间距让按钮重叠，不设下限。
            var pitch = count > 1
                ? Math.Min(standard, Math.Max(0d, (_lineLength - standard) / (count - 1)))
                : standard;

            for (var i = 0; i < count; i++) centers[i] = standard + i * pitch;
            return centers;
        }


        // 胶囊的弹出/收回动画。用 Storyboard + 默认的 HoldEnd：终值一定落定在目标值上，
        // 连续跨项时后一次 Begin 直接替换前一次，不会留下半透明残影。
        // 注意缩放必须走"元素的属性路径"——直接 SetTarget 到 ScaleTransform 这种
        // 游离的 Freezable 上，动画不会生效（透明度和缩放会一半动一半不动）。
        // 焦点视觉：胶囊弹出 + 加重阴影 + 临时置顶。完全不涉及位置。
        private void ApplyPillFocus(ContentPresenter presenter, bool focused, int zIndex)
        {
            // 只在状态真的变化时才动：每次布局都给所有项重启一遍动画，
            // 快速掠过时前一个胶囊的收回会被反复打断，看起来像卡住。
            var wasFocused = _focusedPresenters.Contains(presenter);
            if (focused == wasFocused) return;
            if (focused) _focusedPresenters.Add(presenter);
            else _focusedPresenters.Remove(presenter);

            // 阴影只给焦点项：重叠时每个圆片都带投影，会在邻居身上叠出一片柔和的
            // 过渡，看起来像两个煎蛋融在一起。非焦点项靠 1px 描边读边界。
            if (presenter.ContentTemplate?.FindName("ActionButton", presenter) is FrameworkElement button)
            {
                button.Effect = focused ? ResolvePillShadow() : null;
                // 临时置顶：焦点项压住邻居（但仍然在主按钮之下）。
                Panel.SetZIndex(presenter, focused ? 999 : zIndex);
            }

            if (presenter.ContentTemplate?.FindName("ExpandedRail", presenter) is not FrameworkElement rail) return;

            rail.IsHitTestVisible = focused;

            var duration = TimeSpan.FromMilliseconds(focused ? 170 : 130);
            var easing = new CubicEase { EasingMode = focused ? EasingMode.EaseOut : EasingMode.EaseIn };
            var storyboard = new Storyboard
            {
                Children =
                {
                    Create("Opacity", focused ? 1d : 0d),
                    Create("(UIElement.RenderTransform).(ScaleTransform.ScaleX)", focused ? 1d : 0.6d),
                    Create("(UIElement.RenderTransform).(ScaleTransform.ScaleY)", focused ? 1d : 0.9d),
                },
            };

            storyboard.Begin(rail, isControllable: true);

            DoubleAnimation Create(string path, double to)
            {
                var animation = new DoubleAnimation(to, duration) { EasingFunction = easing };
                Storyboard.SetTarget(animation, rail);
                Storyboard.SetTargetProperty(animation, new PropertyPath(path));
                return animation;
            }
        }

        private Effect? ResolvePillShadow()
        {
            if (_pillShadow is not null) return _pillShadow;
            _pillShadow = TryFindResource("ActionFocusShadow") as Effect;
            return _pillShadow;
        }
        private int CollapseLayout()
        {
            ActionsHost.UpdateLayout();

            var count = GetActionCount();
            var animated = 0;
            for (var i = 0; i < count; i++)
            {
                if (ActionsHost.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter) continue;
                if (presenter.RenderTransform is not TranslateTransform translate || translate.IsFrozen) continue;

                var currentOffset = translate.Y;
                if (Math.Abs(currentOffset) < 0.5)
                {
                    translate.Y = 0d;
                    continue;
                }

                translate.Y = 0d;
                var animation = new DoubleAnimation(currentOffset, 0d, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                    FillBehavior = FillBehavior.Stop,
                };
                animation.Completed += (_, _) => DisableActionsHitTestIfCollapsed();
                translate.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
                animated++;
            }

            return animated;
        }

        // ---- 展开 / 收起 ----

        private void UpdateExpansionAnimation(bool isExpanded)
        {
            if (isExpanded)
            {
                ActionsHost.IsHitTestVisible = true;
                ResetFocusState();
                // 展开即"重新归位"：按钮吸到底部，并且忘掉之前拖到的位置，
                // 否则收起时还要再挪回去，会出现"先上移再收起"的双段动作。
                _restingOffset = 0d;
                AnimateShift(0d);
                AnimateActionsOpacity(1d, 160d);
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyLayout(animateTranslate: true);
                }, DispatcherPriority.Loaded);
                return;
            }

            ResetFocusState();
            // 收起时按钮原地不动，只把动作列淡出并收回主按钮背后。
            AnimateActionsOpacity(0d, 150d);
            Dispatcher.BeginInvoke(() =>
            {
                var animations = CollapseLayout();
                if (animations == 0) DisableActionsHitTestIfCollapsed();
            }, DispatcherPriority.Loaded);
        }

        // 收起的动作列必须整体透明：它们叠在主按钮背后，每个都带阴影，
        // 不淡出的话阴影会累加，主按钮看起来像糊了一团。
        private void AnimateActionsOpacity(double target, double milliseconds)
        {
            var current = ActionsHost.Opacity;
            if (Math.Abs(current - target) < 0.01)
            {
                ActionsHost.BeginAnimation(OpacityProperty, null);
                ActionsHost.Opacity = target;
                return;
            }

            ActionsHost.Opacity = target;
            ActionsHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    FillBehavior = FillBehavior.Stop,
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void AnimateShift(double offset)
        {
            var target = -offset;
            var current = ClusterShift.Y;
            if (Math.Abs(current - target) < 0.5)
            {
                ClusterShift.BeginAnimation(TranslateTransform.YProperty, null);
                ClusterShift.Y = target;
                return;
            }

            ClusterShift.Y = target;
            ClusterShift.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void DisableActionsHitTestIfCollapsed()
        {
            if (!IsExpanded) ActionsHost.IsHitTestVisible = false;
        }

        private void ResetFocusState()
        {
            _hoveredIndex = -1;
            _pointerDistance = double.NaN;
        }

        // ---- 悬停 ----
        //
        // 凸显焦点只靠"胶囊弹出 + 加重阴影 + 临时置顶"，不改变任何按钮的位置，
        // 所以不存在漂移、抖动、边界越界这些问题，也就不需要防抖。

        private void OnActionsHostMouseMove(object sender, MouseEventArgs e)
        {
            if (!IsExpanded) return;

            var count = GetActionCount();
            if (count == 0) return;

            // 指针到主按钮圆心的距离。
            _pointerDistance = ActionsHost.ActualHeight - e.GetPosition(ActionsHost).Y - ButtonDiameter / 2d;

            var hovered = FindNearestPill(_pointerDistance);
            if (hovered == _hoveredIndex) return;

            _hoveredIndex = hovered;
            UpdateFocusVisuals();
        }

        private void OnClusterMouseLeave(object sender, MouseEventArgs e)
        {
            _pointerDistance = double.NaN;
            if (_hoveredIndex < 0) return;

            _hoveredIndex = -1;
            UpdateFocusVisuals();
        }

        private void OnItemGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!IsExpanded) return;

            var index = FindItemIndex(e.NewFocus as DependencyObject);
            if (index == _hoveredIndex) return;

            _hoveredIndex = index;
            UpdateFocusVisuals();
        }

        // 指针附近最近的圆心。
        private int FindNearestPill(double distance)
        {
            var count = GetActionCount();
            if (count <= 0 || double.IsNaN(distance)) return -1;

            var centers = ComputeCenters(count);
            var index = -1;
            var best = double.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var delta = Math.Abs(distance - centers[i]);
                if (delta >= best) continue;
                best = delta;
                index = i;
            }

            // 落在主按钮身上、或者离所有圆心都太远，就不算悬停。
            if (index >= 0 && (distance < ButtonDiameter * 0.45 || best > ButtonDiameter * 0.75)) index = -1;
            return index;
        }

        private void UpdateFocusVisuals()
        {
            var count = GetActionCount();
            for (var i = 0; i < count; i++)
            {
                if (ActionsHost.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter) continue;
                ApplyPillFocus(presenter, i == _hoveredIndex, count - i);
            }
        }

        private int FindItemIndex(DependencyObject? element)
        {
            for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ContentPresenter presenter)
                    return ActionsHost.ItemContainerGenerator.IndexFromContainer(presenter);
            }

            return -1;
        }

        // ---- 主按钮：拖动与开关 ----

        private void OnMainButtonDragStart(object sender, MouseButtonEventArgs e)
        {
            _suppressClick = false;
            if (IsExpanded) return;

            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartOffset = _restingOffset;
            MainButton.CaptureMouse();
        }

        private void OnMainButtonDragMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

            // 只允许上下移动：横向固定贴边，避免和页面内容抢位置。
            var delta = _dragStartPoint.Y - e.GetPosition(this).Y;
            if (!_suppressClick && Math.Abs(delta) > DragThreshold) _suppressClick = true;
            if (!_suppressClick) return;

            _restingOffset = Math.Clamp(_dragStartOffset + delta, 0d, GetMaxDragOffset());
            ClusterShift.Y = -_restingOffset;
            e.Handled = true;
        }

        private void OnMainButtonDragEnd(object sender, MouseButtonEventArgs e)
        {
            // 只清状态，不要在这里 ReleaseMouseCapture：Button 自己的 MouseUp 逻辑
            // 依赖"仍持有捕获"才会抛出 Click，提前释放会把开关一起吃掉。
            _isDragging = false;
        }

        private void OnMainButtonLostCapture(object sender, MouseEventArgs e) => _isDragging = false;

        private void OnMainButtonClick(object sender, RoutedEventArgs e)
        {
            if (_suppressClick)
            {
                _suppressClick = false;
                return;
            }

            IsExpanded = !IsExpanded;
        }

        // 菜单式交互：选完就收起。rail 里的内嵌控件（下拉、输入框）不经过这里。
        private void OnActionInvoked(object sender, RoutedEventArgs e) => IsExpanded = false;
    }
}
