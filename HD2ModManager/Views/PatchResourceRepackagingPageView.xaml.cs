using System.Windows.Controls;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class PatchResourceRepackagingPageView : UserControl
{
    public PatchResourceRepackagingPageView() => InitializeComponent();

    private void OnSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
    {
        if (DataContext is PatchResourceRepackagingPageViewModel vm)
            vm.ApplyModSelection(e.SelectedKeys);
    }
}
