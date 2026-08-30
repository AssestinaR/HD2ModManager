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
        private readonly ModListStateController<LibraryModListState, ModCardViewModel> _list;
        private bool _hideSelectedProfileMembers;
        private bool _canDragToProfile;
        private readonly ObservableCollection<string> _selectedGuids = new();
        private readonly Dictionary<string, ModUserStatus> _userStatuses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _thumbnailSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _thumbnailRevisions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _directOptionCounts = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _searchCancellation;
        private CancellationTokenSource? _thumbnailCancellation;
        private readonly HashSet<string> _pendingProjectionGuids = new(StringComparer.OrdinalIgnoreCase);
        private int _projectionRefreshQueued;
        private bool _projectionRequiresRebuild;
        private bool _projectionRefreshVisibleContent;
        private bool _thumbnailWarmupQueued;
        private int _lifecycleVersion;
        private bool _hasSubmittedInitialProjection;
        private bool _disposed;

        public ModListProjectionSource<ModCardViewModel> Items => _list.Items;
        public bool HasItems => _list.VisibleCount != 0;

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
        public string ItemCountText => $"显示 {_list.VisibleCount} / {_library.All().Count()} 个 Mod";
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
            _list = new ModListStateController<LibraryModListState, ModCardViewModel>(
                state => state.Guid,
                state => state.PresentationVersion,
                state => new ModCardViewModel(state),
                (card, state) => card.ApplyListState(state),
                card => card.SelectionKey);
            _list.Committed += OnListProjectionCommitted;
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
            ArgumentNullException.ThrowIfNull(selectedKeys);
            if (_selection is not null)
            {
                _selection.Replace(SelectionScope, selectedKeys);
                return;
            }

            ApplyLocalSelection(selectedKeys);
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
                    QueueThumbnailRefresh([card.Mod.Guid]);
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
            var libraryMods = _library.All().ToList();
            RefreshDirectOptionCounts();
            var all = libraryMods;
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
            var states = all
                .OrderBy(mod => mod.Name, System.StringComparer.CurrentCultureIgnoreCase)
                .Select(CreateListState)
                .ToList();
            _list.Submit(
                states,
                libraryMods.Select(mod => mod.Guid),
                commitImmediately: !_hasSubmittedInitialProjection);
            _hasSubmittedInitialProjection = true;
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

        private async void QueueThumbnailRefresh(IEnumerable<string>? requestedGuids = null)
        {
            var version = _lifecycleVersion;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _thumbnailCancellation = cancellation;
            var cancellationToken = cancellation.Token;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var processed = 0;
            var completedSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var refreshedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var thumbnailPerf = UiPerformanceDiagnostics.Measure(
                "Library.ThumbnailRefresh",
                thresholdMilliseconds: 100,
                details: () => $"processed={processed}; version={version}; canceled={cancellationToken.IsCancellationRequested}");
            try
            {
                // 先固定本次刷新快照，避免异步请求期间库同步修改底层字典。
                // Normal content changes ask only for their affected Mod IDs. A
                // full library pass is reserved for the one-time warmup.
                var requested = requestedGuids?
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var mods = await Task.Run(() => requested is null
                    ? _library.All().ToList()
                    : requested.Select(_library.Get).Where(mod => mod is not null).Cast<HD2ModManager.Models.ModEntity>().ToList(), cancellationToken).ConfigureAwait(false);
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
                        completedSources[mod.Guid] = facts.SourcePath;
                        refreshedIds.Add(mod.Guid);
                    }
                }
                if (IsCurrentThumbnailRefresh(version, cancellation))
                {
                    await RunOnUiThreadAsync(() => ApplyThumbnailResults(version, cancellation, completedSources, refreshedIds));
                }
                LogService.Info($"列表缩略图刷新完成：页面=模组库，条目={processed}，耗时={clock.ElapsedMilliseconds}ms，版本={version}。");
            }
            catch (OperationCanceledException)
            {
                LogService.Info($"列表缩略图刷新取消：页面=模组库，已处理={processed}，耗时={clock.ElapsedMilliseconds}ms，版本={version}。");
            }
            finally
            {
                if (ReferenceEquals(_thumbnailCancellation, cancellation))
                {
                    _thumbnailCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var changedIds = e.GetChangedIds(SelectionScope);
            ApplyLocalSelection(e.GetCurrentIds(SelectionScope), changedIds);
        }

        private void OnProfileChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(
                requiresRebuild: _hideSelectedProfileMembers,
                refreshVisibleContent: true);

        private void OnLibraryContentFactsChanged(object? sender, ModContentFactsChangedEventArgs e)
            => QueueProjectionRefresh(
                e.NodeIds,
                requiresRebuild: e.Kind is ModContentChangeKind.Added or ModContentChangeKind.Removed);

        private void OnLibrarySnapshotChanged(object? sender, EventArgs e)
            => QueueProjectionRefresh(requiresRebuild: true);

        private void OnDerivedStateSnapshotChanged(object? sender, DerivedStateSnapshot e)
            => QueueProjectionRefresh(refreshVisibleContent: true);

        private void QueueProjectionRefresh(
            IEnumerable<ModNodeId>? nodeIds = null,
            bool requiresRebuild = false,
            bool refreshVisibleContent = false)
        {
            if (_disposed) return;
            lock (_pendingProjectionGuids)
            {
                if (nodeIds is not null)
                    foreach (var nodeId in nodeIds) _pendingProjectionGuids.Add(nodeId.Value.ToString("N"));
                _projectionRequiresRebuild |= requiresRebuild;
                _projectionRefreshVisibleContent |= refreshVisibleContent;
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
            lock (_pendingProjectionGuids)
            {
                guids = new HashSet<string>(_pendingProjectionGuids, StringComparer.OrdinalIgnoreCase);
                _pendingProjectionGuids.Clear();
                requiresRebuild = _projectionRequiresRebuild;
                _projectionRequiresRebuild = false;
                refreshVisibleContent = _projectionRefreshVisibleContent;
                _projectionRefreshVisibleContent = false;
            }
            Interlocked.Exchange(ref _projectionRefreshQueued, 0);
            UiPerformanceDiagnostics.RecordBurst(
                "Library.ApplyProjection",
                $"guids={guids.Count}; rebuild={requiresRebuild}; visibleContent={refreshVisibleContent}",
                threshold: 8);
            using var perf = UiPerformanceDiagnostics.Measure(
                "Library.ApplyProjection",
                thresholdMilliseconds: 25,
                details: () => $"guids={guids.Count}; rebuild={requiresRebuild}; visibleContent={refreshVisibleContent}; items={Items.Count}");
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
            else QueueThumbnailWarmup();
        }

        private void RefreshVisibleThumbnailSources(IReadOnlyCollection<string> refreshedIds)
        {
            if (_disposed) return;
            PublishContent(refreshedIds);
        }

        private void ApplyThumbnailResults(
            int version,
            CancellationTokenSource cancellation,
            IReadOnlyDictionary<string, string?> completedSources,
            IReadOnlyCollection<string> refreshedIds)
        {
            if (!IsCurrentThumbnailRefresh(version, cancellation)) return;
            var changed = false;
            foreach (var pair in completedSources)
            {
                if (_thumbnailSources.TryGetValue(pair.Key, out var existing)
                    && string.Equals(existing, pair.Value, StringComparison.OrdinalIgnoreCase))
                    continue;
                _thumbnailSources[pair.Key] = pair.Value;
                changed = true;
            }
            foreach (var guid in refreshedIds)
            {
                _thumbnailRevisions[guid] = _thumbnailRevisions.GetValueOrDefault(guid) + 1;
                changed = true;
            }

            if (changed) RefreshVisibleThumbnailSources(refreshedIds);
        }

        private void PublishVisibleContent()
        {
            if (_disposed) return;
            var states = Items
                .Select(card => _library.Get(card.Mod.Guid))
                .Where(mod => mod is not null)
                .Cast<HD2ModManager.Models.ModEntity>()
                .Select(CreateListState)
                .ToArray();
            _list.SubmitContent(states);
        }

        private void PublishContent(IEnumerable<string> guids, bool commitImmediately = false)
        {
            if (_disposed) return;
            var states = guids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(_library.Get)
                .Where(mod => mod is not null)
                .Cast<HD2ModManager.Models.ModEntity>()
                .Select(CreateListState)
                .ToArray();
            _list.SubmitContent(states, commitImmediately);
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
                var deletion = await _library.RemoveManyAsync(targets);
                _notifications?.Show(
                    ModLibraryService.DescribeDeletionResult(deletion),
                    deletion.HasFailures ? NotificationLevel.Warning : NotificationLevel.Info);
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
            var next = _selectedGuids.ToList();
            if (!next.Remove(card.Mod.Guid)) next.Add(card.Mod.Guid);
            if (_selection is null) ApplyLocalSelection(next);
            else _selection.Replace(SelectionScope, next);
        }

        private bool IsSelected(string guid) => _selectedGuids.Any(id => string.Equals(id, guid, System.StringComparison.OrdinalIgnoreCase));

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

        private LibraryModListState CreateListState(HD2ModManager.Models.ModEntity mod)
        {
            var derived = mod.IsDecoration || mod.IsOption || mod.IsReShade
                ? null
                : _library.GetDerivedData(mod.Guid);
            _userStatuses.TryGetValue(mod.Guid, out var status);
            _thumbnailSources.TryGetValue(mod.Guid, out var thumbnailSource);
            var decorationStatus = default(string);
            var enabledHostCount = 0;
            var availableHostCount = 0;
            if (mod.IsDecoration)
            {
                var activation = _library.GetDecorationActivationSummary(mod.Guid);
                decorationStatus = activation.StatusText;
                enabledHostCount = activation.EnabledHostCount;
                availableHostCount = activation.AvailableHostCount;
            }
            else if (mod.IsOption)
            {
                var activation = _library.GetOptionActivationSummary(mod.Guid);
                decorationStatus = activation.StatusText;
                enabledHostCount = activation.EnabledHostCount;
                availableHostCount = activation.AvailableHostCount;
            }
            var selected = IsSelected(mod.Guid);
            var assetSummaryText = ModCardViewModel.AppendDirectOptionSummary(
                ModCardViewModel.BuildAssetSummaryText(mod, derived?.AssetSummary),
                _directOptionCounts.GetValueOrDefault(mod.Guid));
            var (activationLabel, activationTone) = mod.IsDecoration || mod.IsOption
                ? BuildAttachmentActivationPresentation(enabledHostCount, availableHostCount)
                : (null, ModListRowTone.Muted);
            var rowPresentation = ModCardViewModel.BuildRowPresentation(
                mod,
                assetSummaryText,
                derived?.UnitCompatibility,
                status,
                activationLabel,
                activationTone);
            var contentVersion = LibraryModListState.BuildContentVersion(
                mod,
                assetSummaryText,
                rowPresentation,
                derived?.UnitCompatibility,
                status,
                thumbnailSource,
                decorationStatus,
                _thumbnailRevisions.GetValueOrDefault(mod.Guid));
            return new LibraryModListState(
                mod.Guid,
                mod,
                selected,
                derived?.AssetSummary,
                derived?.UnitCompatibility,
                status,
                thumbnailSource,
                decorationStatus,
                _thumbnailRevisions.GetValueOrDefault(mod.Guid),
                assetSummaryText,
                rowPresentation,
                contentVersion,
                LibraryModListState.BuildPresentationVersion(
                    contentVersion,
                    selected));
        }

        private static (string? Label, ModListRowTone Tone) BuildAttachmentActivationPresentation(int enabledHostCount, int availableHostCount)
        {
            if (availableHostCount == 0) return ("无可用主体", ModListRowTone.Warning);
            if (enabledHostCount == 0) return ("尚未启用", ModListRowTone.Muted);
            if (enabledHostCount == availableHostCount) return ($"已为 {enabledHostCount} 个主体启用", ModListRowTone.Success);
            return ($"已为 {enabledHostCount}/{availableHostCount} 个主体启用", ModListRowTone.Accent);
        }

        private void RefreshDirectOptionCounts()
        {
            _directOptionCounts.Clear();
            foreach (var pair in _library.GetDirectOptionCounts()) _directOptionCounts[pair.Key] = pair.Value;
        }

        private void OnListProjectionCommitted(object? sender, ModListStateCommittedEventArgs e)
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ItemCountText));
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
            _list.Committed -= OnListProjectionCommitted;
            _list.Dispose();
        }
    }

    internal sealed record LibraryModListState(
        string Guid,
        HD2ModManager.Models.ModEntity Mod,
        bool IsSelected,
        ModAssetSummary? AssetSummary,
        ModUnitCompatibilityReport? UnitCompatibility,
        ModUserStatus? UserStatus,
        string? ThumbnailSourcePath,
        string? DecorationStatus,
        int ThumbnailRevision,
        string AssetSummaryText,
        ModListRowPresentation RowPresentation,
        string ContentVersion,
        string PresentationVersion)
    {
        public static string BuildContentVersion(
            HD2ModManager.Models.ModEntity mod,
            string assetSummaryText,
            ModListRowPresentation rowPresentation,
            ModUnitCompatibilityReport? unitCompatibility,
            ModUserStatus? userStatus,
            string? thumbnailSourcePath,
            string? decorationStatus,
            int thumbnailRevision)
            => string.Join('\u001f',
                mod.Guid,
                mod.Name,
                mod.Description,
                mod.Image,
                mod.SourcePath,
                mod.IsDecoration,
                mod.IsOption,
                mod.IsReShade,
                mod.HasPatchContent,
                mod.IsHelmetToArmorMerged,
                assetSummaryText,
                rowPresentation.TypeLabel,
                rowPresentation.TypeTone,
                rowPresentation.ActivationLabel,
                rowPresentation.ActivationTone,
                rowPresentation.WarningLabel,
                rowPresentation.WarningTone,
                rowPresentation.Summary,
                rowPresentation.OrderLabel,
                rowPresentation.OrderTone,
                rowPresentation.Note,
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
                thumbnailSourcePath,
                decorationStatus,
                thumbnailRevision);

        public static string BuildPresentationVersion(string contentVersion, bool isSelected)
            => string.Join('\u001f', contentVersion, isSelected);
    }

    public class ModCardViewModel : BaseViewModel, IModListSelectable, IModListInternalReorderable
    {
        private HD2ModManager.Models.ModEntity _mod;
        private ModAssetSummary? _assetSummary;
        private ModUnitCompatibilityReport? _unitCompatibility;
        private ModUserStatus? _userStatus;
        private string? _decorationStatus;
        private string _assetSummaryText = string.Empty;
        private ModListRowPresentation _rowPresentation = ModListRowPresentation.Empty;
        private string _contentVersion = string.Empty;
        public HD2ModManager.Models.ModEntity Mod => _mod;
        public string SelectionKey => Mod.Guid;
        public bool IsDecoration => Mod.IsDecoration;
        public bool IsOption => Mod.IsOption;
        public bool IsReShade => Mod.IsReShade;
        public bool IsAttachable => Mod.IsDecoration || Mod.IsOption;
        public bool CanInternalReorder => Mod.IsOption;
        public bool CanJoinProfile => Mod.Capabilities.CanJoinProfile;
        private string? _thumbnailSourcePath;
        private string? _imagePath;
        private int _thumbnailRevision;
        public ModAssetSummary? AssetSummary => _assetSummary;
        public string Name => Mod.Name;
        public string AssetSummaryText => _assetSummaryText;
        public ModListRowPresentation RowPresentation => _rowPresentation;
        public bool ShowsPatchMetadata => Mod.Capabilities.ShowsPatchAssets;
        public string? ImagePath => _imagePath;
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

        public ModCardViewModel(
            HD2ModManager.Models.ModEntity mod,
            bool isSelected = false,
            ModAssetSummary? assetSummary = null,
            ModUnitCompatibilityReport? unitCompatibility = null,
            ModUserStatus? userStatus = null,
            string? thumbnailSourcePath = null,
            string? decorationStatus = null,
            ModListRowPresentation? rowPresentation = null)
        {
            _mod = mod;
            _thumbnailSourcePath = thumbnailSourcePath ?? mod.Image;
            _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
            _assetSummary = assetSummary;
			_unitCompatibility = unitCompatibility;
            _userStatus = userStatus;
            _decorationStatus = decorationStatus;
            _isSelected = isSelected;
            _assetSummaryText = BuildAssetSummaryText(mod, assetSummary);
            _rowPresentation = rowPresentation ?? BuildRowPresentation(mod, _assetSummaryText, unitCompatibility, userStatus);
        }

        internal ModCardViewModel(LibraryModListState state)
            : this(
                state.Mod,
                state.IsSelected,
                state.AssetSummary,
                state.UnitCompatibility,
                state.UserStatus,
                state.ThumbnailSourcePath,
                state.DecorationStatus,
                state.RowPresentation)
        {
            _assetSummaryText = state.AssetSummaryText;
            _rowPresentation = state.RowPresentation;
            _contentVersion = state.ContentVersion;
            _thumbnailRevision = state.ThumbnailRevision;
            _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
        }

        internal void ApplyListState(LibraryModListState state)
        {
            var contentChanged = !string.Equals(_contentVersion, state.ContentVersion, StringComparison.Ordinal);
            IsSelected = state.IsSelected;
            if (!contentChanged) return;

            var nextThumbnailSource = state.ThumbnailSourcePath ?? state.Mod.Image;
            var imageChanged = !string.Equals(_thumbnailSourcePath, nextThumbnailSource, StringComparison.OrdinalIgnoreCase)
                || _thumbnailRevision != state.ThumbnailRevision;
            _mod = state.Mod;
            _assetSummary = state.AssetSummary;
            _unitCompatibility = state.UnitCompatibility;
            _userStatus = state.UserStatus;
            _decorationStatus = state.DecorationStatus;
            _thumbnailSourcePath = nextThumbnailSource;
            _thumbnailRevision = state.ThumbnailRevision;
            if (imageChanged) _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
            _assetSummaryText = state.AssetSummaryText;
            _rowPresentation = state.RowPresentation;
            _contentVersion = state.ContentVersion;
            OnPropertyChanged(nameof(Mod)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(IsDecoration)); OnPropertyChanged(nameof(IsOption)); OnPropertyChanged(nameof(IsReShade)); OnPropertyChanged(nameof(IsAttachable)); OnPropertyChanged(nameof(CanInternalReorder)); OnPropertyChanged(nameof(CanJoinProfile));
            OnPropertyChanged(nameof(AssetSummary)); OnPropertyChanged(nameof(AssetSummaryText)); OnPropertyChanged(nameof(RowPresentation)); OnPropertyChanged(nameof(UnitCompatibility));
            OnPropertyChanged(nameof(IsModelOutdated)); OnPropertyChanged(nameof(UserStatus)); OnPropertyChanged(nameof(UserStatusTitle));
            OnPropertyChanged(nameof(UserStatusSummary)); OnPropertyChanged(nameof(DecorationStatus));
            if (imageChanged) OnPropertyChanged(nameof(ImagePath));
        }

        internal void ApplyAttachmentState(
            HD2ModManager.Models.ModEntity mod,
            bool isSelected,
            string? decorationStatus,
            ModListRowPresentation rowPresentation,
            string contentVersion)
        {
            var contentChanged = !string.Equals(_contentVersion, contentVersion, StringComparison.Ordinal);
            IsSelected = isSelected;
            if (!contentChanged) return;

            var nextThumbnailSource = mod.Image;
            var imageChanged = !string.Equals(_thumbnailSourcePath, nextThumbnailSource, StringComparison.OrdinalIgnoreCase);
            _mod = mod;
            _assetSummary = null;
            _unitCompatibility = null;
            _userStatus = null;
            _decorationStatus = decorationStatus;
            _thumbnailSourcePath = nextThumbnailSource;
            if (imageChanged) _imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
            _assetSummaryText = BuildAssetSummaryText(mod, null);
            _rowPresentation = rowPresentation;
            _contentVersion = contentVersion;

            OnPropertyChanged(nameof(Mod)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(IsDecoration)); OnPropertyChanged(nameof(IsOption)); OnPropertyChanged(nameof(IsReShade));
            OnPropertyChanged(nameof(IsAttachable)); OnPropertyChanged(nameof(CanInternalReorder)); OnPropertyChanged(nameof(CanJoinProfile)); OnPropertyChanged(nameof(ShowsPatchMetadata));
            OnPropertyChanged(nameof(AssetSummary)); OnPropertyChanged(nameof(AssetSummaryText)); OnPropertyChanged(nameof(RowPresentation)); OnPropertyChanged(nameof(UnitCompatibility));
            OnPropertyChanged(nameof(IsModelOutdated)); OnPropertyChanged(nameof(ModelCompatibilitySummary));
            OnPropertyChanged(nameof(UserStatus)); OnPropertyChanged(nameof(UserStatusTitle)); OnPropertyChanged(nameof(UserStatusSummary));
            OnPropertyChanged(nameof(HasUserStatus)); OnPropertyChanged(nameof(DecorationStatus));
            if (imageChanged) OnPropertyChanged(nameof(ImagePath));
        }

        internal static string BuildAssetSummaryText(HD2ModManager.Models.ModEntity mod, ModAssetSummary? assetSummary)
            => mod.IsReShade
                ? "部署到游戏 bin"
                : !mod.HasPatchContent && !mod.IsDecoration && !mod.IsOption
                    ? string.Empty
                    : mod.IsDecoration || mod.IsOption
                        ? mod.HasPatchContent ? "包含 Patch 内容" : "逻辑附加项"
                        : ModAssetSummaryFormatter.Format(assetSummary);

        internal static string AppendDirectOptionSummary(string? summary, int directOptionCount)
        {
            if (directOptionCount <= 0) return summary ?? string.Empty;
            var optionSummary = $"包含 {directOptionCount} 个选项";
            return string.IsNullOrWhiteSpace(summary) ? optionSummary : $"{summary} · {optionSummary}";
        }

        internal static ModListRowPresentation BuildRowPresentation(
            HD2ModManager.Models.ModEntity mod,
            string? summary,
            ModUnitCompatibilityReport? unitCompatibility,
            ModUserStatus? userStatus,
            string? activationLabel = null,
            ModListRowTone activationTone = ModListRowTone.Muted,
            string? orderLabel = null,
            ModListRowTone orderTone = ModListRowTone.Accent)
        {
            var typeLabel = mod.IsReShade ? "ReShade" : mod.IsDecoration ? "装饰" : mod.IsOption ? "选项" : null;
            var typeTone = mod.IsReShade ? ModListRowTone.Accent : ModListRowTone.Muted;
            if (string.IsNullOrWhiteSpace(activationLabel) && userStatus is not null)
            {
                (activationLabel, activationTone) = userStatus.Kind switch
                {
                    ModUserStatusKind.Stored => ("仅存储", ModListRowTone.Muted),
                    _ => ("已启用", ModListRowTone.Success),
                };
            }

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
                typeLabel,
                typeTone,
                activationLabel,
                activationTone,
                warnings.Count == 0 ? null : string.Join(" · ", warnings),
                warningTone,
                summary,
                orderLabel,
                orderTone,
                mod.Description);
        }

    }

}
