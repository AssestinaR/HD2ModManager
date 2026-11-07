using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using HD2ModManager.Controls.Actions;
using HD2ModManager.Controls.EmptyState;

namespace HD2ModManager.ViewModels
{
    // Purpose: Provides bindable state and commands for Manager pages.
    public abstract class PageViewModel : BaseViewModel, IPageActionProvider, IDisposable
    {
        private string _title = string.Empty;
        public string Title { get => _title; set => SetField(ref _title, value); }
        public virtual bool RequiresSingleSlot => false;
        public ObservableCollection<PageActionViewModel> PageActions { get; } = new();
        public virtual void Dispose() { }
    }

    // 作用：标记需要横跨整个工作区的页面，避免宽数据表被压缩进双槽之一。
    public abstract class FullWorkspacePageViewModel : PageViewModel
    {
        public override bool RequiresSingleSlot => true;
    }

    public sealed class HomePageViewModel : PageViewModel
    {
        private readonly ProfileService _profiles;
        private readonly ModLibraryService _library;
        private readonly ImportQueueService _queue;
        private readonly ApplyStatusService _applyStatus;
        private readonly BackgroundTaskService _backgroundTasks;
        private readonly IModRepairBatchService _repairBatch;
        private readonly StoragePaths _paths;
        private bool _isRepairingOutdatedMods;
        private int? _lastDetectedOutdatedModCount;
        private int _lastUnreadableUnitVersionCount;
		private string _gameDataHealth = "正在检查";
		private string _gameDataIndexNotice = string.Empty;
        private DeploymentCapability _deploymentCapability = DeploymentCapability.Unavailable("尚未检测。");

        public string ActiveProfile => _profiles.ActiveKey ?? "未启用";
        public int ModCount => _library.All().Count();
        public int ProfileCount => _profiles.All().Count;
        public string ActiveProfileModSummary => BuildActiveProfileModSummary(_profiles.ActiveProfile);
        public string QueueSummary => $"总计 {_queue.Tasks.Count}，完成 {_queue.CountDone}，待处理 {_queue.CountQueued + _queue.CountRunning}";
        public string ApplySummary => _applyStatus.Summary;
		public string GameDataHealth { get => _gameDataHealth; private set => SetField(ref _gameDataHealth, value); }
		public string GameDataIndexNotice { get => _gameDataIndexNotice; private set => SetField(ref _gameDataIndexNotice, value); }
		public bool HasGameDataIndexNotice => !string.IsNullOrWhiteSpace(GameDataIndexNotice);
        public string AssetMetadataHealth => BuildAssetMetadataHealth();
        public int EnabledModCount => _profiles.ActiveProfile?.Entries.Count ?? 0;
        public int OutdatedModCount => _lastDetectedOutdatedModCount ?? 0;
        public string OutdatedModSummary => IsRepairingOutdatedMods
            ? "检测中"
            : _lastDetectedOutdatedModCount is null
                ? "尚未检测"
                : _lastDetectedOutdatedModCount == 0
                    ? _lastUnreadableUnitVersionCount == 0 ? "未发现过时 Mod" : $"未发现；{_lastUnreadableUnitVersionCount} 个未确认"
                    : $"{_lastDetectedOutdatedModCount} 个过时";
        // Detection is refreshed after rebuilding the GameData index, so the command must
        // remain available even when the previous projection found no outdated Mods.
        public bool CanRepairOutdatedMods => !_isRepairingOutdatedMods && ModCount > 0;
        public bool IsRepairingOutdatedMods { get => _isRepairingOutdatedMods; private set { if (SetField(ref _isRepairingOutdatedMods, value)) { OnPropertyChanged(nameof(OutdatedModSummary)); OnPropertyChanged(nameof(CanRepairOutdatedMods)); RepairOutdatedModsCommand.RaiseCanExecuteChanged(); } } }
        public string TaskHealth => _backgroundTasks.CountQueued + _backgroundTasks.CountRunning is var active && active > 0
            ? $"{active} 项任务进行中或排队"
            : "当前没有进行中的任务";
        public DeploymentCapability DeploymentCapability => _deploymentCapability;
        public bool IsDeploymentBlocked => !DeploymentCapability.IsAvailable;
		public string DeploymentMode => DeploymentCapability.IsAvailable
			? DeploymentCapability.Method == DeploymentMethod.HardLink ? "硬链接" : "软链接"
			: DeploymentCapability.SymbolicLinkPermissionDenied ? "软链接（权限不足）" : "不可用";
		public bool ShowDeploymentPermissionActions => !DeploymentCapability.IsAvailable && DeploymentCapability.SymbolicLinkPermissionDenied;
		public bool HasImportantNotices => ShowDeploymentPermissionActions || HasGameDataIndexNotice;
        public string DeploymentCapabilityText => DeploymentCapability.IsAvailable
            ? $"当前部署方式：{(DeploymentCapability.Method == DeploymentMethod.HardLink ? "硬链接" : "符号链接")}。{DeploymentCapability.Summary}"
            : $"当前无法部署 Mod：{DeploymentCapability.Error}";
        public RelayCommand MoveLibraryToRecommendedCommand { get; }
        public RelayCommand OpenDeveloperSettingsCommand { get; }
        public RelayCommand RestartAsAdministratorCommand { get; }
        public RelayCommand OpenTaskHubCommand { get; }
        public RelayCommand RepairOutdatedModsCommand { get; }
        public RelayCommand LaunchGameCommand { get; }

