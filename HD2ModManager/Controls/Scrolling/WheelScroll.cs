using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HD2ModManager.Controls.Scrolling;

// 让"自带滚动区的控件"参与滚轮链。
//
// 背景：内层 ScrollViewer 往往由控件模板创建（TreeView / ListBox / DataGrid），
// 页面拿不到它，也就没法逐个启用 SmoothScrollBehavior。这个附加属性挂在**控件**上，
// 由行为自己去找那个 ScrollViewer：
//
//   <TreeView scroll:WheelScroll.IsEnabled="True"/>
//   <ComboBox scroll:WheelScroll.IsEnabled="True"/>
//   <ScrollViewer scroll:WheelScroll.IsEnabled="True"/>   <!-- 自身就是滚动区 -->
//
// 启用之后：光标停在内层上滚动 → 内层先滚；内层到底后 → 页面接手
// （由 SmoothScrollBehavior 的"最近启用者"判定完成，这里只负责把它启用）。
//
// ComboBox 是特例：它的滚动区在 Popup 里，是**独立的可视树、独立的窗口**。
// 这件事带来两个必须分开处理的方向，缺一个就会看到"两个滚动区互相抢"：
//
//   1) 光标在弹层列表上 → 滚轮必须只滚列表，到顶到底也不外泄。
//      不能指望弹层里的 ScrollViewer 自己接住：弹层是分层窗口，WM_MOUSEWHEEL 有时被
//      投递给**主窗口**而不是弹层，事件根本没进弹层那棵树。所以在主窗口上也挂一份，
//      用 viewer.IsMouseOver 判断"指针其实在弹层里"，把滚轮转进去。
//
//   2) 光标在页面别处（含页面滚动条）→ 滚轮必须只滚页面。
//      所以"认领"必须按指针位置算，不能是"下拉展开期间整个页面都让位"。
public static class WheelScroll
{
    private static readonly ConditionalWeakTable<ComboBox, ComboSubscription> ComboSubscriptions = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(WheelScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    // 内部状态：控件当前是否在"认领"滚轮（例如下拉已展开的 ComboBox）。
    private static readonly DependencyProperty ClaimsWheelProperty = DependencyProperty.RegisterAttached(
        "ClaimsWheel",
        typeof(bool),
        typeof(WheelScroll),
        new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    // SmoothScrollBehavior 用这个判断"要不要让位"。
    public static bool IsClaimedByAncestor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is DependencyObject element && (bool)element.GetValue(ClaimsWheelProperty)) return true;
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
        => current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control) return;

        if ((bool)e.NewValue)
        {
            control.Loaded -= OnControlLoaded;
            control.Loaded += OnControlLoaded;
            if (control.IsLoaded) Attach(control);
            return;
        }

