using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// Shared visual shell. Host pages retain all Mod-specific commands and transactions.
public partial class ModListPanel : UserControl
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

    public ModListPanel()
    {
        InitializeComponent();
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
        _transitionController.OutgoingPresentedItems.CollectionChanged += OnOutgoingPresentedItemsChanged;
        _transitionController.TransitionStateChanged += OnTransitionStateChanged;
        ItemsList.Loaded += (_, _) => ConfigureInternalSmoothScroll(UseInternalScroll);
        _dragAutoScrollTimer.Tick += OnDragAutoScrollTick;
        Loaded += (_, _) =>
        {
            _transitionController.Attach(ItemsSource);
            ConfigureInternalSmoothScroll(UseInternalScroll);
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
        };
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ModListPanel), new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty HeaderTitleProperty = DependencyProperty.Register(nameof(HeaderTitle), typeof(string), typeof(ModListPanel), new PropertyMetadata("模组"));
    public static readonly DependencyProperty HeaderSummaryProperty = DependencyProperty.Register(nameof(HeaderSummary), typeof(string), typeof(ModListPanel), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(nameof(EmptyMessage), typeof(string), typeof(ModListPanel), new PropertyMetadata("没有可显示的 Mod。"));
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
    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(ModListPanel), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
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
    private static readonly DependencyPropertyKey HasOutgoingPresentedItemsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasOutgoingPresentedItems), typeof(bool), typeof(ModListPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty HasOutgoingPresentedItemsProperty = HasOutgoingPresentedItemsPropertyKey.DependencyProperty;
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
    public bool HasRowActions => (bool)GetValue(HasRowActionsProperty);
    public bool HasSearchActions => (bool)GetValue(HasSearchActionsProperty);
    public bool HasPresentedItems => (bool)GetValue(HasPresentedItemsProperty);
    public bool HasOutgoingPresentedItems => (bool)GetValue(HasOutgoingPresentedItemsProperty);

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
    public event EventHandler? BackgroundClicked;

    private void OnPresentedItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SetValue(HasPresentedItemsPropertyKey, _transitionController.PresentedItems.Count != 0);

    private void OnOutgoingPresentedItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SetValue(HasOutgoingPresentedItemsPropertyKey, _transitionController.OutgoingPresentedItems.Count != 0);

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

    private void ShowSearchAndFocus()
    {
        if (HeaderSearchBox.Visibility == Visibility.Visible)
        {
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
        HeaderSearchBox.Focus();
    }

    private void HideSearch()
    {
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
        if (sender is not FrameworkElement { DataContext: { } item, Tag: ModListRowAction action }) return;
        RowActionInvoked?.Invoke(this, new ModListRowActionEventArgs(item, action));
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
            ExternalProfileDropOverlay.Visibility = Visibility.Visible;
            ExternalProfileDropOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(140)));
            return;
        }

        if (ExternalProfileDropOverlay.Visibility != Visibility.Visible) return;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fadeOut.Completed += (_, _) => ExternalProfileDropOverlay.Visibility = Visibility.Collapsed;
        ExternalProfileDropOverlay.BeginAnimation(OpacityProperty, fadeOut);
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

[Flags]
public enum ModListRowAction
{
    None = 0,
    Rename = 1 << 0,
    Details = 1 << 1,
    AddToProfile = 1 << 2,
    OpenFolder = 1 << 3,
    MoveUp = 1 << 4,
    MoveDown = 1 << 5,
    RemoveFromProfile = 1 << 6,
    DeleteFromLibrary = 1 << 7,
    ToggleDecoration = 1 << 8,
    ArmorAssign = 1 << 9,
    ArmorRelease = 1 << 10,
    ArmorOpenDetails = 1 << 11,
    ArmorEditComponents = 1 << 12,
    ArmorDeleteCustomModel = 1 << 13,
    ArmorRenameCustomModel = 1 << 14,
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

public sealed class RowActionVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is ModListRowAction actions && parameter is ModListRowAction action && actions.HasFlag(action)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DecorationRowActionVisibilityConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not ModListRowAction available || parameter is not ModListRowAction requested || !available.HasFlag(requested))
            return Visibility.Collapsed;
        var isAllowed = values[1] is true;
        return requested switch
        {
            ModListRowAction.AddToProfile when !isAllowed => Visibility.Collapsed,
            ModListRowAction.ToggleDecoration when !isAllowed => Visibility.Collapsed,
            _ => Visibility.Visible
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CustomArmorRowActionVisibilityConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not ModListRowAction actions
            || parameter is not ModListRowAction requested)
            return Visibility.Collapsed;
        var isCustom = values[1] is true;
        if (!actions.HasFlag(requested)) return Visibility.Collapsed;
        if (requested == ModListRowAction.ArmorRelease && isCustom)
            return Visibility.Collapsed;
        if ((requested == ModListRowAction.ArmorDeleteCustomModel
                || requested == ModListRowAction.ArmorRenameCustomModel) && !isCustom)
            return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
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
