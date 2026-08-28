using System.Windows;
using System.Windows.Controls;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class HelmetToArmorMergePlanPageView : UserControl
{
    public HelmetToArmorMergePlanPageView() => InitializeComponent();

    private void OnSourceSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
    {
        if (DataContext is HelmetToArmorMergePlanPageViewModel viewModel)
            viewModel.ApplySourceSelection(e.SelectedKeys);
    }
}
