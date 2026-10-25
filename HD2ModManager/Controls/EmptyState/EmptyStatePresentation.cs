using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HD2ModManager.Controls.EmptyState;

// 空状态的内容描述。逻辑层只给"标题 / 一句解释 / 可选动作"，
// 图标、间距、字号、按钮样式全部由控件层决定。
public sealed record EmptyStatePresentation(
    string Title,
    string? Detail = null,
    string? ActionText = null,
    System.Windows.Input.ICommand? ActionCommand = null,
    EmptyStateIcon Icon = EmptyStateIcon.Inbox);

public enum EmptyStateIcon
{
    Inbox,
    Search,
    Filter,
}

// EmptyStateIcon → 应用级 Geometry 资源（EmptyIcon.Inbox 等），与顶栏图标同一套做法。
public sealed class EmptyStateIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is EmptyStateIcon icon
            ? Application.Current?.TryFindResource("EmptyIcon." + icon) as Geometry
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// 没有内容就塌陷：空状态的"解释"和"按钮"都是可选的。
public sealed class NullOrEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
