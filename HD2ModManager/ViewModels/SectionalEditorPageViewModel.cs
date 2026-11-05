using System;
using System.Windows.Input;
using HD2ModManager.Controls.Actions;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 分件模组（sectional）的 section 编辑页：左边 section 组，右边 section 树。
/// <para>
/// <b>为什么不复制一份编辑逻辑。</b>组表、树、勾选、防抖保存、就地烘焙全都在
/// <see cref="ModDetailsPageViewModel"/> 里，而且和"目标护甲 / 头身合并 / 应用"共享同一份
/// <c>wardrobe.json</c> 状态。复制一份必然漂移，而漂移的表现是"详情页勾的和这里勾的不是一回事"。
/// </para>
/// <para>
/// 所以这一页只做<b>布局</b>：它持着源详情页当数据源，XAML 直接绑那上面的属性。
/// 页面切换时源 VM 被这个页面引用着，不会被回收；返回详情时再把源 VM 放回去。
/// </para>
/// </summary>
public sealed class SectionalEditorPageViewModel : PageViewModel
{
	private readonly Action _backToDetails;

	public SectionalEditorPageViewModel(ModDetailsPageViewModel source, Action backToDetails)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		_backToDetails = backToDetails ?? throw new ArgumentNullException(nameof(backToDetails));
		Title = "分件编辑 · " + source.Name;
		// 返回入口放在动作区：这一页是"从详情进来"的，用户的第一反应就是回去。
		PageActions.Add(new PageActionViewModel(
			PageActionKind.Table,
			"返回详情",
			new RelayCommand(_ => _backToDetails()),
			tone: PageActionTone.Primary,
			order: 10));
	}

	/// <summary>数据源：详情页 VM。组、树、选中、新建组、保存全在它身上。</summary>
	public ModDetailsPageViewModel Source { get; }
}
