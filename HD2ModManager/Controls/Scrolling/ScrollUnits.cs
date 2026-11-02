using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace HD2ModManager.Controls.Scrolling;

/// <summary>
/// 判断一个 <see cref="ScrollViewer"/> 的 <c>VerticalOffset</c> 单位到底是"像素"还是"项"。
/// <para>
/// <b>为什么不能只看 <c>CanContentScroll</c>。</b>那个属性只是"滚动由内容宿主（IScrollInfo）
/// 接管"，并不说明单位：WPF 4.5 之后虚拟化面板多了一个 `ScrollUnit`，
/// `CanContentScroll=true` + `VirtualizingPanel.ScrollUnit=Pixel` 的组合里偏移量仍然是**像素**。
/// 本项目的 <c>ModListBox</c>（App.xaml）正是这么配的——按 <c>CanContentScroll</c> 判断会把它
/// 当成"项"，于是滚轮一格只走 1~3 像素，实机表现就是"卡片滚轮特别慢、几乎不动"。
/// </para>
/// <para>
/// 真正的单位由**内容面板**回答：
/// <list type="bullet">
/// <item>虚拟化面板（VirtualizingStackPanel 等）→ <see cref="VirtualizingPanel.GetScrollUnit"/>；</item>
/// <item>普通面板（StackPanel / WrapPanel 之类）→ <c>CanContentScroll=true</c> 时每个子元素算 1 项；</item>
/// <item><c>CanContentScroll=false</c> → 纯像素。</item>
/// </list>
/// </para>
/// </summary>
internal static class ScrollUnits
{
	/// <summary>偏移量按"第几项"计（而不是像素）。</summary>
	public static bool UsesItemOffsets(ScrollViewer viewer)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		if (!viewer.CanContentScroll) return false;

		// **ScrollUnit 挂在拥有者 ItemsControl 上，不在面板上。**
		// 实测：`ListBox` 上设 `VirtualizingPanel.ScrollUnit=Pixel` 之后，
		// 它内部的 VirtualizingStackPanel 读出来仍然是默认的 `Item`（WPF 由 ItemsControl
		// 自己把单位喂给面板，不写回面板的 DP）。所以判据要从 viewer 往上找最近的 ItemsControl。
		for (var current = GetParent(viewer); current is not null; current = GetParent(current))
		{
			if (current is ItemsControl owner)
				return VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Item;
		}

		var panel = FindVirtualizingPanel(viewer);
		if (panel is not null) return VirtualizingPanel.GetScrollUnit(panel) == ScrollUnit.Item;

		// CanContentScroll=true 但既没有 ItemsControl 也没有虚拟化面板（例如下拉弹层里的
		// 普通 StackPanel）：这时偏移量按"项"计。
		return true;
	}

	private static DependencyObject? GetParent(DependencyObject current)
		=> current is Visual or Visual3D
			? VisualTreeHelper.GetParent(current)
			: LogicalTreeHelper.GetParent(current);

	/// <summary>一行（一项）在当前 viewer 里有多高；拿不到就返回 0。</summary>
	public static double MeasureRowHeight(ScrollViewer viewer)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		return FindDescendant<ComboBoxItem>(viewer)?.ActualHeight
			?? FindDescendant<ListBoxItem>(viewer)?.ActualHeight
			?? FindDescendant<ContentPresenter>(viewer)?.ActualHeight
			?? 0d;
	}

	private static VirtualizingPanel? FindVirtualizingPanel(DependencyObject root)
	{
		var count = VisualTreeHelper.GetChildrenCount(root);
		for (var index = 0; index < count; index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			if (child is VirtualizingPanel panel) return panel;
			if (FindVirtualizingPanel(child) is { } nested) return nested;
		}

		return null;
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
}
