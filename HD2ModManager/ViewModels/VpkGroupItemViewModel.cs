using HD2ModCore.Domain;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 一个组在界面上的状态。<b>界面必须能区分这三种，否则会骗人。</b>
/// </summary>
public enum VpkGroupState
{
	/// <summary>成员都能在当前产物里找到，且没有被别的组覆盖——可以单独开关。</summary>
	Independent = 0,

	/// <summary>
	/// 有成员同时属于别的组。<b>不是错误，但必须说出来</b>：
	/// 关掉这个组会连带关掉另一个组的那一块，而界面上两个复选框看起来是独立的。
	/// </summary>
	Shared = 1,

	/// <summary>成员在当前产物里找不到（换过目标护甲、或重新转换过）。点了什么都不会发生。</summary>
	Stale = 2,
}

/// <summary>
/// 组列表里的一行：<c>猫耳</c>、<c>披风</c>……
/// <para>
/// <b>它为什么不是树上的一个节点。</b>组是<b>跨 unit</b> 的：一个组可能横跨多个 unit，
/// 一个 unit 也可能被多个组各覆盖一部分。硬塞进树里会让"这个节点到底是什么"失去意义，
/// 所以单列一行、按成员掩码写状态。
/// </para>
/// <para>
/// 组的勾选<b>不是</b>独立状态：它就是"这些 section 有没有全被关掉"的另一种说法。
/// 所以它和树上的勾选永远同步（同一个 <see cref="VpkWardrobeState"/>），
/// 不需要第二份持久化数据——这一点是刻意保持的。
/// </para>
/// </summary>
public sealed class VpkGroupItemViewModel : BaseViewModel
{
	private readonly Func<SectionGroup, bool> read;
	private readonly Action<SectionGroup, bool> write;
	private readonly Action<SectionGroup, string> rename;
	private SectionGroup entry;
	private VpkGroupState state;

	public VpkGroupItemViewModel(
		SectionGroup entry,
		VpkGroupState state,
		Func<SectionGroup, bool> read,
		Action<SectionGroup, bool> write,
		Action<SectionGroup, string> rename,
		Action<SectionGroup> delete)
	{
		this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
		this.state = state;
		this.read = read ?? throw new ArgumentNullException(nameof(read));
		this.write = write ?? throw new ArgumentNullException(nameof(write));
		this.rename = rename ?? throw new ArgumentNullException(nameof(rename));
		ArgumentNullException.ThrowIfNull(delete);
		DeleteCommand = new RelayCommand(_ => delete(Entry));
	}

	public SectionGroup Entry => entry;

	public VpkGroupState State => state;

	public bool IsStale => state == VpkGroupState.Stale;

	public bool IsShared => state == VpkGroupState.Shared;

	/// <summary>
	/// 组名。<b>可写</b>——改完名（输入框失焦）就落盘。
	/// 名字不参与身份（身份是 <see cref="SectionGroup.EffectiveId"/>），所以改名不影响勾选状态。
	/// </summary>
	public string Name
	{
		get => entry.Name;
		set
		{
			if (string.Equals(value, entry.Name, StringComparison.Ordinal))
				return;
			rename(Entry, value);
		}
	}

	/// <summary>副标题：覆盖几块几何 + 它来自哪。排查"关了没效果"时先看这里。</summary>
	public string Detail
	{
		get
		{
			var origin = entry.Source switch
			{
				SectionGroupSource.DonorUnit => "来自附加部件",
				SectionGroupSource.Manual => "手工建立",
				_ => "来自源侧开关包",
			};
			// 有具体来源名就写它：sectional 产物的组名本来就是部件名（`尾巴` / `大衣 健壮`），
			// 再补一句"来自「尾巴」"能直接回答"这条开关是谁带来的"。
			if (!string.IsNullOrWhiteSpace(entry.SourceVpk)
				&& !string.Equals(entry.SourceVpk, origin, StringComparison.Ordinal))
			{
				origin = $"来自「{entry.SourceVpk}」";
			}

			var materials = entry.Materials.Count == 0
				? string.Empty
				: " · " + string.Join("、", entry.Materials);
			return $"{entry.SectionCount} 块 · {origin}{materials}";
		}
	}

	/// <summary>状态提示。空字符串表示一切正常（界面据此隐藏那行小字）。</summary>
	public string StateHint => state switch
	{
		VpkGroupState.Shared => "与别的组共用同一块几何：只能一起开关。",
		VpkGroupState.Stale => "这块在当前产物里找不到——多半换过目标护甲，或者重新转换过。",
		_ => string.Empty,
	};

	public bool HasStateHint => StateHint.Length != 0;

	/// <summary>删除这一行。<b>只删组定义，不动几何</b>。</summary>
	public RelayCommand DeleteCommand { get; }

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

	/// <summary>文档改过之后换掉底层记录（改名、设互斥之后用）。</summary>
	internal void Update(SectionGroup group, VpkGroupState newState)
	{
		entry = group;
		state = newState;
		OnPropertyChanged(nameof(Name));
		OnPropertyChanged(nameof(Detail));
		OnPropertyChanged(nameof(State));
		OnPropertyChanged(nameof(IsStale));
		OnPropertyChanged(nameof(IsShared));
		OnPropertyChanged(nameof(StateHint));
		OnPropertyChanged(nameof(HasStateHint));
		RaiseEnabledChanged();
	}
}
