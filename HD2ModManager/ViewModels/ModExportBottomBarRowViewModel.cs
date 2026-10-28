namespace HD2ModManager.ViewModels;

/// <summary>导出底栏的两行各是什么。</summary>
public enum ModExportBottomBarRowKind
{
	/// <summary>路径输入框 + 浏览 + 导出。</summary>
	Target,

	/// <summary>"是否导出 Overwrite 版" + 取消。</summary>
	Options,
}

// 行只是操作的视觉投影：状态与命令都在 ModExportBottomBarViewModel 上。
public sealed record ModExportBottomBarRowViewModel(
	ModExportBottomBarViewModel Operation,
	ModExportBottomBarRowKind Kind)
{
	public bool IsTarget => Kind == ModExportBottomBarRowKind.Target;

	public bool IsOptions => Kind == ModExportBottomBarRowKind.Options;
}
