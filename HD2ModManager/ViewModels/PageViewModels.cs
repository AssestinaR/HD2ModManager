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
						GameDataIndexState.Stale => "你的游戏已经更新，请前往设置页重新拉取在线索引并构建 GameData 索引。",
						GameDataIndexState.Missing => "尚未构建 GameData 索引，请前往设置页拉取在线索引并构建 GameData 索引。",
						_ => "GameData 索引不可用，请前往设置页重新拉取在线索引并构建 GameData 索引。"
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
				SetGameDataHealth("索引不可用", "GameData 索引不可用，请前往设置页重新拉取在线索引并构建 GameData 索引。");
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
                if (!File.Exists(_paths.ArchiveHashesPath))
                    throw new InvalidOperationException("缺少 archivehashes.json；请先在设置页更新在线资产信息。");

                task.MarkRunning("正在重建 GameData 资产索引");
                var archiveHashes = await File.ReadAllTextAsync(_paths.ArchiveHashesPath, task.CancellationToken);
                var assetIndex = CoreServices.CreateAssetArchiveIndexService(_paths);
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

                task.UpdateStage("正在读取 Unit 版本并检测过时 Mod");
                var nodes = new List<ModNode>();
                var unreadableCount = 0;
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
                }
                SetOutdatedDetectionResult(nodes.Count, unreadableCount);
                if (nodes.Count == 0)
                {
                    task.MarkCompleted();
                    var summary = unreadableCount == 0
                        ? "检测完成：未发现需要修复的过时 Mod。"
                        : $"检测完成：未发现需要修复的过时 Mod；{unreadableCount} 个 Mod 的 Unit 版本无法确认，已跳过。";
                    System.Windows.MessageBox.Show(summary, "检测并修复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var unreadableNotice = unreadableCount == 0 ? string.Empty : $"\n另有 {unreadableCount} 个 Mod 的 Unit 版本无法确认，本次将跳过。";
                var confirm = System.Windows.MessageBox.Show($"已检测到 {nodes.Count} 个过时 Mod。{unreadableNotice}\n\n将仅为这些 Mod 生成当前版本候选，并仅在候选完整通过内部检查后替换原 Patch。原 Patch 与 sidecar 会独立备份到管理器目录 backups。是否继续？", "检测并修复过时 Mod", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (confirm != System.Windows.MessageBoxResult.Yes)
                {
                    task.MarkCompleted();
                    return;
                }

                task.UpdateStage("正在生成并验证重构候选");
                task.UpdateProgress(null);
                var result = await _repairBatch.RepairAsync(nodes, _library.ModsRootDirectory, gameData, task.CancellationToken, new InlineProgress<OperationProgressEvent>(bridge.Apply), operationId);
                task.UpdateStage($"已修复 {result.RepairedModCount}；跳过 {result.SkippedModCount}；失败 {result.FailedModCount}；取消 {result.CanceledModCount}；未开始 {result.NotStartedModCount}");
                if (result.CanceledModCount > 0 || result.NotStartedModCount > 0 || task.CancellationToken.IsCancellationRequested) task.MarkCanceled();
                else task.MarkCompleted();
                if (result.HasRepairs)
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
                System.Windows.MessageBox.Show($"批次完成。\n已修复：{result.RepairedModCount}\n跳过：{result.SkippedModCount}\n失败：{result.FailedModCount}\n取消：{result.CanceledModCount}\n未开始：{result.NotStartedModCount}\n\n备份与审计清单：{result.BatchDirectory}", "一键修复", System.Windows.MessageBoxButton.OK, result.FailedModCount == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
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
        public string EmptyMessage => "当前配置中没有 Mod。可以从模组库添加；若右侧为空，则所有 Mod 都已加入此配置。";
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
        public bool CanReorder => string.IsNullOrWhiteSpace(Query) && !ShowOnlyOutdated;
        private bool _canAcceptLibraryDrop;
        public bool CanAcceptLibraryDrop => _canAcceptLibraryDrop;

        public void SetLibraryDropTargetAvailable(bool available)
            => SetField(ref _canAcceptLibraryDrop, available, nameof(CanAcceptLibraryDrop));

        private string? _selectedProfileKey;
        private string _renameText = string.Empty;
        private ProfileSwitchActionViewModel? _switchAction;
        private ProfileRenameActionViewModel? _renameAction;
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
            PageActions.Add(new PageActionViewModel("＋", "新建配置", CreateProfileCommand, order: 10, kind: "CreateProfile"));
            PageActions.Add(new PageActionViewModel("⇄", "切换当前配置", new RelayCommand(_ => _bottomBar?.BeginSwitchProfile()), order: 12, kind: "SwitchProfile"));
            PageActions.Add(new PageActionViewModel("▶", "设为活动配置", ActivateProfileCommand, background: new SolidColorBrush(Color.FromRgb(26, 127, 75)), order: 15, kind: "ActivateProfile"));
            PageActions.Add(new PageActionViewModel("■", "停用活动配置", DeactivateProfileCommand, background: new SolidColorBrush(Color.FromRgb(94, 100, 112)), order: 16, kind: "DeactivateProfile"));
            PageActions.Add(new PageActionViewModel("✎", "重命名配置", new RelayCommand(_ => _bottomBar?.BeginRenameProfile()), background: new SolidColorBrush(Color.FromRgb(30, 99, 214)), order: 20, kind: "RenameProfile"));
            PageActions.Add(new PageActionViewModel("🗑", "删除当前配置", RemoveSelectedProfileCommand, background: new SolidColorBrush(Color.FromRgb(179, 38, 30)), order: 30, kind: "RemoveProfile"));
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
            OnPropertyChanged(nameof(EmptyMessage));
            _switchAction?.SyncFromPage();
            _renameAction?.SyncFromPage();

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
            foreach (var entry in _profiles.GetSortedEntries(profile))
            {
                var guid = entry.NodeId.Value.ToString("N");
                var mod = _library.Get(guid);
                var derived = _library.GetDerivedData(guid);
                var assetSummary = derived?.AssetSummary;
                if (!ModSearchMatcher.IsMatch(mod?.Name, mod?.Description, assetSummary, Query)) continue;
                if (ShowOnlyOutdated && derived?.UnitCompatibility.IsOutdated != true) continue;
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
                && (!string.IsNullOrWhiteSpace(Query) || ShowOnlyOutdated);
            if (requiresRebuild || contentCanAffectMembership)
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
            _switchAction?.SyncFromPage();
            _renameAction?.SyncFromPage();
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
            OnPropertyChanged(nameof(EmptyMessage));
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
        public string? ImagePath => _imagePath;
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
            _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
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
            _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
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
            if (imageChanged) _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
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
        public RelayCommand RebuildAllUnitPartFactsCommand { get; }
        public RelayCommand BuildMissingUnitPartFactsCommand { get; }
        public RelayCommand UpdateAssetMetadataCommand { get; }
        public RelayCommand RefreshAssetIndexStatusCommand { get; }
        public RelayCommand BuildAssetIndexCommand { get; }
        private bool _isLoadingGameDataIndex;
        public bool IsLoadingGameDataIndex { get => _isLoadingGameDataIndex; private set => SetField(ref _isLoadingGameDataIndex, value); }

        public SettingsPageViewModel(ProfileService profiles, ModLibraryService library, BottomBarCoordinator bottomBar, BackgroundTaskService? backgroundTasks = null)
        {
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
            RebuildAllUnitPartFactsCommand = new RelayCommand(_ => _ = BuildUnitPartFactsAsync(rebuildAll: true), _ => !IsBuildingAssetIndex);
            BuildMissingUnitPartFactsCommand = new RelayCommand(_ => _ = BuildUnitPartFactsAsync(rebuildAll: false), _ => !IsBuildingAssetIndex);
            UpdateAssetMetadataCommand = new RelayCommand(UpdateAssetMetadata);
            RefreshAssetIndexStatusCommand = new RelayCommand(_ => _ = RefreshAssetIndexStatusAsync(), _ => !IsRefreshingAssetIndexStatus && !IsBuildingAssetIndex);
            BuildAssetIndexCommand = new RelayCommand(_ => _ = BuildAssetIndexAsync(), _ => !IsBuildingAssetIndex);
        }

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
                    AssetIndexHint = "更新在线资产信息后，明确启动“建立 / 重建资产索引”。";
                    return;
                }

                AssetIndexBuiltUtc = fingerprint.BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                AssetIndexCounts = $"Archive {fingerprint.ArchivesIndexed}/{fingerprint.ArchivesTotal}，AssetKey {fingerprint.AssetKeysTotal}";
                if (!File.Exists(_paths.ArchiveHashesPath))
                {
                    AssetIndexState = "无法校验";
                    AssetIndexSummary = "索引存在，但缺少 archivehashes.json。";
                    AssetIndexHint = "请先更新在线资产信息。";
                    return;
                }

                var archiveHashes = await File.ReadAllTextAsync(_paths.ArchiveHashesPath, cancellationToken);
                var status = await index.GetIndexStatusAsync(gameData, archiveHashes, cancellationToken);
                SettingsService.SetLastGameDataIndexCheckUtc(DateTime.UtcNow);
                OnPropertyChanged(nameof(GameDataIndexLastCheckText));
                AssetIndexState = ToDisplayState(status.State);
                AssetIndexSummary = status.State switch
                {
                    GameDataIndexState.Current => "索引与当前 Game Data 匹配。",
                    GameDataIndexState.Stale => "索引存在但已过期，游戏文件或资产元数据已变化。",
                    GameDataIndexState.Invalid => "索引状态无法验证，资产元数据格式可能无效。",
                    _ => "未找到资产反向索引数据库。"
                };
                AssetIndexHint = status.IsCurrent
                    ? "资产标签可以使用真实 archive 语义。"
                    : "索引过期或缺失；不会自动重建，请确认后手动启动重建。";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                AssetIndexState = "检查失败";
                AssetIndexSummary = exception.Message;
                AssetIndexHint = "请检查 Game Data 路径与在线资产信息。";
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
            if (!File.Exists(_paths.ArchiveHashesPath))
            {
                AssetIndexState = "无法建立";
                AssetIndexSummary = "缺少 archivehashes.json；请先更新在线资产信息。";
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
                AssetIndexSummary = "正在扫描 Archive TOC、资源映射，并重建 Armor / Helmet Unit 部件事实。";
                var archiveHashes = await File.ReadAllTextAsync(_paths.ArchiveHashesPath);
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
                task?.MarkCompleted();
                AssetIndexHint = "基础资产索引已重建；Unit 部件部位事实需通过右侧专用按钮单独计算。";
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
