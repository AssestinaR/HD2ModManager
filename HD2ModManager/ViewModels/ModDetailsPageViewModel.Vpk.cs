using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
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
	VpkTargetArmorKind Kind = VpkTargetArmorKind.GameArmor)
{
	public override string ToString() => DisplayName;
}

/// <summary>
/// Mod 详情页的 VPK 部分：动作卡（换目标护甲）与衣物装饰组件卡。
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
	private IReadOnlyList<VpkSwitchItemViewModel> _vpkSwitches = [];
	private string _vpkSwitchStatus = string.Empty;

	/// <summary>这个 Mod 是不是 VPK 版（有 <c>vpk2patch/</c> 载荷）。</summary>
	public bool IsVpkMod { get; private set; }

	/// <summary>patch 版设置卡的可见性：VPK 版一律不显示。</summary>
	public bool ShowPatchSettingsCard => CanUsePatchTools && !IsVpkMod;

	public WardrobeTreeViewModel? VpkTree
	{
		get => _vpkTree;
		private set
		{
			if (!SetField(ref _vpkTree, value)) return;
			OnPropertyChanged(nameof(ShowVpkComponentCard));
			OnPropertyChanged(nameof(HasVpkComponentTree));
		}
	}

	public bool HasVpkComponentTree => _vpkTree?.HasRoots == true;

	/// <summary>
	/// 组件开关（<c>components.json</c> 里的"关掉猫耳/披风"）。
	/// <para>
	/// 与树是<b>两种视图、同一份状态</b>：开关写的是"这些 section 关没关"，
	/// 树上显示的也是同一件事。所以这里没有第二份持久化数据，勾一下等于替用户把
	/// 对应的那几节全关（或全开）。
	/// </para>
	/// </summary>
	public IReadOnlyList<VpkSwitchItemViewModel> VpkSwitches
	{
		get => _vpkSwitches;
		private set
		{
			_vpkSwitches = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasVpkSwitches));
			OnPropertyChanged(nameof(VpkSwitchSummary));
		}
	}

	public bool HasVpkSwitches => _vpkSwitches.Count > 0;

	/// <summary>开关区的一句话说明：多少个开关、共关掉多少块几何。</summary>
	public string VpkSwitchSummary
	{
		get
		{
			if (_vpkSwitches.Count == 0)
				return string.Empty;
			var sections = _vpkSwitches.Sum(item => item.Entry.SectionCount);
			return $"识别到 {_vpkSwitches.Count} 个组件开关（共 {sections} 块几何）。"
				+ "关掉之后点「应用」烘进 patch。";
		}
	}

	/// <summary>没匹配上的开关、以及"这个 Mod 是旧版转的"这类提示。</summary>
	public string VpkSwitchStatus
	{
		get => _vpkSwitchStatus;
		private set
		{
			if (!SetField(ref _vpkSwitchStatus, value)) return;
			OnPropertyChanged(nameof(HasVpkSwitchStatus));
		}
	}

	public bool HasVpkSwitchStatus => !string.IsNullOrWhiteSpace(_vpkSwitchStatus);

	/// <summary>「启用 L4N 支持的衣物装饰组件切换」——开关本身存在 Mod 自己的 wardrobe.json 里。</summary>
	public bool EnableVpkComponentTree
	{
		get => _vpkFile.ComponentTreeEnabled;
		set
		{
			if (_vpkFile.ComponentTreeEnabled == value) return;
			_vpkFile = _vpkFile with { ComponentTreeEnabled = value };
			OnPropertyChanged();
			OnPropertyChanged(nameof(ShowVpkComponentCard));
			_ = PersistVpkAsync();
		}
	}

	public bool ShowVpkComponentCard => IsVpkMod && _vpkFile.ComponentTreeEnabled && HasVpkComponentTree;

	public string VpkComponentStatus
	{
		get => _vpkStatus;
		private set
		{
			if (!SetField(ref _vpkStatus, value)) return;
			OnPropertyChanged(nameof(HasVpkComponentStatus));
		}
	}

	public bool HasVpkComponentStatus => !string.IsNullOrWhiteSpace(_vpkStatus);

	public IReadOnlyList<VpkTargetArmorOption> VpkTargetArmors
	{
		get => _vpkTargetArmors;
		private set => SetField(ref _vpkTargetArmors, value);
	}

	public VpkTargetArmorOption? SelectedVpkTargetArmor
	{
		get => _selectedVpkTargetArmor;
		set
		{
			if (!SetField(ref _selectedVpkTargetArmor, value)) return;
			OnPropertyChanged(nameof(CanApplyVpkTargetArmor));
		}
	}

	public bool CanApplyVpkTargetArmor => IsVpkMod && !_vpkBusy && SelectedVpkTargetArmor is not null;

	// 命令对象要缓存：属性每次 new 一个会让 CanExecute 的重新查询失去意义，
	// 界面上的按钮就会一直停在初始的可用状态。
	private RelayCommand? _applyVpkTargetArmorCommand;
	private RelayCommand? _applyVpkComponentsCommand;

	public RelayCommand ApplyVpkTargetArmorCommand => _applyVpkTargetArmorCommand ??= new(
		async _ => await ApplyVpkTargetArmorAsync(),
		_ => CanApplyVpkTargetArmor);

	public RelayCommand ApplyVpkComponentsCommand => _applyVpkComponentsCommand ??= new(
		async _ => await ApplyVpkComponentsAsync(),
		_ => IsVpkMod && !_vpkBusy && HasVpkComponentTree);

	/// <summary>每次详情页刷新时重算：是不是 VPK 版、组件树长什么样。</summary>
	private void RefreshVpk()
	{
		_vpkFile = VpkWardrobeFile.Default;
		_vpkStatus = string.Empty;
		VpkTree = null;
		VpkSwitches = [];
		VpkSwitchStatus = string.Empty;
		IsVpkMod = false;
		OnPropertyChanged(nameof(IsVpkMod));
		OnPropertyChanged(nameof(ShowPatchSettingsCard));
		OnPropertyChanged(nameof(ShowVpkComponentCard));
		OnPropertyChanged(nameof(EnableVpkComponentTree));
		OnPropertyChanged(nameof(HasVpkComponentTree));
		OnPropertyChanged(nameof(VpkComponentStatus));
		OnPropertyChanged(nameof(CanApplyVpkTargetArmor));

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		if (!VpkModWardrobeService.IsVpkMod(directory)) return;

		IsVpkMod = true;
		OnPropertyChanged(nameof(IsVpkMod));
		OnPropertyChanged(nameof(ShowPatchSettingsCard));
		// 带上当前的详情请求代号：用户在异步加载途中切走时，旧结果不能盖掉新页面。
		_ = LoadVpkAsync(directory, node.Metadata.Name, _detailRequestGeneration);
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
				// 开关表与树放在同一个目录（都是这次转换的产物）；没有它就不显示开关区。
				var patchDirectory = VpkModWardrobeService.ResolveEffectivePatchDirectory(directory);
				var components = patchDirectory is null
					? null
					: await VpkComponentSwitchFile.TryReadAsync(patchDirectory).ConfigureAwait(true);
				if (components is null)
				{
					// 换过目标护甲之后，实际生效的是 `Overwrite/` 下那份；开关表还留在 `vpk2patch/`。
					// 读它作兜底，但**必须校验 unit 还在不在**——换甲会把 Unit 换到目标护甲的键上，
					// 掩码里的 unit id 于是全部失效。宁可显示"要重新导入"，也不要给一组点不动的开关。
					var source = Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName);
					components = await VpkComponentSwitchFile.TryReadAsync(source).ConfigureAwait(true);
				}

				ApplyVpkSwitches(components, map);
			}
			else
			{
				VpkComponentStatus = "这个 Mod 没有 sections.json（用旧版 vpk2patch 转的）；重新导入一次即可获得组件树。";
			}

			OnPropertyChanged(nameof(ShowVpkComponentCard));
			await EnsureVpkTargetArmorsAsync(directory).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException)
		{
			VpkComponentStatus = "读取 VPK 组件信息失败：" + exception.Message;
		}
	}

	/// <summary>
	/// 目标下拉的候选：游戏护甲 + 已注册的自定义素体。
	/// <para>
	/// <b>为什么必须带上自定义素体。</b>VPK 转换出来的是"自带 unit id 的自定义素体"，
	/// 用户的另一条出路是把它挂到自己已经注册好的素体上——那套 unit 组只存在于
	/// <c>armor-registry.workspace.json</c> 里，游戏装备表里永远查不到。以前这里只查装备表，
	/// 于是"已注册的素体一个都不出现"。
	/// </para>
	/// <para>
	/// 头盔不单列：游戏护甲会按 setId 自动补齐，让用户选两遍没有意义。自定义素体没有
	/// setId 也没有配套头盔，它本身就是完整的目标。
	/// </para>
	/// </summary>
	private async Task EnsureVpkTargetArmorsAsync(string modDirectory)
	{
		if (_vpkArmorsLoading || _vpkTargetArmors.Count != 0) return;
		_vpkArmorsLoading = true;
		try
		{
			var catalog = new EquipmentUnitCatalogService(_paths);
			var entries = await catalog.GetEntriesAsync().ConfigureAwait(true);
			var gameArmors = entries
				.Where(entry => entry.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase))
				.GroupBy(entry => entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.Select(group => new VpkTargetArmorOption(group.Key, group.First().DisplayName))
				.OrderBy(option => option.DisplayName, StringComparer.CurrentCulture);

			// 注册表坏了不能连带把游戏护甲列表一起废掉：两者独立取，谁失败谁说明。
			IReadOnlyList<VpkTargetArmorOption> customModels;
			try
			{
				var registry = CoreServices.CreateRuntimeArmorRegistryService(_paths);
				var customCatalog = await registry.ReadCustomModelCatalogAsync().ConfigureAwait(true);
				customModels = customCatalog
					.Where(entry => entry.Parts.Count != 0)
					.Select(entry => new VpkTargetArmorOption(
						entry.ArchiveId,
						entry.DisplayName + "（自定义素体）",
						VpkTargetArmorKind.CustomModel))
					.OrderBy(option => option.DisplayName, StringComparer.CurrentCulture)
					.ToArray();
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
			{
				customModels = [];
				VpkComponentStatus = "读取已注册的自定义素体失败：" + exception.Message;
			}

			var options = gameArmors.Concat(customModels).ToArray();
			VpkTargetArmors = options;
			// 默认目标 = **这个 Mod 自己的素体**，不是某件游戏护甲。
			//
			// 为什么不能默认指向 RE-2310：VPK 转出来的 7 个 Unit 本身就是一套完整素体，
			// 它默认就该是"自定义素体模式"。指到游戏护甲上会让用户以为这个 Mod 是"某件护甲的皮肤"，
			// 而换到那件护甲上还会把原本的护甲件全部隐藏——那是另一个决定，必须由用户点。
			var ownArchiveId = VpkModWardrobeService.TryReadOwnCustomModelKey(modDirectory) is { } ownKey
				? RuntimeArmorRegistryService.CustomModelArchiveId(ownKey)
				: null;
			SelectedVpkTargetArmor = ownArchiveId is null
				? null
				: options.FirstOrDefault(option =>
					option.ArchiveId.Equals(ownArchiveId, StringComparison.OrdinalIgnoreCase));
			OnPropertyChanged(nameof(CanApplyVpkTargetArmor));
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
		{
			VpkComponentStatus = "读取护甲列表失败（请先在设置页重建游戏资产索引）：" + exception.Message;
		}
		finally
		{
			_vpkArmorsLoading = false;
		}
	}

	private async Task ApplyVpkTargetArmorAsync()
	{
		if (!TryGetCurrentNode(out var node) || SelectedVpkTargetArmor is null) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		var target = SelectedVpkTargetArmor;
		_vpkBusy = true;
		OnPropertyChanged(nameof(CanApplyVpkTargetArmor));
		VpkComponentStatus = $"正在把 Unit 换到 {target.DisplayName}…";
		try
		{
			var outcome = await VpkRekeyService.RunAsync(
				node.Id.Value.ToString("N"),
				directory,
				[target.ArchiveId],
				stage => VpkComponentStatus = stage).ConfigureAwait(true);
			if (!outcome.Ok)
			{
				VpkComponentStatus = outcome.Error ?? "换甲失败。";
				return;
			}

			// 两种结果的说法必须不同：退回来源不是"换甲成功"，它压根没搬运几何。
			VpkComponentStatus = outcome.RestoredFromSource
				? $"已退回自定义素体（{target.DisplayName}）：来源 patch 已原样复制进 Overwrite，开关继续有效。"
				: $"已换到 {target.DisplayName}：替换 {outcome.ReplacedUnits} 个 Unit、隐藏 {outcome.HiddenMeshes} 个网格。";
			_notifications?.Show(VpkComponentStatus, NotificationLevel.Info, TimeSpan.FromSeconds(10));
			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			// 换甲后 section 表是新的，树必须重画。
			await LoadVpkAsync(directory, node.Metadata.Name, _detailRequestGeneration).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			VpkComponentStatus = "换甲失败：" + exception.Message;
		}
		finally
		{
			_vpkBusy = false;
			OnPropertyChanged(nameof(CanApplyVpkTargetArmor));
		}
	}

	/// <summary>
	/// 点"应用"：先把勾选状态落盘，再烘进 patch。
	/// <b>顺序不能反</b>——烘焙是从状态算出来的，状态没写就先烘，崩在中间会出现
	/// "patch 已改、状态还是旧的"，下一次点应用就会把旧状态烘回去。
	/// </summary>
	private async Task ApplyVpkComponentsAsync()
	{
		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		_vpkBusy = true;
		try
		{
			VpkComponentStatus = "正在保存勾选…";
			await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);
			VpkComponentStatus = "正在烘焙到 patch…";
			var result = await VpkModWardrobeService.ApplyAsync(directory, _vpkFile.State).ConfigureAwait(true);
			VpkComponentStatus = result.ToSummary();
			if (result.Warnings.Count != 0)
				VpkComponentStatus += " 注意：" + string.Join("；", result.Warnings.Take(3));
			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			VpkComponentStatus = "应用失败：" + exception.Message;
		}
		finally
		{
			_vpkBusy = false;
		}
	}

	private async Task PersistVpkAsync()
	{
		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		try
		{
			await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);
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
		=> _vpkFile = _vpkFile with { State = _vpkFile.State.SetEnabled(node, enabled) };

	private bool IsVpkSectionDisabled(string unit, int sectionIndex)
		=> _vpkFile.State.IsDisabled(unit, sectionIndex);

	// ── 组件开关（源侧 $no_draw 包的翻译结果）──

	/// <summary>
	/// 把开关表装进界面。**没有开关表不是错误**：用旧转换器产的 Mod 里没有
	/// <c>components.json</c>，那时只显示树，不该给用户一条"出错了"。
	/// </summary>
	private void ApplyVpkSwitches(VpkComponentSwitchDocument? document, WardrobeSectionMapDocument sections)
	{
		var stale = 0;
		var usable = new List<VpkComponentSwitchEntry>();
		if (document is not null)
		{
			foreach (var entry in document.Switches)
			{
				// 掩码里的 unit 必须仍然在**当前生效**的 section 表里。
				// 换甲会把 Unit 换到目标护甲的键上，旧掩码于是整批失效——那种开关点下去什么都不会发生。
				if (entry.Disabled.Count != 0 && entry.Disabled.All(target => sections.FindUnit(target.Unit) is not null))
					usable.Add(entry);
				else
					stale++;
			}
		}

		VpkSwitches = usable
			.Select(entry => new VpkSwitchItemViewModel(entry, ReadSwitchEnabled, WriteSwitch))
			.ToArray();

		// 没匹配上的必须说清：用户装了"猫耳关"却看不到开关，只会以为功能没做。
		var parts = new List<string>();
		if (stale != 0)
			parts.Add($"{stale} 个开关指向的 Unit 已经不在这份 patch 里（旧版换甲没把开关表一起搬）。把目标护甲切回「自定义素体」、或重新导入一次这个 Mod 即可恢复。");
		if (document is { Unmatched.Count: > 0 })
		{
			parts.Add($"{document.Unmatched.Count} 个开关在这个 Mod 里找不到对应材质："
				+ $"{string.Join("、", document.Unmatched.Take(4).Select(item => item.Name))}"
				+ (document.Unmatched.Count > 4 ? "…" : string.Empty)
				+ "。多半是它属于另一个角色/另一份材质包。");
		}

		VpkSwitchStatus = string.Join(Environment.NewLine, parts);
	}

	/// <summary>开关"开着" = 它要关的那几节**没有全部**被关掉（与树的三态语义一致）。</summary>
	private bool ReadSwitchEnabled(VpkComponentSwitchEntry entry)
	{
		if (entry.Disabled.Count == 0)
			return false;
		return !entry.Disabled.All(target =>
			target.Sections.All(section => _vpkFile.State.IsDisabled(target.Unit, section)));
	}

	private void WriteSwitch(VpkComponentSwitchEntry entry, bool enabled)
	{
		var state = _vpkFile.State;
		foreach (var target in entry.Disabled)
			state = state.SetSectionsEnabled(target.Unit, target.Sections, enabled);
		_vpkFile = _vpkFile with { State = state };
		// 树上显示的是同一份状态：不刷新就会出现"开关关了、树上还勾着"。
		VpkTree?.RefreshChecks();
	}
}
