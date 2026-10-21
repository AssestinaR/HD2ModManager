using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.ViewModels;

// 「视觉组件」卡片里的 section 树（无 3D）。
//
// 与 3D 窗口共用同一份核心树（HD2ModCore.Domain.WardrobeTree），所以两层之间只有
// "怎么显示"的差别，没有"勾一下到底写了什么"的差别。
//
// 三个必须说清楚的取舍：
//
// 1. **树是临时的**。它按当前部署的赢家 unit 现算，保存后立刻丢弃；落盘的只有
//    "某 unit 的第 N 个 section 是禁用"。所以这里不缓存树，每次打开重算。
// 2. **勾选状态按 (unit, section 下标) 存**，不按节点存。`bodyType = Any` 的 unit
//    会同时出现在纤细和健壮两支下，两处天然同步——这不是特例处理，是状态键选对了。
// 3. **继承与独立是显隐自己的轴**，和卡片上原有的「跟随视觉来源」（那是模型/组件结构
//    的轴，进协议的 `target_mode`）不是同一件事。两个概念同名会很危险，所以这里叫
//    「显隐跟随来源」。
//
// The card's section tree, sharing the same core tree as the future 3D window. The tree is
// temporary and rebuilt on every open; only "unit N section K is disabled" is persisted, and
// it is keyed by (unit, index) rather than by node, which is why the two body branches stay
// in sync for free. The inherit/own axis here is about section visibility only — the card's
// pre-existing "follow visual source" toggle is about piece structure and lives in the
// protocol's target_mode, so the two are deliberately named differently.

/// <summary>树上的一个节点。父子都持有同一份 <see cref="WardrobeTreeNode"/> 描述。</summary>
public sealed class WardrobeTreeNodeViewModel : BaseViewModel
{
	private readonly WardrobeTreeViewModel owner;
	private bool isExpanded;
	private bool isOddRow;

	public WardrobeTreeNodeViewModel(WardrobeTreeViewModel owner, WardrobeTreeNode node)
	{
		this.owner = owner;
		Node = node;
		Children = node.Children
			.Select(child => new WardrobeTreeNodeViewModel(owner, child))
			.ToArray();
		// 打开时只展开到"能看到槽位"：一件护甲几十个 section 全摊开会一眼看不到结构。
		isExpanded = node.Kind is WardrobeTreeNodeKind.Armour or WardrobeTreeNodeKind.BodyType;
		ToggleCommand = new RelayCommand(_ => IsChecked = !IsChecked);
	}

	public WardrobeTreeNode Node { get; }

	public IReadOnlyList<WardrobeTreeNodeViewModel> Children { get; }

	public bool HasChildren => Children.Count > 0;

	public string Label => Node.Label;

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
			if (!SetField(ref isExpanded, value))
				return;
			// 隔行底色按**可见行**均匀编号，所以展开/收起之后必须重算；
			// 否则收起一棵奇数行的子树，它下面那一行会和上一行同色，正好是用户看到的问题。
			owner.RecomputeRowStripes();
		}
	}

	/// <summary>
	/// 这一行在**当前可见行**里是不是奇数行。界面的隔行底色用它，
	/// 而不是每个父节点各数一遍——那样每个子集都会从白色重新开始，相邻两行常常同色。
	/// </summary>
	public bool IsOddRow
	{
		get => isOddRow;
		internal set => SetField(ref isOddRow, value);
	}

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
/// 为什么把它抽出来：同一个树要服务两种数据源——衣柜的护甲掩码（按护甲 itemId 存）
/// 和 VPK 模组的组件开关（按模组自己的 <c>wardrobe.json</c> 存）。两者语义不同，
/// 但"点一下要改哪几个 section""当前是不是全关"完全一样。抽出来之后树的外观与交互
/// 只有一份，两边不会长歪。
/// </para>
/// </summary>
public interface IWardrobeTreeToggleStore
{
	bool ReadAllDisabled(WardrobeTreeNode node);

	bool IsMixed(WardrobeTreeNode node);

	void ApplyToggle(WardrobeTreeNode node, bool enabled);
}

/// <summary>
/// 一件护甲的 section 树 + 它的权威掩码。
/// 编辑即保存：每次勾选都写一次 <c>wardrobe.workspace.json</c>（原子写，几 KB）。
/// </summary>
public sealed class WardrobeTreeViewModel : BaseViewModel
{
	private readonly string workspacePath;
	private readonly IWardrobeTreeToggleStore? toggleStore;
	private WardrobeWorkspaceDocument workspace = WardrobeWorkspaceDocument.Empty;
	private IReadOnlyList<WardrobeTreeNodeViewModel> roots = Array.Empty<WardrobeTreeNodeViewModel>();
	private string statusMessage = string.Empty;
	private bool isEditable = true;
	private bool followSource = true;
	private bool isLoading;

