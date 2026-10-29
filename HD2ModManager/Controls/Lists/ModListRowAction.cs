using System.Windows.Media;

namespace HD2ModManager.Controls.Lists;

// 行内动作的"身份"。宿主页面只用 flags 声明"这一版列表允许哪些动作"，
// 具体画成什么按钮、多宽、什么顺序、危险操作用什么颜色，全部由行模板决定。
[Flags]
public enum ModListRowAction
{
    None = 0,
    Rename = 1 << 0,
    Details = 1 << 1,
    AddToProfile = 1 << 2,
    OpenFolder = 1 << 3,
    MoveUp = 1 << 4,
    MoveDown = 1 << 5,
    RemoveFromProfile = 1 << 6,
    DeleteFromLibrary = 1 << 7,
    ToggleDecoration = 1 << 8,
    ArmorAssign = 1 << 9,
    ArmorRelease = 1 << 10,
    ArmorOpenDetails = 1 << 11,
    ArmorEditComponents = 1 << 12,
    ArmorDeleteCustomModel = 1 << 13,
    ArmorRenameCustomModel = 1 << 14,
    ExportMod = 1 << 15,
}

// 行级动作抑制。某些动作对特定行没有意义（自定义素体不能"解除"、非装饰不能
// "启用装饰"）。这件事行自己最清楚；以前是模板按属性名去猜 IsCustomModel /
// CanJoinProfile / IsAttachable，行类型改名或缺失时只会静默失效。
// 可选接口：不实现就等于"没有要抑制的动作"。
public interface IModListRowActionFilter
{
    ModListRowAction HiddenActions { get; }
}

// 渲染单元：面板把宿主 flags + 行级抑制 + 自己的文案配置合成为这一项。
// 行模板只认这个 record，不再认枚举。
public sealed record ModListRowActionItem(
    ModListRowAction Action,
    Geometry? Icon,
    string ToolTip,
    double StrokeThickness,
    bool IsDestructive);

// 宿主给同一个动作配了更具体的文案时（"解除视觉对象" vs "解除映射目标"）
// 优先用宿主的。由 ModListPanel 实现，避免控件层反向依赖 Views。
public interface IRowActionToolTipSource
{
    string? GetRowActionToolTip(ModListRowAction action);
}