        control.Loaded -= OnControlLoaded;
        Detach(control);
    }

    private static void OnControlLoaded(object sender, RoutedEventArgs e) => Attach((Control)sender);

    private static void Attach(Control control)
    {
        // 1) 控件自己就是滚动区
        if (control is ScrollViewer self)
        {
            SmoothScrollBehavior.SetIsEnabled(self, true);
            return;
        }

        // 2) 模板里自带的 ScrollViewer
        control.ApplyTemplate();
        if (FindDescendant<ScrollViewer>(control) is { } viewer)
            SmoothScrollBehavior.SetIsEnabled(viewer, true);

        // 3) 下拉：滚动区在弹层里，展开期间认领滚轮
        if (control is ComboBox combo) AttachCombo(combo);
    }

    private static void Detach(Control control)
    {
        if (control is ScrollViewer self)
        {
            SmoothScrollBehavior.SetIsEnabled(self, false);
            return;
        }

        if (FindDescendant<ScrollViewer>(control) is { } viewer)
            SmoothScrollBehavior.SetIsEnabled(viewer, false);

        if (control is ComboBox combo && ComboSubscriptions.TryGetValue(combo, out var subscription))
        {
            subscription.Detach();
            ComboSubscriptions.Remove(combo);
            combo.ClearValue(ClaimsWheelProperty);
        }
    }

    private static void AttachCombo(ComboBox combo)
    {
        if (ComboSubscriptions.TryGetValue(combo, out _)) return;
        ComboSubscriptions.Add(combo, new ComboSubscription(combo));
    }

    /// <summary>
    /// 光标停在弹层列表上时，滚轮只滚列表；到顶到底也不放行。
    /// 放行的后果就是"滚列表把外层页面滚了"——事件会一路冒泡到页面的滚动区。
    /// </summary>
    private static void OnPopupWheel(object? sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not ScrollViewer viewer) return;
        ScrollPopup(viewer, e);
    }

    private static void ScrollPopup(ScrollViewer viewer, MouseWheelEventArgs e)
    {
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - Math.Sign(e.Delta) * 48d);
        // 无条件吃掉：即使已经在顶/底，也不能让事件继续走——那正是页面被滚的原因。
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // 一个 ComboBox 的滚轮订阅。弹层是在展开那一刻才生成的，所以"找到弹层滚动区、
    // 挂/摘监听"都发生在 Opened / Closed 里，而不是 Attach 里。
    private sealed class ComboSubscription
    {
        private readonly ComboBox _combo;

        public ComboSubscription(ComboBox combo)
        {
            _combo = combo;
            _combo.DropDownOpened += OnOpened;
            _combo.DropDownClosed += OnClosed;
            // 指针停在下拉框本体上时，把滚轮转给弹层里的列表。
            _combo.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnComboWheel), handledEventsToo: false);
        }

        private ScrollViewer? PopupViewer { get; set; }
        private Window? HostWindow { get; set; }

        public void Detach()
        {
            _combo.DropDownOpened -= OnOpened;
            _combo.DropDownClosed -= OnClosed;
            _combo.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnComboWheel));
            DetachPopup();
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            _combo.SetValue(ClaimsWheelProperty, true);
            _combo.ApplyTemplate();
            if (_combo.Template.FindName("PART_Popup", _combo) is not Popup { Child: DependencyObject child }) return;
            if (FindDescendant<ScrollViewer>(child) is not { } viewer) return;

            PopupViewer = viewer;
            viewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPopupWheel), handledEventsToo: false);

            // 弹层是独立窗口：WM_MOUSEWHEEL 可能被投递给主窗口，此时弹层那棵树收不到事件。
            // 挂在主窗口上按"指针是否在弹层里"转投，才能保证"光标在列表上就滚列表"。
            if (Window.GetWindow(_combo) is { } window)
            {
                HostWindow = window;
                window.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWindowWheel), handledEventsToo: true);
            }
        }

        private void OnClosed(object? sender, EventArgs e) => DetachPopup();

        private void DetachPopup()
        {
            _combo.ClearValue(ClaimsWheelProperty);

            if (PopupViewer is { } viewer)
                viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPopupWheel));
            PopupViewer = null;

            if (HostWindow is { } window)
                window.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWindowWheel));
            HostWindow = null;
        }

        private void OnComboWheel(object? sender, MouseWheelEventArgs e)
        {
            if (!_combo.IsDropDownOpen || e.Handled || e.Delta == 0) return;
            // 只认"指针就在这个下拉框上"：以前不看指针，展开期间经过下拉框的滚轮都会被转走。
            if (!_combo.IsMouseOver) return;
            if (PopupViewer is not { } viewer) return;
            ScrollPopup(viewer, e);
        }

        private void OnWindowWheel(object? sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || !_combo.IsDropDownOpen) return;
            if (PopupViewer is not { } viewer) return;
            // 指针不在弹层里（例如停在页面滚动条或页面空白处）→ 什么都不做，让页面正常滚。
            if (!viewer.IsMouseOver) return;
            ScrollPopup(viewer, e);
        }
    }
}
