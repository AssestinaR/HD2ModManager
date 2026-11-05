using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using HD2ModManager.Controls.Trees;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class SectionalEditorPageView : UserControl
{
	public SectionalEditorPageView()
	{
		InitializeComponent();
	}

	/// <summary>
	/// 树的多选结果写回源详情页 VM —— 组、选中、新建组都在它身上，
	/// 这一页只是换个布局呈现（见 <see cref="SectionalEditorPageViewModel"/> 的注释）。
	/// </summary>
	private void OnTreeSelectionChanged(object? sender, IReadOnlyList<ITreeSurfaceNode> nodes)
	{
		if (DataContext is not SectionalEditorPageViewModel page) return;
		page.Source.SelectedVpkTreeNodes =
			[.. nodes.OfType<WardrobeTreeNodeViewModel>().Select(item => item.Node)];
	}
}
