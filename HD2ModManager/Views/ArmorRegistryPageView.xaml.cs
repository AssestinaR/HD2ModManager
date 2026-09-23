using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class ArmorRegistryPageView : UserControl, IModRelationshipDropTarget
{
	public ArmorRegistryPageView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Loaded += (_, _) => ModRelationshipDragCoordinator.RegisterTarget(this);
		Unloaded += (_, _) =>
		{
			ModRelationshipDragCoordinator.UnregisterTarget(this);
			ResetDropOverlays();
		};
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (DataContext is not ArmorRegistryPageViewModel viewModel) return;
		await viewModel.InitializeAsync();
	}

	private void OnProviderSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
	{
		if (DataContext is not ArmorRegistryPageViewModel viewModel) return;
		viewModel.ApplyProviderSelection(e.SelectedKeys);
	}

	private void OnProviderRightClicked(object? sender, ModListRowEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.OpenProviderDetails(e.Item as RuntimeArmorChoiceViewModel);
	}

	private void OnProviderRowActionInvoked(object? sender, ModListRowActionEventArgs e)
	{
		if (DataContext is not ArmorRegistryPageViewModel viewModel
			|| e.Item is not RuntimeArmorChoiceViewModel item) return;
		if (e.Action == ModListRowAction.ArmorRelease)
			viewModel.ReleaseProviderItem(item);
		else if (e.Action == ModListRowAction.ArmorOpenDetails)
			viewModel.OpenProviderDetails(item);
	}

	private void OnDetailSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.ApplyDetailSelection(e.SelectedKeys);
	}

	private void OnDetailRowActionInvoked(object? sender, ModListRowActionEventArgs e)
	{
		if (e.Action == ModListRowAction.ArmorRelease
			&& DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.ReleaseTargetItem(e.Item as RuntimeArmorChoiceViewModel);
	}

	private void OnAvailableSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.ApplyAvailableSelection(e.SelectedKeys);
	}

	private void OnAvailableRowActionInvoked(object? sender, ModListRowActionEventArgs e)
	{
		if (e.Action == ModListRowAction.ArmorAssign
			&& DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.AssignAvailableItem(e.Item as RuntimeArmorChoiceViewModel);
	}

	private void OnCycleAvailableArmorUnitFilterClick(object sender, RoutedEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.CycleAvailableArmorUnitFilter();
	}

	private void OnBackToProvidersClick(object? sender, EventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.CloseProvider();
	}

	bool IModRelationshipDropTarget.CanAcceptRelationshipDrop(ModRelationshipDragPayload payload)
	{
		if (!IsLoaded || !IsVisible || DataContext is not ArmorRegistryPageViewModel viewModel)
			return false;

		return payload.Operation switch
		{
			ModRelationshipDragOperation.ArmorRegistryAssign => viewModel.CanAssignFromAvailable(payload.SelectedKeys),
			ModRelationshipDragOperation.ArmorRegistryRelease => viewModel.CanReleaseToAvailable(payload.SelectedKeys),
			_ => false,
		};
	}

	bool IModRelationshipDropTarget.ContainsScreenPoint(Point screenPoint)
	{
		if (!IsLoaded || DataContext is not ArmorRegistryPageViewModel viewModel) return false;
		var payload = ModRelationshipDragCoordinator.CurrentPayload;
		if (payload is null) return false;

		var overlay = payload.Operation == ModRelationshipDragOperation.ArmorRegistryAssign
			? DropLeftOverlay
			: DropRightOverlay;
		if (overlay.Visibility != Visibility.Visible || overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
			return false;
		try
		{
			var local = overlay.PointFromScreen(screenPoint);
			return local.X >= 0 && local.Y >= 0
				&& local.X <= overlay.ActualWidth
				&& local.Y <= overlay.ActualHeight;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	bool IModRelationshipDropTarget.ContainsElement(DependencyObject element)
		=> false;

	void IModRelationshipDropTarget.SetRelationshipDropOverlay(ModRelationshipDragPayload payload, bool visible)
	{
		var overlay = payload.Operation == ModRelationshipDragOperation.ArmorRegistryAssign
			? DropLeftOverlay
			: DropRightOverlay;
		if (visible)
		{
			if (payload.Operation == ModRelationshipDragOperation.ArmorRegistryAssign)
			{
				DropLeftTitle.Text = DataContext is ArmorRegistryPageViewModel { IsProviderDetailOpen: true }
					? "释放以添加映射目标"
					: "释放以设为视觉来源";
				DropLeftDetail.Text = DataContext is ArmorRegistryPageViewModel { IsProviderDetailOpen: true }
					? $"添加 {payload.SelectedKeys.Count} 个未管理护甲"
					: payload.SelectedKeys.Count == 1
						? "将此护甲设为视觉对象"
						: $"将 {payload.SelectedKeys.Count} 个护甲分别设为视觉对象";
			}
			else
			{
				DropRightTitle.Text = "释放以解除管理";
				DropRightDetail.Text = "移除视觉来源会同时释放它的全部目标";
			}
			overlay.Visibility = Visibility.Visible;
			overlay.BeginAnimation(OpacityProperty, null);
			overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(120)));
			return;
		}

		overlay.BeginAnimation(OpacityProperty, null);
		overlay.Visibility = Visibility.Collapsed;
		overlay.Opacity = 0;
	}

	void IModRelationshipDropTarget.RaiseRelationshipDropRequested(ModRelationshipDragPayload payload)
	{
		if (DataContext is not ArmorRegistryPageViewModel viewModel) return;
		if (payload.Operation == ModRelationshipDragOperation.ArmorRegistryAssign)
			viewModel.AssignFromAvailable(payload.SelectedKeys);
		else if (payload.Operation == ModRelationshipDragOperation.ArmorRegistryRelease)
			viewModel.ReleaseToAvailable(payload.SelectedKeys);
	}

	private void ResetDropOverlays()
	{
		foreach (var overlay in new[] { DropLeftOverlay, DropRightOverlay })
		{
			overlay.BeginAnimation(OpacityProperty, null);
			overlay.Visibility = Visibility.Collapsed;
			overlay.Opacity = 0;
		}
	}
}
