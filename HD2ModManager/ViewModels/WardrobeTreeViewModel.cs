using HD2ModManager.Controls.Trees;
using HD2ModCore.Domain;

namespace HD2ModManager.ViewModels;

// section 树（无 3D）。它现在只服务一个数据源：VPK 模组自己的组件开关。
//
// 原先它还兼着"护甲掩码编辑器"那条路（按护甲 itemId 读写 wardrobe.workspace.json、
// 算继承/独立）。那条路在 2026-11-04 随掩码链一起退场：它的编辑器入口早就删了，
// 剩下的是没有任何调用者的死代码，却把 WardrobeWorkspace* 一整套类型拖在仓库里。
//
// 三个仍然成立的取舍：
//
// 1. **树是临时的**。它按当前生效的那份 patch 现算，不缓存。
// 2. **勾选状态按 (unit, section 下标) 存**，不按节点存。`bodyType = Any` 的 unit
//    会同时出现在两支下，两处天然同步——这不是特例处理，是状态键选对了。
// 3. **状态来源抽在 IWardrobeTreeToggleStore 后面**，因为"点一下要改哪几个 section"
//    与"这些 section 存在哪"是两件事。

/// <summary>树上的一个节点。父子都持有同一份 <see cref="WardrobeTreeNode"/> 描述。</summary>
public sealed class WardrobeTreeNodeViewModel : BaseViewModel, ITreeSurfaceNode
{
	private readonly WardrobeTreeViewModel owner;
	private bool isExpanded;
	private bool isAlternateRow;
	private int depth;
	private int zIndex;
	private bool isSelected;

	public WardrobeTreeNodeViewModel(WardrobeTreeViewModel owner, WardrobeTreeNode node, string listKey = "0")
	{
		this.owner = owner;
		Node = node;
		ListKey = listKey;
		Children = node.Children
			.Select((child, index) => new WardrobeTreeNodeViewModel(owner, child, $"{listKey}.{index}"))
			.ToArray();
		// 打开时只展开到"能看到槽位"：一个 Mod 几十个 section 全摊开会一眼看不到结构。
		isExpanded = node.Kind is WardrobeTreeNodeKind.Armour or WardrobeTreeNodeKind.BodyType;
		ToggleCommand = new RelayCommand(_ => IsChecked = !IsChecked);
	}

	public WardrobeTreeNode Node { get; }

	public IReadOnlyList<WardrobeTreeNodeViewModel> Children { get; }

	public bool HasChildren => Children.Count > 0;

	public string Label => Node.Label;

	/// <summary>主标题之外的次要信息（编号、面数）。没有名字时为空，界面据此隐藏。</summary>
	public string Subtitle => Node.Subtitle;

	public bool HasSubtitle => !string.IsNullOrEmpty(Node.Subtitle);

	public bool IsSection => Node.Kind == WardrobeTreeNodeKind.Section;

	/// <summary>该 unit 的 bodyType 是 Any，界面据此加锁链图标（两处切换天然同步）。</summary>
	public bool SharedAcrossBodies => Node.SharedAcrossBodies;

	/// <summary>Unit 行上的"护甲 / 内衣"小标记；其他节点为空。</summary>
	public string PieceTypeLabel => Node.PieceTypeLabel;

	public bool HasPieceTypeLabel => !string.IsNullOrEmpty(Node.PieceTypeLabel);

	/// <summary>该节点只关了一部分 section。界面上用文字标出来——开关本身表达不了三态。</summary>
	public bool IsMixed => owner.IsMixed(Node);

	public bool IsExpanded
	{
		get => isExpanded;
		set
		{
			// 隔行底色与层级现在都由 TreeRowProjection 在重投影时统一算，
			// 这里只记状态——控件翻转它之后会自己重建可见行序列。
			SetField(ref isExpanded, value);
		}
	}

	/// <summary>行的稳定标识，过渡动画靠它认人。结构建好就不变。</summary>
	public string ListKey { get; }

	// 下面三个由 TreeRowProjection 每次重投影时写入。必须走 SetField：展开/收起会让
	// 下方所有行的可见序号改变，隔行底色和层级都要跟着通知界面，否则灰条会串色。
	public int Depth
	{
		get => depth;
		set => SetField(ref depth, value);
	}

	public bool IsAlternateRow
	{
		get => isAlternateRow;
		set => SetField(ref isAlternateRow, value);
	}

	public int ZIndex
	{
		get => zIndex;
		set => SetField(ref zIndex, value);
	}

	/// <summary>由 TreeSurface 写入：点击行时控件维护自己的单选。</summary>
	public bool IsSelected
	{
		get => isSelected;
		set => SetField(ref isSelected, value);
	}

	IReadOnlyList<ITreeSurfaceNode> ITreeSurfaceNode.Children => Children;

	/// <summary>
	/// 开关状态：**只要不是"全关"就是开**。
	/// 开关表达不了三态，所以混合状态显示为开，另用「部分」文字标出来；
	/// 这样点一下的含义始终是"全关"，再点一下"全开"，不会出现点一次毫无反应的情况。
	/// </summary>
	public bool IsChecked
	{
		get => !owner.ReadAllDisabled(Node);
		set
		{
			if (!owner.IsEditable || value == IsChecked)
				return;
			owner.ApplyToggle(Node, value);
		}
	}

