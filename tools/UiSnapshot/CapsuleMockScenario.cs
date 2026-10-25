using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HD2ModManager.Views;

namespace UiSnapshot;

// 只用于评估的静态对照：同一个 CapsuleButtonGroup，两种"轨道"处理。
// 不参与产品代码，也不改任何生产样式。
internal static class CapsuleMockScenario
{
    public static Scenario Build(string name)
    {
        var stub = new NavStub();

        var current = CreateCapsule(stub, out _);
        var reference = CreateCapsule(stub, out var referenceCapsule);
        referenceCapsule.CapsuleBackground = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        referenceCapsule.CapsuleBorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));
        referenceCapsule.CapsuleBorderThickness = new Thickness(1.5);

        var elevated = new Border
        {
            Child = reference,
            Background = Brushes.Transparent,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x20, 0x28, 0x38),
                BlurRadius = 22,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.16
            }
        };

        var rows = new StackPanel();
        AddRow(rows, "A · 现状（1px 描边、无阴影、纯白轨道）", current);
        AddRow(rows, "B · 参考轨道（1.5px 浅描边 + 柔和投影 + #FAFAFA 轨道）", elevated);

        return new Scenario
        {
            Name = name,
            Width = 720,
            Height = 240,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(24),
                Child = rows
            }
        };
    }

    private static FrameworkElement CreateCapsule(NavStub stub, out CapsuleButtonGroup capsule)
    {
        capsule = new CapsuleButtonGroup
        {
            Width = 168,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MiddleContent = "配置页  |  模组库",
            LeftToolTip = "只打开配置页",
            MiddleToolTip = "合并为左右双页",
            RightToolTip = "只打开模组库",
            LeftIsActive = true
        };
        Bind(capsule, CapsuleButtonGroup.LeftCommandProperty, nameof(NavStub.ShowProfileCommand), stub);
        Bind(capsule, CapsuleButtonGroup.MiddleCommandProperty, nameof(NavStub.ShowSplitCommand), stub);
        Bind(capsule, CapsuleButtonGroup.RightCommandProperty, nameof(NavStub.ShowLibraryCommand), stub);

        // 胶囊自己只把激活区文字改成白色，蓝底一直由外部的高亮块提供；
        // 这个 mock 里把它画出来，否则激活区的白图标会消失在白底上。
        var pill = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = ScenarioCatalog.Resource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 0, 0)
        };

        var stack = new Grid { Width = 168, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(pill);
        stack.Children.Add(capsule);
        return stack;
    }

    private static void AddRow(Panel host, string label, FrameworkElement content)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ScenarioCatalog.Resource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        row.Children.Add(content);
        host.Children.Add(row);
    }

    private static void Bind(FrameworkElement target, DependencyProperty property, string path, object source)
        => target.SetBinding(property, new Binding(path) { Source = source });
}
