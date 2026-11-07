using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：给一个或多个 Mod 设置分类的卡片弹窗。点标签即归类并关闭；"新建分类"在弹窗里就地输入。
    public partial class CategoryPickerCard : UserControl
    {
        private sealed record PickerItem(string Key, string Display, bool IsChecked);

        private readonly ModCategoryFilter _filter;
        private readonly IReadOnlyList<string> _guids;
        private readonly Action _close;

        public CategoryPickerCard(ModCategoryFilter filter, IReadOnlyList<string> guids, Action close)
        {
            InitializeComponent();
            _filter = filter;
            _guids = guids;
            _close = close;
            Subtitle.Text = guids.Count == 1 ? "选一个分类，覆盖自动识别的结果" : $"已选 {guids.Count} 个 Mod，一起归到同一分类";
            Pills.ItemsSource = filter.AssignTargets
                .Select(target => new PickerItem(target.Key, filter.AllInCategory(guids, target.Key) ? "✓ " + target.Label : target.Label, filter.AllInCategory(guids, target.Key)))
                .ToArray();
            ResetButton.Visibility = filter.AnyOverridden(guids) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPillClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PickerItem item) return;
            _filter.Assign(_guids, item.Key);
            _close();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            _filter.Assign(_guids, null);
            _close();
        }

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            NewButton.Visibility = Visibility.Collapsed;
            NewPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(() => { Keyboard.Focus(NewBox); NewBox.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnNewTextChanged(object sender, TextChangedEventArgs e) => Hint.Visibility = Visibility.Collapsed;

        private void OnNewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { OnCreateClick(sender, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { _close(); e.Handled = true; }
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (_filter.CreateAndAssign(NewBox.Text, _guids)) { _close(); return; }
            Hint.Text = string.IsNullOrWhiteSpace(NewBox.Text) ? "请输入分类名称" : "名称已存在，或与内置分类重名";
            Hint.Visibility = Visibility.Visible;
        }
    }
}
