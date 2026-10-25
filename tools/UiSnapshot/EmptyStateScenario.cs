using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UiSnapshot;

// 空状态对照：同一个卡片里，现状（一行灰字）与建议形态（图标 + 标题 + 解释 + 动作）。
// 只用现有 token，不引入新颜色。
internal static class EmptyStateScenario
{
    public static Scenario Build(string name)
    {
        var rows = new StackPanel();

        rows.Children.Add(Label("现状 · 库为空"));
        rows.Children.Add(Card(CurrentState("模组库中没有可显示的 Mod。")));

        rows.Children.Add(Label("现状 · 搜索无结果（文案与上面完全一样）"));
        rows.Children.Add(Card(CurrentState("模组库中没有可显示的 Mod。")));

        rows.Children.Add(Label("建议 · 库为空"));
        rows.Children.Add(Card(ProposedState(
            "M2,4 L14,4 L14,13 L2,13 Z M2,7 L14,7 M6,4 L6,13",
            "模组库是空的",
            "把 Mod 压缩包或文件夹拖进窗口，或用右下角菜单里的「导入 Mod」。",
            "导入 Mod")));

        rows.Children.Add(Label("建议 · 搜索无结果"));
        rows.Children.Add(Card(ProposedState(
            "M9.5,8.5 L13.5,12.5 M11,6 A5,5 0 1 1 1,6 A5,5 0 1 1 11,6",
            "没有匹配的 Mod",
            "试试更短的关键词，或清除搜索回到全部 Mod。",
            "清除搜索")));

        return new Scenario
        {
            Name = name,
            Width = 640,
            Height = 720,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(16),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = rows
                }
            }
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(2, 0, 0, 8),
        Foreground = ScenarioCatalog.Resource("TextSecondary")
    };

    private static Border Card(UIElement content) => new()
    {
        Background = ScenarioCatalog.Resource("SurfaceCard"),
        BorderBrush = ScenarioCatalog.Resource("StrokeSubtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 22),
        Child = content
    };

    // 现状：卡片左上角一行灰字，没有图标、没有下一步。
    private static UIElement CurrentState(string message) => new Grid
    {
        Height = 150,
        Children =
        {
            new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left
            }
        }
    };

    private static UIElement ProposedState(string icon, string title, string detail, string action)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360
        };

        stack.Children.Add(new Path
        {
            Data = Geometry.Parse(icon),
            Stroke = ScenarioCatalog.Resource("TextTertiary"),
            StrokeThickness = 1.4,
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ScenarioCatalog.Resource("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = ScenarioCatalog.Resource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 14)
        });

        var button = new Button
        {
            Content = action,
            Style = Application.Current.TryFindResource("AppPrimaryButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(button);

        return new Grid { Height = 190, Children = { stack } };
    }
}
