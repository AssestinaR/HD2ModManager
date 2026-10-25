using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.Controls.Scrolling;
using HD2ModManager.Views;

namespace UiSnapshot;

// 滚轮路由探针：不靠"看代码推断"，直接在离屏窗口里抛一个真实的
// PreviewMouseWheel 事件，看页面 ScrollViewer 与内层 ScrollViewer 谁动了。
//
// 目的是回答一个具体问题：内层滚动区（TreeView 模板自建的 ScrollViewer）
// 没有被 SmoothScrollBehavior 启用时，光标停在它上面滚动，到底滚的是谁？
internal static class WheelProbeScenario
{
    public static Scenario Build(string name, bool wheelScrollEnabled = false)
    {
        var page = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        SmoothScrollBehavior.SetIsEnabled(page, true);

        var tree = new TreeView { MaxHeight = 200 };
        tree.ItemsSource = Enumerable.Range(0, 40).Select(index => "树节点 " + index).ToArray();

        var stack = new StackPanel();
        stack.Children.Add(new Border { Height = 700, Background = Brushes.Transparent });
        stack.Children.Add(tree);
        stack.Children.Add(new Border { Height = 700, Background = Brushes.Transparent });
        page.Content = stack;

        return new Scenario
        {
            Name = name,
            Width = 520,
            Height = 640,
            Build = () => new Border
            {
                Background = ScenarioColors.White,
                Child = page
            },
            Settle = renderHost =>
            {
                if (wheelScrollEnabled)
                {
                    HD2ModManager.Controls.Scrolling.WheelScroll.SetIsEnabled(tree, true);
                    renderHost.PumpFor(60);
                }

                var treeViewer = FindDescendant<ScrollViewer>(tree);
                Console.WriteLine("[wheel] tree viewer enabled=" + (treeViewer is not null && SmoothScrollBehavior.GetIsEnabled(treeViewer)));
                Console.WriteLine("[wheel] before: page=" + page.VerticalOffset.ToString("F1") +
                                  " tree=" + (treeViewer?.VerticalOffset.ToString("F1") ?? "n/a"));

                var item = tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                if (item is null)
                {
                    Console.WriteLine("[wheel] no tree item container");
                    return;
                }

                // 真实事件路由：源在树内部，Preview 从页面 ScrollViewer 一路隧道下来；
                // 未被处理时，WPF 还会再走一遍冒泡的 MouseWheel，内层 ScrollViewer 靠它滚动。
                var args = RaiseWheel(item, -120);
                renderHost.PumpFor(240);

                Console.WriteLine("[wheel] after : page=" + page.VerticalOffset.ToString("F1") +
                                  " tree=" + (treeViewer?.VerticalOffset.ToString("F1") ?? "n/a") +
                                  " handled=" + args.Handled);

                // 再把页面推到底，重复一次：页面没得滚时，内层是否接手？
                page.ScrollToVerticalOffset(page.ScrollableHeight);
                renderHost.PumpFor(80);
                var beforeTree = treeViewer?.VerticalOffset ?? 0;
                var args2 = RaiseWheel(item, -120);
                renderHost.PumpFor(240);
                Console.WriteLine("[wheel] at page bottom: page=" + page.VerticalOffset.ToString("F1") +
                                  " tree=" + (treeViewer?.VerticalOffset.ToString("F1") ?? "n/a") +
                                  " (was " + beforeTree.ToString("F1") + ") handled=" + args2.Handled);
            }
        };
    }

    // 模拟真实输入：先隧道 PreviewMouseWheel，未被处理再冒泡 MouseWheel。
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

internal static class ScenarioColors
{
    public static readonly Brush White = Brushes.White;
}