        public HomePageViewModel(ProfileService profiles, ModLibraryService library, ImportQueueService queue, ApplyStatusService applyStatus, BackgroundTaskService backgroundTasks)
        {
            Title = "首页";
            _profiles = profiles;
            _library = library;
            _queue = queue;
            _applyStatus = applyStatus;
            _backgroundTasks = backgroundTasks;
            _paths = SettingsService.CreateStoragePaths();
			_repairBatch = CoreServices.CreateModRepairBatchService(_paths, _library.InformationCenter, _library.InformationReader);
            RefreshDeploymentCapability();
            MoveLibraryToRecommendedCommand = new RelayCommand(MoveLibraryToRecommended);
            OpenDeveloperSettingsCommand = new RelayCommand(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:developers") { UseShellExecute = true }));
            RestartAsAdministratorCommand = new RelayCommand(RestartAsAdministrator);
            OpenTaskHubCommand = new RelayCommand(OpenTaskHub);
            RepairOutdatedModsCommand = new RelayCommand(async _ => await RepairOutdatedModsAsync(), _ => CanRepairOutdatedMods);
            LaunchGameCommand = new RelayCommand(LaunchGame);
			_ = RefreshGameDataHealthAsync();
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(ActiveProfile));
            OnPropertyChanged(nameof(ModCount));
            OnPropertyChanged(nameof(ProfileCount));
            OnPropertyChanged(nameof(ActiveProfileModSummary));
            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(ApplySummary));
			_ = RefreshGameDataHealthAsync();
            OnPropertyChanged(nameof(AssetMetadataHealth));
            OnPropertyChanged(nameof(EnabledModCount));
            OnPropertyChanged(nameof(OutdatedModCount));
            OnPropertyChanged(nameof(OutdatedModSummary));
            OnPropertyChanged(nameof(CanRepairOutdatedMods));
            RepairOutdatedModsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(TaskHealth));
            RefreshDeploymentCapability();
            OnPropertyChanged(nameof(DeploymentCapability));
            OnPropertyChanged(nameof(IsDeploymentBlocked));
			OnPropertyChanged(nameof(DeploymentMode));
			OnPropertyChanged(nameof(ShowDeploymentPermissionActions));
			OnPropertyChanged(nameof(HasImportantNotices));
            OnPropertyChanged(nameof(DeploymentCapabilityText));
        }

        private static string BuildActiveProfileModSummary(Profile? profile)
        {
            if (profile == null) return "无活动配置";
            return $"已启用 {profile.Entries.Count} 个 Mod";
        }


		private async Task RefreshGameDataHealthAsync()
		{
			var folder = SettingsService.GetGameDataFolder();
			if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
			{
				SetGameDataHealth("路径不可用", "游戏目录不可用，请在设置页重新确认游戏目录并构建 GameData 索引。");
				return;
			}

			try
			{
				var index = CoreServices.CreateAssetArchiveIndexService(_paths);
				var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(_paths).GetArchiveHashesJsonAsync();
				var status = await index.GetIndexStatusAsync(folder, archiveHashes);
				if (!status.IsCurrent)
				{
					var health = status.State switch
					{
						GameDataIndexState.Missing => "未索引",
						GameDataIndexState.Stale => "索引过时",
						GameDataIndexState.Invalid => "索引无效",
						_ => "索引不可用"
					};
					var notice = status.State switch
					{
						GameDataIndexState.Stale => "你的游戏已经更新，请前往设置页重建 GameData 索引；在线资料仅用于补充显示名称。",
						GameDataIndexState.Missing => "尚未构建 GameData 索引，请前往设置页构建；在线资料仅用于补充显示名称。",
						_ => "GameData 索引不可用，请前往设置页重建；在线资料仅用于补充显示名称。"
					};
					SetGameDataHealth(health, notice);
					return;
				}

				var checkedAt = SettingsService.GetLastGameDataIndexCheckUtc() ?? status.StoredFingerprint?.BuiltUtc;
				SetGameDataHealth(checkedAt is null
					? "索引有效"
					: checkedAt.Value.ToLocalTime().ToString("yyyy-MM-dd"), string.Empty);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				SetGameDataHealth("索引不可用", "GameData 索引不可用，请前往设置页重建；在线资料仅用于补充显示名称。");
			}
		}

		private void SetGameDataHealth(string health, string notice)
		{
			GameDataHealth = health;
			if (GameDataIndexNotice == notice) return;
			GameDataIndexNotice = notice;
			OnPropertyChanged(nameof(HasGameDataIndexNotice));
			OnPropertyChanged(nameof(HasImportantNotices));
		}

        private static string BuildAssetMetadataHealth()
        {
            var lastCheck = SettingsService.GetLastAssetMetadataCheckUtc();
            return lastCheck is null
                ? "尚未检查在线资产"
                : $"上次检查 {lastCheck.Value.ToLocalTime():yyyy-MM-dd HH:mm}（{(SettingsService.GetAutoUpdateAssetMetadata() ? "自动检查已启用" : "自动检查已关闭")}）";
        }

        private static void OpenTaskHub()
        {
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.OpenMessagePanel();
        }

        private static void LaunchGame()
            => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://run/553850") { UseShellExecute = true });

        private async Task RepairOutdatedModsAsync()
        {
            var gameData = SettingsService.GetGameDataFolder();
            if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
            {
                System.Windows.MessageBox.Show("请先配置有效的 Game Data 文件夹。", "一键修复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            IsRepairingOutdatedMods = true;
            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.RepairMods, "检测并修复过时 Mod", "正在更新 GameData 索引", origin: "首页维护", userVisibleReason: "先使用最新 GameData 索引检测过时 Unit；仅为确认过时的 Mod 生成候选并安全替换原始 Patch。", suggestedAction: "完成后将重新分析并重新部署活动配置。", presentation: BackgroundTaskPresentation.Foreground);
            var operationId = Guid.NewGuid();
            var bridge = new OperationProgressBridge(new BackgroundTaskOperationTarget(task), operationId, SynchronizationContext.Current ?? new SynchronizationContext());
            try
            {
                task.MarkRunning("正在检查 GameData 资产索引");
                var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(_paths)
                    .GetArchiveHashesJsonAsync(task.CancellationToken);
                var assetIndex = CoreServices.CreateAssetArchiveIndexService(_paths);
                // 索引指纹包含游戏 build 号与 GameData 文件元数据：与当前一致就直接沿用已建好的索引，
                // 不再每次整库重扫；游戏更新 / 校验改了文件时才重建。
                var existingStatus = await assetIndex.GetIndexStatusAsync(gameData, archiveHashes, task.CancellationToken);
                if (existingStatus.IsCurrent)
                {
                    task.UpdateStage("索引与当前游戏版本一致，沿用已有索引");
                    LogService.Info("一键修复：资产索引与当前游戏版本一致，跳过重建。");
                }
                else
                {
                    task.MarkRunning("正在重建 GameData 资产索引");
                    var lastProgressUpdate = 0L;
                    var indexProgress = new Progress<IndexBuildProgress>(item =>
                    {
                        var now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref lastProgressUpdate) < 200 && item.Current < item.Total) return;
                        Interlocked.Exchange(ref lastProgressUpdate, now);
                        task.UpdateStage($"正在重建 GameData 资产索引 {item.Current}/{item.Total}");
                        task.UpdateProgress(item.Total <= 0 ? null : (double)item.Current / item.Total);
                    });
                    await Task.Run(() => assetIndex.BuildOrRebuildAsync(gameData, archiveHashes, indexProgress, task.CancellationToken).AsTask(), task.CancellationToken);
                    var indexStatus = await assetIndex.GetIndexStatusAsync(gameData, archiveHashes, task.CancellationToken);
                    if (!indexStatus.IsCurrent)
                        throw new InvalidOperationException("GameData 资产索引重建后仍不可用或已过时。");

                    await Task.Run(() => _library.RefreshAssetSummariesAsync(task.CancellationToken), task.CancellationToken);
                }

                task.UpdateStage("正在读取 Unit 版本并检测过时 Mod");
                var nodes = new List<ModNode>();
                var unreadableCount = 0;
                // 音效 Mod 自带整库 Wwise bank，游戏更新后缺条目会连原版声音一起丢；与 Unit 版本检测互不相干，单独检测。
                var audioService = new HD2ModCore.Audio.AudioBankRepairService(new HD2ModCore.Audio.GameDataOfficialBankSource(gameData));
                var audioNodes = new List<(ModNode Node, string Directory, int SafeCount, int UnsafeCount)>();
                var candidates = _library.Snapshot.Nodes.Values
                    .OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (var index = 0; index < candidates.Length; index++)
                {
                    task.CancellationToken.ThrowIfCancellationRequested();
                    var candidate = candidates[index];
                    task.UpdateStage($"正在检测 Mod {index + 1}/{candidates.Length}");
                    task.UpdateProgress(candidates.Length == 0 ? null : (double)(index + 1) / candidates.Length);
                    var version = await _library.InformationCenter.RequestUnitVersionAsync(
                        candidate,
                        _library.ModsRootDirectory,
                        new ModInformationRequest(ModInformationKind.UnitVersion, "BatchOutdatedDetection", RequireFresh: true),
                        task.CancellationToken);
                    if (version.Data?.Report.IsOutdated == true)
                    {
                        nodes.Add(candidate);
                    }
                    else if (version.Data is null || version.Data.Report.Status == UnitCompatibilityStatus.Unreadable)
                    {
                        unreadableCount++;
                    }

                    try
                    {
                        var nodeDirectory = Path.Combine(_library.ModsRootDirectory, candidate.RelativePath);
                        var audio = await audioService.InspectDirectoryAsync(nodeDirectory, task.CancellationToken);
                        if (audio.IsOutdated)
                            audioNodes.Add((candidate, nodeDirectory, audio.Findings.Count(item => item.Safe), audio.Findings.Count(item => !item.Safe)));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception audioException)
                    {
                        // 音频检测是附加能力：读取失败只记录，不能拖垮原有的 Unit 过时检测与修复。
                        LogService.Warn($"音效过时检测失败，已跳过 {candidate.Metadata.Name}：{audioException.Message}");
                    }
                }
                SetOutdatedDetectionResult(nodes.Count, unreadableCount);
                var repairableAudioNodes = audioNodes.Where(item => item.SafeCount > 0).ToArray();
                var skippedAudioBanks = audioNodes.Sum(item => item.UnsafeCount);
                if (nodes.Count == 0 && repairableAudioNodes.Length == 0)
                {
                    task.MarkCompleted();
                    var summary = unreadableCount == 0
                        ? "检测完成：未发现需要修复的过时 Mod。"
                        : $"检测完成：未发现需要修复的过时 Mod；{unreadableCount} 个 Mod 的 Unit 版本无法确认，已跳过。";
                    if (skippedAudioBanks > 0)
                        summary += $"\n另有 {skippedAudioBanks} 个语音类音库含较多自定义条目，为避免丢失自定义语音已跳过。";
                    System.Windows.MessageBox.Show(summary, "检测并修复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var unreadableNotice = unreadableCount == 0 ? string.Empty : $"\n另有 {unreadableCount} 个 Mod 的 Unit 版本无法确认，本次将跳过。";
                var detected = new List<string>();
                if (nodes.Count > 0) detected.Add($"{nodes.Count} 个模型过时 Mod");
                if (repairableAudioNodes.Length > 0) detected.Add($"{repairableAudioNodes.Length} 个音效过时 Mod");
                var audioNotice = repairableAudioNodes.Length == 0
                    ? string.Empty
                    : "\n音效 Mod 会以当前官方音库为基础重建，保留 Mod 替换过的音频；任一音库校验不通过时退回官方音库（该库 Mod 音色失效，但原版声音保证可用）。"
                      + (skippedAudioBanks > 0 ? $"\n另有 {skippedAudioBanks} 个语音类音库含较多自定义条目，已跳过。" : string.Empty);
                var confirm = System.Windows.MessageBox.Show($"已检测到 {string.Join("、", detected)}。{unreadableNotice}{audioNotice}\n\n将仅处理这些 Mod，并仅在候选完整通过内部检查后替换原 Patch。原 Patch 与 sidecar 会独立备份到管理器目录 backups。是否继续？", "检测并修复过时 Mod", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (confirm != System.Windows.MessageBoxResult.Yes)
                {
                    task.MarkCompleted();
                    return;
                }

                ModRepairBatchResult? result = null;
                if (nodes.Count > 0)
                {
                    task.UpdateStage("正在生成并验证重构候选");
                    task.UpdateProgress(null);
                    result = await _repairBatch.RepairAsync(nodes, _library.ModsRootDirectory, gameData, task.CancellationToken, new InlineProgress<OperationProgressEvent>(bridge.Apply), operationId);
                    task.UpdateStage($"已修复 {result.RepairedModCount}；跳过 {result.SkippedModCount}；失败 {result.FailedModCount}；取消 {result.CanceledModCount}；未开始 {result.NotStartedModCount}");
                }

                var audioRepairedMods = 0;
                var audioFailedMods = 0;
                var audioFallbackNotes = new List<string>();
                var audioBackupRoot = Path.Combine(_paths.AppRootDirectory, "backups", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-audio");
                if (repairableAudioNodes.Length > 0 && !task.CancellationToken.IsCancellationRequested)
                {
                    task.UpdateStage("正在重建过时音效库");
                    for (var index = 0; index < repairableAudioNodes.Length; index++)
                    {
                        task.CancellationToken.ThrowIfCancellationRequested();
                        var item = repairableAudioNodes[index];
                        task.UpdateStage($"正在重建音效库 {index + 1}/{repairableAudioNodes.Length}：{item.Node.Metadata.Name}");
                        task.UpdateProgress((double)(index + 1) / repairableAudioNodes.Length);
                        try
                        {
                            var repaired = await audioService.RepairDirectoryAsync(item.Directory, Path.Combine(audioBackupRoot, item.Node.RelativePath), includeUnsafe: false, task.CancellationToken);
                            if (repaired.Repaired.Count > 0) audioRepairedMods++;
                            audioFallbackNotes.AddRange(repaired.Repaired.SelectMany(patch => patch.Notes));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception audioException)
                        {
                            audioFailedMods++;
                            LogService.Warn($"音效库重建失败 {item.Node.Metadata.Name}：{audioException.Message}");
                        }
                    }
                }

                if (result is not null && (result.CanceledModCount > 0 || result.NotStartedModCount > 0) || task.CancellationToken.IsCancellationRequested) task.MarkCanceled();
                else task.MarkCompleted();
                if (audioRepairedMods > 0 && result?.HasRepairs != true)
                {
                    // 音效补丁同样是原地替换了 Mod 目录中的文件，指纹必须同步，否则下次启动会被当成外部修改。
                    await _library.SynchronizeAsync(task.CancellationToken);
                    if (_profiles.ActiveProfile is not null) _profiles.NotifyActiveModContentChanged();
                }
                if (result is not null && result.HasRepairs)
                {
                    // 修复过程直接替换了 Mod 目录中的 Patch；必须同步并持久化新的 ContentFingerprint，
                    // 否则下次启动会把本次已知修改误判为外部修改并删除所有信息中心缓存。
                    await _library.SynchronizeAsync(task.CancellationToken);
                    task.UpdateStage("正在使用修复后的来源重建已启用装饰");
                    var rebuiltHosts = await _library.RebuildAllActiveDecorationOverwritesAsync(task.CancellationToken);
                    LogService.Info($"一键修复后已重建装饰 Overwrite：主体数={rebuiltHosts}。");
                    if (_profiles.ActiveProfile is not null) _profiles.NotifyActiveModContentChanged();
                    ClearOutdatedDetectionResult();
                }
                var report = new System.Text.StringBuilder("批次完成。");
                if (result is not null)
                    report.Append($"\n已修复：{result.RepairedModCount}\n跳过：{result.SkippedModCount}\n失败：{result.FailedModCount}\n取消：{result.CanceledModCount}\n未开始：{result.NotStartedModCount}");
                if (repairableAudioNodes.Length > 0)
                    report.Append($"\n音效已重建：{audioRepairedMods} 个 Mod" + (audioFailedMods > 0 ? $"；失败：{audioFailedMods}" : string.Empty));
                if (audioFallbackNotes.Count > 0)
                    report.Append("\n\n以下音库已退回官方版本（Mod 音色失效）：\n" + string.Join("\n", audioFallbackNotes.Take(5)) + (audioFallbackNotes.Count > 5 ? $"\n…另有 {audioFallbackNotes.Count - 5} 条" : string.Empty));
                if (audioRepairedMods > 0) report.Append($"\n\n音效备份：{audioBackupRoot}\n需要重新部署后，游戏里才会生效。");
                if (result is not null) report.Append($"\n\n备份与审计清单：{result.BatchDirectory}");
                var failed = (result?.FailedModCount ?? 0) + audioFailedMods;
                System.Windows.MessageBox.Show(report.ToString(), "一键修复", System.Windows.MessageBoxButton.OK, failed == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
                System.Windows.MessageBox.Show($"批量修复失败：{exception.Message}", "一键修复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsRepairingOutdatedMods = false;
                Refresh();
            }
        }

        private void SetOutdatedDetectionResult(int outdatedCount, int unreadableCount)
        {
            _lastDetectedOutdatedModCount = outdatedCount;
            _lastUnreadableUnitVersionCount = unreadableCount;
            OnPropertyChanged(nameof(OutdatedModCount));
            OnPropertyChanged(nameof(OutdatedModSummary));
        }

        private void ClearOutdatedDetectionResult()
        {
            _lastDetectedOutdatedModCount = null;
            _lastUnreadableUnitVersionCount = 0;
            OnPropertyChanged(nameof(OutdatedModCount));
            OnPropertyChanged(nameof(OutdatedModSummary));
        }

        private async void MoveLibraryToRecommended()
        {
            var source = SettingsService.GetModLibraryFolder();
            var target = SettingsService.GetRecommendedModLibraryFolder();
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Directory.CreateDirectory(target);
                if (Directory.Exists(source))
                {
                    foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(source, sourceFile);
                        var targetFile = Path.Combine(target, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                        await Task.Run(() => File.Copy(sourceFile, targetFile, overwrite: true));
                        var sourceHash = await HashFileAsync(sourceFile);
                        var targetHash = await HashFileAsync(targetFile);
                        if (!sourceHash.SequenceEqual(targetHash)) throw new IOException($"迁移校验失败：{relative}");
                    }
                }
                SettingsService.SetModLibraryFolder(target);
                System.Windows.MessageBox.Show("Mod 库已复制并校验到推荐目录，旧库被保留。应用将重新启动以载入新路径。", "移动 Mod 库", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                RestartCurrentProcess(elevated: false);
            }
            catch (Exception exception)
            {
                System.Windows.MessageBox.Show($"迁移失败，当前 Mod 库未切换：{exception.Message}", "移动 Mod 库", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private static void RestartAsAdministrator()
            => RestartCurrentProcess(elevated: true);

        private static void RestartCurrentProcess(bool elevated)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true };
                if (elevated) startInfo.Verb = "runas";
                System.Diagnostics.Process.Start(startInfo);
                System.Windows.Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void RefreshDeploymentCapability()
            => _deploymentCapability = CoreServices.CreateDeploymentCapabilityService().Probe(_library.ModsRootDirectory, SettingsService.GetGameDataFolder());

        private static async Task<byte[]> HashFileAsync(string path)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await SHA256.HashDataAsync(stream);
        }
    }

    public sealed class ProfilePageViewModel : PageViewModel
    {
        private const string SelectionScope = "Profile";
        private readonly ProfileService _profiles;
        private readonly ModLibraryService _library;
        private readonly DerivedStateCoordinator _derivedState;
        private readonly SelectionCoordinator? _selection;
        private readonly BottomBarCoordinator? _bottomBar;
        private readonly ModListStateController<ProfileModListState, ProfileListItemViewModel> _list;
        private readonly ObservableCollection<string> _selectedGuids = new();
        private readonly Dictionary<string, ModUserStatus> _userStatuses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _thumbnailSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _thumbnailRevisions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _directOptionCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _displayedOrderByGuid = new(StringComparer.OrdinalIgnoreCase);
        private ProfileId? _displayedOrderProfileId;
        private string _query = string.Empty;
        private CancellationTokenSource? _searchCancellation;
        private CancellationTokenSource? _thumbnailCancellation;
        private readonly HashSet<string> _pendingProjectionGuids = new(StringComparer.OrdinalIgnoreCase);
        private int _projectionRefreshQueued;
        private bool _projectionRequiresRebuild;
        private bool _projectionRefreshVisibleContent;
        private bool _refreshThumbnails;
        private bool _thumbnailWarmupQueued;
        private bool _hasSubmittedInitialProjection;
        private bool _disposed;

        public ModListProjectionSource<ProfileListItemViewModel> Items => _list.Items;
        public bool HasItems => _list.VisibleCount != 0;
        // 自动分类筛选（与模组库页共用）。选中具体分类时列表是子集，禁用拖动排序。
        private ModCategoryFilter? _categoryFilter;
        public ModCategoryFilter Category => _categoryFilter ??= new ModCategoryFilter(
            guid => _library.GetDerivedData(guid)?.AssetSummary,
            () =>
            {
                OnPropertyChanged(nameof(CanReorder));
                Refresh(ListTransitionKind.Filter);
            });
        public ObservableCollection<string> Profiles { get; } = new();
        public string Query
        {
            get => _query;
            set
            {
                if (!SetField(ref _query, value)) return;
                OnPropertyChanged(nameof(CanReorder));
                QueueSearchRefresh();
            }
        }
        public string CurrentProfileTitle => _profiles.SelectedKey ?? "未选择配置";
        public string ItemCountText => $"显示 {_list.VisibleCount} / {_profiles.SelectedProfile?.Entries.Count ?? 0} 个 Mod";
        public string HeaderSummary => $"{ItemCountText} · {ActiveProfileText} · {SelectedProfileState}";
        // 配置页的空状态：不提供命令（切页由壳层管），只给一句能照做的说明。
        public EmptyStatePresentation? EmptyState => _list.VisibleCount > 0
            ? null
            : new EmptyStatePresentation(
                "这个配置里还没有 Mod",
                "把左侧模组库里的 Mod 拖进来；或在库页选中它们，点底栏的「加入配置」。",
                Icon: EmptyStateIcon.Inbox);
        private bool _showOnlyOutdated;
        public bool ShowOnlyOutdated
        {
            get => _showOnlyOutdated;
            set
            {
                if (!SetField(ref _showOnlyOutdated, value)) return;
                OnPropertyChanged(nameof(OutdatedFilterText));
                OnPropertyChanged(nameof(CanReorder));
                Refresh(ListTransitionKind.Filter);
            }
        }
        public string OutdatedFilterText => ShowOnlyOutdated ? "显示全部" : "显示过时";
        public bool CanReorder => string.IsNullOrWhiteSpace(Query) && !ShowOnlyOutdated && !Category.IsActive;
        private bool _canAcceptLibraryDrop;
        public bool CanAcceptLibraryDrop => _canAcceptLibraryDrop;

        public void SetLibraryDropTargetAvailable(bool available)
            => SetField(ref _canAcceptLibraryDrop, available, nameof(CanAcceptLibraryDrop));

        private string? _selectedProfileKey;
        private string _renameText = string.Empty;
        public string? SelectedProfileKey
        {
            get => _selectedProfileKey;
            set
            {
                if (SetField(ref _selectedProfileKey, value) && !string.IsNullOrWhiteSpace(value))
                {
                    _profiles.Select(value);
                    Refresh();
                }
            }
        }
        public string ActiveProfileText => _profiles.ActiveKey is { } name ? $"活动配置：{name}" : "当前没有活动配置";
        public string SelectedProfileState => _profiles.SelectedProfileId is { } selected && selected == _profiles.ActiveProfileId ? "正在编辑活动配置" : "正在编辑非活动配置";

        public string RenameText
        {
            get => _renameText;
            set => SetField(ref _renameText, value);
        }

        public RelayCommand CreateProfileCommand { get; }
        public RelayCommand RemoveSelectedProfileCommand { get; }
        public RelayCommand RenameProfileCommand { get; }
        public RelayCommand ActivateProfileCommand { get; }
        public RelayCommand DeactivateProfileCommand { get; }
        public RelayCommand RemoveModCommand { get; }
        public RelayCommand DeleteModFromLibraryCommand { get; }
        public RelayCommand MoveUpCommand { get; }
        public RelayCommand MoveDownCommand { get; }
        public RelayCommand ToggleSelectionCommand { get; }

        public ProfilePageViewModel(ProfileService profiles, ModLibraryService library, DerivedStateCoordinator derivedState, SelectionCoordinator? selection = null, BottomBarCoordinator? bottomBar = null)
        {
            Title = "配置页";
            _profiles = profiles;
            _library = library;
            _derivedState = derivedState;
            _selection = selection;
            _bottomBar = bottomBar;
            _list = new ModListStateController<ProfileModListState, ProfileListItemViewModel>(
                state => state.Guid,
                state => state.PresentationVersion,
                state => new ProfileListItemViewModel(state),
                (item, state) => item.ApplyListState(state),
                item => item.SelectionKey);
            _list.Committed += OnListProjectionCommitted;
            if (_selection != null) _selection.SelectionChanged += OnSelectionChanged;
            _profiles.Changed += OnProfileChanged;
            _library.ModContentFactsChanged += OnLibraryContentFactsChanged;
            _library.SnapshotChanged += OnLibrarySnapshotChanged;
            _derivedState.SnapshotChanged += OnDerivedStateSnapshotChanged;
            CreateProfileCommand = new RelayCommand(async _ => await CreateProfileAsync());
            RemoveSelectedProfileCommand = new RelayCommand(async _ => await RemoveSelectedProfileAsync());
            RenameProfileCommand = new RelayCommand(async _ => await RenameProfileAsync());
            ActivateProfileCommand = new RelayCommand(async _ => await ActivateProfileAsync());
            DeactivateProfileCommand = new RelayCommand(async _ => await DeactivateProfileAsync());
            RemoveModCommand = new RelayCommand(async parameter => await RemoveModAsync(parameter));
            DeleteModFromLibraryCommand = new RelayCommand(async parameter => await DeleteModFromLibraryAsync(parameter));
            MoveUpCommand = new RelayCommand(async parameter => await MoveModAsync(parameter, -1));
            MoveDownCommand = new RelayCommand(async parameter => await MoveModAsync(parameter, 1));
            ToggleSelectionCommand = new RelayCommand(ToggleSelection);
            PageActions.Add(new PageActionViewModel(PageActionKind.CreateProfile, "新建配置", CreateProfileCommand, tone: PageActionTone.Primary, order: 10));
            PageActions.Add(new PageActionViewModel(PageActionKind.SwitchProfile, "切换当前配置", new RelayCommand(_ => _bottomBar?.BeginSwitchProfile()), order: 12));
            PageActions.Add(new PageActionViewModel(PageActionKind.ActivateProfile, "设为活动配置", ActivateProfileCommand, tone: PageActionTone.Primary, order: 15));
            PageActions.Add(new PageActionViewModel(PageActionKind.DeactivateProfile, "停用活动配置", DeactivateProfileCommand, order: 16));
            PageActions.Add(new PageActionViewModel(PageActionKind.RenameProfile, "重命名配置", new RelayCommand(_ => _bottomBar?.BeginRenameProfile()), order: 20));
            PageActions.Add(new PageActionViewModel(PageActionKind.RemoveProfile, "删除当前配置", RemoveSelectedProfileCommand, tone: PageActionTone.Danger, order: 30));
            Refresh();
            QueueStatusRefresh();
            QueueThumbnailWarmup();
        }

        public void AddMod(string guid)
        {
            if (_profiles.SelectedProfile == null)
            {
                _profiles.CreateNew();
            }

            _profiles.AddModToSelected(guid);
            Refresh();
        }

        public void ApplySelection(IReadOnlyList<string> selectedKeys)
        {
            ArgumentNullException.ThrowIfNull(selectedKeys);
            if (_selection is not null)
            {
                _selection.Replace(SelectionScope, selectedKeys);
                return;
            }

            ApplyLocalSelection(selectedKeys);
        }

        public async Task ReorderAsync(IReadOnlyList<string> draggedKeys, int insertionIndex)
        {
            if (!CanReorder || draggedKeys.Count == 0 || _profiles.SelectedProfile is not { } profile) return;

            var ordered = _profiles.GetSortedEntries(profile)
                .Select(entry => entry.NodeId.Value.ToString("N"))
                .ToList();
            var dragged = draggedKeys
                .Where(ordered.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dragged.Count == 0) return;

            var originalIndex = Math.Clamp(insertionIndex, 0, ordered.Count);
            var draggedSet = dragged.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removedBefore = ordered.Take(originalIndex).Count(draggedSet.Contains);
            ordered.RemoveAll(draggedSet.Contains);
            ordered.InsertRange(Math.Clamp(originalIndex - removedBefore, 0, ordered.Count), dragged);
            var current = _profiles.GetSortedEntries(profile)
                .Select(entry => entry.NodeId.Value.ToString("N"));
            if (current.SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase)) return;
            await _profiles.ReplaceSelectedEntriesAsync(ordered);
        }

        private async Task CreateProfileAsync()
        {
            if (_bottomBar is not null) _bottomBar.BeginCreateProfile();
            else { await _profiles.CreateNewAsync(); Refresh(); }
        }

        private async Task ActivateProfileAsync()
        {
            await _profiles.ActivateSelectedAsync();
            Refresh();
        }

        private async Task DeactivateProfileAsync()
        {
            await _profiles.DisableActiveAsync();
            Refresh();
        }

        private async Task RemoveSelectedProfileAsync()
        {
            var key = _profiles.SelectedKey;
            if (string.IsNullOrWhiteSpace(key)) return;
            var confirm = System.Windows.MessageBox.Show($"确定删除当前配置“{key}”？\n这只会移除配置，不会删除库中的 Mod 文件。", "删除配置", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            await _profiles.RemoveAsync(key);
            Refresh();
        }

        private async Task RenameProfileAsync()
        {
            var key = _profiles.SelectedKey;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(RenameText)) return;
            var newName = RenameText.Trim();
            if (string.Equals(key, newName, StringComparison.OrdinalIgnoreCase)) return;
            var confirm = System.Windows.MessageBox.Show($"将配置“{key}”重命名为“{newName}”？", "重命名配置", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            await _profiles.RenameAsync(key, RenameText);
            Refresh();
        }

        private async Task RemoveModAsync(object? parameter)
        {
            var guid = parameter as string;
            if (string.IsNullOrWhiteSpace(guid)) return;
            var modName = _library.Get(guid)?.Name ?? guid;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            {
                await shell.RemoveModFromSelectedProfileAsync(guid, modName);
            }
        }

        private async Task DeleteModFromLibraryAsync(object? parameter)
        {
            var guid = parameter as string;
            if (string.IsNullOrWhiteSpace(guid)) return;
            var modName = _library.Get(guid)?.Name ?? guid;
            var targets = ModDeletionConfirmation.ConfirmTargets(
                _library,
                [guid],
                $"确定彻底删除 Mod“{modName}”？\n这会从所有配置移除它，并删除模组库中的已存储文件。",
                "彻底删除 Mod");
            if (targets is null) return;

            try
            {
                var deletion = await _library.RemoveManyAsync(targets);
                if (deletion.RemovedNodeIds.Count == 0)
                {
                    System.Windows.MessageBox.Show(ModLibraryService.DescribeDeletionResult(deletion), "删除 Mod", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                if (deletion.HasFailures)
                    System.Windows.MessageBox.Show(ModLibraryService.DescribeDeletionResult(deletion), "删除 Mod", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                _selection?.Clear();
                Refresh();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"删除 Mod 失败：{ex.Message}", "删除 Mod", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task MoveModAsync(object? parameter, int direction)
        {
            var guid = parameter as string;
            if (string.IsNullOrWhiteSpace(guid)) return;
            await _profiles.MoveModInSelectedAsync(guid, direction);
        }

        public void Refresh(ListTransitionKind transitionKind = ListTransitionKind.Automatic)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "Profile.Refresh",
                $"kind={transitionKind}; current={Items.Count}",
                threshold: 10);
            using var perf = UiPerformanceDiagnostics.Measure(
                "Profile.Refresh",
                thresholdMilliseconds: 25,
                details: () => $"kind={transitionKind}; result={Items.Count}; query={(string.IsNullOrWhiteSpace(Query) ? "empty" : "set")}; outdated={ShowOnlyOutdated}");
            Profiles.Clear();
            foreach (var profileItem in _profiles.All()) Profiles.Add(profileItem.Name);
            _selectedProfileKey = _profiles.SelectedKey;
            _renameText = _selectedProfileKey ?? string.Empty;
            OnPropertyChanged(nameof(SelectedProfileKey));
            OnPropertyChanged(nameof(RenameText));
            OnPropertyChanged(nameof(CurrentProfileTitle));
            OnPropertyChanged(nameof(ActiveProfileText));
            OnPropertyChanged(nameof(SelectedProfileState));
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(HeaderSummary));
            OnPropertyChanged(nameof(EmptyState));

            var profile = _profiles.SelectedProfile;
            if (profile == null)
            {
                _directOptionCounts.Clear();
                _displayedOrderByGuid.Clear();
                _displayedOrderProfileId = null;
                _list.Submit(
                    Array.Empty<ProfileModListState>(),
                    Array.Empty<string>(),
                    commitImmediately: !_hasSubmittedInitialProjection);
                _hasSubmittedInitialProjection = true;
                OnPropertyChanged(nameof(HasItems));
                return;
            }
            RefreshDirectOptionCounts();
            EnsureDisplayedOrders(profile);
            var states = new List<ProfileModListState>();
            // 先过搜索/过时筛选得到候选集，用它统计分类芯片，再按选中的分类做最后一道筛选。
            var candidates = new List<(string Guid, ProfileEntry Entry, HD2ModManager.Models.ModEntity? Mod, DerivedModNodeData? Derived)>();
            foreach (var entry in _profiles.GetSortedEntries(profile))
            {
                var guid = entry.NodeId.Value.ToString("N");
                var mod = _library.Get(guid);
                var derived = _library.GetDerivedData(guid);
                var assetSummary = derived?.AssetSummary;
                if (!ModSearchMatcher.IsMatch(mod?.Name, mod?.Description, assetSummary, Query)) continue;
                if (ShowOnlyOutdated && derived?.UnitCompatibility.IsOutdated != true) continue;
                candidates.Add((guid, entry, mod, derived));
            }
            Category.Rebuild(candidates.Select(candidate => candidate.Guid).ToList());
            OnPropertyChanged(nameof(CanReorder));
            foreach (var (guid, entry, mod, derived) in candidates)
            {
                if (!Category.Matches(guid)) continue;
                _userStatuses.TryGetValue(guid, out var status);
                states.Add(CreateListState(guid, mod, derived, entry.LoadOrder, entry.AddedUtc, status));
            }
            _list.Submit(
                states,
                _profiles.GetSortedEntries(profile).Select(entry => entry.NodeId.Value.ToString("N")),
                commitImmediately: !_hasSubmittedInitialProjection);
            _hasSubmittedInitialProjection = true;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(HeaderSummary));
        }

        public void RefreshFromShell() => QueueStatusRefresh();

        // Reordering a long visible list must not also mutate every row's text
        // before its FLIP move starts. The view calls this at the transition
        // boundary so the logical order remains authoritative while labels catch
        // up immediately after the motion completes.
        public void CommitDisplayedOrders()
        {
            if (_disposed || _profiles.SelectedProfile is not { } profile) return;
            if (_displayedOrderProfileId != profile.Id) return;

            var orderedEntries = _profiles.GetSortedEntries(profile);
            var isCurrent = orderedEntries.All(entry => _displayedOrderByGuid.GetValueOrDefault(entry.NodeId.Value.ToString("N"), -1) == entry.LoadOrder)
                && _displayedOrderByGuid.Count == orderedEntries.Count;
            if (isCurrent) return;

            _displayedOrderByGuid.Clear();
            foreach (var entry in orderedEntries)
                _displayedOrderByGuid[entry.NodeId.Value.ToString("N")] = entry.LoadOrder;

            var states = orderedEntries
                .Select(entry => CreateCurrentListState(entry.NodeId.Value.ToString("N"), entry))
                .Where(state => state is not null)
                .Cast<ProfileModListState>()
                .ToArray();
            _list.SubmitContent(states, commitImmediately: true);
        }

        private async void QueueThumbnailRefresh(IEnumerable<string>? requestedGuids = null)
        {
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            var cancellationSource = new CancellationTokenSource();
            _thumbnailCancellation = cancellationSource;
            var cancellationToken = cancellationSource.Token;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var processed = 0;
            var completedSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var refreshedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var thumbnailPerf = UiPerformanceDiagnostics.Measure(
                "Profile.ThumbnailRefresh",
                thresholdMilliseconds: 100,
                details: () => $"processed={processed}; canceled={cancellationToken.IsCancellationRequested}");
            try
            {
                var requested = requestedGuids?
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var entries = await Task.Run(() => requested ?? (_profiles.SelectedProfile is { } profile
                    ? _profiles.GetSortedEntries(profile).Select(entry => entry.NodeId.Value.ToString("N")).ToArray()
                    : Array.Empty<string>())).ConfigureAwait(false);
                foreach (var guid in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;
                    var result = await Task.Run(() => _library.RequestThumbnailAsync(guid, "Profile", cancellationToken: CancellationToken.None).AsTask())
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (result.Data is { } facts)
                    {
                        await Task.Run(() => ThumbnailService.EnsureThumbnailAsync(facts, 72, CancellationToken.None))
                            .WaitAsync(cancellationToken).ConfigureAwait(false);
                        completedSources[guid] = facts.SourcePath;
                        refreshedIds.Add(guid);
                    }
                }
                if (refreshedIds.Count != 0 && !cancellationToken.IsCancellationRequested && ReferenceEquals(_thumbnailCancellation, cancellationSource))
                {
                    // 中心生产不随页面取消；页面令牌只取消本页的等待和显示。
                    await RunOnUiThreadAsync(() => ApplyThumbnailResults(cancellationSource, completedSources, refreshedIds));
                }
                LogService.Info($"列表缩略图刷新完成：页面=配置，条目={processed}，耗时={clock.ElapsedMilliseconds}ms。");
            }
            catch (OperationCanceledException)
            {
                LogService.Info($"列表缩略图刷新取消：页面=配置，已处理={processed}，耗时={clock.ElapsedMilliseconds}ms。");
            }
            finally
            {
                if (ReferenceEquals(_thumbnailCancellation, cancellationSource))
                {
                    _thumbnailCancellation = null;
                    cancellationSource.Dispose();
                }
            }
        }

        private async void QueueSearchRefresh()
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            var cancellationToken = _searchCancellation.Token;
            try
            {
                await Task.Delay(180, cancellationToken);
                if (!cancellationToken.IsCancellationRequested) Refresh(ListTransitionKind.Filter);
            }
            catch (OperationCanceledException) { }
        }

        private void RefreshUserStatuses()
        {
            var statuses = _derivedState.ProjectStatuses(_profiles.SelectedProfileId);
            _userStatuses.Clear();
            foreach (var pair in statuses) _userStatuses[pair.Key.Value.ToString("N")] = pair.Value;
        }

        private void QueueStatusRefresh()
        {
            if (_disposed) return;
            // Status projection is part of the same list snapshot. A second Reset after
            // Task.Yield used to replace the containers while a list transition was playing.
            RefreshUserStatuses();
            Refresh(ListTransitionKind.Refresh);
        }

        private static Task RunOnUiThreadAsync(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var changedIds = e.GetChangedIds(SelectionScope);
            ApplyLocalSelection(e.GetCurrentIds(SelectionScope), changedIds);
        }

        private void OnProfileChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(requiresRebuild: true, refreshThumbnails: true);

        private void OnLibraryContentFactsChanged(object? sender, ModContentFactsChangedEventArgs e)
            => QueueProjectionRefresh(
                e.NodeIds,
                requiresRebuild: e.Kind is ModContentChangeKind.Added or ModContentChangeKind.Removed);

        private void OnLibrarySnapshotChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(requiresRebuild: true, refreshThumbnails: true);

        private void OnDerivedStateSnapshotChanged(object? sender, DerivedStateSnapshot e)
            => QueueProjectionRefresh(refreshVisibleContent: true);

        private void QueueProjectionRefresh(
            IEnumerable<ModNodeId>? nodeIds = null,
            bool requiresRebuild = false,
            bool refreshVisibleContent = false,
            bool refreshThumbnails = false)
        {
            if (_disposed) return;
            lock (_pendingProjectionGuids)
            {
                if (nodeIds is not null)
                    foreach (var nodeId in nodeIds) _pendingProjectionGuids.Add(nodeId.Value.ToString("N"));
                _projectionRequiresRebuild |= requiresRebuild;
                _projectionRefreshVisibleContent |= refreshVisibleContent;
                _refreshThumbnails |= refreshThumbnails;
            }
            if (Interlocked.Exchange(ref _projectionRefreshQueued, 1) != 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.BeginInvoke(new Action(ApplyPendingProjectionRefresh), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ApplyPendingProjectionRefresh()
        {
            if (_disposed) return;
            HashSet<string> guids;
            bool requiresRebuild;
            bool refreshVisibleContent;
            bool refreshThumbnails;
            lock (_pendingProjectionGuids)
            {
                guids = new HashSet<string>(_pendingProjectionGuids, StringComparer.OrdinalIgnoreCase);
                _pendingProjectionGuids.Clear();
                requiresRebuild = _projectionRequiresRebuild;
                _projectionRequiresRebuild = false;
                refreshVisibleContent = _projectionRefreshVisibleContent;
                _projectionRefreshVisibleContent = false;
                refreshThumbnails = _refreshThumbnails;
                _refreshThumbnails = false;
            }
            Interlocked.Exchange(ref _projectionRefreshQueued, 0);
            UiPerformanceDiagnostics.RecordBurst(
                "Profile.ApplyProjection",
                $"guids={guids.Count}; rebuild={requiresRebuild}; visibleContent={refreshVisibleContent}; refreshThumbnails={refreshThumbnails}",
                threshold: 8);
            using var perf = UiPerformanceDiagnostics.Measure(
                "Profile.ApplyProjection",
                thresholdMilliseconds: 25,
                details: () => $"guids={guids.Count}; rebuild={requiresRebuild}; visibleContent={refreshVisibleContent}; refreshThumbnails={refreshThumbnails}; items={Items.Count}");
            RefreshUserStatuses();
            var contentCanAffectMembership = guids.Count != 0
                && (!string.IsNullOrWhiteSpace(Query) || ShowOnlyOutdated || Category.IsActive);
            var categoriesChanged = !requiresRebuild && Category.HasClassificationChanged(guids.Count != 0 ? guids : null);
            if (requiresRebuild || contentCanAffectMembership || categoriesChanged)
                Refresh();
            else if (guids.Count != 0)
                PublishContent(guids);
            else if (refreshVisibleContent)
                PublishVisibleContent();
            if (guids.Count != 0) QueueThumbnailRefresh(guids);
            else if (refreshThumbnails) QueueThumbnailRefresh();
            else QueueThumbnailWarmup();
        }

        private void RefreshProfileChrome()
        {
            var names = _profiles.All().Select(profile => profile.Name).ToArray();
            if (!Profiles.SequenceEqual(names, StringComparer.Ordinal))
            {
                Profiles.Clear();
                foreach (var name in names) Profiles.Add(name);
            }
            _selectedProfileKey = _profiles.SelectedKey;
            _renameText = _selectedProfileKey ?? string.Empty;
            OnPropertyChanged(nameof(SelectedProfileKey));
            OnPropertyChanged(nameof(RenameText));
            OnPropertyChanged(nameof(CurrentProfileTitle));
            OnPropertyChanged(nameof(ActiveProfileText));
            OnPropertyChanged(nameof(SelectedProfileState));
            OnPropertyChanged(nameof(HeaderSummary));
        }

        private void RefreshVisibleThumbnails(IReadOnlyCollection<string> refreshedIds)
        {
            if (_disposed) return;
            PublishContent(refreshedIds);
        }

        private void ApplyThumbnailResults(
            CancellationTokenSource cancellationSource,
            IReadOnlyDictionary<string, string?> completedSources,
            IReadOnlyCollection<string> refreshedIds)
        {
            if (_disposed || !ReferenceEquals(_thumbnailCancellation, cancellationSource)) return;
            foreach (var pair in completedSources)
                _thumbnailSources[pair.Key] = pair.Value;
            foreach (var guid in refreshedIds)
                _thumbnailRevisions[guid] = _thumbnailRevisions.GetValueOrDefault(guid) + 1;
            RefreshVisibleThumbnails(refreshedIds);
        }

        private void PublishVisibleContent()
        {
            if (_disposed) return;
            var profile = _profiles.SelectedProfile;
            if (profile is null)
            {
                _list.SubmitContent(Array.Empty<ProfileModListState>());
                return;
            }

            var entriesByGuid = _profiles.GetSortedEntries(profile)
                .ToDictionary(entry => entry.NodeId.Value.ToString("N"), StringComparer.OrdinalIgnoreCase);
            var states = Items
                .Select(item => item.Guid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(entriesByGuid.ContainsKey)
                .Select(guid => CreateCurrentListState(guid, entriesByGuid[guid]))
                .Where(state => state is not null)
                .Cast<ProfileModListState>()
                .ToArray();
            _list.SubmitContent(states);
        }

        private void PublishContent(IEnumerable<string> guids, bool commitImmediately = false)
        {
            if (_disposed) return;
            var profile = _profiles.SelectedProfile;
            if (profile is null) return;
            var entriesByGuid = _profiles.GetSortedEntries(profile)
                .ToDictionary(entry => entry.NodeId.Value.ToString("N"), StringComparer.OrdinalIgnoreCase);
            var states = guids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(entriesByGuid.ContainsKey)
                .Select(guid => CreateCurrentListState(guid, entriesByGuid[guid]))
                .Where(state => state is not null)
                .Cast<ProfileModListState>()
                .ToArray();
            _list.SubmitContent(states, commitImmediately);
        }

        private ProfileModListState? CreateCurrentListState(string guid, ProfileEntry entry)
        {
            var mod = _library.Get(guid);
            var derived = _library.GetDerivedData(guid);
            var assetSummary = derived?.AssetSummary;
            if (!ModSearchMatcher.IsMatch(mod?.Name, mod?.Description, assetSummary, Query)) return null;
            if (ShowOnlyOutdated && derived?.UnitCompatibility.IsOutdated != true) return null;
            if (!Category.Matches(guid)) return null;
            _userStatuses.TryGetValue(guid, out var status);
            return CreateListState(guid, mod, derived, entry.LoadOrder, entry.AddedUtc, status);
        }

        private void QueueThumbnailWarmup()
        {
            if (_thumbnailWarmupQueued || _profiles.SelectedProfile?.Entries.Count is not > 0) return;
            _thumbnailWarmupQueued = true;
            QueueThumbnailRefresh();
        }

        private void ToggleSelection(object? parameter)
        {
            var guid = parameter as string;
            if (string.IsNullOrWhiteSpace(guid)) return;
            var next = _selectedGuids.ToList();
            if (!next.Remove(guid)) next.Add(guid);
            if (_selection is null) ApplyLocalSelection(next);
            else _selection.Replace(SelectionScope, next);
        }

        private bool IsSelected(string guid) => _selectedGuids.Any(id => string.Equals(id, guid, StringComparison.OrdinalIgnoreCase));

        private void ApplyLocalSelection(IEnumerable<string> selectedKeys, IReadOnlyList<string>? knownChangedIds = null)
        {
            ArgumentNullException.ThrowIfNull(selectedKeys);
            var previous = _selectedGuids.ToArray();
            var next = selectedKeys
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _selectedGuids.Clear();
            foreach (var id in next) _selectedGuids.Add(id);
            var changedIds = knownChangedIds ?? SelectionSetOperations.SymmetricDifference(previous, next);
            if (changedIds.Count != 0) PublishContent(changedIds, commitImmediately: true);
        }

        internal void SwitchProfile(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return;
            SelectedProfileKey = profileName;
        }

        internal async Task RenameCurrentProfileAsync(string? newName)
        {
            RenameText = newName ?? string.Empty;
            await RenameProfileAsync();
        }

        private ProfileModListState CreateListState(
            string guid,
            HD2ModManager.Models.ModEntity? mod,
            DerivedModNodeData? derived,
            int loadOrder,
            DateTimeOffset addedUtc,
            ModUserStatus? userStatus)
        {
            var name = mod?.Name ?? guid;
            var description = mod?.Description;
            _thumbnailSources.TryGetValue(guid, out var thumbnailSource);
            var imagePath = thumbnailSource ?? mod?.Image;
            var directOptionCount = _directOptionCounts.GetValueOrDefault(guid);
            var assetSummary = ModCardViewModel.AppendDirectOptionSummary(
                ModAssetSummaryFormatter.Format(derived?.AssetSummary),
                directOptionCount);
            var displayOrder = _displayedOrderByGuid.GetValueOrDefault(guid, loadOrder);
            var selected = IsSelected(guid);
            var contentVersion = ProfileModListState.BuildContentVersion(
                guid,
                name,
                description,
                imagePath,
                assetSummary,
                displayOrder,
                directOptionCount,
                addedUtc,
                derived?.UnitCompatibility,
                userStatus,
                _thumbnailRevisions.GetValueOrDefault(guid));
            return new ProfileModListState(
                guid,
                name,
                description,
                imagePath,
                assetSummary,
                loadOrder,
                displayOrder,
                directOptionCount,
                addedUtc,
                selected,
                derived?.UnitCompatibility,
                userStatus,
                _thumbnailRevisions.GetValueOrDefault(guid),
                contentVersion,
                ProfileModListState.BuildPresentationVersion(
                    contentVersion,
                    selected));
        }

        private void RefreshDirectOptionCounts()
        {
            _directOptionCounts.Clear();
            foreach (var pair in _library.GetDirectOptionCounts()) _directOptionCounts[pair.Key] = pair.Value;
        }

        private void EnsureDisplayedOrders(Profile profile)
        {
            if (_displayedOrderProfileId == profile.Id) return;
            _displayedOrderProfileId = profile.Id;
            _displayedOrderByGuid.Clear();
            foreach (var entry in _profiles.GetSortedEntries(profile))
                _displayedOrderByGuid[entry.NodeId.Value.ToString("N")] = entry.LoadOrder;
        }

        private void OnListProjectionCommitted(object? sender, ModListStateCommittedEventArgs e)
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(HeaderSummary));
            OnPropertyChanged(nameof(EmptyState));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _selection?.SelectionChanged -= OnSelectionChanged;
            _profiles.Changed -= OnProfileChanged;
            _library.ModContentFactsChanged -= OnLibraryContentFactsChanged;
            _library.SnapshotChanged -= OnLibrarySnapshotChanged;
            _derivedState.SnapshotChanged -= OnDerivedStateSnapshotChanged;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            _thumbnailCancellation = null;
            _list.Committed -= OnListProjectionCommitted;
            _categoryFilter?.Dispose();
            _list.Dispose();
        }
    }

    internal sealed record ProfileModListState(
        string Guid,
        string Name,
        string? Description,
        string? ImagePath,
        string AssetSummary,
        int LoadOrder,
        int DisplayOrder,
        int DirectOptionCount,
        DateTimeOffset AddedUtc,
        bool IsSelected,
        ModUnitCompatibilityReport? UnitCompatibility,
        ModUserStatus? UserStatus,
        int ThumbnailRevision,
        string ContentVersion,
        string PresentationVersion)
    {
        public static string BuildContentVersion(
            string guid,
            string name,
            string? description,
            string? imagePath,
            string assetSummary,
            int displayOrder,
            int directOptionCount,
            DateTimeOffset addedUtc,
            ModUnitCompatibilityReport? unitCompatibility,
            ModUserStatus? userStatus,
            int thumbnailRevision)
            => string.Join('\u001f',
                guid,
                name,
                description,
                imagePath,
                assetSummary,
                displayOrder,
                directOptionCount,
                addedUtc.UtcTicks,
                unitCompatibility?.Status,
                unitCompatibility?.UnitCount,
                unitCompatibility?.ReadableUnitCount,
                unitCompatibility?.OutdatedUnitCount,
                unitCompatibility?.Summary,
                userStatus?.Kind,
                userStatus?.Title,
                userStatus?.Summary,
                userStatus?.IsInSelectedProfile,
                userStatus?.IsInActiveProfile,
                thumbnailRevision);

        public static string BuildPresentationVersion(string contentVersion, bool isSelected)
            => string.Join('\u001f', contentVersion, isSelected);
    }

    // 作用：提供配置浮动操作簇中按需展开的配置切换输入状态。
    public sealed class ProfileSwitchActionViewModel : BaseViewModel
    {
        private readonly ProfilePageViewModel _page;
        private string? _selectedProfile;

        public ProfileSwitchActionViewModel(ProfilePageViewModel page)
        {
            _page = page;
            ConfirmCommand = new RelayCommand(Confirm);
            SyncFromPage();
        }

        public ObservableCollection<string> Profiles => _page.Profiles;
        public string? SelectedProfile { get => _selectedProfile; set => SetField(ref _selectedProfile, value); }
        public RelayCommand ConfirmCommand { get; }

        public void SyncFromPage()
        {
            SelectedProfile = _page.SelectedProfileKey;
            OnPropertyChanged(nameof(Profiles));
        }

        private void Confirm() => _page.SwitchProfile(SelectedProfile);
    }

    // 作用：提供配置浮动操作簇中按需展开的重命名输入状态。
    public sealed class ProfileRenameActionViewModel : BaseViewModel
    {
        private readonly ProfilePageViewModel _page;
        private string _newName = string.Empty;

        public ProfileRenameActionViewModel(ProfilePageViewModel page)
        {
            _page = page;
            ConfirmCommand = new RelayCommand(Confirm);
            SyncFromPage();
        }

        public string NewName { get => _newName; set => SetField(ref _newName, value); }
        public RelayCommand ConfirmCommand { get; }

        public void SyncFromPage() => NewName = _page.SelectedProfileKey ?? string.Empty;

        private async void Confirm() => await _page.RenameCurrentProfileAsync(NewName);
    }

    public sealed class ProfileListItemViewModel : BaseViewModel, IModListSelectable, IModListInternalReorderable
    {
        public string Guid { get; }
        public string SelectionKey => Guid;
        public string ListKey => SelectionKey;
        public bool IsDecoration => false;
        private string _name;
        private string? _description;
        private string? _thumbnailSourcePath;
        private string? _imagePath;
        private bool _imagePathResolved;
        private int _thumbnailRevision;
        private string _contentVersion = string.Empty;
        private string _assetSummary;
        private ModUnitCompatibilityReport? _unitCompatibility;
        private ModUserStatus? _userStatus;
        private ModListRowPresentation _rowPresentation = ModListRowPresentation.Empty;
        private int _loadOrder;
        private int _displayOrder;
        private DateTimeOffset _addedUtc;
        public string Name => _name;
        public string? Description => _description;
        public string? ImagePath
        {
            get
            {
                // 缩略图路径要 stat 磁盘：只在行真正被虚拟化面板实例化、绑定读取时才解析，
                // 屏幕外的行不付这笔钱。
                if (!_imagePathResolved)
                {
                    _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
                    _imagePathResolved = true;
                }
                return _imagePath;
            }
        }
        public string AssetSummary => _assetSummary;
        public string AssetSummaryText => SecondaryDetailText;
        public ModListRowPresentation RowPresentation => _rowPresentation;
        public int LoadOrder => _loadOrder;
        public bool CanInternalReorder => true;
        public DateTimeOffset AddedUtc => _addedUtc;
        public ModUserStatus? UserStatus => _userStatus;
        public ModUnitCompatibilityReport? UnitCompatibility => _unitCompatibility;
        public bool IsModelOutdated => UnitCompatibility?.IsOutdated == true;
        public string ModelCompatibilitySummary => UnitCompatibility?.Summary ?? "模型版本尚未检测。";
        // Display order is staged by ProfilePageViewModel after a list move has
        // reached its visual transition boundary.
        public string StatusText => UserStatus?.Title ?? "配置成员";
        public string UserStatusTitle => StatusText;
        public bool IsVisible => true;
        public string SecondaryDetailText => string.Join(" · ", new[] { Description, AssetSummary }.Where(value => !string.IsNullOrWhiteSpace(value)));
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

        public ProfileListItemViewModel(string guid, string name, string? description, string? imagePath, string assetSummary, int loadOrder, int displayOrder, DateTimeOffset addedUtc, bool isSelected = false, ModUnitCompatibilityReport? unitCompatibility = null, ModUserStatus? userStatus = null)
        {
            Guid = guid;
            _name = name;
            _description = description;
            _thumbnailSourcePath = imagePath;
            _imagePathResolved = false;
            _assetSummary = assetSummary;
            _loadOrder = loadOrder;
            _displayOrder = displayOrder;
            _addedUtc = addedUtc;
            _isSelected = isSelected;
			_unitCompatibility = unitCompatibility;
            _userStatus = userStatus;
            _rowPresentation = BuildRowPresentation(_description, _assetSummary, _unitCompatibility, _userStatus, _displayOrder);
        }

        internal ProfileListItemViewModel(ProfileModListState state)
            : this(
                state.Guid,
                state.Name,
                state.Description,
                state.ImagePath,
                state.AssetSummary,
                state.LoadOrder,
                state.DisplayOrder,
                state.AddedUtc,
                state.IsSelected,
                state.UnitCompatibility,
                state.UserStatus)
        {
            _thumbnailRevision = state.ThumbnailRevision;
            _contentVersion = state.ContentVersion;
            _thumbnailSourcePath = state.ImagePath;
            _imagePathResolved = false;
        }

        internal void ApplyListState(ProfileModListState state)
        {
            var contentChanged = !string.Equals(_contentVersion, state.ContentVersion, StringComparison.Ordinal);
            IsSelected = state.IsSelected;
            if (!contentChanged) return;

            var imageChanged = !string.Equals(_thumbnailSourcePath, state.ImagePath, StringComparison.OrdinalIgnoreCase)
                || _thumbnailRevision != state.ThumbnailRevision;
            var nameChanged = !string.Equals(_name, state.Name, StringComparison.Ordinal);
            var descriptionChanged = !string.Equals(_description, state.Description, StringComparison.Ordinal);
            var assetSummaryChanged = !string.Equals(_assetSummary, state.AssetSummary, StringComparison.Ordinal);
            var loadOrderChanged = _loadOrder != state.LoadOrder;
            var addedUtcChanged = _addedUtc != state.AddedUtc;
            var unitCompatibilityChanged = !Equals(_unitCompatibility, state.UnitCompatibility);
            var userStatusChanged = !Equals(_userStatus, state.UserStatus);
            _name = state.Name;
            _description = state.Description;
            _thumbnailSourcePath = state.ImagePath;
            _thumbnailRevision = state.ThumbnailRevision;
            _contentVersion = state.ContentVersion;
            if (imageChanged) _imagePathResolved = false;
            _assetSummary = state.AssetSummary;
            _loadOrder = state.LoadOrder;
            _displayOrder = state.DisplayOrder;
            _addedUtc = state.AddedUtc;
            _unitCompatibility = state.UnitCompatibility;
            _userStatus = state.UserStatus;
            _rowPresentation = BuildRowPresentation(_description, _assetSummary, _unitCompatibility, _userStatus, _displayOrder);
            if (nameChanged) OnPropertyChanged(nameof(Name));
            if (descriptionChanged) OnPropertyChanged(nameof(Description));
            if (imageChanged) OnPropertyChanged(nameof(ImagePath));
            if (assetSummaryChanged) OnPropertyChanged(nameof(AssetSummary));
            if (descriptionChanged || assetSummaryChanged)
            {
                OnPropertyChanged(nameof(AssetSummaryText));
                OnPropertyChanged(nameof(SecondaryDetailText));
            }
            if (unitCompatibilityChanged)
            {
                OnPropertyChanged(nameof(UnitCompatibility));
                OnPropertyChanged(nameof(IsModelOutdated));
            }
            if (userStatusChanged)
            {
                OnPropertyChanged(nameof(UserStatus));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(UserStatusTitle));
            }
            OnPropertyChanged(nameof(RowPresentation));
            if (loadOrderChanged) OnPropertyChanged(nameof(LoadOrder));
            if (addedUtcChanged) OnPropertyChanged(nameof(AddedUtc));
        }

        private static ModListRowPresentation BuildRowPresentation(
            string? description,
            string? assetSummary,
            ModUnitCompatibilityReport? unitCompatibility,
            ModUserStatus? userStatus,
            int displayOrder)
        {
            var summary = assetSummary ?? string.Empty;
            var activationLabel = userStatus?.IsInActiveProfile == true ? "已启用" : "当前配置成员";
            var activationTone = userStatus?.IsInActiveProfile == true ? ModListRowTone.Success : ModListRowTone.Accent;
            var warnings = new List<string>();
            var warningTone = ModListRowTone.Warning;
            if (unitCompatibility?.IsOutdated == true)
            {
                warnings.Add("模型过时");
                warningTone = ModListRowTone.Danger;
            }
            if (userStatus?.Kind == ModUserStatusKind.MissingMaterial)
            {
                warnings.Add("缺材质");
                warningTone = ModListRowTone.Danger;
            }
            else if (userStatus?.Kind == ModUserStatusKind.Overridden)
            {
                warnings.Add("被覆盖");
            }

            return new ModListRowPresentation(
                ActivationLabel: activationLabel,
                ActivationTone: activationTone,
                WarningLabel: warnings.Count == 0 ? null : string.Join(" · ", warnings),
                WarningTone: warningTone,
                Summary: summary,
                OrderLabel: $"配置顺序 {displayOrder + 1}",
                OrderTone: ModListRowTone.Accent,
                Note: description);
        }
    }

    /// <summary>设置页「映射模式」下拉框的一项。</summary>
    public sealed record ArmorMappingModeOption(ArmorMappingMode Value, string DisplayName, string Description);

    /// <summary>设置页「物理模式」下拉框的一项。</summary>
    public sealed record PhysicsModeOption(PhysicsMode Value, string DisplayName, string Description);

    public class SettingsPageViewModel : PageViewModel
    {
		private readonly ProfileService _profiles;
		private readonly ModLibraryService _library;
        private readonly BottomBarCoordinator _bottomBar;
        private readonly BackgroundTaskService? _backgroundTasks;
		private readonly ModLibrarySwitchBottomBarViewModel _modLibrarySwitchBar;
        private string _modLibraryFolderCandidate;
        private bool _hasPendingModLibrarySwitch;
        private readonly StoragePaths _paths = SettingsService.CreateStoragePaths();
        private CancellationTokenSource? _assetIndexStatusCancellation;
        private bool _isRefreshingAssetIndexStatus;
        private bool _isBuildingAssetIndex;
        private string _assetIndexState = "尚未检查";
        private string _assetIndexSummary = "打开设置后将检查资产索引状态。";
        private string _assetIndexBuiltUtc = "未知";
        private string _assetIndexCounts = "未知";
        private string _assetIndexHint = "请先配置游戏目录和在线资产信息。";
        public string Language
        {
            get => SettingsService.GetLanguage() ?? "";
            set
            {
                SettingsService.SetLanguage(value);
                OnPropertyChanged(nameof(Language));
            }
        }

        public bool AutoImportToActiveProfile
        {
            get => SettingsService.GetAutoImportToActiveProfile();
            set
            {
                SettingsService.SetAutoImportToActiveProfile(value);
                OnPropertyChanged(nameof(AutoImportToActiveProfile));
            }
        }

        /// <summary>
        /// 总开关「启用管理器内置 lua 类功能」。
        ///
        /// 关掉时管理器**永不占用 boot 入口**，所以两个下拉里的「独占」档都不可选——
        /// 选项本身表达能力边界，而不是让用户选完再发现无效。
        /// </summary>
        public bool BuiltInLuaEnabled
        {
            get => SettingsService.GetBuiltInLuaEnabled();
            set
            {
                SettingsService.SetBuiltInLuaEnabled(value);
                OnPropertyChanged(nameof(BuiltInLuaEnabled));
                // 选项集合变了，下拉要重建；当前值也可能被收敛过，一并刷新。
                OnPropertyChanged(nameof(ArmorMappingModeOptions));
                OnPropertyChanged(nameof(ArmorMappingMode));
                OnPropertyChanged(nameof(ArmorMappingModeDescription));
                OnPropertyChanged(nameof(ArmorMappingModeHotSwitchNote));
                OnPropertyChanged(nameof(PhysicsModeOptions));
                OnPropertyChanged(nameof(PhysicsMode));
                OnPropertyChanged(nameof(PhysicsModeDescription));
                OnPropertyChanged(nameof(BuiltInLuaNote));
            }
        }

        /// <summary>总开关下面那句说明：讲清"关掉之后会发生什么"。</summary>
        public string BuiltInLuaNote => BuiltInLuaEnabled
            ? "管理器会占用游戏启动入口（boot）来投放内置 Lua 功能。这是所有内置功能的唯一入口。"
            : "管理器不占用启动入口；内置 Lua 功能只能以外置补丁包形式存在，需要你手动加进 mod 库。";

        /// <summary>设置页「映射模式」下拉框的选项（按总开关过滤）。</summary>
        public IReadOnlyList<ArmorMappingModeOption> ArmorMappingModeOptions =>
            ArmorMappingModes.AvailableFor(BuiltInLuaEnabled)
                .Select(mode => new ArmorMappingModeOption(mode, mode.DisplayName(), mode.Description()))
                .ToArray();

        /// <summary>
        /// 护甲映射模式。兼容模式走运行时覆盖表、可热切换；真映射模式改写注册表数据，
        /// 改动需要重启游戏才生效。
        /// </summary>
        public ArmorMappingMode ArmorMappingMode
        {
            get => SettingsService.GetArmorMappingMode();
            set
            {
                var changed = value != SettingsService.GetArmorMappingMode();
                if (SettingsService.SetArmorMappingMode(value))
                {
                    _profiles.RequestActiveProfileDeployment();
                    // 同 PhysicsMode：每次切到外置都对齐一次导出包（理由见那里）。
                    if (value == ArmorMappingMode.ExternalLuaEntry)
                    {
                        var refreshed = EnsureLuaPackageUpToDate(LuaAddonCatalog.Wardrobe());
                        if (changed) OpenLuaPackagesFolder();
                        if (refreshed) NoteLuaPackageRefreshed([LuaAddonCatalog.Wardrobe()]);
                    }
                }
                OnPropertyChanged(nameof(ArmorMappingMode));
                OnPropertyChanged(nameof(ArmorMappingModeDescription));
                OnPropertyChanged(nameof(ArmorMappingModeHotSwitchNote));
            }
        }

        /// <summary>当前模式的一句话说明，绑定到设置页的说明文字。</summary>
        public string ArmorMappingModeDescription => ArmorMappingMode.Description();

        /// <summary>热切换提示：只有兼容模式可以运行中改映射。</summary>
        public string ArmorMappingModeHotSwitchNote => ArmorMappingMode.AllowsHotSwitch()
            ? "该模式支持游戏运行中切换映射，不需要重启。"
            : "该模式把外观写进注册表数据，改动映射后必须重启游戏才生效。";

        /// <summary>设置页「物理」下拉框的选项（按总开关过滤）。</summary>
        public IReadOnlyList<PhysicsModeOption> PhysicsModeOptions =>
            PhysicsModes.AvailableFor(BuiltInLuaEnabled)
                .Select(mode => new PhysicsModeOption(mode, mode.DisplayName(), mode.Description()))
                .ToArray();

        /// <summary>
        /// 物理骨骼模式。选「外置 lua 入口兼容」并应用时，会把运行时导出成一个独立补丁包
        /// 并打开所在文件夹，供用户手动加进 mod 库。
        /// </summary>
        public PhysicsMode PhysicsMode
        {
            get => SettingsService.GetPhysicsMode();
            set
            {
                var changed = value != SettingsService.GetPhysicsMode();
                if (SettingsService.SetPhysicsMode(value))
                {
                    _profiles.RequestActiveProfileDeployment();
                    // 每次切到外置都**重新对齐一次导出包**，而不是只在"模式变了"那一次。
                    //
                    // 为什么：切模式那条路径只覆盖"用户此刻正在切"。可绝大多数玩家一旦选了外置就
                    // 一直停在这一档——管理器升级之后，他手里的 hd2physbone.lua 还是旧版本，
                    // 而且没有任何地方会提示他（物理运行时的修复就这样永远到不了这些人）。
                    if (value == PhysicsMode.ExternalLuaEntry)
                    {
                        var refreshed = EnsureLuaPackageUpToDate(LuaAddonCatalog.Physics());
                        if (changed) OpenLuaPackagesFolder();
                        if (refreshed) NoteLuaPackageRefreshed([LuaAddonCatalog.Physics()]);
                    }
                }
                OnPropertyChanged(nameof(PhysicsMode));
                OnPropertyChanged(nameof(PhysicsModeDescription));
            }
        }

        /// <summary>物理模式的一句话说明。</summary>
        public string PhysicsModeDescription => PhysicsMode.Description();

        /// <summary>
        /// 把物理运行时导出成外置包并打开文件夹。
        /// 路径固定落在管理器数据目录下，不跟随"默认导出路径"——那个是给转换产物用的，
        /// 混在一起会让用户分不清"哪个是要拖进 mod 库的"。
        /// </summary>
        private static void RevealPhysBonePackage()
        {
            RevealLuaPackage(LuaAddonCatalog.Physics());
        }

        /// <summary>
        /// 把某个内置 Lua 功能导出成外置包并打开**根目录**。
        ///
        /// 两个功能落在同一个根目录下（各自一个子目录），所以不管用户先选哪个，
        /// 打开的都是同一个地方——他装了几个加载器、需要哪份包，一眼就能看到。
        /// </summary>
        private static void RevealLuaPackage(LuaAddonPackageExport.Feature feature)
        {
            try
            {
                var root = SettingsService.GetLuaPackagesDirectory();
                LuaAddonPackageExport.Export(root, feature);
                OpenFolder(root);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                // 导出失败不应让设置保存失败。
            }
        }

        /// <summary>
        /// 让导出目录里那份包与**当前版本**一致；内容本来就一样就什么都不做。
        /// <para>
        /// 只比内容、不比时间戳：`LuaAddonPackageExport.Export` 每次都整目录重建，
        /// 无脑重导会让时间戳一直变、也会把用户在用的那份删一次再建一次。
        /// </para>
        /// </summary>
        /// <returns>真的写过盘（＝用户手里那份是旧的或缺失）时返回 true。</returns>
        private static bool EnsureLuaPackageUpToDate(LuaAddonPackageExport.Feature feature)
        {
            try
            {
                var root = SettingsService.GetLuaPackagesDirectory();
                var luaPath = Path.Combine(root, feature.Key, "lua版", feature.LuaFileName);
                if (File.Exists(luaPath)
                    && string.Equals(File.ReadAllText(luaPath), feature.Source, StringComparison.Ordinal))
                {
                    return false;
                }

                LuaAddonPackageExport.Export(root, feature);
                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>打开外置包根目录（两个功能共用同一个根）。</summary>
        private static void OpenLuaPackagesFolder()
        {
            try
            {
                var root = SettingsService.GetLuaPackagesDirectory();
                Directory.CreateDirectory(root);
                OpenFolder(root);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                // 打不开目录不影响导出本身。
            }
        }

        /// <summary>记一次"外置包刚刷新过"，并提示用户把它重新装进 mod 库。</summary>
        private void NoteLuaPackageRefreshed(IReadOnlyList<LuaAddonPackageExport.Feature> features)
        {
            if (features.Count == 0) return;
            LuaPackageUpdateHint =
                "外置包已更新到当前版本（" + string.Join("、", features.Select(feature => feature.Title))
                + "）：请把它们重新拖进 mod 库替换旧的那一份，否则游戏里跑的还是旧脚本。";
        }

        private string _luaPackageUpdateHint = string.Empty;

        /// <summary>
        /// 外置包刷新提示（空 = 不显示）。
        /// 只在"确实写过盘"时出现：升级管理器后第一次打开设置页，或切到外置那一下。
        /// </summary>
        public string LuaPackageUpdateHint
        {
            get => _luaPackageUpdateHint;
            private set
            {
                if (!SetField(ref _luaPackageUpdateHint, value)) return;
                OnPropertyChanged(nameof(HasLuaPackageUpdateHint));
            }
        }

        public bool HasLuaPackageUpdateHint => !string.IsNullOrEmpty(_luaPackageUpdateHint);

        /// <summary>
        /// 启动时把"已经是外置模式"的那些包对齐到当前版本。
        /// <para>
        /// 这是升级路径上唯一的自救点：用户选了外置之后通常一辈子不会再动那个下拉，
        /// 只靠"切换时导出"等于升级后永远不更新——物理运行时的修复就这样到不了他手上。
        /// 这里比对内容，只有真的变了才写盘。
        /// </para>
        /// </summary>
        private void RefreshLuaPackagesOnStartup()
        {
            var refreshed = new List<LuaAddonPackageExport.Feature>();
            if (SettingsService.GetPhysicsMode() == PhysicsMode.ExternalLuaEntry)
            {
                var feature = LuaAddonCatalog.Physics();
                if (EnsureLuaPackageUpToDate(feature)) refreshed.Add(feature);
            }

            if (SettingsService.GetArmorMappingMode() == ArmorMappingMode.ExternalLuaEntry)
            {
                var feature = LuaAddonCatalog.Wardrobe();
                if (EnsureLuaPackageUpToDate(feature)) refreshed.Add(feature);
            }

            NoteLuaPackageRefreshed(refreshed);
        }

        /// <summary>
        /// 关闭独占模式时把运行时脚本导出到「默认导出路径\wardrobe-lua」并打开该文件夹，
        /// 让用户可以直接把它丢给第三方加载器。导出失败不影响设置本身已保存。
        /// </summary>
        private static void RevealRuntimeScriptForExternalLoader()
        {
            RevealLuaPackage(LuaAddonCatalog.Wardrobe());
        }
    public bool AutoUpdateAssetMetadata
        {
            get => SettingsService.GetAutoUpdateAssetMetadata();
            set
            {
                SettingsService.SetAutoUpdateAssetMetadata(value);
                OnPropertyChanged(nameof(AutoUpdateAssetMetadata));
            }
        }

        public int AssetMetadataCheckIntervalHours
        {
            get => SettingsService.GetAssetMetadataCheckIntervalHours();
            set
            {
                SettingsService.SetAssetMetadataCheckIntervalHours(value);
                OnPropertyChanged(nameof(AssetMetadataCheckIntervalHours));
                OnPropertyChanged(nameof(AssetMetadataLastCheckText));
            }
        }

        public string AssetMetadataLastCheckText => FormatLastCheck(SettingsService.GetLastAssetMetadataCheckUtc());

        public bool AutoCheckGameDataIndex
        {
            get => SettingsService.GetAutoCheckGameDataIndex();
            set
            {
                SettingsService.SetAutoCheckGameDataIndex(value);
                OnPropertyChanged(nameof(AutoCheckGameDataIndex));
                OnPropertyChanged(nameof(GameDataIndexLastCheckText));
            }
        }

        public int GameDataIndexCheckIntervalHours
        {
            get => SettingsService.GetGameDataIndexCheckIntervalHours();
            set
            {
                SettingsService.SetGameDataIndexCheckIntervalHours(value);
                OnPropertyChanged(nameof(GameDataIndexCheckIntervalHours));
                OnPropertyChanged(nameof(GameDataIndexLastCheckText));
            }
        }

        public string GameDataIndexLastCheckText => FormatLastCheck(SettingsService.GetLastGameDataIndexCheckUtc());

        public string AssetIndexState { get => _assetIndexState; private set => SetField(ref _assetIndexState, value); }
        public string AssetIndexSummary { get => _assetIndexSummary; private set => SetField(ref _assetIndexSummary, value); }
        public string AssetIndexBuiltUtc { get => _assetIndexBuiltUtc; private set => SetField(ref _assetIndexBuiltUtc, value); }
        public string AssetIndexCounts { get => _assetIndexCounts; private set => SetField(ref _assetIndexCounts, value); }
        public string AssetIndexHint { get => _assetIndexHint; private set => SetField(ref _assetIndexHint, value); }
        public bool IsRefreshingAssetIndexStatus { get => _isRefreshingAssetIndexStatus; private set => SetField(ref _isRefreshingAssetIndexStatus, value); }
        public bool IsBuildingAssetIndex { get => _isBuildingAssetIndex; private set => SetField(ref _isBuildingAssetIndex, value); }

        public string AssetMetadataRepository
        {
            get => SettingsService.GetAssetMetadataRepository();
            set
            {
                SettingsService.SetAssetMetadataRepository(value);
                OnPropertyChanged(nameof(AssetMetadataRepository));
            }
        }

        private string _assetMetadataStatus = BuildInitialAssetMetadataStatus();
        public string AssetMetadataStatus
        {
            get => _assetMetadataStatus;
            private set => SetField(ref _assetMetadataStatus, value);
        }

        public string ModLibraryFolder
        {
            get => _modLibraryFolderCandidate;
            set
            {
                if (SetField(ref _modLibraryFolderCandidate, value ?? string.Empty))
                    UpdateModLibrarySwitchPrompt();
                OnPropertyChanged(nameof(ModLibraryFolder));
            }
        }

        public string GameDataFolder
        {
            get => SettingsService.GetGameDataFolder();
            set
            {
                SettingsService.SetGameDataFolder(value);
                OnPropertyChanged(nameof(GameDataFolder));
            }
        }

        public string DefaultExportPath
        {
            get => SettingsService.GetDefaultExportPath();
            set
            {
                SettingsService.SetDefaultExportPath(value);
                OnPropertyChanged(nameof(DefaultExportPath));
            }
        }

        public RelayCommand OpenModFolderCommand { get; }
        public RelayCommand ResetModFolderCommand { get; }
        public RelayCommand OpenGameDataFolderCommand { get; }
        public RelayCommand OpenDefaultExportFolderCommand { get; }
        public RelayCommand ResetDefaultExportPathCommand { get; }
        public RelayCommand DetectGameDataFolderCommand { get; }
        public RelayCommand ViewGameDataIndexCommand { get; }
        public RelayCommand OpenArmorRegistryCommand { get; }
        public RelayCommand RebuildAllUnitPartFactsCommand { get; }
        public RelayCommand BuildMissingUnitPartFactsCommand { get; }
        public RelayCommand UpdateAssetMetadataCommand { get; }
        public RelayCommand RefreshAssetIndexStatusCommand { get; }
        public RelayCommand BuildAssetIndexCommand { get; }
        /// <summary>兜底：调用 Steam 校验游戏文件完整性，完成后重置真映射的原始备份基准。</summary>
        public RelayCommand VerifyWithSteamCommand { get; }
        private bool _isLoadingGameDataIndex;
        public bool IsLoadingGameDataIndex { get => _isLoadingGameDataIndex; private set => SetField(ref _isLoadingGameDataIndex, value); }

        /// <summary>设置页「程序更新」卡片；更新服务在 Shell 启动时创建，这里只是它的界面视图模型。</summary>
        public AppUpdateSectionViewModel AppUpdate { get; }

        public override void Dispose()
        {
            AppUpdate.Dispose();
            base.Dispose();
        }

        public SettingsPageViewModel(ProfileService profiles, ModLibraryService library, BottomBarCoordinator bottomBar, BackgroundTaskService? backgroundTasks = null)
        {
            AppUpdate = new AppUpdateSectionViewModel(AppUpdateService.Instance ?? throw new InvalidOperationException("应用更新服务尚未初始化。"));
            Title = "设置";
			_profiles = profiles;
			_library = library;
            _bottomBar = bottomBar ?? throw new ArgumentNullException(nameof(bottomBar));
            _backgroundTasks = backgroundTasks;
			_modLibraryFolderCandidate = SettingsService.GetModLibraryFolder();
			_modLibrarySwitchBar = new ModLibrarySwitchBottomBarViewModel(RestartAndSwitchModLibrary, CancelModLibrarySwitch);
            OpenModFolderCommand = new RelayCommand(() => OpenFolder(ModLibraryFolder));
            ResetModFolderCommand = new RelayCommand(() => ModLibraryFolder = SettingsService.GetDefaultModLibraryFolder());
            OpenGameDataFolderCommand = new RelayCommand(OpenGameDataFolder);
            OpenDefaultExportFolderCommand = new RelayCommand(OpenDefaultExportFolder);
            ResetDefaultExportPathCommand = new RelayCommand(() => DefaultExportPath = SettingsService.DefaultExportPath);
            DetectGameDataFolderCommand = new RelayCommand(DetectGameDataFolder);
            ViewGameDataIndexCommand = new RelayCommand(_ => ViewGameDataIndex(), _ => !IsLoadingGameDataIndex);
            OpenArmorRegistryCommand = new RelayCommand(OpenArmorRegistry);
            RebuildAllUnitPartFactsCommand = new RelayCommand(_ => _ = BuildUnitPartFactsAsync(rebuildAll: true), _ => !IsBuildingAssetIndex);
            BuildMissingUnitPartFactsCommand = new RelayCommand(_ => _ = BuildUnitPartFactsAsync(rebuildAll: false), _ => !IsBuildingAssetIndex);
            UpdateAssetMetadataCommand = new RelayCommand(UpdateAssetMetadata);
            RefreshAssetIndexStatusCommand = new RelayCommand(_ => _ = RefreshAssetIndexStatusAsync(), _ => !IsRefreshingAssetIndexStatus && !IsBuildingAssetIndex);
            BuildAssetIndexCommand = new RelayCommand(_ => _ = BuildAssetIndexAsync(), _ => !IsBuildingAssetIndex);
            VerifyWithSteamCommand = new RelayCommand(
                _ => _ = VerifyWithSteamAsync(),
                _ => !IsVerifyingWithSteam && !string.IsNullOrWhiteSpace(GameDataFolder));

            // 早就是外置模式的用户永远不会再点那个下拉，唯一的刷新机会就是这里。
            RefreshLuaPackagesOnStartup();
        }

        bool isVerifyingWithSteam;
        public bool IsVerifyingWithSteam
        {
            get => isVerifyingWithSteam;
            private set
            {
                if (isVerifyingWithSteam == value) return;
                isVerifyingWithSteam = value;
                OnPropertyChanged(nameof(IsVerifyingWithSteam));
                VerifyWithSteamCommand?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// 兜底修复：让 Steam 校验完整性，把游戏文件恢复成官方版本；完成后丢弃真映射记录
        /// 的原始备份，让恢复出来的文件成为新基准。
        ///
        /// 这是"无论改乱成什么样都能恢复"的最后一条路：真映射改的是游戏安装目录，只有
        /// Steam 能权威地把它还原。
        /// </summary>
        private async Task VerifyWithSteamAsync()
        {
            if (IsVerifyingWithSteam) return;
            var gameData = GameDataFolder;
            if (string.IsNullOrWhiteSpace(gameData)) return;
            IsVerifyingWithSteam = true;
            // 底栏**立刻**出现：用户此刻就等在电脑前，Steam 一校验完他就能自己点「刷新状态」，
            // 不必等我们先探测到校验结束。120 秒只是"没点按钮时"的兜底。
            ShowSteamRefreshPrompt();
            try
            {
                var completed = await SteamIntegrityService.VerifyAsync(gameData).ConfigureAwait(true);
                // 校验完成是整条链路里**唯一可信的时刻**：此刻游戏文件一定是官方版本。
                // 旧备份与旧状态一律不可信，直接整个换掉，用当前位置的文件重建一份新备份。
                if (completed) await ArmorRegistryDataMapper.ResetBaselineAsync(gameData).ConfigureAwait(true);
                AssetIndexHint = completed
                    ? "Steam 校验已完成，已用官方文件重建原始备份。请重新应用一次护甲映射。"
                    : "已请求 Steam 校验，但未确认完成；请确认后再重新应用映射。";
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                AssetIndexHint = $"调用 Steam 校验失败：{exception.Message}";
                DismissSteamVerifyBar();
            }
            finally
            {
                IsVerifyingWithSteam = false;
            }
        }

        SteamVerifyBottomBarViewModel? _steamVerifyBar;

        const string SteamVerifySourceId = "steam-verify";

        /// <summary>点击按钮的瞬间就显示：Steam 正在校验，用户可随时点「刷新状态」继续。</summary>
        private void ShowSteamRefreshPrompt()
        {
            ShowSteamVerifyBar(new SteamVerifyBottomBarViewModel(
                "Steam 正在校验游戏文件，校验完成后点「刷新状态」继续。",
                "刷新状态",
                () => _ = RefreshAfterSteamVerifyAsync()));
            // 120 秒是"用户没去点按钮"时的兜底，给慢机器留足时间。
            _steamVerifyBar!.StartCountdown(120, () => _ = RefreshAfterSteamVerifyAsync());
        }

        /// <summary>刷新完成：仍然过期才询问是否重建，10 秒后自动重建。</summary>
        private async Task RefreshAfterSteamVerifyAsync()
        {
            if (_steamVerifyBar is null) return;
            _steamVerifyBar.Stop();
            // 这里**不能**清基准：Steam 可能还没把文件恢复完，此时清掉备份就失去了唯一的
            // 原件副本，下次应用会拿"被改过的文件"当基准重新播种。基准只在确认校验结束后
            // （后台探测返回 completed）才清。
            await RefreshAssetIndexStatusAsync().ConfigureAwait(true);
            if (_steamVerifyBar is null) return;
            if (!IsAssetIndexReportedStale)
            {
                DismissSteamVerifyBar();
                AssetIndexHint = "Steam 校验后资产索引状态正常，无需重建。";
                return;
            }
            ShowSteamVerifyBar(new SteamVerifyBottomBarViewModel(
                "校验后资产索引状态过期，是否重建？",
                "重建",
                () => _ = RebuildAfterSteamVerifyAsync(),
                "取消",
                DismissSteamVerifyBar));
            _steamVerifyBar!.StartCountdown(10, () => _ = RebuildAfterSteamVerifyAsync());
        }

        private async Task RebuildAfterSteamVerifyAsync()
        {
            if (_steamVerifyBar is null) return;
            DismissSteamVerifyBar();
            await BuildAssetIndexAsync().ConfigureAwait(true);
        }

        private void ShowSteamVerifyBar(SteamVerifyBottomBarViewModel bar)
        {
            DismissSteamVerifyBar();
            _steamVerifyBar = bar;
            _bottomBar.UpdateSurfaceSource(new BottomBarRegistrationRequest(
                SteamVerifySourceId,
                [new BottomBarRowDefinition("main", bar)]));
        }

        private void DismissSteamVerifyBar()
        {
            _steamVerifyBar?.Stop();
            _steamVerifyBar = null;
            _bottomBar.RemoveSurfaceSource(SteamVerifySourceId);
        }

        /// <summary>索引状态文字里是否包含"过期/过时"。状态文案由 <c>GameDataIndexState</c> 决定。</summary>
        private bool IsAssetIndexReportedStale
            => AssetIndexState is { } state
                && (state.Contains("过期", StringComparison.Ordinal)
                    || state.Contains("过时", StringComparison.Ordinal));

        public void Refresh()
        {
            OnPropertyChanged(nameof(Language));
            OnPropertyChanged(nameof(AutoImportToActiveProfile));
            OnPropertyChanged(nameof(AutoUpdateAssetMetadata));
            OnPropertyChanged(nameof(AssetMetadataCheckIntervalHours));
            OnPropertyChanged(nameof(AssetMetadataLastCheckText));
            OnPropertyChanged(nameof(AutoCheckGameDataIndex));
            OnPropertyChanged(nameof(GameDataIndexCheckIntervalHours));
            OnPropertyChanged(nameof(GameDataIndexLastCheckText));
            OnPropertyChanged(nameof(AssetMetadataRepository));
            AssetMetadataStatus = BuildInitialAssetMetadataStatus();
            OnPropertyChanged(nameof(ModLibraryFolder));
            OnPropertyChanged(nameof(GameDataFolder));
            OnPropertyChanged(nameof(DefaultExportPath));
            _ = RefreshAssetIndexStatusAsync();
        }

		private void UpdateModLibrarySwitchPrompt()
		{
			_hasPendingModLibrarySwitch = !PathsEqual(_modLibraryFolderCandidate, SettingsService.GetModLibraryFolder());
			if (_hasPendingModLibrarySwitch)
			{
				_bottomBar.UpdateSurfaceSource(new BottomBarRegistrationRequest(
					"mod-library-switch",
					[new BottomBarRowDefinition("main", _modLibrarySwitchBar)]));
			}
			else
			{
				_bottomBar.RemoveSurfaceSource("mod-library-switch");
			}
		}

		private void CancelModLibrarySwitch()
		{
			_modLibraryFolderCandidate = SettingsService.GetModLibraryFolder();
			_hasPendingModLibrarySwitch = false;
			OnPropertyChanged(nameof(ModLibraryFolder));
			_bottomBar.RemoveSurfaceSource("mod-library-switch");
		}

		private void RestartAndSwitchModLibrary()
		{
			if (!_hasPendingModLibrarySwitch) return;
			if (_backgroundTasks?.Tasks.Any(task => task.IsActive) == true)
			{
				System.Windows.MessageBox.Show("请等待后台任务完成后再切换 Mod 库。", "切换 Mod 库", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
				return;
			}
			try
			{
				var directory = ValidateModLibraryDirectory(_modLibraryFolderCandidate);
				if (!SettingsService.SetModLibraryFolder(directory)) throw new IOException("无法保存 Mod 库目录设置。");
				var executable = Environment.ProcessPath;
				if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法确定当前应用程序路径。");
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true });
				System.Windows.Application.Current.Shutdown();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				System.Windows.MessageBox.Show($"无法切换 Mod 库：{exception.Message}", "切换 Mod 库", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
			}
		}

		private static string ValidateModLibraryDirectory(string candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate)) throw new ArgumentException("Mod 库目录不能为空。", nameof(candidate));
			var directory = Path.GetFullPath(candidate.Trim());
			Directory.CreateDirectory(directory);
			var probe = Path.Combine(directory, $".hd2-write-probe-{Guid.NewGuid():N}.tmp");
			try
			{
				File.WriteAllText(probe, "probe");
			}
			finally
			{
				if (File.Exists(probe)) File.Delete(probe);
			}
			return directory;
		}

		private static bool PathsEqual(string? left, string? right)
		{
			try { return string.Equals(Path.GetFullPath(left ?? string.Empty), Path.GetFullPath(right ?? string.Empty), StringComparison.OrdinalIgnoreCase); }
			catch (Exception) when (left is not null || right is not null) { return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase); }
		}

        private async Task RefreshAssetIndexStatusAsync()
        {
            if (IsRefreshingAssetIndexStatus) return;
            _assetIndexStatusCancellation?.Cancel();
            _assetIndexStatusCancellation?.Dispose();
            _assetIndexStatusCancellation = new CancellationTokenSource();
            var cancellationToken = _assetIndexStatusCancellation.Token;
            IsRefreshingAssetIndexStatus = true;
            RefreshAssetIndexStatusCommand.RaiseCanExecuteChanged();
            AssetIndexState = "检查中";
            AssetIndexSummary = "正在读取资产索引状态。";
            try
            {
                var gameData = SettingsService.GetGameDataFolder();
                if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
                {
                    AssetIndexState = "未设置";
                    AssetIndexSummary = "请先配置有效的 Game Data 目录。";
                    AssetIndexBuiltUtc = "无";
                    AssetIndexCounts = "无";
                    AssetIndexHint = "配置后可在此检查或明确启动索引重建。";
                    return;
                }

                var index = CoreServices.CreateAssetArchiveIndexService(_paths);
                var fingerprint = await index.GetFingerprintAsync(cancellationToken);
                if (fingerprint is null)
                {
                    AssetIndexState = "缺失";
                    AssetIndexSummary = "未找到资产反向索引数据库。";
                    AssetIndexBuiltUtc = "无";
                    AssetIndexCounts = "无";
                    AssetIndexHint = "可直接建立索引；在线资料仅用于补充显示名称。";
                    return;
                }

                AssetIndexBuiltUtc = fingerprint.BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                AssetIndexCounts = $"Archive {fingerprint.ArchivesIndexed}/{fingerprint.ArchivesTotal}，AssetKey {fingerprint.AssetKeysTotal}";
                var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(_paths)
                    .GetArchiveHashesJsonAsync(cancellationToken);
                var status = await index.GetIndexStatusAsync(gameData, archiveHashes, cancellationToken);
                SettingsService.SetLastGameDataIndexCheckUtc(DateTime.UtcNow);
                OnPropertyChanged(nameof(GameDataIndexLastCheckText));
                AssetIndexState = ToDisplayState(status.State);
                AssetIndexSummary = status.State switch
                {
                    GameDataIndexState.Current => "索引与当前 Game Data 匹配。",
                    GameDataIndexState.Stale => "索引存在但已过期，游戏文件或权威装备关系表已变化。",
                    GameDataIndexState.Invalid => "索引状态无法验证，请检查 GameData 文件。",
                    _ => "未找到资产反向索引数据库。"
                };
                AssetIndexHint = status.IsCurrent
                    ? (File.Exists(_paths.ArchiveHashesPath)
                        ? "资产标签使用真实 archive 语义；在线资料仅补充显示名称。"
                        : "资产标签使用真实 archive 语义；未安装在线显示资料，部分名称将显示为哈希。")
                    : "索引过期或缺失；不会自动重建，请确认后手动启动重建。";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                AssetIndexState = "检查失败";
                AssetIndexSummary = exception.Message;
                AssetIndexHint = "请检查 Game Data 路径与游戏文件完整性。";
            }
            finally
            {
                IsRefreshingAssetIndexStatus = false;
                RefreshAssetIndexStatusCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task BuildAssetIndexAsync()
        {
            if (IsBuildingAssetIndex) return;
            var gameData = SettingsService.GetGameDataFolder();
            if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
            {
                AssetIndexState = "无法建立";
                AssetIndexSummary = "Game Data 目录未设置或不存在。";
                return;
            }
            IsBuildingAssetIndex = true;
            BuildAssetIndexCommand.RaiseCanExecuteChanged();
            RefreshAssetIndexStatusCommand.RaiseCanExecuteChanged();
            var task = _backgroundTasks?.Enqueue(
                BackgroundTaskKind.BuildAssetIndex,
                "建立资产索引",
                gameData,
                origin: "设置与资产",
                userVisibleReason: "用户手动请求建立或重建 Game Data 资产索引。",
                suggestedAction: "完成后可在此页面确认索引状态，或打开资产浏览器。",
                retry: BuildAssetIndexAsync,
                presentation: BackgroundTaskPresentation.Foreground);
            try
            {
                task?.MarkRunning("正在准备资产索引");
                AssetIndexState = "建立中";
                AssetIndexSummary = "正在扫描实际 Archive TOC、权威装备关系与资源映射，并重建 Armor / Helmet Unit 部件事实。";
                var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(_paths)
                    .GetArchiveHashesJsonAsync(task?.CancellationToken ?? CancellationToken.None);
                var index = CoreServices.CreateAssetArchiveIndexService(_paths);
				var lastProgressUpdate = 0L;
                var progress = new Progress<IndexBuildProgress>(item =>
                {
					var now = Environment.TickCount64;
					if (now - Interlocked.Read(ref lastProgressUpdate) < 200 && item.Current < item.Total) return;
					Interlocked.Exchange(ref lastProgressUpdate, now);
                    task?.UpdateStage($"正在索引 Archive {item.Current}/{item.Total}");
                    task?.UpdateProgress(item.Total <= 0 ? null : (double)item.Current / item.Total);
                    AssetIndexCounts = $"Archive {item.Current}/{item.Total}";
                });
                await Task.Run(() => index.BuildOrRebuildAsync(gameData, archiveHashes, progress, task?.CancellationToken ?? CancellationToken.None).AsTask());

                // **接着把「Armor / Helmet Unit 部位事实」一起算出来。**
                //
                // 这两半以前是两个按钮：主按钮只建基础索引，部位事实要另点一次。可是 VPK 换甲、
                // 跨护甲规划、护甲映射页全都依赖部位事实（以及它顺带写出的
                // `data/hidden-unit-cache`）——用户装完包点"重建资产"再去换甲，就会撞上
                // "索引不可用/需要重建"的提示，而他明明刚重建过。
                // CLI 的 `gamedata index apply` 一直是两半连着做的，这里对齐它。
                task?.MarkRunning("正在分析 Armor / Helmet Unit 部位");
                AssetIndexState = "部位计算中";
                AssetIndexSummary = "基础索引已就绪，正在计算 Unit 部件部位与隐藏 Unit 缓存。";
                var advancedIndex = CoreServices.CreateAdvancedEquipmentIndexService(_paths);
                var unitProgress = new Progress<IndexBuildProgress>(item =>
                {
                    task?.UpdateStage(item.Current <= 0
                        ? item.CurrentArchiveId ?? "正在分析 Unit 部位"
                        : $"正在分析 Unit 部位 {item.Current}/{item.Total}");
                    task?.UpdateProgress(item.Total <= 0 ? null : (double)item.Current / item.Total);
                    AssetIndexCounts = $"Unit 部位 {item.Current}/{item.Total}";
                });
                // `BuildMissing…` 而不是 `RebuildAll…`：基础索引刚重建时"缺事实"就是全部，于是它等价于
                // 全量重算；而重复点这个按钮时它只处理真正还缺的那几个（实测 0.1 秒）。
                // 不用 `IsCurrentAsync` 当门：那个判据要求**每一个**注册表 unit 都有事实，
                // 只要有一个读不出来的（实测 2 个只存在于 bundle 里的归档），门就永远为假，
                // 于是每次重建都会白跑八十秒全量分析。
                var token = task?.CancellationToken ?? CancellationToken.None;
                await Task.Run(() => advancedIndex.BuildMissingUnitPartFactsAsync(gameData, unitProgress, token).AsTask());

                task?.MarkCompleted();
                AssetIndexHint = "资产索引已重建，Unit 部件部位事实与隐藏 Unit 缓存也已就绪。";
                await Task.Run(
                    () => _library.RefreshAssetSummariesAsync(task?.CancellationToken ?? CancellationToken.None),
                    task?.CancellationToken ?? CancellationToken.None);
                await RefreshAssetIndexStatusAsync();
            }
            catch (OperationCanceledException)
            {
                task?.MarkCanceled();
                AssetIndexState = "已取消";
                AssetIndexSummary = "资产索引建立已取消。";
            }
            catch (Exception exception)
            {
                task?.MarkFailed(exception.Message);
                AssetIndexState = "建立失败";
                AssetIndexSummary = exception.Message;
            }
            finally
            {
                IsBuildingAssetIndex = false;
                BuildAssetIndexCommand.RaiseCanExecuteChanged();
                RefreshAssetIndexStatusCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task BuildUnitPartFactsAsync(bool rebuildAll)
        {
            if (IsBuildingAssetIndex) return;
            var gameData = SettingsService.GetGameDataFolder();
            if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
            {
                AssetIndexState = "无法建立";
                AssetIndexSummary = "Game Data 目录未设置或不存在。";
                return;
            }

            IsBuildingAssetIndex = true;
            BuildAssetIndexCommand.RaiseCanExecuteChanged();
            RebuildAllUnitPartFactsCommand.RaiseCanExecuteChanged();
            BuildMissingUnitPartFactsCommand.RaiseCanExecuteChanged();
            var task = _backgroundTasks?.Enqueue(
                BackgroundTaskKind.BuildAssetIndex,
                rebuildAll ? "完全重算护甲类 Unit 部位" : "重算新增的护甲类 Unit 部位",
                gameData,
                origin: "设置与资产",
                userVisibleReason: rebuildAll ? "强制重建所有 Armor / Helmet Unit 部位事实。" : "仅补齐数据库中尚无部位事实的 Armor / Helmet Unit。",
                suggestedAction: "完成后可重新打开替换护甲规划器。",
                retry: () => BuildUnitPartFactsAsync(rebuildAll),
                presentation: BackgroundTaskPresentation.Foreground);
            try
            {
                task?.MarkRunning(rebuildAll ? "正在完全重算 Armor / Helmet Unit 部位" : "正在补齐缺失的 Armor / Helmet Unit 部位");
                AssetIndexState = "部位计算中";
                AssetIndexSummary = rebuildAll ? "正在强制重算所有 Armor / Helmet Unit 部件部位。" : "正在查找并计算缺少部位事实的 Armor / Helmet Unit。";
                var index = CoreServices.CreateAdvancedEquipmentIndexService(_paths);
                var progress = new Progress<IndexBuildProgress>(item =>
                {
                    task?.UpdateStage(item.Current <= 0 ? item.CurrentArchiveId ?? "正在分析 Unit 部位" : $"正在分析 Unit 部位 {item.Current}/{item.Total}");
                    task?.UpdateProgress(item.Total <= 0 ? null : (double)item.Current / item.Total);
                    AssetIndexCounts = $"Unit 部位 {item.Current}/{item.Total}";
                });
                if (rebuildAll)
                    await Task.Run(() => index.RebuildAllUnitPartFactsAsync(gameData, progress, task?.CancellationToken ?? CancellationToken.None).AsTask());
                else
                    await Task.Run(() => index.BuildMissingUnitPartFactsAsync(gameData, progress, task?.CancellationToken ?? CancellationToken.None).AsTask());
                task?.MarkCompleted();
                AssetIndexState = "部位事实已完成";
                AssetIndexSummary = rebuildAll ? "所有 Armor / Helmet Unit 部位事实已重算。" : "缺少部位事实的 Armor / Helmet Unit 已补齐。";
                await RefreshAssetIndexStatusAsync();
            }
            catch (OperationCanceledException)
            {
                task?.MarkCanceled();
                AssetIndexState = "已取消";
            }
            catch (Exception exception)
            {
                task?.MarkFailed(exception.Message);
                AssetIndexState = "部位计算失败";
                AssetIndexSummary = exception.Message;
            }
            finally
            {
                IsBuildingAssetIndex = false;
                BuildAssetIndexCommand.RaiseCanExecuteChanged();
                RebuildAllUnitPartFactsCommand.RaiseCanExecuteChanged();
                BuildMissingUnitPartFactsCommand.RaiseCanExecuteChanged();
            }
        }

        private void ViewGameDataIndex()
        {
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenGameDataBrowser();
        }

        private static void OpenArmorRegistry()
        {
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenArmorRegistry();
        }

        public void PromptLanguageIfMissing()
        {
            if (!string.IsNullOrWhiteSpace(SettingsService.GetLanguage())) return;
            var result = System.Windows.MessageBox.Show(
                "选择语言?\nYes: 中文(zh-CN) / No: English(en-US)",
                "Language",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            Language = result == System.Windows.MessageBoxResult.Yes ? "zh-CN" : "en-US";
            System.Windows.MessageBox.Show("语言已设置为 " + Language + "，请重启应用以生效。", "Language", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private static void OpenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void OpenGameDataFolder()
        {
            var folder = GameDataFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = SettingsService.TryDetectAndSetGameDataFolder();
                OnPropertyChanged(nameof(GameDataFolder));
            }
            OpenFolder(folder);
        }

        private void OpenDefaultExportFolder()
        {
            OpenFolder(SettingsService.GetResolvedDefaultExportPath());
        }

        private void DetectGameDataFolder()
        {
            GameDataFolder = SettingsService.TryDetectAndSetGameDataFolder();
        }

        private async void UpdateAssetMetadata()
        {
            AssetMetadataStatus = "正在更新资产信息...";
            var task = _backgroundTasks?.Enqueue(
                BackgroundTaskKind.UpdateAssetMetadata,
                "更新资产元数据",
                "由设置页手动启动",
                origin: "设置与资产",
                userVisibleReason: "用户手动检查在线资产索引源。",
                suggestedAction: "失败时请检查仓库地址、网络连接与访问权限。",
                presentation: BackgroundTaskPresentation.Foreground);
            try
            {
                task?.MarkRunning("正在同步资产元数据");
                var paths = SettingsService.CreateStoragePaths();
                var sync = CoreServices.CreateAssetMetadataSyncService(paths);
                var result = await sync.SyncAsync(AssetMetadataRepository);
                if (!result.Success)
                {
                    AssetMetadataStatus = $"更新失败：{result.ErrorMessage}";
                    task?.MarkFailed(result.ErrorMessage ?? "未知错误");
                    System.Windows.MessageBox.Show(AssetMetadataStatus, "资产信息", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AssetMetadataStatus = $"更新成功：{result.UpdatedFiles.Count} 个文件，{result.UpdatedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                SettingsService.SetLastAssetMetadataCheckUtc(DateTime.UtcNow);
                await Task.Run(() => _library.RefreshAssetSummariesAsync());
                task?.MarkCompleted();
                OnPropertyChanged(nameof(AssetMetadataLastCheckText));
                System.Windows.MessageBox.Show(AssetMetadataStatus, "资产信息", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AssetMetadataStatus = $"更新失败：{ex.Message}";
                task?.MarkFailed(ex.Message);
                System.Windows.MessageBox.Show(AssetMetadataStatus, "资产信息", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private static string FormatLastCheck(DateTime? value)
            => value is null ? "尚未检查" : $"上次检查：{value.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        private static string ToDisplayState(GameDataIndexState state)
            => state switch
            {
                GameDataIndexState.Current => "可用",
                GameDataIndexState.Stale => "过期",
                GameDataIndexState.Missing => "缺失",
                GameDataIndexState.Invalid => "无效",
                _ => "未知"
            };

        private static string BuildInitialAssetMetadataStatus()
        {
            var paths = SettingsService.CreateStoragePaths();
            if (!File.Exists(paths.AssetMetadataManifestPath)) return "尚未更新资产信息";
            try
            {
                var updated = File.GetLastWriteTime(paths.AssetMetadataManifestPath);
                return $"本地缓存已存在，最后写入 {updated:yyyy-MM-dd HH:mm:ss}";
            }
            catch
            {
                return "本地缓存已存在";
            }
        }
    }
}
