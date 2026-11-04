using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.Controls.Trees;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

/// <summary>
/// 扁平化树控件：**只认行，不认层级**。
///
/// 它和 ModListPanel 是同一个思路的两种形态——调用方给数据，控件给几何、交互和动画：
/// <list type="bullet">
/// <item>调用方给 <see cref="Roots"/>（实现 <see cref="ITreeSurfaceNode"/> 的节点树）和
/// <see cref="ItemTemplate"/>（行外观）；</item>
/// <item>控件负责压平、缩进、竖线、行高、点击展开、以及展开/收起的"扑克牌"过渡。</item>
/// </list>
///
/// 动画直接复用 <see cref="ModListTransitionController"/>：把压平后的行序列当成普通列表喂进去，
/// 它自己会算出"哪些是新增、哪些只是位置变了"，然后逐行做 FLIP。树只需要额外告诉它一件事——
/// 新增的行从**父行**出发（<see cref="ModListTransitionController.InsertHints"/>），
/// 以及收起时滑回父行（<see cref="ModListTransitionController.RemoveHints"/>）。
/// </summary>
public partial class TreeSurface : UserControl
{
    // 与 ModList 的"行让位"同一个时长，位移和淡入同节拍——边滑边显形，
    // 而不是先凭空出现再继续滑。
    private const double FanMoveMilliseconds = 200d;
    private const double FanFadeMilliseconds = 200d;

    private readonly BulkObservableCollection<object> _rows = new();
    private readonly BulkObservableCollection<object> _presentedItems = new();
    private readonly ModListTransitionController _transitions;

    private IReadOnlyDictionary<object, ITreeSurfaceNode> _parentOf = TreeRowProjection.Result.Empty.ParentOf;
    private IReadOnlyDictionary<object, ITreeSurfaceNode> _parentOfBefore = TreeRowProjection.Result.Empty.ParentOf;
    private ITreeSurfaceNode? _expandingNode;
    private ITreeSurfaceNode? _selectedNode;

    public TreeSurface()
    {
        InitializeComponent();
        // 默认就用"简单树"那一套外观；调用方给了 ItemTemplate 就覆盖它。
        if (ItemTemplate is null)
            ItemTemplate = (DataTemplate)FindResource("TreeSurface.DefaultRowTemplate");
        _transitions = new ModListTransitionController(this, RowsHost, TransitionOverlay, _presentedItems)
        {
            InsertHints = GetInsertHints,
            RemoveHints = GetRemoveHints,
        };
        _transitions.TransitionStateChanged += OnTransitionStateChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty RootsProperty = DependencyProperty.Register(
        nameof(Roots), typeof(IEnumerable), typeof(TreeSurface), new PropertyMetadata(null, OnRootsChanged));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(TreeSurface), new PropertyMetadata(null));

    public static readonly DependencyProperty VerticalScrollBarVisibilityProperty = DependencyProperty.Register(
        nameof(VerticalScrollBarVisibility), typeof(ScrollBarVisibility), typeof(TreeSurface),
        new PropertyMetadata(ScrollBarVisibility.Auto));

    /// <summary>节点树的根集合。元素需实现 <see cref="ITreeSurfaceNode"/>。</summary>
    public IEnumerable? Roots
    {
        get => (IEnumerable?)GetValue(RootsProperty);
        set => SetValue(RootsProperty, value);
    }

    /// <summary>行外观。行高由控件钉死，模板只管横向内容。</summary>
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => (ScrollBarVisibility)GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    /// <summary>当前交给列表渲染的行序列（压平之后）。</summary>
    public IEnumerable PresentedItems => _presentedItems;

    /// <summary>当前选中行。旧树靠 TreeViewItem 自带选中，扁平列表里只能自己记。</summary>
    public ITreeSurfaceNode? SelectedNode => _selectedNode;

