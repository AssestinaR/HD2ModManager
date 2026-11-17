using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    // 作用：在工作区左槽按需加载并筛选持久化 Game Data archive 索引。
    public sealed class GameDataBrowserPageViewModel : PageViewModel
    {
        private readonly ModLibraryService _library;
        private readonly ProfileService _profiles;
        private readonly IModInformationCenter _informationCenter;
        private readonly IAssetArchiveIndexService _index;
        private CancellationTokenSource? _loadCancellation;
        private CancellationTokenSource? _detailsCancellation;
        private string _searchText = string.Empty;
        private string _statusFilter = "全部";
        private string _categoryFilter = "全部";
        private GameDataArchiveRowViewModel? _selectedArchive;
        private GameDataArchiveDetailsPageViewModel? _selectedDetails;
        private bool _isLoading;
        private string _loadState = "正在准备资产浏览。";

        public BulkObservableCollection<GameDataArchiveRowViewModel> Archives { get; } = new();
        public ICollectionView ArchivesView { get; }
        public IReadOnlyList<string> StatusFilters { get; } = new[] { "全部", "未替换", "Mod 库中存在", "当前配置启用", "已生效", "竞争生效", "异常" };
        public ObservableCollection<string> CategoryFilters { get; } = new() { "全部" };
        public string Summary { get; private set; } = "尚未读取资产索引。";
        public string BuiltText { get; private set; } = string.Empty;
        public string SourceText { get; private set; } = string.Empty;
        public string ProfileText => $"当前配置：{_profiles.ActiveKey ?? "未启用"}";
        public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }
        public string LoadState { get => _loadState; private set => SetField(ref _loadState, value); }
        public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) ArchivesView.Refresh(); } }
        public string StatusFilter { get => _statusFilter; set { if (SetField(ref _statusFilter, value)) ArchivesView.Refresh(); } }
        public string CategoryFilter { get => _categoryFilter; set { if (SetField(ref _categoryFilter, value)) ArchivesView.Refresh(); } }
        public GameDataArchiveRowViewModel? SelectedArchive
        {
            get => _selectedArchive;
            set
            {
                if (SetField(ref _selectedArchive, value)) _ = LoadSelectedDetailsAsync(value);
            }
        }
        public GameDataArchiveDetailsPageViewModel? SelectedDetails { get => _selectedDetails; private set => SetField(ref _selectedDetails, value); }

        public GameDataBrowserPageViewModel(ModLibraryService library, ProfileService profiles, IModInformationCenter informationCenter)
        {
            Title = "Game Data 资产";
            _library = library;
            _profiles = profiles;
            _informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
            _index = CoreServices.CreateAssetArchiveIndexService(SettingsService.CreateStoragePaths());
            ArchivesView = CollectionViewSource.GetDefaultView(Archives);
            ArchivesView.Filter = FilterArchive;
            ArchivesView.SortDescriptions.Add(new(nameof(GameDataArchiveRowViewModel.Category), ListSortDirection.Ascending));
            ArchivesView.SortDescriptions.Add(new(nameof(GameDataArchiveRowViewModel.DisplayName), ListSortDirection.Ascending));
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            var loadCancellation = new CancellationTokenSource();
            _loadCancellation = loadCancellation;
            var cancellationToken = loadCancellation.Token;
            IsLoading = true;
            LoadState = "正在读取 archive 索引摘要。";
            try
            {
                var gameData = SettingsService.GetGameDataFolder();
                if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
                {
                    LoadState = "请先在设置中配置有效的 Game Data 目录。";
                    return;
                }

                var browser = CoreServices.CreateGameDataArchiveBrowserService(SettingsService.CreateStoragePaths(), _informationCenter);
                var snapshot = await browser.BuildAsync(_library.Snapshot, _library.ModsRootDirectory, gameData, cancellationToken);
                if (!ReferenceEquals(_loadCancellation, loadCancellation) || cancellationToken.IsCancellationRequested) return;
                if (snapshot is null || snapshot.Archives.Count == 0)
                {
                    LoadState = "当前资产索引不可用或没有 archive；请先在设置中建立资产索引。";
                    return;
                }

                var archiveRows = snapshot.Archives
                    .Select(item => new GameDataArchiveRowViewModel(item, snapshot.ModNames))
                    .ToList();
                Archives.ReplaceWith(archiveRows);
                CategoryFilters.Clear();
                CategoryFilters.Add("全部");
                foreach (var category in archiveRows.Select(row => row.Category).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) CategoryFilters.Add(category);
                ResetFilters();
                Summary = $"Archive {snapshot.Fingerprint.ArchivesIndexed}/{snapshot.Fingerprint.ArchivesTotal}，AssetKey {snapshot.Fingerprint.AssetKeysTotal} · 已生效 {archiveRows.Count(row => row.ReplacementStatus == "已生效")} · 竞争 {archiveRows.Count(row => row.ReplacementStatus == "竞争生效")} · 异常 {archiveRows.Count(row => row.ReplacementStatus == "异常")}";
                BuiltText = $"构建时间：{snapshot.Fingerprint.BuiltUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                SourceText = $"来源：{snapshot.Fingerprint.GameDataDirectory}";
                LoadState = "选择左侧 archive 查看 AssetKey 详情。";
                OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(BuiltText)); OnPropertyChanged(nameof(SourceText)); OnPropertyChanged(nameof(ProfileText));
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { LoadState = $"读取 Game Data 资产索引失败：{exception.Message}"; }
            finally
            {
                if (ReferenceEquals(_loadCancellation, loadCancellation))
                {
                    IsLoading = false;
                    _loadCancellation = null;
                }
                loadCancellation.Dispose();
            }
        }

        private async Task LoadSelectedDetailsAsync(GameDataArchiveRowViewModel? row)
        {
            _detailsCancellation?.Cancel(); _detailsCancellation?.Dispose();
            if (row is null) { SelectedDetails = null; return; }
            var detailsCancellation = new CancellationTokenSource();
            _detailsCancellation = detailsCancellation;
            var cancellationToken = detailsCancellation.Token;
            SelectedDetails = GameDataArchiveDetailsPageViewModel.Loading(row);
            try
            {
                var details = await _index.GetArchiveDetailsAsync(row.PackageName, cancellationToken);
                if (ReferenceEquals(_detailsCancellation, detailsCancellation) && !cancellationToken.IsCancellationRequested)
                    SelectedDetails = details is null ? GameDataArchiveDetailsPageViewModel.NotFound(row) : new GameDataArchiveDetailsPageViewModel(details, row);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (ReferenceEquals(_detailsCancellation, detailsCancellation) && !cancellationToken.IsCancellationRequested)
                    SelectedDetails = GameDataArchiveDetailsPageViewModel.Failed(row, exception.Message);
            }
            finally
            {
                if (ReferenceEquals(_detailsCancellation, detailsCancellation)) _detailsCancellation = null;
                detailsCancellation.Dispose();
            }
        }

        public void Sort(string propertyName)
        {
            var current = ArchivesView.SortDescriptions.FirstOrDefault(item => item.PropertyName == propertyName);
            var direction = current.PropertyName == propertyName && current.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            ArchivesView.SortDescriptions.Clear(); ArchivesView.SortDescriptions.Add(new(propertyName, direction));
        }

        private void ResetFilters()
        {
            _categoryFilter = "全部";
            _statusFilter = "全部";
            OnPropertyChanged(nameof(CategoryFilter));
            OnPropertyChanged(nameof(StatusFilter));
            ArchivesView.Refresh();
        }

        private bool FilterArchive(object value)
        {
            if (value is not GameDataArchiveRowViewModel row) return false;
            if (StatusFilter != "全部" && StatusFilter != row.ReplacementStatus) return false;
            if (CategoryFilter != "全部" && !string.Equals(CategoryFilter, row.Category, StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(SearchText) || row.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || row.PackageName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || row.EffectiveMod.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        public override void Dispose()
        {
            _loadCancellation?.Cancel(); _loadCancellation?.Dispose();
            _detailsCancellation?.Cancel(); _detailsCancellation?.Dispose();
        }
    }

    // 作用：承载右槽选中 archive 的详情、复制状态与 AssetKey 表数据。
    public sealed class GameDataArchiveDetailsPageViewModel : BaseViewModel
    {
        private string _copyStatus = string.Empty;
        private string _searchText = string.Empty;
        private string _typeFilter = "全部";
        private string _replacementFilter = "全部";
        public string DisplayName { get; private set; } = "未选择 archive";
        public string PackageName { get; private set; } = "—";
        public string Category { get; private set; } = "—";
        public string ReplacementStatus { get; private set; } = "—";
        public string EffectivePatchGroup { get; private set; } = "—";
        public string EffectiveMod { get; private set; } = "—";
        public string ContentSummary { get; private set; } = "从左侧选择 archive 后显示 AssetKey。";
        public string LoadState { get; private set; } = "等待选择";
        public BulkObservableCollection<GameDataArchiveAssetRowViewModel> Assets { get; } = new();
        public ICollectionView AssetsView { get; }
        public ObservableCollection<string> TypeFilters { get; } = new() { "全部" };
        public IReadOnlyList<string> ReplacementFilters { get; } = new[] { "全部", "已生效", "未生效" };
        public string CopyStatus { get => _copyStatus; private set => SetField(ref _copyStatus, value); }
        public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) AssetsView.Refresh(); } }
        public string TypeFilter { get => _typeFilter; set { if (SetField(ref _typeFilter, value)) AssetsView.Refresh(); } }
        public string ReplacementFilter { get => _replacementFilter; set { if (SetField(ref _replacementFilter, value)) AssetsView.Refresh(); } }

        public GameDataArchiveDetailsPageViewModel()
        {
            AssetsView = CollectionViewSource.GetDefaultView(Assets);
            AssetsView.Filter = FilterAsset;
        }
        public GameDataArchiveDetailsPageViewModel(GameDataArchiveDetails details, GameDataArchiveRowViewModel row) : this()
        {
            DisplayName = row.DisplayName; PackageName = row.PackageName; Category = row.Category; ReplacementStatus = row.ReplacementStatus; EffectivePatchGroup = row.EffectivePatchGroup; EffectiveMod = row.EffectiveMod;
            var assetRows = details.Assets
                .Select(asset => new GameDataArchiveAssetRowViewModel(asset, row))
                .ToList();
            Assets.ReplaceWith(assetRows);
            foreach (var typeName in assetRows.Select(asset => asset.TypeName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) TypeFilters.Add(typeName);
            ContentSummary = $"共 {assetRows.Count} 个 AssetKey"; LoadState = "详情已加载。";
        }
        public static GameDataArchiveDetailsPageViewModel Loading(GameDataArchiveRowViewModel row) => CreateState(row, "正在读取 AssetKey 详情。");
        public static GameDataArchiveDetailsPageViewModel NotFound(GameDataArchiveRowViewModel row) => CreateState(row, "未找到该 archive 的详情。");
        public static GameDataArchiveDetailsPageViewModel Failed(GameDataArchiveRowViewModel row, string message) => CreateState(row, $"读取详情失败：{message}");
        private static GameDataArchiveDetailsPageViewModel CreateState(GameDataArchiveRowViewModel row, string state) => new() { DisplayName = row.DisplayName, PackageName = row.PackageName, Category = row.Category, ReplacementStatus = row.ReplacementStatus, EffectivePatchGroup = row.EffectivePatchGroup, EffectiveMod = row.EffectiveMod, LoadState = state, ContentSummary = state };
        public void CopyAsset(GameDataArchiveAssetRowViewModel row)
        {
            var text = $"Type={row.TypeName}\nTypeID={row.TypeId}\nFileID={row.FileId}\nPart={row.PartSummary}\nPackage={PackageName}\nDisplayName={DisplayName}\nEffectivePatchGroup={row.EffectivePatchGroup}\nEffectiveMod={row.EffectiveMod}\nSharedPackages={string.Join(", ", row.SharedPackages)}\nSharedObjects={string.Join(", ", row.SharedObjects)}";
            CopyStatus = ClipboardService.TrySetText(text)
                ? "已复制 AssetKey 信息"
                : "复制失败：剪贴板正被其他程序占用，请稍后重试";
        }
        private bool FilterAsset(object value)
        {
            if (value is not GameDataArchiveAssetRowViewModel asset) return false;
            if (TypeFilter != "全部" && !string.Equals(TypeFilter, asset.TypeName, StringComparison.OrdinalIgnoreCase)) return false;
            if (ReplacementFilter != "全部" && ReplacementFilter != asset.ReplacementStatus) return false;
            return string.IsNullOrWhiteSpace(SearchText) || asset.TypeName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.Hash.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.FriendlyName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.PartSummary.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.EffectiveMod.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.SharedPackagesFullText.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || asset.SharedObjectsFullText.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // 作用：将左槽浏览器的异步选中详情投影到固定右槽，且不重新创建 archive 列表。
    public sealed class GameDataArchiveDetailsHostPageViewModel : PageViewModel
    {
        private readonly GameDataBrowserPageViewModel? _browser;
        public GameDataArchiveDetailsPageViewModel Details => _browser?.SelectedDetails ?? new GameDataArchiveDetailsPageViewModel();

        public GameDataArchiveDetailsHostPageViewModel(GameDataBrowserPageViewModel? browser)
        {
            Title = "Archive 详情";
            _browser = browser;
            if (_browser is not null)
            {
                _browser.PropertyChanged += OnBrowserPropertyChanged;
            }
        }

        private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GameDataBrowserPageViewModel.SelectedDetails)) OnPropertyChanged(nameof(Details));
        }

        public override void Dispose()
        {
            if (_browser is not null) _browser.PropertyChanged -= OnBrowserPropertyChanged;
        }
    }

    // 作用：投影 archive 的索引摘要、替换状态与当前配置中的有效 patch 信息。
    public sealed class GameDataArchiveRowViewModel
    {
        public string PackageName { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public int EntryCount { get; }
        public string ReplacementStatus { get; }
        public string EffectivePatchGroup { get; }
        public string EffectiveMod { get; }
        internal IReadOnlyDictionary<AssetKey, EffectivePatch> EffectiveByAsset { get; }

        internal GameDataArchiveRowViewModel(GameDataArchiveBrowserItem item, IReadOnlyDictionary<ModNodeId, string> modNames)
        {
            var archive = item.Archive;
            var overlay = item.Overlay;
            PackageName = archive.PackageName; DisplayName = archive.DisplayName; Category = archive.Category; EntryCount = archive.EntryCount;
            ReplacementStatus = archive.Status == "存在问题" || overlay.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error) ? "异常"
                : overlay.HasEffectiveReplacement ? overlay.HasCompetition ? "竞争生效" : "已生效"
                : overlay.HasActiveReplacement ? "当前配置启用" : overlay.HasLibraryReplacement ? "Mod 库中存在" : "未替换";
            EffectivePatchGroup = overlay.EffectiveTargetPatchIndexes.Count == 0 ? "—" : overlay.EffectiveTargetPatchIndexes.Count == 1 ? $"目标 patch_{overlay.EffectiveTargetPatchIndexes[0]}" : $"{overlay.EffectiveTargetPatchIndexes.Count} 个 patch 组";
            var effectiveNames = overlay.EffectiveModIds.Select(id => modNames.GetValueOrDefault(id) ?? "未知 Mod").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var activeNames = overlay.ActiveModIds.Select(id => modNames.GetValueOrDefault(id) ?? "未知 Mod").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var libraryNames = overlay.LibraryModIds.Select(id => modNames.GetValueOrDefault(id) ?? "未知 Mod").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = effectiveNames.Count > 0 ? effectiveNames : activeNames.Count > 0 ? activeNames : libraryNames;
            EffectiveMod = names.Count == 0 ? "—" : names.Count == 1 ? names[0] : $"{names.Count} 个 Mod";
            EffectiveByAsset = overlay.EffectiveAssets.GroupBy(asset => asset.AssetKey).ToDictionary(
                group => group.Key,
                group =>
                {
                    var winner = group.OrderBy(asset => asset.TargetPatchIndex).Last();
                    var modName = winner.WinnerNodeId is { } nodeId ? modNames.GetValueOrDefault(nodeId) ?? "未知 Mod" : "未知 Mod";
                    return new EffectivePatch(modName, $"目标 patch_{winner.TargetPatchIndex}", winner.TargetPatchIndex, winner.AssetKey, winner.HasCompetition);
                });
        }
    }

    // 作用：投影 archive 详情中的单个 AssetKey、共享对象和最终覆盖信息。
    public sealed class GameDataArchiveAssetRowViewModel
    {
        public string TypeName { get; }
        public string TypeId { get; }
        public string FileId { get; }
        public string Hash => $"{TypeId} / {FileId}";
        public string FriendlyName { get; }
        public string PartSummary { get; }
        public string EffectivePatchGroup { get; }
        public string EffectiveMod { get; }
        public string ReplacementStatus => EffectivePatchGroup == "—" ? "未生效" : "已生效";
        public IReadOnlyList<string> SharedPackages { get; }
        public IReadOnlyList<string> SharedObjects { get; }
        public string SharedPackagesText => SharedPackages.Count == 0 ? "—" : SharedPackages.Count == 1 ? SharedPackages[0] : $"{SharedPackages.Count} 个 Package";
        public string SharedObjectsText => SharedObjects.Count == 0 ? "—" : SharedObjects.Count == 1 ? SharedObjects[0] : $"{SharedObjects.Count} 个对象";
        public string SharedPackagesFullText => SharedPackages.Count == 0 ? "—" : string.Join("\n", SharedPackages);
        public string SharedObjectsFullText => SharedObjects.Count == 0 ? "—" : string.Join("\n", SharedObjects);

        public GameDataArchiveAssetRowViewModel(GameDataArchiveAssetEntry asset, GameDataArchiveRowViewModel archive)
        {
            TypeName = asset.TypeName; TypeId = asset.AssetKey.TypeId.ToString("x16"); FileId = asset.AssetKey.FileId.ToString("x16"); FriendlyName = asset.FriendlyName; PartSummary = asset.PartSummary;
            if (archive.EffectiveByAsset.TryGetValue(asset.AssetKey, out var effective)) { EffectivePatchGroup = effective.PatchGroup; EffectiveMod = effective.ModName; }
            else { EffectivePatchGroup = "—"; EffectiveMod = "—"; }
            SharedPackages = asset.SharedPackages; SharedObjects = asset.SharedDisplayNames;
        }
    }

    internal sealed record EffectivePatch(string ModName, string PatchGroup, int LoadOrder, AssetKey AssetKey, bool HasCompetition);
}