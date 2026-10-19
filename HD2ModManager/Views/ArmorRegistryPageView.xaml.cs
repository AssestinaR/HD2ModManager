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
		Unloaded += async (_, _) =>
		{
			if (DataContext is not ArmorRegistryPageViewModel viewModel) return;
			try { await viewModel.FlushWorkspaceAsync(); }
			catch (Exception exception) { HD2ModManager.Services.LogService.Error(exception.ToString()); }
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
		else if (e.Action == ModListRowAction.ArmorEditComponents)
			_ = viewModel.OpenComponentEditorAsync(item, false);
		else if (e.Action == ModListRowAction.ArmorDeleteCustomModel)
			viewModel.DeleteCustomModelItem(item);
		else if (e.Action == ModListRowAction.ArmorRenameCustomModel)
			viewModel.RenameCustomModelItem(item);
	}

	private void OnDetailSelectionRequested(object? sender, ModListSelectionRequestEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.ApplyDetailSelection(e.SelectedKeys);
	}

	private void OnDetailRowActionInvoked(object? sender, ModListRowActionEventArgs e)
	{
		if (DataContext is not ArmorRegistryPageViewModel viewModel) return;
		if (e.Action == ModListRowAction.ArmorRelease)
			viewModel.ReleaseTargetItem(e.Item as RuntimeArmorChoiceViewModel);
		else if (e.Action == ModListRowAction.ArmorEditComponents)
			_ = viewModel.OpenComponentEditorAsync(e.Item as RuntimeArmorChoiceViewModel, true);
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

	// 双击展开/收起，树上没有小三角。两个坑都在这里挡住：
	// 1. MouseDoubleClick 会沿祖先链一路冒泡，不判断"最内层"的话，双击子行会把顶层也收起；
	// 2. 点在开关上不算双击展开，否则连点两下开关会顺手展开子树。
	private void OnWardrobeTreeItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (sender is not TreeViewItem item)
			return;
		if (e.OriginalSource is not System.Windows.DependencyObject source)
			return;
		var innermost = FindAncestorTreeViewItem(source);
		if (!ReferenceEquals(innermost, item))
			return;
		if (FindAncestorCheckBox(source) is not null)
			return;
		item.IsExpanded = !item.IsExpanded;
		e.Handled = true;
	}

	private static TreeViewItem? FindAncestorTreeViewItem(System.Windows.DependencyObject source)
	{
		// 要的是**最内层**那个：从命中点往上走，遇到的第一个就是子行自己。
		// 取最外层的话，双击子行会被判成"命中的是顶层"，于是顶层被收起。
		for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
		{
			if (current is TreeViewItem item)
				return item;
		}
		return null;
	}

	private static CheckBox? FindAncestorCheckBox(System.Windows.DependencyObject source)
	{
		for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
		{
			if (current is CheckBox checkBox)
				return checkBox;
		}
		return null;
	}

	private void OnCycleAvailableArmorKindFilterClick(object sender, RoutedEventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.CycleAvailableArmorKindFilter();
	}

	private void OnBackToProvidersClick(object? sender, EventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.CloseProvider();
	}

	private void OnCloseComponentEditorClick(object? sender, EventArgs e)
	{
		if (DataContext is ArmorRegistryPageViewModel viewModel)
			viewModel.CloseComponentEditor();
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
