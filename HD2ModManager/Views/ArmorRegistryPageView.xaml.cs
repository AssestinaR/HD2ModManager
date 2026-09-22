using System.Windows;
using System.Windows.Controls;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class ArmorRegistryPageView : UserControl
{
	private bool initialized;

	public ArmorRegistryPageView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (initialized || DataContext is not ArmorRegistryPageViewModel viewModel) return;
		initialized = true;
		await viewModel.InitializeAsync();
	}
}
