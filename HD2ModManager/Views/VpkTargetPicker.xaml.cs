using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：目标护甲下拉控件（搜索 + 单选/多选）。行为都在 VpkTargetPickerViewModel，这里只转交点击与键盘。
    // Purpose: Code-behind for the searchable target picker; forwards clicks/keys to the view-model.
    public partial class VpkTargetPicker : UserControl
    {
        public VpkTargetPicker() { InitializeComponent(); }

        private VpkTargetPickerViewModel? Vm => DataContext as VpkTargetPickerViewModel;

        private void Box_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm is not { } vm) return;
            Search.Focus();
            vm.IsOpen = true;
        }

        private void Search_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (Vm is { } vm) vm.IsOpen = true;
        }

        private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Vm is not { } vm) return;
            if (e.Key == Key.Escape) { vm.IsOpen = false; e.Handled = true; }
            // 多选时在空输入框里按退格：去掉最后一个标签（和 el-select 一致）。
            else if (e.Key == Key.Back && vm.IsMultiple && string.IsNullOrEmpty(vm.SearchText) && vm.Chips.Count != 0)
            {
                vm.Chips[^1].IsChecked = false;
                e.Handled = true;
            }
        }

        private void Row_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not VpkTargetChoice choice) return;
            vm.ToggleCommand.Execute(choice);
            e.Handled = true;
        }
    }
}
