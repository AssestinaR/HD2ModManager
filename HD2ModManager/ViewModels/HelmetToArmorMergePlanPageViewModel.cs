using System.Collections.ObjectModel;
using System.IO;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 作用：承载“头盔合并到盔甲并输出”的来源选择、模型集合选择和 Patch 输出向导。
// Purpose: Hosts source Mod selection, model-set selection, and Patch output for helmet merging.
public sealed class HelmetToArmorMergePlanPageViewModel : FullWorkspacePageViewModel
{
    private readonly ModLibraryService _library;
    private readonly NotificationService? _notifications;
    private readonly BackgroundTaskService? _backgroundTasks;
    private readonly string _initialSourceModId;
    private readonly List<HelmetMergeSourceModItem> _allSourceMods = [];
    private readonly ModListItemProjectionController<HelmetMergeSourceModItem> _sourceMods;
    private CancellationTokenSource? _inspectionCancellation;
    private int _inspectionGeneration;
    private string _sourceQuery = string.Empty;
    private string _outputDirectory = SettingsService.GetResolvedDefaultExportPath();
    private string _state = "请选择来源 Mod。";
    private bool _isInspecting;
    private bool _isGenerating;

    public HelmetToArmorMergePlanPageViewModel(
        ModLibraryService library,
        string initialSourceModId,
        NotificationService? notifications = null,
        BackgroundTaskService? backgroundTasks = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _notifications = notifications;
        _backgroundTasks = backgroundTasks;
        _initialSourceModId = initialSourceModId ?? string.Empty;
        Title = "头盔合并到盔甲并输出";

        _sourceMods = new ModListItemProjectionController<HelmetMergeSourceModItem>(item => item.SelectionKey);
        ModelSets = new BulkObservableCollection<HelmetMergeModelSetItem>(item => item.SelectionKey);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        SelectAllSourceModsCommand = new RelayCommand(_ => SetAllSourceMods(true));
        ClearSourceSelectionCommand = new RelayCommand(_ => SetAllSourceMods(false));
        SelectAllModelSetsCommand = new RelayCommand(_ => SetAllModelSets(true));
        ClearModelSetsCommand = new RelayCommand(_ => SetAllModelSets(false));
        InvertModelSetsCommand = new RelayCommand(_ => InvertModelSets());

        var sourceItems = _library.All()
            .Where(mod => !mod.IsDecoration && mod.HasPatchContent)
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new HelmetMergeSourceModItem(mod, OnSourceSelectionChanged))
            .ToArray();
        _allSourceMods.AddRange(sourceItems);
        foreach (var item in sourceItems)
            item.IsSelected = string.Equals(item.Mod.Guid, _initialSourceModId, StringComparison.OrdinalIgnoreCase);
        if (sourceItems.Length > 0 && !sourceItems.Any(item => item.IsSelected)) sourceItems[0].IsSelected = true;
        ApplySourceVisibility();
        _ = ReloadModelSetsAsync();
    }

    public ModListProjectionSource<HelmetMergeSourceModItem> SourceMods => _sourceMods.Items;
    public BulkObservableCollection<HelmetMergeModelSetItem> ModelSets { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand SelectAllSourceModsCommand { get; }
    public RelayCommand ClearSourceSelectionCommand { get; }
    public RelayCommand SelectAllModelSetsCommand { get; }
    public RelayCommand ClearModelSetsCommand { get; }
    public RelayCommand InvertModelSetsCommand { get; }

    public string SourceQuery
    {
        get => _sourceQuery;
        set
        {
            if (!SetField(ref _sourceQuery, value)) return;
            ApplySourceVisibility();
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (!SetField(ref _outputDirectory, value)) return;
            OnPropertyChanged(nameof(CanGenerate));
            GenerateCommand.RaiseCanExecuteChanged();
        }
    }

    public string State { get => _state; private set => SetField(ref _state, value); }
    public bool IsInspecting { get => _isInspecting; private set { if (SetField(ref _isInspecting, value)) OnPropertyChanged(nameof(CanGenerate)); } }
    public bool IsGenerating { get => _isGenerating; private set { if (SetField(ref _isGenerating, value)) { OnPropertyChanged(nameof(CanGenerate)); GenerateCommand.RaiseCanExecuteChanged(); } } }
    public bool CanGenerate => !IsInspecting && !IsGenerating && !string.IsNullOrWhiteSpace(OutputDirectory) && ModelSets.Any(item => item.IsSelected);
    public int SelectedSourceCount => _allSourceMods.Count(item => item.IsSelected);
    public int SelectedModelSetCount => ModelSets.Count(item => item.IsSelected);

    public void ApplySourceSelection(IReadOnlyList<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allSourceMods) item.IsSelected = selected.Contains(item.SelectionKey);
        OnSourceSelectionChanged();
        _ = ReloadModelSetsAsync();
    }

    public override void Dispose()
    {
        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();
        _inspectionCancellation = null;
        _sourceMods.Dispose();
        base.Dispose();
    }

    private void ApplySourceVisibility()
    {
        var query = SourceQuery.Trim();
        var visible = _allSourceMods.Where(item => query.Length == 0
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
        _sourceMods.Submit(visible, _allSourceMods);
    }

    private void OnSourceSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSourceCount));
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void SetAllSourceMods(bool selected)
    {
        foreach (var item in _allSourceMods)
            item.IsSelected = selected;

        OnSourceSelectionChanged();
        _ = ReloadModelSetsAsync();
    }

    private async Task ReloadModelSetsAsync()
    {
        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _inspectionCancellation = cancellation;
        var generation = ++_inspectionGeneration;
        IsInspecting = true;
        State = "正在读取选中 Mod 的 Armor/Helmet 模型集合。";
        try
        {
            var selected = _allSourceMods.Where(item => item.IsSelected).ToArray();
            var result = new List<HelmetMergeModelSetItem>();
            foreach (var source in selected)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Guid.TryParse(source.Mod.Guid, out var guid)) continue;
                if (!_library.Snapshot.Nodes.TryGetValue(new ModNodeId(guid), out var node)) continue;
                var inspection = await _library.HelmetToArmorMergeService.InspectAsync(
                    node,
                    _library.ModsRootDirectory,
                    SettingsService.GetGameDataFolder(),
                    cancellation.Token).ConfigureAwait(true);
                result.AddRange(inspection.Candidates.Select(candidate => new HelmetMergeModelSetItem(
                    source.Mod.Guid,
                    source.Name,
                    source.Mod.Image,
                    candidate,
                    OnModelSetSelectionChanged)
                { IsSelected = true }));
            }

            if (!ReferenceEquals(_inspectionCancellation, cancellation) || cancellation.IsCancellationRequested || generation != _inspectionGeneration) return;
            ModelSets.ReplaceWith(result, ListTransitionKind.Replace);
            State = result.Count == 0
                ? "没有找到完整的 Armor/Helmet 模型集合。"
                : $"已找到 {result.Count} 个模型集合；生成时会按来源 Mod 分别输出 Patch。";
            OnPropertyChanged(nameof(SelectedModelSetCount));
            OnPropertyChanged(nameof(CanGenerate));
            GenerateCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(_inspectionCancellation, cancellation)) State = $"读取模型集合失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_inspectionCancellation, cancellation))
            {
                IsInspecting = false;
                cancellation.Dispose();
                _inspectionCancellation = null;
            }
        }
    }

    private void OnModelSetSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedModelSetCount));
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void SetAllModelSets(bool selected)
    {
        foreach (var item in ModelSets) item.IsSelected = selected;
        OnModelSetSelectionChanged();
    }

    private void InvertModelSets()
    {
        foreach (var item in ModelSets) item.IsSelected = !item.IsSelected;
        OnModelSetSelectionChanged();
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private async Task GenerateAsync()
    {
        var selected = ModelSets.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0 || string.IsNullOrWhiteSpace(OutputDirectory)) return;
        var outputRoot = Path.GetFullPath(OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var task = _backgroundTasks?.Enqueue(
            BackgroundTaskKind.Other,
            "头盔合并到盔甲并输出",
            outputRoot,
            origin: "头盔合并向导",
            userVisibleReason: "将来源头盔模型追加到胸部 Unit，并生成隐藏头盔的 Patch。",
            canCancel: true,
            presentation: BackgroundTaskPresentation.Foreground);
        IsGenerating = true;
        var outputs = new List<string>();
        try
        {
            task?.MarkRunning("正在生成");
            var groups = selected.GroupBy(item => item.SourceModId, StringComparer.OrdinalIgnoreCase).ToArray();
            var completed = 0;
            foreach (var group in groups)
            {
                task?.CancellationToken.ThrowIfCancellationRequested();
                var mod = _library.Get(group.Key) ?? throw new InvalidOperationException("来源 Mod 已不存在。");
                if (!Guid.TryParse(mod.Guid, out var guid) || !_library.Snapshot.Nodes.TryGetValue(new ModNodeId(guid), out var node))
                    throw new InvalidOperationException($"无法定位来源 Mod：{mod.Name}");
                var destination = Path.Combine(outputRoot, $"{SanitizeDirectoryName(mod.Name)}-{timestamp}-头盔合并");
                var progress = task is null ? null : new Progress<HelmetToArmorMergeProgress>(value =>
                {
                    task.UpdateStage($"{mod.Name}：{value.Stage}");
                    task.UpdateProgress(groups.Length == 0 ? 0 : (completed + value.Fraction) / groups.Length);
                });
                var result = await _library.HelmetToArmorMergeService.ExecuteAsync(
                    node,
                    _library.ResolveAbsolutePath(mod.SourcePath),
                    destination,
                    SettingsService.GetGameDataFolder(),
                    group.Select(item => item.Candidate).ToArray(),
                    task?.CancellationToken ?? CancellationToken.None,
                    progress).ConfigureAwait(true);
                if (!result.IsSuccessful)
                {
                    var detail = string.Join("；", result.Issues.Take(4).Select(issue => issue.Message));
                    throw new InvalidDataException($"{mod.Name}：{detail}");
                }
                outputs.Add(destination);
                completed++;
            }
            task?.MarkCompleted();
            State = $"已完成 {outputs.Count} 个来源 Mod 的输出：{string.Join("；", outputs)}";
            _notifications?.Show($"头盔合并完成：输出 {outputs.Count} 个 Patch。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            task?.MarkCanceled();
            State = "头盔合并输出已取消。";
        }
        catch (Exception exception)
        {
            task?.MarkFailed(exception.Message);
            State = $"头盔合并输出失败：{exception.Message}";
            _notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(12));
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "未命名Mod" : safe;
    }
}

public sealed class HelmetMergeSourceModItem : BaseViewModel, IModListSelectable
{
    private readonly Action _changed;
    private readonly ModListRowPresentation _rowPresentation;
    private bool _isSelected;

    public HelmetMergeSourceModItem(ModEntity mod, Action changed)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _changed = changed;
        _rowPresentation = new ModListRowPresentation(
            Mod.IsReShade ? "ReShade" : Mod.IsDecoration ? "装饰" : Mod.IsOption ? "选项" : null,
            ModListRowTone.Muted,
            Summary: "可作为来源");
    }

    public ModEntity Mod { get; }
    public string SelectionKey => Mod.Guid;
    public bool IsDecoration => Mod.IsDecoration;
    public bool IsOption => Mod.IsOption;
    public bool IsAttachable => Mod.IsOption;
    public bool CanJoinProfile => false;
    public string Name => Mod.Name;
    public string? Description => Mod.Description;
    public string? ImagePath => Mod.Image;
    public string AssetSummaryText => Mod.IsOption ? "选项 Mod · 可作为来源" : "标准 Mod · 可作为来源";
    public ModListRowPresentation RowPresentation => _rowPresentation;
    public string UserStatusTitle => Mod.IsOption ? "选项 Mod" : "标准 Mod";
    public bool IsModelOutdated => false;
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }
}

