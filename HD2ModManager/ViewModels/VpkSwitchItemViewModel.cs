using HD2ModCore.Domain;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 组件卡片里的一个开关：<c>猫耳</c>、<c>披风</c>……
/// <para>
/// <b>它为什么不是树上的一个节点。</b>源侧的开关是<b>材质层</b>的（装一份 <c>$no_draw</c> 覆盖），
/// 翻到 HD2 就变成"关掉某几个 unit 的某几节"。这个集合与树上任何一层都不重合：
/// 一个开关可能跨多个 unit，一个 unit 也可能被多个开关各关一部分。硬塞进树里会让
/// "这个节点到底是什么"失去意义，所以单列一行、按掩码写状态。
/// </para>
/// <para>
/// 开关的勾选<b>不是</b>独立状态：它就是"这些 section 有没有全被关掉"的另一种说法。
/// 所以它和树上的勾选永远同步（同一个 <see cref="VpkWardrobeState"/>），
/// 不需要第二份持久化数据——这一点是刻意保持的。
/// </para>
/// </summary>
public sealed class VpkSwitchItemViewModel : BaseViewModel
{
	private readonly Func<VpkComponentSwitchEntry, bool> read;
	private readonly Action<VpkComponentSwitchEntry, bool> write;

	public VpkSwitchItemViewModel(
		VpkComponentSwitchEntry entry,
		Func<VpkComponentSwitchEntry, bool> read,
		Action<VpkComponentSwitchEntry, bool> write)
	{
		Entry = entry ?? throw new ArgumentNullException(nameof(entry));
		this.read = read ?? throw new ArgumentNullException(nameof(read));
		this.write = write ?? throw new ArgumentNullException(nameof(write));
	}

	public VpkComponentSwitchEntry Entry { get; }

	/// <summary>开关名，来自源侧 addoninfo 的方括号（<c>[猫耳关]</c> → <c>猫耳</c>）。</summary>
	public string Name => Entry.Name;

	/// <summary>副标题：关掉几块几何 + 它藏的材质名。排查"关了没效果"时先看这里。</summary>
	public string Detail
		=> $"关掉 {Entry.SectionCount} 块 · {string.Join("、", Entry.Materials)}";


	public bool IsEnabled
	{
		get => read(Entry);
		set
		{
			if (value == IsEnabled)
				return;
			write(Entry, value);
			OnPropertyChanged();
		}
	}

	/// <summary>状态被别处改了（例如树上点了同一批 section），通知界面重画。</summary>
	internal void RaiseEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));
}
