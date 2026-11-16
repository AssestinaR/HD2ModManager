using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.Controls.EmptyState;
using HD2ModManager.Controls.Lists;
using HD2ModManager.Controls.Scrolling;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// Shared visual shell. Host pages retain all Mod-specific commands and transactions.
public partial class ModListPanel : UserControl, IRowActionToolTipSource, IRowActionAvailability, IModRelationshipDropTarget
{
    private readonly Dictionary<Border, SelectionIndicatorSubscription> _selectionIndicatorSubscriptions = new();
    private readonly BulkObservableCollection<object> _presentedItems = new();
    private readonly BulkObservableCollection<object> _outgoingPresentedItems = new();
    private readonly ModListTransitionController _transitionController;
    private string? _selectionAnchorKey;
    // Edge scrolling is deliberately sampled below the display refresh rate.
    // SmoothScrollBehavior owns the per-frame interpolation; this timer only
    // updates its target velocity and must not compete with rendering.
    private readonly DispatcherTimer _dragAutoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private DragCandidate? _dragCandidate;
    private Point _dragStartPoint;
    private Point? _dragPointerOnScreen;
    private ScrollViewer? _dragAutoScrollViewer;
    private int _dragAutoScrollDirection;
    private double _dragAutoScrollStep;
    private bool _transitionActive;
    private readonly MouseButtonEventHandler _dragUpGuardHandler;
    private bool _isInternalDragActive;
    private bool _isExternalProfileDragActive;
    private bool _isRelationshipDragActive;
    private bool _isRelationshipDropTargetActive;
    private bool _isExternalProfileRemovalTargetActive;
    private InternalDragPayload? _activeInternalDragPayload;
    private Cursor? _previousOverrideCursor;
    private DateTime _dragWheelCooldownUntilUtc;
    private bool _dropIndicatorRefreshQueued;
    private readonly ICommand _clearSearchCommand;
    private bool _registeredRelationshipDropTarget;

