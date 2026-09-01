using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace HD2ModManager.Views
{
    // 作用：承载跨护甲左侧计划、来源和目标预测数据表。
    public partial class CrossArmorPlanPageView : UserControl
    {
        public CrossArmorPlanPageView() => InitializeComponent();

        private void OnBrowseOutputClick(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择跨护甲验证候选输出文件夹", Multiselect = false };
            if (dialog.ShowDialog() == true && DataContext is CrossArmorTransferPlanWindowViewModel { CandidateOutput: { } output }) output.SetOutputDirectory(dialog.FolderName);
        }

        private void OnTargetMappingContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not DataGrid grid || DataContext is not CrossArmorTransferPlanWindowViewModel viewModel) return;
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.Item is not HD2ModCore.Domain.CrossArmorTransferMapping mapping)
            {
                e.Handled = true;
                return;
            }

            grid.SelectedItem = mapping;
            viewModel.SelectedTargetMapping = mapping;
            var menu = new ContextMenu();
            var restore = new MenuItem { Header = "恢复自动（取消手动设置）", Command = viewModel.RestoreSelectedMappingCommand };
            restore.IsEnabled = mapping.IsManual || mapping.IsSuppressed;
            menu.Items.Add(restore);
            menu.Items.Add(new Separator());

            var suppress = new MenuItem { Header = "强制隐藏", Command = viewModel.SuppressSelectedMappingCommand };
            suppress.IsEnabled = !mapping.IsSuppressed;
            menu.Items.Add(suppress);

            var sources = viewModel.GetManualSourceChoices(mapping);
            var recommendations = sources
                .Where(source => source.PartKind == mapping.Target.PartKind)
                .Take(2)
                .ToArray();
            for (var index = 0; index < recommendations.Length; index++)
            {
                var source = recommendations[index];
                var item = new MenuItem { Header = $"强制命中 {(char)('A' + index)}：{DescribeManualSource(source)}" };
                item.Click += (_, _) => viewModel.SetManualMapping(mapping, source);
                menu.Items.Add(item);
            }

            var alternatives = sources
                .Where(source => !recommendations.Contains(source))
                .ToArray();
            if (alternatives.Length != 0)
            {
                menu.Items.Add(new Separator());
                var more = new MenuItem { Header = $"更多强制命中来源（{alternatives.Length}）" };
                foreach (var group in alternatives.GroupBy(source => source.PartKind))
                {
                    var groupMenu = new MenuItem { Header = $"{group.Key}（{group.Count()}）" };
                    foreach (var source in group)
                    {
                        var item = new MenuItem { Header = DescribeManualSource(source) };
                        item.Click += (_, _) => viewModel.SetManualMapping(mapping, source);
                        groupMenu.Items.Add(item);
                    }
                    more.Items.Add(groupMenu);
                }
                menu.Items.Add(more);
            }

            menu.PlacementTarget = grid;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static string DescribeManualSource(HD2ModCore.Domain.EquipmentUnitPart source)
        {
            var semantic = string.IsNullOrWhiteSpace(source.SemanticName) ? "未命名来源" : source.SemanticName;
            return $"{semantic} · {source.PartKind}/{source.BodyVariant}/{source.Layer} · 0x{source.UnitAssetKey.FileId:x16}";
        }

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }
    }
}
