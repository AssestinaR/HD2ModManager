using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：显示单个 Mod 的详情信息与派生文件组概览。
    public partial class ModDetailsPageView : UserControl
    {
        public ModDetailsPageView()
        {
            InitializeComponent();
        }

        private void OnSelectImageClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Mod 图像",
                Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true && DataContext is ModDetailsPageViewModel details)
            {
                details.UpdateImageCommand.Execute(dialog.FileName);
            }
        }

        private void OnEditNameClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;
            if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.BeginBottomBarNameEdit(details.ModId, details.Name);
        }

        private void OnEditDescriptionClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;
            if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.BeginBottomBarDescriptionEdit(details.ModId, details.Mod?.Description ?? string.Empty);
        }

        private async void OnHostDecorationRowActionInvoked(object? sender, ModListRowActionEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details || e.Item is not ModCardViewModel card) return;
            var shell = Application.Current?.MainWindow?.DataContext as ShellViewModel;
            shell?.ClearTransientSelection();
            switch (e.Action)
            {
                case ModListRowAction.Rename:
                    shell?.BeginBottomBarNameEdit(card.Mod.Guid, card.Mod.Name);
                    break;
                case ModListRowAction.Details:
                    shell?.OpenModDetailsFromPage(details, card.Mod.Guid);
                    break;
                case ModListRowAction.ToggleDecoration:
                    await details.ToggleDecorationForCurrentHostAsync(card);
                    break;
                case ModListRowAction.OpenFolder:
                    details.OpenModFolder(card.Mod.Guid);
                    break;
                case ModListRowAction.DeleteFromLibrary:
                    await details.DeleteDecorationAsync(card.Mod.Guid);
                    break;
            }
        }

        private void OnHostDecorationOpenDetailsOnRightClick(object? sender, ModListRowEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details || e.Item is not ModCardViewModel card) return;
            var shell = (Application.Current?.MainWindow as MainWindow)?.DataContext as ShellViewModel;
            shell?.OpenModDetailsFromPage(details, card.Mod.Guid);
        }

        private void OnHostDecorationSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;
            if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.Selection.Replace($"DecorationHost:{details.ModId}", e.SelectedKeys);
        }

        private async void OnHostDecorationKeyboardCommandRequested(object? sender, ModListKeyboardCommandEventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;
            var shell = (Application.Current?.MainWindow as MainWindow)?.DataContext as ShellViewModel;
            switch (e.Command)
            {
                case ModListKeyboardCommand.Rename when e.Item is ModCardViewModel card:
                    shell?.ClearTransientSelection();
                    shell?.BeginBottomBarNameEdit(card.Mod.Guid, card.Mod.Name);
                    break;
                case ModListKeyboardCommand.OpenDetails when e.Item is ModCardViewModel card:
                    shell?.OpenModDetailsFromPage(details, card.Mod.Guid);
                    break;
                case ModListKeyboardCommand.Delete:
                case ModListKeyboardCommand.DeleteFromLibrary:
                    if (shell is not null) await shell.DeleteModsFromLibraryAsync(e.SelectedKeys);
                    break;
            }
        }
    }
}