    public ModListPanel()
    {
        InitializeComponent();
        _clearSearchCommand = new RelayCommand(ClearSearch);
        UpdateEffectiveEmptyState();
        HeaderModes.CollectionChanged += OnHeaderModesChanged;
        ApplyHeaderContent();
        _transitionController = new ModListTransitionController(
            this,
            ItemsList,
            OutgoingItemsList,
            TransitionOverlay,
            ActivePresentationLayer,
            OutgoingPresentationLayer,
            _presentedItems,
            _outgoingPresentedItems);
        _transitionController.PresentedItems.CollectionChanged += OnPresentedItemsChanged;
        _transitionController.TransitionStateChanged += OnTransitionStateChanged;
        ItemsList.Loaded += (_, _) => ConfigureInternalSmoothScroll(UseInternalScroll);
        _dragAutoScrollTimer.Tick += OnDragAutoScrollTick;
        _dragUpGuardHandler = OnDragUpGuard;
        Loaded += (_, _) =>
        {
            _transitionController.Attach(ItemsSource);
            ConfigureInternalSmoothScroll(UseInternalScroll);
            // 回到一个带着查询的页面时，搜索框必须已经展开，否则过滤结果无法解释。
            if (!string.IsNullOrWhiteSpace(SearchText)) ShowSearch(focus: false);
            SyncRelationshipDropRegistration();
        };
        Unloaded += (_, _) =>
        {
            // Normal workspace navigation unloads a cached page view only long
            // enough to move it between hosts. Preserve the real presentation
            // collection so returning to a Mod-list page does not rebuild every
            // realized row from scratch.
            _transitionController.Suspend();
            EndInternalDrag();
            EndExternalRelationshipDrag(commit: false);
            EndExternalProfileDrag(commit: false);
            StopDragAutoScroll();
			ConfigureInternalSmoothScroll(false);
            if (_registeredRelationshipDropTarget)
            {
                ModRelationshipDragCoordinator.UnregisterTarget(this);
                _registeredRelationshipDropTarget = false;
            }
            RelationshipDropOverlay.HideImmediately();
        };
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ModListPanel), new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty HeaderTitleProperty = DependencyProperty.Register(nameof(HeaderTitle), typeof(string), typeof(ModListPanel), new PropertyMetadata("模组"));
    public static readonly DependencyProperty HeaderSummaryProperty = DependencyProperty.Register(nameof(HeaderSummary), typeof(string), typeof(ModListPanel), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(nameof(EmptyMessage), typeof(string), typeof(ModListPanel), new PropertyMetadata("没有可显示的 Mod。"));
    // 富空状态：给了它就渲染图标 + 标题 + 解释 + 可选动作，否则回落到 EmptyMessage。
    public static readonly DependencyProperty EmptyStateProperty = DependencyProperty.Register(nameof(EmptyState), typeof(object), typeof(ModListPanel), new PropertyMetadata(null, OnEmptyStateChanged));
    public static readonly DependencyProperty ShowHeaderProperty = DependencyProperty.Register(nameof(ShowHeader), typeof(bool), typeof(ModListPanel), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowSelectionCheckboxProperty = DependencyProperty.Register(nameof(ShowSelectionCheckbox), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty SelectionPolicyProperty = DependencyProperty.Register(nameof(SelectionPolicy), typeof(ModListSelectionPolicy), typeof(ModListPanel), new PropertyMetadata(ModListSelectionPolicy.None));
    public static readonly DependencyProperty AllowInternalReorderProperty = DependencyProperty.Register(nameof(AllowInternalReorder), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowExternalProfileDragProperty = DependencyProperty.Register(nameof(AllowExternalProfileDrag), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowExternalProfileDropProperty = DependencyProperty.Register(nameof(AllowExternalProfileDrop), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowExternalProfileRemovalDragProperty = DependencyProperty.Register(nameof(AllowExternalProfileRemovalDrag), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowExternalProfileRemovalDropProperty = DependencyProperty.Register(nameof(AllowExternalProfileRemovalDrop), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty RelationshipDragOperationProperty = DependencyProperty.Register(nameof(RelationshipDragOperation), typeof(ModRelationshipDragOperation), typeof(ModListPanel), new PropertyMetadata(ModRelationshipDragOperation.None));
    public static readonly DependencyProperty RelationshipDragSourceHostIdProperty = DependencyProperty.Register(nameof(RelationshipDragSourceHostId), typeof(string), typeof(ModListPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty ExternalDropTitleProperty = DependencyProperty.Register(nameof(ExternalDropTitle), typeof(string), typeof(ModListPanel), new PropertyMetadata("拖到这里释放以加入配置"));
    public static readonly DependencyProperty ExternalDropDetailProperty = DependencyProperty.Register(nameof(ExternalDropDetail), typeof(string), typeof(ModListPanel), new PropertyMetadata("不会改变当前配置中的排列顺序"));
    // 这个面板参与哪一种跨列表关系拖放。None = 不参与：不注册、不显示叠层、不命中。
    public static readonly DependencyProperty AcceptsRelationshipDropProperty = DependencyProperty.Register(nameof(AcceptsRelationshipDrop), typeof(ModRelationshipDragOperation), typeof(ModListPanel), new PropertyMetadata(ModRelationshipDragOperation.None, OnAcceptsRelationshipDropChanged));
    // 搜索框的展开状态由面板持有，但 SearchText 由宿主 VM 持有。宿主在
    // 页面重建后可能带着非空查询回来；没有这条同步就会出现"列表已被过滤，
    // 但搜索框收着、也看不到清除入口"的状态。
    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(ModListPanel), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSearchTextChanged));
    public static readonly DependencyProperty VerticalScrollBarVisibilityProperty = DependencyProperty.Register(nameof(VerticalScrollBarVisibility), typeof(ScrollBarVisibility), typeof(ModListPanel), new PropertyMetadata(ScrollBarVisibility.Auto));
    public static readonly DependencyProperty UseInternalScrollProperty = DependencyProperty.Register(nameof(UseInternalScroll), typeof(bool), typeof(ModListPanel), new PropertyMetadata(true, OnUseInternalScrollChanged));
    public static readonly DependencyProperty RowActionsProperty = DependencyProperty.Register(nameof(RowActions), typeof(ModListRowAction), typeof(ModListPanel), new PropertyMetadata(ModListRowAction.None, OnRowActionsChanged));
    public static readonly DependencyProperty HeaderTitleClickableProperty = DependencyProperty.Register(nameof(HeaderTitleClickable), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty ArmorAssignToolTipProperty = DependencyProperty.Register(nameof(ArmorAssignToolTip), typeof(string), typeof(ModListPanel), new PropertyMetadata("加入视觉来源或其映射"));
    public static readonly DependencyProperty ArmorReleaseToolTipProperty = DependencyProperty.Register(nameof(ArmorReleaseToolTip), typeof(string), typeof(ModListPanel), new PropertyMetadata("解除视觉来源或映射"));
    public static readonly DependencyProperty ArmorOpenDetailsToolTipProperty = DependencyProperty.Register(nameof(ArmorOpenDetailsToolTip), typeof(string), typeof(ModListPanel), new PropertyMetadata("查看映射目标"));
    public static readonly DependencyProperty AllowKeyboardRenameProperty = DependencyProperty.Register(nameof(AllowKeyboardRename), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty CategoryFilterProperty = DependencyProperty.Register(nameof(CategoryFilter), typeof(HD2ModManager.ViewModels.ModCategoryFilter), typeof(ModListPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty SearchActionsTemplateProperty = DependencyProperty.Register(nameof(SearchActionsTemplate), typeof(DataTemplate), typeof(ModListPanel), new PropertyMetadata(null, OnSearchActionsTemplateChanged));
    private static readonly DependencyPropertyKey HasPresentedItemsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasPresentedItems), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty HasPresentedItemsProperty = HasPresentedItemsPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey EffectiveEmptyStatePropertyKey = DependencyProperty.RegisterReadOnly(nameof(EffectiveEmptyState), typeof(object), typeof(ModListPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty EffectiveEmptyStateProperty = EffectiveEmptyStatePropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasRowActionsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasRowActions), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty HasRowActionsProperty = HasRowActionsPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasSearchActionsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasSearchActions), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty HasSearchActionsProperty = HasSearchActionsPropertyKey.DependencyProperty;

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IEnumerable PresentedItems => _presentedItems;
    public IEnumerable OutgoingPresentedItems => _outgoingPresentedItems;
    public string HeaderTitle { get => (string)GetValue(HeaderTitleProperty); set => SetValue(HeaderTitleProperty, value); }
    public string HeaderSummary { get => (string)GetValue(HeaderSummaryProperty); set => SetValue(HeaderSummaryProperty, value); }
    public string EmptyMessage { get => (string)GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }
    public object? EmptyState { get => GetValue(EmptyStateProperty); set => SetValue(EmptyStateProperty, value); }
    public bool ShowHeader { get => (bool)GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }
    public bool ShowSelectionCheckbox { get => (bool)GetValue(ShowSelectionCheckboxProperty); set => SetValue(ShowSelectionCheckboxProperty, value); }
    public ModListSelectionPolicy SelectionPolicy { get => (ModListSelectionPolicy)GetValue(SelectionPolicyProperty); set => SetValue(SelectionPolicyProperty, value); }
    public bool AllowInternalReorder { get => (bool)GetValue(AllowInternalReorderProperty); set => SetValue(AllowInternalReorderProperty, value); }
    public bool AllowExternalProfileDrag { get => (bool)GetValue(AllowExternalProfileDragProperty); set => SetValue(AllowExternalProfileDragProperty, value); }
    public bool AllowExternalProfileDrop { get => (bool)GetValue(AllowExternalProfileDropProperty); set => SetValue(AllowExternalProfileDropProperty, value); }
    public bool AllowExternalProfileRemovalDrag { get => (bool)GetValue(AllowExternalProfileRemovalDragProperty); set => SetValue(AllowExternalProfileRemovalDragProperty, value); }
    public bool AllowExternalProfileRemovalDrop { get => (bool)GetValue(AllowExternalProfileRemovalDropProperty); set => SetValue(AllowExternalProfileRemovalDropProperty, value); }
    public ModRelationshipDragOperation RelationshipDragOperation { get => (ModRelationshipDragOperation)GetValue(RelationshipDragOperationProperty); set => SetValue(RelationshipDragOperationProperty, value); }
    public string? RelationshipDragSourceHostId { get => (string?)GetValue(RelationshipDragSourceHostIdProperty); set => SetValue(RelationshipDragSourceHostIdProperty, value); }
    public string ExternalDropTitle { get => (string)GetValue(ExternalDropTitleProperty); set => SetValue(ExternalDropTitleProperty, value); }
    public string ExternalDropDetail { get => (string)GetValue(ExternalDropDetailProperty); set => SetValue(ExternalDropDetailProperty, value); }
    public ModRelationshipDragOperation AcceptsRelationshipDrop { get => (ModRelationshipDragOperation)GetValue(AcceptsRelationshipDropProperty); set => SetValue(AcceptsRelationshipDropProperty, value); }

    // 域判定（可选）。面板不猜"这个宿主现在能不能收"，只负责问；不设就等于恒真。
    public Func<ModRelationshipDragPayload, bool>? RelationshipDropFilter { get; set; }
    // 叠层文案（可选）。不给就按操作类型给通用文案。
    public Func<ModRelationshipDragPayload, ModListDropHint>? RelationshipDropHintFactory { get; set; }
    public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
    public ScrollBarVisibility VerticalScrollBarVisibility { get => (ScrollBarVisibility)GetValue(VerticalScrollBarVisibilityProperty); set => SetValue(VerticalScrollBarVisibilityProperty, value); }
    public bool UseInternalScroll { get => (bool)GetValue(UseInternalScrollProperty); set => SetValue(UseInternalScrollProperty, value); }
    public ModListRowAction RowActions { get => (ModListRowAction)GetValue(RowActionsProperty); set => SetValue(RowActionsProperty, value); }
    public bool HeaderTitleClickable { get => (bool)GetValue(HeaderTitleClickableProperty); set => SetValue(HeaderTitleClickableProperty, value); }
    public string ArmorAssignToolTip { get => (string)GetValue(ArmorAssignToolTipProperty); set => SetValue(ArmorAssignToolTipProperty, value); }
    public string ArmorReleaseToolTip { get => (string)GetValue(ArmorReleaseToolTipProperty); set => SetValue(ArmorReleaseToolTipProperty, value); }
    public string ArmorOpenDetailsToolTip { get => (string)GetValue(ArmorOpenDetailsToolTipProperty); set => SetValue(ArmorOpenDetailsToolTipProperty, value); }
    public bool AllowKeyboardRename { get => (bool)GetValue(AllowKeyboardRenameProperty); set => SetValue(AllowKeyboardRenameProperty, value); }
    // 宿主提供自动分类筛选时，面板在卡片顶部固定显示芯片条，并在搜索按钮旁提供开关图标。
    public HD2ModManager.ViewModels.ModCategoryFilter? CategoryFilter { get => (HD2ModManager.ViewModels.ModCategoryFilter?)GetValue(CategoryFilterProperty); set => SetValue(CategoryFilterProperty, value); }
    public DataTemplate? SearchActionsTemplate { get => (DataTemplate?)GetValue(SearchActionsTemplateProperty); set => SetValue(SearchActionsTemplateProperty, value); }

    // 同一动作在不同列表里意思不同（"解除视觉对象" vs "解除映射目标"），
    // 所以这四个文案由宿主覆盖；其余动作直接用 catalog 的默认文案。
    string? IRowActionToolTipSource.GetRowActionToolTip(ModListRowAction action) => action switch
    {
        ModListRowAction.ArmorAssign => ArmorAssignToolTip,
        ModListRowAction.ArmorRelease => ArmorReleaseToolTip,
        ModListRowAction.ArmorOpenDetails => ArmorOpenDetailsToolTip,
        _ => null,
    };
    public bool HasRowActions => (bool)GetValue(HasRowActionsProperty);
    public bool HasSearchActions => (bool)GetValue(HasSearchActionsProperty);
    public bool HasPresentedItems => (bool)GetValue(HasPresentedItemsProperty);
    // 最终真正渲染的空状态：宿主给的优先，否则查询非空时由面板派生。
    public object? EffectiveEmptyState => GetValue(EffectiveEmptyStateProperty);

    public event EventHandler<ModListRowEventArgs>? RowClicked;
    public event EventHandler<ModListRowEventArgs>? RowRightClicked;
    public event EventHandler<ModListSelectionRequestEventArgs>? SelectionRequested;
    public event EventHandler<ModListRowActionEventArgs>? RowActionInvoked;
    public event EventHandler? HeaderTitleClicked;
    public event EventHandler<ModListKeyboardCommandEventArgs>? KeyboardCommandRequested;
    public event EventHandler<ModListTransitionStateChangedEventArgs>? TransitionStateChanged;
    public event EventHandler<ModListInternalReorderEventArgs>? InternalReorderRequested;
    public event EventHandler<ModListExternalProfileDropEventArgs>? ExternalProfileDropRequested;
    public event EventHandler<ModListExternalProfileDropEventArgs>? ExternalProfileRemovalRequested;
    public event EventHandler<ModListRelationshipDropEventArgs>? RelationshipDropRequested;
    public event EventHandler? BackgroundClicked;

    private void OnPresentedItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SetValue(HasPresentedItemsPropertyKey, _transitionController.PresentedItems.Count != 0);

    private void OnTransitionStateChanged(bool isTransitionActive)
    {
        _transitionActive = isTransitionActive;
        SyncItemsHitTest();
        TransitionStateChanged?.Invoke(this, new ModListTransitionStateChangedEventArgs(isTransitionActive));
    }

    /// <summary>
    /// 列表在过渡期间不接受点击（防止动画中误点）。但**拖动进行中绝不能关**：
    /// WPF 在"持有鼠标捕获的元素变成不可命中"时会释放捕获，拖动会当场断掉——
    /// 表现就是拖到一半松手什么都没发生，而且只有第一次拖得动。
    /// </summary>
    private void SyncItemsHitTest()
    {
        ItemsList.IsHitTestVisible = !_transitionActive || IsDragActive;
    }

    private bool IsDragActive => _isInternalDragActive || _isRelationshipDragActive || _isExternalProfileDragActive;

    private void OnToggleSearchClick(object sender, RoutedEventArgs e)
    {
        if (_searchOpen)
        {
            HideSearch();
            return;
        }

        ShowSearchAndFocus();
    }

    // ---- 标题卡的状态：标题 / 搜索 / 引用方注册的额外状态 ----
    //
    // 面板只内置"标题"和"搜索"。除此之外想占用这块位置的人（模组库页与配置页的分类芯片条）
    // 往 HeaderModes 里加一项，面板负责互斥、交叉淡入淡出，并按注册项的图标与提示生成切换按钮。
    // 面板不再知道"分类"是什么，所以第四个、第五个状态不需要动面板。
    //
    // 互斥只由一个状态决定：_searchOpen 表示"搜索框要不要占这个位置"，其余由注册项的
    // IsRequested && IsAvailable 决定。所有内容常驻视觉树，只改 Opacity 和命中测试——
    // 不用"动画完成回调里改 Visibility"：快速来回切换时，上一轮 fade-out 的完成回调会把
    // 刚显示出来的元素又藏起来（标题消失、列表还在标题状态），这是这套动画最容易出的错。

    private const int HeaderFadeOutMilliseconds = 140;
    private const int HeaderFadeInMilliseconds = 160;

    private bool _searchOpen;

    /// <summary>
    /// 引用方注册的标题卡状态。按注册顺序取第一个"想显示且能显示"的；
    /// 程序化切换见 <see cref="SetHeaderMode"/>。
    /// </summary>
    public ObservableCollection<ModListHeaderMode> HeaderModes { get; } = new();

    private void OnHeaderModesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var mode in e.OldItems?.OfType<ModListHeaderMode>() ?? Enumerable.Empty<ModListHeaderMode>())
        {
            mode.StateChanged -= OnHeaderModeStateChanged;
            HeaderModeLayer.Children.Remove(mode);
        }

        foreach (var mode in e.NewItems?.OfType<ModListHeaderMode>() ?? Enumerable.Empty<ModListHeaderMode>())
        {
            mode.StateChanged += OnHeaderModeStateChanged;
            mode.Focusable = false;
            // 入场先透明：否则注册的那一刻会在标题上方闪一下。
            mode.Opacity = 0;
            mode.IsHitTestVisible = false;
            HeaderModeLayer.Children.Add(mode);
        }

        RebuildHeaderModeToggles();
        ApplyHeaderContent();
    }

    private void OnHeaderModeStateChanged(object? sender, EventArgs e)
    {
        UpdateHeaderModeToggles();
        ApplyHeaderContent();
    }

    // 切换按钮用代码生成：注册项本身是 UIElement，作为 ItemsControl 的项会被当成容器使用，
    // 而它已经挂在 HeaderModeLayer 上了（一个元素只能有一个父级）。
    private void RebuildHeaderModeToggles()
    {
        HeaderModeToggles.Children.Clear();
        foreach (var mode in HeaderModes)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            // 图标与提示用绑定而不是在这里取值：XAML 里注册项是先加进集合、后设属性的
            // （FrameworkElement 自上而下初始化），这一刻 ToggleIcon 还是 null，按钮就成了空圆。
            icon.SetBinding(System.Windows.Shapes.Path.DataProperty,
                new System.Windows.Data.Binding(nameof(ModListHeaderMode.ToggleIcon)) { Source = mode });
            var button = new Button
            {
                Style = TryFindResource("AppCategoryToggleButton") as Style,
                Tag = mode,
                Margin = new Thickness(0, 0, 2, 0),
                Content = icon,
            };
            button.SetBinding(ToolTipProperty,
                new System.Windows.Data.Binding(nameof(ModListHeaderMode.ToggleToolTip)) { Source = mode });
            button.Click += OnHeaderModeToggleClick;
            HeaderModeToggles.Children.Add(button);
        }

        UpdateHeaderModeToggles();
    }

    private void UpdateHeaderModeToggles()
    {
        for (var index = 0; index < HeaderModeToggles.Children.Count && index < HeaderModes.Count; index++)
        {
            if (HeaderModeToggles.Children[index] is not Button { Content: System.Windows.Shapes.Shape icon } button) continue;
            // 已请求时用强调色，和搜索键的"开着"观感一致。
            icon.Stroke = (button.Tag as ModListHeaderMode)?.IsRequested == true
                ? TryFindResource("Accent") as Brush
                : TryFindResource("TextSecondary") as Brush;
        }
    }

    private void OnHeaderModeToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModListHeaderMode mode }) return;

        // 判据是"它此刻是不是正占着这块位置"，不是 IsRequested：
        // 搜索框占位时分类的 IsRequested 仍是 true 但看不见，这时候点它应该是"抢回来"，
        // 而不是把分类整个关掉（那样列表会莫名其妙少一批东西，界面上却看不出发生了什么）。
        var visible = ReferenceEquals(ActiveHeaderMode, mode);
        var request = !visible;
        if (request) _searchOpen = false;
        mode.IsRequested = request;
        ApplyHeaderContent();
    }

    /// <summary>程序化切换：只请求 key 对应的状态（null = 交还给标题）。</summary>
    internal void SetHeaderMode(string? key)
    {
        if (key is not null) _searchOpen = false;
        foreach (var mode in HeaderModes)
            mode.IsRequested = key is not null && string.Equals(mode.Key, key, StringComparison.Ordinal);
        ApplyHeaderContent();
    }

    // 当前占位者：搜索优先（它开着就是用户在打字），否则第一个可用状态，再否则标题。
    private ModListHeaderMode? ActiveHeaderMode
        => _searchOpen ? null : HeaderModes.FirstOrDefault(mode => mode.IsActive);

    private void ApplyHeaderContent(bool focusSearch = false)
    {
        if (HeaderModeLayer is null) return;   // 还没 InitializeComponent

        var mode = ActiveHeaderMode;
        var title = !_searchOpen && mode is null;

        CrossFade(HeaderTitleElement, title);
        CrossFade(HeaderSummaryElement, title);
        CrossFade(HeaderSearchBox, _searchOpen);
        ApplyHeaderSearchActions(_searchOpen);

        foreach (var item in HeaderModes)
        {
            var active = ReferenceEquals(item, mode);
            CrossFade(item, active);
            // 淡出中的层不能继续吃点击：透明度 0 的元素照样命中测试。
            item.IsHitTestVisible = active;
        }

        HeaderTitleElement.IsHitTestVisible = title;
        // 摘要文字在 XAML 里排在状态层之后（盖在上面），淡出后仍会挡住分类第二行里字形所在的像素。
        HeaderSummaryElement.IsHitTestVisible = title;
        HeaderSearchBox.IsHitTestVisible = _searchOpen;
        // 不可见时不让 Tab 键走进搜索框。
        HeaderSearchBox.Focusable = _searchOpen;

        if (_searchOpen && focusSearch)
        {
            HeaderSearchBox.Focus();
            HeaderSearchBox.SelectAll();
        }
    }

    // 搜索动作区必须"不显示就彻底不参与布局"。
    //
    // 它的 Content 是宿主页面的 DataContext，而 MainWindow 为每个页面 VM 都注册了隐式
    // DataTemplate（VM → View）。宿主没给 SearchActionsTemplate 时，这个 ContentPresenter
    // 会退化成"用隐式模板渲染整个页面"，而页面里往往又嵌着 ModListPanel——于是
    // ModListPanel → 页面 → ModListPanel 无限套下去，直接 StackOverflow。
    // 所以：不用它的时候既不给 Content，也让 Visibility 收起。
    private void ApplyHeaderSearchActions(bool searchOpen)
    {
        var show = searchOpen && HasSearchActions;
        HeaderSearchActions.Content = show ? DataContext : null;
        HeaderSearchActions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // 交叉淡入淡出：只动 Opacity，终值立刻写进本地值（FillBehavior.Stop 让动画只负责观感），
    // 所以不存在"动画没跑完、状态却是旧的"的中间态。
    private static void CrossFade(UIElement element, bool visible)
    {
        if (element is null) return;

        var target = visible ? 1d : 0d;
        var duration = TimeSpan.FromMilliseconds(visible ? HeaderFadeInMilliseconds : HeaderFadeOutMilliseconds);
        var animation = new DoubleAnimation(target, duration) { FillBehavior = FillBehavior.Stop };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        element.Opacity = target;
    }

    private void OnHeaderTitleClick(object sender, MouseButtonEventArgs e)
    {
        if (!HeaderTitleClickable) return;
        HeaderTitleClicked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private static void OnSearchTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ModListPanel panel) return;
        // 只负责"从收起变为展开"。清空查询时不收起搜索框——用户可能正在退格重输，
        // 把输入框从脚下撤掉是最糟的反馈。
        if (panel.IsLoaded && !panel._searchOpen && !string.IsNullOrWhiteSpace(e.NewValue as string))
            panel.ShowSearch(focus: false);
        panel.UpdateEffectiveEmptyState();
    }

    private static void OnEmptyStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => (sender as ModListPanel)?.UpdateEffectiveEmptyState();

    private static void OnAcceptsRelationshipDropChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => (sender as ModListPanel)?.SyncRelationshipDropRegistration();

    private void SyncRelationshipDropRegistration()
    {
        var participates = AcceptsRelationshipDrop != ModRelationshipDragOperation.None && IsLoaded;
        if (participates == _registeredRelationshipDropTarget) return;

        if (participates)
        {
            ModRelationshipDragCoordinator.RegisterTarget(this);
            _registeredRelationshipDropTarget = true;
            return;
        }

        ModRelationshipDragCoordinator.UnregisterTarget(this);
        _registeredRelationshipDropTarget = false;
        RelationshipDropOverlay.HideImmediately();
    }

    // 面板自己就知道"有查询、但投影为空"，所以这件事不该由每个宿主 VM 各写一遍
    // （库页写了三分支、护甲页写了两个属性）。宿主给了 EmptyState 就用宿主的——
    // 页面文案永远比通用文案具体；没给而查询非空，就派生一个通用"没有匹配"态，
    // 并且由面板自己负责清除查询，不需要宿主提供命令。
    private void UpdateEffectiveEmptyState()
    {
        var state = EmptyState as EmptyStatePresentation;
        if (state is null && !string.IsNullOrWhiteSpace(SearchText))
        {
            state = new EmptyStatePresentation(
                "没有匹配的条目",
                "试试更短的关键词，或清除搜索回到全部内容。",
                "清除搜索",
                _clearSearchCommand,
                EmptyStateIcon.Search);
        }

        SetValue(EffectiveEmptyStatePropertyKey, state);
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        ShowSearchAndFocus();
    }

    private void ShowSearchAndFocus() => ShowSearch(focus: true);

    private void ShowSearch(bool focus)
    {
        // 已经在搜索模式：只处理"把焦点给输入框"，不重跑动画。
        if (_searchOpen)
        {
            if (!focus) return;
            HeaderSearchBox.Focus();
            HeaderSearchBox.SelectAll();
            return;
        }

        _searchOpen = true;
        ApplyHeaderContent(focusSearch: focus);
    }

    private void HideSearch()
    {
        // 收起搜索框必须同时放弃过滤。否则列表还停在"筛过的结果"上，而解释这个
        // 结果的控件已经不见了——用户会以为自己的 Mod 丢了，而且没有任何线索。
        // 这条和 OnSearchTextChanged 里的自动展开合起来维持一个不变量：
        // 查询非空 ⟺ 搜索框可见。
        if (!string.IsNullOrWhiteSpace(SearchText)) SearchText = string.Empty;
        if (!_searchOpen) return;

        _searchOpen = false;
        // 收起后回到"分类 / 标题"中的一个，而不是无条件回到标题：
        // 分类还开着的话，用户刚才看的应该是分类。
        ApplyHeaderContent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ItemsList.IsHitTestVisible || !ItemsList.IsKeyboardFocusWithin || IsTextInputFocused(e.OriginalSource as DependencyObject)) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F && modifiers == ModifierKeys.Control)
        {
            ShowSearchAndFocus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && modifiers == ModifierKeys.Control)
        {
            SelectAllVisibleRows();
            e.Handled = true;
            return;
        }

        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0) return;

        if (e.Key == Key.F2
            && modifiers == ModifierKeys.None
            && (AllowKeyboardRename || RowActions.HasFlag(ModListRowAction.Rename))
            && selectedItems.Count == 1)
        {
            RaiseKeyboardCommand(ModListKeyboardCommand.Rename, selectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && RowActions.HasFlag(ModListRowAction.Details) && selectedItems.Count == 1)
        {
            RaiseKeyboardCommand(ModListKeyboardCommand.OpenDetails, selectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Delete || (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift)) return;
        if (modifiers == ModifierKeys.Shift)
        {
            if (!RowActions.HasFlag(ModListRowAction.DeleteFromLibrary)) return;
            RaiseKeyboardCommand(ModListKeyboardCommand.DeleteFromLibrary, selectedItems);
            e.Handled = true;
            return;
        }

        if (!RowActions.HasFlag(ModListRowAction.RemoveFromProfile)
            && !RowActions.HasFlag(ModListRowAction.DeleteFromLibrary)) return;
        RaiseKeyboardCommand(ModListKeyboardCommand.Delete, selectedItems);
        e.Handled = true;
    }

    private void SelectAllVisibleRows()
    {
        if (SelectionPolicy != ModListSelectionPolicy.Multiple) return;
        var visibleItems = ItemsList.Items
            .OfType<IModListSelectable>()
            .Where(item => !item.IsBlocked && !string.IsNullOrWhiteSpace(item.SelectionKey))
            .ToArray();
        var selectedKeys = visibleItems.Select(item => item.SelectionKey).ToArray();
        if (selectedKeys.Length == 0) return;
        _selectionAnchorKey = selectedKeys[0];
        SelectionRequested?.Invoke(this, new ModListSelectionRequestEventArgs(visibleItems[0], selectedKeys, ModifierKeys.Control));
    }

    private IReadOnlyList<object> GetSelectedItems()
        => ItemsList.Items
            .Cast<object>()
            .Where(item => item is IModListSelectable { IsSelected: true })
            .ToArray();

    private void RaiseKeyboardCommand(ModListKeyboardCommand command, IReadOnlyList<object> selectedItems)
    {
        var selectedKeys = selectedItems
            .OfType<IModListSelectable>()
            .Select(item => item.SelectionKey)
            .ToArray();
        KeyboardCommandRequested?.Invoke(this, new ModListKeyboardCommandEventArgs(command, selectedItems, selectedKeys));
    }

    private static bool IsTextInputFocused(DependencyObject? source)
    {
        var focused = Keyboard.FocusedElement as DependencyObject ?? source;
        return FindAncestor<TextBoxBase>(focused) is not null
            || FindAncestor<PasswordBox>(focused) is not null
            || FindAncestor<ComboBox>(focused) is not null;
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        ItemsList.Focus();
        var item = (sender as FrameworkElement)?.DataContext;
        if (item is IModListSelectable { IsBlocked: true })
        {
            e.Handled = true;
            return;
        }
        if (item is IModListSelectable selectable && SelectionPolicy is not ModListSelectionPolicy.None and not ModListSelectionPolicy.ReadOnly)
        {
            var selectedKeys = ResolveSelection(selectable, Keyboard.Modifiers);
            SelectionRequested?.Invoke(this, new ModListSelectionRequestEventArgs(item, selectedKeys, Keyboard.Modifiers));
        }
        else
        {
            RowClicked?.Invoke(this, new ModListRowEventArgs(item, Keyboard.Modifiers));
        }
        e.Handled = true;
    }

    private IReadOnlyList<string> ResolveSelection(IModListSelectable clicked, ModifierKeys modifiers)
    {
        var selectableItems = ItemsList.Items.OfType<IModListSelectable>().Where(item => !item.IsBlocked).ToList();
        if (clicked.IsBlocked)
        {
            return selectableItems.Where(item => item.IsSelected).Select(item => item.SelectionKey).ToList();
        }
        if (SelectionPolicy == ModListSelectionPolicy.Single)
        {
            _selectionAnchorKey = clicked.SelectionKey;
            return [clicked.SelectionKey];
        }

        var selected = selectableItems.Where(item => item.IsSelected).Select(item => item.SelectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && !string.IsNullOrWhiteSpace(_selectionAnchorKey))
        {
            var anchorIndex = selectableItems.FindIndex(item => string.Equals(item.SelectionKey, _selectionAnchorKey, StringComparison.OrdinalIgnoreCase));
            var clickedIndex = selectableItems.FindIndex(item => string.Equals(item.SelectionKey, clicked.SelectionKey, StringComparison.OrdinalIgnoreCase));
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                selected.Clear();
                foreach (var item in selectableItems.Skip(Math.Min(anchorIndex, clickedIndex)).Take(Math.Abs(anchorIndex - clickedIndex) + 1))
                    selected.Add(item.SelectionKey);
            }
        }
        else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (!selected.Remove(clicked.SelectionKey)) selected.Add(clicked.SelectionKey);
            _selectionAnchorKey = clicked.SelectionKey;
        }
        else
        {
            selected.Clear();
            selected.Add(clicked.SelectionKey);
            _selectionAnchorKey = clicked.SelectionKey;
        }

        return selectableItems.Where(item => selected.Contains(item.SelectionKey)).Select(item => item.SelectionKey).ToList();
    }

    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        ItemsList.Focus();
        RowRightClicked?.Invoke(this, new ModListRowEventArgs((sender as FrameworkElement)?.DataContext, Keyboard.Modifiers));
        e.Handled = true;
    }

    // 没有分类筛选（宿主没传，或用户关掉了分类）时，"设置分类"动作不出现。
    public ModListRowAction UnavailableActions
        => CategoryFilter is { IsEnabled: true } ? ModListRowAction.None : ModListRowAction.Category;

    // 点行上的"设置分类"：目标是被选中的所有行（点的这行在选中里时），否则只是点的这一行。
    private void ShowCategoryMenu(FrameworkElement anchor, object rowItem)
    {
        if (CategoryFilter is not { IsEnabled: true } filter) return;
        var guids = (rowItem is IModListSelectable { IsSelected: true }
                ? GetSelectedItems()
                : new[] { rowItem })
            .OfType<IModListSelectable>()
            .Select(item => item.SelectionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (guids.Length == 0) return;

        // 卡片宽 300 + 两侧 14 的阴影边距；右对齐到按钮，避免从窗口右缘溢出。
        // 开合由 CategoryPopup 统一管：再点这一行的按钮就是收起。
        CategoryPopup.Toggle(anchor, PlacementMode.Bottom, anchor.ActualWidth - 328 + 14, -6,
            () => new CategoryPickerCard(filter, guids, CategoryPopup.Close));
    }

    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        // 按钮在行模板里的 ItemsControl 中，所以按钮的 DataContext 是动作项而不是
        // 行对象；行对象必须从容器上取回来。
        if (sender is not FrameworkElement { Tag: ModListRowActionItem actionItem }) return;
        if (FindAncestor<ListBoxItem>(sender as DependencyObject)?.DataContext is not { } rowItem) return;
        if (rowItem is IModListSelectable { IsBlocked: true })
        {
            e.Handled = true;
            return;
        }
        if (actionItem.Action == ModListRowAction.Category)
        {
            // 分类是面板自己的能力（数据都在 CategoryFilter 里），不需要宿主页面再转一手。
            ShowCategoryMenu((FrameworkElement)sender, rowItem);
            e.Handled = true;
            return;
        }
        RowActionInvoked?.Invoke(this, new ModListRowActionEventArgs(rowItem, actionItem.Action));
        e.Handled = true;
    }

    private void OnListBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 保险：上一次拖动如果在窗口外松手，抬起事件谁都收不到，状态会残留。
        // 新的一次按下先把残留清掉，否则这一下会被误当成上一次拖动的延续。
        if (IsDragActive)
        {
            EndInternalDrag();
            EndExternalRelationshipDrag(commit: false);
            EndExternalProfileDrag(commit: false);
        }
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null)
        {
            if ((AllowInternalReorder
                || AllowExternalProfileDrag
                || RelationshipDragOperation != ModRelationshipDragOperation.None)
                && FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is IModListSelectable { IsSelected: true } selectable)
            {
                var selectedItems = ItemsList.Items.OfType<IModListSelectable>()
                    .Where(item => item.IsSelected)
                    .ToList();
                var canInternalReorder = AllowInternalReorder
                    && selectedItems.Count != 0
                    && selectedItems.All(item => item is IModListInternalReorderable { CanInternalReorder: true });
                var canRelationshipDrag = RelationshipDragOperation != ModRelationshipDragOperation.None
                    && selectedItems.Count != 0;
                if ((canInternalReorder || AllowExternalProfileDrag || canRelationshipDrag) && selectedItems.Count != 0)
                {
                    _dragCandidate = new DragCandidate(
                        selectable.SelectionKey,
                        selectedItems.Select(item => item.SelectionKey).ToList());
                    _dragStartPoint = e.GetPosition(ItemsList);
                }
            }
            // The panel owns row selection, so prevent Selector's drag-selection auto-scroll.
            e.Handled = true;
            return;
        }
        BackgroundClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemsListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 拖动结算通常在 OnDragUpGuard（窗口级）就完成了，它先于这里执行并把事件吃掉。
        // 下面这三支是"面板没有挂在窗口里、兜底挂不上"时的退路——不是重复逻辑，
        // 两条路都调同一组 End* 方法，谁先跑到谁结算，后到的因为状态已清而空转。
        if (_isInternalDragActive)
        {
            EndInternalDrag(e.GetPosition(ItemsList));
            e.Handled = true;
            return;
        }

