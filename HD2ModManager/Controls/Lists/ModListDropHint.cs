namespace HD2ModManager.Controls.Lists;

// 拖放叠层的内容。和卡片、空状态同一套约定：逻辑层只给"说给用户听的话"，
// 尺寸、透明度、动画曲线、命中区全部由控件层决定。
public sealed record ModListDropHint(string Title, string? Detail = null);
