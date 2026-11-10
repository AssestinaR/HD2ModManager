using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HD2ModManager.Controls.Actions;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Services;

/// <summary>
/// 把一个页面此刻"用户能看到的动作"导成稳定文本，用作重构的行为基线。
/// <para>
/// <b>为什么需要它。</b>本仓库没有 UI 测试项目（主工程 <c>net10.0-windows</c> + <c>UseWPF</c>，
/// <c>HD2ModCore.Tests</c> 是 <c>net8.0</c>），"改完之后界面有没有变"在编译期无法证明。
/// 于是每批重构前后各跑一次，**diff 必须为空**——这是"零行为改动"唯一可判定的口径。
/// </para>
/// <para>
/// <b>导什么。</b>只导<b>渲染层实际会读的东西</b>：动作是否出现、文案、图标键、顺序，
/// 以及命令此刻能不能执行。不导颜色、尺寸这类纯外观量。
/// </para>
/// <para>
/// <b>一处刻意保留的重复。</b><c>IsVisible</c> 的取值规则（按 <c>PageActionKind</c> 分派到页面谓词）
/// 与 <c>ModDetailsPageViewModel.SyncActionVisibility</c> 是同一张表。P2 会把两边合并成一份声明表；
/// 在那之前这里的重复是<b>有意的</b>：基线要能在**不改产品代码**的前提下记录当前行为，
/// 如果基线读的是产品代码自己算出来的结果，产品代码一变基线就跟着变，diff 永远为空，等于没测。
/// </para>
/// </summary>
public static class ActionBarBaseline
{
	/// <summary>一段基线文本：每行 <c>区域键 | 字段=值 | ...</c>，行序即渲染序。</summary>
	public static string CaptureModDetails(ModDetailsPageViewModel page)
	{
		ArgumentNullException.ThrowIfNull(page);
		return Capture(page.PageActions, ActionVisibleInModDetails(page));
	}

	/// <summary>底栏的一行：<paramref name="rows"/> 是"键 + 该键下渲染的东西"的有序集合。</summary>
	public static string CaptureRows(IEnumerable<(string Key, IEnumerable<KeyValuePair<string, string>> Fields)> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);
		var builder = new StringBuilder();
		foreach (var (key, fields) in rows)
		{
			builder.Append(key);
			foreach (var pair in fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
				builder.Append(" | ").Append(pair.Key).Append('=').Append(pair.Value);
			builder.Append('\n');
		}

		return builder.ToString();
	}

	private static string Capture(IEnumerable<PageActionViewModel> actions, Func<PageActionKind, bool> isVisible)
	{
		ArgumentNullException.ThrowIfNull(actions);
		ArgumentNullException.ThrowIfNull(isVisible);
		var builder = new StringBuilder();
		// 渲染层按 Order 升序铺（FloatingActionCluster 的 _view.SortDescriptions），基线照抄这个顺序，
		// 这样"顺序变了"也会被 diff 抓到。
		foreach (var action in actions.OrderBy(action => action.Order))
		{
			builder.Append(action.Kind)
				.Append(" | visible=").Append(isVisible(action.Kind) ? "1" : "0")
				.Append(" | label=").Append(action.Label)
				.Append(" | order=").Append(action.Order)
				// 命令此刻能不能点：WPF 的 IsEnabledCore 就是 IsEnabled && CanExecute，
				// 而 IsEnabled 在本仓库恒为 true，所以这一项等价于"按钮可不可点"。
				.Append(" | canExecute=").Append(SafeCanExecute(action.Command) ? "1" : "0")
				.Append('\n');
		}

		return builder.ToString();
	}

	/// <summary>
	/// 把当前可见性判据固化成一张表，供基线使用。
	/// <para>P2 合并声明表之后，这个方法与 <c>SyncActionVisibility</c> 一起消失。</para>
	/// </summary>
	private static Func<PageActionKind, bool> ActionVisibleInModDetails(ModDetailsPageViewModel page) => kind => kind switch
	{
		// 与 ModDetailsPageViewModel.Actions.cs 的 SyncActionVisibility 逐条对应（2026-11-10 的状态）。
		PageActionKind.Table => page.CanUsePatchTools,
		PageActionKind.RebuildVpk => page.HasRebuildSource,
		PageActionKind.Fix or PageActionKind.CrossArmor or PageActionKind.Compact
			or PageActionKind.Package or PageActionKind.Helmet or PageActionKind.Merge
			=> page.ShowPatchTools,
		PageActionKind.CustomModel => page.CanConvertToSectional,
		// PatchRebuild 的判据是 IsDecoration（恒 false，见 ModLibraryService.cs:1390-1392），
		// 即它在本基线里永远是 visible=0——这正是它被判为"死动作"的证据，保留不删是为了让 diff 说话。
		PageActionKind.PatchRebuild => page.IsDecoration,
		PageActionKind.CloneVariant => page.CanCloneVariant,
		_ => true,
	};

	/// <summary>命令的 <c>CanExecute</c> 可能抛（谓词里解析节点失败等），基线不该因此中断。</summary>
	private static bool SafeCanExecute(System.Windows.Input.ICommand command)
	{
		try
		{
			return command.CanExecute(null);
		}
		catch (Exception)
		{
			return false;
		}
	}
}
