using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>下拉里的一个目标。<c>ArchiveId</c> 是这一套装备要替换的全部归档（护甲 + 头盔），或者自定义素体的伪 id。</summary>
public enum VpkTargetArmorKind
{
	/// <summary>游戏里真实存在的护甲归档（按 setId 会再补上配套头盔）。</summary>
	GameArmor = 0,

	/// <summary>护甲映射页里注册的自定义素体：<c>custom-model:&lt;key&gt;</c>，没有游戏归档。</summary>
	CustomModel = 1,
}

public sealed record VpkTargetArmorOption(
	string ArchiveId,
	string DisplayName,
	VpkTargetArmorKind Kind = VpkTargetArmorKind.GameArmor,
	uint? ItemId = null,
	string? BaseName = null,
	bool ExclusiveAvailable = true,
	string? Tag = null,
	bool IsNonPlayer = false)
{
	public override string ToString() => DisplayName;

	/// <summary>专属模式下的候选（按记录 item_id 列）；普通模式（按归档列）为假。</summary>
	public bool IsExclusiveOption => ItemId is not null;

	/// <summary>写进 wardrobe.json 的名字：专属模式下 <see cref="DisplayName"/> 带编号和状态，不能直接存。</summary>
	public string StoredName => BaseName ?? DisplayName;
}

