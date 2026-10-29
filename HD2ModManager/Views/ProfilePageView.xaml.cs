using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HD2ModManager.Controls.Lists;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class ProfilePageView : UserControl
{
    public ProfilePageView()
    {
        InitializeComponent();
        Loaded += (_, _) => ModListExternalProfileDropCoordinator.RegisterTarget(ProfileModList);
        Unloaded += (_, _) => ModListExternalProfileDropCoordinator.UnregisterTarget(ProfileModList);
    }

    private void OnSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
    {
        if (DataContext is ProfilePageViewModel vm)
            vm.ApplySelection(e.SelectedKeys);
    }

    private async void OnInternalReorderRequested(object? sender, ModListInternalReorderEventArgs e)
    {
        if (DataContext is ProfilePageViewModel vm)
            await vm.ReorderAsync(e.DraggedKeys, e.InsertionIndex);
    }

    private void OnListTransitionStateChanged(object? sender, ModListTransitionStateChangedEventArgs e)
    {
        if (e.IsActive || DataContext is not ProfilePageViewModel vm) return;

        // Keep changing order labels out of the FLIP frame itself. A background
        // dispatcher turn gives the controller a clean visual boundary before
        // the text content for the moved rows is updated.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(vm.CommitDisplayedOrders));
    }

    private async void OnExternalProfileDropRequested(object? sender, ModListExternalProfileDropEventArgs e)
    {
        if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            await shell.AddModsToSelectedProfileAsync(e.SelectedKeys);
    }

    private async void OnKeyboardCommandRequested(object? sender, ModListKeyboardCommandEventArgs e)
    {
        if (DataContext is not ProfilePageViewModel vm) return;
        var shell = (Application.Current?.MainWindow as MainWindow)?.DataContext as ShellViewModel;
        switch (e.Command)
        {
            case ModListKeyboardCommand.Rename when e.Item is ProfileListItemViewModel item:
                shell?.ClearTransientSelection();
                shell?.BeginBottomBarNameEdit(item.Guid, item.Name);
                break;
            case ModListKeyboardCommand.OpenDetails when e.Item is ProfileListItemViewModel item:
                shell?.OpenModDetailsFromPage(vm, item.Guid);
                break;
            case ModListKeyboardCommand.Delete:
                if (shell is not null) await shell.RemoveModsFromSelectedProfileAsync(e.SelectedKeys);
                break;
            case ModListKeyboardCommand.DeleteFromLibrary:
                if (shell is not null) await shell.DeleteModsFromLibraryAsync(e.SelectedKeys);
                break;
        }
    }

    private void OnRowActionInvoked(object? sender, ModListRowActionEventArgs e)
    {
        if (DataContext is not ProfilePageViewModel vm || e.Item is not ProfileListItemViewModel item) return;
        ClearTransientSelection();
        var shell = (Application.Current?.MainWindow as MainWindow)?.DataContext as ShellViewModel;
        switch (e.Action)
        {
            case ModListRowAction.Details:
                shell?.OpenModDetailsFromPage(vm, item.Guid);
                break;
            case ModListRowAction.MoveUp:
                vm.MoveUpCommand.Execute(item.Guid);
                break;
            case ModListRowAction.MoveDown:
                vm.MoveDownCommand.Execute(item.Guid);
                break;
            case ModListRowAction.ExportMod:
                shell?.BeginModExport(item.Guid, item.Name);
                break;
            case ModListRowAction.RemoveFromProfile:
                vm.RemoveModCommand.Execute(item.Guid);
                break;
            case ModListRowAction.DeleteFromLibrary:
                vm.DeleteModFromLibraryCommand.Execute(item.Guid);
                break;
        }
    }

    private void OnOpenDetailsOnRightClick(object sender, ModListRowEventArgs e)
    {
        if (DataContext is not ProfilePageViewModel vm || e.Item is not ProfileListItemViewModel item) return;
        var shell = (Application.Current?.MainWindow as MainWindow)?.DataContext as ShellViewModel;
        shell?.OpenModDetailsFromPage(vm, item.Guid);
    }

    private void OnListBackgroundClick(object? sender, EventArgs e) => ClearTransientSelection();

    private void OnToggleOutdatedFilterClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilePageViewModel vm) vm.ShowOnlyOutdated = !vm.ShowOnlyOutdated;
    }

    private static void ClearTransientSelection()
    {
        if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            shell.ClearTransientSelection();
    }
}
