using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HD2ModManager.Controls.Cards;

// 受管控的卡片栈。只认识 CardDescriptor，不认识任何业务类型。
//
// 动画流程照搬 ModListPanel 的过渡控制器，因为"随手 FLIP"一定会翻车：
//   1. Idle -> 收到变化请求（集合增删 / IsVisible 变化）
//   2. 立刻按 Id 抓一份"变化前"快照（容器、位置、尺寸），跳过不可见与零尺寸容器
//   3. AwaitingLayout：订阅 ItemsHost.LayoutUpdated，等容器真正生成完（GeneratorStatus）
//   4. Play：再抓"变化后"快照，逐项比较——
//        仍存在 -> 位移 delta 交给 TranslateTransform，200ms 回到 0
//        新增   -> 透明度 0 + 下移 5px，淡入并归位
//        已移除 -> 用旧位置/旧尺寸生成幽灵副本放在覆盖层上淡出，同时让下面的卡片合拢
//   5. Playing：动画时钟统一在下一个 CompositionTarget.Rendering 边界启动，
//      240ms 后回到 Idle；期间到达的新请求排队，结束后再跑一轮
//
// 两个必须守住的细节：
//   - 绝不在 UpdateLayout() 之后立刻读位置：那读到的仍是旧布局，会算出巨大的 delta，
//     表现为"卡片从页面顶部飞进来"。
//   - IsVisible 的绑定比 PropertyChanged 回调先执行，所以"消失"的那一刻容器已经塌陷、
//     无法再量位置；靠 _lastKnown 里最近一次量到的矩形给幽灵副本定位。
public partial class CardStackHost : UserControl
{
    private const int ReflowMilliseconds = 200;
    private const int AddFadeMilliseconds = 120;
    private const int AddSlideMilliseconds = 140;
    private const int RemoveFadeMilliseconds = 140;
    private const int RoundMilliseconds = 240;
    private const int HeightMilliseconds = 220;

    private enum Phase
    {
        Idle,
        AwaitingLayout,
        Playing
    }

    private readonly List<CardDescriptor> _observedItems = new();
    private readonly List<CardSnapshot> _pendingRemoved = new();
    // 在"还不能跑一轮"的时候变成可见的卡片：等能跑的时候要按"新增"处理，否则会直接跳出来。
    private readonly HashSet<string> _pendingShow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardSnapshot> _lastKnown = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _roundTimer = new() { Interval = TimeSpan.FromMilliseconds(RoundMilliseconds) };
    private Dictionary<string, CardSnapshot> _before = new(StringComparer.Ordinal);
    private INotifyCollectionChanged? _observedCollection;
    private EventHandler? _pendingRenderHandler;
    private Phase _phase;
    private bool _pendingRound;
    private bool _seeded;
    private int _ghostsStaged;
    private int _roundsPlayed;

    public static readonly DependencyProperty CardsProperty = DependencyProperty.Register(
        nameof(Cards),
        typeof(IEnumerable),
        typeof(CardStackHost),
        new PropertyMetadata(null, OnCardsPropertyChanged));

    public CardStackHost()
    {
        InitializeComponent();
        _roundTimer.Tick += OnRoundTimerTick;
        Loaded += (_, _) =>
        {
            // 首次布局完成后先量一遍：之后任何一次"消失"都要靠这份最近矩形定位幽灵副本；
            // 同时把"加载完成前就已经变成可见"的卡片补一次淡入。
            ItemsHost.LayoutUpdated += OnSeedLayoutUpdated;
            Dispatcher.BeginInvoke(new Action(SeedFirstLayout), DispatcherPriority.Loaded);
        };
    }

    public IEnumerable? Cards
    {
        get => (IEnumerable?)GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }

