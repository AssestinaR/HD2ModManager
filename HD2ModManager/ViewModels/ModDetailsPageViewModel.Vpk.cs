using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>下拉里的一个目标护甲。<c>ArchiveIds</c> 是这一套装备要替换的全部归档（护甲 + 头盔）。</summary>
public sealed record VpkTargetArmorOption(string ArchiveId, string DisplayName)
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
	/// <summary>默认目标：RE-2310「荣誉卫队」的护甲归档（头盔会按 setId 自动补齐）。</summary>
	private const string DefaultTargetArmorArchiveId = "d923906f8b815987";

	private VpkWardrobeFile _vpkFile = VpkWardrobeFile.Default;
	private WardrobeTreeViewModel? _vpkTree;
	private string _vpkStatus = string.Empty;
	private bool _vpkBusy;
	private bool _vpkArmorsLoading;
	private IReadOnlyList<VpkTargetArmorOption> _vpkTargetArmors = [];
	private VpkTargetArmorOption? _selectedVpkTargetArmor;

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
		_ = LoadVpkAsync(directory, node.Metadata.Name);
	}

	private async Task LoadVpkAsync(string directory, string modName)
	{
		try
		{
			var file = await VpkModWardrobeService.ReadAsync(directory).ConfigureAwait(true);
			var map = await VpkModWardrobeService.ReadSectionMapAsync(directory).ConfigureAwait(true);
			_vpkFile = file;
			OnPropertyChanged(nameof(EnableVpkComponentTree));
			if (map is not null)
			{
				var label = BuildVpkTreeLabel(modName, directory);
				var root = VpkModWardrobeService.BuildTree(label, map);
				var tree = new WardrobeTreeViewModel(this);
				tree.LoadFromTree(root, editable: true);
				VpkTree = tree;
			}
			else
			{
				VpkComponentStatus = "这个 Mod 没有 sections.json（用旧版 vpk2patch 转的）；重新导入一次即可获得组件树。";
			}

			OnPropertyChanged(nameof(ShowVpkComponentCard));
			await EnsureVpkTargetArmorsAsync().ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException)
		{
			VpkComponentStatus = "读取 VPK 组件信息失败：" + exception.Message;
		}
	}

	/// <summary>树的根标题：<c>VPK &lt;模组名&gt; 替换 &lt;目标护甲&gt;</c>。</summary>
	private static string BuildVpkTreeLabel(string modName, string directory)
	{
		var name = string.IsNullOrWhiteSpace(modName) ? Path.GetFileName(directory) : modName;
		var overwrite = Path.Combine(directory, ModNodePayloadLayout.OverwriteDirectoryName);
		return Directory.Exists(overwrite) ? $"VPK {name}（已换到目标护甲）" : $"VPK {name}（自定义素体）";
	}

	/// <summary>
	/// 目标护甲下拉的候选。只列护甲：头盔会按 setId 自动补齐，让用户选两遍没有意义。
	/// </summary>
	private async Task EnsureVpkTargetArmorsAsync()
	{
		if (_vpkArmorsLoading || _vpkTargetArmors.Count != 0) return;
		_vpkArmorsLoading = true;
		try
		{
			var catalog = new EquipmentUnitCatalogService(_paths);
			var entries = await catalog.GetEntriesAsync().ConfigureAwait(true);
			var options = entries
				.Where(entry => entry.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase))
				.GroupBy(entry => entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.Select(group => new VpkTargetArmorOption(group.Key, group.First().DisplayName))
				.OrderBy(option => option.DisplayName, StringComparer.CurrentCulture)
				.ToArray();
			VpkTargetArmors = options;
			SelectedVpkTargetArmor = options.FirstOrDefault(option =>
				option.ArchiveId.Equals(DefaultTargetArmorArchiveId, StringComparison.OrdinalIgnoreCase))
				?? options.FirstOrDefault();
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
				[target.ArchiveId],
				stage => VpkComponentStatus = stage).ConfigureAwait(true);
			if (!outcome.Ok)
			{
				VpkComponentStatus = outcome.Error ?? "换甲失败。";
				return;
			}

			VpkComponentStatus = $"已换到 {target.DisplayName}：替换 {outcome.ReplacedUnits} 个 Unit、隐藏 {outcome.HiddenMeshes} 个网格。";
			_notifications?.Show(VpkComponentStatus, NotificationLevel.Info, TimeSpan.FromSeconds(10));
			await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync([node.Id], ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			// 换甲后 section 表是新的，树必须重画。
			await LoadVpkAsync(directory, node.Metadata.Name).ConfigureAwait(true);
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
}
