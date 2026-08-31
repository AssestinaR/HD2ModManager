using System.IO;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 作用：承载多 Mod / 多 Patch 组的 AssetKey 选择、冲突处理与 Patch 资源重打包。
public sealed class PatchResourceRepackagingPageViewModel : PageViewModel
{
    private readonly ModLibraryService _library;
    private readonly NotificationService? _notifications;
    private readonly BackgroundTaskService? _backgroundTasks;
    private readonly IPatchResourceRepackagingService _repackaging;
    private readonly string _sourceModId;
    private readonly List<PatchResourceRepackagingModItem> _allModItems = [];
    private readonly List<PatchResourceAssetRowViewModel> _allAssetRows = [];
    private readonly ModListItemProjectionController<PatchResourceRepackagingModItem> _sourceMods;
    private readonly Dictionary<AssetKey, int> _conflictCursors = new();
    private CancellationTokenSource? _inspectionCancellation;
    private PatchResourceRepackagingInspection? _inspection;
    private int _inspectionGeneration;
    private string _modQuery = string.Empty;
    private string _query = string.Empty;
    private bool _showOnlyConflicts;
    private string _outputDirectory = SettingsService.GetResolvedDefaultExportPath();
    private string _state = "正在读取可重打包的 Mod。";
    private string _patchGroupSummary = string.Empty;
    private bool _isInspecting;
    private bool _isGenerating;
    private bool _autoImport;

    public PatchResourceRepackagingPageViewModel(
        ModLibraryService library,
        string sourceModId,
        NotificationService? notifications = null,
        BackgroundTaskService? backgroundTasks = null)
    {
        _library = library;
        _sourceModId = sourceModId;
        _notifications = notifications;
        _backgroundTasks = backgroundTasks;
        _repackaging = CoreServices.CreatePatchResourceRepackagingService();
        Title = "Patch 资源重打包";

        _sourceMods = new ModListItemProjectionController<PatchResourceRepackagingModItem>(item => item.SelectionKey);
        AssetRows = new BulkObservableCollection<PatchResourceAssetRowViewModel>(item => item.SelectionKey);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        SelectAllModsCommand = new RelayCommand(SelectAllMods);
        ClearModSelectionCommand = new RelayCommand(ClearModSelection);
        SelectAllAssetsCommand = new RelayCommand(SelectAllAssets);
        ClearAssetSelectionCommand = new RelayCommand(ClearAssetSelection);
        InvertAssetSelectionCommand = new RelayCommand(InvertAssetSelection);
        SelectMaterialResourcesCommand = new RelayCommand(SelectMaterialResources);
        ResolveConflictsCommand = new RelayCommand(_ => ResolveConflicts(), _ => HasConflicts);
        ToggleConflictFilterCommand = new RelayCommand(_ => ShowOnlyConflicts = !ShowOnlyConflicts);

        _ = LoadAsync();
    }

    public ModListProjectionSource<PatchResourceRepackagingModItem> SourceMods => _sourceMods.Items;
    public BulkObservableCollection<PatchResourceAssetRowViewModel> AssetRows { get; }
    public IReadOnlyList<PatchResourcePatchGroup> PatchGroups => _inspection?.PatchGroups ?? Array.Empty<PatchResourcePatchGroup>();

