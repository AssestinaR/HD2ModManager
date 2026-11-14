using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：目标护甲下拉控件（搜索 + 单选）。行为都在 VpkTargetPickerViewModel，这里只转交点击与键盘。
    // Purpose: Code-behind for the searchable target picker; forwards clicks/keys to the view-model.
    public partial class VpkTargetPicker : UserControl
    {
        // 根因：Popup 原来是 StaysOpen=False，会在鼠标按下/抬起落在弹层外（输入框在弹层外）时自己关掉，
        // 而这里又在按下时把 IsOpen 置 true、搜索框拿到焦点也置 true，两边抢同一个状态：单击时刚开就被关，
        // 只有按住不放（抬起前）才看得见。现在改成 StaysOpen=True，开关只由本控件决定：
        // 点输入框区域切换（点在搜索框文字上只展开，不收起），点控件和弹层之外、窗口失活、窗口移动都收起。
        public VpkTargetPicker()
        {
            InitializeComponent();
            Loaded += (_, _) => Hook(true);
            Unloaded += (_, _) => Hook(false);
        }

        private Window? _window;

        private VpkTargetPickerViewModel? Vm => DataContext as VpkTargetPickerViewModel;

        private void Hook(bool on)
        {
            if (on)
            {
                _window = Window.GetWindow(this);
                if (_window == null) return;
                _window.PreviewMouseDown += Window_PreviewMouseDown;
                _window.Deactivated += Window_Closeish;
                _window.LocationChanged += Window_Closeish;
            }
            else if (_window != null)
            {
                _window.PreviewMouseDown -= Window_PreviewMouseDown;
                _window.Deactivated -= Window_Closeish;
                _window.LocationChanged -= Window_Closeish;
                _window = null;
            }
        }

        private void Window_Closeish(object? sender, EventArgs e)
        {
            if (Vm is { } vm) vm.IsOpen = false;
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm is not { IsOpen: true } vm) return;
            if (e.OriginalSource is DependencyObject src && (IsInside(src, Box) || IsInside(src, DropPopup.Child))) return;
            vm.IsOpen = false;
        }

        private static DependencyObject? ParentOf(DependencyObject n) =>
            (n is System.Windows.Media.Visual || n is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(n) : null) ?? LogicalTreeHelper.GetParent(n);

        private static bool IsInside(DependencyObject? node, DependencyObject? root)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, root)) return true;
                node = ParentOf(node);
            }
            return false;
        }

        private void Box_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm is not { } vm) return;
            var src = e.OriginalSource as DependencyObject;
            bool onSearch = src != null && IsInside(src, Search);
            vm.IsOpen = onSearch ? true : !vm.IsOpen;
            if (!onSearch) { Search.Focus(); e.Handled = true; }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Vm is { } vm && Search.IsKeyboardFocusWithin) vm.IsOpen = true;
        }

        private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Vm is not { } vm) return;
            if (e.Key == Key.Escape) { vm.IsOpen = false; e.Handled = true; }
        }

        private void Row_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not VpkTargetChoice choice) return;
            vm.ToggleCommand.Execute(choice);
            e.Handled = true;
        }
    }
}
