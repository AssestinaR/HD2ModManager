using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：通过右键菜单复制右槽中选定 AssetKey 的完整事实。
    public partial class GameDataArchiveDetailsPageView : UserControl
    {
        public GameDataArchiveDetailsPageView() => InitializeComponent();
        private void OnAssetContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is DataGrid grid && FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is { DataContext: GameDataArchiveAssetRowViewModel row })
            {
                grid.SelectedItem = row;
            }
        }

        private void OnCopyAssetMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGrid grid } }
                && grid.DataContext is GameDataArchiveDetailsPageViewModel viewModel
                && grid.SelectedItem is GameDataArchiveAssetRowViewModel row)
            {
                viewModel.CopyAsset(row);
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
        {
            for (var current = source; current is not null; current = VisualTreeWalk.Parent(current))
            {
                if (current is T result) return result;
            }
            return null;
        }
    }
}