	public WardrobeTreeViewModel(string workspacePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
		this.workspacePath = workspacePath;
	}

	/// <summary>用外部的开关状态来源建树（VPK 模组的组件卡走这条）。</summary>
	public WardrobeTreeViewModel(IWardrobeTreeToggleStore toggleStore)
	{
		this.toggleStore = toggleStore ?? throw new ArgumentNullException(nameof(toggleStore));
		workspacePath = string.Empty;
	}

	/// <summary>
	/// 用一棵**现成的**树填充视图。
	/// <para>
	/// VPK 模组没有"护甲 itemId"，它的 section 表来自自己 patch 的 <c>sections.json</c>，
	/// 所以不走 <see cref="Load"/>（那条路要读衣柜工作区、算继承关系）。
	/// </para>
	/// </summary>
	public void LoadFromTree(WardrobeTreeNode root, bool editable, bool expandRoot = true)
	{
		ArgumentNullException.ThrowIfNull(root);
		IsEditable = editable;
		followSource = false;
		StatusMessage = string.Empty;
		roots = [new WardrobeTreeNodeViewModel(this, root)];
		OnPropertyChanged(nameof(Roots));
		OnPropertyChanged(nameof(HasRoots));
		OnPropertyChanged(nameof(FollowSource));
		if (expandRoot)
		{
			foreach (var child in roots)
			{
				child.IsExpanded = true;
				foreach (var body in child.Children)
					body.IsExpanded = true;
			}
		}

		RecomputeRowStripes();
	}

	public IReadOnlyList<WardrobeTreeNodeViewModel> Roots => roots;

	public bool HasRoots => roots.Count > 0;

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

	/// <summary>显隐是否跟随视觉来源。仅对映射目标有意义；视觉来源恒为独立。</summary>
	public bool FollowSource
	{
		get => followSource;
		set
		{
			if (!SetField(ref followSource, value))
				return;
			if (!value)
			{
				// 从"继承"转"独立"时用**来源**的当前状态做初值，否则用户点第一下之前
				// 看到的那份隐藏会突然全部消失——那不是开始独立编辑，那是清空设置。
				var inherited = SourceItemId is { } source ? workspace.FindArmour(source) : null;
				workspace = WardrobeTree.MakeOwn(workspace, ArmourItemId, inherited);
				Save();
			}
			else
			{
				workspace = WardrobeTree.MakeInherit(workspace, ArmourItemId);
				Save();
			}

			IsEditable = !IsTarget || !followSource;
			Refresh();
		}
	}

	/// <summary>当前护甲的 itemId。</summary>
	public uint ArmourItemId { get; private set; }

	/// <summary>它的视觉来源 itemId；视觉来源自己编辑时为空。</summary>
	public uint? SourceItemId { get; private set; }

	/// <summary>正在编辑的是映射目标（它才有"继承"这个选项）。</summary>
	public bool IsTarget { get; private set; }

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

	private WardrobeWorkspaceArmour? inheritedFrom;

	/// <summary>
	/// 打开编辑器时算一次树。
	/// <paramref name="unitIds"/> 是这件护甲的赢家 unit（来自权威件表）；
	/// <paramref name="gameDataDirectory"/> 用来从**当前部署的 patch** 现算 section 表。
	/// </summary>
	public void Load(
		uint armourItemId,
		uint? sourceItemId,
		bool isTarget,
		string armourLabel,
		IReadOnlyList<WardrobeSectionMapUnit> units,
		IReadOnlyList<string> missingUnitIds,
		IReadOnlyList<uint>? missingArmourItems = null)
	{
		ArmourItemId = armourItemId;
		SourceItemId = sourceItemId;
		IsTarget = isTarget;
		roots = Array.Empty<WardrobeTreeNodeViewModel>();
		OnPropertyChanged(nameof(Roots));
		OnPropertyChanged(nameof(HasRoots));
		StatusMessage = string.Empty;
		try
		{
			workspace = WardrobeWorkspaceFile.Read(workspacePath);
			inheritedFrom = null;
			if (isTarget)
			{
				var own = workspace.FindArmour(armourItemId);
				followSource = own is null || own.Mode == WardrobeMaskMode.Inherit;
				inheritedFrom = own;
			}
			else
			{
				// 视觉来源没有来源可继承，恒为独立。
				followSource = false;
				inheritedFrom = workspace.FindArmour(armourItemId);
			}

			OnPropertyChanged(nameof(FollowSource));

			if (units.Count == 0)
			{
				StatusMessage = "这件护甲没有可解析的 unit。";
				return;
			}

			var built = WardrobeTree.Build(
				string.IsNullOrWhiteSpace(armourLabel) ? $"护甲 {armourItemId}" : armourLabel,
				units);
			roots = new[] { new WardrobeTreeNodeViewModel(this, built) };
			RecomputeRowStripes();
			IsEditable = !isTarget || !followSource;
			var notes = new List<string>();
			// 两种缺法必须分开说：unit 没找到（部署与原版都没有）与护甲条目在注册文件里
			// 根本没有 kit（数据缺失）。合成一句话会让人分不清该查哪一层。
			if (missingArmourItems is { Count: > 0 })
			{
				notes.Add($"这件护甲在游戏注册文件里缺少 {missingArmourItems.Count} 个条目："
					+ string.Join(", ", missingArmourItems));
			}

			if (missingUnitIds.Count > 0)
			{
				notes.Add($"有 {missingUnitIds.Count} 个 unit 在已部署的 patch 与原版数据包里都没找到："
					+ string.Join(", ", missingUnitIds));
			}

			StatusMessage = string.Join("；", notes);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			StatusMessage = $"读取衣柜设置失败：{exception.Message}";
		}
		finally
		{
			OnPropertyChanged(nameof(Roots));
			OnPropertyChanged(nameof(HasRoots));
		}
	}

