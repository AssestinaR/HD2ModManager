using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HD2ModManager.Controls.Nav;
using HD2ModManager.Views;

namespace UiSnapshot;

internal static class NavScenario
{
    public static Scenario Build(string name, double width)
    {
        var stub = new NavStub();

        var capsule = new CapsuleButtonGroup
        {
            Width = 168,
            Height = 40,
            Margin = new Thickness(10, 0, 10, 0),
            MiddleContent = "配置页  |  模组库",
            LeftToolTip = "只打开配置页",
            MiddleToolTip = "合并为左右双页",
            RightToolTip = "只打开模组库"
        };
        Bind(capsule, CapsuleButtonGroup.LeftCommandProperty, nameof(NavStub.ShowProfileCommand), stub);
        Bind(capsule, CapsuleButtonGroup.MiddleCommandProperty, nameof(NavStub.ShowSplitCommand), stub);
        Bind(capsule, CapsuleButtonGroup.RightCommandProperty, nameof(NavStub.ShowLibraryCommand), stub);
        Bind(capsule, CapsuleButtonGroup.MiddleIsActiveProperty, nameof(NavStub.IsSplitActive), stub);

        var host = new NavBarHost
        {
            VerticalAlignment = VerticalAlignment.Center,
            LeadingItems = new[]
            {
                new NavItemDescriptor("home", "首页", NavIconKey.Home, stub.ShowHomeCommand)
            },
            TrailingItems = new[]
            {
                new NavItemDescriptor("armor", "护甲映射", NavIconKey.ArmorRegistry, stub.ShowArmorCommand),
                new NavItemDescriptor("settings", "设置", NavIconKey.Settings, stub.ShowSettingsCommand) { IsActive = true }
            },
            CenterContent = capsule
        };

        var utility = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            Child = new TextBlock
            {
                Text = "消息",
                FontSize = 13,
                Foreground = ScenarioCatalog.Resource("TextPrimary")
            }
        };

        var bar = new Grid { Height = 48 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(host);
        Grid.SetColumn(utility, 1);
        bar.Children.Add(utility);

        return new Scenario
        {
            Name = name,
            Width = width,
            Height = 64,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCard"),
                Padding = new Thickness(8, 0, 0, 0),
                Child = bar
            }
        };
    }

    // 切换激活项并截取过渡中间帧：验证指示块从当前位置续播 + 标签交叉淡入。
    public static Scenario BuildTransition(string name)
    {
        var scenario = Build(name, 1200);
        return new Scenario
        {
            Name = scenario.Name,
            Width = scenario.Width,
            Height = scenario.Height,
            Build = scenario.Build,
            Settle = renderHost =>
            {
                var host = FindNavHost(renderHost.Content);
                if (host is null) return;

                var items = new List<NavItemDescriptor>();
                foreach (var entry in host.LeadingItems ?? Array.Empty<object>()) if (entry is NavItemDescriptor d) items.Add(d);
                foreach (var entry in host.TrailingItems ?? Array.Empty<object>()) if (entry is NavItemDescriptor d) items.Add(d);

                var settings = items.FirstOrDefault(i => i.Id == "settings");
                var armor = items.FirstOrDefault(i => i.Id == "armor");
                var indicator = FindBorder(renderHost.Content, "ActiveIndicator");
                if (settings is null || armor is null || indicator is null) return;

                var startLeft = Canvas.GetLeft(indicator);
                settings.IsActive = false;
                armor.IsActive = true;

                // 离屏环境的动画时钟跳得比墙钟快，按位置采样常常一帧就过头；
                // 改成盯"到达项的白色层透明度"，只要它落在半程就说明交叉淡入真的在跑。
                var sampled = -1d;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    renderHost.PumpFor(2);
                    var opacity = FindLayerOpacity(renderHost.Content, "armor", "ActiveLayer");
                    if (opacity is { } value && value > 0.25 && value < 0.55)
                    {
                        sampled = value;
                        break;
                    }
                }

                Console.WriteLine("[nav-transition] armor ActiveLayer opacity=" + sampled.ToString("F2") +
                                  " indicatorLeft=" + Canvas.GetLeft(indicator).ToString("F1") +
                                  " start=" + startLeft.ToString("F1"));
            }
        };
    }

    private static NavBarHost? FindNavHost(DependencyObject root)
    {
        if (root is NavBarHost host) return host;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindNavHost(System.Windows.Media.VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        }
        return null;
    }

    private static Border? FindBorder(DependencyObject root, string name)
    {
        if (root is Border border && border.Name == name) return border;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindBorder(System.Windows.Media.VisualTreeHelper.GetChild(root, index), name) is { } found) return found;
        }
        return null;
    }

    private static double? FindLayerOpacity(DependencyObject root, string itemId, string layerName)
    {
        if (root is FrameworkElement element
            && element.Name == layerName
            && element.DataContext is NavItemDescriptor descriptor
            && descriptor.Id == itemId)
        {
            return element.Opacity;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindLayerOpacity(System.Windows.Media.VisualTreeHelper.GetChild(root, index), itemId, layerName) is { } found)
                return found;
        }
        return null;
    }

    private static void Bind(FrameworkElement target, DependencyProperty property, string path, object source)
        => target.SetBinding(property, new Binding(path) { Source = source });
}

public sealed class NavStub
{
    public ICommand ShowHomeCommand { get; } = new AlwaysCommand();
    public ICommand ShowArmorCommand { get; } = new AlwaysCommand();
    public ICommand ShowSettingsCommand { get; } = new AlwaysCommand();
    public ICommand ShowProfileCommand { get; } = new AlwaysCommand();
    public ICommand ShowSplitCommand { get; } = new AlwaysCommand();
    public ICommand ShowLibraryCommand { get; } = new AlwaysCommand();
    public bool IsSplitActive { get; set; }
}
