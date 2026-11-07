using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：自建分类的管理弹窗：新建、重命名、删除。每次操作后原地刷新列表，弹窗保持打开。
    public partial class CategoryManageCard : UserControl
    {
        private sealed class Row : BaseViewModel
        {
            private bool _isEditing;
            private string _editText = string.Empty;
            private bool _isConfirmingDelete;
            public Row(string key, string name, int count)
            {
                Key = key; Name = name; CountText = count == 0 ? "空" : $"{count} 个 Mod";
                DeleteMessage = count == 0 ? $"删除「{name}」？" : $"删除「{name}」？里面 {count} 个 Mod 会回到自动分类。";
            }
            public string DeleteMessage { get; }
            public bool IsConfirmingDelete { get => _isConfirmingDelete; set => SetField(ref _isConfirmingDelete, value); }
            public string Key { get; }
            public string Name { get; }
            public string CountText { get; }
            public bool IsEditing { get => _isEditing; set => SetField(ref _isEditing, value); }
            public string EditText { get => _editText; set => SetField(ref _editText, value); }
        }

        private readonly ModCategoryFilter _filter;
        private readonly Action _close;
        private readonly ObservableCollection<Row> _rows = new();

        public CategoryManageCard(ModCategoryFilter filter, Action close)
        {
            InitializeComponent();
            _filter = filter;
            _close = close;
            Rows.ItemsSource = _rows;
            Reload();
        }

        private void Reload()
        {
            _rows.Clear();
            foreach (var item in _filter.CustomCategories)
            {
                var key = CategoryKey.OfCustom(item.Id);
                _rows.Add(new Row(key, item.Name, _filter.CountOf(key)));
            }
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rows.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static Row? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as Row;

        private void ShowHint(string? text)
        {
            Hint.Text = text ?? string.Empty;
            Hint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is not { } row) return;
            foreach (var other in _rows) { other.IsEditing = false; other.IsConfirmingDelete = false; }
            row.EditText = row.Name;
            row.IsEditing = true;
            ShowHint(null);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) row.IsEditing = false;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) SaveRename(row);
        }

        private void SaveRename(Row row)
        {
            if (_filter.Rename(row.Key, row.EditText)) { Reload(); ShowHint(null); }
            else ShowHint(string.IsNullOrWhiteSpace(row.EditText) ? "请输入分类名称" : "名称已存在，或与内置分类重名");
        }

        // 删除要二次确认：先把这一行切到确认态，同一时间只有一行处于确认/编辑。
        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is not { } row) return;
            foreach (var other in _rows) { other.IsEditing = false; other.IsConfirmingDelete = false; }
            row.IsConfirmingDelete = true;
            ShowHint(null);
        }

        private void OnCancelDeleteClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) row.IsConfirmingDelete = false;
        }

        private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is not { } row) return;
            _filter.Delete(row.Key);
            Reload();
            ShowHint(null);
        }

        private void OnEditKeyDown(object sender, KeyEventArgs e)
        {
            if (RowOf(sender) is not { } row) return;
            if (e.Key == Key.Enter) { SaveRename(row); e.Handled = true; }
            else if (e.Key == Key.Escape) { row.IsEditing = false; e.Handled = true; }
        }

        private void OnEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true || sender is not TextBox box) return;
            Dispatcher.BeginInvoke(() => { Keyboard.Focus(box); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnNewTextChanged(object sender, TextChangedEventArgs e) => ShowHint(null);

        private void OnNewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { OnCreateClick(sender, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { _close(); e.Handled = true; }
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (_filter.Create(NewBox.Text))
            {
                NewBox.Clear();
                Reload();
                ShowHint(null);
            }
            else ShowHint(string.IsNullOrWhiteSpace(NewBox.Text) ? "请输入分类名称" : "名称已存在，或与内置分类重名");
        }
    }
}
