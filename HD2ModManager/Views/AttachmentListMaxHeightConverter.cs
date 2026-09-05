using System.Globalization;
using System.Windows.Data;

namespace HD2ModManager.Views;

/// <summary>
/// Keeps the details-page attachment list large enough for normal use while
/// giving WPF a finite viewport so the ModListBox virtualization can work.
/// The value is the total ModListPanel height (header included), not merely
/// the inner ListBox height.
/// </summary>
public sealed class AttachmentListMaxHeightConverter : IValueConverter
{
    private const double FallbackHeight = 520d;
    private const double MinimumHeight = 520d;
    private const double MaximumHeight = 1040d;
    private const double ViewportRatio = 0.80d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var viewportHeight = value is double height && !double.IsNaN(height) && !double.IsInfinity(height)
            ? height
            : 0d;
        if (viewportHeight <= 0d)
            return FallbackHeight;

        return Math.Clamp(viewportHeight * ViewportRatio, MinimumHeight, MaximumHeight);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
