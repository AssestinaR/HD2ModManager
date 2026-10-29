namespace HD2ModManager.Controls.Lists;

// 一个行内动作"长什么样"的完整声明：图标几何资源键、默认提示、线宽、是否危险。
// 线宽属于图形本身（齿轮 1.1 和删除 1.4 不能统一），所以留在这里而不是模板里。
// 列表顺序就是按钮在 pill 里的顺序。
internal sealed record ModListRowActionDefinition(
    ModListRowAction Action,
    string IconKey,
    string ToolTip,
    double StrokeThickness = 1.5,
    bool IsDestructive = false);

internal static class ModListRowActionCatalog
{
    public static IReadOnlyList<ModListRowActionDefinition> All { get; } =
    [
        new(ModListRowAction.Rename, "RowIcon.Rename", "重命名", 1.4),
        new(ModListRowAction.Details, "RowIcon.Details", "查看详情", 1.5),
        new(ModListRowAction.ArmorAssign, "RowIcon.ArmorAssign", "加入视觉来源或其映射", 1.6),
        new(ModListRowAction.ArmorRelease, "RowIcon.ArmorRelease", "解除视觉来源或映射", 1.6),
        new(ModListRowAction.ArmorDeleteCustomModel, "RowIcon.ArmorDeleteCustomModel", "删除自定义素体", 1.4, IsDestructive: true),
        new(ModListRowAction.ArmorRenameCustomModel, "RowIcon.ArmorRenameCustomModel", "重命名自定义素体", 1.4),
        new(ModListRowAction.ArmorOpenDetails, "RowIcon.ArmorOpenDetails", "查看映射目标", 1.5),
        new(ModListRowAction.ArmorEditComponents, "RowIcon.ArmorEditComponents", "编辑视觉组件", 1.1),
        new(ModListRowAction.AddToProfile, "RowIcon.AddToProfile", "加入正在编辑的配置", 1.6),
        new(ModListRowAction.ToggleDecoration, "RowIcon.ToggleDecoration", "启用或禁用装饰", 1.5),
        new(ModListRowAction.ExportMod, "RowIcon.ExportMod", "导出为文件夹结构 zip", 1.5),
        new(ModListRowAction.OpenFolder, "RowIcon.OpenFolder", "打开文件夹", 1.4),
        new(ModListRowAction.MoveUp, "RowIcon.MoveUp", "上移", 1.6),
        new(ModListRowAction.MoveDown, "RowIcon.MoveDown", "下移", 1.6),
        new(ModListRowAction.RemoveFromProfile, "RowIcon.RemoveFromProfile", "从配置移除", 1.8, IsDestructive: true),
        new(ModListRowAction.DeleteFromLibrary, "RowIcon.DeleteFromLibrary", "从模组库彻底删除", 1.4, IsDestructive: true),
    ];
}
