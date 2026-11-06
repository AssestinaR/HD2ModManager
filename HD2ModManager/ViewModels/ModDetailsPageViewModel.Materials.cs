using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>
/// Mod 详情页的材质选项列表（一张 modlist 卡）。
/// <para>
/// <b>这里没有第二份真相。</b>读走 <see cref="MaterialOptionSettings.Read"/>，写走它的
/// <c>Move / SetName / SetNotes / SetImage / SetEnabled</c>；改完必须重建生效层
/// （<see cref="OverwriteMaterializer.Materialize"/>），否则文件改了、游戏里没变——
/// 这是这段逻辑最容易漏的一步，所以统一收口在 <see cref="CommitMaterialOptionsAsync"/> 里。
/// </para>
/// <para>
/// <b>材质选项不是 Mod。</b>它是主 Mod 基底目录里的一个 patch，所以没有删除、没有移出列表：
/// patch 还在目录里，它就在列表里。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel
{
	private readonly ModListProjectionSource<MaterialOptionRowViewModel> _materialOptions = new();
	private readonly Dictionary<string, MaterialOptionRowViewModel> _materialOptionRows =
		new(StringComparer.OrdinalIgnoreCase);
	private string? _materialOptionStatus;
	private bool _materialOperationInFlight;

	public ModListProjectionSource<MaterialOptionRowViewModel> MaterialOptions => _materialOptions;

	public bool HasMaterialOptions => _materialOptions.Count > 0;

	/// <summary>标题右侧的一句话：几项、启用几项。用户不必自己数。</summary>
	public string MaterialOptionSummary
	{
		get
		{
			var total = _materialOptions.Count;
			if (total == 0) return string.Empty;
			var enabled = _materialOptions.Count(option => option.IsEnabled);
			return $"共 {total} 个材质选项 · 已启用 {enabled} 个 · 越靠下越优先";
		}
	}

	public string? MaterialOptionStatus => _materialOptionStatus;

	public bool HasMaterialOptionStatus => !string.IsNullOrWhiteSpace(_materialOptionStatus);

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
		var directory = MaterialOptionDirectory;
		var views = directory is null
			? Array.Empty<MaterialOptionView>()
			: MaterialOptionSettings.Read(directory);
		var optionsDirectory = directory is null ? null : MaterialOptionSettings.GetOptionsDirectory(directory);

		var rows = new List<MaterialOptionRowViewModel>(views.Count);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var view in views)
		{
			seen.Add(view.PatchFileName);
			var imagePath = ResolveMaterialOptionImage(optionsDirectory, view.ImageFileName);
			if (_materialOptionRows.TryGetValue(view.PatchFileName, out var existing))
			{
				existing.Update(view, imagePath);
				rows.Add(existing);
				continue;
			}

			var row = new MaterialOptionRowViewModel(this, view, imagePath);
			_materialOptionRows[view.PatchFileName] = row;
			rows.Add(row);
		}

		// 目录里消失的 patch 从缓存里退场，否则下次又出现时会带着旧名字回来。
		foreach (var stale in _materialOptionRows.Keys.Where(key => !seen.Contains(key)).ToArray())
			_materialOptionRows.Remove(stale);

		_materialOptions.Publish(rows);
		OnPropertyChanged(nameof(HasMaterialOptions));
		OnPropertyChanged(nameof(MaterialOptionSummary));
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
			SetMaterialOptionStatus("找不到 Mod 目录，改动没有保存。", isError: true);
			return;
		}

		row.SetEnabledState(enabled);
		_ = RunMaterialOperationAsync(async () =>
		{
			var enabledNames = _materialOptions
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
		}, successMessage: enabled ? "已启用并重建生效层（重启游戏后生效）。" : "已停用并重建生效层（重启游戏后生效）。");
	}

	/// <summary>上移/下移一格。到头了就不动，也不写盘。</summary>
	internal void MoveMaterialOption(MaterialOptionRowViewModel row, int delta)
	{
		var ordered = _materialOptions.ToList();
		var index = ordered.IndexOf(row);
		var target = index + delta;
		if (index < 0 || target < 0 || target >= ordered.Count) return;

		ordered.RemoveAt(index);
		ordered.Insert(target, row);
		ApplyMaterialOptionOrder(ordered);
	}

	/// <summary>
	/// 拖动排序。面板给的是"插到当前列表的第几位"，而被拖走的行还在那个列表里，
	/// 所以插入点要按"把它拿出去之后"的坐标系修正一次。
	/// </summary>
	internal void ReorderMaterialOptions(IReadOnlyList<string> draggedKeys, int insertionIndex)
	{
		if (draggedKeys.Count == 0) return;

		var current = _materialOptions.ToList();
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
			// 等写完再让输入条收起：否则底栏先关掉、卡片外的状态行后到，用户会以为没保存。
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
		=> _materialOptions.Where(option => option.IsEnabled).Select(option => option.PatchFileName).ToArray();

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
	/// 所有写操作走同一个门：期间锁住那一行的开关，避免连点让两次物化互相覆盖；
	/// 结束后统一刷新列表与状态文案。
	/// </summary>
	private async Task RunMaterialOperationAsync(Func<Task<string?>> operation, string successMessage)
	{
		if (_materialOperationInFlight)
		{
			SetMaterialOptionStatus("上一项改动还在处理，请稍候。", isError: false);
			return;
		}

		_materialOperationInFlight = true;
		try
		{
			var error = await operation().ConfigureAwait(true);
			if (error is null)
			{
				RefreshMaterialOptions();
				SetMaterialOptionStatus(successMessage, isError: false);
				return;
			}

			RefreshMaterialOptions();
			SetMaterialOptionStatus(error, isError: true);
		}
		catch (Exception exception)
		{
			SetMaterialOptionStatus($"改动失败：{exception.Message}", isError: true);
		}
		finally
		{
			_materialOperationInFlight = false;
		}
	}

	private void SetMaterialOptionStatus(string? message, bool isError)
	{
		_materialOptionStatus = message;
		OnPropertyChanged(nameof(MaterialOptionStatus));
		OnPropertyChanged(nameof(HasMaterialOptionStatus));
		OnPropertyChanged(nameof(MaterialOptionSummary));
		if (isError && !string.IsNullOrWhiteSpace(message))
			LogService.Warn($"材质选项：{message}");
	}
}