    /// <summary>
    /// 选中行变了。<b>只报"变了"，不报"没变"</b>——重复报告会让订阅方做无用的刷新。
    /// <para>
    /// 控件不知道选中行要拿来干什么（展开、跳转、还是"把这一块建成组"），
    /// 所以它只把事实说出来，语义留给调用方。
    /// </para>
    /// </summary>
    public event EventHandler<ITreeSurfaceNode?>? SelectionChanged;

    /// <summary>单选：换行时把上一行的标记清掉，保证同一时刻只有一行亮着。</summary>
    public void Select(ITreeSurfaceNode? node)
    {
        if (ReferenceEquals(_selectedNode, node)) return;
        if (_selectedNode is not null) _selectedNode.IsSelected = false;
        _selectedNode = node;
        if (node is not null) node.IsSelected = true;
        SelectionChanged?.Invoke(this, node);
    }

    private static bool ContainsRow(IReadOnlyList<ITreeSurfaceNode> rows, ITreeSurfaceNode node)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], node)) return true;
        }

        return false;
    }

    /// <summary>展开/收起一个节点。用户点击走的就是这条路。</summary>
    public void Toggle(ITreeSurfaceNode node)
    {
        if (node.Children.Count == 0) return;
        node.IsExpanded = !node.IsExpanded;
        Rebuild(node.IsExpanded ? node : null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e) => _transitions.Detach();

    private static void OnRootsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TreeSurface surface && surface.IsLoaded) surface.Attach();
    }

    private void Attach()
    {
        Rebuild();
        _transitions.Attach(_rows);
    }

    private void Rebuild(ITreeSurfaceNode? expanding = null)
    {
        _expandingNode = expanding;
        var projection = TreeRowProjection.Flatten(Roots);
        // 收起时被删掉的行已经不在新投影里了，它的父级要从上一份表里找。
        _parentOfBefore = _parentOf;
        _parentOf = projection.ParentOf;
        _rows.ReplaceWith(projection.Rows);

        // 选中的行被收起藏起来了就一起清掉：看不见的行不该还带着高亮。
        if (_selectedNode is not null && !ContainsRow(projection.Rows, _selectedNode)) Select(null);
    }

    private void OnRowsMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 展开动画期间锁输入：200ms 内不接第二次点击，避开"从半路再展开"的协调问题。
        if (_transitions.IsPlaying) return;
        if (e.OriginalSource is not DependencyObject source) return;
        // 开关、按钮上的点击不是"展开"。
        if (FindAncestor<ButtonBase>(source) is not null) return;
        if (FindRowNode(source) is not { } node) return;
        // 选中对每一行都成立（叶子也能选），展开只对有子节点的行。
        Select(node);
        // 点一下就交给键盘：否则鼠标选完再按方向键，焦点还在别处。
        Focus();
        if (node.Children.Count > 0) Toggle(node);
        e.Handled = true;
    }

    /// <summary>
    /// 键盘导航。旧的 TreeView 白送这一套，扁平列表得自己写。
    /// 方向语义与文件管理器一致：上下走行，右键展开或进入第一个孩子，左键收起或回到父级。
    /// 展开/收起仍走 <see cref="Toggle"/>，所以键盘触发的也是同一套扑克牌过渡。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_presentedItems.Count == 0) return;

        var current = _selectedNode;
        var index = IndexOfPresented(current);
        switch (e.Key)
        {
            case Key.Down:
                MoveTo(index < 0 ? 0 : index + 1);
                break;
            case Key.Up:
                MoveTo(index < 0 ? _presentedItems.Count - 1 : index - 1);
                break;
            case Key.Home:
                MoveTo(0);
                break;
            case Key.End:
                MoveTo(_presentedItems.Count - 1);
                break;
            case Key.Right:
                if (current is null) { MoveTo(0); break; }
                if (current.HasChildren && !current.IsExpanded) Toggle(current);
                else if (current.HasChildren) MoveTo(index + 1);
                break;
            case Key.Left:
                if (current is null) { MoveTo(0); break; }
                if (current.HasChildren && current.IsExpanded) Toggle(current);
                else if (_parentOf.TryGetValue(current, out var parent)) MoveTo(IndexOfPresented(parent));
                break;
            case Key.Enter:
            case Key.Space:
                if (current is { HasChildren: true }) Toggle(current);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnTreeGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 只在焦点落到控件本身时补一个默认选中；落到行内的开关上不算。
        if (!ReferenceEquals(e.NewFocus, this)) return;
        if (_selectedNode is null) MoveTo(0);
    }

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _presentedItems.Count) return;
        if (_presentedItems[index] is not ITreeSurfaceNode node) return;
        Select(node);
        EnsureVisible(index);
    }

    /// <summary>
    /// 把第 index 行滚进视口。固定行高在这里兑现第二次：位置就是"序号 × 行高"，
    /// 不用去问容器要位置，也就不必等布局。
    /// </summary>
    private void EnsureVisible(int index)
    {
        var top = index * TreeMetrics.RowHeight;
        var bottom = top + TreeMetrics.RowHeight;
        var viewTop = TreeScrollViewer.VerticalOffset;
        var viewBottom = viewTop + TreeScrollViewer.ViewportHeight;
        if (top < viewTop) TreeScrollViewer.ScrollToVerticalOffset(top);
        else if (bottom > viewBottom) TreeScrollViewer.ScrollToVerticalOffset(bottom - TreeScrollViewer.ViewportHeight);
    }

    private int IndexOfPresented(ITreeSurfaceNode? node)
    {
        if (node is null) return -1;
        for (var index = 0; index < _presentedItems.Count; index++)
        {
            if (ReferenceEquals(_presentedItems[index], node)) return index;
        }

        return -1;
    }

    private void OnTransitionStateChanged(bool playing)
    {
        RowsHost.IsHitTestVisible = !playing;
        if (!playing) _expandingNode = null;
    }

    /// <summary>入场：这一行从刚展开的父行出发。</summary>
    private ModListTransitionController.RowTransitionHints? GetInsertHints(object item, FrameworkElement container)
    {
        if (_expandingNode is null || item is not ITreeSurfaceNode node) return null;
        if (!_parentOf.TryGetValue(node, out var parent) || !ReferenceEquals(parent, _expandingNode)) return null;
        if (RowsHost.ItemContainerGenerator.ContainerFromItem(parent) is not FrameworkElement anchor) return null;

        var own = container.TransformToAncestor(RowsHost).Transform(default).Y;
        var from = anchor.TransformToAncestor(RowsHost).Transform(default).Y;
        return new ModListTransitionController.RowTransitionHints(from - own, FanMoveMilliseconds, FanFadeMilliseconds);
    }

    /// <summary>出场：收起时这一行滑回父行所在的位置。</summary>
    private ModListTransitionController.RowTransitionHints? GetRemoveHints(object item, double currentY)
    {
        if (item is not ITreeSurfaceNode node) return null;
        if (!_parentOfBefore.TryGetValue(node, out var parent)) return null;
        if (RowsHost.ItemContainerGenerator.ContainerFromItem(parent) is not FrameworkElement anchor) return null;

        var target = anchor.TransformToAncestor(RowsHost).Transform(default).Y;
        return new ModListTransitionController.RowTransitionHints(target - currentY, FanMoveMilliseconds, FanFadeMilliseconds);
    }

    private static ITreeSurfaceNode? FindRowNode(DependencyObject source)
    {
        for (var current = source; current is not null; current = AncestorOf(current))
        {
            if (current is ContentPresenter { DataContext: ITreeSurfaceNode node }) return node;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = AncestorOf(current))
        {
            if (current is T match) return match;
        }

        return null;
    }

    /// <summary>
    /// 父级走可视树；走到可视树之外（模板里生成的内容）时退回逻辑树，
    /// 否则向上找会在中途断掉。
    /// </summary>
    private static DependencyObject? AncestorOf(DependencyObject current)
        => current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
}