    // 只读诊断串：离屏渲染与回归测试用它确认"动画到底走到哪一步"，
    // 不参与任何业务逻辑。
    public string AnimationDiagnostics =>
        $"phase={_phase}; before={_before.Count}; pendingRemoved={_pendingRemoved.Count}; " +
        $"pendingShow={_pendingShow.Count}; rounds={_roundsPlayed}; " +
        $"lastKnown={_lastKnown.Count}; ghosts={_ghostsStaged}; " +
        $"overlay={TransitionOverlay.Children.Count}; loaded={IsLoaded}; seeded={_seeded}";

    private static void OnCardsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CardStackHost)d).AttachCards(e.NewValue as IEnumerable);

    private void AttachCards(IEnumerable? cards)
    {
        if (_observedCollection is not null) _observedCollection.CollectionChanged -= OnCardsCollectionChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= OnCardPropertyChanged;
        _observedItems.Clear();
        _observedCollection = null;
        _lastKnown.Clear();

        if (cards is null)
        {
            ItemsHost.ItemsSource = null;
            return;
        }

        var view = CollectionViewSource.GetDefaultView(cards);
        if (view.CanSort)
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(CardDescriptor.Order), ListSortDirection.Ascending));
            }
        }

        ItemsHost.ItemsSource = view;
        _observedCollection = cards as INotifyCollectionChanged;
        if (_observedCollection is not null) _observedCollection.CollectionChanged += OnCardsCollectionChanged;
        ObserveItems(cards);
    }

    private void ObserveItems(IEnumerable cards)
    {
        foreach (var item in _observedItems) item.PropertyChanged -= OnCardPropertyChanged;
        _observedItems.Clear();
        foreach (var entry in cards)
        {
            if (entry is not CardDescriptor card) continue;
            card.PropertyChanged += OnCardPropertyChanged;
            _observedItems.Add(card);
        }
    }

    private void OnCardsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnCardsCollectionChanged(sender, e)), DispatcherPriority.DataBind);
            return;
        }

        if (Cards is not null) ObserveItems(Cards);

        // 被移除的卡片已经不在 Items 里，容器也没了；用最近一次量到的矩形给它做退场幽灵。
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
        {
            foreach (var old in e.OldItems ?? Array.Empty<object>())
            {
                if (old is CardDescriptor card && _lastKnown.TryGetValue(card.Id, out var snapshot))
                    _pendingRemoved.Add(snapshot);
            }
        }

        BeginRound();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CardDescriptor card) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnCardPropertyChanged(card, e)), DispatcherPriority.DataBind);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(CardDescriptor.PreferredHeight), StringComparison.Ordinal))
        {
            AnimatePreferredHeight(card);
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(CardDescriptor.IsVisible), StringComparison.Ordinal)) return;

        // 绑定已经把容器显隐改掉了，但布局还没跑，所以此刻量到的位置仍是变化前的。
        var before = CaptureCards();
        if (card.IsVisible)
        {
            // 重新出现：从"变化前"里摘掉自己，交给 StageAdded 做淡入，
            // 否则会被当成"只是移动了"，看不到任何出现动画。
            before.Remove(card.Id);

            // 但如果这一轮跑不起来（页面还没加载完 / 上一轮还在播），
            // 这个"新增"身份必须留下来，等下一轮再按新增处理。
            _pendingShow.Add(card.Id);
        }
        else if (_lastKnown.TryGetValue(card.Id, out var snapshot))
        {
            _pendingRemoved.Add(snapshot);
        }

        BeginRound(before);
    }

    // 首次布局就绪后的一次性收尾：建立基线，并把加载期间变成可见的卡片补上淡入。
    private void SeedFirstLayout()
    {
        if (_seeded) return;
        if (!AreContainersReady()) return;

        _seeded = true;
        ItemsHost.LayoutUpdated -= OnSeedLayoutUpdated;

        var current = CaptureCards();
        if (_pendingShow.Count == 0) return;

        _before = current;
        _phase = Phase.Idle;
        PlayRound();
    }

    private void OnSeedLayoutUpdated(object? sender, EventArgs e) => SeedFirstLayout();

    private void BeginRound(Dictionary<string, CardSnapshot>? capturedBefore = null, bool layoutSettled = false)
    {
        // 页面还没加载完：把变化留给 SeedFirstLayout 处理（新增淡入 / 基线建立）。
        if (!IsLoaded || !_seeded) return;

        // 正在播动画时只记一个待办，结束后再跑，避免两轮动画互相覆盖 transform。
        if (_phase == Phase.Playing)
        {
            _pendingRound = true;
            return;
        }

        // 已经在等布局：布局回调会读到最新状态，保留最初那份"变化前"快照。
        if (_phase == Phase.AwaitingLayout) return;

        _before = capturedBefore ?? CaptureCards();
        _phase = Phase.AwaitingLayout;

        // 只有"排队轮次"才能在布局已就绪时直接开播：那时上一轮已经把布局跑完了。
        // 刚发生的可见性变化不能走这条捷径——此刻 IsMeasureValid 可能仍为真，
        // 但塌陷/展开还没进布局，直接读位置会得到旧值，下面的卡片就不会做 FLIP。
        if (layoutSettled && ItemsHost.IsMeasureValid && ItemsHost.IsArrangeValid && AreContainersReady())
        {
            _phase = Phase.Idle;
            PlayRound();
            return;
        }

        ItemsHost.LayoutUpdated -= OnLayoutUpdated;
        ItemsHost.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_phase != Phase.AwaitingLayout) return;
        if (!AreContainersReady()) return;

        ItemsHost.LayoutUpdated -= OnLayoutUpdated;
        PlayRound();
    }

    private bool AreContainersReady()
        => ItemsHost.Items.Count == 0
           || ItemsHost.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated;

    private void PlayRound()
    {
        if (!IsLoaded)
        {
            _phase = Phase.Idle;
            return;
        }

        var after = CaptureCards();

        // 上一轮播放期间/加载期间变成可见的卡片，不能因为"现在已经在 after 里"就被当成只是移动。
        foreach (var id in _pendingShow) _before.Remove(id);
        _pendingShow.Clear();

        var starts = new List<Action>();
        var stagedRemovals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in after)
        {
            if (_before.TryGetValue(pair.Key, out var previous))
                StageMoved(pair.Value, previous, starts);
            else
                StageAdded(pair.Value, starts);
        }

        foreach (var removed in _pendingRemoved)
        {
            if (!stagedRemovals.Add(removed.Key)) continue;
            StageRemoved(removed, starts);
        }

        foreach (var removed in _before.Values.Where(snapshot => !after.ContainsKey(snapshot.Key)))
        {
            if (!stagedRemovals.Add(removed.Key)) continue;
            StageRemoved(removed, starts);
        }

        _pendingRemoved.Clear();
        _before.Clear();
        _roundsPlayed++;

        if (starts.Count == 0)
        {
            _phase = Phase.Idle;
            DrainPendingRound();
            return;
        }

        _phase = Phase.Playing;
        QueueAnimationStart(starts);
    }

    private Dictionary<string, CardSnapshot> CaptureCards()
    {
        var result = new Dictionary<string, CardSnapshot>(StringComparer.Ordinal);
        if (!IsLoaded) return result;

        foreach (var item in ItemsHost.Items)
        {
            if (item is not CardDescriptor card) continue;
            if (ItemsHost.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;

            // 不可见或还没量出尺寸的卡片不能进快照：它们的"位置"没有意义，
            // 一旦参与 delta 计算就会把相邻卡片推到视口外。
            if (!container.IsVisible) continue;
            if (container.ActualWidth <= 0 || container.ActualHeight <= 0) continue;

            var position = container.TransformToAncestor(ItemsHost).Transform(new Point(0d, 0d));
            var snapshot = new CardSnapshot(card.Id, card, container, position, new Size(container.ActualWidth, container.ActualHeight));
            result[card.Id] = snapshot;
            _lastKnown[card.Id] = snapshot;
        }

        return result;
    }

    private static void StageMoved(CardSnapshot current, CardSnapshot previous, ICollection<Action> starts)
    {
        var delta = previous.Position.Y - current.Position.Y;
        if (Math.Abs(delta) < 0.5d) return;

        current.Container.BeginAnimation(UIElement.OpacityProperty, null);
        current.Container.Opacity = 1;
        var transform = new TranslateTransform(0d, delta);
        current.Container.RenderTransform = transform;
        starts.Add(() => transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0d, TimeSpan.FromMilliseconds(ReflowMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }));
    }

    private static void StageAdded(CardSnapshot current, ICollection<Action> starts)
    {
        current.Container.BeginAnimation(UIElement.OpacityProperty, null);
        current.Container.Opacity = 0d;
        var transform = new TranslateTransform(0d, 5d);
        current.Container.RenderTransform = transform;
        starts.Add(() =>
        {
            current.Container.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1d, TimeSpan.FromMilliseconds(AddFadeMilliseconds)));
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(0d, TimeSpan.FromMilliseconds(AddSlideMilliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        });
    }

    // 被移除的卡片已经没有容器了，所以在覆盖层上放一个同尺寸的幽灵副本原地淡出，
    // 让"消失"有交代；下面的卡片同时合拢。
    private void StageRemoved(CardSnapshot snapshot, ICollection<Action> starts)
    {
        var ghost = new ContentPresenter
        {
            Content = snapshot.Item,
            ContentTemplate = ItemsHost.ItemTemplate,
            Width = snapshot.Size.Width,
            Height = snapshot.Size.Height,
            Opacity = 1d,
            IsHitTestVisible = false
        };
        _ghostsStaged++;
        Canvas.SetLeft(ghost, snapshot.Position.X);
        Canvas.SetTop(ghost, snapshot.Position.Y);
        TransitionOverlay.Children.Add(ghost);

        starts.Add(() =>
        {
            var animation = new DoubleAnimation(0d, TimeSpan.FromMilliseconds(RemoveFadeMilliseconds))
            {
                BeginTime = TimeSpan.FromMilliseconds(20)
            };
            animation.Completed += (_, _) => TransitionOverlay.Children.Remove(ghost);
            ghost.BeginAnimation(UIElement.OpacityProperty, animation);
        });
    }

    // 先把所有 transform 摆好，再在下一个渲染边界统一启动时钟：
    // 否则新生成的幽灵副本会在动画时间里抢着测量，第一帧永远画不出来。
    private void QueueAnimationStart(IReadOnlyList<Action> starts)
    {
        if (_pendingRenderHandler is not null)
        {
            CompositionTarget.Rendering -= _pendingRenderHandler;
            _pendingRenderHandler = null;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null) CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_pendingRenderHandler, handler)) _pendingRenderHandler = null;
            if (_phase != Phase.Playing) return;

            foreach (var start in starts) start();
            _roundTimer.Stop();
            _roundTimer.Interval = TimeSpan.FromMilliseconds(RoundMilliseconds);
            _roundTimer.Start();
        };

        _pendingRenderHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void OnRoundTimerTick(object? sender, EventArgs e)
    {
        _roundTimer.Stop();
        _phase = Phase.Idle;
        DrainPendingRound();
    }

    private void DrainPendingRound()
    {
        if (!_pendingRound) return;
        _pendingRound = false;
        BeginRound(layoutSettled: true);
    }

    private void AnimatePreferredHeight(CardDescriptor card)
    {
        if (ItemsHost.ItemContainerGenerator.ContainerFromItem(card) is not ContentPresenter container) return;

        container.BeginAnimation(HeightProperty, null);
        if (card.PreferredHeight is not { } target || target <= 0d) return;

        var from = container.ActualHeight;
        if (from <= 0d || Math.Abs(from - target) < 0.5d) return;

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(HeightMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => container.BeginAnimation(HeightProperty, null);
        container.BeginAnimation(HeightProperty, animation);
    }

    private sealed record CardSnapshot(
        string Key,
        CardDescriptor Item,
        FrameworkElement Container,
        Point Position,
        Size Size);
}
