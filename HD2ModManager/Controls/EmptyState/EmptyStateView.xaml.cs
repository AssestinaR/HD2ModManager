using System.Windows;
using System.Windows.Controls;

namespace HD2ModManager.Controls.EmptyState;

// 空状态视图。State 收一个 EmptyStatePresentation。
// 属性名刻意不叫 Content：UserControl 自己就是 ContentControl，会撞名。
public partial class EmptyStateView : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(object),
        typeof(EmptyStateView),
        new PropertyMetadata(null));

    public EmptyStateView()
    {
        InitializeComponent();
    }

    public object? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
