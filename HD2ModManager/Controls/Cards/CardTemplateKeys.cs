using System;
using System.Collections.Generic;
using System.Windows.Data;

namespace HD2ModManager.Controls.Cards;

// CardKind → 资源键的唯一约定。逻辑层不需要知道这个类；
// 控件层与模板字典靠它对齐。
public static class CardTemplateKeys
{
    public const string Prefix = "CardTemplate.";

    public static string For(CardKind kind) => Prefix + kind;

    public static IReadOnlyList<CardKind> All { get; } = Enum.GetValues<CardKind>();
}

// PreferredHeight（可空）→ Height（NaN = Auto）。
// 单独一个 converter，避免在 XAML 里用 TargetNullValue 之类的隐式技巧。
public sealed class CardHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not double height || double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            return double.NaN;
        return height;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// CardOverflow → 滚动条可见性。Grow 不滚动也不裁切，交给外层自然长高。
public sealed class CardOverflowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is CardOverflow.Scroll ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