	internal bool ReadAllDisabled(WardrobeTreeNode node)
		=> toggleStore is not null
			? toggleStore.ReadAllDisabled(node)
			: WardrobeTree.Counts(node, ArmourItemId, workspace) is { SectionCount: > 0 } counts && counts.AllDisabled;

	internal bool IsMixed(WardrobeTreeNode node)
		=> toggleStore is not null
			? toggleStore.IsMixed(node)
			: WardrobeTree.Counts(node, ArmourItemId, workspace).IsMixed;

	internal void ApplyToggle(WardrobeTreeNode node, bool enabled)
	{
		if (toggleStore is not null)
		{
			// 外部来源（VPK 组件卡）只改内存状态：真正落盘要等用户点"应用"。
			toggleStore.ApplyToggle(node, enabled);
			Refresh();
			return;
		}

		try
		{
			workspace = WardrobeTree.SetEnabled(workspace, ArmourItemId, node, enabled);
			Save();
			Refresh();
		}
		catch (InvalidOperationException exception)
		{
			// 继承模式下改条目会和 mode 自相矛盾；界面本该把树禁掉，走到这里说明状态没同步。
			StatusMessage = exception.Message;
		}
	}

	private void Save()
	{
		try
		{
			// 先推进修订号再写：否则连续两次保存会写出同一个 revision，
			// 而"内容没变"与"修订没变"是两件事，混在一起会让下游判断失真。
			workspace = WardrobeWorkspaceFile.NextRevision(workspace);
			WardrobeWorkspaceFile.Write(workspacePath, workspace);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			StatusMessage = $"保存衣柜设置失败：{exception.Message}";
		}
	}

	/// <summary>整棵树重算勾选状态。父子节点的状态互相牵连，逐节点推更新比精确推送更不容易漏。</summary>
	private void Refresh()
	{
		foreach (var root in roots)
			RaiseRecursive(root);

		void RaiseRecursive(WardrobeTreeNodeViewModel node)
		{
			node.RaiseCheckedChanged();
			foreach (var child in node.Children)
				RaiseRecursive(child);
		}
	}

	/// <summary>
	/// 按**当前可见行**重新给隔行底色编号：只往下走已展开的节点，
	/// 这样"相邻两行一定不同色"这件事与展开状态无关。
	/// 收起一棵奇数行的子树时如果不重算，它下面那一行会和上一行同色——
	/// 那正是按父节点各数一遍会看到的问题，只是换了个触发时机。
	/// </summary>
	internal void RecomputeRowStripes()
	{
		var index = 0;
		foreach (var root in roots)
			Walk(root);

		void Walk(WardrobeTreeNodeViewModel node)
		{
			node.IsOddRow = (index++ & 1) == 1;
			if (!node.IsExpanded)
				return;
			foreach (var child in node.Children)
				Walk(child);
		}
	}

	/// <summary>清空（关闭编辑器时调用）。</summary>
	public void Clear()
	{
		roots = Array.Empty<WardrobeTreeNodeViewModel>();
		inheritedFrom = null;
		ArmourItemId = 0;
		IsTarget = false;
		followSource = true;
		isEditable = true;
		StatusMessage = string.Empty;
		OnPropertyChanged(nameof(Roots));
		OnPropertyChanged(nameof(HasRoots));
		OnPropertyChanged(nameof(FollowSource));
		OnPropertyChanged(nameof(IsEditable));
	}
}
