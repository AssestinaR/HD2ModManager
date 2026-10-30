using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HD2ModManager.Controls.Trees;

/// <summary>
/// 行点击展开：这套树没有小三角，<b>点整行就展开/收起</b>。
/// <para>
/// <b>为什么不是双击。</b>树里最常用的一行是"部位"，而双击展开要先选中再点第二下——
/// 用户的心智是"点一下看看里面有什么"。双击还要求两次点击之间不移动鼠标，
/// 手抖一下就变成"选了两行、什么都没展开"。
/// </para>
/// <para>
/// <b>挂在 TreeViewItem 上而不是 TreeView 上。</b>行容器是模板生成出来的，页面拿不到它们；
/// 附加属性写在 <c>ItemContainerStyle</c> 的 Setter 里，每一行生成时自动生效：
/// <code>
/// &lt;Style TargetType="TreeViewItem" BasedOn="{StaticResource WardrobeTreeItemStyleBase}"&gt;
///     &lt;Setter Property="tree:TreeRowExpand.OnSingleClick" Value="True"/&gt;
/// &lt;/Style&gt;
/// </code>
/// </para>
/// <para>
/// 两个必须挡掉的情况（否则"点一下"会顺手改掉别的东西）：
/// </para>
/// <list type="number">
/// <item>点在<b>开关</b>上不算展开——连点两下开关会把子树展开，那不是用户要的；</item>
/// <item>点的是<b>子行</b>时父行也会收到事件（沿祖先链冒泡），只认最内层那一行，
/// 否则点一个 section 会把它的整个部位收起来。</item>
/// </list>
/// <para>
/// 叶子行（section）没有可展开的内容，跳过——否则会展开出一块只有竖线的空白。
/// </para>
/// </summary>
public static class TreeRowExpand
{
	/// <summary>设为 <c>true</c> 时，点这一行的任意位置（开关除外）都会展开/收起它。</summary>
	public static readonly DependencyProperty OnSingleClickProperty = DependencyProperty.RegisterAttached(
		"OnSingleClick",
		typeof(bool),
		typeof(TreeRowExpand),
		new PropertyMetadata(false, OnOnSingleClickChanged));

	public static void SetOnSingleClick(DependencyObject element, bool value)
		=> element.SetValue(OnSingleClickProperty, value);

	public static bool GetOnSingleClick(DependencyObject element)
		=> (bool)element.GetValue(OnSingleClickProperty);

	private static void OnOnSingleClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not TreeViewItem item) return;
		// 先摘再挂：容器会被复用（虚拟化），重复挂会一次点击走两遍。
		item.PreviewMouseLeftButtonUp -= OnRowClick;
		if ((bool)e.NewValue)
			item.PreviewMouseLeftButtonUp += OnRowClick;
	}

	private static void OnRowClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is not TreeViewItem item) return;
		if (e.OriginalSource is not DependencyObject source) return;
		// 只认最内层那一行：子行的事件会冒泡到所有祖先行。
		if (!ReferenceEquals(FindInnermostTreeViewItem(source), item)) return;
		// 开关上的点击不是"展开"。
		if (FindAncestor<CheckBox>(source) is not null) return;
		// 叶子行没有可展开的内容。
		if (!item.HasItems) return;

		item.IsExpanded = !item.IsExpanded;
		// 吃掉事件：不然同一行的其它处理器还会按"普通点击"再处理一次。
		e.Handled = true;
	}

	private static TreeViewItem? FindInnermostTreeViewItem(DependencyObject source)
	{
		for (var current = source; current is not null; current = Parent(current))
		{
			if (current is TreeViewItem item)
				return item;
		}

		return null;
	}

	private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
	{
		for (var current = source; current is not null; current = Parent(current))
		{
			if (current is T match)
				return match;
		}

		return null;
	}

	/// <summary>
	/// 父级要走可视树；已经走到可视树之外（例如 ContentPresenter 的 Content 对象）时退回逻辑树，
	/// 否则从"模板里生成的内容"往上找会在中途断掉。
	/// </summary>
	private static DependencyObject? Parent(DependencyObject current)
		=> current is Visual or System.Windows.Media.Media3D.Visual3D
			? VisualTreeHelper.GetParent(current)
			: LogicalTreeHelper.GetParent(current);
}
