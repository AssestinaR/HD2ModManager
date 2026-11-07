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

        // 选择栏在窗口底部，卡片向上展开。开合由 CategoryPopup 统一管。
        CategoryPopup.Toggle(anchor, PlacementMode.Top, -14, 6,
            () => new CategoryPickerCard(target.Filter, target.Guids, CategoryPopup.Close));
    }
}
