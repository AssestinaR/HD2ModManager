using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HD2ModManager.Views;

public partial class SelectionActionBarContent : UserControl
{
    public SelectionActionBarContent()
    {
        InitializeComponent();
    }

    // 批量设置分类：对当前选中的所有 Mod 打开分类卡片弹窗（和行内按钮是同一张卡片）。
    private void OnSetCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor
            || DataContext is not SelectionActionBarPresentation { CategoryTargetProvider: { } provider }
            || provider() is not { } target
            || target.Guids.Count == 0) return;

        var popup = new Popup
        {
            PlacementTarget = anchor,
            // 选择栏在窗口底部，卡片向上展开。
            Placement = PlacementMode.Top,
            HorizontalOffset = -14,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.Child = new CategoryPickerCard(target.Filter, target.Guids, () => popup.IsOpen = false);
        popup.IsOpen = true;
    }
}
