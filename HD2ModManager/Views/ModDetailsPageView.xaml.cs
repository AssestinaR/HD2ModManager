using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.Controls.Lists;
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
                RelationshipDropOverlay.HideImmediately();
                ModRelationshipDragCoordinator.UnregisterTarget(this);
            };
            DataContextChanged += OnDetailsDataContextChanged;
        }

        // 动作在 VM 里声明（浮动操作簇负责画）；这里只接住"必须由视图/壳才能做"的三件事：
        // 文件对话框、底栏重命名、底栏改备注。
        private void OnDetailsDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ModDetailsPageViewModel previous)
            {
                previous.EditImageRequested -= OnEditImageRequested;
                previous.EditNameRequested -= OnEditNameRequested;
                previous.EditDescriptionRequested -= OnEditDescriptionRequested;
            }

            if (e.NewValue is ModDetailsPageViewModel current)
            {
                current.EditImageRequested += OnEditImageRequested;
                current.EditNameRequested += OnEditNameRequested;
                current.EditDescriptionRequested += OnEditDescriptionRequested;
            }
        }

        private void OnEditImageRequested(object? sender, EventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;

            var dialog = new OpenFileDialog
            {
                Title = "选择 Mod 图像",
                Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true) details.UpdateImageCommand.Execute(dialog.FileName);
        }

        private void OnEditNameRequested(object? sender, EventArgs e)
        {
            if (DataContext is not ModDetailsPageViewModel details) return;
            if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.BeginBottomBarNameEdit(details.ModId, details.Name);
        }

        private void OnEditDescriptionRequested(object? sender, EventArgs e)
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
                case ModListRowAction.ExportMod:
                    shell?.BeginModExport(card.Mod.Guid, card.Mod.Name);
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
            => IsLoaded && RelationshipDropOverlay.ContainsPoint(screenPoint);

        bool IModRelationshipDropTarget.ContainsElement(DependencyObject element)
            => IsAncestorOf(element);

        // 详情页的目标是整页而不是某个列表（把 Mod 拖到详情页 = 设为该主体的选项），
        // 所以它继续自己实现 IModRelationshipDropTarget；叠层、淡入淡出和命中
        // 由 DropHintOverlay 统一负责。
        void IModRelationshipDropTarget.SetRelationshipDropOverlay(ModRelationshipDragPayload payload, bool visible)
        {
            if (visible)
            {
                var hostName = DataContext is ModDetailsPageViewModel details
                    ? details.Name
                    : "当前 Mod";
                RelationshipDropOverlay.Show(new ModListDropHint(
                    $"释放以设为“{hostName}”的选项",
                    $"将 {payload.SelectedKeys.Count} 个 Mod 挂载到该主体下"));
                return;
            }

            RelationshipDropOverlay.Hide();
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

    }
}
