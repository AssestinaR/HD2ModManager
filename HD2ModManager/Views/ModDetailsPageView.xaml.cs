using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views
{
    // 作用：显示单个 Mod 的详情信息与派生文件组概览。
    public partial class ModDetailsPageView : UserControl, IModRelationshipDropTarget
    {
        public ModDetailsPageView()
        {
            InitializeComponent();
            Loaded += (_, _) => ModRelationshipDragCoordinator.RegisterTarget(this);
            Unloaded += (_, _) =>
            {
                ResetRelationshipDropOverlay();
                ModRelationshipDragCoordinator.UnregisterTarget(this);
            };
        }

        /// <summary>
        /// 下拉打开时把滚轮劫下来。
        /// <para>
        /// 为什么需要：外层是整页的 <c>ScrollViewer</c>，而下拉的 <c>Popup</c> 是独立窗口。
        /// 指针停在展开的列表上时滚轮本来归列表；但停在"下拉框本身"上时事件会一路隧道到
        /// 页面，表现成"列表没动、整页在滚"。这里在打开状态把事件吃掉并转给列表，
        /// 页面就不会跟着滚。
        /// </para>
        /// </summary>
        private void OnVpkTargetArmorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ComboBox combo || !combo.IsDropDownOpen)
                return;
            e.Handled = true;
            if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: DependencyObject child })
                return;
            var viewer = FindDescendant<ScrollViewer>(child);
            viewer?.ScrollToVerticalOffset(viewer.VerticalOffset - Math.Sign(e.Delta) * 48d);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is { } nested) return nested;
            }

            return null;
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
                    if (shell is not null) await shell.DeleteModsFromLibraryAsync([card.Mod.Guid]);
                    break;
            }
        }

        private async void OnHostAttachmentInternalReorderRequested(object? sender, ModListInternalReorderEventArgs e)
        {
            if (DataContext is ModDetailsPageViewModel details)
                await details.ReorderHostOptionsAsync(e.DraggedKeys, e.InsertionIndex);
        }

        private void OnHostAttachmentTransitionStateChanged(object? sender, ModListTransitionStateChangedEventArgs e)
        {
            if (e.IsActive || DataContext is not ModDetailsPageViewModel details) return;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(details.CommitDisplayedHostOptionOrders));
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

        bool IModRelationshipDropTarget.CanAcceptRelationshipDrop(ModRelationshipDragPayload payload)
            => IsLoaded
                && IsVisible
                && ModRelationshipDragCoordinator.IsVisuallyActive(this)
                && payload.Operation == ModRelationshipDragOperation.SetAsOption
                && payload.SelectedKeys.Count > 0
                && DataContext is ModDetailsPageViewModel details
                && details.CanHostAttachments;

        bool IModRelationshipDropTarget.ContainsScreenPoint(Point screenPoint)
        {
            if (!IsLoaded
                || !IsVisible
                || !ModRelationshipDragCoordinator.IsVisuallyActive(this)
                || RelationshipDropOverlay.Visibility != Visibility.Visible
                || RelationshipDropOverlay.ActualWidth <= 0
                || RelationshipDropOverlay.ActualHeight <= 0)
                return false;
            try
            {
                var local = RelationshipDropOverlay.PointFromScreen(screenPoint);
                return local.X >= 0
                    && local.Y >= 0
                    && local.X <= RelationshipDropOverlay.ActualWidth
                    && local.Y <= RelationshipDropOverlay.ActualHeight;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        bool IModRelationshipDropTarget.ContainsElement(DependencyObject element)
            => IsAncestorOf(element);

        void IModRelationshipDropTarget.SetRelationshipDropOverlay(ModRelationshipDragPayload payload, bool visible)
        {
            if (visible)
            {
                var hostName = DataContext is ModDetailsPageViewModel details
                    ? details.Name
                    : "当前 Mod";
                RelationshipDropTitle.Text = $"释放以设为“{hostName}”的选项";
                RelationshipDropDetail.Text = $"将 {payload.SelectedKeys.Count} 个 Mod 挂载到该主体下";
                RelationshipDropOverlay.Visibility = Visibility.Visible;
                RelationshipDropOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                RelationshipDropOverlay.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(140)));
                return;
            }

            if (!IsLoaded)
            {
                ResetRelationshipDropOverlay();
                return;
            }
            if (RelationshipDropOverlay.Visibility != Visibility.Visible) return;
            RelationshipDropOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) =>
            {
                if (RelationshipDropOverlay.Opacity <= 0.01)
                    RelationshipDropOverlay.Visibility = Visibility.Collapsed;
            };
            RelationshipDropOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        void IModRelationshipDropTarget.RaiseRelationshipDropRequested(ModRelationshipDragPayload payload)
        {
            if (payload.Operation != ModRelationshipDragOperation.SetAsOption
                || DataContext is not ModDetailsPageViewModel details)
                return;
            var shell = Application.Current?.MainWindow?.DataContext as ShellViewModel;
            if (shell is not null)
                _ = shell.SetModsAsOptionsForHostAsync(details.ModId, payload.SelectedKeys);
        }

        private void ResetRelationshipDropOverlay()
        {
            RelationshipDropOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            RelationshipDropOverlay.Visibility = Visibility.Collapsed;
            RelationshipDropOverlay.Opacity = 0;
        }
    }
}
