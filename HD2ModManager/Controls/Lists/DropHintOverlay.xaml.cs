using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace HD2ModManager.Controls.Lists;

// 拖放叠层的视图部分。宿主只需要说"现在该显示什么话"和"收起"，
// 淡入淡出参数与命中判定不允许各页各写一套。
public partial class DropHintOverlay : UserControl
{
    private const double ShownOpacity = 0.92;
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(object),
        typeof(DropHintOverlay),
        new PropertyMetadata(null));

    public DropHintOverlay()
    {
        InitializeComponent();
    }

    public object? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public void Show(ModListDropHint hint)
    {
        Hint = hint;
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(OpacityProperty, new DoubleAnimation(ShownOpacity, FadeIn));
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible)
        {
            Opacity = 0;
            return;
        }

        BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation(0, FadeOut);
        fadeOut.Completed += (_, _) =>
        {
            if (Opacity <= 0.01) Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    // 用于卸载/重置：立刻消失，不留半透明残影。
    public void HideImmediately()
    {
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Collapsed;
        Opacity = 0;
    }

    // 命中判定统一用叠层自己的矩形：视觉盖到哪里，命中就是哪里。
    public bool ContainsPoint(Point screenPoint)
    {
        if (Visibility != Visibility.Visible || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            var local = PointFromScreen(screenPoint);
            return local.X >= 0
                && local.Y >= 0
                && local.X <= ActualWidth
                && local.Y <= ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