public sealed class HelmetMergeModelSetItem : BaseViewModel, IModListSelectable
{
    private readonly Action _changed;
    private readonly ModListRowPresentation _rowPresentation;
    private bool _isSelected;

    public HelmetMergeModelSetItem(string sourceModId, string sourceModName, string? sourceImage, HelmetToArmorMergeCandidate candidate, Action changed)
    {
        SourceModId = sourceModId;
        SourceModName = sourceModName;
        SourceImage = sourceImage;
        Candidate = candidate;
        _changed = changed;
        _rowPresentation = new ModListRowPresentation(
            "完整模型集合",
            ModListRowTone.Accent,
            Summary: $"Armor {candidate.ArmorParts.Count} 个胸部 Unit；Helmet Any；{candidate.ArmorArchiveId} + {candidate.HelmetArchiveId}");
    }

    public string SourceModId { get; }
    public string SourceModName { get; }
    public string? SourceImage { get; }
    public HelmetToArmorMergeCandidate Candidate { get; }
    public string SelectionKey => Candidate.CandidateId + "|" + SourceModId;
    public bool IsDecoration => false;
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }
    public string Name => Candidate.DisplayName;
    public string Description => $"来源：{SourceModName}；Patch：{Candidate.SourcePatchFileName}";
    public string? ImagePath => SourceImage;
    public string AssetSummaryText => $"Armor {Candidate.ArmorParts.Count} 个胸部 Unit；Helmet Any；{Candidate.ArmorArchiveId} + {Candidate.HelmetArchiveId}";
    public ModListRowPresentation RowPresentation => _rowPresentation;
    public string UserStatusTitle => "完整模型集合";
    public bool IsModelOutdated => false;
    public string ArmorVariants => string.Join("、", Candidate.ArmorParts.Select(part => part.BodyVariant.ToString()).Distinct());
    public string HelmetVariant => Candidate.HelmetPart.BodyVariant.ToString();
    public string ArmorUnitIds => string.Join("、", Candidate.ArmorParts.Select(part => $"0x{part.UnitAssetKey.FileId:x16}"));
    public string HelmetUnitId => $"0x{Candidate.HelmetPart.UnitAssetKey.FileId:x16}";
}
