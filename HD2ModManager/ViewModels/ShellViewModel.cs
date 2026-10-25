using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Enums;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    public sealed partial class ShellViewModel : BaseViewModel, IAsyncDisposable
    {
        private enum ArmorRegistrySelectionAction { Promote, ReleaseProviders, ReleaseTargets }

        // 作用：一次导航**之前**的页面组合，用于按 Esc 逐层返回。
        // 只保存"重建这次组合所需的最小信息"：左右页面类型、右槽是否存在、
        // 当时的 WorkspaceMode（UpdateModeFromSlots 对未识别的组合会保留旧值，所以必须一起记）、
        // 以及两个会改变 CreatePage 结果的易变字段（SelectedModId / _editingDecorationId）。
        private readonly record struct WorkspaceHistoryEntry(
            WorkspacePageType LeftType,
            WorkspacePageType RightType,
            bool HasRightPage,
            WorkspaceMode Mode,
            string? SelectedModId,
            string? EditingDecorationId);
        private readonly ProfileService _profileService;
        private readonly ModLibraryService _libraryService;
        private readonly ImportQueueService _importQueue;
        private readonly BackgroundTaskService _backgroundTasks;
        private readonly ApplyStatusService _applyStatus;
        private readonly DerivedStateCoordinator _derivedState;
        private readonly NotificationService _notificationService;
        private readonly MessageCenterService _messageCenter;
        private readonly IProfileDeploymentCoordinator _deploymentCoordinator;
        private BackgroundTaskItem? _deploymentTask;
		private System.Windows.Threading.DispatcherTimer? _deploymentBufferTimer;
        private readonly SelectionCoordinator _selection = new();
        private readonly BottomBarCoordinator _bottomBar;
        private readonly List<IDisposable> _materialPackagingBottomBarRegistrations = [];
        private MaterialPackagingPageViewModel? _materialPackagingBottomBarOperation;
        private readonly List<IDisposable> _sameKeyRebuildBottomBarRegistrations = [];
        private SameKeyRebuildBottomBarViewModel? _sameKeyRebuildBottomBarOperation;
        private readonly SemaphoreSlim _importProcessGate = new(1, 1);
        private int _pageRefreshQueued;

        private PageViewModel? _leftPage;
        private PageViewModel? _rightPage;
        // These workspace pages own long-lived list state. Navigation only changes hosts.
        private ProfilePageViewModel? _profileWorkspacePage;
        private LibraryPageViewModel? _libraryWorkspacePage;
        private ArmorRegistryPageViewModel? _armorRegistryWorkspacePage;
        private WorkspaceMode _currentMode;
        private WorkspacePageType _leftPageType;
        private WorkspacePageType _rightPageType;
        private string? _selectedModId;
        private string? _editingDecorationId;
        // Esc 返回队列。按**导航事件**计数（不是按时间），超过上限丢最旧的一条。
        // 只活在本次会话里：不落盘、不跨会话，也不参与任何持久化。
        private const int NavigationHistoryLimit = 20;
        private readonly List<WorkspaceHistoryEntry> _navigationHistory = [];
        private WorkspaceHistoryEntry? _lastWorkspaceEntry;
        private bool _isRestoringWorkspace;
        private bool _isMessagePanelOpen;
        private bool _isMessagePreviewOpen;
        private readonly IModInformationCenter _informationCenter;
		private readonly IModInformationReader _informationReader;
        private readonly System.Collections.Generic.Dictionary<string, BackgroundTaskItem> _informationTasks = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private int _libraryProjectionRequested;
        private int _activeProfileProjectionRequested;
        private int _workspacePrewarmQueued;
        private int _disposed;
        // 护甲映射页取数的闸门（见 InitializeLibraryAndRunStartupChecksAsync 的 finally）。
        // 它是"库快照已加载 且 启动部署已落定"的替身：护甲页的显示事实（图标 + 覆盖）
        // 只有在这两件事之后才算得对，而视觉预热会在窗口刚 Loaded 时就把这一页建出来。
        private readonly TaskCompletionSource _armorDisplayFactsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PageViewModel? CurrentPage => LeftPage;
        public bool HasMaterialPackagingBottomBar => _materialPackagingBottomBarOperation is not null;
        public bool HasSameKeyRebuildBottomBar => _sameKeyRebuildBottomBarRegistrations.Count != 0;
        public PageViewModel? LeftPage
        {
            get => _leftPage;
            private set
            {
                if (ReferenceEquals(_leftPage, value)) return;
                var previous = _leftPage;
                if (SetField(ref _leftPage, value))
                {
                    DisposePageIfTransient(previous, _rightPage);
                    OnPropertyChanged(nameof(CurrentPage));
                    RaiseActionFlags();
                }
            }
        }
        public PageViewModel? RightPage
        {
            get => _rightPage;
            private set
            {
                if (ReferenceEquals(_rightPage, value)) return;
                var previous = _rightPage;
                if (SetField(ref _rightPage, value))
                {
                    DisposePageIfTransient(previous, _leftPage);
                    RaiseActionFlags();
                }
            }
        }
        public WorkspaceMode CurrentMode { get => _currentMode; private set { if (SetField(ref _currentMode, value)) RaiseModeFlags(); } }
        public WorkspacePageType LeftPageType { get => _leftPageType; private set { if (SetField(ref _leftPageType, value)) RaiseSlotFlags(); } }
        public WorkspacePageType RightPageType { get => _rightPageType; private set { if (SetField(ref _rightPageType, value)) RaiseSlotFlags(); } }
        public string? SelectedModId { get => _selectedModId; private set => SetField(ref _selectedModId, value); }
        public bool IsMessagePanelOpen { get => _isMessagePanelOpen; private set => SetField(ref _isMessagePanelOpen, value); }
        public ReadOnlyObservableCollection<MessageCenterItem> ActiveMessageTasks => _messageCenter.ActiveTasks;
        public ReadOnlyObservableCollection<MessageCenterItem> MessageItems => _messageCenter.Messages;
        public ReadOnlyObservableCollection<MessageCenterItem> ActiveTaskToasts => _messageCenter.ActiveTaskToasts;
        public bool HasActiveTaskToasts => _messageCenter.HasActiveTaskToasts;
        public MessageCenterItem? CurrentTransientToast => _messageCenter.CurrentTransientToast;
        public bool HasTransientToast => _messageCenter.HasTransientToast;
        public int ActiveTaskCount => _backgroundTasks.CountQueued + _backgroundTasks.CountRunning;
        public int MessageUnreadCount => _messageCenter.SuppressedUnreadCount;
        public bool HasUnreadTaskHubEvents => MessageUnreadCount > 0;
        public string MessageBadgeText => MessageUnreadCount > 9 ? "9+" : MessageUnreadCount.ToString();
        public bool IsMessagePreviewOpen { get => _isMessagePreviewOpen; private set => SetField(ref _isMessagePreviewOpen, value); }

        public RelayCommand ShowHomeCommand { get; }
        public RelayCommand ShowProfileCommand { get; }
        public RelayCommand ShowLibraryCommand { get; }
        public RelayCommand LaunchGameCommand { get; }
        public RelayCommand ShowSplitCommand { get; }
        public RelayCommand ShowSettingsCommand { get; }
        public RelayCommand ShowArmorRegistryCommand { get; }
        public RelayCommand ApplyChangesCommand { get; }
        public RelayCommand ValidateModLibraryCommand { get; }
        public RelayCommand ImportCommand { get; }
        public RelayCommand CreateLogicalModCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand CancelSelectionCommand { get; }
        public RelayCommand SelectionPrimaryCommand { get; }
        public RelayCommand SelectionDeleteCommand { get; }
        public RelayCommand SelectionDeleteFromLibraryCommand { get; }
        public RelayCommand SetSelectedModsAsOptionsCommand { get; }
        public RelayCommand AttachSelectedDecorationsCommand { get; }
        public RelayCommand RemoveSelectedOptionsFromHostCommand { get; }
        public RelayCommand PromoteArmorProvidersCommand { get; }
        public RelayCommand ReleaseArmorProvidersCommand { get; }
        public RelayCommand ReleaseArmorTargetsCommand { get; }
        public RelayCommand EnableSelectedDecorationsCommand { get; }
        public RelayCommand DisableSelectedDecorationsCommand { get; }
        public RelayCommand ToggleMessagePanelCommand { get; }
        public RelayCommand CancelTaskCommand { get; }
        public RelayCommand RetryTaskCommand { get; }
        public RelayCommand OpenTaskReportCommand { get; }
        public RelayCommand OpenTaskOutputCommand { get; }
        public RelayCommand DismissMessageCommand { get; }
        public RelayCommand ClearReadMessagesCommand { get; }
        public RelayCommand CopyMessageCommand { get; }
        public RelayCommand ToggleMessageExpansionCommand { get; }

        public bool IsHomeActive => CurrentMode == WorkspaceMode.Home;
        public bool ShowHomeTitle => IsHomeActive;
        public bool ShowLaunchGameButton => !IsHomeActive;
        public bool IsProfileActive => CurrentMode == WorkspaceMode.ProfileOnly;
        public bool IsLibraryActive => CurrentMode == WorkspaceMode.LibraryOnly;
        public bool IsSplitActive => CurrentMode == WorkspaceMode.ProfileLibrarySplit;
        public bool IsSettingsActive => CurrentMode == WorkspaceMode.Settings;
        public bool IsArmorRegistryActive => CurrentMode == WorkspaceMode.ArmorRegistry;
        // Page type alone is insufficient: two different Mod detail pages both
        // use ModDetails, but they still need to occupy two visible slots.
        public bool IsSplitView => RightPage is not null
            && (LeftPageType != RightPageType || !ReferenceEquals(LeftPage, RightPage));
        public bool ShowRightSlot => IsSplitView;
        public bool IsLeftSlotLibrary => LeftPageType == WorkspacePageType.Library;
        public bool IsRightSlotLibrary => IsSplitView && RightPageType == WorkspacePageType.Library;
        public bool IsFullScreenLibrary => !IsSplitView && LeftPageType == WorkspacePageType.Library;
        public bool IsSplitLeftSlotLibrary => IsSplitView && LeftPageType == WorkspacePageType.Library;
        public ObservableCollection<PageActionViewModel>? LeftSlotActions => LeftPage?.PageActions;
        public ObservableCollection<PageActionViewModel>? RightSlotActions => RightPage?.PageActions;
        public bool HasLeftSlotActions => LeftSlotActions?.Count > 0;
        public bool HasRightSlotActions => RightSlotActions?.Count > 0;
        public bool ShowSplitLeftSlotActions => IsSplitView && HasLeftSlotActions;
        public bool ShowRightSlotActions => IsSplitView && HasRightSlotActions;
        public bool ShowFullScreenActions => !IsSplitView && HasLeftSlotActions;
        public string LeftSlotTitle => GetPageTitle(LeftPageType);
        public string RightSlotTitle => GetPageTitle(RightPageType);

        public ModLibraryService LibraryService => _libraryService;
        public ProfileService ProfileService => _profileService;
        public ImportQueueService ImportQueue => _importQueue;
        public BackgroundTaskService BackgroundTasks => _backgroundTasks;
        public SelectionCoordinator Selection => _selection;
        public BottomBarCoordinator BottomBar => _bottomBar;
        public bool HasSelection => _selection.HasSelection;
        public string SelectionSummary => _selection.Summary;
        public string SelectionPrimaryText => string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase) ? "移除" : "加入配置";
        public string SelectionDeleteText => string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase) ? "移除" : "删除";

        public ShellViewModel()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var configDir = System.IO.Path.Combine(baseDir, "config");
            System.IO.Directory.CreateDirectory(configDir);

            // Services capture StoragePaths during construction, so initial path discovery must precede them.
            SettingsService.InitializeDefaultPaths();

            _profileService = new ProfileService(configDir);

            var storagePaths = SettingsService.CreateStoragePaths();
            var informationReader = CoreServices.CreateModInformationReader();
			_informationReader = informationReader;
            var informationCenter = CoreServices.CreateModInformationCenter(storagePaths, informationReader);
            _informationCenter = informationCenter;
            var optionActivations = CoreServices.CreateOptionActivationStore(storagePaths);
            _libraryService = new ModLibraryService(
                System.IO.Path.Combine(configDir, "library.json"),
                informationCenter,
                optionActivations,
                informationReader);
            _derivedState = new DerivedStateCoordinator(_libraryService, _profileService, informationCenter);
            _importQueue = new ImportQueueService();
            _backgroundTasks = new BackgroundTaskService();
            _libraryService.AttachBackgroundTasks(_backgroundTasks);
            _applyStatus = new ApplyStatusService();
            _notificationService = new NotificationService();
            _messageCenter = new MessageCenterService(_notificationService, _backgroundTasks);
            _informationCenter.ProductionStarted += OnInformationProductionStarted;
            _informationCenter.DiagnosticRecorded += OnInformationDiagnosticRecorded;
            _bottomBar = new BottomBarCoordinator(_selection, _libraryService, _profileService, _notificationService, RefreshCurrentPage);
            _deploymentCoordinator = CoreServices.CreateProfileDeploymentCoordinator(
                storagePaths,
                SettingsService.GetGameDataFolder,
                informationCenter,
                optionActivations: optionActivations,
                luaPermissionProvider: () => SettingsService.GetAllowUnsafeLuaMerge()
                    ? HD2ModCore.Application.LuaDeploymentPermission.AllowOpaque
                    : HD2ModCore.Application.LuaDeploymentPermission.DenyOpaque);
            _deploymentCoordinator.StatusChanged += OnDeploymentStatusChanged;
            _profileService.ActiveProfileDeploymentRequired += (_, _) => _deploymentCoordinator.NotifyActiveProfileChanged();
            _profileService.ActiveProfileDeactivationRequired += (_, _) => _ = _deploymentCoordinator.DeactivateAsync();
            _libraryService.ModContentFactsChanged += (_, change) =>
            {
                if (!change.RequiresDeployment || change.Kind == ModContentChangeKind.DerivedOnly)
                {
                    LogService.Info($"部署触发检查：派生投影已刷新，节点数={change.NodeIds.Count}；不触发部署。");
                    return;
                }

                var active = _profileService.ActiveProfile;
                var activeRoutes = active is null
                    ? ProfileOptionRouteResolution.Empty
                    : ProfileOptionRouteResolver.Resolve(active, _libraryService.Snapshot, _libraryService.GetOptionActivationSnapshot());
                var affectsActiveProfile = active is not null && change.NodeIds.Any(nodeId =>
                    active.Entries.Any(entry => entry.NodeId == nodeId)
                    || activeRoutes.IsNodeReachable(nodeId));
                LogService.Info($"部署触发检查：库内容{change.Kind}，节点数={change.NodeIds.Count}，影响活动配置={affectsActiveProfile}。");
                if (affectsActiveProfile)
                {
                    _profileService.NotifyActiveModContentChanged();
                }
            };
            _libraryService.SnapshotChanged += (_, _) => QueueLibrarySnapshotChanged();
            _libraryService.OptionActivationChanged += (_, _) => _deploymentCoordinator.NotifyActiveProfileChanged();
            _backgroundTasks.Changed += (_, args) => RefreshOnUiThread(() =>
            {
                if (!args.RequiresProjectionRefresh) return;
                RefreshTaskHubState();
                ShowMessagePreview();
            });
            _notificationService.Changed += (_, _) => RefreshOnUiThread(RefreshTaskHubState);
            _messageCenter.Changed += (_, _) => RefreshOnUiThread(() =>
            {
                OnPropertyChanged(nameof(CurrentTransientToast));
                OnPropertyChanged(nameof(HasTransientToast));
                OnPropertyChanged(nameof(MessageUnreadCount));
                OnPropertyChanged(nameof(HasUnreadTaskHubEvents));
                OnPropertyChanged(nameof(MessageBadgeText));
                ShowMessagePreview();
            });
			_ = Task.Run(() => new ImportTemporaryDirectoryManager(SettingsService.CreateStoragePaths()).CleanupStaleDirectories());

            ShowHomeCommand = new RelayCommand(() => Navigate(WorkspaceMode.Home));
            ShowProfileCommand = new RelayCommand(() => Navigate(WorkspaceMode.ProfileOnly));
            ShowLibraryCommand = new RelayCommand(() => Navigate(WorkspaceMode.LibraryOnly));
            LaunchGameCommand = new RelayCommand(async _ => await LaunchGameAsync());
            ShowSplitCommand = new RelayCommand(() => Navigate(WorkspaceMode.ProfileLibrarySplit));
            ShowSettingsCommand = new RelayCommand(() => Navigate(WorkspaceMode.Settings));
            ShowArmorRegistryCommand = new RelayCommand(OpenArmorRegistry);
            ApplyChangesCommand = new RelayCommand(QueueActiveProfileDeployment);
            ValidateModLibraryCommand = new RelayCommand(ValidateModLibrary);
            ImportCommand = new RelayCommand(BrowseAndImport);
            CreateLogicalModCommand = new RelayCommand(_ => BeginCreateLogicalMod());
            RefreshCommand = new RelayCommand(RefreshCurrentPage);
            CancelSelectionCommand = new RelayCommand(_selection.Clear);
            SelectionPrimaryCommand = new RelayCommand(async _ => await ExecuteSelectionPrimaryAsync());
            SelectionDeleteCommand = new RelayCommand(async _ => await ExecuteSelectionDeleteAsync());
            SelectionDeleteFromLibraryCommand = new RelayCommand(async _ => await ExecuteSelectionDeleteFromLibraryAsync());
            SetSelectedModsAsOptionsCommand = new RelayCommand(async _ => await SetSelectedModsAsOptionsAsync());
            AttachSelectedDecorationsCommand = new RelayCommand(async _ => await AttachSelectedDecorationsAsync());
            RemoveSelectedOptionsFromHostCommand = new RelayCommand(async _ => await RemoveSelectedOptionsFromHostAsync());
            PromoteArmorProvidersCommand = new RelayCommand(_ => ApplyArmorRegistrySelection(ArmorRegistrySelectionAction.Promote));
            ReleaseArmorProvidersCommand = new RelayCommand(_ => ApplyArmorRegistrySelection(ArmorRegistrySelectionAction.ReleaseProviders));
            ReleaseArmorTargetsCommand = new RelayCommand(_ => ApplyArmorRegistrySelection(ArmorRegistrySelectionAction.ReleaseTargets));
            EnableSelectedDecorationsCommand = new RelayCommand(async _ => await SetSelectedDecorationsEnabledAsync(true));
            DisableSelectedDecorationsCommand = new RelayCommand(async _ => await SetSelectedDecorationsEnabledAsync(false));
            ToggleMessagePanelCommand = new RelayCommand(ToggleMessagePanel);
            CancelTaskCommand = new RelayCommand(CancelTask, task => task is BackgroundTaskItem { CanCancel: true });
            RetryTaskCommand = new RelayCommand(async task => await RetryTaskAsync(task), task => task is BackgroundTaskItem { CanRetry: true });
            OpenTaskReportCommand = new RelayCommand(OpenTaskReport, task => task is BackgroundTaskItem { HasReport: true });
            OpenTaskOutputCommand = new RelayCommand(OpenTaskOutput, task => task is BackgroundTaskItem { HasOutputDirectory: true });
            DismissMessageCommand = new RelayCommand(DismissMessage, item => item is MessageCenterItem { CanDismiss: true });
            ClearReadMessagesCommand = new RelayCommand(_messageCenter.DismissRead);
            CopyMessageCommand = new RelayCommand(CopyMessage, item => item is MessageCenterItem);
            ToggleMessageExpansionCommand = new RelayCommand(item => (item as MessageCenterItem)?.ToggleExpanded(), item => item is MessageCenterItem);
            _selection.SelectionChanged += (_, _) => RaiseSelectionFlags();

            Navigate(WorkspaceMode.Home);
            QueueWorkspacePagePrewarm();
            // 启动维护不得阻塞 ShellViewModel 构造；库元数据、同步、稳定投影按顺序在后台执行。
            // 启动检查在构造函数返回后才调度，避免 async 方法首个 await 前的同步工作阻塞 UI。
            _ = Task.Run(() => InitializeLibraryAndRunStartupChecksAsync(configDir, _lifetimeCancellation.Token));
            _ = Task.Run(() => CheckGameDataIndexOnStartupAsync(_lifetimeCancellation.Token));
        }

        private void QueueWorkspacePagePrewarm()
        {
            if (Interlocked.Exchange(ref _workspacePrewarmQueued, 1) != 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // Build data-only list state after the home page has rendered; no workspace view is created here.
                _profileWorkspacePage ??= new ProfilePageViewModel(_profileService, _libraryService, _derivedState, _selection, _bottomBar);
                if (_libraryWorkspacePage is null)
                {
                    _libraryWorkspacePage = new LibraryPageViewModel(_libraryService, _derivedState, _selection, _profileService, _notificationService, importCommand: ImportCommand);
                    RegisterLibraryActions(_libraryWorkspacePage);
                }
                // 护甲映射页的 VM 也在这里建好。它的构造会给三个集合挂上 CollectionView，
                // 而 CollectionView 绑定的是**创建它的那个 Dispatcher**——所以必须趁 UI 线程空闲时建，
                // 不能等预热数据时再顺手 new。数据预热另见 QueueArmorRegistryPrewarm。
                _ = CreateArmorRegistryPage();
                LogService.Info("工作区页面预热完成：配置页、模组库与护甲映射页 VM 已缓存。");
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // 启动预热：把护甲映射页的数据（目录、快照、最贵的覆盖事实）也算好，
        // 用户点开这一页时就是现成的，不会看到"先空一下再刷出来"。
        //
        // **必须在启动维护之后调用**，两个理由缺一不可：
        // 1. 图标是对"覆盖者节点"的库内查找，库快照没加载时只会查到 null；
        //    而那次结果会被当成"已加载"缓存下来，之后用户看到的将是永远没有图标的护甲列表。
        // 2. 覆盖事实读的是游戏 Data 目录与激活状态。启动部署还没跑完就预热，
        //    算出来的是**我们自己马上就要改掉**的那份部署，页面之后不会再读第二遍。
        private void QueueArmorRegistryPrewarm()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    using var perf = UiPerformanceDiagnostics.Measure("ArmorRegistry.Prewarm", thresholdMilliseconds: 50);
                    // 与用户首开共用同一次加载（ArmorRegistryPageViewModel.EnsureCatalogLoadedAsync），
                    // 所以这里晚于用户操作也不会白做，先到的那一次为准。
                    await CreateArmorRegistryPage().WarmUpAsync();
                }
                catch (Exception exception)
                {
                    // 预热失败不该影响启动：真正打开页面时会自己重试并给出可见的状态。
                    LogService.Warn($"预热护甲映射页失败：{exception.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // MainWindow owns FrameworkElement lifetime, while the Shell owns the
        // reusable page VMs. This explicit bridge lets the window warm actual
        // Profile/Library views without performing a visible navigation.
        internal (ProfilePageViewModel Profile, LibraryPageViewModel Library, ArmorRegistryPageViewModel Armor) GetOrCreateWorkspaceVisualPrewarmPages()
        {
            var profile = CreateProfilePage();
            var library = CreateLibraryPage();
            // The hidden hosts mirror the split workspace so the library warms
            // its profile-companion projection as well as its visual tree.
            library.SetProfileCompanionVisible(true);
            return (profile, library, CreateArmorRegistryPage());
        }

        private async Task InitializeLibraryAndRunStartupChecksAsync(string configDir, CancellationToken cancellationToken)
        {
			var startupClock = Stopwatch.StartNew();
            try
            {
                if (!SettingsService.IsGameDataFolderValid())
                {
                    _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        _notificationService.Show("游戏目录不正确或尚未设置。请在设置页点击“重置”按钮，让程序自动查找游戏目录。", NotificationLevel.Warning, TimeSpan.FromSeconds(10)));
                }
                await _libraryService.LoadAsync(buildDerivedData: false, cancellationToken).ConfigureAwait(false);
				LogStartupCheckpoint("库快照加载", startupClock);
                await _profileService.ReloadFromLibraryAsync(cancellationToken).ConfigureAwait(false);
				LogStartupCheckpoint("配置加载", startupClock);
                if (_profileService.Profiles.Count == 0)
                {
                    const string defaultProfileName = "配置文件（放在这边的mod才会启用）";
                    if (string.IsNullOrWhiteSpace(SettingsService.GetGameDataFolder()))
                        SettingsService.TryDetectAndSetGameDataFolder();
                    await _profileService.CreateNewAsync(defaultProfileName, cancellationToken).ConfigureAwait(false);
                    await _profileService.ActivateSelectedAsync(cancellationToken).ConfigureAwait(false);
                    LogService.Info($"首次启动已创建并启用默认配置：{defaultProfileName}。");
                }
                await RunStartupChecksAsync(configDir, cancellationToken).ConfigureAwait(false);
				LogStartupCheckpoint("启动维护完成", startupClock);
                // 缩略图磁盘缓存的一次性收敛：旧键改名 + 无主文件清扫（设计文档 §27.5）。
                // fire-and-forget：它与库页缩略图刷新幂等且与顺序无关，抢不到先后也不影响正确性；
                // 放在这里是为了让"旧键改名"有机会抢在库页刷新之前完成，升级首启因此不必重生成整库缩略图。
                _ = Task.Run(() => SweepThumbnailCacheAsync(cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                LogService.Info("启动模组库元数据加载已取消。");
            }
            catch (Exception exception)
            {
                LogService.Error($"启动模组库元数据加载失败：{exception}");
                _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    _notificationService.Show("已启动，但模组库元数据加载失败；可在库页刷新后重试。", NotificationLevel.Warning, TimeSpan.FromSeconds(8)));
            }
            finally
            {
                // 无论启动维护成功、失败还是被取消，都必须放行护甲页的取数闸门——
                // 卡住它等于让用户打开护甲页后永远停在"尚未读取"。
                // 放行之后立刻把这一页的数据也预热起来（此刻库快照与启动部署都已落定）。
                _armorDisplayFactsGate.TrySetResult();
                QueueArmorRegistryPrewarm();
            }
        }

        // 缩略图磁盘缓存的一次性收敛（设计文档 §27.5）。失败不影响功能：它只回收磁盘，
        // 最坏结果是多留几个无人引用的缩略图文件，下次启动再扫一遍。
        private async Task SweepThumbnailCacheAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _libraryService.SweepThumbnailCacheAsync(cancellationToken).ConfigureAwait(false);
                if (result.Migrated > 0 || result.Removed > 0)
                {
                    LogService.Info($"缩略图缓存收敛完成：复用旧键={result.Migrated}，清理无主文件={result.Removed}。");
                }
            }
            catch (OperationCanceledException)
            {
                // 退出时取消，属正常路径。
            }
            catch (Exception exception)
            {
                LogService.Warn($"缩略图缓存收敛失败：{exception.Message}");
            }
        }

        private async Task RestoreStableLibraryProjectionAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _libraryService.RefreshDerivedDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogService.Info("稳定模组库投影恢复已取消。");
            }
            catch (Exception exception)
            {
                LogService.Error($"Stable library projection restore failed: {exception}");
                _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _notificationService.Show("已启动，但稳定资产事实恢复失败；可在库页刷新后重试。", NotificationLevel.Warning, TimeSpan.FromSeconds(8)));
            }
        }

        private async Task CheckGameDataIndexOnStartupAsync(CancellationToken cancellationToken)
        {
            if (!SettingsService.GetAutoCheckGameDataIndex() || !IsCheckDue(
                    SettingsService.GetLastGameDataIndexCheckUtc(),
                    SettingsService.GetGameDataIndexCheckIntervalHours())) return;

            var gameData = SettingsService.GetGameDataFolder();
            var paths = SettingsService.CreateStoragePaths();
            if (string.IsNullOrWhiteSpace(gameData) || !System.IO.Directory.Exists(gameData)) return;

            var task = _backgroundTasks.Enqueue(
                BackgroundTaskKind.BuildAssetIndex,
                "检查游戏资产索引",
                gameData,
                origin: "启动维护",
                userVisibleReason: "按设置的周期比较 Game Data 与现有资产索引。",
                suggestedAction: "若检测到过期，请在“设置与资产”中明确启动索引重建。");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                task.MarkRunning("正在检查索引指纹");
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(task.CancellationToken, cancellationToken);
                var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(paths)
                    .GetArchiveHashesJsonAsync(linkedCancellation.Token).ConfigureAwait(false);
                var index = CoreServices.CreateAssetArchiveIndexService(paths);
                var status = await index.GetIndexStatusAsync(gameData, archiveHashes, linkedCancellation.Token).ConfigureAwait(false);
                SettingsService.SetLastGameDataIndexCheckUtc(DateTime.UtcNow);
                if (status.IsCurrent)
                {
                    task.MarkCompleted();
                    return;
                }

                task.UpdateStage("索引缺失或已过期");
                task.SetSuggestedAction("请在“设置与资产”中明确启动“建立 / 重建资产索引”。");
                task.MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
                LogService.Error($"Startup Game Data index check failed: {exception}");
            }
        }

        public void Navigate(WorkspaceMode mode)
        {
            ClearTransientSelection();
            var (left, right) = mode switch
            {
                WorkspaceMode.ProfileLibrarySplit => (WorkspacePageType.Profile, (WorkspacePageType?)WorkspacePageType.Library),
                WorkspaceMode.ProfileOnly => (WorkspacePageType.Profile, null),
                WorkspaceMode.LibraryOnly => (WorkspacePageType.Library, null),
                WorkspaceMode.Settings => (WorkspacePageType.Settings, null),
                WorkspaceMode.ArmorRegistry => (WorkspacePageType.ArmorRegistry, null),
                _ => (WorkspacePageType.Home, null),
            };
            CommitNavigation(left, right, mode);
        }

        private void CommitNavigation(WorkspacePageType leftType, WorkspacePageType? rightType, WorkspaceMode mode)
        {
            var oldLeft = _leftPage;
            var oldRight = _rightPage;
            _leftPageType = leftType;
            _rightPageType = rightType ?? leftType;
            var nextLeft = CreatePage(leftType);
            var nextRight = rightType is { } type ? CreatePage(type) : null;
            _leftPage = nextLeft;
            _rightPage = nextRight;
            _currentMode = mode;

            DisposePageIfTransient(oldLeft, oldRight);
            DisposePageIfTransient(oldRight, nextLeft);

            OnPropertyChanged(nameof(LeftPageType));
            OnPropertyChanged(nameof(RightPageType));
            OnPropertyChanged(nameof(LeftPage));
            OnPropertyChanged(nameof(RightPage));
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CurrentMode));
            RaiseModeFlags();
            RaiseSlotFlags();
            RecordWorkspaceEntry();
        }

        // Esc 的第三级：按顺序返回上一次的页面组合。
        // 返回 false 表示队列为空，调用方保持原有行为（回首页）。
        // 语义是"不可反撤销"：这里只出栈、不再入栈；想回到更深的页面只能正向导航一次。
        public bool TryGoBack()
        {
            if (_navigationHistory.Count == 0) return false;
            var entry = _navigationHistory[^1];
            _navigationHistory.RemoveAt(_navigationHistory.Count - 1);

            // 整个回放过程都不记录：否则"回放失败 → 回首页"这一步会把自己压回队列，
            // 让 Esc 在两页之间来回弹。
            _isRestoringWorkspace = true;
            try
            {
                if (!TryRestoreWorkspace(entry)) Navigate(WorkspaceMode.Home);
            }
            finally
            {
                _isRestoringWorkspace = false;
            }

            return true;
        }

        // 回放一条历史组合。有长期缓存的页面（Profile / Library / ArmorRegistry）由 CreatePage
        // 内部直接复用实例，其余按类型重建。重建不了的组合返回 false。
        private bool TryRestoreWorkspace(WorkspaceHistoryEntry entry)
        {
            // 存档浏览器与 Archive 详情是一对：只有 OpenGameDataBrowser 会把两者配好，
            // CreatePage(GameDataArchiveDetails) 造出来的是无源宿主页。
            if (entry.LeftType == WorkspacePageType.GameDataBrowser
                && entry.HasRightPage
                && entry.RightType == WorkspacePageType.GameDataArchiveDetails)
            {
                OpenGameDataBrowser();
                return true;
            }

            if (!CanReopenPage(entry.LeftType)) return false;
            if (entry.HasRightPage && !CanReopenPage(entry.RightType)) return false;

            SelectedModId = entry.SelectedModId;
            _editingDecorationId = entry.EditingDecorationId;
            WorkspacePageType? rightType = entry.HasRightPage ? entry.RightType : null;
            CommitNavigation(entry.LeftType, rightType, entry.Mode);
            return true;
        }

        // 哪些页面类型无法从"页面类型 + Mod id"重建。
        private static bool CanReopenPage(WorkspacePageType pageType)
            => pageType switch
            {
                // CreatePage 对这两个直接抛异常：必须由调用方传入活实例。
                WorkspacePageType.CrossArmorPlan => false,
                WorkspacePageType.MaterialPackaging => false,
                // 必须与左侧浏览器配对，见 OpenGameDataBrowser。
                WorkspacePageType.GameDataArchiveDetails => false,
                _ => true,
            };

        // 每次导航收尾时调用：把"上一个组合"压入队列，并把当前组合记为最新。
        // 首次导航（构造函数里的 Navigate(Home)）只做播种，不入栈。
        private void RecordWorkspaceEntry()
        {
            var current = CaptureWorkspaceEntry();
            var previous = _lastWorkspaceEntry;
            _lastWorkspaceEntry = current;
            if (_isRestoringWorkspace || previous is null || previous.Value == current) return;
            if (_navigationHistory.Count >= NavigationHistoryLimit) _navigationHistory.RemoveAt(0);
            _navigationHistory.Add(previous.Value);
        }

        private WorkspaceHistoryEntry CaptureWorkspaceEntry()
            => new(_leftPageType, _rightPageType, _rightPage is not null, _currentMode, _selectedModId, _editingDecorationId);

        public void OpenMessagePanel()
        {
            IsMessagePanelOpen = true;
            IsMessagePreviewOpen = false;
            _messageCenter.ClearTransientToasts();
            _messageCenter.MarkCurrentMessagesRead();
            RefreshTaskHubState();
        }

        public void CloseMessagePanel()
        {
            if (!IsMessagePanelOpen) return;
            IsMessagePanelOpen = false;
            ShowMessagePreview();
        }

        private void ShowMessagePreview()
        {
            if (IsMessagePanelOpen) return;
            IsMessagePreviewOpen = HasTransientToast || ActiveTaskToasts.Count > 0;
        }

        public void OpenSinglePage(WorkspacePageType pageType)
        {
            LeftPageType = pageType;
            RightPageType = pageType;
            LeftPage = CreatePage(pageType);
            RightPage = null;
            UpdateModeFromSlots();
            RaiseSlotFlags();
        }

        public void OpenPage(WorkspacePageType pageType, bool leftSlot)
        {
            if (leftSlot)
            {
                LeftPageType = pageType;
                LeftPage = CreatePage(pageType);
            }
            else
            {
                RightPageType = pageType;
                RightPage = CreatePage(pageType);
            }

            UpdateModeFromSlots();
            RaiseSlotFlags();
        }

        public void OpenModDetails(string modId, bool preferRightSlot = true)
        {
            if (string.IsNullOrWhiteSpace(modId)) return;
            ClearTransientSelection();
            SelectedModId = modId;
            OpenSecondaryPage(WorkspacePageType.ModDetails, preferRightSlot ? LeftPage : RightPage);
        }

        public void BeginBottomBarNameEdit(string modId, string currentValue) => _bottomBar.BeginNameEdit(modId, currentValue);
        public void BeginBottomBarDescriptionEdit(string modId, string currentValue) => _bottomBar.BeginDescriptionEdit(modId, currentValue);
        public void BeginCreateLogicalMod()
        {
            ClearTransientSelection();
            _bottomBar.BeginCreateLogicalMod();
        }
        public void CancelBottomBarEdit() => _bottomBar.CancelEdit();
        public void ClearTransientSelection()
        {
            _bottomBar.CancelEdit();
            _selection.Clear();
        }

        public void OpenModDetailsFromPage(PageViewModel? sourcePage, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return;
            ClearTransientSelection();
            SelectedModId = modId;
            OpenSecondaryPage(WorkspacePageType.ModDetails, sourcePage);
        }

        public void OpenAdvancedModDetails(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return;
            ClearTransientSelection();
            SelectedModId = modId;
            OpenSinglePage(WorkspacePageType.AdvancedModDetails);
        }

        public void OpenGameDataBrowser()
        {
            LeftPageType = WorkspacePageType.GameDataBrowser;
            RightPageType = WorkspacePageType.GameDataArchiveDetails;
            LeftPage = new GameDataBrowserPageViewModel(_libraryService, _profileService, _derivedState.InformationCenter);
            RightPage = new GameDataArchiveDetailsHostPageViewModel((GameDataBrowserPageViewModel)LeftPage);
            UpdateModeFromSlots();
            RaiseSlotFlags();
        }

        public void OpenArmorRegistry()
        {
            ClearTransientSelection();
            OpenSinglePage(WorkspacePageType.ArmorRegistry);
        }

        private void ApplyArmorRegistrySelection(ArmorRegistrySelectionAction action)
        {
            var page = LeftPage as ArmorRegistryPageViewModel
                ?? RightPage as ArmorRegistryPageViewModel;
            if (page is null || !_selection.HasSelection) return;
            var ids = _selection.SelectedIds.ToArray();
            switch (action)
            {
                case ArmorRegistrySelectionAction.Promote:
                    page.PromoteAvailableAsProviders(ids);
                    break;
                case ArmorRegistrySelectionAction.ReleaseProviders:
                    page.ReleaseProviderSelection(ids);
                    break;
                case ArmorRegistrySelectionAction.ReleaseTargets:
                    page.ReleaseTargetSelection(ids);
                    break;
            }
            _selection.Clear();
        }

        public void OpenCrossArmorPlan(HD2ModManager.Views.CrossArmorTransferPlanWindowViewModel plan)
        {
            LeftPageType = WorkspacePageType.CrossArmorPlan;
            RightPageType = WorkspacePageType.CrossArmorPlan;
            LeftPage = plan;
            plan.AttachCandidateOutput(new CrossArmorCandidateOutputPageViewModel(plan, _notificationService, _backgroundTasks));
            RightPage = null;
            UpdateModeFromSlots();
            RaiseSlotFlags();
        }

        public void OpenMaterialPackaging(ModDetailsPageViewModel sourcePage, MaterialPackagingPageViewModel packagingPage)
        {
            DismissToolBottomBars();
            _materialPackagingBottomBarOperation = packagingPage;

            RegisterMaterialPackagingRow("material-packaging-output", MaterialPackagingBottomBarRowKind.Output, insertAtRow: 1);
            RegisterMaterialPackagingRow("material-packaging-options", MaterialPackagingBottomBarRowKind.Options);
            if (packagingPage.RequiresCandidate)
                RegisterMaterialPackagingRow("material-packaging-candidates", MaterialPackagingBottomBarRowKind.Candidates);
        }

        private async Task DeployActiveProfileOnStartupAsync(CancellationToken cancellationToken)
        {
            if (_profileService.ActiveProfile is null) return;

            try
            {
                LogService.Info("启动部署：活动配置已加载，立即部署并跳过变更缓冲等待。");
                await _libraryService.FlushPendingDecorationRebuildsAsync(cancellationToken).ConfigureAwait(false);
                var status = await _deploymentCoordinator.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (status.Stage != ProfileDeploymentStage.Completed)
                    LogService.Error($"启动部署未完成：{status.Message ?? status.Stage.ToString()}。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LogService.Error($"启动部署失败：{exception}");
            }
        }

        public void OpenDecorationPlan(string sourceModId)
        {
            if (string.IsNullOrWhiteSpace(sourceModId)) return;
            _editingDecorationId = null;
            ClearTransientSelection();
            SelectedModId = sourceModId;
            OpenSinglePage(WorkspacePageType.DecorationPlan);
        }

        public void OpenDecorationEditor(string decorationId)
        {
            if (string.IsNullOrWhiteSpace(decorationId)) return;
            _editingDecorationId = decorationId;
            ClearTransientSelection();
            SelectedModId = decorationId;
            OpenSinglePage(WorkspacePageType.DecorationPlan);
        }

        public void OpenBatchDecorationPlan(string hostModId)
        {
            if (string.IsNullOrWhiteSpace(hostModId)) return;
            ClearTransientSelection();
            SelectedModId = hostModId;
            OpenSinglePage(WorkspacePageType.BatchDecorationPlan);
        }

        public void OpenHelmetToArmorMergePlan(string sourceModId)
        {
            if (string.IsNullOrWhiteSpace(sourceModId)) return;
            DismissToolBottomBars();
            ClearTransientSelection();
            SelectedModId = sourceModId;
            OpenSinglePage(WorkspacePageType.HelmetToArmorMergePlan);
        }

        public void OpenPatchResourceRepackaging(string sourceModId)
        {
            if (string.IsNullOrWhiteSpace(sourceModId)) return;
            DismissToolBottomBars();
            _editingDecorationId = null;
            ClearTransientSelection();
            SelectedModId = sourceModId;
            OpenSinglePage(WorkspacePageType.PatchResourceRepackaging);
        }

        public void DismissMaterialPackagingBottomBar() => ClearMaterialPackagingBottomBar();

        public void OpenSameKeyRebuild(SameKeyRebuildBottomBarViewModel operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ClearMaterialPackagingBottomBar();
            ClearSameKeyRebuildBottomBar();
            _sameKeyRebuildBottomBarOperation = operation;
            RegisterSameKeyRebuildRow("same-key-rebuild-output", SameKeyRebuildBottomBarRowKind.Output, insertAtRow: 1);
            RegisterSameKeyRebuildRow("same-key-rebuild-options", SameKeyRebuildBottomBarRowKind.Options);
        }

        public void DismissToolBottomBars()
        {
            ClearMaterialPackagingBottomBar();
            ClearSameKeyRebuildBottomBar();
        }

        public void OpenCustomModelConversion(CustomModelConversionBottomBarViewModel operation)
        {
            DismissToolBottomBars();
            foreach (var output in new[] { true, false })
                _sameKeyRebuildBottomBarRegistrations.Add(_bottomBar.RegisterSurfaceSource(new BottomBarRegistrationRequest(
                    "custom-model-" + (output ? "output" : "options"),
                    [new BottomBarRowDefinition("main", new CustomModelConversionBottomBarRow(operation, output))],
                    output ? 1 : null)));
        }

        public async Task FlushArmorRegistryWorkspaceAsync()
        {
            var page = CreateArmorRegistryPage();
            await page.InitializeAsync();
            await page.BeginExternalWorkspaceEditAsync();
        }

        public void EndArmorRegistryWorkspaceEdit() => _armorRegistryWorkspacePage?.EndExternalWorkspaceEdit();

        public Task SaveArmorRegistryWorkspaceBeforeCloseAsync()
            => _armorRegistryWorkspacePage?.FlushWorkspaceAsync() ?? Task.CompletedTask;

        public async Task ReloadArmorRegistryWorkspaceAsync()
        {
            if (_armorRegistryWorkspacePage is not null)
                await _armorRegistryWorkspacePage.ReloadWorkspaceAsync();
        }

        private void RegisterMaterialPackagingRow(string sourceId, MaterialPackagingBottomBarRowKind kind, int? insertAtRow = null)
        {
            if (_materialPackagingBottomBarOperation is null) return;
            var row = new MaterialPackagingBottomBarRowViewModel(_materialPackagingBottomBarOperation, kind);
            _materialPackagingBottomBarRegistrations.Add(_bottomBar.RegisterSurfaceSource(new BottomBarRegistrationRequest(
                sourceId,
                [new BottomBarRowDefinition("main", row)],
                insertAtRow)));
        }

        private void ClearMaterialPackagingBottomBar()
        {
            foreach (var registration in _materialPackagingBottomBarRegistrations) registration.Dispose();
            _materialPackagingBottomBarRegistrations.Clear();
            _materialPackagingBottomBarOperation = null;
        }

        private void RegisterSameKeyRebuildRow(string sourceId, SameKeyRebuildBottomBarRowKind kind, int? insertAtRow = null)
        {
            if (_sameKeyRebuildBottomBarOperation is null) return;
            var row = new SameKeyRebuildBottomBarRowViewModel(_sameKeyRebuildBottomBarOperation, kind);
            _sameKeyRebuildBottomBarRegistrations.Add(_bottomBar.RegisterSurfaceSource(new BottomBarRegistrationRequest(
                sourceId,
                [new BottomBarRowDefinition("main", row)],
                insertAtRow)));
        }

        private void ClearSameKeyRebuildBottomBar()
        {
            foreach (var registration in _sameKeyRebuildBottomBarRegistrations) registration.Dispose();
            _sameKeyRebuildBottomBarRegistrations.Clear();
            _sameKeyRebuildBottomBarOperation = null;
        }

        private void OpenSecondaryPage(WorkspacePageType pageType, PageViewModel? sourcePage)
        {
            var targetPage = CreatePage(pageType);
            if (targetPage.RequiresSingleSlot)
            {
                OpenSinglePage(pageType);
                return;
            }

            if (!IsSplitView)
            {
                RightPageType = pageType;
                RightPage = targetPage;
            }
            else if (ReferenceEquals(sourcePage, RightPage))
            {
                LeftPageType = pageType;
                LeftPage = targetPage;
            }
            else
            {
                RightPageType = pageType;
                RightPage = targetPage;
            }

            UpdateModeFromSlots();
            RaiseSlotFlags();
        }

        public async System.Threading.Tasks.Task ProcessImportQueueAsync(string[] paths)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "Import.QueueRequest",
                $"paths={paths.Length}",
                threshold: 6);
            _importQueue.Enqueue(paths);
            ShowQueueSummary();

            using var queuePerf = UiPerformanceDiagnostics.Measure(
                "Import.ProcessQueue",
                thresholdMilliseconds: 150,
                details: () => $"paths={paths.Length}");
            await _importProcessGate.WaitAsync();
            try
            {
                var libraryChanged = false;
                var importedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var affectedNodeIds = new HashSet<ModNodeId>();
                var importedSources = new List<string>();
                ImportTaskItem? item;
                while ((item = _importQueue.DequeueNextQueued()) != null)
                {
                    var task = _backgroundTasks.Enqueue(
                        BackgroundTaskKind.Import,
                        $"导入 {System.IO.Path.GetFileName(item.Path)}",
                        item.Path,
                        presentation: BackgroundTaskPresentation.Foreground);
                    var import = new ImportService(_libraryService, onInfo: null, onError: err => _importQueue.MarkFailed(item, err), informationCenter: _derivedState.InformationCenter);
                    using var itemPerf = UiPerformanceDiagnostics.Measure(
                        "Import.ProcessItem",
                        thresholdMilliseconds: 100,
                        details: () => $"source={System.IO.Path.GetFileName(item.Path)}; created={import.LastAffectedNodeIds.Count}");
                    try
                    {
                        task.MarkRunning("正在解压缩");
                        // Commit each source immediately, but defer expensive derived-data
                        // production until the whole queue has been committed.
                        var created = await import.ImportPathAsync(
                            item.Path,
                            task.CancellationToken,
                            notifyLibraryChanged: false,
                            refreshDerivedData: false);
                        foreach (var nodeId in import.LastAffectedNodeIds)
                            affectedNodeIds.Add(nodeId);
                        libraryChanged = true;
                        task.UpdateStage("模组库已更新");
                        LogService.Info($"快速导入完成：来源={item.Path}，新增 Mod 数={created.Count}。导入仅写入模组库；除非后续显式加入活动配置，否则不会部署。");
						if (import.LastImportHasOptionPathConflicts)
							_notificationService.Show("Mod 导入成功，但部分选项共享同一 Patch 目录；这些共享内容默认未选择，其余选项已按通常规则初始化。", NotificationLevel.Warning, TimeSpan.FromSeconds(8));
                        _importQueue.MarkDone(item);
                        task.MarkCompleted();
                        if (created.Count > 0)
                        {
                            importedModIds.UnionWith(created);
                            importedSources.Add(item.Path);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _importQueue.MarkFailed(item, "任务已取消");
                        task.MarkCanceled();
                    }
                    catch (Exception ex)
                    {
                        _importQueue.MarkFailed(item, ex.Message);
                        task.MarkFailed(ex.Message);
                        System.Windows.MessageBox.Show($"导入失败: {item.Path}\n{ex.Message}", "Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    }
                }

                if (libraryChanged)
                {
                    // ImportPathAsync already commits the copied filesystem nodes and
                    // the matching snapshot. Synchronizing the whole library here would
                    // rediscover the same batch and publish a second content change.
                    if (affectedNodeIds.Count > 0)
                    {
                        using (UiPerformanceDiagnostics.Measure(
                            "Import.QueueRefreshDerived",
                            thresholdMilliseconds: 100,
                            details: () => $"affected={affectedNodeIds.Count}"))
                        {
                            await _libraryService.RefreshCommittedContentAsync(
                                affectedNodeIds,
                                ModContentChangeKind.Changed,
                                alreadyInvalidated: true,
                                cancellationToken: _lifetimeCancellation.Token).ConfigureAwait(false);
                        }
                    }
                    _libraryService.NotifyImportCompleted();
                    if (SettingsService.GetAutoImportToActiveProfile() && importedModIds.Count > 0 && _profileService.ActiveProfile is not null)
                    {
                        using (UiPerformanceDiagnostics.Measure(
                            "Import.AutoProfileAndDeploy",
                            thresholdMilliseconds: 100,
                            details: () => $"imported={importedModIds.Count}"))
                        {
                            await _profileService.ReloadFromLibraryAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                            var deployable = importedModIds
                                .Where(id => _libraryService.Get(id)?.Capabilities.CanJoinProfile == true)
                                .ToArray();
                            if (deployable.Length > 0)
                            {
                                var added = await _profileService.AddModsToActiveAsync(deployable, _lifetimeCancellation.Token).ConfigureAwait(false);
                                if (added > 0)
                                {
                                    LogService.Info($"导入后自动加入活动配置：新增={added}，选项/装饰未加入配置。即将统一部署一次。");
                                    await _libraryService.FlushPendingDecorationRebuildsAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                                    await _deploymentCoordinator.FlushAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    QueueCurrentPageRefresh("导入后库同步完成");
                }
            }
            finally
            {
                _importProcessGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (_armorRegistryWorkspacePage is not null)
                    await _armorRegistryWorkspacePage.FlushWorkspaceAsync();
            }
            catch (Exception exception) { LogService.Error($"退出前护甲组件保存失败：{exception}"); }
            try
            {
                // Window close is a consistency boundary too: do not leave a successful UI
                // toggle waiting only in the in-memory debounce overlay.
                await _libraryService.FlushPendingDecorationRebuildsAsync().ConfigureAwait(false);
            }
            catch (Exception exception) { LogService.Error($"退出前装饰合并失败：{exception}"); }
            _lifetimeCancellation.Cancel();
            _deploymentCoordinator.StatusChanged -= OnDeploymentStatusChanged;
            _informationCenter.ProductionStarted -= OnInformationProductionStarted;
            _informationCenter.DiagnosticRecorded -= OnInformationDiagnosticRecorded;
			StopDeploymentBufferTimer();
			_deploymentBufferTimer = null;
            DismissToolBottomBars();
            DisposeCurrentPages();
            _importProcessGate.Dispose();
            _lifetimeCancellation.Dispose();
            await _derivedState.DisposeAsync().ConfigureAwait(false);
            await _informationCenter.DisposeAsync().ConfigureAwait(false);
			await _informationReader.DisposeAsync().ConfigureAwait(false);
        }

        private void DisposeCurrentPages()
        {
            var left = _leftPage;
            var right = _rightPage;
            _leftPage = null;
            _rightPage = null;
            DisposePageIfTransient(left, null);
            DisposePageIfTransient(right, left);
            (_profileWorkspacePage as IDisposable)?.Dispose();
            if (!ReferenceEquals(_libraryWorkspacePage, _profileWorkspacePage)) (_libraryWorkspacePage as IDisposable)?.Dispose();
            (_armorRegistryWorkspacePage as IDisposable)?.Dispose();
            _profileWorkspacePage = null;
            _libraryWorkspacePage = null;
            _armorRegistryWorkspacePage = null;
        }

        private void DisposePageIfTransient(PageViewModel? page, PageViewModel? retainedElsewhere)
        {
            if (page is null
                || ReferenceEquals(page, retainedElsewhere)
                || ReferenceEquals(page, _profileWorkspacePage)
                || ReferenceEquals(page, _libraryWorkspacePage)
                || ReferenceEquals(page, _armorRegistryWorkspacePage)) return;
            (page as IDisposable)?.Dispose();
        }

        private async Task RunStartupChecksAsync(string configDir, CancellationToken cancellationToken)
        {
			var startupClock = Stopwatch.StartNew();
            try
            {
                // SynchronizeAsync 包含同步目录扫描；整个调用必须在线程池执行。
                await Task.Run(() => _libraryService.SynchronizeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
				LogStartupCheckpoint("库目录同步", startupClock);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(SettingsService.GetGameDataFolder()))
                {
                    var detected = SettingsService.TryDetectAndSetGameDataFolder();
                    if (!string.IsNullOrWhiteSpace(detected))
                    {
                        _notificationService.Show($"已检测到游戏数据目录: {detected}", NotificationLevel.Info, TimeSpan.FromSeconds(4));
                    }
                }

                await DeployActiveProfileOnStartupAsync(cancellationToken).ConfigureAwait(false);

                if (SettingsService.GetAutoUpdateAssetMetadata())
                {
                    _ = UpdateAssetMetadataOnStartupAsync(cancellationToken);
                }

                // Full-library projection can retain very large asset inventories. It is only
                // needed by the library page, so defer it until that page is actually opened.
                LogStartupCheckpoint("启动维护就绪（全库派生已延后）", startupClock);
            }
            catch (OperationCanceledException)
            {
                LogService.Info("启动模组库同步已取消。");
            }
            catch (Exception exception)
            {
                LogService.Error($"启动模组库同步或完整性检查失败：{exception}");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    _notificationService.Show("已启动，但模组库启动同步或完整性检查失败；可在库页刷新后重试。", NotificationLevel.Warning, TimeSpan.FromSeconds(8))));
            }
        }

        private async Task UpdateAssetMetadataOnStartupAsync(CancellationToken cancellationToken)
        {
            if (!IsCheckDue(
                    SettingsService.GetLastAssetMetadataCheckUtc(),
                    SettingsService.GetAssetMetadataCheckIntervalHours())) return;

            var task = _backgroundTasks.Enqueue(
                BackgroundTaskKind.UpdateAssetMetadata,
                "更新资产元数据",
                "启动检查",
                origin: "启动维护",
                userVisibleReason: "已达到在线资产自动检查周期。",
                suggestedAction: "失败时请检查在线资产索引源与网络连接。");
            try
            {
                task.MarkRunning("正在同步资产元数据");
                var paths = SettingsService.CreateStoragePaths();
                var sync = CoreServices.CreateAssetMetadataSyncService(paths);
                var result = await sync.SyncAsync(SettingsService.GetAssetMetadataRepository(), cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    SettingsService.SetLastAssetMetadataCheckUtc(DateTime.UtcNow);
                    await _libraryService.RefreshAssetSummariesAsync(cancellationToken).ConfigureAwait(false);
                    task.MarkCompleted();
                    _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _notificationService.Show("资产信息已自动更新", NotificationLevel.Info, TimeSpan.FromSeconds(4)));
                }
                else
                {
                    task.MarkFailed(result.ErrorMessage ?? "未知错误");
                    RefreshOnUiThread(() => _notificationService.Show($"资产信息自动更新失败：{result.ErrorMessage}", NotificationLevel.Warning, TimeSpan.FromSeconds(6)));
                }
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
                LogService.Info("启动资产元数据更新已取消。");
            }
            catch (Exception ex)
            {
                task.MarkFailed(ex.Message);
                RefreshOnUiThread(() => _notificationService.Show($"资产信息自动更新失败：{ex.Message}", NotificationLevel.Warning, TimeSpan.FromSeconds(6)));
            }
        }

        private static bool IsCheckDue(DateTime? lastCheckUtc, int intervalHours)
            => lastCheckUtc is null || DateTime.UtcNow - lastCheckUtc.Value >= TimeSpan.FromHours(intervalHours);

        private void QueueActiveProfileDeployment()
        {
            if (_profileService.ActiveProfile is null)
            {
                _notificationService.Show("当前没有活动配置。", NotificationLevel.Info, TimeSpan.FromSeconds(4));
                return;
            }
            _ = FlushActiveProfileDeploymentAsync();
            _notificationService.Show("已请求立即部署最新活动配置。", NotificationLevel.Info, TimeSpan.FromSeconds(4));
        }

		private static void LogStartupCheckpoint(string stage, Stopwatch clock)
			=> LogService.Info($"启动性能：阶段={stage}；耗时={clock.ElapsedMilliseconds}ms；托管堆={GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024}MB；工作集={Environment.WorkingSet / 1024 / 1024}MB。");

        private async Task FlushActiveProfileDeploymentAsync()
        {
            try
            {
                await _libraryService.FlushPendingDecorationRebuildsAsync(_lifetimeCancellation.Token);
                var status = await _deploymentCoordinator.FlushAsync(_lifetimeCancellation.Token);
                if (status.Stage == ProfileDeploymentStage.Completed) return;
                _notificationService.Show(status.Message ?? "活动配置部署失败。", NotificationLevel.Error, TimeSpan.FromSeconds(8));
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _notificationService.Show($"无法完成部署：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
            }
        }

        private void OnDeploymentStatusChanged(object? sender, ProfileDeploymentStatus status)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(() => OnDeploymentStatusChanged(sender, status));
                return;
            }

            switch (status.Stage)
            {
				case ProfileDeploymentStage.WaitingForStableState:
					if (_deploymentTask?.IsActive != true)
						_deploymentTask = _backgroundTasks.Enqueue(
							BackgroundTaskKind.Deployment,
							"部署活动配置",
							status.Message,
							canCancel: false,
							presentation: BackgroundTaskPresentation.Foreground);
					_deploymentTask.MarkRunning("等待配置变更稳定");
					UpdateDeploymentBufferProgress(status);
					StartDeploymentBufferTimer();
					break;
                case ProfileDeploymentStage.Deploying:
					StopDeploymentBufferTimer();
                    if (_deploymentTask?.IsActive != true)
                        _deploymentTask = _backgroundTasks.Enqueue(
                            BackgroundTaskKind.Deployment,
                            "部署活动配置",
                            status.Message,
                            presentation: BackgroundTaskPresentation.Foreground);
                    _deploymentTask.MarkRunning(status.Message ?? "正在部署");
					_deploymentTask.UpdateProgress(null);
                    break;
                case ProfileDeploymentStage.Deactivating:
					StopDeploymentBufferTimer();
                    if (_deploymentTask?.IsActive == true) _deploymentTask.Cancel();
                    _deploymentTask = _backgroundTasks.Enqueue(
                        BackgroundTaskKind.DeactivateProfile,
                        "停用活动配置",
                        status.Message,
                        presentation: BackgroundTaskPresentation.Foreground);
                    _deploymentTask.MarkRunning(status.Message ?? "正在清理 Patch");
                    break;
                case ProfileDeploymentStage.Completed:
					StopDeploymentBufferTimer();
                    _deploymentTask?.MarkCompleted();
                    _notificationService.Supersede("deployment-result");
                    _notificationService.Show(status.Message ?? "活动配置部署完成。", NotificationLevel.Info, TimeSpan.FromSeconds(6), "部署", "deployment-result");
                    if (status.ApplyResult is { } success) _applyStatus.Record(new ApplyExecutionStatus(success.Success, status.Message ?? "部署完成", success));
                    _derivedState.MarkDeploymentDirty();
                    RefreshCurrentPage();
                    break;
                case ProfileDeploymentStage.Failed:
					StopDeploymentBufferTimer();
                    _deploymentTask?.MarkFailed(status.Message ?? "部署失败");
                    if (status.ApplyResult is { } failure) _applyStatus.Record(new ApplyExecutionStatus(false, status.Message ?? "部署失败", failure));
                    var failureMessage = status.Message ?? "活动配置部署失败。";
                    if (status.ApplyResult is { Issues.Count: > 0 } failedResult)
                    {
                        if (failedResult.Issues.Any(issue => string.Equals(issue.Code, "LuaOpaqueMergeDisabled", StringComparison.Ordinal)))
                        {
                            _notificationService.Supersede("deployment-result");
                            _notificationService.Show("当前配置包含需要合并的不安全 Lua，部署已停止。请在设置页的“通用”中打开“允许不安全 Lua 合并部署”，然后重新部署。", NotificationLevel.Warning, TimeSpan.FromSeconds(10), "部署", "deployment-result");
                        }
                        var details = failedResult.Issues.Take(3).Select(issue => $"{issue.Code}: {issue.Message}");
                        failureMessage = $"{failureMessage}\n{string.Join("\n", details)}";
                    }
                    // A foreground deployment task already owns the persistent error toast.
                    // Keep this fallback only for failures reported before a task could be created.
                    if (_deploymentTask is null)
                    {
                        _notificationService.Supersede("deployment-result");
                        _notificationService.Show(failureMessage, NotificationLevel.Error, TimeSpan.FromSeconds(12), "部署", "deployment-result");
                    }
                    RefreshCurrentPage();
                    break;
                case ProfileDeploymentStage.Canceled:
					StopDeploymentBufferTimer();
                    _deploymentTask?.MarkCanceled();
                    break;
            }
        }

        private void StartGame()
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/553850")
            {
                UseShellExecute = true,
            });
        }

        private async void BrowseAndImport()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的 Mod 压缩包",
                Filter = "Mod Archives|*.zip;*.rar;*.7z|All Files|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog() == true)
            {
                await ProcessImportQueueAsync(dialog.FileNames);
            }
        }

        public void RefreshCurrentPage()
        {
            RefreshPage(LeftPage);
            if (IsSplitView) RefreshPage(RightPage);
        }

        private void QueueCurrentPageRefresh(string reason)
        {
            if (Interlocked.Exchange(ref _pageRefreshQueued, 1) != 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                Interlocked.Exchange(ref _pageRefreshQueued, 0);
                RefreshCurrentPage();
                return;
            }

            _ = dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(80);
                Interlocked.Exchange(ref _pageRefreshQueued, 0);
                RefreshCurrentPage();
            });
        }

        private void RefreshPage(PageViewModel? page)
        {
            switch (page)
            {
                case HomePageViewModel home:
                    home.Refresh();
                    break;
                case ProfilePageViewModel profile:
                    profile.RefreshFromShell();
                    break;
                case LibraryPageViewModel library:
                    library.RefreshFromShell();
                    break;
                case ModDetailsPageViewModel details:
                    details.Refresh();
                    break;
            }
        }

        private void ShowQueueSummary()
        {
            try
            {
                var total = _importQueue.Tasks.Count;
                var imported = _importQueue.CountDone;
                var pending = _importQueue.CountQueued + _importQueue.CountRunning;
                _notificationService.Show($"导入队列 - 总数：{total}，已导入：{imported}，待处理：{pending}");
            }
            catch { }
        }

        private void QueueLibrarySnapshotChanged()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _ = Task.Run(() => HandleLibrarySnapshotChangedAsync(_lifetimeCancellation.Token));
        }

        private async Task HandleLibrarySnapshotChangedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _profileService.ReloadFromLibraryAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
                RefreshOnUiThread(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    CloseDeletedModDetails();
                    RefreshNonListPagesAfterLibrarySnapshot();
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LogService.Error($"库快照变更后的配置重载失败：{exception}");
            }
        }

        private void ToggleMessagePanel()
        {
            if (IsMessagePanelOpen) CloseMessagePanel(); else OpenMessagePanel();
        }

        private static void CopyMessage(object? value)
        {
            if (value is MessageCenterItem item && !string.IsNullOrWhiteSpace(item.CopyText)) System.Windows.Clipboard.SetText(item.CopyText);
        }

        private void RefreshNonListPagesAfterLibrarySnapshot()
        {
            foreach (var page in new[] { LeftPage, RightPage }.Distinct())
            {
                switch (page)
                {
                    case HomePageViewModel home:
                        home.Refresh();
                        break;
                    case ModDetailsPageViewModel details:
                        details.Refresh();
                        break;
                }
            }
        }

		private async Task LaunchGameAsync()
		{
			if (_profileService.ActiveProfile is not null)
			{
				ProfileDeploymentStatus status;
                    try
                    {
                        await _libraryService.FlushPendingDecorationRebuildsAsync(_lifetimeCancellation.Token);
                        status = await _deploymentCoordinator.FlushAsync(_lifetimeCancellation.Token);
				}
				catch (Exception exception)
				{
					_notificationService.Show($"无法在启动前完成部署：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
					return;
				}

				if (status.Stage != ProfileDeploymentStage.Completed)
				{
					_notificationService.Show(status.Message ?? "启动前部署失败，未启动游戏。", NotificationLevel.Error, TimeSpan.FromSeconds(8));
					return;
				}
			}

			StartGame();
		}

		private void StartDeploymentBufferTimer()
		{
			_deploymentBufferTimer ??= new System.Windows.Threading.DispatcherTimer(
				TimeSpan.FromMilliseconds(100),
				System.Windows.Threading.DispatcherPriority.Background,
				(_, _) => UpdateDeploymentBufferProgress(_deploymentCoordinator.Status),
				System.Windows.Application.Current?.Dispatcher);
			if (!_deploymentBufferTimer.IsEnabled) _deploymentBufferTimer.Start();
		}

		private void StopDeploymentBufferTimer()
		{
			if (_deploymentBufferTimer?.IsEnabled == true) _deploymentBufferTimer.Stop();
		}

		private void UpdateDeploymentBufferProgress(ProfileDeploymentStatus status)
		{
			if (status.Stage != ProfileDeploymentStage.WaitingForStableState
				|| status.BufferStartedUtc is not { } startedUtc
				|| status.BufferEndsUtc is not { } endsUtc)
			{
				StopDeploymentBufferTimer();
				return;
			}

			var total = endsUtc - startedUtc;
			var elapsed = DateTimeOffset.UtcNow - startedUtc;
			var progress = total <= TimeSpan.Zero ? 1d : Math.Clamp(elapsed.TotalMilliseconds / total.TotalMilliseconds, 0d, 1d);
			var remaining = Math.Max(0, (int)Math.Ceiling((endsUtc - DateTimeOffset.UtcNow).TotalSeconds));
			_deploymentTask?.UpdateStage($"等待配置稳定（{remaining} 秒）");
			_deploymentTask?.UpdateProgress(progress);
		}

        private void DismissMessage(object? value)
        {
            if (value is not MessageCenterItem item) return;
            _messageCenter.Acknowledge(item);
        }

        private void OnInformationProductionStarted(object? sender, ModInformationProductionStarted value)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "InformationCenter.Production",
                $"kind={value.Kind}; source={value.Source}",
                threshold: 8);
            LogService.Info($"信息中心开始生产：类型={value.Kind}，节点={value.NodeId?.Value.ToString("N") ?? "全局"}，generation={value.Generation ?? "空"}，来源={value.Source}，operation={value.OperationKey}。缓存未命中或要求刷新。");
            RefreshOnUiThread(() =>
            {
                if (_informationTasks.ContainsKey(value.OperationKey)) return;
                var task = _backgroundTasks.Enqueue(BackgroundTaskKind.InformationCenter,
                    $"生成{GetInformationKindText(value.Kind)}信息{(value.NodeId is null ? string.Empty : $"（Mod {value.NodeId}）")}",
                    $"generation={value.Generation ?? "自动"}", value.Source);
                task.MarkRunning($"正在生成{GetInformationKindText(value.Kind)}");
                _informationTasks[value.OperationKey] = task;
            });
        }

        private void OnInformationDiagnosticRecorded(object? sender, ModInformationDiagnostic value)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "InformationCenter.Diagnostic",
                $"kind={value.Kind}; status={value.Status}",
                threshold: 8);
            var issues = value.Issues.Count == 0
                ? string.Empty
                : $"，问题={string.Join(" | ", value.Issues.Take(5).Select(issue => $"{issue.Code}:{issue.Message}"))}";
            LogService.Info($"信息中心诊断：类型={value.Kind}，节点={value.NodeId?.Value.ToString("N") ?? "全局"}，generation={value.Generation ?? "空"}，状态={value.Status}，缓存命中={value.CacheHit}，合并请求={value.WasCoalesced}，耗时={(value.CompletedUtc - value.StartedUtc).TotalMilliseconds:F0}ms，operation={value.OperationKey}{issues}。");
            RefreshOnUiThread(() =>
            {
                if (value.OperationKey is null || !_informationTasks.TryGetValue(value.OperationKey, out var task)) return;
                if (value.Status == ModInformationStatus.Failed)
                    task.MarkFailed(string.Join("；", value.Issues.Select(issue => issue.Message)));
                else
                {
                    task.MarkCompleted();
                    if (value.Status is ModInformationStatus.Stale or ModInformationStatus.Unavailable)
                        task.UpdateStage(value.Status == ModInformationStatus.Stale ? "已完成（结果过期，已使用旧数据）" : "已完成（信息不可用）");
                }
                // 诊断是该 OperationKey 的唯一终态；历史任务仍由 BackgroundTaskService 保留。
                _informationTasks.Remove(value.OperationKey);
            });
        }

        private static string GetInformationKindText(ModInformationKind kind) => kind switch
        {
            ModInformationKind.AssetInventory => "资产",
            ModInformationKind.ReferenceGraph => "引用",
            ModInformationKind.AdvancedUnitAnalysis => "高级分析",
            ModInformationKind.Thumbnail => "缩略图",
            ModInformationKind.UnitVersion => "版本",
            ModInformationKind.MaintenanceAnalysis => "维护分析",
            _ => "文件",
        };

        private static void CancelTask(object? parameter)
        {
            if (parameter is BackgroundTaskItem task) task.Cancel();
        }

        private static async Task RetryTaskAsync(object? parameter)
        {
            if (parameter is not BackgroundTaskItem { Retry: { } retry }) return;
            await retry();
        }

        private static void OpenTaskReport(object? parameter) => OpenPath((parameter as BackgroundTaskItem)?.ReportPath);

        private static void OpenTaskOutput(object? parameter) => OpenPath((parameter as BackgroundTaskItem)?.OutputDirectory);

        private static void OpenPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void RefreshTaskHubState()
        {
            using var perf = UiPerformanceDiagnostics.Measure(
                "Shell.RefreshTaskHub",
                thresholdMilliseconds: 6,
                details: () => $"tasks={_backgroundTasks.Tasks.Count}; unread={MessageUnreadCount}; messagePanel={IsMessagePanelOpen}; preview={IsMessagePreviewOpen}");
            OnPropertyChanged(nameof(ActiveMessageTasks));
            OnPropertyChanged(nameof(MessageItems));
            OnPropertyChanged(nameof(ActiveTaskToasts));
                OnPropertyChanged(nameof(HasActiveTaskToasts));
            OnPropertyChanged(nameof(HasActiveTaskToasts));
            OnPropertyChanged(nameof(CurrentTransientToast));
            OnPropertyChanged(nameof(HasTransientToast));
            OnPropertyChanged(nameof(ActiveTaskCount));
            OnPropertyChanged(nameof(MessageUnreadCount));
            OnPropertyChanged(nameof(HasUnreadTaskHubEvents));
            OnPropertyChanged(nameof(MessageBadgeText));
            CancelTaskCommand.RaiseCanExecuteChanged();
            RetryTaskCommand.RaiseCanExecuteChanged();
            DismissMessageCommand.RaiseCanExecuteChanged();
            OpenTaskReportCommand.RaiseCanExecuteChanged();
            OpenTaskOutputCommand.RaiseCanExecuteChanged();
        }

        private void CloseDeletedModDetails()
        {
            var selectedModWasDeleted = !string.IsNullOrWhiteSpace(SelectedModId)
                && _libraryService.Get(SelectedModId) is null;
            if (selectedModWasDeleted) SelectedModId = null;

            var leftDetailsWasDeleted = LeftPage is ModDetailsPageViewModel leftDetails
                && _libraryService.Get(leftDetails.ModId) is null;
            var rightDetailsWasDeleted = RightPage is ModDetailsPageViewModel rightDetails
                && _libraryService.Get(rightDetails.ModId) is null;
            if (!leftDetailsWasDeleted && !rightDetailsWasDeleted) return;

            if (rightDetailsWasDeleted)
            {
                RightPageType = LeftPageType;
                RightPage = null;
                UpdateModeFromSlots();
                RaiseSlotFlags();
                return;
            }

            if (RightPage is not null)
            {
                OpenSinglePage(RightPageType);
                return;
            }

            OpenSinglePage(WorkspacePageType.Library);
        }

        private async Task ExecuteSelectionPrimaryAsync()
        {
            if (!_selection.HasSelection) return;
            if (string.Equals(_selection.Scope, "Library", StringComparison.OrdinalIgnoreCase))
            {
                var ids = _selection.SelectedIds.Where(id => _libraryService.Get(id)?.Capabilities.CanJoinProfile == true).ToList();
                if (ids.Count == 0)
                {
                    _selection.Clear();
                    return;
                }
                var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "加入配置", $"{ids.Count} 个 Mod");
                task.MarkRunning("正在写入配置");
                try
                {
                    var added = await _profileService.AddModsToSelectedAsync(ids, task.CancellationToken);
                    task.MarkCompleted();
                    _notificationService.Show($"已加入正在编辑的配置：{added} 个 Mod");
                }
                catch (OperationCanceledException)
                {
                    task.MarkCanceled();
                    return;
                }
                catch (Exception exception)
                {
                    task.MarkFailed(exception.Message);
                    return;
                }
                _selection.Clear();
            }
            else if (string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase))
            {
                var ids = _selection.SelectedIds.ToList();
                var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "从配置移除", $"{ids.Count} 个 Mod");
                task.MarkRunning("正在写入配置");
                try
                {
                    var removed = await _profileService.RemoveModsFromSelectedAsync(ids, task.CancellationToken);
                    task.MarkCompleted();
                    _notificationService.Show($"已从配置移除：{removed} 个 Mod");
                }
                catch (OperationCanceledException)
                {
                    task.MarkCanceled();
                    return;
                }
                catch (Exception exception)
                {
                    task.MarkFailed(exception.Message);
                    return;
                }
                _selection.Clear();
            }
        }

        private async Task SetSelectedModsAsOptionsAsync()
        {
            if (!_selection.HasSelection
                || (!_bottomBar.IsLibrarySelection && !_bottomBar.IsProfileSelection)
                || string.IsNullOrWhiteSpace(_bottomBar.RelationshipHostId)) return;

            await SetModsAsOptionsForHostAsync(
                _bottomBar.RelationshipHostId,
                _selection.SelectedIds).ConfigureAwait(true);
        }

        /// <summary>
        /// Applies the same relationship transaction used by the selection bar
        /// to an explicit host and source list. Cross-page drag targets call
        /// this method so they never mutate the library snapshot themselves.
        /// </summary>
        public async Task<bool> SetModsAsOptionsForHostAsync(
            string hostId,
            IReadOnlyList<string> sourceModIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sourceModIds);
            var host = string.IsNullOrWhiteSpace(hostId) ? null : _libraryService.Get(hostId.Trim());
            if (host is null || host.IsDecoration || host.IsReShade)
            {
                _notificationService.Show("当前 Mod 不能作为选项主体。", NotificationLevel.Warning);
                return false;
            }

            var selected = sourceModIds
                .Where(id => !string.Equals(id, host.Guid, StringComparison.OrdinalIgnoreCase))
                .Select(_libraryService.Get)
                .Where(mod => mod is not null && !mod.IsDecoration && !mod.IsReShade)
                .Cast<HD2ModManager.Models.ModEntity>()
                .GroupBy(mod => mod.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (selected.Length == 0)
            {
                _notificationService.Show("没有可挂载到该主体的 Mod。", NotificationLevel.Warning);
                return false;
            }

            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "调整 Mod 父子关系", $"将 {selected.Length} 个 Mod 设为选项");
            task.MarkRunning("正在更新 Option 父级关系");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.CancellationToken);
                var result = await _libraryService.AddOptionsToHostAsync(
                    host.Guid,
                    selected.Select(mod => mod.Guid).ToArray(),
                    linked.Token);
                task.MarkCompleted();
                var message = result.ChangedNodeIds.Count == 0
                    ? $"已确认这些 Mod 属于“{host.Name}”的选项。"
                    : $"已将 {result.ChangedNodeIds.Count} 个 Mod 设为“{host.Name}”的选项。";
                if (result.AffectedProfileIds.Count > 0)
                    message += $" 已从 {result.AffectedProfileIds.Count} 个配置文件释放。";
                _notificationService.Show(message);
                _selection.Clear();
                RefreshCurrentPage();
                return true;
            }
            catch (OperationCanceledException) { task.MarkCanceled(); return false; }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
                _notificationService.Show($"调整 Mod 父子关系失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
                return false;
            }
        }

        private async Task AttachSelectedDecorationsAsync()
        {
            if (!_selection.HasSelection
                || (!_bottomBar.IsLibrarySelection && !_bottomBar.IsProfileSelection)
                || string.IsNullOrWhiteSpace(_bottomBar.RelationshipHostId)) return;

            var host = _libraryService.Get(_bottomBar.RelationshipHostId);
            if (host is null || host.IsDecoration || host.IsReShade) return;
            var decorations = _selection.SelectedIds
                .Select(_libraryService.Get)
                .Where(mod => mod?.IsDecoration == true)
                .Cast<HD2ModManager.Models.ModEntity>()
                .Where(decoration => _libraryService.IsDecorationAvailableForHost(decoration.Guid, host.Guid))
                .GroupBy(decoration => decoration.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (decorations.Length == 0)
            {
                _notificationService.Show($"所选装饰没有可附加到“{host.Name}”的主体。", NotificationLevel.Warning);
                return;
            }

            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "附加装饰", $"为“{host.Name}”附加 {decorations.Length} 个装饰");
            task.MarkRunning("正在更新装饰状态");
            try
            {
                await _libraryService.ApplyDecorationActivationBatchAsync(
                    decorations.Select(decoration => new DecorationActivationMutation(decoration.Guid, true, host.Guid)).ToArray(),
                    task.CancellationToken);
                task.MarkCompleted();
                _notificationService.Show($"已将 {decorations.Length} 个装饰附加到“{host.Name}”；后台将合并主体覆盖文件。 ");
                _selection.Clear();
                RefreshCurrentPage();
            }
            catch (OperationCanceledException) { task.MarkCanceled(); }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
                _notificationService.Show($"附加装饰失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
            }
        }

        private async Task RemoveSelectedOptionsFromHostAsync()
        {
            if (!_selection.HasSelection
                || !_bottomBar.IsDecorationHostSelection
                || string.IsNullOrWhiteSpace(_selection.Scope)) return;

            var hostId = _selection.Scope["DecorationHost:".Length..];
            await RemoveOptionsFromHostAsync(hostId, _selection.SelectedIds).ConfigureAwait(true);
        }

        /// <summary>
        /// Removes only direct Option parent relations for the supplied host.
        /// The operation is shared by the details-page selection bar and the
        /// details-to-library drag gesture.
        /// </summary>
        public async Task<bool> RemoveOptionsFromHostAsync(
            string hostId,
            IReadOnlyList<string> optionIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(optionIds);
            if (string.IsNullOrWhiteSpace(hostId)) return false;

            var options = optionIds
                .Select(_libraryService.Get)
                .Where(mod => mod?.IsOption == true)
                .Cast<HD2ModManager.Models.ModEntity>()
                .Where(option => _libraryService.IsDirectOptionForHost(option.Guid, hostId))
                .GroupBy(option => option.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (options.Length == 0)
            {
                _notificationService.Show("没有可从该主体移除的直属选项。", NotificationLevel.Warning);
                return false;
            }

            var host = _libraryService.Get(hostId);
            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "移除 Option 父级", $"从“{host?.Name ?? hostId}”移除 {options.Length} 个选项");
            task.MarkRunning("正在更新 Option 父级关系");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.CancellationToken);
                var result = await _libraryService.RemoveOptionsFromHostAsync(
                    hostId,
                    options.Select(option => option.Guid).ToArray(),
                    linked.Token);
                task.MarkCompleted();
                _notificationService.Show($"已从“{host?.Name ?? "当前 Mod"}”移除 {result.RemovedRelationOptionIds.Count} 个选项。 ");
                _selection.Clear();
                RefreshCurrentPage();
                return true;
            }
            catch (OperationCanceledException) { task.MarkCanceled(); return false; }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
                _notificationService.Show($"移除 Option 父级失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
                return false;
            }
        }

        public async Task AddModToSelectedProfileAsync(string modId, string modName)
        {
            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "加入配置", modName);
            task.MarkRunning("正在写入配置");
            try
            {
                if (await _profileService.AddModToSelectedAsync(modId, task.CancellationToken))
                {
                    task.MarkCompleted();
                    _notificationService.Show($"已加入正在编辑的配置：{modName}");
                }
                else
                {
                    task.MarkFailed("可能尚未选择配置或该 Mod 已存在。");
                }
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        public async Task RemoveModFromSelectedProfileAsync(string modId, string modName)
        {
            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "从配置移除", modName);
            task.MarkRunning("正在写入配置");
            try
            {
                if (await _profileService.RemoveModFromSelectedAsync(modId, task.CancellationToken))
                {
                    task.MarkCompleted();
                    _notificationService.Show($"已从配置移除：{modName}");
                }
                else
                {
                    task.MarkFailed("可能尚未选择配置或该 Mod 已不存在。");
                }
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        private async Task ExecuteSelectionDeleteAsync()
        {
            if (!_selection.HasSelection) return;
            var ids = _selection.SelectedIds.ToList();
            if (string.Equals(_selection.Scope, "Library", StringComparison.OrdinalIgnoreCase))
            {
                await DeleteModsFromLibraryAsync(ids);
                return;
            }
            if (string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveModsFromSelectedProfileAsync(ids);
                return;
            }

            if (_selection.Scope?.StartsWith("DecorationHost:", StringComparison.OrdinalIgnoreCase) == true)
                await DeleteModsFromLibraryAsync(ids);
        }

        public async Task DeleteModsFromLibraryAsync(IReadOnlyList<string> modIds)
        {
            var ids = modIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0) return;

            var targetText = ids.Count == 1
                ? $"确定彻底删除 Mod“{_libraryService.Get(ids[0])?.Name ?? ids[0]}”？\n这会从所有配置移除它，并删除模组库中的已存储文件。"
                : $"确定彻底删除选中的 {ids.Count} 个 Mod？\n这会从所有配置移除它们，并删除模组库中的已存储文件。";
            var targets = ModDeletionConfirmation.ConfirmTargets(_libraryService, ids, targetText, "批量删除 Mod");
            if (targets is null) return;

            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "批量删除 Mod", $"{targets.Count} 个 Mod");
            task.MarkRunning("正在删除");
            try
            {
                var deletion = await _libraryService.RemoveManyAsync(targets, task.CancellationToken);
                if (deletion.RemovedNodeIds.Count == 0 && deletion.HasFailures)
                {
                    task.MarkFailed("无法删除所选 Mod");
                    _notificationService.Show(ModLibraryService.DescribeDeletionResult(deletion), NotificationLevel.Error);
                    return;
                }
                task.MarkCompleted();
                _notificationService.Show(
                    ModLibraryService.DescribeDeletionResult(deletion),
                    deletion.HasFailures ? NotificationLevel.Warning : NotificationLevel.Info);
                _selection.Clear();
                RefreshCurrentPage();
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        public async Task AddModsToSelectedProfileAsync(IReadOnlyList<string> modIds)
        {
            var eligible = modIds
                .Where(id => _libraryService.Get(id)?.Capabilities.CanJoinProfile == true)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (eligible.Count == 0) return;

            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "加入配置", $"{eligible.Count} 个 Mod");
            task.MarkRunning("正在写入配置");
            try
            {
                var added = await _profileService.AddModsToSelectedAsync(eligible, task.CancellationToken);
                task.MarkCompleted();
                _notificationService.Show($"已加入正在编辑的配置：{added} 个 Mod");
                _selection.Clear();
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        public async Task RemoveModsFromSelectedProfileAsync(IReadOnlyList<string> modIds)
        {
            var ids = modIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return;

            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, "从配置移除", $"{ids.Count} 个 Mod");
            task.MarkRunning("正在写入配置");
            try
            {
                var removed = await _profileService.RemoveModsFromSelectedAsync(ids, task.CancellationToken);
                task.MarkCompleted();
                _notificationService.Show($"已从配置移除：{removed} 个 Mod");
                _selection.Clear();
            }
            catch (OperationCanceledException)
            {
                task.MarkCanceled();
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        private async Task SetSelectedDecorationsEnabledAsync(bool enabled)
        {
            if (!_selection.HasSelection) return;
            var decorations = _selection.SelectedIds
                .Select(id => _libraryService.Get(id))
                .Where(mod => mod?.IsDecoration == true)
                .Cast<HD2ModManager.Models.ModEntity>()
                .ToArray();
            var options = _selection.SelectedIds
                .Select(id => _libraryService.Get(id))
                .Where(mod => mod?.IsOption == true)
                .Cast<HD2ModManager.Models.ModEntity>()
                .ToArray();
            if (decorations.Length == 0 && options.Length == 0) return;

            var hostId = _selection.Scope?.StartsWith("DecorationHost:", StringComparison.Ordinal) == true
                ? _selection.Scope["DecorationHost:".Length..]
                : null;
            var task = _backgroundTasks.Enqueue(BackgroundTaskKind.Other, enabled ? "启用装饰" : "禁用装饰", $"{decorations.Length} 个装饰 Mod");
            task.MarkRunning(enabled ? "正在更新装饰启用状态" : "正在更新装饰禁用状态");
            try
            {
                var result = await _libraryService.ApplyDecorationActivationBatchAsync(
                    decorations.Select(decoration => new DecorationActivationMutation(decoration.Guid, enabled, hostId)).ToArray(),
                    task.CancellationToken);
                var optionMessages = new List<string>();
                foreach (var option in options)
                {
                    optionMessages.Add(hostId is null
                        ? await _libraryService.SetOptionEnabledForAllAvailableHostsAsync(option.Guid, enabled, task.CancellationToken)
                        : await SetOptionEnabledForHostAsync(option.Guid, hostId, enabled, task.CancellationToken));
                }
                task.MarkCompleted();
                var stateText = enabled ? "已启用选中的装饰。" : "已禁用选中的装饰。";
                _notificationService.Show(result.ChangedDecorationCount == 0 ? "选中的装饰状态没有变化。" : stateText);
                _selection.Clear();
                RefreshCurrentPage();
            }
            catch (OperationCanceledException) { task.MarkCanceled(); }
            catch (Exception exception) { task.MarkFailed(exception.Message); }
        }

        private async Task<string> SetOptionEnabledForHostAsync(string optionId, string hostId, bool enabled, CancellationToken cancellationToken)
        {
            var current = _libraryService.IsOptionEnabledForHost(optionId, hostId);
            if (current == enabled) return "选项状态没有变化。";
            return await _libraryService.ToggleOptionForHostAsync(optionId, hostId, cancellationToken).ConfigureAwait(false);
        }

        private async Task ExecuteSelectionDeleteFromLibraryAsync()
        {
            if (!_selection.HasSelection
                || !string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase)) return;
            await DeleteModsFromLibraryAsync(_selection.SelectedIds.ToList());
        }

        private void RaiseSelectionFlags()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(SelectionPrimaryText));
            OnPropertyChanged(nameof(SelectionDeleteText));
        }

        private static void RefreshOnUiThread(Action refresh)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                refresh();
                return;
            }
            _ = dispatcher.InvokeAsync(refresh);
        }

        private PageViewModel CreatePage(WorkspacePageType pageType)
        {
            PageViewModel page = pageType switch
            {
                WorkspacePageType.Home => new HomePageViewModel(_profileService, _libraryService, _importQueue, _applyStatus, _backgroundTasks),
                WorkspacePageType.Profile => CreateProfilePage(),
                WorkspacePageType.Library => CreateLibraryPage(),
                WorkspacePageType.Settings => new SettingsPageViewModel(_profileService, _libraryService, _bottomBar, _backgroundTasks),
                WorkspacePageType.ModDetails => new ModDetailsPageViewModel(_libraryService, _profileService, _derivedState, SelectedModId ?? string.Empty, _notificationService, _backgroundTasks, _selection),
                WorkspacePageType.AdvancedModDetails => new AdvancedModDetailsPageViewModel(_libraryService, _profileService, _derivedState, SelectedModId ?? string.Empty, _notificationService, _backgroundTasks),
                WorkspacePageType.GameDataBrowser => new GameDataBrowserPageViewModel(_libraryService, _profileService, _derivedState.InformationCenter),
                WorkspacePageType.GameDataArchiveDetails => new GameDataArchiveDetailsHostPageViewModel(null),
                WorkspacePageType.CrossArmorPlan => throw new InvalidOperationException("跨护甲计划必须通过专用路由创建。"),
                WorkspacePageType.MaterialPackaging => throw new InvalidOperationException("材质打包必须通过 Mod 详情创建。"),
                WorkspacePageType.PatchResourceRepackaging => new PatchResourceRepackagingPageViewModel(_libraryService, SelectedModId ?? string.Empty, _notificationService, _backgroundTasks),
                WorkspacePageType.DecorationPlan => new DecorationPlanPageViewModel(_libraryService, _notificationService, SelectedModId ?? string.Empty, _backgroundTasks, _editingDecorationId),
                WorkspacePageType.BatchDecorationPlan => new BatchDecorationPlanPageViewModel(_libraryService, _notificationService, SelectedModId ?? string.Empty, _backgroundTasks),
                WorkspacePageType.HelmetToArmorMergePlan => new HelmetToArmorMergePlanPageViewModel(_libraryService, SelectedModId ?? string.Empty, _notificationService, _backgroundTasks),
                WorkspacePageType.ArmorRegistry => CreateArmorRegistryPage(),
                _ => new HomePageViewModel(_profileService, _libraryService, _importQueue, _applyStatus, _backgroundTasks),
            };
            return page;
        }

        private ArmorRegistryPageViewModel CreateArmorRegistryPage()
            => _armorRegistryWorkspacePage ??= new ArmorRegistryPageViewModel(
                _libraryService,
                _notificationService,
                _deploymentCoordinator,
                _selection,
                _armorDisplayFactsGate.Task);

        private LibraryPageViewModel CreateLibraryPage()
        {
            var hideSelectedProfileMembers = LeftPageType == WorkspacePageType.Profile || RightPageType == WorkspacePageType.Profile;
            if (_libraryWorkspacePage is null)
            {
                _libraryWorkspacePage = new LibraryPageViewModel(_libraryService, _derivedState, _selection, _profileService, _notificationService, hideSelectedProfileMembers, ImportCommand);
                RegisterLibraryActions(_libraryWorkspacePage);
            }
            else
            {
                _libraryWorkspacePage.SetProfileCompanionVisible(hideSelectedProfileMembers);
            }
            EnsureDeferredLibraryProjection();
            return _libraryWorkspacePage;
        }

        private ProfilePageViewModel CreateProfilePage()
        {
            _profileWorkspacePage ??= new ProfilePageViewModel(_profileService, _libraryService, _derivedState, _selection, _bottomBar);
            EnsureDeferredActiveProfileProjection();
            return _profileWorkspacePage;
        }

        private void EnsureDeferredLibraryProjection()
        {
            if (Interlocked.Exchange(ref _libraryProjectionRequested, 1) != 0) return;
            _ = Task.Run(() => RestoreStableLibraryProjectionAsync(_lifetimeCancellation.Token));
        }

        private void EnsureDeferredActiveProfileProjection()
        {
            if (Interlocked.Exchange(ref _activeProfileProjectionRequested, 1) != 0) return;
            _ = Task.Run(() => _derivedState.RefreshAsync(_lifetimeCancellation.Token));
        }

        private void RegisterLibraryActions(PageViewModel page)
        {
            page.PageActions.Add(new PageActionViewModel("□", "新建逻辑 Mod", CreateLogicalModCommand, order: 10, kind: "CreateLogicalMod"));
            page.PageActions.Add(new PageActionViewModel("＋", "导入 Mod", ImportCommand, order: 20, kind: "Import"));
            page.PageActions.Add(new PageActionViewModel("▶", "立即重新部署", ApplyChangesCommand, background: new SolidColorBrush(Color.FromRgb(46, 125, 50)), order: 30, kind: "ScheduleDeployment"));
            page.PageActions.Add(new PageActionViewModel("✓", "校验 Mod 库", ValidateModLibraryCommand, order: 40, kind: "ValidateLibrary"));
        }

		private async void ValidateModLibrary()
		{
			var task = _backgroundTasks.Enqueue(
				BackgroundTaskKind.RefreshLibrary,
				"校验 Mod 库",
				"扫描实际 Mod 文件夹并同步库索引",
				presentation: BackgroundTaskPresentation.Foreground);
			try
			{
				task.MarkRunning("正在扫描 Mod 文件夹");
				await Task.Run(() => _libraryService.SynchronizeAsync(_lifetimeCancellation.Token, suppressDeploymentNotification: true), _lifetimeCancellation.Token).ConfigureAwait(false);
				var result = _libraryService.LastSynchronizationResult;
				var added = result?.AddedNodeIds.Count ?? 0;
				var changed = result?.ChangedNodeIds.Count ?? 0;
				var missing = result?.MissingNodeIds.Count ?? 0;
				task.MarkCompleted();
				var message = added == 0 && changed == 0 && missing == 0
					? "Mod 库校验完成，库索引与实际文件夹一致。"
					: $"Mod 库校验完成：新增 {added} 个，更新 {changed} 个，缺失 {missing} 个。";
				RefreshOnUiThread(() => _notificationService.Show(message, missing > 0 ? NotificationLevel.Warning : NotificationLevel.Info, TimeSpan.FromSeconds(8)));
			}
			catch (OperationCanceledException)
			{
				task.MarkCanceled();
			}
			catch (Exception exception)
			{
				task.MarkFailed(exception.Message);
				RefreshOnUiThread(() => _notificationService.Show($"校验 Mod 库失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(10)));
			}
		}

        private void UpdateModeFromSlots()
        {
            CurrentMode = (LeftPageType, RightPageType, IsSplitView) switch
            {
                (WorkspacePageType.Home, WorkspacePageType.Home, false) => WorkspaceMode.Home,
                (WorkspacePageType.Profile, WorkspacePageType.Profile, false) => WorkspaceMode.ProfileOnly,
                (WorkspacePageType.Library, WorkspacePageType.Library, false) => WorkspaceMode.LibraryOnly,
                (WorkspacePageType.Profile, WorkspacePageType.Library, true) => WorkspaceMode.ProfileLibrarySplit,
                (WorkspacePageType.Settings, WorkspacePageType.Settings, false) => WorkspaceMode.Settings,
                (WorkspacePageType.ArmorRegistry, WorkspacePageType.ArmorRegistry, false) => WorkspaceMode.ArmorRegistry,
                _ => CurrentMode,
            };
            // 这里是除 CommitNavigation 之外所有导航路径的共同收尾
            // （OpenSinglePage / OpenPage / OpenGameDataBrowser / OpenCrossArmorPlan /
            //  OpenSecondaryPage / CloseDeletedModDetails），所以返回队列的记录点挂在这里。
            RecordWorkspaceEntry();
        }

        private static string GetPageTitle(WorkspacePageType pageType) => pageType switch
        {
            WorkspacePageType.Home => "首页",
            WorkspacePageType.Profile => "配置页",
            WorkspacePageType.Library => "模组库",
            WorkspacePageType.Settings => "设置",
            WorkspacePageType.ModDetails => "Mod 详情",
            WorkspacePageType.AdvancedModDetails => "高级 Mod 详情",
            WorkspacePageType.GameDataBrowser => "Game Data 资产",
            WorkspacePageType.GameDataArchiveDetails => "Archive 详情",
            WorkspacePageType.CrossArmorPlan => "跨护甲计划",
            WorkspacePageType.MaterialPackaging => "材质候选与输出",
            WorkspacePageType.PatchResourceRepackaging => "Patch 资源重打包",
            WorkspacePageType.DecorationPlan => "生成装饰 Mod",
            WorkspacePageType.BatchDecorationPlan => "批量生成装饰",
            WorkspacePageType.HelmetToArmorMergePlan => "头盔合并到盔甲并输出",
            WorkspacePageType.ArmorRegistry => "运行时护甲注册",
            _ => "页面",
        };

        private void RaiseSlotFlags()
        {
            UpdateLibraryCompanionContext();
            OnPropertyChanged(nameof(IsSplitView));
            OnPropertyChanged(nameof(ShowRightSlot));
            OnPropertyChanged(nameof(IsLeftSlotLibrary));
            OnPropertyChanged(nameof(IsRightSlotLibrary));
            OnPropertyChanged(nameof(IsFullScreenLibrary));
            OnPropertyChanged(nameof(IsSplitLeftSlotLibrary));
            OnPropertyChanged(nameof(LeftSlotTitle));
            OnPropertyChanged(nameof(RightSlotTitle));
            RaiseActionFlags();
        }

        private void UpdateLibraryCompanionContext()
        {
            var isLibraryProfilePair = IsSplitView
                && ((LeftPageType == WorkspacePageType.Library && RightPageType == WorkspacePageType.Profile)
                    || (RightPageType == WorkspacePageType.Library && LeftPageType == WorkspacePageType.Profile));
            _bottomBar.SetLibraryProfileCompanionVisible(isLibraryProfilePair);
            if (LeftPage is LibraryPageViewModel leftLibrary)
                leftLibrary.SetProfileCompanionVisible(IsSplitView && RightPageType == WorkspacePageType.Profile);
            if (RightPage is LibraryPageViewModel rightLibrary)
                rightLibrary.SetProfileCompanionVisible(IsSplitView && LeftPageType == WorkspacePageType.Profile);
            if (LeftPage is ProfilePageViewModel leftProfile)
                leftProfile.SetLibraryDropTargetAvailable(isLibraryProfilePair);
            if (RightPage is ProfilePageViewModel rightProfile)
                rightProfile.SetLibraryDropTargetAvailable(isLibraryProfilePair);

            UpdateRelationshipHostContext();
        }

        private void UpdateRelationshipHostContext()
        {
            ModDetailsPageViewModel? target = null;
            if (IsSplitView)
            {
                if (LeftPage is ModDetailsPageViewModel leftDetails
                    && RightPage is (LibraryPageViewModel or ProfilePageViewModel))
                    target = leftDetails;
                else if (RightPage is ModDetailsPageViewModel rightDetails
                    && LeftPage is (LibraryPageViewModel or ProfilePageViewModel))
                    target = rightDetails;
            }

            _bottomBar.SetRelationshipHostContext(
                target?.CanHostAttachments == true ? target.ModId : null,
                target?.CanHostAttachments == true ? target.Name : null);
        }

        private void RaiseActionFlags()
        {
            OnPropertyChanged(nameof(LeftSlotActions));
            OnPropertyChanged(nameof(RightSlotActions));
            OnPropertyChanged(nameof(HasLeftSlotActions));
            OnPropertyChanged(nameof(HasRightSlotActions));
            OnPropertyChanged(nameof(ShowSplitLeftSlotActions));
            OnPropertyChanged(nameof(ShowRightSlotActions));
            OnPropertyChanged(nameof(ShowFullScreenActions));
        }

        private void RaiseModeFlags()
        {
            OnPropertyChanged(nameof(IsHomeActive));
            OnPropertyChanged(nameof(ShowHomeTitle));
            OnPropertyChanged(nameof(ShowLaunchGameButton));
            OnPropertyChanged(nameof(IsProfileActive));
            OnPropertyChanged(nameof(IsLibraryActive));
            OnPropertyChanged(nameof(IsSplitActive));
            OnPropertyChanged(nameof(IsSettingsActive));
            OnPropertyChanged(nameof(IsArmorRegistryActive));
        }
    }
}