    public string ModQuery
    {
        get => _modQuery;
        set
        {
            if (!SetField(ref _modQuery, value)) return;
            ApplyModFilter();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetField(ref _query, value)) return;
            ApplyAssetFilter(ListTransitionKind.Filter);
        }
    }

    public bool ShowOnlyConflicts
    {
        get => _showOnlyConflicts;
        set
        {
            if (!SetField(ref _showOnlyConflicts, value)) return;
            OnPropertyChanged(nameof(ConflictFilterText));
            ApplyAssetFilter(ListTransitionKind.Filter);
        }
    }

    public string ConflictFilterText => ShowOnlyConflicts ? "显示全部" : "仅冲突";
    public string OutputDirectory { get => _outputDirectory; set => SetField(ref _outputDirectory, value); }
    public bool AutoImport { get => _autoImport; set => SetField(ref _autoImport, value); }
    public string State { get => _state; private set => SetField(ref _state, value); }
    public string PatchGroupSummary { get => _patchGroupSummary; private set => SetField(ref _patchGroupSummary, value); }
    public bool IsInspecting { get => _isInspecting; private set => SetField(ref _isInspecting, value); }
    public bool IsGenerating { get => _isGenerating; private set { if (SetField(ref _isGenerating, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
    public int SelectedModCount => _allModItems.Count(item => item.IsSelected);
    public int AssetCount => _allAssetRows.Count;
    public int SelectedAssetCount => _allAssetRows.Count(item => item.IsSelected);
    public int ConflictCount => _allAssetRows.Count(item => item.IsConflict);
    public bool HasConflicts => _allAssetRows.Any(item => item.IsConflict);
    public bool HasSelectedConflict => _allAssetRows
        .Where(item => item.IsSelected)
        .GroupBy(item => item.AssetKey)
        .Any(group => group.Count() > 1);
    public bool CanGenerate => !IsInspecting
        && !IsGenerating
        && !HasSelectedConflict
        && SelectedAssetCount > 0
        && !string.IsNullOrWhiteSpace(OutputDirectory);

    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand SelectAllModsCommand { get; }
    public RelayCommand ClearModSelectionCommand { get; }
    public RelayCommand SelectAllAssetsCommand { get; }
    public RelayCommand ClearAssetSelectionCommand { get; }
    public RelayCommand InvertAssetSelectionCommand { get; }
    public RelayCommand SelectMaterialResourcesCommand { get; }
    public RelayCommand ResolveConflictsCommand { get; }
    public RelayCommand ToggleConflictFilterCommand { get; }

    private async Task LoadAsync()
    {
        try
        {
            var items = _library.All()
                .Where(mod => mod.HasPatchContent)
                .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .Select(mod => new PatchResourceRepackagingModItem(mod))
                .ToArray();
            _allModItems.Clear();
            _allModItems.AddRange(items);
            ApplyModFilter();

            var source = items.FirstOrDefault(item => string.Equals(item.Mod.Guid, _sourceModId, StringComparison.OrdinalIgnoreCase));
            if (source is null && items.Length != 0) source = items[0];
            if (source is not null) source.IsSelected = true;
            OnPropertyChanged(nameof(SelectedModCount));
            await RefreshInspectionAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            State = $"读取 Mod 失败：{exception.Message}";
            LogService.Error($"Patch 资源重打包页面初始化失败：{exception}");
        }
    }

    public void ApplyModSelection(IReadOnlyList<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allModItems)
            item.IsSelected = selected.Contains(item.SelectionKey);
        OnPropertyChanged(nameof(SelectedModCount));
        _ = RefreshInspectionAsync();
    }

    private void ApplyModFilter()
    {
        var query = ModQuery.Trim();
        var visible = _allModItems.Where(item =>
            query.Length == 0
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            || item.AssetSummaryText.Contains(query, StringComparison.OrdinalIgnoreCase));
        _sourceMods.Submit(visible, _allModItems);
    }

    private async Task RefreshInspectionAsync()
    {
        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();
        _inspectionCancellation = new CancellationTokenSource();
        var cancellationToken = _inspectionCancellation.Token;
        var generation = ++_inspectionGeneration;
        var selectedNodes = _allModItems
            .Where(item => item.IsSelected)
            .Select(item => _library.Snapshot.Nodes.Values.FirstOrDefault(node => string.Equals(node.Id.Value.ToString("N"), item.Mod.Guid, StringComparison.OrdinalIgnoreCase)))
            .Where(node => node is not null)
            .Cast<ModNode>()
            .ToArray();

        IsInspecting = true;
        GenerateCommand.RaiseCanExecuteChanged();
        if (selectedNodes.Length == 0)
        {
            _inspection = null;
            _allAssetRows.Clear();
            AssetRows.ReplaceWith(Array.Empty<PatchResourceAssetRowViewModel>(), ListTransitionKind.Replace);
            PatchGroupSummary = string.Empty;
            State = "请在中间选择至少一个包含 Patch 的 Mod。";
            FinishInspectionState();
            return;
        }

        try
        {
            State = $"正在读取 {selectedNodes.Length} 个 Mod 的 Patch 资源…";
            var groups = _repackaging.DiscoverPatchGroups(selectedNodes, _library.ModsRootDirectory);
            PatchGroupSummary = groups.Count == 0
                ? "未发现 Patch 组。"
                : string.Join("；", groups.GroupBy(group => group.ModName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => $"{group.Key}: {group.Count()} 个 Patch"));
            var inspection = await _repackaging.InspectAsync(groups, cancellationToken).ConfigureAwait(true);
            if (generation != _inspectionGeneration || cancellationToken.IsCancellationRequested) return;

            _inspection = inspection;
            _allAssetRows.Clear();
            _allAssetRows.AddRange(inspection.Variants.Select(variant => new PatchResourceAssetRowViewModel(variant, OnAssetSelectionChanged)));
            foreach (var row in _allAssetRows) row.IsSelected = true;
            ApplyAssetFilter(ListTransitionKind.Replace);
            State = inspection.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)
                ? $"读取完成，但有 {inspection.Issues.Count(issue => issue.Severity == CoreIssueSeverity.Error)} 个错误；请先检查消息。"
                : $"已读取 {AssetCount} 个 AssetKey 变体，其中 {ConflictCount} 个属于冲突变体。";
            _notifications?.Show(State, inspection.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error) ? NotificationLevel.Error : NotificationLevel.Info, TimeSpan.FromSeconds(8));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation != _inspectionGeneration) return;
            _inspection = null;
            _allAssetRows.Clear();
            AssetRows.ReplaceWith(Array.Empty<PatchResourceAssetRowViewModel>(), ListTransitionKind.Replace);
            State = $"读取 Patch 资源失败：{exception.Message}";
            LogService.Error($"Patch 资源重打包分析失败：{exception}");
            _notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (generation == _inspectionGeneration) FinishInspectionState();
        }
    }

    private void FinishInspectionState()
    {
        IsInspecting = false;
        OnPropertyChanged(nameof(AssetCount));
        OnPropertyChanged(nameof(SelectedAssetCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(HasSelectedConflict));
        GenerateCommand.RaiseCanExecuteChanged();
        ResolveConflictsCommand.RaiseCanExecuteChanged();
    }

    private void ApplyAssetFilter(ListTransitionKind kind)
    {
        var query = Query.Trim();
        var visible = _allAssetRows.Where(row =>
            (!ShowOnlyConflicts || row.IsConflict)
            && (query.Length == 0
                || row.AssetKeyText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.SourceModText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.SourcePatchText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.ReferenceTargetsText.Contains(query, StringComparison.OrdinalIgnoreCase)));
        AssetRows.ReplaceWith(visible, kind);
        OnPropertyChanged(nameof(SelectedAssetCount));
        OnPropertyChanged(nameof(HasSelectedConflict));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void OnAssetSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedAssetCount));
        OnPropertyChanged(nameof(HasSelectedConflict));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void SelectAllMods()
    {
        ApplyModSelection(_allModItems.Select(item => item.SelectionKey).ToArray());
    }

    private void ClearModSelection()
    {
        ApplyModSelection(Array.Empty<string>());
    }

    private void SelectAllAssets()
    {
        foreach (var row in _allAssetRows) row.IsSelected = true;
        OnAssetSelectionChanged();
    }

    private void ClearAssetSelection()
    {
        foreach (var row in _allAssetRows) row.IsSelected = false;
        OnAssetSelectionChanged();
    }

    private void InvertAssetSelection()
    {
        foreach (var row in _allAssetRows) row.IsSelected = !row.IsSelected;
        OnAssetSelectionChanged();
    }

    private void SelectMaterialResources()
    {
        foreach (var row in _allAssetRows)
            row.IsSelected = row.TypeName is "Material" or "Texture";
        OnAssetSelectionChanged();
    }

    private void ResolveConflicts()
    {
        foreach (var group in _allAssetRows.Where(row => row.IsConflict).GroupBy(row => row.AssetKey))
        {
            var ordered = group.OrderBy(row => SourcePriority(row)).ThenBy(row => row.PayloadFingerprint, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0) continue;
            var current = _conflictCursors.GetValueOrDefault(group.Key, -1);
            var next = (current + 1) % ordered.Length;
            _conflictCursors[group.Key] = next;
            for (var index = 0; index < ordered.Length; index++) ordered[index].IsSelected = index == next;
        }
        OnAssetSelectionChanged();
        State = "已为每个冲突 AssetKey 选择一个来源；再次点击可轮换来源。";
    }

    private int SourcePriority(PatchResourceAssetRowViewModel row)
    {
        var priorities = _allModItems.Select((item, index) => (item.SelectionKey, index)).ToDictionary(item => item.SelectionKey, item => item.index, StringComparer.OrdinalIgnoreCase);
        return row.Variant.Occurrences.Select(item => priorities.GetValueOrDefault(item.NodeId.Value.ToString("N"), int.MaxValue)).DefaultIfEmpty(int.MaxValue).Min();
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Patch 资源重打包输出目录", Multiselect = false, InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private async Task GenerateAsync()
    {
        if (!CanGenerate)
        {
            _notifications?.Show(HasSelectedConflict ? "当前仍有同 AssetKey 的多个冲突版本被选中，请点击“自动移除冲突选择”。" : "请选择至少一个无冲突的 AssetKey。", NotificationLevel.Info, TimeSpan.FromSeconds(8));
            return;
        }

        var selected = _allAssetRows
            .Where(row => row.IsSelected)
            .Select(row => new PatchResourceRepackagingSelection(row.Variant.Occurrences[0]))
            .ToArray();
        var importToLibrary = AutoImport;
        var outputRoot = importToLibrary
            ? Path.Combine(Path.GetTempPath(), "HD2ModManager", "PatchResourceRepackaging", Guid.NewGuid().ToString("N"))
            : Path.Combine(OutputDirectory, $"Patch资源重打包+{DateTime.Now:yyyyMMdd-HHmmssfff}");
        IsGenerating = true;
        State = "正在写出选中的 Patch 资源…";
        var task = _backgroundTasks?.Enqueue(
            BackgroundTaskKind.Other,
            "Patch 资源重打包",
            "多 Mod AssetKey 资源选择",
            "Patch 工具",
            presentation: BackgroundTaskPresentation.Foreground);
        task?.MarkRunning("正在复制资源并重建 Patch 索引");
        try
        {
            var output = await _repackaging.WriteAsync(selected, outputRoot, task?.CancellationToken ?? CancellationToken.None).ConfigureAwait(true);
            if (importToLibrary)
            {
                var importer = new ImportService(_library, informationCenter: _library.InformationCenter);
                await importer.ImportPathAsync(output, task?.CancellationToken ?? CancellationToken.None).ConfigureAwait(true);
                TryDeleteTemporaryDirectory(outputRoot);
                State = "Patch 资源已生成并导入 Mod 库。";
            }
            else
            {
                State = $"Patch 资源重打包完成：{output}";
            }
            task?.MarkCompleted();
            if (task is null) _notifications?.Show(State, NotificationLevel.Info, TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            task?.MarkCanceled();
            State = "Patch 资源重打包已取消。";
        }
        catch (Exception exception)
        {
            task?.MarkFailed(exception.Message);
            State = $"Patch 资源重打包失败：{exception.Message}";
            LogService.Error($"Patch 资源重打包失败：{exception}");
            if (task is null) _notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(12));
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private static void TryDeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public override void Dispose()
    {
        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();
        _sourceMods.Dispose();
        base.Dispose();
    }
}

public sealed class PatchResourceRepackagingModItem : BaseViewModel, IModListSelectable
{
    private bool _isSelected;
    private readonly ModListRowPresentation _rowPresentation;
    public PatchResourceRepackagingModItem(ModEntity mod)
    {
        Mod = mod;
        _rowPresentation = new ModListRowPresentation(
            mod.IsReShade ? "ReShade" : mod.IsDecoration ? "装饰" : mod.IsOption ? "选项" : null,
            ModListRowTone.Muted,
            Summary: "Patch 资源来源");
    }
    public ModEntity Mod { get; }
    public string SelectionKey => Mod.Guid;
    public string ListKey => SelectionKey;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public bool IsDecoration => Mod.IsDecoration;
    public bool IsOption => Mod.IsOption;
    public bool IsAttachable => Mod.IsDecoration || Mod.IsOption;
    public bool CanJoinProfile => false;
    public string Name => Mod.Name;
    public string? Description => Mod.Description;
    public string? ImagePath => Mod.Image;
    public string AssetSummaryText => Mod.IsDecoration ? "装饰 Mod" : Mod.IsOption ? "选项 Mod" : "Patch 资源来源";
    public ModListRowPresentation RowPresentation => _rowPresentation;
    public bool IsModelOutdated => false;
    public string UserStatusTitle => Mod.IsDecoration ? "装饰 Mod" : Mod.IsOption ? "选项 Mod" : "普通 Mod";
}

public sealed class PatchResourceAssetRowViewModel : BaseViewModel
{
    private readonly Action _changed;
    private bool _isSelected;
    public PatchResourceAssetRowViewModel(PatchResourceAssetVariant variant, Action changed)
    {
        Variant = variant;
        _changed = changed;
    }
    public PatchResourceAssetVariant Variant { get; }
    public AssetKey AssetKey => Variant.AssetKey;
    public string SelectionKey => $"{AssetKey.TypeId:x16}/{AssetKey.FileId:x16}/{PayloadFingerprint}";
    public string PayloadFingerprint => Variant.PayloadFingerprint;
    public string AssetKeyText => $"0x{AssetKey.TypeId:x16}/0x{AssetKey.FileId:x16}";
    public string TypeName => Variant.TypeName;
    public bool IsConflict => Variant.IsConflict;
    public string ConflictText => IsConflict ? "冲突" : string.Empty;
    public string SourceModText => string.Join("、", Variant.Occurrences.Select(item => item.ModName).Distinct(StringComparer.OrdinalIgnoreCase));
    public string SourcePatchText => string.Join("、", Variant.Occurrences.Select(item => item.PatchLabel).Distinct(StringComparer.OrdinalIgnoreCase));
    public string ReferenceTargetsText => Variant.ReferenceTargets.Count == 0
        ? "无"
        : string.Join("、", Variant.ReferenceTargets.Select(key => $"0x{key.TypeId:x16}/0x{key.FileId:x16}"));
    public string SizeText
    {
        get
        {
            var occurrence = Variant.Occurrences[0];
            return $"TOC {FormatBytes(occurrence.TocBytes)}；Stream {FormatBytes(occurrence.StreamBytes)}；GPU {FormatBytes(occurrence.GpuBytes)}；总计 {FormatBytes(occurrence.TotalBytes)}";
        }
    }
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes / (1024d * 1024d):0.0} MiB";
    }
}
