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
        PageActions.Add(new PageActionViewModel(PageActionKind.Helmet, "头盔合并到盔甲并输出", PlanHelmetToArmorMergeCommand, order: 50));
        PageActions.Add(new PageActionViewModel(PageActionKind.Merge, HelmetMergeToggleText, ToggleHelmetToArmorMergeCommand, order: 51));
        // 名字改成「生成sectional版」：产出不再只是一具自定义素体，而是**带 section 组的**产物
        // （7 标准槽 × 身形，每块几何能在游戏里单独开关），生成完直接进库。
        // 原来那套"只转自定义素体"的用法已被它完全覆盖，所以不再另留入口。
        PageActions.Add(new PageActionViewModel(PageActionKind.CustomModel, "生成sectional版", ConvertToCustomModelCommand, tone: PageActionTone.Primary, order: 60));
        // 「用来源重构建」从 VPK 动作卡移到这里：它是 patch 级动作，而动作卡留给"这个 Mod 有什么"。
        // 只有**根上有来源 .vpk** 的 Mod 才有它 —— 转出来的分件模组没有来源包，重构建无从谈起。
        PageActions.Add(new PageActionViewModel(PageActionKind.RebuildVpk, "用来源重构建", RebuildVpkCommand, order: 40));
        PageActions.Add(new PageActionViewModel(PageActionKind.PatchRebuild, "重构 patch", RebuildSameKeyCommand, order: 71));
        // 复制成独立命名空间的变体：两套外观能同时部署。只对 sectional / vpk2patch 类有意义。
        PageActions.Add(new PageActionViewModel(PageActionKind.CloneVariant, "复制为变体", CloneVariantCommand, order: 72));

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
                // 重构建对两档 sectional 系列都成立：有来源 .vpk 走 VPK 转换机，
                // 有转换依据（sectional-source.json）走 legacy2sectional。
                PageActionKind.RebuildVpk => HasRebuildSource,
                PageActionKind.Fix or PageActionKind.CrossArmor or PageActionKind.Compact
                    or PageActionKind.Package or PageActionKind.Helmet or PageActionKind.Merge
                    => ShowPatchTools,
                // 「生成sectional版」的来源集是 legacy 系列，但**宿主可以是逻辑容器**
                // （把一个系列包起来、成员都是 legacy 的那种）。所以它的判据不是"自己有 patch"。
                // 容器里到底有没有可用来源，由底栏在点击时判断——这里只负责让按钮出现。
                PageActionKind.CustomModel => ShowSectionalConversion,
                PageActionKind.PatchRebuild => IsDecoration,
                PageActionKind.CloneVariant => CanCloneVariant,
                _ => true,
            };

            // 头盔合并是个开关：同一个动作两种文案。
            if (action.Kind == PageActionKind.Merge) action.Label = HelmetMergeToggleText;
        }
    }
}