/// <summary>
/// Mod 详情页的 sectional 部分（sectional / vpk2patch 两档共用）：动作卡（目标恒为自身素体 + 组件开关）与衣物装饰组件卡。
/// <para>
/// <b>为什么 VPK 版 Mod 不显示 patch 版设置卡</b>：那张卡上的按钮（修复 patch、跨护甲转移、
/// 规整护甲包、头盔合并…）都假设"这个 Mod 是一堆手写/社区 patch"。VPK 版 Mod 的 patch 是
/// 转换产物，重转一次就全变；在那上面做 patch 级编辑，结果会被下一次转换覆盖。
/// 用户若真要做，把 <c>vpk2patch/</c> 里的 patch 拿出来当普通 Mod 导入即可。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel : IWardrobeTreeToggleStore
{

	private VpkWardrobeFile _vpkFile = VpkWardrobeFile.Default;
	private WardrobeTreeViewModel? _vpkTree;
	private string _vpkStatus = string.Empty;
	private bool _vpkBusy;
	private bool _vpkArmorsLoading;
	private IReadOnlyList<VpkTargetArmorOption> _vpkTargetArmors = [];
	private VpkTargetArmorOption? _selectedVpkTargetArmor;
	private IReadOnlyList<VpkTargetArmorOption> _vpkNormalTargetArmors = [];
	private bool _vpkExclusiveMode;
	private int _vpkExclusiveCatalogVersion;
	private IReadOnlyList<VpkGroupItemViewModel> _vpkGroups = [];
	private string _vpkSwitchStatus = string.Empty;

	/// <summary>
	/// 要不要走"就地烘焙"那条快捷路径（直接改 <c>Overwrite/</c> 里那份 patch 的 <c>numIndices</c>）。
	/// <para>
	/// <b>本轮固定为 <c>false</c></b>：编译器的地基是"任何一处变动都从 <c>sectional/</c> 完整重算一次"，
	/// 多留一条直接改字节的写者，就多一个能跟盘上状态不同步的分支。实现与测试都留着，
	/// 等链路稳定、且有了"编译戳"这类判据之后再打开。
	/// </para>
	/// </summary>
	private const bool EnableInPlaceBake = false;

	/// <summary>
	/// 组表（<c>components.json</c>）。**与开关状态分开**：这里只回答"有哪些组、每个组包含哪几块"，
	/// 而"哪些块当前不画"存在 <see cref="VpkWardrobeFile.State"/>（<c>wardrobe.json</c>）。
	/// 两者分开的理由见 <see cref="SectionGroup"/> 的注释。
	/// </summary>
	private SectionGroupDocument _sectionGroups = SectionGroupDocument.Empty;

	/// <summary>新建组 / 改名时用的输入框内容。</summary>
	private string _newVpkGroupName = string.Empty;

	/// <summary>
	/// 树上当前选中的节点。<b>新建组就是"把这些块（或这几整棵）建成组"</b>，所以它必须有。
	/// <para>
	/// 多选：Ctrl 单拾取、Shift 区域拾取，<b>同一时刻只可能有一层</b>（跨层会被当成重新开始）。
	/// </para>
	/// </summary>
	private IReadOnlyList<WardrobeTreeNode> _selectedVpkTreeNodes = [];

	/// <summary>当前生效的 section 表。组 CRUD 之后要拿它重画（不重读文件）。</summary>
	private WardrobeSectionMapDocument? _vpkSectionMap;

	/// <summary>勾选状态的防抖保存。勾一下不该立刻写盘（连点十下会写十次），但也不能不写。</summary>
	private CancellationTokenSource? _vpkStateSave;

	/// <summary>防抖窗口。250 ms：比人手连点慢，比"切页"快。</summary>
	private static readonly TimeSpan VpkStateSaveDelay = TimeSpan.FromMilliseconds(250);

	/// <summary>这个 Mod 是不是 VPK 版（有 <c>vpk2patch/</c> 载荷）。</summary>
	public bool IsVpkMod { get; private set; }

	/// <summary>这个 Mod 根上有没有来源 .vpk。没有就无法「用来源重构建」（转出来的分件模组就是这种）。</summary>
	public bool HasSourceVpk { get; private set; }

	/// <summary>
	/// 这个 Mod 是不是"根上来源 + 转换依据"那一档（<c>sectional-source.json</c>）。
	/// <para>
	/// 它与 <see cref="HasSourceVpk"/> 是**并列的两种来源**，不是互斥的两种 Mod：
	/// 有 .vpk 走 VPK 转换机，有依据文件走 legacy2sectional。两者都有时以 .vpk 为准。
	/// </para>
	/// </summary>
	public bool HasSectionalSource { get; private set; }

	/// <summary>有没有可用的重构建来源（任一形态）。</summary>
	public bool HasRebuildSource => HasSourceVpk || HasSectionalSource;

	/// <summary>patch 工具（修复/换甲/规整/重打包/头盔合并）的总闸。VPK 版一律不显示。</summary>
	public bool ShowPatchTools => CanUsePatchTools && !IsVpkMod;

	/// <summary>这个 Mod 是不是逻辑容器（自己没载荷，靠结构文件把子级组织起来）。</summary>
	public bool IsLogicalContainer => Mod?.IsLogicalContainer == true;

	/// <summary>
	/// 「生成sectional版」的<b>唯一</b>判据：能不能点 = 该不该显示（**legacy 或逻辑容器**）。
	/// <para>
	/// 逻辑容器自己没有 patch，但它的成员（legacy 子级）有；这一颗按钮正是"把这个系列转成一个
	/// sectional 条目"的入口，所以不能沿用"自己有 patch 才显示"的判据。
	/// </para>
	/// <para>
	/// <b>这里刻意不判断"容器里到底有没有可用来源"</b>：那要遍历后代并扫盘，而这是绑定属性，
	/// 每次属性变化都会求值。有没有来源由底栏在**点击时**判断并给出原因——那里反正要读一遍。
	/// </para>
	/// <para>
	/// <b>不要再给它另配一套"可点性"判据。</b>动作按钮是 <c>Command</c> 绑定，WPF 的
	/// <c>IsEnabledCore</c> 会把 <see cref="PageActionViewModel.IsEnabled"/> 和
	/// <c>Command.CanExecute</c> 相与：命令那边一旦借了别的判据（这里曾经借的是
	/// <c>CanPlanCrossArmorTransfer</c>，要求"自己有 patch"），按钮就在逻辑容器上
	/// 明明显示出来却永远是灰的；而 <see cref="RelayCommand"/> 不挂
	/// <c>CommandManager.RequerySuggested</c>，没人主动 Raise 的话它连重算都不会重算，
	/// 一旦灰掉就再也亮不回来。可见性与可点性必须是同一个属性。
	/// </para>
	/// </summary>
	public bool CanConvertToSectional => !_disposed
		&& TryGetCurrentNode(out _)
		&& !IsVpkMod
		&& (HasPatchContent || IsLogicalContainer);

	public WardrobeTreeViewModel? VpkTree
	{
		get => _vpkTree;
		private set
		{
			if (!SetField(ref _vpkTree, value)) return;
			OnPropertyChanged(nameof(ShowVpkComponentCard));
			OnPropertyChanged(nameof(HasVpkComponentTree));
		OnPropertyChanged(nameof(CanOpenSectionalEditor));
		}
	}

	public bool HasVpkComponentTree => _vpkTree?.HasRoots == true;

	/// <summary>
	/// section 组列表（<c>components.json</c> 里的"猫耳 / 披风 / 外套"）。
	/// <para>
	/// 与树是<b>两种视图、同一份状态</b>：组写的是"这些 section 关没关"，
	/// 树上显示的也是同一件事。所以这里没有第二份持久化数据，勾一下等于替用户把
	/// 对应的那几节全关（或全开）。
	/// </para>
	/// </summary>
	public IReadOnlyList<VpkGroupItemViewModel> VpkGroups
	{
		get => _vpkGroups;
		private set
		{
			_vpkGroups = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasVpkGroups));
			OnPropertyChanged(nameof(VpkGroupSummary));
		}
	}

	public bool HasVpkGroups => _vpkGroups.Count > 0;

	/// <summary>新建组时用的名字。留空则用「新组」。</summary>
	public string NewVpkGroupName
	{
		get => _newVpkGroupName;
		set => SetField(ref _newVpkGroupName, value);
	}

	/// <summary>树上当前选中的节点。由视图在选中变化时写入。</summary>
	public IReadOnlyList<WardrobeTreeNode> SelectedVpkTreeNodes
	{
		get => _selectedVpkTreeNodes;
		set
		{
			// 集合是按引用比较的，SetField 那套不适用；这里比内容。
			var next = value ?? [];
			if (_selectedVpkTreeNodes.Count == next.Count
				&& _selectedVpkTreeNodes.SequenceEqual(next))
				return;
			_selectedVpkTreeNodes = next;
			OnPropertyChanged();
			OnPropertyChanged(nameof(VpkSelectionHint));
			OnPropertyChanged(nameof(HasVpkSelection));
		}
	}

	public bool HasVpkSelection => _selectedVpkTreeNodes.Count != 0;

	/// <summary>选中提示：新建组会包含哪几块。<b>点之前就说清</b>，比点了之后猜好。</summary>
	public string VpkSelectionHint
	{
		get
		{
			if (_selectedVpkTreeNodes.Count == 0)
				return "先在下面的树里点一块几何（或一个部位），再点「新建组」。按住 Ctrl 可多选、Shift 可选一段。";
			var sections = CollectSelectedSections();
			var count = sections.Count(section => section.Unit is not null && section.SectionIndex is not null);
			if (count == 0)
				return _selectedVpkTreeNodes.Count == 1
					? $"「{_selectedVpkTreeNodes[0].Label}」下面没有可开关的几何。"
					: "选中的这些条目下面没有可开关的几何。";
			return _selectedVpkTreeNodes.Count == 1
				? $"「{_selectedVpkTreeNodes[0].Label}」→ 新建组会包含 {count} 块几何。"
				: $"已选 {_selectedVpkTreeNodes.Count} 个条目 → 新建组会包含 {count} 块几何。";
		}
	}

	/// <summary>
	/// 把选中的节点摊平成 section 列表。<b>按 section 去重</b>——
	/// 多选时两条父级条目可能覆盖同一块几何（例如同一个 unit 下的相邻分支），
	/// 不去重会让同一块几何在组里出现两次。
	/// </summary>
	private IReadOnlyList<WardrobeTreeNode> CollectSelectedSections()
	{
		if (_selectedVpkTreeNodes.Count == 0) return [];
		var seen = new HashSet<WardrobeTreeNode>();
		var result = new List<WardrobeTreeNode>();
		foreach (var node in _selectedVpkTreeNodes)
		{
			var sections = node.Kind == WardrobeTreeNodeKind.Section ? [node] : node.Sections();
			foreach (var section in sections)
			{
				if (seen.Add(section)) result.Add(section);
			}
		}

		return result;
	}

	/// <summary>组区的一句话说明：多少个组、共覆盖多少块几何、其中多少条已经失效。</summary>
	public string VpkGroupSummary
	{
		get
		{
			if (_vpkGroups.Count == 0)
				return string.Empty;
			var sections = _vpkGroups.Sum(item => item.Entry.SectionCount);
			var text = $"共 {_vpkGroups.Count} 个组（覆盖 {sections} 块几何）。";
			var stale = _vpkGroups.Count(item => item.State == VpkGroupState.Stale);
			if (stale != 0)
				text += $"其中 {stale} 个组在当前产物里找不到落点。";
			var shared = _vpkGroups.Count(item => item.State == VpkGroupState.Shared);
			if (shared != 0)
				text += $"另有 {shared} 个组与别的组共用同一块几何（只能一起开关）。";
			return text + "勾完点「应用」烘进 patch。";
		}
	}

	/// <summary>没匹配上的包、以及"这个 Mod 是旧版转的"这类提示。</summary>
	public string VpkGroupStatus
	{
		get => _vpkSwitchStatus;
		private set
		{
			if (!SetField(ref _vpkSwitchStatus, value)) return;
			OnPropertyChanged(nameof(HasVpkGroupStatus));
		}
	}

	public bool HasVpkGroupStatus => !string.IsNullOrWhiteSpace(_vpkSwitchStatus);

	/// <summary>「启用 L4N 支持的衣物装饰组件切换」——开关本身存在 Mod 自己的 wardrobe.json 里。详情页已无入口（编辑搬到独立页面），保留读法是因为老 Mod 可能把它关着。</summary>
	public bool EnableVpkComponentTree
	{
		get => _vpkFile.ComponentTreeEnabled;
		set
		{
			if (_vpkFile.ComponentTreeEnabled == value) return;
			_vpkFile = _vpkFile with { ComponentTreeEnabled = value };
			OnPropertyChanged();
			_ = PersistVpkAsync();
		}
	}

	/// <summary>
	/// 能不能进分件编辑页。判据是"有 section 树"——空树进去只会看到一片空白，
	/// 不如把按钮藏掉（那种 Mod 通常还没有 section 表，或者产物里没有可开关的块）。
	/// </summary>
	public bool CanOpenSectionalEditor => IsVpkMod && HasVpkComponentTree;

	private RelayCommand? _openSectionalEditorCommand;

	/// <summary>
	/// 跳到分件编辑页（左 section 组 / 右 section 树）。
	/// <para>
	/// 原来这里是一个「启用 L4N 支持的衣物装饰组件切换」开关，打开后详情页内嵌一棵树。
	/// 详情页那一列太窄，树和组挤在一起谁都不好用；改成独立页面，两边各得一块。
	/// </para>
	/// </summary>
	public RelayCommand OpenSectionalEditorCommand => _openSectionalEditorCommand ??= new RelayCommand(_ =>
	{
		if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
			shell.OpenSectionalEditor(this);
	});
	public bool ShowVpkComponentCard => false;


	/// <summary>
	/// 「头身合并到身体」：头并进护甲套件的胸部槽位，游戏里那件头盔 item 就没有模型了；
	/// 关掉则回到老做法（头由头盔套件承载，玩家必须把配套头盔也穿上才有头）。
	/// <para>
	/// <b>这不是显示开关，是产物开关</b>：两种状态写出来的 patch 完全不同，
	/// 所以翻转它必须重新生成 Overwrite——和「切换」「应用」一样重。为此这里直接触发一次换甲，
	/// 而不是等用户另外去点「应用」。状态先落盘（同「应用」的理由：烘焙是从状态算出来的）。
	/// </para>
	/// <para>旧状态文件里没有这个字段，读出来就是 <c>true</c>（见 <see cref="VpkWardrobeFile"/>）。</para>
	/// </summary>
	public bool HeadMergeIntoBody
	{
		get => _vpkFile.HeadMergeIntoBody;
		set
		{
			if (_vpkFile.HeadMergeIntoBody == value) return;
			_vpkFile = _vpkFile with { HeadMergeIntoBody = value };
			OnPropertyChanged();
			_ = ApplyHeadMergeAsync(value);
		}
	}

	public string VpkComponentStatus
	{
		get => _vpkStatus;
		private set => SetVpkStatus(value);
	}

	/// <summary>
	/// 卡片不再显示状态行（那张卡要保持静态），所以这里把每一条状态都**同时**送进消息中心，
	/// 否则"读取护甲列表失败""还没选目标护甲"这类只在卡上出现过的提示会彻底变隐形。
	/// <para>
	/// 调用方原来在"没有后台任务时"额外补一条通知，现在由本方法统一发，那些镜像调用已删除——
	/// 留着会一条消息弹两次。
	/// </para>
	/// </summary>
	private void SetVpkStatus(string value, NotificationLevel level = NotificationLevel.Info, int seconds = 12, bool notify = true)
	{
		if (_vpkStatus != value)
		{
			_vpkStatus = value;
			OnPropertyChanged(nameof(VpkComponentStatus));
			OnPropertyChanged(nameof(HasVpkComponentStatus));
		}

		if (string.IsNullOrWhiteSpace(value)) return;
		// notify=false：这条只是"这次翻开关发生了什么"的即时反馈，翻来翻去会刷屏，
		// 不弹通知，只在日志里留一条痕迹。
		if (notify) _notifications?.Show(value, level, TimeSpan.FromSeconds(seconds), "Mod 详情");
		else LogService.Info("Mod 详情：" + value);
	}

	public bool HasVpkComponentStatus => !string.IsNullOrWhiteSpace(_vpkStatus);

	/// <summary>
	/// 「专属」开关（P4a）。<b>只决定下拉怎么列、「切换」怎么做</b>，翻它本身不写盘、不编译：
	/// 真正生效的是点「切换」那一下（产物形状不同，必须由用户明确确认预览）。
	/// </summary>
	public bool VpkExclusiveMode
	{
		get => _vpkExclusiveMode;
		set
		{
			if (_vpkExclusiveMode == value) return;
			_vpkExclusiveMode = value;
			OnPropertyChanged();
			_ = ReloadVpkTargetOptionsAsync();
		}
	}

	private async Task ReloadVpkTargetOptionsAsync()
	{
		var version = ++_vpkExclusiveCatalogVersion;
		if (!_vpkExclusiveMode)
		{
			VpkTargetArmors = _vpkNormalTargetArmors;
			if (TryGetCurrentNode(out var current))
				SelectedVpkTargetArmor = ResolveCurrentTargetArmor(Path.Combine(_library.ModsRootDirectory, current.RelativePath));
			return;
		}

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		var headMerge = _vpkFile.HeadMergeIntoBody;
		KitExclusiveCatalog catalog;
		try
		{
			catalog = await Task.Run(() => KitExclusiveService.LoadCatalogAsync(directory, _paths, headMerge)).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
		{
			catalog = new KitExclusiveCatalog([], exception.Message);
		}

		// 用户在加载期间翻回去了 / 换了页面：丢掉这次结果。
		if (version != _vpkExclusiveCatalogVersion || !_vpkExclusiveMode) return;
		if (catalog.Unavailable is not null)
		{
			VpkComponentStatus = catalog.Unavailable;
			_vpkExclusiveMode = false;
			OnPropertyChanged(nameof(VpkExclusiveMode));
			return;
		}

		VpkTargetArmors = catalog.Options
			.Select(option => new VpkTargetArmorOption(
				option.ArchiveId, $"{option.Label}{(option.Tag is null ? string.Empty : "  · " + option.Tag)}  · {option.Status}",
				VpkTargetArmorKind.GameArmor, option.ItemId, option.Name, option.Available, option.Tag, option.IsNonPlayer))
			.ToArray();
		// 多选：勾选集合由 VpkTargetArmors 的 setter 按盘上记录预勾；单选的老属性留空。
		SelectedVpkTargetArmor = null;
	}

	public IReadOnlyList<VpkTargetArmorOption> VpkTargetArmors
	{
		get => _vpkTargetArmors;
		private set
		{
			if (!SetField(ref _vpkTargetArmors, value)) return;
			// 候选一换，下拉也跟着换：专属模式多选（预勾盘上记录的目标），否则单选。
			VpkTargetPicker.SetOptions(
				value,
				_vpkExclusiveMode && value.Any(option => option.IsExclusiveOption),
				_vpkFile.ExclusiveTargets.Select(target => target.ItemId),
				_selectedVpkTargetArmor);
		}
	}

	public VpkTargetArmorOption? SelectedVpkTargetArmor
	{
		get => _selectedVpkTargetArmor;
		set
		{
			if (!SetField(ref _selectedVpkTargetArmor, value)) return;
			VpkTargetPicker.SelectSingle(value);
		}
	}

	private VpkTargetPickerViewModel? _vpkTargetPicker;

	/// <summary>目标护甲下拉的状态：可输入搜索；普通模式单选，专属模式多选（勾选框 + 标签 + 全选）。</summary>
	public VpkTargetPickerViewModel VpkTargetPicker
	{
		get
		{
			if (_vpkTargetPicker is not null) return _vpkTargetPicker;
			_vpkTargetPicker = new VpkTargetPickerViewModel();
			_vpkTargetPicker.SelectionChanged += (_, _) =>
			{
				// 多选的勾选集合在点「切换」时直接读；这里只把单选同步回老的 SelectedVpkTargetArmor。
				if (_vpkTargetPicker.IsMultiple) return;
				SetField(ref _selectedVpkTargetArmor, _vpkTargetPicker.Selected?.Option, nameof(SelectedVpkTargetArmor));
			};
			return _vpkTargetPicker;
		}
	}

	/// <summary>「继续并记住」之后为真：这个 Mod 专属编译失败的目标自动跳过。界面上有按钮能清掉。</summary>
	public bool VpkAutoSkipFailedTargets => _vpkFile.AutoSkipFailedTargets;

	private RelayCommand? _clearVpkAutoSkipCommand;

	public RelayCommand ClearVpkAutoSkipCommand => _clearVpkAutoSkipCommand ??= new(async _ =>
	{
		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		_vpkFile = _vpkFile with { AutoSkipFailedTargets = false };
		OnPropertyChanged(nameof(VpkAutoSkipFailedTargets));
		try { await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true); }
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			VpkComponentStatus = "清除记忆失败：" + exception.Message;
			return;
		}

		SetVpkStatus("已清除：以后专属编译失败时会重新询问。");
	});

	// 命令对象要缓存：属性每次 new 一个会让 CanExecute 的重新查询失去意义。
	private RelayCommand? _applyVpkTargetArmorCommand;
	private RelayCommand? _applyVpkComponentsCommand;

	/// <summary>
	/// 「切换」与「应用」都<b>不做禁用</b>，能不能做由点击之后判断。
	/// <para>
	/// <b>为什么去掉 CanExecute。</b><see cref="RelayCommand"/> 没有挂
	/// <c>CommandManager.RequerySuggested</c>，而这两个命令的判据
	/// （<c>SelectedVpkTargetArmor</c> / <c>HasVpkComponentTree</c>）都是<b>异步加载完之后</b>才有值的。
	/// 按钮在模板实例化那一刻问过一次 <c>CanExecute</c>，得到 <c>false</c> 之后就再也没有人重新问
	/// —— 界面上的表现是"这个按钮永远是灰的，重启管理器也一样"。
	/// </para>
	/// <para>
	/// 禁用的本意是"别让用户点了没反应"。更好的做法是<b>点了之后把话说清楚</b>：
	/// 目标没变就说没变，没选就提示去选。这比一个永远灰着的按钮好，因为灰按钮不解释自己。
	/// </para>
	/// </summary>
	public RelayCommand ApplyVpkTargetArmorCommand => _applyVpkTargetArmorCommand ??= new(
		async _ => await ApplyVpkTargetArmorAsync());

	public RelayCommand ApplyVpkComponentsCommand => _applyVpkComponentsCommand ??= new(
		async _ => await ApplyVpkComponentsAsync());

	private RelayCommand? _createVpkGroupCommand;

	/// <summary>
	/// 「新建组」：把树上当前选中的那一块（或那一整棵）建成一个命名组。
	/// <para>
	/// <b>为什么用"当前选中"而不是让用户逐节勾选。</b>树上的勾选框表达的是"这一块画不画"，
	/// 不是"选中"；把两种语义压到同一个控件上，用户会分不清"我勾了它是让它显示，还是让它进组"。
	/// 选中态是 <see cref="TreeSurface"/> 本来就有的，用它最省事也最不容易误解。
	/// </para>
	/// </summary>
	public RelayCommand CreateVpkGroupCommand => _createVpkGroupCommand ??= new(
		async _ => await CreateVpkGroupAsync());

	/// <summary>每次详情页刷新时重算：是不是 VPK 版、组件树长什么样。</summary>
	private void RefreshVpk()
	{
		// 先把手上的防抖写盘落地：用户切走之前勾的那几下不能丢。
		FlushVpkStateSave();
		_vpkFile = VpkWardrobeFile.Default;
		_vpkStatus = string.Empty;
		// 专属模式跟着 Mod 走：换到别的 Mod 时丢掉上一个 Mod 的专属下拉。
		_vpkExclusiveCatalogVersion++;
		if (_vpkExclusiveMode)
		{
			_vpkExclusiveMode = false;
			OnPropertyChanged(nameof(VpkExclusiveMode));
			VpkTargetArmors = _vpkNormalTargetArmors;
		}
		VpkTree = null;
		VpkGroups = [];
		VpkGroupStatus = string.Empty;
		_sectionGroups = SectionGroupDocument.Empty;
		SelectedVpkTreeNodes = [];
		NewVpkGroupName = string.Empty;
		IsVpkMod = false;
		RaiseKitVpkHintChanged();
		HasSectionalSource = false;
		OnPropertyChanged(nameof(IsVpkMod));
		OnPropertyChanged(nameof(HasSectionalSource));
		OnPropertyChanged(nameof(HasRebuildSource));
		OnPropertyChanged(nameof(ShowPatchTools));
		RaiseCanConvertToSectionalChanged();
		OnPropertyChanged(nameof(ShowVpkComponentCard));
		OnPropertyChanged(nameof(EnableVpkComponentTree));
		OnPropertyChanged(nameof(HasVpkComponentTree));
		OnPropertyChanged(nameof(CanOpenSectionalEditor));
		OnPropertyChanged(nameof(VpkComponentStatus));

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		// 来源包在不在，与"是不是分件模组"是两件事：转出来的分件模组没有来源 .vpk，
		// 所以它没有"用来源重构建"这个动作。
		HasSourceVpk = VpkRebuildService.FindSourceVpkCount(directory) != 0;
		// 另一档来源：根上有 patch + 转换依据（legacy2sectional 的输入）。
		// 两者都在时以 .vpk 为准（见 HasSectionalSource 的注释）。
		HasSectionalSource = SectionalSourceFile.Exists(directory);
		OnPropertyChanged(nameof(HasSectionalSource));
		OnPropertyChanged(nameof(HasRebuildSource));
		if (!VpkModWardrobeService.IsVpkMod(directory)) return;

		IsVpkMod = true;
		OnPropertyChanged(nameof(IsVpkMod));
		RaiseKitVpkHintChanged();
		OnPropertyChanged(nameof(ShowPatchTools));
		RaiseCanConvertToSectionalChanged();
		// 带上当前的详情请求代号：用户在异步加载途中切走时，旧结果不能盖掉新页面。
		_ = LoadVpkAsync(directory, node.Metadata.Name, _detailRequestGeneration);
	}

	/// <summary>
	/// 重算「生成sectional版」的动作状态。
	/// <para>
	/// 两件事都得做：通知属性（动作表的可见性靠它）和通知命令（按钮的可点性靠它）。
	/// <see cref="RelayCommand"/> 不挂 <c>CommandManager.RequerySuggested</c>，没人在这里
	/// Raise 一次，按钮的灰/亮就永远停在第一次绑定时的结果上。
	/// </para>
	/// </summary>
	private void RaiseCanConvertToSectionalChanged()
	{
		OnPropertyChanged(nameof(CanConvertToSectional));
		ConvertToCustomModelCommand.RaiseCanExecuteChanged();
	}

	private async Task LoadVpkAsync(string directory, string modName, int generation)
	{
		try
		{
			var file = await VpkModWardrobeService.ReadAsync(directory).ConfigureAwait(true);
			var map = await VpkModWardrobeService.ReadSectionMapAsync(directory).ConfigureAwait(true);
			if (generation != _detailRequestGeneration) return;
			_vpkFile = file;
			OnPropertyChanged(nameof(EnableVpkComponentTree));
			if (map is not null)
			{
				var label = file.BuildTreeLabel(string.IsNullOrWhiteSpace(modName) ? Path.GetFileName(directory) : modName);
				var root = VpkModWardrobeService.BuildTree(label, map);
				var tree = new WardrobeTreeViewModel(this);
				tree.LoadFromTree(root, editable: true);
				VpkTree = tree;
				// 组表与 section 表都取自**权威侧**（`vpk2patch/`）：它们描述的是"这个 VPK 有什么"，
				// 与目标护甲无关。Overwrite 只是当前变体，拿它的 Unit 键去对权威侧的掩码永远对不上。
				var components = await VpkModWardrobeService.ReadComponentsAsync(directory).ConfigureAwait(true);
				ApplyVpkGroups(components, map);
			}
			else
			{
				VpkComponentStatus = "这个 Mod 没有 sections.json（用旧版 vpk2patch 转的）；重新导入一次即可获得组件树。";
			}

			OnPropertyChanged(nameof(ShowVpkComponentCard));
			await EnsureVpkTargetArmorsAsync(directory).ConfigureAwait(true);
			// **每次进页面都按盘上的记录重选**（不只是第一次）。
			// 候选列表是缓存（`EnsureVpkTargetArmorsAsync` 在列表非空时直接返回），
			// 把"选中哪一项"也交给它，第二次进页面就会沿用上一次的选择：
			// 用户换到 O-3、离开、回来，下拉仍然显示自己的素体——而那已经不是盘上的值了。
			if (file.Exclusive)
			{
				// 上次是专属切换：下拉按记录列，并选回那一条（专属模式下不能沿用按归档列的老下拉）。
				_vpkExclusiveMode = true;
				OnPropertyChanged(nameof(VpkExclusiveMode));
				await ReloadVpkTargetOptionsAsync().ConfigureAwait(true);
			}
			else
			{
				SelectedVpkTargetArmor = ResolveCurrentTargetArmor(directory);
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException)
		{
			VpkComponentStatus = "读取 VPK 组件信息失败：" + exception.Message;
		}
	}

	/// <summary>
	/// 目标下拉的候选：**任意游戏护甲 + 这个 Mod 自己的那一具素体**。
	/// <para>
	/// <b>为什么不再列别人的素体。</b>换到别人的素体上，产物会去改那套素体的 unit 键——宿主与
	/// 它的注册表当场分家；而换到游戏护甲是"把原版护甲件全部隐藏"的另一个决定，是合法用法，
	/// 所以游戏护甲要留着。两条路都真实存在，只是不允许把别人的素体当成目标。
	/// </para>
	/// <para>
	/// 自己的素体从 <c>custom-model.json</c> 读（key + 名字），不依赖注册表同步：重构建刚写完
	/// 文件时注册表可能还没认识它，那时下拉也得有这一项。
	/// </para>
	/// </summary>
	private async Task EnsureVpkTargetArmorsAsync(string modDirectory)
	{
		if (_vpkArmorsLoading || _vpkNormalTargetArmors.Count != 0) return;
		_vpkArmorsLoading = true;
		try
		{
			var options = new List<VpkTargetArmorOption>();
			// 自己的素体排最前：它是默认目标，也是"不换甲"的那个选项。
			var ownKey = VpkModWardrobeService.TryReadOwnCustomModelKey(modDirectory);
			_ownModelArchiveId = string.IsNullOrWhiteSpace(ownKey)
				? null
				: RuntimeArmorRegistryService.CustomModelArchiveId(ownKey);
			if (_ownModelArchiveId is not null)
				options.Add(new VpkTargetArmorOption(_ownModelArchiveId, "自定义素体", VpkTargetArmorKind.CustomModel));

			try
			{
				var catalog = new EquipmentUnitCatalogService(_paths);
				var entries = await catalog.GetEntriesAsync().ConfigureAwait(true);
				options.AddRange(entries
					.Where(entry => entry.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase))
					.GroupBy(entry => entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
					.Select(group => new VpkTargetArmorOption(group.Key, group.First().DisplayName))
					.OrderBy(option => option.DisplayName, StringComparer.CurrentCulture));
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
			{
				VpkComponentStatus = "读取游戏护甲列表失败（请先在设置页重建游戏资产索引）：" + exception.Message;
			}

			_vpkNormalTargetArmors = options;
			if (!_vpkExclusiveMode) VpkTargetArmors = options;
		}
		finally
		{
			_vpkArmorsLoading = false;
		}
	}

	/// <summary>这个 Mod 自己的素体归档 id（<c>custom-model:&lt;key&gt;</c>）；没有则为 <c>null</c>。</summary>
	private string? _ownModelArchiveId;

	/// <summary>
	/// 点「切换」：把 Unit 换到下拉里选中的目标上。
	/// <para>
	/// <b>先比旧目标和新目标。</b>没变化就什么都不做——换甲是一次几秒到几十秒的重新烘焙，
	/// 而且会重写 Overwrite。用户什么都没改却看到"已换到 X"，下次就会怀疑这个按钮到底做了什么。
	/// </para>
	/// <para>
	/// 没有禁用态（见 <see cref="ApplyVpkTargetArmorCommand"/>）：没选目标、目标没变、
	/// 上一个操作还没跑完，这三种情况都在这里用一句话说清楚，而不是让按钮灰着不解释自己。
	/// </para>
	/// </summary>
	private async Task ApplyVpkTargetArmorAsync()
	{
		// 连点两下不该起两次转换。按钮不再禁用，所以这道门必须自己把。
		if (_vpkBusy)
		{
			VpkComponentStatus = "上一个操作还在跑，请等它结束。";
			return;
		}

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		// 专属模式（P5 多选）：目标是勾选的整个集合；普通模式仍是下拉里选中的那一个。
		var exclusive = _vpkExclusiveMode;
		IReadOnlyList<uint> exclusiveIds = [];
		VpkTargetArmorOption? target;
		if (exclusive)
		{
			exclusiveIds = VpkTargetPicker.CheckedItemIds;
			target = exclusiveIds.Count == 0
				? null
				: VpkTargetPicker.Choices.First(choice => choice.Option.ItemId == exclusiveIds[0]).Option;
			if (target is null)
			{
				VpkComponentStatus = "专属模式下请在下拉里勾选至少一套护甲（可输入搜索，或点「全选」），再点「切换」。";
				return;
			}
		}
		else
		{
			target = SelectedVpkTargetArmor;
			if (target is null)
			{
				VpkComponentStatus = "还没选目标：请在上方下拉里选一个（游戏护甲或自定义素体），再点「切换」。";
				return;
			}
		}

		// 专属切换（P4a）：先确认预览，再动手。关着「专属」时这一段整个跳过，下面的老流程原样不变。
		uint? exclusiveItem = exclusive ? exclusiveIds[0] : null;
		if (exclusive)
		{
			var owner = System.Windows.Application.Current?.MainWindow;
			var existingRecord = await KitConversionService.TryGetRecordAsync(node.Id, _library.ModsRootDirectory).ConfigureAwait(true);
			if (existingRecord is not null && !KitExclusiveWorkspace.IsExclusiveRecord(existingRecord))
			{
				HD2ModManager.Views.AppDialog.Show(
					owner, "这个 Mod 已有一条手写或「转专属」生成的隐藏记录，专属切换不会覆盖它；请先撤销那条记录。",
					"专属切换", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
				return;
			}

			var headMerge = _vpkFile.HeadMergeIntoBody;
			KitExclusivePreview preview;
			try
			{
				var ids = exclusiveIds;
				preview = await Task.Run(() => KitExclusiveService.PreviewAsync(
					directory, _paths, ids, headMerge, node.Id, _library.ModsRootDirectory)).ConfigureAwait(true);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
			{
				VpkComponentStatus = "生成专属预览失败：" + exception.Message;
				return;
			}

			if (!preview.Succeeded)
			{
				HD2ModManager.Views.AppDialog.Show(
					owner, string.Join(Environment.NewLine, preview.Lines), "不能专属切换",
					System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
				return;
			}

			// 临时行为：多目标部署将改用运行时软映射（下一阶段）。勾了两个及以上时预览照常显示，但不能确认、不写任何文件。
			var multiBlocked = exclusiveIds.Count > 1;
			var confirm = new HD2ModManager.Views.KitExclusivePreviewWindow(
				string.IsNullOrWhiteSpace(node.Metadata.Name) ? node.RelativePath : node.Metadata.Name, preview,
				multiBlocked ? "多目标部署改用映射，下一步开放" : null);
			if (confirm.ShowDialog() != true || multiBlocked) return;
		}
		else
		{
			// 切回普通切换 / 自己的素体：先删掉专属记录（只删自己写的），再编译。
			// 先删后编译：任何中途失败的状态都是「没有隐藏规则」，最坏是多显示一份原版，不会藏掉不该藏的东西。
			var cleared = await KitExclusiveWorkspace.SyncAsync(node.Id, _library.ModsRootDirectory, directory, null).ConfigureAwait(true);
			if (!cleared.Ok)
			{
				VpkComponentStatus = cleared.Error ?? "清理专属记录失败。";
				return;
			}
		}

		// **不判断"目标有没有变"。**
		//
		// 编译层的约定是"每次从 `sectional/` 重新编译到 `Overwrite/`"，不做增量、不做缓存，
		// 也不靠"看起来没变"来省一次。这里以前有一道 `current == target → 直接返回` 的短路，
		// 它把"要不要编译"又判断了一遍：状态一旦与盘上不一致（例如上一次切换没落盘），
		// 用户点了按钮却得到"目标没有变化"，而产物其实还是旧的。判断只该有一处：编译本身。

		_vpkBusy = true;

		// 换甲同样是几分钟的转换：**过程信息走消息中心**（阶段 / 进度 / 取消），
		// 卡片只留最后那句话和"某个部位没有落点"这类必须被看见的警告。
		// 与「重构建」用同一个任务类别：它们都是"用来源 VPK 重出一份产物"。
		var task = _backgroundTasks?.Enqueue(
			BackgroundTaskKind.RebuildVpk,
			"切换目标护甲",
			$"{node.Metadata.Name} → {target.DisplayName}",
			origin: "Mod 详情",
			userVisibleReason: "把素体的 Unit 换到目标的键上（游戏护甲或自己的自定义素体），并按当前勾选重新生成产物。",
			suggestedAction: "完成后重启游戏生效。",
			presentation: BackgroundTaskPresentation.Foreground);
		var cancellation = task?.CancellationToken ?? CancellationToken.None;
		task?.MarkRunning($"正在把 Unit 换到 {target.DisplayName}…");

		void Report(string stage)
		{
			if (task is not null) task.UpdateStage(stage);
			else VpkComponentStatus = stage;
		}

		try
		{
			// 换甲同样把当前勾选一起烘进去：换目标与改开关是同一件事的两半，
			// 分开做会出现"换了目标、开关却回到全开"。
			// 多目标有失败时问用户：回滚 / 继续跳过 / 继续并记住（此刻还没写任何文件）。
			var rememberSkip = false;
			async ValueTask<bool> AskTargetFailures(IReadOnlyList<SectionalTargetFailure> failures, int succeeded)
			{
				var dispatcher = System.Windows.Application.Current.Dispatcher;
				var dialog = await dispatcher.InvokeAsync(() =>
				{
					var window = new HD2ModManager.Views.KitTargetFailureWindow(failures, succeeded);
					window.ShowDialog();
					return window;
				});
				rememberSkip = dialog.Choice == HD2ModManager.Views.KitTargetFailureChoice.ContinueAndRemember;
				return dialog.Choice != HD2ModManager.Views.KitTargetFailureChoice.Rollback;
			}

			var outcome = await ModCompileService.CompileAsync(
				node.Id.Value.ToString("N"),
				directory,
				new VpkHostContext(SettingsService.GetGameDataFolder(), SettingsService.CreateStoragePaths()),
				[target.ArchiveId],
				_vpkFile.State.Disabled,
				Report,
				cancellation,
				exclusiveItemId: exclusiveItem,
				exclusiveItemIds: exclusive ? exclusiveIds : null,
				autoSkipFailedTargets: exclusive ? _vpkFile.AutoSkipFailedTargets : null,
				onTargetFailures: exclusive ? new SectionalTargetFailureHandler(AskTargetFailures) : null).ConfigureAwait(true);
			if (!outcome.Ok)
			{
				var error = outcome.Error ?? "换甲失败。";
				task?.MarkFailed(error);
				SetVpkStatus(error, NotificationLevel.Error, 20);
				return;
			}

			// 两种结果的说法必须不同：退回来源不是"换甲成功"，它压根没搬运几何。
			var rekey = outcome.Rekey;
			var warnings = rekey?.Warnings ?? Array.Empty<string>();
			var summary = exclusive
				? outcome.ExclusiveTargets.Count > 1
					? $"已专属切换到 {outcome.ExclusiveTargets.Count} 套护甲：{rekey?.ReplacedUnits ?? 0} 份模型部件落到独有部件上"
						+ $"（承载件 {outcome.CarrierCount} 份、合并模型 {outcome.MergedModelCount} 份），"
						+ $"{outcome.ExclusiveTargets.Count(seed => seed.NeedsRule)} 套护甲的原版部件会在穿它们的角色身上按实例隐藏，下次部署后生效。"
					: $"已专属切换到 {outcome.ExclusiveTargets.FirstOrDefault()?.TargetName ?? target.StoredName}（{outcome.ExclusiveTargets.FirstOrDefault()?.ItemId ?? exclusiveItem}）："
						+ $"{rekey?.ReplacedUnits ?? 0} 份模型部件落到独有部件上，"
						+ (outcome.Exclusive is { NeedsRule: true } rule
							? $"{rule.Hide.Count} 个原版部件会在穿它的角色身上按实例隐藏，下次部署后生效。"
							: "没有需要隐藏的原版部件。")
				: outcome.Rekeyed
				? rekey?.RestoredFromSource == true
					? $"已退回自定义素体（{target.DisplayName}）：来源 patch 已原样复制进 Overwrite，开关继续有效。"
					: $"已换到 {target.DisplayName}：替换 {rekey?.ReplacedUnits ?? 0} 个 Unit、隐藏 {rekey?.HiddenMeshes ?? 0} 个网格。"
				// 自身素体：没有换甲这一步，别说"替换 0 个 Unit"。
				: $"已按 {target.DisplayName} 编译生效层（自定义素体，unit 键不变）。";

			// 专属切换：把隐藏规则记进 workspace（先编译后记录，见上面「先删后编译」的对称理由）。
			// 记不上时产物已经是专属形态但没有隐藏规则：只会多显示一份原版，不会藏掉不该藏的东西，让用户再点一次「切换」。
			if (exclusive)
			{
				var synced = await KitExclusiveWorkspace.SyncTargetsAsync(
					node.Id, _library.ModsRootDirectory, directory, outcome.ExclusiveTargets).ConfigureAwait(true);
				if (!synced.Ok)
					warnings = warnings.Concat([$"隐藏规则没能记下来（再点一次「切换」重试）：{synced.Error}"]).ToArray();

				// 被跳过的目标：从勾选里去掉，并把原因告诉用户。
				if (outcome.SkippedTargets.Count != 0)
				{
					foreach (var skipped in outcome.SkippedTargets)
					{
						var choice = VpkTargetPicker.Choices.FirstOrDefault(item => item.Option.ItemId == skipped.ItemId);
						if (choice is not null) choice.IsChecked = false;
					}

					warnings = warnings.Concat(outcome.SkippedTargets.Select(item => $"已跳过 {item.Title}：{item.Reason}")).ToArray();
				}
			}

			// **用户按下「切换」= 这次的目标就是他的意图，必须落盘。**
			// 编译基座只读配置（不写 wardrobe.json），所以由这个"用户动作"负责写：
			// 少了这一步，离开页面再回来读到的还是旧目标（下拉弹回自己的素体），
			// 而下一次「切换」还会因为"当前目标 == 选中目标"被判成"没有变化"。
			// 专属多选：记下实际编进去的目标列表（被跳过的不记）；第一项同步到单个字段。
			var kept = exclusive
				? outcome.ExclusiveTargets.Select(seed => new VpkWardrobeTarget(seed.ItemId, seed.TargetName)).ToArray()
				: [];
			var firstKept = kept.Length != 0 ? kept[0] : null;
			var firstOption = firstKept is null
				? target
				: VpkTargetPicker.Choices.FirstOrDefault(item => item.Option.ItemId == firstKept.ItemId)?.Option ?? target;
			_vpkFile = _vpkFile with
			{
				TargetArmorArchiveId = firstOption.ArchiveId,
				TargetArmorName = firstOption.StoredName,
				TargetItemId = firstKept?.ItemId ?? exclusiveItem,
				Exclusive = exclusive,
				Targets = kept.Length > 1 ? kept : null,
				AutoSkipFailedTargets = _vpkFile.AutoSkipFailedTargets || (exclusive && rememberSkip),
			};
			OnPropertyChanged(nameof(VpkAutoSkipFailedTargets));
			var persisted = true;
			try
			{
				await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException)
			{
				persisted = false;
				warnings = warnings.Concat([$"目标护甲没能记进 wardrobe.json：{exception.Message}"]).ToArray();
			}

			// 记不住目标时把话说清楚：产物已经是新目标了，但下次进页面会显示旧目标。
			if (!persisted)
				summary += "（这次编译已经生效，但目标没记住；下次进页面可能显示旧目标）";

			// ⚠️ **"某个部位没有落点"必须留在卡上。** 换甲会照常报成功，但那个部位的来源 Unit
			// 整份没写出去、原版又已被隐藏——玩家进游戏看到的是"衣服还在、人体胳膊不见了"。
			// 以前这些提示只存在于 CLI 的 warnings 里，界面完全不显示，等于没查。
			var warningText = warnings.Count == 0
				? string.Empty
				: " 注意：" + string.Join(" ", warnings.Take(2))
					+ (warnings.Count > 2 ? $"（另有 {warnings.Count - 2} 条）" : string.Empty);
			task?.SetOutputArtifacts(directory, null);
			if (warnings.Count != 0) task?.UpdateStage($"注意：{warnings[0]}");
			task?.MarkCompleted();
			SetVpkStatus(summary + warningText,
				warnings.Count == 0 ? NotificationLevel.Info : NotificationLevel.Warning,
				warnings.Count == 0 ? 10 : 20);

			_ = RefreshKitPollutionAsync(node.Id, _detailContentFacts);
			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			// **不重载整页**：树与组件取自**权威侧**（sectional 的 section 表），
			// 换甲只改 `Overwrite/` 里那份产物，权威侧一个字节都没动。
			// 以前这里重载一次，整列组件树 + 材质列表被重建，卡面就抖一下。
		}
		catch (OperationCanceledException)
		{
			task?.MarkCanceled();
			VpkComponentStatus = "换甲已取消。";
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			task?.MarkFailed(exception.Message);
			VpkComponentStatus = "换甲失败：" + exception.Message;
		}
		finally
		{
			_vpkBusy = false;
		}
	}

	/// <summary>
	/// 当前实际生效的目标：<c>wardrobe.json</c> 里记着的那个；没记过就是这个 Mod 自己的素体。
	/// <para>
	/// <b>为什么要单独算一次而不是直接读 <see cref="SelectedVpkTargetArmor"/>。</b>下拉里的选中项是
	/// "用户想换成什么"，这里的答案是"现在是什么"。两者相同才叫没有变化。
	/// </para>
	/// <para>
	/// 记着的那件护甲可能已经不在候选里（护甲索引重建过、或者那个自定义素体被删了）。
	/// 那时仍然返回它——否则比对会退回"自己的素体"，于是界面说"目标没变"而实际目标并不是它。
	/// </para>
	/// </summary>
	/// <summary>
	/// 下拉里选中的目标：**盘上记录的那个**（游戏护甲或自己的素体），没有记录就是自己的素体。
	/// <para>
	/// 记录里若是**别人的素体**（旧版本留下的，现在已经不许选），退回自己的素体——
	/// 那种目标继续留着只会让下一次编译报"选了别人的素体"。
	/// </para>
	/// </summary>
	private VpkTargetArmorOption? ResolveCurrentTargetArmor(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!string.IsNullOrWhiteSpace(_vpkFile.TargetArmorArchiveId))
		{
			var recorded = _vpkFile.TargetArmorArchiveId;
			var match = VpkTargetArmors.FirstOrDefault(option =>
				option.ArchiveId.Equals(recorded, StringComparison.OrdinalIgnoreCase));
			if (match is not null)
				return match;

			var recordedIsForeignModel = recorded.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(recorded, _ownModelArchiveId, StringComparison.OrdinalIgnoreCase);
			if (recordedIsForeignModel)
			{
				return OwnModelOption();
			}

			// 游戏护甲不在候选里（索引重建过、那件没了）：照实显示记录，让用户自己决定要不要换。
			return new VpkTargetArmorOption(recorded, _vpkFile.TargetArmorName ?? recorded);
		}

		return OwnModelOption();
	}

	private VpkTargetArmorOption? OwnModelOption()
		=> _ownModelArchiveId is null
			? null
			: VpkTargetArmors.FirstOrDefault(option =>
					option.ArchiveId.Equals(_ownModelArchiveId, StringComparison.OrdinalIgnoreCase))
				?? new VpkTargetArmorOption(_ownModelArchiveId, "自定义素体", VpkTargetArmorKind.CustomModel);

	/// <summary>
	/// 点"应用"：先把勾选状态落盘，再烘进 patch。
	/// <b>顺序不能反</b>——烘焙是从状态算出来的，状态没写就先烘，崩在中间会出现
	/// "patch 已改、状态还是旧的"，下一次点应用就会把旧状态烘回去。
	/// <para>
	/// 与「切换」一样<b>不做禁用</b>（原因见 <see cref="ApplyVpkTargetArmorCommand"/>）：
	/// 上一次操作还没跑完时点它，这里给一句话而不是让按钮灰着。
	/// </para>
	/// </summary>
	private async Task ApplyVpkComponentsAsync()
	{
		if (_vpkBusy)
		{
			VpkComponentStatus = "上一个操作还在跑，请等它结束。";
			return;
		}

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		_vpkBusy = true;
		// 声明在 try 外面：取消/异常时也要能在消息中心把这条任务收尾，
		// 否则它会一直挂在"进行中"。
		BackgroundTaskItem? task = null;
		try
		{
			VpkComponentStatus = "正在保存勾选…";
			await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);

			// 优先就地烘焙：Overwrite 已经在的话，勾选变化只是改几个 numIndices，
			// 不必重跑整条换甲（那是分钟级，这是秒级）。
			//
			// **本轮关掉这条快捷路径**（2026-11-08）：编译器的地基是"任何一处变动都从 sectional
			// 完整重算一次"，多留一条直接改字节的写者就多一个能跟盘上状态不同步的分支。
			// 实现与测试都留着，等链路稳定后再打开。
			if (EnableInPlaceBake && await TryBakeInPlaceAsync(directory).ConfigureAwait(true))
				return;

			// **重新从 vpk2patch 生成 Overwrite**，而不是就地改 Overwrite。
			//
			// 为什么不是就地烘：Overwrite 是派生品，它的 Unit 键取决于当时的目标护甲。
			// 就地改会让"这份变体"慢慢偏离权威事实，而用户改一次设置就想看到"按新方案重新生成"。
			// 生成过程同时完成两件事：把 Unit 换到目标护甲的键上、把勾选状态烘进去——
			// 两件事都要那份"来源 → 目标"的映射，所以只能在换甲里面做。
			// 「应用」也是重跑一次换甲（几分钟）：过程走消息中心，卡片只留结论。
			VpkComponentStatus = "正在从 vpk2patch 重新生成 Overwrite…";
			// **不在这里拦。** 没有记录目标时 `targets` 是 null，编译基座会按"自身素体"编译
			// （unit 键不变 + 烘焙勾选）——那是合法结果，不该被一道预先的门禁挡住。
			// 就算应用出来的是错的，也应该写进去、然后报错：报错能查，静默不写查不了。
			var targets = ResolveApplyTargets(directory);

			task = _backgroundTasks?.Enqueue(
				BackgroundTaskKind.RebuildVpk,
				"应用组件勾选",
				node.Metadata.Name,
				origin: "Mod 详情",
				userVisibleReason: "把组件树上的勾选烘进换甲产物：重新从 vpk2patch 生成 Overwrite。",
				suggestedAction: "完成后重启游戏生效。",
				presentation: BackgroundTaskPresentation.Foreground);
			var cancellation = task?.CancellationToken ?? CancellationToken.None;
			task?.MarkRunning("正在从 vpk2patch 重新生成 Overwrite…");

			void Report(string stage)
			{
				if (task is not null) task.UpdateStage(stage);
				else VpkComponentStatus = stage;
			}

			var outcome = await ModCompileService.CompileAsync(
				node.Id.Value.ToString("N"),
				directory,
				new VpkHostContext(SettingsService.GetGameDataFolder(), SettingsService.CreateStoragePaths()),
				targets,
				_vpkFile.State.Disabled,
				Report,
				cancellation,
				exclusiveItemIds: ResolveApplyExclusiveIds()).ConfigureAwait(true);
			if (!outcome.Ok)
			{
				var error = outcome.Error ?? "重新生成失败。";
				task?.MarkFailed(error);
				VpkComponentStatus = error;
				return;
			}

			task?.SetOutputArtifacts(directory, null);
			task?.MarkCompleted();
			await SyncKitExclusiveAfterRecompileAsync(node.Id, directory, outcome).ConfigureAwait(true);
			// 自身素体（没有换甲）时别说"替换 0 个 Unit"：那读起来像失败。
			VpkComponentStatus = !outcome.Rekeyed
				? "已按当前条件重新编译生效层（自定义素体）。"
				: outcome.Rekey?.RestoredFromSource == true
					? "已按当前勾选重新生成（自定义素体模式）。"
					: $"已按当前勾选重新生成：替换 {outcome.Rekey?.ReplacedUnits ?? 0} 个 Unit、隐藏 {outcome.Rekey?.HiddenMeshes ?? 0} 个网格。";
			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			task?.MarkCanceled();
			VpkComponentStatus = "应用已取消。";
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			task?.MarkFailed(exception.Message);
			VpkComponentStatus = "应用失败：" + exception.Message;
		}
		finally
		{
			_vpkBusy = false;
		}
	}

	/// <summary>
	/// "应用"时要重新生成到哪：记过的目标护甲；没记过就用这个 Mod 自己的素体（恒等 → 退回来源 + 烘焙）。
	/// <para>
	/// 返回 <c>null</c> 表示既没有目标也没有登记素体——那时唯一能做的正确事情是让用户先选目标，
	/// 而不是替他猜一个游戏护甲（旧版就是这样悄悄换到 RE-2310 的）。
	/// </para>
	/// </summary>

	/// <summary>
	/// 翻转「头身合并」之后要做的事：先落盘，再按新设置重新生成一次 Overwrite。
	/// <para>
	/// <b>为什么必须重新生成而不是只存状态。</b>两个状态写出来的 patch 完全不同
	/// （头在胸部槽位 / 头在头盔套件），只存状态的话用户会看到开关翻了、游戏里什么都没变。
	/// </para>
	/// <para>
	/// 还没有目标护甲（Overwrite 本来就不存在）时只存状态：那种 Mod 的第一次换甲会自己读到它。
	/// </para>
	/// </summary>
	private async Task ApplyHeadMergeAsync(bool enabled)
	{
		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);

		try
		{
			await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			VpkComponentStatus = "保存头身合并开关失败：" + exception.Message;
			return;
		}

		if (_vpkBusy)
		{
			SetVpkStatus("头身合并已保存；上一个操作还在跑，请等它结束后点「应用」重新生成。", notify: false);
			return;
		}

		var targets = ResolveApplyTargets(directory);
		if (targets is null)
		{
			SetVpkStatus(
				enabled
					? "已开启头身合并：这个 Mod 还没选过目标护甲，下次「切换」时生效。"
					: "已关闭头身合并：这个 Mod 还没选过目标护甲，下次「切换」时生效。",
				notify: false);
			return;
		}

		_vpkBusy = true;
		// 头身合并要重跑一次换甲（几分钟）：过程同样走消息中心，卡片只留结论。
		var task = _backgroundTasks?.Enqueue(
			BackgroundTaskKind.RebuildVpk,
			enabled ? "开启头身合并" : "关闭头身合并",
			node.Metadata.Name,
			origin: "Mod 详情",
			userVisibleReason: "改变头画在哪一件装备上，必须按新方案重新生成一次换甲产物。",
			suggestedAction: "完成后重启游戏生效。",
			presentation: BackgroundTaskPresentation.Foreground);
		var cancellation = task?.CancellationToken ?? CancellationToken.None;
		task?.MarkRunning(enabled ? "正在把头并进身体…" : "正在把头放回头盔套件…");

		void Report(string stage)
		{
			if (task is not null) task.UpdateStage(stage);
			else VpkComponentStatus = stage;
		}

		try
		{
			var outcome = await ModCompileService.CompileAsync(
				node.Id.Value.ToString("N"),
				directory,
				new VpkHostContext(SettingsService.GetGameDataFolder(), SettingsService.CreateStoragePaths()),
				targets,
				_vpkFile.State.Disabled,
				Report,
				cancellation,
				headMergeIntoBody: enabled,
				exclusiveItemIds: ResolveApplyExclusiveIds()).ConfigureAwait(true);

			if (!outcome.Ok)
			{
				var error = outcome.Error ?? "头身合并切换失败。";
				task?.MarkFailed(error);
				SetVpkStatus(error, NotificationLevel.Error, 20);
				return;
			}

			// 说什么由**实际结果**决定，而不是由开关状态决定。
			// 开关打开却什么也没合并（目标是自己素体 / 目标胸部只有一层 / 没有配套头盔）时，
			// 一句"已开启"会让用户下次进游戏发现没变化，然后再也不信这个开关。
			// 自身素体与换甲两条路现在都填 HeadMerge，所以**不再按 Rekeyed 分叉**——
			// 那种分叉正是"开关翻了、界面说已开启、游戏里没变化"的来源。
			var summary = outcome.Rekey?.HeadMerge switch
			{
				"applied" => outcome.Rekeyed
					? "头身合并已开启：头并进护甲，头盔 item 不再有模型。"
					: "头身合并已开启：头件已挂到胸部槽位（具体层号见消息中心）。",
				"single-layer" => "开关已记录，但这一次没有合并：目标胸部槽位装不下「身体 + 头」两件几何。",
				"no-helmet-kit" => "开关已记录，但当前目标没有配套头盔归档，无从合并。",
				_ => enabled
					? "头身合并没有生效（原因见消息中心）。"
					: "头身合并已关闭：头回到头盔套件。",
			};
			var warningText = (outcome.Rekey?.Warnings ?? Array.Empty<string>()).Count == 0
				? string.Empty
				: " " + string.Join(" ", (outcome.Rekey?.Warnings ?? Array.Empty<string>()).Take(2))
					+ ((outcome.Rekey?.Warnings ?? Array.Empty<string>()).Count > 2 ? $"（另有 {(outcome.Rekey?.Warnings ?? Array.Empty<string>()).Count - 2} 条）" : string.Empty);
			task?.SetOutputArtifacts(directory, null);
			await SyncKitExclusiveAfterRecompileAsync(node.Id, directory, outcome).ConfigureAwait(true);
			if ((outcome.Rekey?.Warnings ?? Array.Empty<string>()).Count != 0) task?.UpdateStage($"注意：{(outcome.Rekey?.Warnings ?? Array.Empty<string>())[0]}");
			task?.MarkCompleted();
			SetVpkStatus(summary + warningText);

			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			// **不重载整页**：这次编译只改了 `Overwrite/custom-model.json`，
			// 组件树（取自权威 sectional）与材质列表都没变。重载会把它们重建一遍，
			// 表现就是"每翻一次开关，卡面跟着抖一下"。
		}
		catch (OperationCanceledException)
		{
			task?.MarkCanceled();
			VpkComponentStatus = "头身合并切换已取消。";
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			task?.MarkFailed(exception.Message);
			VpkComponentStatus = "头身合并切换失败：" + exception.Message;
		}
		finally
		{
			_vpkBusy = false;
		}
	}


	/// <summary>「应用」「头身合并」「重构建」沿用上次切换的专属状态：盘上是专属切换就继续专属，否则老做法。</summary>
	private IReadOnlyList<uint>? ResolveApplyExclusiveIds()
		=> _vpkFile.Exclusive && _vpkFile.ExclusiveTargets.Count != 0 ? _vpkFile.ExclusiveTargets.Select(item => item.ItemId).ToArray() : null;

	/// <summary>
	/// 重新编译（应用勾选 / 翻头身合并）之后同步专属记录：头身合并会改变隐藏集，所以每次都按这次的结果重写。
	/// 失败只报告、不阻断（产物已写好；规则没记上的最坏结果是多显示一份原版）。
	/// </summary>
	private async Task SyncKitExclusiveAfterRecompileAsync(ModNodeId nodeId, string directory, ModCompileOutcome outcome)
	{
		if (!_vpkFile.Exclusive) return;
		var synced = await KitExclusiveWorkspace.SyncTargetsAsync(
			nodeId, _library.ModsRootDirectory, directory, outcome.ExclusiveTargets).ConfigureAwait(true);
		if (!synced.Ok)
			SetVpkStatus("隐藏规则没能记下来（再点一次「切换」重试）：" + synced.Error, NotificationLevel.Warning, 20);
		_ = RefreshKitPollutionAsync(nodeId, _detailContentFacts);
	}

	/// <summary>
	/// 「应用」时的编译目标：盘上记录的那个（游戏护甲或自己的素体），没有记录就是自己的素体。
	/// 记录里若是别人的素体（旧版本留下的），退回自己的素体——那种目标现在已经不许选。
	/// </summary>
	private IReadOnlyList<string>? ResolveApplyTargets(string directory)
	{
		var ownKey = VpkModWardrobeService.TryReadOwnCustomModelKey(directory);
		var ownArchiveId = ownKey is null ? null : RuntimeArmorRegistryService.CustomModelArchiveId(ownKey);
		var recorded = _vpkFile.TargetArmorArchiveId;
		if (!string.IsNullOrWhiteSpace(recorded))
		{
			var isForeignModel = recorded.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(recorded, ownArchiveId, StringComparison.OrdinalIgnoreCase);
			if (!isForeignModel)
				return [recorded];
		}

		return ownArchiveId is null ? null : [ownArchiveId];
	}

	/// <summary>
	/// 就地烘焙：直接改 <c>Overwrite/</c> 里那份 patch 的 <c>numIndices</c>。
	/// <para>
	/// <b>返回 false = "这次不适合就地烘"</b>（还没有 Overwrite、或它没有自己的
	/// <c>sections.json</c>），调用方应当退回"重跑换甲"那条路。
	/// <b>返回 true = 烘焙已经完成</b>（成功，或者已经给出了失败原因）。
	/// </para>
	/// <para>
	/// <b>为什么能就地烘。</b><see cref="WardrobeSectionBaker"/> 本来就是就地、幂等、可逆的：
	/// 每个要写的 dword 当前必须 ∈ {0, 原值}，否则拒绝写入。缺的只是"来源键 → 目标键"那张对照表，
	/// 换甲时已经写进 <c>sections.json</c> 的 <c>sourceUnit</c> 里了。
	/// </para>
	/// </summary>
	private async Task<bool> TryBakeInPlaceAsync(string modDirectory)
	{
		var overwrite = Path.Combine(modDirectory, ModNodePayloadLayout.OverwriteDirectoryName);
		// 只改管理器自己生成的 Overwrite。没有归属标记的目录可能是用户手工放的，
		// 就地改它的字节是**不可逆**的（numIndices 被清零后无法还原），所以宁可退回"重跑换甲"。
		if (!Directory.Exists(overwrite) || !ManagedOverwriteDirectory.IsManaged(overwrite))
			return false;
		var mapPath = Path.Combine(overwrite, WardrobeSectionMapFile.FileName);
		if (!File.Exists(mapPath))
			return false;
		var patchPath = Directory
			.EnumerateFiles(overwrite, "*.patch_*", SearchOption.TopDirectoryOnly)
			.FirstOrDefault(path => !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase));
		if (patchPath is null)
			return false;

		try
		{
			VpkComponentStatus = "正在把勾选就地烘进 Overwrite…";
			var map = await WardrobeSectionMapFile.ReadAsync(mapPath).ConfigureAwait(true);
			var translation = VpkSectionBakePlan.Translate(_vpkFile.State.Disabled, map);
			// 载荷是只读的：先清、写完再补回去。
			VpkPayloadSwap.ClearReadOnly(patchPath);
			var baked = await WardrobeSectionBaker.BakeAsync(patchPath, map, translation.DisabledByTargetUnit).ConfigureAwait(true);
			VpkPayloadSwap.MarkReadOnly(patchPath);

			var dropped = VpkSectionBakePlan.Describe(translation);
			VpkComponentStatus = baked.ToSummary()
				+ (dropped.Length == 0 ? string.Empty : " " + dropped)
				+ " 重启游戏后生效。";

			if (TryGetCurrentNode(out var bakedNode))
			{
				await _library.InvalidateContentNodeAsync(bakedNode.Id, CancellationToken.None).ConfigureAwait(true);
				await _library.RefreshCommittedContentAsync([bakedNode.Id], ModContentChangeKind.Changed,
					alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			// 就地烘失败不该把用户卡住：退回"重跑换甲"那条路，它一定能把状态烘进去。
			VpkComponentStatus = "就地烘焙没成功（" + exception.Message + "），改为重新生成…";
			return false;
		}
	}

	private async Task PersistVpkAsync()
	{
		if (!TryGetCurrentNode(out var node)) return;
		await WriteWardrobeAsync(Path.Combine(_library.ModsRootDirectory, node.RelativePath), _vpkFile).ConfigureAwait(true);
	}

	/// <summary>
	/// 写一份 <c>wardrobe.json</c>。<b>接的是快照而不是字段</b>——防抖落地时页面可能正在重置，
	/// 异步写盘读到的必须还是调用那一刻的值。
	/// </summary>
	private async Task WriteWardrobeAsync(string directory, VpkWardrobeFile file)
	{
		try
		{
			await VpkModWardrobeService.WriteAsync(directory, file).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			VpkComponentStatus = "保存开关失败：" + exception.Message;
		}
	}

	// ── IWardrobeTreeToggleStore：树只认"某个 unit 的某个 section 关没关" ──

	bool IWardrobeTreeToggleStore.ReadAllDisabled(WardrobeTreeNode node)
		=> WardrobeTree.Counts(node, IsVpkSectionDisabled) is { SectionCount: > 0 } counts && counts.AllDisabled;

	bool IWardrobeTreeToggleStore.IsMixed(WardrobeTreeNode node)
		=> WardrobeTree.Counts(node, IsVpkSectionDisabled).IsMixed;

	void IWardrobeTreeToggleStore.ApplyToggle(WardrobeTreeNode node, bool enabled)
	{
		_vpkFile = _vpkFile with { State = _vpkFile.State.SetEnabled(node, enabled) };
		// 组列表与树是同一份状态的两种视图：树上改了一节，覆盖它的那些组也要跟着变。
		foreach (var item in _vpkGroups)
			item.RaiseEnabledChanged();
		// **树自己也要重读勾选状态。**
		// 状态是级联的（点 unit ⇒ 它所有 section；子项全关 ⇒ unit 显示为关），
		// 但只有被点的那一行会自己发通知：不刷一遍，别的行仍显示旧勾选
		// ——用户看到的就是"关了主体、子项还亮着"和"子项全关、主体还亮着"。
		// 组的开关一直是这么做的（见 ApplyVpkGroupAsync），这里补上同一条。
		VpkTree?.RefreshChecks();
		// 勾一下就要落盘：以前只有「应用」会写文件，勾完直接切页就静默丢了。
		ScheduleVpkStateSave();
	}

	private bool IsVpkSectionDisabled(string unit, int sectionIndex)
		=> _vpkFile.State.IsDisabled(unit, sectionIndex);

	// ── 组（components.json）──

	/// <summary>
	/// 把组表装进界面。**没有组表不是错误**：用旧转换器产的 Mod 里没有
	/// <c>components.json</c>，那时只显示树，不该给用户一条"出错了"。
	/// </summary>
	private void ApplyVpkGroups(SectionGroupDocument? document, WardrobeSectionMapDocument sections)
	{
		_sectionGroups = document ?? SectionGroupDocument.Empty;
		_vpkSectionMap = sections;

		// 每块几何被几个组覆盖：>1 就是"关一个会连带关另一个"，界面必须说出来。
		var coverage = new Dictionary<(string Unit, int Section), int>();
		foreach (var group in _sectionGroups.Groups)
		{
			foreach (var target in group.Members)
			{
				foreach (var section in target.Sections)
				{
					var key = (target.Unit, section);
					coverage[key] = coverage.TryGetValue(key, out var count) ? count + 1 : 1;
				}
			}
		}

		var items = new List<VpkGroupItemViewModel>(_sectionGroups.Groups.Count);
		foreach (var group in _sectionGroups.Groups)
		{
			items.Add(new VpkGroupItemViewModel(
				group,
				Classify(group, sections, coverage),
				ReadGroupEnabled,
				WriteGroup,
				RenameGroup,
				DeleteGroup));
		}

		VpkGroups = items;

		// 没匹配上的必须说清：用户装了"猫耳关"却看不到组，只会以为功能没做。
		var parts = new List<string>();
		if (_sectionGroups.Groups.Count == 0)
		{
			// 没有组时不说"0 个组"——那读起来像出错。给一句可照做的提示。
			parts.Add("这个 Mod 还没有 section 组。在下面的树里点一块几何，再点「新建组」。");
		}

		if (document is { Unmatched.Count: > 0 })
		{
			// 两种来源的"没成组"共用这一行：源侧开关包找不到材质（VPK），以及
			// sectional 的来源带了 unit 却没有一段几何进产物（装饰追加失败）。原因逐条写在产物里。
			parts.Add($"{document.Unmatched.Count} 个来源没能成组："
				+ $"{string.Join("、", document.Unmatched.Take(4).Select(item => item.Name))}"
				+ (document.Unmatched.Count > 4 ? "…" : string.Empty)
				+ $"（例如：{document.Unmatched[0].Reason}）");
		}

		VpkGroupStatus = string.Join(Environment.NewLine, parts);
		OnPropertyChanged(nameof(VpkSelectionHint));
	}

	/// <summary>判定一个组能不能单独开关。三种状态的定义见 <see cref="VpkGroupState"/>。</summary>
	private static VpkGroupState Classify(
		SectionGroup group,
		WardrobeSectionMapDocument sections,
		IReadOnlyDictionary<(string Unit, int Section), int> coverage)
	{
		if (group.Members.Count == 0)
			return VpkGroupState.Stale;
		// 掩码里的 unit 必须仍然在**当前生效**的 section 表里。换甲会把 Unit 换到目标护甲的键上。
		if (group.Members.Any(target => sections.FindUnit(target.Unit) is null))
			return VpkGroupState.Stale;
		foreach (var target in group.Members)
		{
			foreach (var section in target.Sections)
			{
				if (coverage.TryGetValue((target.Unit, section), out var count) && count > 1)
					return VpkGroupState.Shared;
			}
		}

		return VpkGroupState.Independent;
	}

	/// <summary>组"开着" = 它的成员**没有全部**被关掉（与树的三态语义一致）。</summary>
	private bool ReadGroupEnabled(SectionGroup group)
	{
		if (group.Members.Count == 0)
			return false;
		return !group.Members.All(target =>
			target.Sections.All(section => _vpkFile.State.IsDisabled(target.Unit, section)));
	}

	private void WriteGroup(SectionGroup group, bool enabled)
	{
		var state = _vpkFile.State;

		// 互斥组：同名互斥组内最多一个开启。**开启**时把同组里已经开着的那些关掉。
		// 这条就是"变体替换"的全部实现——基础件与各个变体同属一个互斥组，
		// 选一个变体就等于把基础件关掉，不需要第二个"替换"字段。
		if (enabled && !string.IsNullOrWhiteSpace(group.ExclusiveGroup))
		{
			foreach (var other in _sectionGroups.EnabledInExclusiveGroup(group.ExclusiveGroup, ReadGroupEnabled))
			{
				if (string.Equals(other.EffectiveId, group.EffectiveId, StringComparison.Ordinal))
					continue;
				foreach (var target in other.Members)
					state = state.SetSectionsEnabled(target.Unit, target.Sections, false);
			}
		}

		foreach (var target in group.Members)
			state = state.SetSectionsEnabled(target.Unit, target.Sections, enabled);
		_vpkFile = _vpkFile with { State = state };
		// 树上显示的是同一份状态：不刷新就会出现"组开了、树上还关着"。
		VpkTree?.RefreshChecks();
		// 互斥组可能顺手改了别的组的状态，整列都要重画。
		foreach (var item in _vpkGroups)
			item.RaiseEnabledChanged();
		ScheduleVpkStateSave();
	}

	private void RenameGroup(SectionGroup group, string name) => _ = RenameVpkGroupAsync(group, name);

	private void DeleteGroup(SectionGroup group) => _ = DeleteVpkGroupAsync(group);

	/// <summary>新建组：把树上选中的那几块（或那几整棵）建成组。</summary>
	private async Task CreateVpkGroupAsync()
	{
		if (!TryGetCurrentNode(out var node))
			return;
		if (_selectedVpkTreeNodes.Count == 0)
		{
			VpkGroupStatus = "先在下面的树里点一块几何（或一个部位），再点「新建组」。按住 Ctrl 可多选、Shift 可选一段。";
			return;
		}

		var sections = CollectSelectedSections();
		var members = sections
			.Where(section => section.Unit is not null && section.SectionIndex is not null)
			.GroupBy(section => section.Unit!, StringComparer.Ordinal)
			.Select(group => new SectionGroupTarget(
				group.Key,
				[.. group.Select(section => section.SectionIndex!.Value).Distinct().Order()]))
			.ToArray();

		var created = SectionGroupEditor.Create(_sectionGroups, NewVpkGroupName, members);
		if (created is null)
		{
			VpkGroupStatus = "这一块下面没有可开关的几何，建不出组。";
			return;
		}

		// 新建同名组 = 用户显式要它回来：把同名的删除记录撤掉，否则下次重建又会被抑制。
		_sectionGroups = SectionGroupCustomizations.WithoutHidden(created.Value.Document, created.Value.Group.Name);
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		if (await SaveSectionGroupsAsync(directory).ConfigureAwait(true))
		{
			NewVpkGroupName = string.Empty;
			VpkGroupStatus = $"已新建组「{created.Value.Group.Name}」，覆盖 {created.Value.Group.SectionCount} 块几何。";
		}

		ReapplyVpkGroups();
	}

	private async Task RenameVpkGroupAsync(SectionGroup group, string name)
	{
		if (!TryGetCurrentNode(out var node))
			return;
		var renamed = SectionGroupEditor.Rename(_sectionGroups, group.EffectiveId, name);
		if (renamed is null)
		{
			// 名字为空或 id 找不到：不改文档，只把界面刷回去（否则输入框会停在那个空名字上）。
			VpkGroupStatus = "组名不能为空。";
			ReapplyVpkGroups();
			return;
		}

		// 改回某个名字也算"要它回来"：撤掉同名的删除记录。改名本身由重建时的合并保留（见 SectionGroupCustomizations）。
		_sectionGroups = SectionGroupCustomizations.WithoutHidden(renamed, name);
		await SaveSectionGroupsAsync(Path.Combine(_library.ModsRootDirectory, node.RelativePath)).ConfigureAwait(true);
		ReapplyVpkGroups();
	}

	private async Task DeleteVpkGroupAsync(SectionGroup group)
	{
		if (!TryGetCurrentNode(out var node))
			return;
		var removed = SectionGroupEditor.Remove(_sectionGroups, group.EffectiveId);
		if (removed is null)
			return;

		// 删组 = 记住"用户不想要它"：几何还在，但重建时不该把它自动生成回来。
		_sectionGroups = SectionGroupCustomizations.WithHidden(removed, group);
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		if (await SaveSectionGroupsAsync(directory).ConfigureAwait(true))
		{
			// 说清"几何没动"：用户最怕的是删组把模型删掉一块。
			VpkGroupStatus = $"已删除组「{group.Name}」。它的几何还在，只是不再有名字和开关；重建也不会把它加回来（重新建一个同名组即可恢复）。";
		}

		ReapplyVpkGroups();
	}

	/// <summary>把组表写回权威侧（<c>vpk2patch/</c>）。载荷是只读的，写完要把只读补回去。</summary>
	private async Task<bool> SaveSectionGroupsAsync(string modDirectory)
	{
		var payload = Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName);
		var path = Path.Combine(payload, SectionGroupFile.FileName);
		try
		{
			VpkPayloadSwap.ClearReadOnly(path);
			await SectionGroupFile.WriteAsync(payload, _sectionGroups).ConfigureAwait(true);
			VpkPayloadSwap.MarkReadOnly(path);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			VpkGroupStatus = "保存组表失败：" + exception.Message;
			return false;
		}
	}

	/// <summary>文档改过之后重画组列表（不重读文件）。</summary>
	private void ReapplyVpkGroups()
	{
		if (_vpkSectionMap is not { } map)
		{
			VpkGroups = [];
			return;
		}

		ApplyVpkGroups(_sectionGroups, map);
	}

	// ── 勾选状态的防抖保存 ──

	/// <summary>
	/// 勾一下之后安排一次写盘。<b>以前只有「应用」会写文件</b>，勾完直接切页就静默丢了。
	/// <para>
	/// <b>必须在这里就把"写给哪个目录、写什么内容"抓成快照。</b>延时窗口里用户可能已经切到
	/// 另一个 Mod：那时 <c>_vpkFile</c> 已被重置成默认值，照字段写会把**新 Mod 的**
	/// <c>wardrobe.json</c> 用一份空状态覆盖掉。
	/// </para>
	/// </summary>
	private void ScheduleVpkStateSave()
	{
		if (!TryGetCurrentNode(out var node))
			return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		var file = _vpkFile;

		_vpkStateSave?.Cancel();
		_vpkStateSave?.Dispose();
		var source = new CancellationTokenSource();
		_vpkStateSave = source;
		_ = SaveAfterDelayAsync(directory, file, source.Token);
	}

	private async Task SaveAfterDelayAsync(string directory, VpkWardrobeFile file, CancellationToken token)
	{
		try
		{
			await Task.Delay(VpkStateSaveDelay, token).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		await WriteWardrobeAsync(directory, file).ConfigureAwait(true);
	}

	/// <summary>
	/// 立刻把待写的状态落地。<b>切页 / 刷新详情之前必须调</b>——
	/// 防抖窗口只有 250 ms，但"用户在窗口内切走"是真实会发生的，不能靠时间赌。
	/// </summary>
	private void FlushVpkStateSave()
	{
		if (_vpkStateSave is null)
			return;
		_vpkStateSave.Cancel();
		_vpkStateSave.Dispose();
		_vpkStateSave = null;
		if (!TryGetCurrentNode(out var node))
			return;
		// 快照：RefreshVpk 紧接着就会把 _vpkFile 重置成 Default，异步写盘读到的必须还是旧值。
		var file = _vpkFile;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		_ = WriteWardrobeAsync(directory, file);
	}
}