        if (_isExternalProfileDragActive)
        {
            EndExternalProfileDrag(commit: true, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (_isRelationshipDragActive)
        {
            EndExternalRelationshipDrag(commit: true, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        // Rows with a selection policy are owned by this panel, rather than by
        // ListBox's native selection model. Commit their selection here, at the
        // ListBox preview boundary, so virtualized/templated rows cannot lose
        // the bubbling MouseLeftButtonUp event before their host ViewModel sees
        // the complete selected-key set. This is especially important for the
        // decoration planners, where a selection immediately drives the plan.
        if (TryRequestSelectionAtPointer(e))
        {
            _dragCandidate = null;
            e.Handled = true;
            return;
        }

        _dragCandidate = null;
    }

    /// <summary>
    /// 拖动期间在窗口级兜住鼠标抬起。WPF 有时会在拖动中途释放捕获（原因未明），
    /// 那一瞬间抬起事件会落到光标下的别的元素上，列表收不到，拖动就"静默失败"了。
    /// 窗口级的预览处理器一定能收到，所以把它作为唯一出口——捕获还在时也走这里，
    /// 只有一条路径，不会重复处理。
    /// </summary>
    private void OnDragUpGuard(object sender, MouseButtonEventArgs e)
    {
        if (!IsDragActive) return;
        if (_isExternalProfileDragActive)
        {
            EndExternalProfileDrag(commit: true, e.GetPosition(this));
        }
        else if (_isRelationshipDragActive)
        {
            EndExternalRelationshipDrag(commit: true, e.GetPosition(this));
        }
        else
        {
            EndInternalDrag(e.GetPosition(ItemsList));
        }

        e.Handled = true;
    }

    private void AttachDragUpGuard()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _dragUpGuardHandler);
        window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, _dragUpGuardHandler, handledEventsToo: true);
    }

