using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HD2ModCore.Domain;
using HD2ModManager.Services;
using Microsoft.Win32;

namespace HD2ModManager.ViewModels;

public partial class SettingsPageViewModel
{
	private CancellationTokenSource? _diagnosticExport;
	private string? _diagnosticFolder;
	private string _diagnosticStatus = string.Empty;
	private bool _disposedForDiagnostics;
	private GameDataIndexStatus? _lastDiagnosticIndexStatus;
	internal Func<ProfileDeploymentStatus>? DiagnosticDeploymentStatus { get; init; }
	public AsyncRelayCommand ExportDiagnosticsCommand { get; }
	public RelayCommand CancelDiagnosticExportCommand { get; }
	public RelayCommand OpenDiagnosticFolderCommand { get; }
	public bool IsExportingDiagnostics => _diagnosticExport is not null;
	public bool HasDiagnosticFolder => _diagnosticFolder is not null;
	public string DiagnosticStatus
	{
		get => _diagnosticStatus;
		private set => SetField(ref _diagnosticStatus, value);
	}

	private async Task ExportDiagnosticsAsync()
	{
		if (_diagnosticExport is not null || _disposedForDiagnostics) return;
		var picker = new SaveFileDialog
		{
			Title = "保存诊断包",
			Filter = "诊断包 (*.zip)|*.zip",
			FileName = $"HD2-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-UTC.zip",
			DefaultExt = ".zip",
			AddExtension = true,
			OverwritePrompt = false,
		};
		try
		{
			var result = Application.Current?.MainWindow is { IsVisible: true } owner
				? picker.ShowDialog(owner) : picker.ShowDialog();
			if (result != true || _disposedForDiagnostics) return;
		}
		catch (Exception exception)
		{
			DiagnosticStatus = $"无法打开保存对话框（{exception.GetType().Name}）。";
			return;
		}

		using var cancellation = new CancellationTokenSource();
		_diagnosticExport = cancellation;
		_diagnosticFolder = null;
		NotifyDiagnosticState();
		DiagnosticStatus = "正在收集诊断信息…";
		try
		{
			var path = picker.FileName;
			var profiles = _profiles.Snapshot;
			var library = _library.Snapshot with { Profiles = profiles.Profiles, ActiveProfileId = profiles.ActiveProfileId };
			var capture = new DiagnosticCapture(DateTimeOffset.UtcNow, SettingsService.CaptureDiagnosticSettings(),
				_library.ModsRootDirectory, library, _library.GetOptionActivationSnapshot(),
				DiagnosticDeploymentStatus?.Invoke() ?? ProfileDeploymentStatus.Idle, _lastDiagnosticIndexStatus, AppUpdateService.Instance?.Updater.View(),
				_backgroundTasks?.Snapshot.Where(item => item.IsActive || item.Status == BackgroundTaskStatus.Failed).TakeLast(80)
					.Select(item => (object)new { item.Kind, item.Status, item.CreatedAt, item.StartedAt, item.FinishedAt, item.Progress }).ToArray() ?? []);
			var progress = new Progress<string>(message =>
			{
				if (ReferenceEquals(_diagnosticExport, cancellation) && !_disposedForDiagnostics) DiagnosticStatus = message;
			});
			var gaps = await Task.Run(() => DiagnosticExportService.ExportAsync(path, capture, progress, cancellation.Token), cancellation.Token);
			_diagnosticFolder = Path.GetDirectoryName(path);
			DiagnosticStatus = gaps == 0 ? "诊断包已保存。" : $"诊断包已保存，{gaps} 个来源有采集缺口。详见包内摘要。";
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { DiagnosticStatus = "诊断导出已取消。"; }
		catch (UnauthorizedAccessException) { DiagnosticStatus = "无法写入诊断包。请选择有写入权限的位置。"; }
		catch (IOException) { DiagnosticStatus = "保存诊断包失败。请检查目标文件是否已存在、磁盘空间是否充足。"; }
		catch (Exception exception) { DiagnosticStatus = $"诊断导出失败（{exception.GetType().Name}）。"; }
		finally
		{
			_diagnosticExport = null;
			if (!_disposedForDiagnostics) NotifyDiagnosticState();
		}
	}

	private void OpenDiagnosticFolder()
	{
		if (_diagnosticFolder is not { } folder) return;
		try { OpenFolder(folder); }
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			DiagnosticStatus = "打不开诊断包所在的文件夹。";
		}
	}

	private void NotifyDiagnosticState()
	{
		OnPropertyChanged(nameof(IsExportingDiagnostics));
		OnPropertyChanged(nameof(HasDiagnosticFolder));
		ExportDiagnosticsCommand.RaiseCanExecuteChanged();
		OpenDiagnosticFolderCommand.RaiseCanExecuteChanged();
	}
}
