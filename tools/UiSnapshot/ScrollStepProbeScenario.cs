using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.Controls.Scrolling;

namespace UiSnapshot;

// 滚轮步长探针：回答"一格滚轮到底走了多远"，用真实事件 + 真实控件测，
// 不靠读代码推断。
//
// 为什么需要它：滚轮这块连着改错两次——
//   第一次 下拉一格跳 48 *项*（单位错配）
//   第二次 把 `CanContentScroll=true` 当成"按项滚动"，而 ModListBox 是
//          `CanContentScroll=true` + `VirtualizingPanel.ScrollUnit=Pixel`，
//          于是卡片列表一格只走 1~3 *像素*，看起来就是"滚不动"。
// 这两个错误都只在真机上才暴露，所以这里把三种配置各搭一份，量出每格的位移：
//   ① ModListBox 那种（CanContentScroll=true + ScrollUnit=Pixel）→ 期望 ~84 像素
//   ② ScrollUnit=Item 的虚拟化列表                          → 期望 1~3 项
//   ③ 普通像素 ScrollViewer                                  → 期望 ~84 像素
internal static class ScrollStepProbeScenario
{
    public static Scenario Build(string name)
    {
        var outputs = new System.Collections.Generic.List<string>();

        return new Scenario
        {
            Name = name,
            Width = 520,
            Height = 640,
            Build = () =>
            {
                var stack = new StackPanel { Margin = new Thickness(8) };
                stack.Children.Add(BuildList("pixel-virtualized", list =>
                {
                    // 与 App.xaml 的 ModListBox 完全一致的两个关键属性
                    ScrollViewer.SetCanContentScroll(list, true);
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
                }));
                stack.Children.Add(BuildList("item-virtualized", list =>
                {
                    ScrollViewer.SetCanContentScroll(list, true);
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Item);
                }));
                var plain = new ScrollViewer
                {
                    Height = 160,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                SmoothScrollBehavior.SetIsEnabled(plain, true);
                var inner = new StackPanel();
                for (var index = 0; index < 30; index++)
                    inner.Children.Add(new Border { Height = 40, Background = Brushes.WhiteSmoke });
                plain.Content = inner;
                plain.Tag = "pixel-scrollviewer";
                stack.Children.Add(new Label { Content = "③ 普通像素 ScrollViewer" });
                stack.Children.Add(plain);
                return new Border { Background = ScenarioColors.White, Child = stack };
            },
            Settle = host =>
            {
                host.PumpFor(120);
                foreach (var root in ((Panel)((Border)host.Content!).Child).Children.OfType<FrameworkElement>())
                {
                    if (root.Tag is not string tag) continue;
                    var viewer = root as ScrollViewer ?? FindDescendant<ScrollViewer>(root);
                    if (viewer is null) continue;
                    SmoothScrollBehavior.SetIsEnabled(viewer, true);

                    var before = viewer.VerticalOffset;
                    var args = RaiseWheel(viewer, -120);
                    host.PumpFor(260);
                    var after = viewer.VerticalOffset;
                    var panel = FindDescendant<VirtualizingPanel>(viewer);
                    var owner = (root as Panel)?.Children.OfType<ItemsControl>().FirstOrDefault();
                    var diag = "panel=" + (panel?.GetType().Name ?? "n/a")
                        + " panelUnit=" + (panel is null ? "n/a" : VirtualizingPanel.GetScrollUnit(panel).ToString())
                        + " ownerUnit=" + (owner is null ? "n/a" : VirtualizingPanel.GetScrollUnit(owner).ToString())
                        + " ownerType=" + (owner?.GetType().Name ?? "n/a");
                    var line = $"[wheel] {tag,-20} delta={after - before,7:F1}  (before {before:F1} after {after:F1}, scrollable {viewer.ScrollableHeight:F1}, handled={args.Handled}, canContentScroll={viewer.CanContentScroll}, {diag})";
                    outputs.Add(line);
                    Console.WriteLine(line);
                }

                Console.WriteLine("[wheel] 期望：pixel-virtualized ≈ 84 像素；item-virtualized = 1~3 项；pixel-scrollviewer ≈ 84 像素");
            }
        };
    }

    private static FrameworkElement BuildList(string name, Action<ListBox> configure)
    {
        var list = new ListBox
        {
            Height = 160,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            ItemContainerStyle = new Style(typeof(ListBoxItem))
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        list.ItemContainerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 60d));
        list.ItemsSource = Enumerable.Range(0, 40).Select(index => "条目 " + index).ToArray();
        configure(list);
        var host = new StackPanel { Tag = name };
        host.Children.Add(new Label { Content = name });
        host.Children.Add(list);
        return host;
    }

    private static MouseWheelEventArgs RaiseWheel(UIElement source, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = source
        };
        source.RaiseEvent(preview);
        if (preview.Handled) return preview;

        var bubble = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source
        };
        source.RaiseEvent(bubble);
        return bubble;
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
}
