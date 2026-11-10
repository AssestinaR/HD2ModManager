using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HD2ModCore.Infrastructure;
using HD2ModManager.Controls.EmptyState;
using HD2ModManager.Controls.Lists;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>
/// Mod 详情页的材质选项列表（一张 modlist 卡）。
/// <para>
/// <b>这里没有第二份真相。</b>读走 <see cref="MaterialOptionSettings.Read"/>，写走它的
/// <c>Move / SetName / SetNotes / SetImage / SetEnabled</c>；改完必须重建生效层
/// （<see cref="OverwriteMaterializer.Materialize"/>），否则文件改了、游戏里没变——
/// 这是这段逻辑最容易漏的一步，所以统一收口在 <see cref="RunMaterialOperationAsync"/> 里。
/// </para>
/// <para>
/// <b>材质选项不是 Mod。</b>它是主 Mod 基底目录里的一个 patch，所以没有删除、没有移出列表：
/// patch 还在目录里，它就在列表里。
/// </para>
/// <para>
/// <b>两份列表。</b><see cref="_materialOptionAll"/> 是全部行（顺序 = 部署顺序），
/// 所有写入都以它为准；<see cref="MaterialOptions"/> 是交给面板的<b>可见</b>投影。
/// 两者分开是必须的：拿过滤后的列表去拼启用集合，会把隐藏的行当成"未启用"写进设置文件。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel
{
	private readonly ModListProjectionSource<MaterialOptionRowViewModel> _materialOptions = new();
	private readonly List<MaterialOptionRowViewModel> _materialOptionAll = new();
	private readonly Dictionary<string, MaterialOptionRowViewModel> _materialOptionRows =
		new(StringComparer.OrdinalIgnoreCase);
	private string _materialOptionQuery = string.Empty;
	// 材质写操作的串行闸门。以前是一个 bool + "忙就丢弃"，见 RunMaterialOperationAsync 的注释。
	private readonly SemaphoreSlim _materialOperationGate = new(1, 1);
	// 是否已经有一次"按行上的当前状态同步"在排队（或即将读取状态）。
	private bool _materialSyncPending;
	private RelayCommand? _clearMaterialOptionQueryCommand;

	/// <summary>交给面板的可见行。</summary>
	public ModListProjectionSource<MaterialOptionRowViewModel> MaterialOptions => _materialOptions;

	/// <summary>
	/// 卡片显不显示。看的是**文件里有没有材质选项**，不是过滤后剩几个——
	/// 否则搜不到东西时整张卡（连同搜索框和"清除搜索"）会一起消失，用户没有出路。
	/// </summary>
	public bool HasMaterialOptions => _materialOptionAll.Count > 0;

	/// <summary>搜索词。面板的搜索框双向绑到这里，过滤在 <see cref="RefreshMaterialOptions"/> 里做。</summary>
	public string MaterialOptionQuery
	{
		get => _materialOptionQuery;
		set
		{
			if (string.Equals(_materialOptionQuery, value, StringComparison.Ordinal)) return;
			_materialOptionQuery = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasMaterialOptionQuery));
			OnPropertyChanged(nameof(ShowMaterialOptionReorder));
			OnPropertyChanged(nameof(MaterialOptionRowActions));
			// 只重新过滤，不重读磁盘：搜索是纯展示层的动作，敲一个键读一次
			// patches.json 没有必要，也会在慢盘上顶住 UI。
			ApplyMaterialOptionFilter();
		}
	}

	public bool HasMaterialOptionQuery => !string.IsNullOrWhiteSpace(_materialOptionQuery);

	/// <summary>
	/// 过滤时不提供排序。列表顺序就是部署顺序，"把这一项移到第 2 位"在隐藏了若干行之后
	/// 没有确定含义；与其猜，不如等清掉搜索再做。
	/// </summary>
	public bool ShowMaterialOptionReorder => !HasMaterialOptionQuery;

	public ModListRowAction MaterialOptionRowActions => ShowMaterialOptionReorder
		? ModListRowAction.Rename | ModListRowAction.ToggleEnabled | ModListRowAction.OpenFolder
			| ModListRowAction.MoveUp | ModListRowAction.MoveDown
		: ModListRowAction.Rename | ModListRowAction.ToggleEnabled | ModListRowAction.OpenFolder;

	public RelayCommand ClearMaterialOptionQueryCommand =>
		_clearMaterialOptionQueryCommand ??= new RelayCommand(_ => MaterialOptionQuery = string.Empty);

	/// <summary>标题右侧的一句话：几项、启用几项；过滤时改说"显示几个 / 共几个"。</summary>
	public string MaterialOptionSummary
	{
		get
		{
			var total = _materialOptionAll.Count;
			if (total == 0) return string.Empty;
			if (HasMaterialOptionQuery)
				return $"显示 {_materialOptions.Count} / {total} 个材质选项";

			var enabled = _materialOptionAll.Count(option => option.IsEnabled);
			return $"共 {total} 个材质选项 · 已启用 {enabled} 个 · 越靠下越优先";
		}
	}

	/// <summary>搜不到东西时的空态。和库页同一套说法，图标与按钮由控件层决定。</summary>
	public EmptyStatePresentation? MaterialOptionEmptyState
	{
		get
		{
			if (_materialOptionAll.Count == 0 || _materialOptions.Count > 0) return null;
			if (!HasMaterialOptionQuery) return null;
			return new EmptyStatePresentation(
				"没有匹配的材质选项",
				"试试更短的关键词，或清除搜索回到全部材质。",
				"清除搜索",
				ClearMaterialOptionQueryCommand,
				EmptyStateIcon.Search);
		}
	}


	/// <summary>这个 Mod 的目录（材质选项的 patch、配图、结构文件都在里面）。</summary>
	private string? MaterialOptionDirectory
	{
		get
		{
			if (Mod is null) return null;
			var directory = _library.ResolveAbsolutePath(Mod.SourcePath);
			return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? null : directory;
		}
	}

	internal void RefreshMaterialOptions()
	{
		// 有一次"按行上状态同步"还没落地时**不要**读盘重建。
		//
		// 这条路径不只在写操作末尾触发：材质改动成功后要通报"内容变了"
		// （NotifyMaterialContentChangedAsync），库随即发 SnapshotChanged，
		// 详情页的处理器调 Refresh() → 又回到这里。此时磁盘上还没有用户刚点的那几下，
		// 读盘重建会把行上的乐观状态覆盖掉 —— 表现就是"点了又弹回去"。
		// 那次同步结束时会自己再刷一次，所以这里直接让路。
		if (_materialSyncPending) return;

		var directory = MaterialOptionDirectory;
		var views = directory is null
			? Array.Empty<MaterialOptionView>()
			: MaterialOptionSettings.Read(directory);
		var optionsDirectory = directory is null ? null : MaterialOptionSettings.GetOptionsDirectory(directory);

		_materialOptionAll.Clear();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var view in views)
		{
			seen.Add(view.PatchFileName);
			var imagePath = ResolveMaterialOptionImage(optionsDirectory, view.ImageFileName);
			if (_materialOptionRows.TryGetValue(view.PatchFileName, out var existing))
			{
				existing.Update(view, imagePath);
				_materialOptionAll.Add(existing);
				continue;
			}

			var row = new MaterialOptionRowViewModel(this, view, imagePath);
			_materialOptionRows[view.PatchFileName] = row;
			_materialOptionAll.Add(row);
		}

		// 目录里消失的 patch 从缓存里退场，否则下次又出现时会带着旧名字回来。
		foreach (var stale in _materialOptionRows.Keys.Where(key => !seen.Contains(key)).ToArray())
			_materialOptionRows.Remove(stale);

		ApplyMaterialOptionFilter();
	}

	/// <summary>按当前搜索词重新投影，不读盘。</summary>
	private void ApplyMaterialOptionFilter()
	{
		_materialOptions.Publish(_materialOptionAll.Where(MatchesMaterialOptionQuery).ToArray());
		RaiseMaterialOptionCounts();
	}

	private bool MatchesMaterialOptionQuery(MaterialOptionRowViewModel row)
	{
		if (!HasMaterialOptionQuery) return true;
		// 和库页/配置页同一条匹配规则：拆词、全部命中、含别名（材质/贴图/模型…）。
		return ModSearchMatcher.IsMatch(
			row.Name,
			row.Notes ?? row.PatchFileName,
			assetSummary: null,
			_materialOptionQuery);
	}

	private void RaiseMaterialOptionCounts()
	{
		OnPropertyChanged(nameof(HasMaterialOptions));
		OnPropertyChanged(nameof(MaterialOptionSummary));
		OnPropertyChanged(nameof(MaterialOptionEmptyState));
	}

	private static string? ResolveMaterialOptionImage(string? optionsDirectory, string? imageFileName)
	{
		if (string.IsNullOrWhiteSpace(optionsDirectory) || string.IsNullOrWhiteSpace(imageFileName)) return null;
		var path = Path.Combine(optionsDirectory, imageFileName);
		return File.Exists(path) ? path : null;
	}

	/// <summary>
	/// 启用集合变了：先改显示，再写设置文件，最后重建生效层。
	/// 失败时把显示推回原值——否则界面说"已启用"、磁盘上没有，下一次读又会变回去，
	/// 用户会以为状态自己弹回去了。
	/// </summary>
	internal void SetMaterialOptionEnabled(MaterialOptionRowViewModel row, bool enabled)
	{
		var directory = MaterialOptionDirectory;
		if (directory is null)
		{
			_notifications?.Show("找不到 Mod 目录，改动没有保存。", NotificationLevel.Error);
			return;
		}

		row.SetEnabledState(enabled);
		_ = RunMaterialOperationAsync(async () =>
		{
			// 以**全部**行为准：过滤掉的行也必须按它们当前的状态写回去。
			var enabledNames = _materialOptionAll
				.Where(option => option.IsEnabled)
				.Select(option => option.PatchFileName)
				.ToArray();

			var error = MaterialOptionSettings.SetEnabled(directory, enabledNames);
			if (error is not null)
			{
				row.SetEnabledState(!enabled);
				return error;
			}

			return await MaterializeAsync(directory, enabledNames).ConfigureAwait(true);
		}, successMessage: enabled ? "已启用并重建生效层（重启游戏后生效）。" : "已停用并重建生效层（重启游戏后生效）。",
		// 读的是行上的最新状态，所以排队期间的连续点击可以合并成一次同步。
		coalescibleSync: true);
	}

	/// <summary>上移/下移一格。到头了就不动，也不写盘。</summary>
	internal void MoveMaterialOption(MaterialOptionRowViewModel row, int delta)
	{
		var index = _materialOptionAll.IndexOf(row);
		var target = index + delta;
		if (index < 0 || target < 0 || target >= _materialOptionAll.Count) return;

		var ordered = _materialOptionAll.ToList();
		ordered.RemoveAt(index);
		ordered.Insert(target, row);
		ApplyMaterialOptionOrder(ordered);
	}

	/// <summary>
	/// 拖动排序。面板给的是"插到当前可见列表的第几位"，而被拖走的行还在那个列表里，
	/// 所以插入点要按"把它拿出去之后"的坐标系修正一次。
	/// </summary>
	internal void ReorderMaterialOptions(IReadOnlyList<string> draggedKeys, int insertionIndex)
	{
		if (draggedKeys.Count == 0 || !ShowMaterialOptionReorder) return;

		var current = _materialOptionAll.ToList();
		var moving = current.Where(row =>
			draggedKeys.Contains(row.PatchFileName, StringComparer.OrdinalIgnoreCase)).ToList();
		if (moving.Count == 0) return;

		var rest = current.Where(row => !moving.Contains(row)).ToList();
		var bounded = Math.Clamp(insertionIndex, 0, current.Count);
		var adjusted = bounded - current.Take(bounded).Count(moving.Contains);
		rest.InsertRange(Math.Clamp(adjusted, 0, rest.Count), moving);

		ApplyMaterialOptionOrder(rest);
	}

	private void ApplyMaterialOptionOrder(IReadOnlyList<MaterialOptionRowViewModel> ordered)
	{
		var directory = MaterialOptionDirectory;
		if (directory is null) return;

		_ = RunMaterialOperationAsync(
			operation: async () =>
			{
				var error = MaterialOptionSettings.Move(
					directory, ordered.Select(option => option.PatchIndex).ToArray());
				if (error is not null) return error;

				var enabledNames = ordered
					.Where(option => option.IsEnabled)
					.Select(option => option.PatchFileName)
					.ToArray();
				// 顺序变了谁覆盖谁就变了，所以这里也必须重建生效层。
				return await MaterializeAsync(directory, enabledNames).ConfigureAwait(true);
			},
			successMessage: "顺序已更新并重建生效层（重启游戏后生效）。");
	}

	/// <summary>改名：走底栏那个通用输入条，和其它改名操作同一处入口。</summary>
	internal void BeginRenameMaterialOption(MaterialOptionRowViewModel row)
	{
		var directory = MaterialOptionDirectory;
		if (directory is null) return;
		if (System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;

		shell.BeginCustomNameEdit(row.Name, async value =>
		{
			// 等写完再让输入条收起：否则底栏先关掉、状态行后到，用户会以为没保存。
			await RunMaterialOperationAsync(async () =>
			{
				var error = MaterialOptionSettings.SetName(directory, row.PatchIndex, value);
				return error ?? await MaterializeAsync(directory, EnabledMaterialPatchNames()).ConfigureAwait(true);
			}, successMessage: "名称已保存。").ConfigureAwait(true);
		});
	}

	/// <summary>打开这个材质 patch 所在的基底目录——用户要拿它替换/对比时用得上。</summary>
	internal void OpenMaterialOptionFolder()
	{
		var directory = MaterialOptionDirectory;
		if (directory is null) return;
		var target = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory) ?? directory;
		if (!Directory.Exists(target)) return;
		Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
	}

	private string[] EnabledMaterialPatchNames()
		=> _materialOptionAll.Where(option => option.IsEnabled).Select(option => option.PatchFileName).ToArray();

	/// <summary>
	/// 重建生效层。返回 <c>null</c> 表示成功；否则是给用户看的错误文本。
	/// 物化器自己会守住两件事：unit 层取生效层里已有的那份（不让改材质把换甲成果冲掉），
	/// 以及失败时回滚上一次的 Overwrite——所以这里不需要再做备份。
	/// </summary>
	private async Task<string?> MaterializeAsync(string directory, IReadOnlyList<string> enabledNames)
	{
		var result = await Task.Run(() =>
			OverwriteMaterializer.Materialize(directory, new OverwriteSelection(enabledNames))).ConfigureAwait(true);
		return result.Succeeded ? null : result.Error ?? "重建生效层失败。";
	}

	/// <summary>
	/// 所有写操作走同一个门：同一时刻只允许一个物化在跑（连点会让两次物化互相覆盖）。
	/// 成功后按"内容变了"通报出去——要不要重新部署由 Shell 的部署触发检查决定，
	/// 和换甲合并、VPK 重建走同一条路。反馈用通知，和详情页那张选项卡一致：
	/// 那张卡也没有在卡片下方留一行状态文字。
	/// <para>
	/// <b>排队，不丢弃。</b>以前这里是一个 <c>bool inFlight</c>：忙就弹「上一项改动还在处理」并
	/// <b>直接 return</b>。但调用方已经把行上的开关乐观翻转了，丢弃的结果就是文件没写、
	/// 紧接着 <see cref="RefreshMaterialOptions"/> 又把界面推回旧状态——用户看到的是
	/// 「改了一下又被恢复」。批量开关材质时每一行都要撞一次这个坑。
	/// 现在改成进队列：改动一定会落盘，只是按顺序慢慢来。
	/// </para>
	/// <para>
	/// <b>能合并的只有"按当前状态同步"。</b>开关操作读的是<b>行上的最新状态</b>，所以排队期间
	/// 再点几次不必各排一次——那次同步会把它们一起写下去。顺序变更带着一份顺序快照、
	/// 改名带着具体值，都不能合并，只能老实排队。
	/// </para>
	/// </summary>
	private async Task RunMaterialOperationAsync(
		Func<Task<string?>> operation,
		string successMessage,
		bool coalescibleSync = false)
	{
		if (coalescibleSync)
		{
			// 已经有一次同步排在前面（还没读到状态）：它读的就是最新状态，这次点击会被它带上。
			if (_materialSyncPending) return;
			_materialSyncPending = true;
		}

		await _materialOperationGate.WaitAsync().ConfigureAwait(true);
		try
		{
			// 从这一刻起它要读行上的状态了：之后到来的点击必须自己再排一次，
			// 否则会被"读到之前就已经过去了"地漏掉。
			if (coalescibleSync) _materialSyncPending = false;

			var error = await operation().ConfigureAwait(true);
			if (error is not null)
			{
				RefreshMaterialOptions();
				_notifications?.Show(error, NotificationLevel.Error);
				return;
			}

			// 还有更新的同步在排队时**不要**刷新：刷新会用磁盘上的状态覆盖行上的乐观改动，
			// 而那几次点击正是下一次同步要读的东西——刷早了就等于把它们丢掉。
			if (!_materialSyncPending) RefreshMaterialOptions();
			await NotifyMaterialContentChangedAsync().ConfigureAwait(true);
			_notifications?.Show(successMessage);
		}
		catch (Exception exception)
		{
			RefreshMaterialOptions();
			_notifications?.Show($"改动失败：{exception.Message}", NotificationLevel.Error);
		}
		finally
		{
			_materialOperationGate.Release();
		}
	}

	/// <summary>
	/// 告诉库"这个 Mod 的目录内容变了"（生效层刚被重建）。
	/// 不自己调部署：这个应用里"内容变了要不要重新部署"只有 DeploymentTriggerCheck 一个判断点，
	/// 而它挂在 ModContentFactsChanged 上。
	/// </summary>
	private async Task NotifyMaterialContentChangedAsync()
	{
		if (Mod is null) return;
		try
		{
			await _library.NotifyModContentChangedAsync(Mod.Guid).ConfigureAwait(true);
		}
		catch (Exception exception)
		{
			// 通报失败不该把"已经写好的改动"变成一次报错；记一行，用户仍能手动部署。
			LogService.Warn($"材质选项：通报内容变更失败（{exception.Message}），本次不会自动重新部署。");
		}
	}
}