	public RelayCommand ToggleCommand { get; }

	/// <summary>勾选状态变了（可能是被兄弟节点带着变的），通知界面重画。</summary>
	internal void RaiseCheckedChanged()
	{
		OnPropertyChanged(nameof(IsChecked));
		OnPropertyChanged(nameof(IsMixed));
	}
}

/// <summary>
/// 树的开关状态来源。
/// <para>
/// "点一下要改哪几个 section""当前是不是全关"是树的事；"这些 section 存在哪"
/// 是调用方的事。抽出来之后树的外观与交互只有一份，换数据源不会长歪。
/// </para>
/// </summary>
public interface IWardrobeTreeToggleStore
{
	bool ReadAllDisabled(WardrobeTreeNode node);

	bool IsMixed(WardrobeTreeNode node);

	void ApplyToggle(WardrobeTreeNode node, bool enabled);
}

/// <summary>
/// 一棵现成的 section 树。<b>它不自带状态</b>——状态由构造时给的
/// <see cref="IWardrobeTreeToggleStore"/> 回答，所以同一个控件能服务任意数据源。
/// </summary>
public sealed class WardrobeTreeViewModel : BaseViewModel
{
	private readonly IWardrobeTreeToggleStore? toggleStore;
	private IReadOnlyList<WardrobeTreeNodeViewModel> roots = Array.Empty<WardrobeTreeNodeViewModel>();
	private string statusMessage = string.Empty;
	private bool isEditable = true;
	private bool isLoading;

	/// <summary>用外部的开关状态来源建树（VPK 模组的组件卡走这条）。</summary>
	public WardrobeTreeViewModel(IWardrobeTreeToggleStore toggleStore)
	{
		this.toggleStore = toggleStore ?? throw new ArgumentNullException(nameof(toggleStore));
	}

	/// <summary>
	/// 用一棵**现成的**树填充视图。
	/// <para>
	/// 树的形状来自 <c>sections.json</c>（"这个 patch 里有什么"），
	/// 与"这些 section 当前关没关"分开——后者由 <see cref="IWardrobeTreeToggleStore"/> 回答。
	/// </para>
	/// </summary>
	public void LoadFromTree(WardrobeTreeNode root, bool editable, bool expandRoot = true)
	{
		ArgumentNullException.ThrowIfNull(root);
		IsEditable = editable;
		StatusMessage = string.Empty;
		roots = [new WardrobeTreeNodeViewModel(this, root)];
		OnPropertyChanged(nameof(Roots));
		OnPropertyChanged(nameof(HasRoots));
		if (expandRoot)
		{
			foreach (var child in roots)
			{
				child.IsExpanded = true;
				foreach (var body in child.Children)
					body.IsExpanded = true;
			}
		}
	}

	public IReadOnlyList<WardrobeTreeNodeViewModel> Roots => roots;

	public bool HasRoots => roots.Count > 0;

	/// <summary>
	/// 让每一行重读勾选状态。
	/// <para>
	/// 用在一个"树外"的控件改了同一份状态之后（组列表就是这种：一个组对应若干
	/// section，但它不是树上的节点）。重建整棵树会丢掉展开状态，所以只刷新勾选。
	/// </para>
	/// </summary>
	public void RefreshChecks()
	{
		foreach (var root in roots)
			RefreshChecks(root);
	}

	private static void RefreshChecks(WardrobeTreeNodeViewModel node)
	{
		node.RaiseCheckedChanged();
		foreach (var child in node.Children)
			RefreshChecks(child);
	}

	public string StatusMessage
	{
		get => statusMessage;
		private set
		{
			if (!SetField(ref statusMessage, value))
				return;
			OnPropertyChanged(nameof(HasStatus));
		}
	}

	/// <summary>有没有要显示的状态文字（界面据此收起那一行，避免留空白）。</summary>
	public bool HasStatus => !string.IsNullOrWhiteSpace(statusMessage);

	public bool IsEditable
	{
		get => isEditable;
		private set => SetField(ref isEditable, value);
	}

	public bool IsLoading
	{
		get => isLoading;
		private set => SetField(ref isLoading, value);
	}

	/// <summary>清空（切走 / 关闭时调用）。</summary>
	public void Clear()
	{
		roots = Array.Empty<WardrobeTreeNodeViewModel>();
		isEditable = true;
		StatusMessage = string.Empty;
		OnPropertyChanged(nameof(Roots));
		OnPropertyChanged(nameof(HasRoots));
		OnPropertyChanged(nameof(IsEditable));
	}

	// ── IWardrobeTreeToggleStore 的转发（节点自己只认 Node，不认数据源）──

	internal bool ReadAllDisabled(WardrobeTreeNode node)
		=> toggleStore?.ReadAllDisabled(node) ?? false;

	internal bool IsMixed(WardrobeTreeNode node)
		=> toggleStore?.IsMixed(node) ?? false;

	internal void ApplyToggle(WardrobeTreeNode node, bool enabled)
		=> toggleStore?.ApplyToggle(node, enabled);
}
