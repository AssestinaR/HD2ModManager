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
    }

    private void OnRowsMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 展开动画期间锁输入：200ms 内不接第二次点击，避开"从半路再展开"的协调问题。
        if (_transitions.IsPlaying) return;
        if (e.OriginalSource is not DependencyObject source) return;
        // 开关、按钮上的点击不是"展开"。
        if (FindAncestor<ButtonBase>(source) is not null) return;
        if (FindRowNode(source) is not { } node) return;
        if (node.Children.Count == 0) return;
        Toggle(node);
        e.Handled = true;
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
