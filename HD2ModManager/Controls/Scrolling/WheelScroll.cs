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
// ComboBox 是特例：它的滚动区在 Popup 里，是独立视觉树。下拉展开期间由它"认领"滚轮，
// SmoothScrollBehavior 看到认领就让位，随后本行为把滚轮转给弹层里的 ScrollViewer。
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
            combo.DropDownOpened -= subscription.Opened;
            combo.DropDownClosed -= subscription.Closed;
            combo.RemoveHandler(UIElement.PreviewMouseWheelEvent, subscription.Wheel);
            ComboSubscriptions.Remove(combo);
            combo.ClearValue(ClaimsWheelProperty);
        }
    }

    private static void AttachCombo(ComboBox combo)
    {
        if (ComboSubscriptions.TryGetValue(combo, out _)) return;

        var subscription = new ComboSubscription
        {
            Opened = (_, _) => combo.SetValue(ClaimsWheelProperty, true),
            Closed = (_, _) => combo.SetValue(ClaimsWheelProperty, false),
            Wheel = (_, e) => RouteWheelToPopup(combo, e)
        };

        combo.DropDownOpened += subscription.Opened;
        combo.DropDownClosed += subscription.Closed;
        combo.AddHandler(UIElement.PreviewMouseWheelEvent, subscription.Wheel, handledEventsToo: false);
        ComboSubscriptions.Add(combo, subscription);
    }

    // 指针停在下拉框本体上时，把滚轮转给弹层里的列表。
    private static void RouteWheelToPopup(ComboBox combo, MouseWheelEventArgs e)
    {
        if (!combo.IsDropDownOpen || e.Handled || e.Delta == 0) return;
        if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: DependencyObject child }) return;
        if (FindDescendant<ScrollViewer>(child) is not { } viewer) return;

        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - Math.Sign(e.Delta) * 48d);
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

    private sealed class ComboSubscription
    {
        public required EventHandler Opened { get; init; }
        public required EventHandler Closed { get; init; }
        public required MouseWheelEventHandler Wheel { get; init; }
    }
}