    private void DetachDragUpGuard()
    {
        var window = Window.GetWindow(this);
        window?.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _dragUpGuardHandler);
    }


    private bool TryRequestSelectionAtPointer(MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null)
            return false;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not IModListSelectable selectable
            || SelectionPolicy is ModListSelectionPolicy.None or ModListSelectionPolicy.ReadOnly)
            return false;

        if (selectable.IsBlocked)
        {
            ItemsList.Focus();
            return true;
        }

        ItemsList.Focus();
        var selectedKeys = ResolveSelection(selectable, Keyboard.Modifiers);
        SelectionRequested?.Invoke(this, new ModListSelectionRequestEventArgs(selectable, selectedKeys, Keyboard.Modifiers));
        return true;
    }

    private void OnItemsListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(ItemsList);
        if (_isInternalDragActive)
        {
            UpdateInternalDrag(position);
            return;
        }

        if (_isExternalProfileDragActive || _isRelationshipDragActive) return;

        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var candidate = _dragCandidate;
        _dragCandidate = null;
        if (AllowInternalReorder)
            BeginInternalDrag(candidate, position);
        else if (RelationshipDragOperation != ModRelationshipDragOperation.None
            && BeginExternalRelationshipDrag(candidate))
        {
            // The relationship target accepted the gesture.
        }
        else if (AllowExternalProfileDrag)
            BeginExternalProfileDrag(candidate);
    }

    private void BeginInternalDrag(DragCandidate candidate, Point position)
    {
        _isInternalDragActive = true;
        SyncItemsHitTest();
        AttachDragUpGuard();
        _activeInternalDragPayload = new InternalDragPayload(candidate.SelectedKeys);
        _isRelationshipDropTargetActive = RelationshipDragOperation != ModRelationshipDragOperation.None
            && ModRelationshipDragCoordinator.TryBegin(
                this,
                RelationshipDragOperation,
                GetRelationshipDragKeys(candidate.SelectedKeys),
                RelationshipDragSourceHostId);
        _isExternalProfileRemovalTargetActive = AllowExternalProfileRemovalDrag
            && ModListExternalProfileRemovalCoordinator.TryBegin(this, candidate.SelectedKeys);
        _previousOverrideCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.SizeAll;
        Mouse.Capture(ItemsList, CaptureMode.Element);
        UpdateInternalDrag(position);
    }

    private void UpdateInternalDrag(Point position)
    {
        _dragPointerOnScreen = ItemsList.PointToScreen(position);
        if (IsPointInsideItemsList(position))
        {
            UpdateDropInsertionIndicator(position);
            UpdateDragAutoScroll();
        }
        else
        {
            HideDropInsertionIndicator();
            StopDragAutoScroll();
        }
    }

    private void EndInternalDrag(Point? dropPosition = null)
    {
        var payload = _activeInternalDragPayload;
        _activeInternalDragPayload = null;
        _dragCandidate = null;
        _isInternalDragActive = false;
        SyncItemsHitTest();
        DetachDragUpGuard();
        try
        {
            var position = dropPosition;
            var relationshipCompleted = _isRelationshipDropTargetActive
                && position is { } relationshipPosition
                && ModRelationshipDragCoordinator.Complete(this, ItemsList.PointToScreen(relationshipPosition));
            var removed = !relationshipCompleted
                && _isExternalProfileRemovalTargetActive
                && position is { } screenCandidate
                && ModListExternalProfileRemovalCoordinator.Complete(this, ItemsList.PointToScreen(screenCandidate));
            if (!relationshipCompleted && !removed && payload is not null && position is { } listPosition && IsPointInsideItemsList(listPosition))
                InternalReorderRequested?.Invoke(this, new ModListInternalReorderEventArgs(payload.SelectedKeys, GetInsertionIndex(listPosition)));
        }
        finally
        {
            if (_isRelationshipDropTargetActive) ModRelationshipDragCoordinator.Cancel(this);
            _isRelationshipDropTargetActive = false;
            if (_isExternalProfileRemovalTargetActive) ModListExternalProfileRemovalCoordinator.Cancel(this);
            _isExternalProfileRemovalTargetActive = false;
            if (Mouse.Captured == ItemsList) Mouse.Capture(null);
            Mouse.OverrideCursor = _previousOverrideCursor;
            _previousOverrideCursor = null;
            HideDropInsertionIndicator();
            StopDragAutoScroll();
        }
    }

    private bool IsPointInsideItemsList(Point point)
        => point.X >= 0 && point.Y >= 0 && point.X <= ItemsList.ActualWidth && point.Y <= ItemsList.ActualHeight;

    private void OnItemsListLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!IsDragActive) return;

        // 捕获丢失**不等于**拖动结束。
        // WPF 会在拖动开始约 0.3 秒后自己把捕获清成 null（ChangeMouseCapture(null)，
        // 从 InputManager.ProcessStagingArea 进来，列表本身可用/可见/可命中、也没有过渡在跑）。
        // 早先这里直接把拖动取消，于是用户真正松手时什么都没发生——那正是"拖不动"的成因。
        // 现在抬起统一由窗口级兜底处理器结算，这里只记录，不动状态。
    }

    private void BeginExternalProfileDrag(DragCandidate candidate)
    {
        if (!ModListExternalProfileDropCoordinator.TryBegin(this, candidate.SelectedKeys))
        {
            // 拖不起来的三种原因都在 TryBegin 里，让它自己写日志，这里只记一次"到此一游"。
            return;
        }
        _isExternalProfileDragActive = true;
        SyncItemsHitTest();
        AttachDragUpGuard();
        _previousOverrideCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.SizeAll;
        Mouse.Capture(ItemsList, CaptureMode.Element);
    }

    private bool BeginExternalRelationshipDrag(DragCandidate candidate)
    {
        var selectedKeys = GetRelationshipDragKeys(candidate.SelectedKeys);
        if (!ModRelationshipDragCoordinator.TryBegin(
                this,
                RelationshipDragOperation,
                selectedKeys,
                RelationshipDragSourceHostId))
            return false;

        _isRelationshipDragActive = true;
        SyncItemsHitTest();
        AttachDragUpGuard();
        _previousOverrideCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.SizeAll;
        Mouse.Capture(ItemsList, CaptureMode.Element);
        return true;
    }

    private IReadOnlyList<string> GetRelationshipDragKeys(IReadOnlyList<string> selectedKeys)
    {
        if (RelationshipDragOperation != ModRelationshipDragOperation.RemoveOptionParent)
            return selectedKeys;

        // The details list displays Options and Decorations together. A release
        // gesture must never accidentally detach a Decoration; those rows are
        // filtered before the drop target is shown.
        var selectableByKey = ItemsList.Items
            .OfType<IModListSelectable>()
            .ToDictionary(item => item.SelectionKey, StringComparer.OrdinalIgnoreCase);
        return selectedKeys
            .Where(key => selectableByKey.TryGetValue(key, out var item) && !item.IsDecoration)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void EndExternalProfileDrag(bool commit, Point? dropPosition = null)
    {
        if (!_isExternalProfileDragActive) return;
        _isExternalProfileDragActive = false;
        SyncItemsHitTest();
        DetachDragUpGuard();
        _dragCandidate = null;
        try
        {
            if (commit && dropPosition is { } position)
                ModListExternalProfileDropCoordinator.Complete(this, PointToScreen(position));
            else
                ModListExternalProfileDropCoordinator.Cancel(this);
        }
        finally
        {
            if (Mouse.Captured == ItemsList) Mouse.Capture(null);
            Mouse.OverrideCursor = _previousOverrideCursor;
            _previousOverrideCursor = null;
        }
    }

    private void EndExternalRelationshipDrag(bool commit, Point? dropPosition = null)
    {
        if (!_isRelationshipDragActive) return;
        _isRelationshipDragActive = false;
        SyncItemsHitTest();
        DetachDragUpGuard();
        _dragCandidate = null;
        try
        {
            if (commit && dropPosition is { } position)
                ModRelationshipDragCoordinator.Complete(this, PointToScreen(position));
            else
                ModRelationshipDragCoordinator.Cancel(this);
        }
        finally
        {
            if (Mouse.Captured == ItemsList) Mouse.Capture(null);
            Mouse.OverrideCursor = _previousOverrideCursor;
            _previousOverrideCursor = null;
        }
    }

    internal bool ContainsScreenPoint(Point point)
    {
        if (!IsLoaded || !AllowExternalProfileDrop || ItemsList.ActualWidth <= 0 || ItemsList.ActualHeight <= 0)
        {
            return false;
        }
        var local = ItemsList.PointFromScreen(point);
        var inside = local.X >= 0 && local.Y >= 0 && local.X <= ItemsList.ActualWidth && local.Y <= ItemsList.ActualHeight;
        if (!inside)
        {
        }
        return inside;
    }

    internal bool ContainsScreenPointForRemoval(Point point)
    {
        if (!IsLoaded || !AllowExternalProfileRemovalDrop || ItemsList.ActualWidth <= 0 || ItemsList.ActualHeight <= 0) return false;
        var local = ItemsList.PointFromScreen(point);
        return local.X >= 0 && local.Y >= 0 && local.X <= ItemsList.ActualWidth && local.Y <= ItemsList.ActualHeight;
    }

    internal void SetExternalProfileDropOverlay(bool visible)
    {
        if (visible)
        {
            ExternalDropOverlay.Show(new ModListDropHint(ExternalDropTitle, ExternalDropDetail));
            return;
        }

        ExternalDropOverlay.Hide();
    }

    // ---- IModRelationshipDropTarget ----
    // 面板自己就是关系拖放的目标：叠层、命中、淡入淡出、注册生命周期都在这里。
    // 宿主只回答三件事：参与哪种操作（AcceptsRelationshipDrop）、现在能不能收
    // （RelationshipDropFilter，可选）、放下之后做什么（RelationshipDropRequested）。

    bool IModRelationshipDropTarget.CanAcceptRelationshipDrop(ModRelationshipDragPayload payload)
    {
        if (!IsLoaded || !IsVisible) return false;
        if (AcceptsRelationshipDrop == ModRelationshipDragOperation.None) return false;
        if (payload.Operation != AcceptsRelationshipDrop) return false;
        if (payload.SelectedKeys.Count == 0) return false;
        if (!ModRelationshipDragCoordinator.IsVisuallyActive(this)) return false;
        return RelationshipDropFilter?.Invoke(payload) ?? true;
    }

    bool IModRelationshipDropTarget.ContainsScreenPoint(Point screenPoint)
        => IsLoaded && RelationshipDropOverlay.ContainsPoint(screenPoint);

    // 源和目标是同一个面板时必须把自己排除掉——否则拖自己的行会在自己身上
    // 弹出"释放以解除关系"，松手还会真的执行。IsAncestorOf 对自身返回 false，
    // 所以这里要先比引用。
    bool IModRelationshipDropTarget.ContainsElement(DependencyObject element)
        => ReferenceEquals(element, this) || IsAncestorOf(element);

    void IModRelationshipDropTarget.SetRelationshipDropOverlay(ModRelationshipDragPayload payload, bool visible)
    {
        if (visible)
        {
            RelationshipDropOverlay.Show(
                RelationshipDropHintFactory?.Invoke(payload) ?? DefaultRelationshipDropHint(payload));
            return;
        }

        RelationshipDropOverlay.Hide();
    }

    void IModRelationshipDropTarget.RaiseRelationshipDropRequested(ModRelationshipDragPayload payload)
        => RelationshipDropRequested?.Invoke(this, new ModListRelationshipDropEventArgs(payload));

    private static ModListDropHint DefaultRelationshipDropHint(ModRelationshipDragPayload payload)
    {
        var count = payload.SelectedKeys.Count;
        return payload.Operation switch
        {
            ModRelationshipDragOperation.SetAsOption => new("释放以设为选项", $"将 {count} 个 Mod 挂载到该主体下"),
            ModRelationshipDragOperation.RemoveOptionParent => new("释放以解除选项关系", $"将 {count} 个选项移出该主体"),
            ModRelationshipDragOperation.ArmorRegistryAssign => new("释放以设为视觉来源", $"将 {count} 个护甲设为视觉对象"),
            ModRelationshipDragOperation.ArmorRegistryRelease => new("释放以解除管理", "移除视觉来源会同时释放它的全部目标"),
            _ => new("释放以应用", null),
        };
    }

    internal void RaiseExternalProfileDropRequested(IReadOnlyList<string> selectedKeys)
        => ExternalProfileDropRequested?.Invoke(this, new ModListExternalProfileDropEventArgs(selectedKeys));

    internal void RaiseExternalProfileRemovalRequested(IReadOnlyList<string> selectedKeys)
        => ExternalProfileRemovalRequested?.Invoke(this, new ModListExternalProfileDropEventArgs(selectedKeys));

    private int GetInsertionIndex(Point position)
    {
        var realized = GetRealizedContainers();
        if (realized.Count == 0) return ItemsList.Items.Count;
        foreach (var row in realized)
        {
            var rowPosition = row.Container.TransformToAncestor(ItemsList).Transform(new Point());
            if (position.Y < rowPosition.Y + (row.Container.ActualHeight / 2)) return row.Index;
        }
        return realized[^1].Index + 1;
    }

    private void UpdateDropInsertionIndicator(Point position)
    {
        var insertionIndex = GetInsertionIndex(position);
        var realized = GetRealizedContainers();
        if (realized.Count == 0) return;
        var before = realized.FirstOrDefault(row => row.Index >= insertionIndex);
        var top = before is not null
            ? before.Container.TransformToAncestor(ItemsList).Transform(new Point()).Y - 1
            : realized[^1].Container.TransformToAncestor(ItemsList).Transform(new Point()).Y + realized[^1].Container.ActualHeight - 1;
        Canvas.SetLeft(DropInsertionIndicator, 8);
        Canvas.SetTop(DropInsertionIndicator, Math.Max(0, top));
        DropInsertionIndicator.Width = Math.Max(0, ItemsList.ActualWidth - 16);
        DropInsertionIndicator.Visibility = Visibility.Visible;
    }

    private List<RealizedRow> GetRealizedContainers()
    {
        var rows = new List<RealizedRow>();
        for (var index = 0; index < ItemsList.Items.Count; index++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container && container.IsVisible)
                rows.Add(new RealizedRow(index, container));
        }
        return rows;
    }

    private void HideDropInsertionIndicator() => DropInsertionIndicator.Visibility = Visibility.Collapsed;

    private void UpdateDragAutoScroll()
    {
        if (!TryResolveDragAutoScroll(out var scrollViewer, out var direction, out var step))
        {
            StopDragAutoScroll();
            return;
        }

        _dragAutoScrollViewer = scrollViewer;
        _dragAutoScrollDirection = direction;
        _dragAutoScrollStep = step;
        if (!_dragAutoScrollTimer.IsEnabled) _dragAutoScrollTimer.Start();
    }

    private void OnDragAutoScrollTick(object? sender, EventArgs e)
    {
        if (_dragAutoScrollDirection == 0)
        {
            StopDragAutoScroll();
            return;
        }

        if (DateTime.UtcNow < _dragWheelCooldownUntilUtc) return;

        if (_dragPointerOnScreen is not { } pointerOnScreen)
        {
            StopDragAutoScroll();
            return;
        }

        UpdateDragAutoScroll();
        if (_dragAutoScrollViewer is null) return;
        var offsetDelta = _dragAutoScrollDirection
            * _dragAutoScrollStep
            * _dragAutoScrollTimer.Interval.TotalSeconds;
        if (!SmoothScrollBehavior.ScrollBy(_dragAutoScrollViewer, offsetDelta))
        {
            // Compatibility fallback for a parent ScrollViewer that is outside
            // the panel's own smooth-scroll scope. The normal Mod-list path
            // always takes the branch above.
            _dragAutoScrollViewer.ScrollToVerticalOffset(Math.Clamp(
                _dragAutoScrollViewer.VerticalOffset + offsetDelta,
                0,
                _dragAutoScrollViewer.ScrollableHeight));
        }
        QueueDropInsertionIndicatorRefresh();
    }

    private void StopDragAutoScroll()
    {
        var viewer = _dragAutoScrollViewer;
        _dragAutoScrollDirection = 0;
        _dragAutoScrollStep = 0;
        _dragAutoScrollViewer = null;
        _dragPointerOnScreen = null;
        _dragWheelCooldownUntilUtc = default;
        _dragAutoScrollTimer.Stop();
        SmoothScrollBehavior.Stop(viewer);
    }

    private bool TryResolveDragAutoScroll(out ScrollViewer? scrollViewer, out int direction, out double step)
    {
        const double edgeSize = 32;
        if (_dragPointerOnScreen is not { } pointerOnScreen)
        {
            scrollViewer = null;
            direction = 0;
            step = 0;
            return false;
        }

        foreach (var candidate in GetDragScrollViewers())
        {
            if (candidate.ActualHeight <= 0 || candidate.ScrollableHeight <= 0) continue;
            var pointer = candidate.PointFromScreen(pointerOnScreen);
            if (pointer.Y < -edgeSize || pointer.Y > candidate.ActualHeight + edgeSize) continue;
            var topDistance = pointer.Y;
            var bottomDistance = candidate.ActualHeight - pointer.Y;
            direction = topDistance < edgeSize ? -1 : bottomDistance < edgeSize ? 1 : 0;
            if (direction == 0) continue;
            if (direction < 0 && candidate.VerticalOffset <= 0.5) continue;
            if (direction > 0 && candidate.VerticalOffset >= candidate.ScrollableHeight - 0.5) continue;

            var distance = direction < 0 ? topDistance : bottomDistance;
            var pressure = Math.Clamp((edgeSize - distance) / edgeSize, 0, 1);
            scrollViewer = candidate;
            // pixels per second; SmoothScrollBehavior turns this velocity into
            // frame-synchronous motion. Keeping the value in physical units
            // makes behavior independent from the timer cadence.
            step = 46 + (620 * pressure * pressure);
            return true;
        }

        scrollViewer = null;
        direction = 0;
        step = 0;
        return false;
    }

    private IEnumerable<ScrollViewer> GetDragScrollViewers()
    {
        var yielded = new HashSet<ScrollViewer>();
        if (FindDescendant<ScrollViewer>(ItemsList) is { } inner && yielded.Add(inner))
            yield return inner;

        for (DependencyObject? current = ItemsList; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer candidate && yielded.Add(candidate))
                yield return candidate;
        }
    }

    private void OnItemsListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isInternalDragActive || e.Delta == 0) return;

        // Mouse capture during an internal drag can keep wheel input from
        // reaching the nested ScrollViewer consistently. Route it explicitly
        // through the same smooth state used by normal wheel scrolling.
        var viewer = FindDescendant<ScrollViewer>(ItemsList);
        if (SmoothScrollBehavior.ScrollBy(viewer, -(e.Delta / 120d) * 84d))
        {
            _dragWheelCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(140);
            QueueDropInsertionIndicatorRefresh();
            e.Handled = true;
            return;
        }

        _dragWheelCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(140);
    }

    private void QueueDropInsertionIndicatorRefresh()
    {
        if (_dropIndicatorRefreshQueued || !_isInternalDragActive) return;
        _dropIndicatorRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _dropIndicatorRefreshQueued = false;
            if (!_isInternalDragActive || _dragPointerOnScreen is not { } pointerOnScreen) return;
            var position = ItemsList.PointFromScreen(pointerOnScreen);
            if (IsPointInsideItemsList(position)) UpdateDropInsertionIndicator(position);
        }, DispatcherPriority.Render);
    }

	private static void OnUseInternalScrollChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
	{
		if (target is ModListPanel panel && panel.IsLoaded)
			panel.ConfigureInternalSmoothScroll((bool)args.NewValue);
	}

	private void ConfigureInternalSmoothScroll(bool enabled)
	{
		if (FindDescendant<ScrollViewer>(ItemsList) is { } viewer)
			SmoothScrollBehavior.SetIsEnabled(viewer, enabled);
	}

    private static void OnItemsSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ModListPanel panel && panel.IsLoaded)
            panel._transitionController.Attach(args.NewValue as IEnumerable);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        for (var node = current; node is not null; node = VisualTreeWalk.Parent(node))
        {
            if (node is T ancestor) return ancestor;
        }
        return null;
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

    private void OnSelectionIndicatorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border indicator || _selectionIndicatorSubscriptions.ContainsKey(indicator)) return;
        var subscription = new SelectionIndicatorSubscription();
        _selectionIndicatorSubscriptions.Add(indicator, subscription);
        indicator.DataContextChanged += OnSelectionIndicatorDataContextChanged;
        AttachSelectionIndicator(indicator, subscription, indicator.DataContext);
    }

    private void OnSelectionIndicatorUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border indicator || !_selectionIndicatorSubscriptions.Remove(indicator, out var subscription)) return;
        indicator.DataContextChanged -= OnSelectionIndicatorDataContextChanged;
        DetachSelectionIndicator(subscription);
    }

    private void OnSelectionIndicatorDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border indicator || !_selectionIndicatorSubscriptions.TryGetValue(indicator, out var subscription)) return;
        DetachSelectionIndicator(subscription);
        AttachSelectionIndicator(indicator, subscription, e.NewValue);
    }

    private void AttachSelectionIndicator(Border indicator, SelectionIndicatorSubscription subscription, object? sourceObject)
    {
        subscription.Source = sourceObject as INotifyPropertyChanged;
        subscription.IsSelected = ReadIsSelected(sourceObject);
        SetSelectionIndicator(indicator, subscription.IsSelected, animate: false);
        if (subscription.Source is null) return;

        subscription.Handler = (_, args) =>
        {
            if (!string.Equals(args.PropertyName, "IsSelected", StringComparison.Ordinal)) return;
            var selected = ReadIsSelected(sourceObject);
            if (selected == subscription.IsSelected) return;
            subscription.IsSelected = selected;
            if (Dispatcher.CheckAccess())
                SetSelectionIndicator(indicator, selected, animate: true);
            else
                _ = Dispatcher.BeginInvoke(() => SetSelectionIndicator(indicator, selected, animate: true), DispatcherPriority.Input);
        };
        subscription.Source.PropertyChanged += subscription.Handler;
    }

    private static void DetachSelectionIndicator(SelectionIndicatorSubscription subscription)
    {
        if (subscription.Source is not null && subscription.Handler is not null) subscription.Source.PropertyChanged -= subscription.Handler;
        subscription.Source = null;
        subscription.Handler = null;
    }

    private static bool ReadIsSelected(object? source)
        => source is IModListSelectable selectable && selectable.IsSelected;

    private static void SetSelectionIndicator(Border indicator, bool selected, bool animate)
    {
        var scale = indicator.RenderTransform as ScaleTransform;
        if (scale is null || scale.IsFrozen)
        {
            scale = new ScaleTransform();
            indicator.RenderTransform = scale;
        }

        indicator.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate)
        {
            indicator.Opacity = selected ? 1 : 0;
            scale.ScaleY = selected ? 1 : 0;
            return;
        }

        indicator.Opacity = selected ? 0 : 1;
        scale.ScaleY = selected ? 0 : 1;
        indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(selected ? 1 : 0, TimeSpan.FromMilliseconds(selected ? 60 : 80))
        {
            BeginTime = selected ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40),
            EasingFunction = new CubicEase { EasingMode = selected ? EasingMode.EaseOut : EasingMode.EaseIn }
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(selected ? 1 : 0, TimeSpan.FromMilliseconds(selected ? 200 : 120))
        {
            EasingFunction = new CubicEase { EasingMode = selected ? EasingMode.EaseOut : EasingMode.EaseIn }
        });
    }

    private static void OnRowActionsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        => target.SetValue(HasRowActionsPropertyKey, (ModListRowAction)args.NewValue != ModListRowAction.None);

    private static void OnSearchActionsTemplateChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        => target.SetValue(HasSearchActionsPropertyKey, args.NewValue is DataTemplate);

    private sealed class SelectionIndicatorSubscription
    {
        public INotifyPropertyChanged? Source { get; set; }
        public PropertyChangedEventHandler? Handler { get; set; }
        public bool IsSelected { get; set; }
    }

    private sealed record DragCandidate(string PressedKey, IReadOnlyList<string> SelectedKeys);

    private sealed record InternalDragPayload(IReadOnlyList<string> SelectedKeys);

    private sealed record RealizedRow(int Index, FrameworkElement Container);

}

