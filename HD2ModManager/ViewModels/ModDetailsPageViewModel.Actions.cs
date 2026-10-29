using System.Windows.Input;
using HD2ModManager.Controls.Actions;

namespace HD2ModManager.ViewModels;

// 详情页的动作声明。
//
// 以前这些动作是页面 XAML 里 5 行手写按钮（4 个 WrapPanel + 3 个 Click 后置处理器），
// 现在只声明"是什么动作、什么时候可见、点了做什么"，浮动操作簇负责画。
// 可见性用 PageActionViewModel.IsVisible 表达——集合只注册一次，不增删。
public partial class ModDetailsPageViewModel
{
    private bool _actionsRegistered;

    // 这三个动作需要页面（而不是逻辑层）来做：文件对话框、底栏编辑器。
    // 逻辑层只发请求，页面接住。
    private ICommand? _requestEditImageCommand;
    private ICommand? _requestEditNameCommand;
    private ICommand? _requestEditDescriptionCommand;

    public ICommand RequestEditImageCommand => _requestEditImageCommand ??=
        new RelayCommand(_ => EditImageRequested?.Invoke(this, EventArgs.Empty));
    public ICommand RequestEditNameCommand => _requestEditNameCommand ??=
        new RelayCommand(_ => EditNameRequested?.Invoke(this, EventArgs.Empty));
    public ICommand RequestEditDescriptionCommand => _requestEditDescriptionCommand ??=
        new RelayCommand(_ => EditDescriptionRequested?.Invoke(this, EventArgs.Empty));

    public event EventHandler? EditImageRequested;
    public event EventHandler? EditNameRequested;
    public event EventHandler? EditDescriptionRequested;

    private void RegisterPageActions()
    {
        if (_actionsRegistered) return;
        _actionsRegistered = true;

        PageActions.Add(new PageActionViewModel(PageActionKind.OpenFolder, "打开文件夹", OpenFolderCommand, order: 10));
        PageActions.Add(new PageActionViewModel(PageActionKind.Image, "修改图像", RequestEditImageCommand, order: 11));
        PageActions.Add(new PageActionViewModel(PageActionKind.EditName, "修改名称", RequestEditNameCommand, order: 12));
        PageActions.Add(new PageActionViewModel(PageActionKind.EditDescription, "修改备注", RequestEditDescriptionCommand, order: 13));
        PageActions.Add(new PageActionViewModel(PageActionKind.Table, "详情表格", OpenAdvancedDetailsCommand, order: 20));
        PageActions.Add(new PageActionViewModel(PageActionKind.Fix, "修复 patch", RebuildSameKeyCommand, tone: PageActionTone.Primary, order: 30));
        PageActions.Add(new PageActionViewModel(PageActionKind.CrossArmor, "生成替换其他护甲的模组", PlanCrossArmorTransferCommand, order: 31));
        PageActions.Add(new PageActionViewModel(PageActionKind.Compact, "规整护甲 patch 包", NormalizeArmorCommand, order: 32));
        PageActions.Add(new PageActionViewModel(PageActionKind.Package, "Patch 资源重打包", PatchResourceRepackagingCommand, order: 33));
        PageActions.Add(new PageActionViewModel(PageActionKind.BatchDecoration, "批量生成装饰（以主体 Mod）", PlanBatchDecorationCommand, order: 40));
        PageActions.Add(new PageActionViewModel(PageActionKind.DecorationPlan, "生成装饰 Mod（以来源 Mod）", PlanDecorationCommand, order: 41));
        PageActions.Add(new PageActionViewModel(PageActionKind.Helmet, "头盔合并到盔甲并输出", PlanHelmetToArmorMergeCommand, order: 50));
        PageActions.Add(new PageActionViewModel(PageActionKind.Merge, HelmetMergeToggleText, ToggleHelmetToArmorMergeCommand, order: 51));
        PageActions.Add(new PageActionViewModel(PageActionKind.CustomModel, "一键转为自定义素体", ConvertToCustomModelCommand, tone: PageActionTone.Danger, order: 60));
        PageActions.Add(new PageActionViewModel(PageActionKind.EditDecoration, "编辑装饰 Mod", EditDecorationCommand, order: 70));
        PageActions.Add(new PageActionViewModel(PageActionKind.PatchRebuild, "重构 patch", RebuildSameKeyCommand, order: 71));

        SyncActionVisibility();
        // 状态一变就重算可见性与文案；动作集合本身不动，所以不会触发重排动画。
        PropertyChanged += (_, _) => SyncActionVisibility();
    }

    private void SyncActionVisibility()
    {
        if (!_actionsRegistered) return;

        foreach (var action in PageActions)
        {
            action.IsVisible = action.Kind switch
            {
                PageActionKind.Table => CanUsePatchTools,
                PageActionKind.Fix or PageActionKind.CrossArmor or PageActionKind.Compact
                    or PageActionKind.Package or PageActionKind.Helmet or PageActionKind.Merge
                    or PageActionKind.CustomModel => ShowPatchSettingsCard,
                PageActionKind.BatchDecoration or PageActionKind.DecorationPlan => CanShowDecorationPlan,
                PageActionKind.EditDecoration or PageActionKind.PatchRebuild => IsDecoration,
                _ => true,
            };

            // 头盔合并是个开关：同一个动作两种文案。
            if (action.Kind == PageActionKind.Merge) action.Label = HelmetMergeToggleText;
        }
    }
}
