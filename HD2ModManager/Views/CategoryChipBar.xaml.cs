using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：列表卡片顶部固定的分类筛选芯片条。数据来自 ModCategoryFilter，横向滚动不换行；
    // 末尾的"设置分类"芯片打开管理弹窗。
    public partial class CategoryChipBar : UserControl
    {
        public CategoryChipBar() => InitializeComponent();

        // 滚轮竖向滚动换成横向平移：条本身没有竖向内容，让滚轮在这里有意义，而不是被吞掉。
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scroller || scroller.ScrollableWidth <= 0) return;
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        // 普通芯片的筛选走 SelectCommand；"设置分类"芯片在这里打开弹窗（弹窗要锚在按钮上，命令里没有这个信息）。
        private void OnChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LibraryCategoryChip { IsAdd: true } } anchor
                || DataContext is not ModCategoryFilter filter) return;
            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                // 卡片左缘对齐按钮左缘；阴影边距 14 要抵掉。
                HorizontalOffset = -14,
                VerticalOffset = -6,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };
            popup.Child = new CategoryManageCard(filter, () => popup.IsOpen = false);
            popup.IsOpen = true;
        }
    }
}
