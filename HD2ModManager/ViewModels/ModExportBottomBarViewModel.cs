using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>
/// "导出 Mod 为文件夹结构 zip"这个操作的**状态与命令**。底栏的两行只是它的投影
/// （见 <see cref="ModExportBottomBarRowViewModel"/>），行本身不持有任何状态。
/// <para>
/// 输出格式刻意不是"可重新导入的包"（那种带 manifest.json，另一个导出器负责）：
/// 主体就是 zip 根，每个选项是一个同名文件夹，选项的选项是子文件夹——把库里的嵌套
/// 结构一对一摊开，人一眼就能看懂。
/// </para>
/// </summary>
public sealed class ModExportBottomBarViewModel : BaseViewModel
{
	private readonly IModFolderTreeExporter _exporter;
	private readonly LibrarySnapshot _snapshot;
	private readonly NotificationService _notifications;
	private readonly Action _requestClose;

	private string _targetPath;
	private bool _includeOverwrite;
	private bool _isBusy;
	private string _statusText = string.Empty;
	private string? _lastExportPath;

	public ModExportBottomBarViewModel(
		IModFolderTreeExporter exporter,
		LibrarySnapshot snapshot,
		ModNodeId rootNodeId,
		string rootName,
		NotificationService notifications,
		Action requestClose)
	{
		_exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
		_snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
		_notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		_requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));

		RootNodeId = rootNodeId;
		RootName = string.IsNullOrWhiteSpace(rootName) ? "export" : rootName;
		_targetPath = BuildDefaultTargetPath(RootName);

		BrowseCommand = new RelayCommand(Browse);
		ExportCommand = new RelayCommand(() => _ = RunExportAsync());
		CancelCommand = new RelayCommand(() => _requestClose());
		OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => _lastExportPath is not null);
	}

	public ModNodeId RootNodeId { get; }

	public string RootName { get; }

	/// <summary>目标 zip 的完整路径。用户可以随便改（改目录、改文件名都行）。</summary>
	public string TargetPath
	{
		get => _targetPath;
		set
		{
			if (SetField(ref _targetPath, value)) RaiseCanExecuteChanged();
		}
	}

	/// <summary>是否导出 Overwrite 版。对 VPK 版节点意味着"只输出 Overwrite 那份 patch"。</summary>
	public bool IncludeOverwrite
	{
		get => _includeOverwrite;
		set => SetField(ref _includeOverwrite, value);
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (!SetField(ref _isBusy, value)) return;
			OnPropertyChanged(nameof(IsIdle));
			RaiseCanExecuteChanged();
		}
	}

	public bool IsIdle => !IsBusy;

	public string StatusText
	{
		get => _statusText;
		private set => SetField(ref _statusText, value);
	}

	public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

	public ICommand BrowseCommand { get; }

	public ICommand ExportCommand { get; }

	public ICommand CancelCommand { get; }

	public ICommand OpenFolderCommand { get; }

	private void Browse()
	{
		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "导出 Mod",
			Filter = "Zip 压缩包 (*.zip)|*.zip",
			DefaultExt = ".zip",
			AddExtension = true,
			OverwritePrompt = true,
			FileName = SafeFileName(RootName) + ".zip",
		};

		var directory = Path.GetDirectoryName(TargetPath);
		if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
			dialog.InitialDirectory = directory;

		if (dialog.ShowDialog() == true) TargetPath = dialog.FileName;
	}

	private async Task RunExportAsync()
	{
		if (IsBusy) return;
		var target = (TargetPath ?? string.Empty).Trim().Trim('"');
		if (string.IsNullOrWhiteSpace(target))
		{
			SetStatus("请先填写导出路径。");
			return;
		}

		if (Directory.Exists(target))
		{
			SetStatus("导出路径是一个目录；请给出 .zip 文件名。");
			return;
		}

		if (!target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) target += ".zip";
		// 回写一次，让输入框显示的就是真正会写出去的路径（含补上的 .zip）。
		TargetPath = target;

		if (File.Exists(target))
		{
			var confirm = System.Windows.MessageBox.Show(
				$"目标文件已存在，是否覆盖？\n\n{target}",
				"导出 Mod",
				System.Windows.MessageBoxButton.OKCancel,
				System.Windows.MessageBoxImage.Question);
			if (confirm != System.Windows.MessageBoxResult.OK) return;
		}

		IsBusy = true;
		SetStatus("正在导出…");
		try
		{
			// 快照与节点在 UI 线程取好；文件 IO 丢线程池，避免大包期间界面僵住。
			var includeOverwrite = IncludeOverwrite;
			var rootId = RootNodeId;
			var snapshot = _snapshot;
			var result = await Task.Run(
				() => _exporter.ExportAsync(rootId, snapshot, target, includeOverwrite, CancellationToken.None).AsTask())
				.ConfigureAwait(true);

			_lastExportPath = result.ZipPath;
			(OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();

			var summary = $"已导出 {result.NodeCount} 个节点、{result.FileCount} 个文件。";
			var warning = result.Warnings.Count == 0 ? string.Empty : $"（{result.Warnings.Count} 条提示：{result.Warnings[0]}）";
			SetStatus(summary + warning);
			_notifications.Show($"导出完成：{Path.GetFileName(result.ZipPath)}", NotificationLevel.Info, TimeSpan.FromSeconds(6));
			LogService.Info($"Mod 导出完成：节点={RootName}，目标={result.ZipPath}，节点数={result.NodeCount}，文件数={result.FileCount}，Overwrite={includeOverwrite}。");
			foreach (var warningText in result.Warnings)
				LogService.Warn($"Mod 导出提示（{RootName}）：{warningText}");
		}
		catch (OperationCanceledException)
		{
			SetStatus("导出已取消。");
		}
		catch (Exception exception)
		{
			SetStatus($"导出失败：{exception.Message}");
			LogService.Error($"Mod 导出失败：节点={RootName}，目标={target}，错误={exception}");
			_notifications.Show($"导出失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(10));
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void OpenFolder()
	{
		if (_lastExportPath is null) return;
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = "explorer.exe",
				Arguments = $"/select,\"{_lastExportPath}\"",
				UseShellExecute = true,
			});
		}
		catch (Exception exception)
		{
			LogService.Warn($"打开导出目录失败：{exception.Message}");
		}
	}

	private void SetStatus(string text)
	{
		StatusText = text;
		OnPropertyChanged(nameof(HasStatus));
	}

	private void RaiseCanExecuteChanged()
	{
		(ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
		(OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
	}

	/// <summary>默认落在管理器自己的 Output 目录（与设置页的"默认导出位置"一致）。</summary>
	private static string BuildDefaultTargetPath(string rootName)
	{
		var directory = SettingsService.GetResolvedDefaultExportPath();
		return Path.Combine(directory, SafeFileName(rootName) + ".zip");
	}

	private static string SafeFileName(string name)
	{
		var value = (name ?? string.Empty).Trim();
		foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
		value = value.Trim().TrimEnd('.');
		return string.IsNullOrWhiteSpace(value) ? "export" : value;
	}
}