public sealed class ModListRowEventArgs(object? item, ModifierKeys modifiers) : EventArgs
{
    public object? Item { get; } = item;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public sealed class ModListRowActionEventArgs(object item, ModListRowAction action) : EventArgs
{
    public object Item { get; } = item;
    public ModListRowAction Action { get; } = action;
}

public enum ModListKeyboardCommand
{
    Rename,
    OpenDetails,
    Delete,
    DeleteFromLibrary,
}

// The shared panel recognizes physical keys only.  The host page decides what
// an intent means for its own domain (for example Delete is profile removal in
// the configuration page, but library deletion in the library page).
public sealed class ModListKeyboardCommandEventArgs(
    ModListKeyboardCommand command,
    IReadOnlyList<object> items,
    IReadOnlyList<string> selectedKeys) : EventArgs
{
    public ModListKeyboardCommand Command { get; } = command;
    public IReadOnlyList<object> Items { get; } = items;
    public IReadOnlyList<string> SelectedKeys { get; } = selectedKeys;
    public object? Item => Items.Count == 1 ? Items[0] : null;
}

public sealed class ModListTransitionStateChangedEventArgs(bool isActive) : EventArgs
{
    public bool IsActive { get; } = isActive;
}

public sealed class ModListInternalReorderEventArgs(IReadOnlyList<string> draggedKeys, int insertionIndex) : EventArgs
{
    public IReadOnlyList<string> DraggedKeys { get; } = draggedKeys;
    public int InsertionIndex { get; } = insertionIndex;
}

public sealed class ModListExternalProfileDropEventArgs(IReadOnlyList<string> selectedKeys) : EventArgs
{
    public IReadOnlyList<string> SelectedKeys { get; } = selectedKeys;
}

// 关系拖放的放下事件。宿主拿到的就是协调器构造的 payload，
// 不需要再从控件状态里反推"刚才拖的是什么"。
public sealed class ModListRelationshipDropEventArgs(ModRelationshipDragPayload payload) : EventArgs
{
    public ModRelationshipDragOperation Operation { get; } = payload.Operation;
    public IReadOnlyList<string> SelectedKeys { get; } = payload.SelectedKeys;
    public string? SourceHostId { get; } = payload.SourceHostId;
    public ModRelationshipDragPayload Payload { get; } = payload;
}

public sealed class ModListRowToneBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var resourceKey = value is ModListRowTone tone
            ? tone switch
            {
                ModListRowTone.Accent => "Accent",
                ModListRowTone.Success => "StatusSuccess",
                ModListRowTone.Warning => "StatusWarning",
                ModListRowTone.Danger => "StatusDanger",
                _ => "TextSecondary",
            }
            : "TextSecondary";
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.DimGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}