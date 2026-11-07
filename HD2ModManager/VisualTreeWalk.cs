using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace HD2ModManager;

/// <summary>
/// 视觉树向上遍历的唯一入口。
///
/// 为什么不能直接调用 <see cref="VisualTreeHelper.GetParent"/>：输入事件的
/// <c>OriginalSource</c> 在点中「TextBlock 里的 Run」时会是
/// <see cref="System.Windows.Documents.Run"/>——它是 <c>ContentElement</c>，不是 <c>Visual</c>。
/// <c>VisualTreeHelper.GetParent</c> 对它抛 <see cref="InvalidOperationException"/>
/// （“System.Windows.Documents.Run”不是 Visual 或 Visual3D。），异常一路冒到
/// App.DispatcherUnhandledException，用户看到的就是「管理器遇到未处理错误」。
///
/// 主页帮助文字里就有 &lt;Run&gt;（“已激活”那句），点一下就踩到；护甲页表格单元格里也有。
/// 对非 Visual 的节点改走逻辑树：Run 的逻辑父级正是它所在的 TextBlock，再往上的每一级
/// 又都是 Visual，于是整条链路都能走完。
/// </summary>
internal static class VisualTreeWalk
{
	/// <summary>
	/// 取父节点：纯视觉节点走视觉树，<c>ContentElement</c>（Run / Hyperlink 等）走逻辑树。
	/// </summary>
	internal static DependencyObject? Parent(DependencyObject? node)
		=> node switch
		{
			null => null,
			Visual or Visual3D => VisualTreeHelper.GetParent(node),
			_ => LogicalTreeHelper.GetParent(node),
		};
}
