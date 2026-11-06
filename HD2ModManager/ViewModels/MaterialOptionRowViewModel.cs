using System;
using HD2ModCore.Infrastructure;
using HD2ModManager.Controls.Lists;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 材质选项的一行。**不是 Mod**：它是主 Mod 基底目录里的一个 <c>patch_N</c>，
/// 所以这里没有"删除"和"移出列表"——patch 还在目录里，它就在列表里。
/// <para>
/// 这一行只负责"长什么样"，读写全部转给 <see cref="MaterialOptionSettings"/>：
/// 状态存在 Mod 自己的结构文件与设置文件里，不在 VM 里留下第二份真相。
/// 每次写成功之后由详情页 VM 统一重建生效层（<c>OverwriteMaterializer</c>）。
/// </para>
/// <para>
/// <b>启用/停用是一个行内动作，不是开关。</b>和详情页那张「可为该 Mod 启用的附加选项」
/// 一致：电源图标的动作胶囊 + 行内的「已启用/未启用」文字。行模板是全应用共用的一份，
/// 不该为了某一张卡多画一种状态控件。
/// </para>
/// </summary>
public sealed class MaterialOptionRowViewModel : BaseViewModel,
    IModListKeyed,
    IModListInternalReorderable,
    IModListRowActionFilter
{
    private readonly ModDetailsPageViewModel owner;
    private MaterialOptionView view;
    private string? imagePath;
    private bool enabled;

    public MaterialOptionRowViewModel(ModDetailsPageViewModel owner, MaterialOptionView view, string? imagePath)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.view = view;
        enabled = view.Enabled;
        ImagePath = imagePath;
    }

    /// <summary>结构文件里的序号。改名/改备注/换配图都靠它定位。</summary>
    public int PatchIndex => view.PatchIndex;

    /// <summary>启用集合用的文件名（<c>&lt;archive&gt;.patch_2</c>）。</summary>
    public string PatchFileName => view.PatchFileName;

    /// <summary>过渡动画认人的稳定键。patch 文件名在同一个 Mod 里唯一，且不随改名变化。</summary>
    public string ListKey => view.PatchFileName;

    /// <summary>显示名：用户改过就用它，否则退回"材质 N"。</summary>
    public string Name => string.IsNullOrWhiteSpace(view.Name) ? $"材质 {view.PatchIndex}" : view.Name!;

    /// <summary>备注。搜索也会查它。</summary>
    public string? Notes => view.Notes;

    /// <summary>配图绝对路径；没有配图时为 <c>null</c>，行模板会退回占位图。</summary>
    public string? ImagePath
    {
        get => imagePath;
        private set => SetField(ref imagePath, value);
    }

    /// <summary>只读给界面看；改它一律走 <see cref="Toggle"/>，那里才会落盘。</summary>
    public bool IsEnabled => enabled;

    /// <summary>顺序就是部署顺序，拖动才有意义。</summary>
    public bool CanInternalReorder => true;

    /// <summary>
    /// 没有删除、没有移出、没有加进配置——这些动作对"主 Mod 的一部分"没有意义。
    /// 文档也点明了不要出现它们。
    /// </summary>
    public ModListRowAction HiddenActions =>
        ModListRowAction.DeleteFromLibrary
        | ModListRowAction.RemoveFromProfile
        | ModListRowAction.AddToProfile
        | ModListRowAction.ToggleDecoration
        | ModListRowAction.ExportMod;

    public ModListRowPresentation RowPresentation => new(
        // 内置 = 模型自带的那一份，不是折并进来的材质包。
        TypeLabel: IsBuiltIn ? "内置" : "材质",
        TypeTone: ModListRowTone.Muted,
        ActivationLabel: enabled ? "已启用" : "未启用",
        ActivationTone: enabled ? ModListRowTone.Success : ModListRowTone.Muted,
        OrderLabel: $"顺序 {view.Order + 1}",
        Summary: string.IsNullOrWhiteSpace(view.Notes) ? null : view.Notes);

    private bool IsBuiltIn
        => string.Equals(view.Name, HD2ModCore.Domain.SectionalPatchManifest.BuiltInMaterialOptionName,
            StringComparison.Ordinal);

    /// <summary>点电源图标：翻转并交给宿主落盘。</summary>
    internal void Toggle() => owner.SetMaterialOptionEnabled(this, !enabled);

    /// <summary>把显示状态改成目标值。不落盘——落盘在宿主那里，失败时由宿主推回。</summary>
    internal void SetEnabledState(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(RowPresentation));
    }

    /// <summary>写回成功之后用新的一份视图替换，避免列表里显示的是旧名字/旧备注。</summary>
    internal void Update(MaterialOptionView updated, string? updatedImagePath)
    {
        view = updated;
        SetEnabledState(updated.Enabled);
        ImagePath = updatedImagePath;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(RowPresentation));
    }
}
