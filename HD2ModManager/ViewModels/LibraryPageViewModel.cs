using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HD2ModCore.Infrastructure;
using HD2ModCore.Domain;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    // 作用：管理模组库列表、分组、行选择与库内条目操作。
    public class LibraryPageViewModel : PageViewModel
    {
        private const string SelectionScope = "Library";
        private readonly ModLibraryService _library;
        private readonly DerivedStateCoordinator? _derivedState;
        private readonly ProfileService? _profiles;
        private readonly NotificationService? _notifications;
        private readonly SelectionCoordinator? _selection;
        private bool _hideSelectedProfileMembers;
        private bool _canDragToProfile;
        private readonly ObservableCollection<string> _selectedGuids = new();
        private readonly Dictionary<string, ModUserStatus> _userStatuses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _thumbnailSources = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _searchCancellation;
        private CancellationTokenSource? _thumbnailCancellation;
        private readonly HashSet<string> _pendingProjectionGuids = new(StringComparer.OrdinalIgnoreCase);
        private int _projectionRefreshQueued;
        private bool _forceProjectionReconcile;
        private bool _refreshAllPresentations;
        private bool _thumbnailWarmupQueued;
        private int _lifecycleVersion;
        private bool _disposed;

        public BulkObservableCollection<ModCardViewModel> Items { get; } = new(item => item.Mod.Guid);
        public bool HasItems => Items.Count != 0;

        private string _query = string.Empty;
        public string Query
        {
            get => _query;
            set
            {
                if (string.Equals(_query, value, StringComparison.Ordinal)) return;
                _query = value;
                OnPropertyChanged();
                QueueSearchRefresh();
            }
        }
        public string ItemCountText => $"显示 {Items.Count} / {_library.All().Count()} 个 Mod";
        public string EmptyMessage => _hideSelectedProfileMembers && _profiles?.SelectedProfile is not null ? "所有 Mod 都已加入此配置。" : "模组库中没有可显示的 Mod。";
        private bool _showOnlyOutdated;
        private bool _showDecorations;
        private bool _showOptions;
        public bool ShowOnlyOutdated
        {
            get => _showOnlyOutdated;
            set
            {
                if (!SetField(ref _showOnlyOutdated, value)) return;
                OnPropertyChanged(nameof(OutdatedFilterText));
                Refresh(ListTransitionKind.Filter);
            }
        }
        public string OutdatedFilterText => ShowOnlyOutdated ? "显示全部" : "显示过时";
        public bool ShowDecorations
        {
            get => _showDecorations;
            set
            {
                if (!SetField(ref _showDecorations, value)) return;
                OnPropertyChanged(nameof(DecorationFilterText));
                Refresh(ListTransitionKind.Filter);
            }
        }
        public string DecorationFilterText => ShowDecorations ? "隐藏装饰" : "显示装饰";
        public bool ShowOptions
        {
            get => _showOptions;
            set
            {
                if (!SetField(ref _showOptions, value)) return;
                OnPropertyChanged(nameof(OptionFilterText));
                Refresh(ListTransitionKind.Filter);
            }
        }
        public string OptionFilterText => ShowOptions ? "隐藏选项" : "显示选项";

        public RelayCommand RemoveModCommand { get; }
        public RelayCommand ToggleSelectionCommand { get; }
        public RelayCommand AddToProfileCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand ToggleDecorationCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand EditDescriptionCommand { get; }
        public RelayCommand EditImageCommand { get; }
        public RelayCommand RemoveCommand { get; }
        private bool _isCompact = true;
        public bool IsCompact { get => _isCompact; set { _isCompact = value; OnPropertyChanged(nameof(IsCompact)); } }

        public LibraryPageViewModel(ModLibraryService library, DerivedStateCoordinator? derivedState = null, SelectionCoordinator? selection = null, ProfileService? profiles = null, NotificationService? notifications = null, bool hideSelectedProfileMembers = false)
        {
            Title = "模组库";
            _library = library;
            _derivedState = derivedState;
            _profiles = profiles;
            _notifications = notifications;
            _selection = selection;
            _hideSelectedProfileMembers = hideSelectedProfileMembers;
            if (_selection != null) _selection.SelectionChanged += OnSelectionChanged;
            if (_profiles != null) _profiles.Changed += OnProfileChanged;
            _library.ModContentFactsChanged += OnLibraryContentFactsChanged;
            _library.SnapshotChanged += OnLibrarySnapshotChanged;
            if (_derivedState is not null) _derivedState.SnapshotChanged += OnDerivedStateSnapshotChanged;
            RemoveModCommand = new RelayCommand(() => { /* parameter passed via CommandParameter not used here */ });
            ToggleSelectionCommand = new RelayCommand(ToggleSelection);
            AddToProfileCommand = new RelayCommand(parameter => AddToProfile(parameter as ModCardViewModel));
            OpenFolderCommand = new RelayCommand(parameter => OpenFolder(parameter as ModCardViewModel));
            ToggleDecorationCommand = new RelayCommand(async parameter => await ToggleDecorationAsync(parameter as ModCardViewModel));
            RenameCommand = new AsyncRelayCommand(parameter => RenameModAsync(parameter as ModCardViewModel, parameter is string name ? name : string.Empty));
            EditDescriptionCommand = new AsyncRelayCommand(parameter => UpdateDescriptionAsync(parameter as ModCardViewModel, parameter is string description ? description : string.Empty));
            EditImageCommand = new RelayCommand(parameter => _ = UpdateIconAsync(parameter as ModCardViewModel, parameter is string path ? path : string.Empty));
            RemoveCommand = new RelayCommand(parameter => RemoveMod(parameter as ModCardViewModel));
            QueueStatusRefresh();
            QueueThumbnailWarmup();
        }

        public LibraryPageViewModel(ModLibraryService library, SelectionCoordinator? selection, ProfileService? profiles, NotificationService? notifications = null)
            : this(library, null, selection, profiles, notifications)
        {
        }

        public void SetProfileCompanionVisible(bool visible)
        {
            if (SetField(ref _canDragToProfile, visible, nameof(CanDragToProfile))) { }
            if (_hideSelectedProfileMembers == visible) return;
            _hideSelectedProfileMembers = visible;
            Refresh();
        }

        public void ApplySelection(IReadOnlyList<string> selectedKeys)
        {
            _selectedGuids.Clear();
            foreach (var key in selectedKeys) _selectedGuids.Add(key);
            _selection?.Replace(SelectionScope, selectedKeys);
            RefreshSelectionFlags();
        }

        public bool CanDragToProfile => _canDragToProfile;

        public bool RenameMod(ModCardViewModel? card, string newName)
        {
            if (card == null || string.IsNullOrWhiteSpace(newName) || newName == card.Mod.Name) return false;
            var ok = _library.Rename(card.Mod.Guid, newName);
            if (ok) _notifications?.Show($"已重命名：{newName.Trim()}");
            Refresh();
            return ok;
        }

        public async Task RenameModAsync(ModCardViewModel? card, string newName)
        {
            if (card == null || string.IsNullOrWhiteSpace(newName) || newName == card.Mod.Name) return;
            try
            {
                if (await _library.RenameAsync(card.Mod.Guid, newName))
                    _notifications?.Show($"已重命名：{newName.Trim()}");
                Refresh();
            }
            catch (System.Exception ex) { _notifications?.Show($"重命名失败：{ex.Message}", NotificationLevel.Error); }
        }

        public void UpdateDescription(ModCardViewModel? card, string? description)
        {
            if (card == null) return;
            card.Mod.Description = description ?? string.Empty;
            _library.Add(card.Mod);
            _library.Save();
            _notifications?.Show($"已更新备注：{card.Mod.Name}");
            Refresh();
        }

        public async Task UpdateDescriptionAsync(ModCardViewModel? card, string? description)
        {
            if (card == null) return;
            try
            {
                card.Mod.Description = description ?? string.Empty;
                if (await _library.AddAsync(card.Mod))
                    _notifications?.Show($"已更新备注：{card.Mod.Name}");
                Refresh();
            }
            catch (System.Exception ex) { _notifications?.Show($"更新备注失败：{ex.Message}", NotificationLevel.Error); }
        }

        public void UpdateIcon(ModCardViewModel? card, string sourceImagePath)
            => _ = UpdateIconAsync(card, sourceImagePath);

        private async Task UpdateIconAsync(ModCardViewModel? card, string sourceImagePath)
        {
            if (card == null || string.IsNullOrWhiteSpace(sourceImagePath)) return;
            var modDir = _library.ResolveAbsolutePath(card.Mod.SourcePath);
            if (string.IsNullOrWhiteSpace(modDir) || !System.IO.Directory.Exists(modDir)) return;
            var destination = System.IO.Path.Combine(modDir, "icon" + System.IO.Path.GetExtension(sourceImagePath).ToLowerInvariant());
            try
            {
                await Task.Run(() => System.IO.File.Copy(sourceImagePath, destination, overwrite: true)).ConfigureAwait(false);
                card.Mod.Image = destination;
                await _library.AddAsync(card.Mod).ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    _notifications?.Show($"已更新图标：{card.Mod.Name}");
                    Refresh();
                    QueueThumbnailRefresh();
                });
            }
            catch (System.Exception ex)
            {
                RunOnUiThread(() => _notifications?.Show($"更新图标失败：{ex.Message}", NotificationLevel.Error));
            }
        }

        public void Refresh(ListTransitionKind transitionKind = ListTransitionKind.Automatic)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "Library.Refresh",
                $"kind={transitionKind}; current={Items.Count}",
                threshold: 10);
            using var perf = UiPerformanceDiagnostics.Measure(
                "Library.Refresh",
                thresholdMilliseconds: 25,
                details: () => $"kind={transitionKind}; result={Items.Count}; query={(string.IsNullOrWhiteSpace(Query) ? "empty" : "set")}; decorations={ShowDecorations}; options={ShowOptions}; outdated={ShowOnlyOutdated}");
            var all = _library.All().ToList();
            if (!ShowDecorations) all = all.Where(mod => !mod.IsDecoration).ToList();
            if (!ShowOptions) all = all.Where(mod => !mod.IsOption).ToList();
            if (_hideSelectedProfileMembers && _profiles?.SelectedProfile is not null)
            {
                var selectedIds = _profiles!.SelectedProfile!.Entries.Select(entry => entry.NodeId.Value.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                all = all.Where(mod => !selectedIds.Contains(mod.Guid)).ToList();
            }
            var q = (_query ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                all = all.Where(mod => ModSearchMatcher.IsMatch(mod.Name, mod.Description, _library.GetDerivedData(mod.Guid)?.AssetSummary, q)).ToList();
            }
			if (ShowOnlyOutdated)
			{
				all = all.Where(mod => _library.GetDerivedData(mod.Guid)?.UnitCompatibility.IsOutdated == true).ToList();
			}
            var cards = all
                .OrderBy(mod => mod.Name, System.StringComparer.CurrentCultureIgnoreCase)
                .Select(mod =>
                {
                    var derived = _library.GetDerivedData(mod.Guid);
                    _userStatuses.TryGetValue(mod.Guid, out var status);
                    _thumbnailSources.TryGetValue(mod.Guid, out var thumbnailSource);
                    var decorationStatus = mod.IsDecoration ? _library.GetDecorationActivationSummary(mod.Guid).StatusText : mod.IsOption ? _library.GetOptionActivationStatus(mod.Guid) : null;
                    return new ModCardViewModel(mod, IsSelected(mod.Guid), mod.IsDecoration || mod.IsOption || mod.IsReShade ? null : derived?.AssetSummary, mod.IsDecoration || mod.IsOption || mod.IsReShade ? null : derived?.UnitCompatibility, status, thumbnailSource, decorationStatus);
                })
                .ToList();
            Items.ReplaceWith(cards, transitionKind);
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ItemCountText));
        }

        public void RefreshFromShell() => QueueStatusRefresh();

        private async void QueueSearchRefresh()
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            var cancellationToken = _searchCancellation.Token;
            try
            {
                await Task.Delay(180, cancellationToken);
                if (!_disposed && !cancellationToken.IsCancellationRequested && _searchCancellation?.Token == cancellationToken) Refresh(ListTransitionKind.Filter);
            }
            catch (OperationCanceledException) { }
        }

        private void RefreshUserStatuses()
        {
            if (_profiles is null || _derivedState is null) return;
            var statuses = _derivedState.ProjectStatuses(_profiles.SelectedProfileId);
            _userStatuses.Clear();
            foreach (var pair in statuses) _userStatuses[pair.Key.Value.ToString("N")] = pair.Value;
        }

        private void QueueStatusRefresh()
        {
            if (_disposed) return;
            RefreshUserStatuses();
            Refresh();
        }

        private async void QueueThumbnailRefresh()
        {
            var version = _lifecycleVersion;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _thumbnailCancellation = cancellation;
            var cancellationToken = cancellation.Token;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var processed = 0;
            using var thumbnailPerf = UiPerformanceDiagnostics.Measure(
                "Library.ThumbnailRefresh",
                thresholdMilliseconds: 100,
                details: () => $"processed={processed}; version={version}; canceled={cancellationToken.IsCancellationRequested}");
            try
            {
                // 先固定本次刷新快照，避免异步请求期间库同步修改底层字典。
                var mods = await Task.Run(() => _library.All().ToList(), cancellationToken).ConfigureAwait(false);
                foreach (var mod in mods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;
                    var result = await Task.Run(
                        () => _library.RequestThumbnailAsync(mod.Guid, "Library", cancellationToken: cancellationToken).AsTask(),
                        cancellationToken).ConfigureAwait(false);
                    if (result.Data is { } facts)
                    {
                        await ThumbnailService.EnsureThumbnailAsync(facts, 72, cancellationToken);
                        _thumbnailSources[mod.Guid] = facts.SourcePath;
                    }
                }
                if (IsCurrentThumbnailRefresh(version, cancellation))
                    RunOnUiThread(RefreshVisibleThumbnailSources);
                LogService.Info($"列表缩略图刷新完成：页面=模组库，条目={processed}，耗时={clock.ElapsedMilliseconds}ms，版本={version}。");
            }
            catch (OperationCanceledException)
            {
                LogService.Info($"列表缩略图刷新取消：页面=模组库，已处理={processed}，耗时={clock.ElapsedMilliseconds}ms，版本={version}。");
            }
        }

        private void OnSelectionChanged(object? sender, EventArgs e) => SyncSelectionFromCoordinator();

        private void OnProfileChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(null, forceReconcile: _hideSelectedProfileMembers, refreshAllPresentations: true);

        private void OnLibraryContentFactsChanged(object? sender, ModContentFactsChangedEventArgs e)
            => QueueProjectionRefresh(e.NodeIds, e.Kind is ModContentChangeKind.Added or ModContentChangeKind.Removed);

        private void OnLibrarySnapshotChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(null, forceReconcile: true);

        private void OnDerivedStateSnapshotChanged(object? sender, DerivedStateSnapshot e)
            => QueueProjectionRefresh(null, forceReconcile: false, refreshAllPresentations: true);

        private void QueueProjectionRefresh(IEnumerable<ModNodeId>? nodeIds, bool forceReconcile, bool refreshAllPresentations = false)
        {
            if (_disposed) return;
            lock (_pendingProjectionGuids)
            {
                if (nodeIds is not null)
                    foreach (var nodeId in nodeIds) _pendingProjectionGuids.Add(nodeId.Value.ToString("N"));
                _forceProjectionReconcile |= forceReconcile;
                _refreshAllPresentations |= refreshAllPresentations;
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
            bool reconcile;
            bool refreshAllPresentations;
            lock (_pendingProjectionGuids)
            {
                guids = new HashSet<string>(_pendingProjectionGuids, StringComparer.OrdinalIgnoreCase);
                _pendingProjectionGuids.Clear();
                reconcile = _forceProjectionReconcile;
                _forceProjectionReconcile = false;
                refreshAllPresentations = _refreshAllPresentations;
                _refreshAllPresentations = false;
            }
            Interlocked.Exchange(ref _projectionRefreshQueued, 0);
            UiPerformanceDiagnostics.RecordBurst(
                "Library.ApplyProjection",
                $"guids={guids.Count}; reconcile={reconcile}; allPresentations={refreshAllPresentations}",
                threshold: 8);
            using var perf = UiPerformanceDiagnostics.Measure(
                "Library.ApplyProjection",
                thresholdMilliseconds: 25,
                details: () => $"guids={guids.Count}; reconcile={reconcile}; allPresentations={refreshAllPresentations}; items={Items.Count}");
            RefreshUserStatuses();
            // A status-only update cannot affect the current filter or order. Keep the
            // existing containers so virtualized rows retain their visual state.
            var contentCanAffectFilter = guids.Count != 0 && (!string.IsNullOrWhiteSpace(Query) || ShowOnlyOutdated);
            var didReconcile = reconcile || contentCanAffectFilter;
            if (didReconcile)
            {
                // 此处只在成员或筛选结果可能变化时重建集合；让集合按真实
                // Key 差异解析插入、移除和重排，状态投影分支仍不触碰集合。
                Refresh();
            }
            else
            {
                var cards = refreshAllPresentations
                    ? Items
                    : Items.Where(item => guids.Contains(item.Mod.Guid));
                foreach (var card in cards)
                    card.UpdatePresentation(_library.Get(card.Mod.Guid), card.Mod.IsDecoration || card.Mod.IsOption || card.Mod.IsReShade ? null : _library.GetDerivedData(card.Mod.Guid), _userStatuses.GetValueOrDefault(card.Mod.Guid), _thumbnailSources.GetValueOrDefault(card.Mod.Guid), card.Mod.IsDecoration ? _library.GetDecorationActivationSummary(card.Mod.Guid).StatusText : card.Mod.IsOption ? _library.GetOptionActivationStatus(card.Mod.Guid) : null);
                OnPropertyChanged(nameof(ItemCountText));
                OnPropertyChanged(nameof(EmptyMessage));
            }
            if (didReconcile || guids.Count != 0) QueueThumbnailRefresh();
            else QueueThumbnailWarmup();
        }

        private void RefreshVisibleThumbnailSources()
        {
            if (_disposed) return;
            foreach (var card in Items)
                card.SetThumbnailSource(_thumbnailSources.GetValueOrDefault(card.Mod.Guid));
        }

        private void QueueThumbnailWarmup()
        {
            if (_thumbnailWarmupQueued || !_library.All().Any()) return;
            _thumbnailWarmupQueued = true;
            QueueThumbnailRefresh();
        }

        private bool IsCurrentThumbnailRefresh(int version, CancellationTokenSource cancellation)
            => !_disposed && version == _lifecycleVersion && ReferenceEquals(_thumbnailCancellation, cancellation) && !cancellation.IsCancellationRequested;

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else _ = dispatcher.InvokeAsync(action);
        }

        private async void RemoveMod(ModCardViewModel? card)
        {
            if (card == null) return;
            var targets = ModDeletionConfirmation.ConfirmTargets(
                _library,
                [card.Mod.Guid],
                $"确定删除 Mod“{card.Mod.Name}”？\n这会同时删除库中的已存储文件。");
            if (targets is null) return;
            try
            {
                ThumbnailService.CancelPendingGeneration();
                await _library.RemoveManyAsync(targets);
                _notifications?.Show($"已删除：{card.Mod.Name}");
            }
            catch (System.Exception ex)
            {
                _notifications?.Show($"删除失败：{ex.Message}", NotificationLevel.Error);
            }
            Refresh();
        }

        private void AddToProfile(ModCardViewModel? card)
        {
            if (card == null || _profiles == null) return;
            if (card.Mod.IsDecoration || card.Mod.IsOption)
            {
                _notifications?.Show(card.Mod.IsDecoration
                    ? "装饰 Mod 不能加入配置；请在主体 Mod 中启用。"
                    : "选项 Mod 不能加入配置；请在主体 Mod 中启用。", NotificationLevel.Info);
                return;
            }
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            {
                _ = shell.AddModToSelectedProfileAsync(card.Mod.Guid, card.Mod.Name);
            }
        }

        private void OpenFolder(ModCardViewModel? card)
        {
            if (card == null) return;
            try
            {
                var abs = _library.ResolveAbsolutePath(card.Mod.SourcePath);
                if (!System.IO.Directory.Exists(abs)) System.IO.Directory.CreateDirectory(abs);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = abs, UseShellExecute = true });
            }
            catch { }
        }

        private async Task ToggleDecorationAsync(ModCardViewModel? card)
        {
            if (card?.Mod.IsDecoration != true) return;
            try
            {
                var result = await _library.ToggleDecorationForAllAvailableHostsAsync(card.Mod.Guid).ConfigureAwait(true);
                _notifications?.Show(result.StatusText, NotificationLevel.Info);
                Refresh();
            }
            catch (Exception exception)
            {
                LogService.Error($"模组库启用装饰失败：装饰={card.Mod.Guid}，异常={exception}");
                _notifications?.Show($"启用装饰失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
            }
        }

        private void ToggleSelection(object? parameter)
        {
            if (parameter is not ModCardViewModel card) return;
            if (!_selectedGuids.Remove(card.Mod.Guid)) _selectedGuids.Add(card.Mod.Guid);
            _selection?.Replace(SelectionScope, _selectedGuids);
            RefreshSelectionFlags();
        }

        private bool IsSelected(string guid) => _selectedGuids.Any(id => string.Equals(id, guid, System.StringComparison.OrdinalIgnoreCase));

        private void SyncSelectionFromCoordinator()
        {
            if (_selection == null) return;
            _selectedGuids.Clear();
            if (string.Equals(_selection.Scope, SelectionScope, System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in _selection.SelectedIds) _selectedGuids.Add(id);
            }
            RefreshSelectionFlags();
        }

        private void RefreshSelectionFlags()
        {
            foreach (var card in Items)
            {
                card.IsSelected = IsSelected(card.Mod.Guid);
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifecycleVersion++;
            _selection?.SelectionChanged -= OnSelectionChanged;
            if (_profiles != null) _profiles.Changed -= OnProfileChanged;
            _library.ModContentFactsChanged -= OnLibraryContentFactsChanged;
            _library.SnapshotChanged -= OnLibrarySnapshotChanged;
            if (_derivedState is not null) _derivedState.SnapshotChanged -= OnDerivedStateSnapshotChanged;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            _thumbnailCancellation = null;
        }
    }

    public class ModCardViewModel : BaseViewModel, IModListSelectable
    {
        private HD2ModManager.Models.ModEntity _mod;
        private ModAssetSummary? _assetSummary;
        private ModUnitCompatibilityReport? _unitCompatibility;
        private ModUserStatus? _userStatus;
        private string? _decorationStatus;
        public HD2ModManager.Models.ModEntity Mod => _mod;
        public string SelectionKey => Mod.Guid;
        public bool IsDecoration => Mod.IsDecoration;
        public bool IsOption => Mod.IsOption;
        public bool IsReShade => Mod.IsReShade;
        public bool IsAttachable => Mod.IsDecoration || Mod.IsOption;
        public bool CanJoinProfile => Mod.Capabilities.CanJoinProfile;
        private string? _thumbnailSourcePath;
        public ModAssetSummary? AssetSummary => _assetSummary;
        public string Name => Mod.Name;
        public string AssetSummaryText => Mod.IsReShade
            ? "ReShade 插件 · 部署到游戏 bin"
            : !Mod.HasPatchContent && !Mod.IsDecoration && !Mod.IsOption
            ? Mod.Description ?? string.Empty
            : Mod.IsDecoration || Mod.IsOption
            ? DecorationStatus ?? "尚未启用。"
            : ModAssetSummaryFormatter.Format(AssetSummary);
        public bool ShowsPatchMetadata => Mod.Capabilities.ShowsPatchAssets;
        public string? ImagePath => ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
        public bool IsVisible => true;
        public bool ShowImage => true;
        public string? Description => Mod.Description;
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
        public ModUserStatus? UserStatus => _userStatus;
        public ModUnitCompatibilityReport? UnitCompatibility => _unitCompatibility;
        public bool IsModelOutdated => UnitCompatibility?.IsOutdated == true;
        public string ModelCompatibilitySummary => UnitCompatibility?.Summary ?? "模型版本尚未检测。";
        public string UserStatusTitle => Mod.IsReShade ? "ReShade 插件" : Mod.IsDecoration ? "装饰 Mod" : Mod.IsOption ? "选项 Mod" : UserStatus?.Title ?? "状态未知";
        public string UserStatusSummary => Mod.IsReShade ? "启用后部署到游戏 bin 目录。" : Mod.IsDecoration || Mod.IsOption ? DecorationStatus ?? "尚未启用。" : UserStatus?.Summary ?? "正在读取状态。";
        public bool HasUserStatus => UserStatus is not null;
        public string? DecorationStatus => _decorationStatus;

        public void SetThumbnailSource(string? sourcePath)
        {
            if (string.Equals(_thumbnailSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                OnPropertyChanged(nameof(ImagePath));
                return;
            }

            _thumbnailSourcePath = sourcePath;
            OnPropertyChanged(nameof(ImagePath));
        }

        public ModCardViewModel(HD2ModManager.Models.ModEntity mod, bool isSelected = false, ModAssetSummary? assetSummary = null, ModUnitCompatibilityReport? unitCompatibility = null, ModUserStatus? userStatus = null, string? thumbnailSourcePath = null, string? decorationStatus = null)
        {
            _mod = mod;
            _thumbnailSourcePath = thumbnailSourcePath ?? mod.Image;
            _assetSummary = assetSummary;
			_unitCompatibility = unitCompatibility;
            _userStatus = userStatus;
            _decorationStatus = decorationStatus;
            _isSelected = isSelected;
        }

        public void UpdatePresentation(HD2ModManager.Models.ModEntity? mod, DerivedModNodeData? derived, ModUserStatus? userStatus, string? thumbnailSourcePath, string? decorationStatus)
        {
            if (mod is not null) _mod = mod;
            _assetSummary = derived?.AssetSummary;
            _unitCompatibility = derived?.UnitCompatibility;
            _userStatus = userStatus;
            _decorationStatus = decorationStatus;
            if (!string.IsNullOrWhiteSpace(thumbnailSourcePath)) _thumbnailSourcePath = thumbnailSourcePath;
            OnPropertyChanged(nameof(Mod)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(IsDecoration)); OnPropertyChanged(nameof(IsOption)); OnPropertyChanged(nameof(IsReShade)); OnPropertyChanged(nameof(IsAttachable)); OnPropertyChanged(nameof(CanJoinProfile));
            OnPropertyChanged(nameof(AssetSummary)); OnPropertyChanged(nameof(AssetSummaryText)); OnPropertyChanged(nameof(UnitCompatibility));
            OnPropertyChanged(nameof(IsModelOutdated)); OnPropertyChanged(nameof(UserStatus)); OnPropertyChanged(nameof(UserStatusTitle));
            OnPropertyChanged(nameof(UserStatusSummary)); OnPropertyChanged(nameof(DecorationStatus)); OnPropertyChanged(nameof(ImagePath));
        }

    }

}
