using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：标题卡里"分类模式"的内容——与标题/搜索框同一个位置，按可用宽高换行，装不下纵向滚动。
    // 数据来自 ModCategoryFilter；末尾的"设置分类"芯片打开管理弹窗。
    public partial class CategoryChipBar : UserControl
    {
        public CategoryChipBar() => InitializeComponent();

        // 普通芯片的筛选走 SelectCommand；"设置分类"芯片在这里打开弹窗（弹窗要锚在按钮上，命令里没有这个信息）。
        // 开合同一个锚点只由一个地方管：再点一下这个芯片就是收起。
        private void OnChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LibraryCategoryChip { IsAdd: true } } anchor
                || DataContext is not ModCategoryFilter filter) return;

            // 卡片左缘对齐按钮左缘；阴影边距 14 要抵掉。
            CategoryPopup.Toggle(anchor, PlacementMode.Bottom, -14, -6,
                () => new CategoryManageCard(filter, CategoryPopup.Close));
        }
    }
}
