using System.Collections;
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
public partial class ModListPanel : UserControl, IRowActionToolTipSource, IModRelationshipDropTarget
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
    public static readonly DependencyProperty ArmorEditComponentsToolTipProperty = DependencyProperty.Register(nameof(ArmorEditComponentsToolTip), typeof(string), typeof(ModListPanel), new PropertyMetadata("编辑视觉组件"));
    public static readonly DependencyProperty AllowKeyboardRenameProperty = DependencyProperty.Register(nameof(AllowKeyboardRename), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
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
    public string ArmorEditComponentsToolTip { get => (string)GetValue(ArmorEditComponentsToolTipProperty); set => SetValue(ArmorEditComponentsToolTipProperty, value); }
    public bool AllowKeyboardRename { get => (bool)GetValue(AllowKeyboardRenameProperty); set => SetValue(AllowKeyboardRenameProperty, value); }
    public DataTemplate? SearchActionsTemplate { get => (DataTemplate?)GetValue(SearchActionsTemplateProperty); set => SetValue(SearchActionsTemplateProperty, value); }

    // 同一动作在不同列表里意思不同（"解除视觉对象" vs "解除映射目标"），
    // 所以这四个文案由宿主覆盖；其余动作直接用 catalog 的默认文案。
    string? IRowActionToolTipSource.GetRowActionToolTip(ModListRowAction action) => action switch
    {
        ModListRowAction.ArmorAssign => ArmorAssignToolTip,
        ModListRowAction.ArmorRelease => ArmorReleaseToolTip,
        ModListRowAction.ArmorOpenDetails => ArmorOpenDetailsToolTip,
        ModListRowAction.ArmorEditComponents => ArmorEditComponentsToolTip,
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
        ItemsList.IsHitTestVisible = !isTransitionActive;
        TransitionStateChanged?.Invoke(this, new ModListTransitionStateChangedEventArgs(isTransitionActive));
    }

    private void OnToggleSearchClick(object sender, RoutedEventArgs e)
    {
        if (HeaderSearchBox.Visibility == Visibility.Visible)
        {
            HideSearch();
            return;
        }

        ShowSearchAndFocus();
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
        if (panel.IsLoaded && !string.IsNullOrWhiteSpace(e.NewValue as string))
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
        if (HeaderSearchBox.Visibility == Visibility.Visible)
        {
            if (!focus) return;
            HeaderSearchBox.Focus();
            HeaderSearchBox.SelectAll();
            return;
        }

        var titleFadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        titleFadeOut.Completed += (_, _) => HeaderTitleElement.Visibility = Visibility.Collapsed;
        HeaderTitleElement.BeginAnimation(OpacityProperty, titleFadeOut);
        var summaryFadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        summaryFadeOut.Completed += (_, _) => HeaderSummaryElement.Visibility = Visibility.Collapsed;
        HeaderSummaryElement.BeginAnimation(OpacityProperty, summaryFadeOut);
            if (HasSearchActions)
            {
                HeaderSearchActions.Visibility = Visibility.Visible;
                HeaderSearchActions.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            }
        HeaderSearchBox.Visibility = Visibility.Visible;
        HeaderSearchBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        if (focus) HeaderSearchBox.Focus();
    }

    private void HideSearch()
    {
        // 收起搜索框必须同时放弃过滤。否则列表还停在"筛过的结果"上，而解释这个
        // 结果的控件已经不见了——用户会以为自己的 Mod 丢了，而且没有任何线索。
        // 这条和 OnSearchTextChanged 里的自动展开合起来维持一个不变量：
        // 查询非空 ⟺ 搜索框可见。
        if (!string.IsNullOrWhiteSpace(SearchText)) SearchText = string.Empty;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (_, _) =>
        {
            HeaderSearchBox.Visibility = Visibility.Collapsed;
            HeaderTitleElement.Visibility = Visibility.Visible;
            HeaderSummaryElement.Visibility = Visibility.Visible;
            HeaderTitleElement.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            HeaderSummaryElement.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        };
        HeaderSearchBox.BeginAnimation(OpacityProperty, fadeOut);
        var actionFadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        actionFadeOut.Completed += (_, _) => HeaderSearchActions.Visibility = Visibility.Collapsed;
        HeaderSearchActions.BeginAnimation(OpacityProperty, actionFadeOut);
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
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectionKey))
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
        var selectableItems = ItemsList.Items.OfType<IModListSelectable>().ToList();
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

    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        // 按钮在行模板里的 ItemsControl 中，所以按钮的 DataContext 是动作项而不是
        // 行对象；行对象必须从容器上取回来。
        if (sender is not FrameworkElement { Tag: ModListRowActionItem actionItem }) return;
        if (FindAncestor<ListBoxItem>(sender as DependencyObject)?.DataContext is not { } rowItem) return;
        RowActionInvoked?.Invoke(this, new ModListRowActionEventArgs(rowItem, actionItem.Action));
        e.Handled = true;
    }

    private void OnListBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
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

    private bool TryRequestSelectionAtPointer(MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null)
            return false;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not IModListSelectable selectable
            || SelectionPolicy is ModListSelectionPolicy.None or ModListSelectionPolicy.ReadOnly)
            return false;

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
        if (_isInternalDragActive) EndInternalDrag();
        if (_isRelationshipDragActive) EndExternalRelationshipDrag(commit: false);
        if (_isExternalProfileDragActive) EndExternalProfileDrag(commit: false);
    }

    private void BeginExternalProfileDrag(DragCandidate candidate)
    {
        if (!ModListExternalProfileDropCoordinator.TryBegin(this, candidate.SelectedKeys)) return;
        _isExternalProfileDragActive = true;
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
        if (!IsLoaded || !AllowExternalProfileDrop || ItemsList.ActualWidth <= 0 || ItemsList.ActualHeight <= 0) return false;
        var local = ItemsList.PointFromScreen(point);
        return local.X >= 0 && local.Y >= 0 && local.X <= ItemsList.ActualWidth && local.Y <= ItemsList.ActualHeight;
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
        while (current is not null)
        {
            if (current is T ancestor) return ancestor;
            current = VisualTreeHelper.GetParent(current);
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
