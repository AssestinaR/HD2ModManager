using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// Purpose: Creates many independent decoration packages for one already-selected host Mod.
public sealed class BatchDecorationPlanPageViewModel : PageViewModel
{
    private readonly ModLibraryService _library;
    private readonly NotificationService _notifications;
    private readonly IModEquipmentSourceFactsReader _equipmentSourceFacts;
    private readonly IModFileResolver _fileResolver;
    private readonly BackgroundTaskService? _backgroundTasks;
    private readonly Guid _sourceFactsSessionId = Guid.NewGuid();
    private readonly Dictionary<string, DecorationBatchSourcePlanItem> _plansBySource = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DecorationBatchSourceModItem> _allSources = Array.Empty<DecorationBatchSourceModItem>();
    private string _outputDirectory;
    private bool _autoImport = true;
    private bool _showOptions = true;
    private string _sourceQuery = string.Empty;
    private bool _isGenerating;
    private int _planSyncGeneration;
    private string _state = "正在加载可作为装饰来源的 Mod。";

    public BatchDecorationPlanPageViewModel(ModLibraryService library, NotificationService notifications, string hostModId, BackgroundTaskService? backgroundTasks = null)
    {
        _library = library;
        _notifications = notifications;
        _equipmentSourceFacts = _library.EquipmentSourceFactsReader;
        _fileResolver = CoreServices.CreateModFileResolver();
        _backgroundTasks = backgroundTasks;
        HostModId = hostModId;
        HostName = library.Get(hostModId)?.Name ?? hostModId;
        _outputDirectory = SettingsService.GetResolvedDefaultExportPath();
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        Title = "批量生成装饰";
        _ = LoadAsync();
    }

    public string HostModId { get; }
    public string HostName { get; }
    public BulkObservableCollection<DecorationBatchSourceModItem> SourceMods { get; } = new(item => item.SelectionKey);
    public BulkObservableCollection<DecorationBatchSourcePlanItem> Plans { get; } = new(item => item.SourceModId);
    public RelayCommand GenerateCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public bool CanGenerate => !_isGenerating && Plans.Any(plan => plan.SourceUnits.Any(unit => unit.IsSelected));
    public string State { get => _state; private set => SetField(ref _state, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetField(ref _outputDirectory, value); }
    public bool AutoImport { get => _autoImport; set => SetField(ref _autoImport, value); }
    public bool ShowOptions
    {
        get => _showOptions;
        set { if (SetField(ref _showOptions, value)) RefreshSourceVisibility(); }
    }
    public string OptionFilterText => ShowOptions ? "隐藏选项" : "显示选项";
    public string SourceQuery
    {
        get => _sourceQuery;
        set { if (SetField(ref _sourceQuery, value)) RefreshSourceVisibility(); }
    }

    public void ApplySourceSelection(IReadOnlyList<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allSources) item.IsSelected = selected.Contains(item.ModId);
        _ = SynchronizePlansAsync();
    }

    public override void Dispose()
    {
        // The batch planner shares lightweight source facts only for this page's
        // lifetime; release them once its animated list is no longer active.
        _library.InformationReader.ClearSession(_sourceFactsSessionId);
        base.Dispose();
    }

    private async Task LoadAsync()
    {
        _allSources = _library.All()
            .Where(mod => !mod.IsDecoration && !mod.IsReShade && !string.Equals(mod.Guid, HostModId, StringComparison.OrdinalIgnoreCase) && mod.HasPatchContent)
            .OrderBy(mod => mod.Name)
            .Select(mod => new DecorationBatchSourceModItem(mod, OnSourceSelectionChanged))
            .ToArray();
        RefreshSourceVisibility();
        State = _allSources.Count == 0 ? "没有可作为装饰来源的 Mod。" : "选择来源后将自动分析默认 Archive、部位和 Unit。";
        await Task.CompletedTask;
    }

    private void OnSourceSelectionChanged() => _ = SynchronizePlansAsync();

    private async Task SynchronizePlansAsync()
    {
		var generation = ++_planSyncGeneration;
        var selected = _allSources.Where(source => source.IsSelected).ToArray();
        var selectedIds = selected.Select(source => source.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = selected.Where(source => !_plansBySource.ContainsKey(source.ModId)).ToArray();
        if (pending.Length != 0) State = $"正在分析 {pending.Length} 个新增来源 Mod。";
        foreach (var source in pending)
        {
			if (generation != _planSyncGeneration) return;
            var plan = await AnalyzeSourceAsync(source).ConfigureAwait(true);
			if (generation != _planSyncGeneration) return;
            _plansBySource[source.ModId] = plan;
        }
		if (generation != _planSyncGeneration) return;
        Plans.ReplaceWith(selected.Where(source => _plansBySource.ContainsKey(source.ModId)).Select(source => _plansBySource[source.ModId]), ListTransitionKind.Automatic);
        State = Plans.Count == 0 ? "请选择至少一个来源 Mod。" : $"已准备 {Plans.Count} 个来源装饰计划，目标主体：{HostName}。";
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private async Task<DecorationBatchSourcePlanItem> AnalyzeSourceAsync(DecorationBatchSourceModItem source)
    {
        var node = _library.Snapshot.Nodes.Values.FirstOrDefault(candidate => candidate.Id.Value.ToString("N") == source.ModId)
            ?? throw new InvalidOperationException($"来源 Mod 已不存在：{source.Name}");
        var sourceFacts = await _equipmentSourceFacts.ReadAsync(
            node,
            _library.ModsRootDirectory,
            new ModInformationRequest(
                ModInformationKind.AssetInventory,
                "BatchDecorationPlan",
                Context: ModInformationRequestContext.Create(
                    ModInformationCacheScope.Session,
                    sessionId: _sourceFactsSessionId,
                    operationName: "BatchDecorationPlan"))
            {
                Property = ModInformationPropertyKind.UnitGeometrySummary
            })
            .ConfigureAwait(true);
        var preferredArchive = DecorationPlanningDefaults.SelectPreferredArchiveId(sourceFacts.SourceCandidates);
        var units = sourceFacts.SourceCandidates.SelectMany(entry => entry.Parts.Select(part => new { entry.ArchiveId, Part = part }))
            .GroupBy(item => new { item.ArchiveId, item.Part.UnitAssetKey, item.Part.MeshInfoIndex, item.Part.PartKind, item.Part.BodyVariant, item.Part.Layer })
            .Select(group => new DecorationSourceUnitItem(group.Key.ArchiveId, group.First().Part, () => { })
            {
                IsSelected = string.Equals(group.Key.ArchiveId, preferredArchive, StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(item => item.PartKind).ThenBy(item => item.BodyVariant).ThenBy(item => item.Layer).ThenBy(item => item.FileId).ToArray();
        var selectedParts = units.Where(unit => unit.IsSelected).Select(unit => unit.Part).ToArray();
        return new DecorationBatchSourcePlanItem(source, node, sourceFacts.PreparedEntries, units,
            DecorationPlanningDefaults.ResolveTargetPart(selectedParts),
            DecorationPlanningDefaults.ResolveTargetBodyVariant(selectedParts), OnPlanChanged);
    }

    private void OnPlanChanged()
    {
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSourceVisibility()
    {
        var query = SourceQuery.Trim();
        SourceMods.ReplaceWith(_allSources.Where(source => (ShowOptions || !source.IsOption)
            && (query.Length == 0 || source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (source.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))), ListTransitionKind.Filter);
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private async Task GenerateAsync()
    {
        var plans = Plans.Where(plan => plan.SourceUnits.Any(unit => unit.IsSelected)).ToArray();
        if (plans.Length == 0) return;
        _isGenerating = true;
        OnPlanChanged();
        var task = _backgroundTasks?.Enqueue(
            BackgroundTaskKind.Other,
            "批量生成装饰",
            $"{plans.Length} 个来源 Mod",
            "装饰生成",
            presentation: BackgroundTaskPresentation.Foreground);
        task?.MarkRunning("正在准备生成计划");
        try
        {
            var generated = 0;
            foreach (var item in plans)
            {
                task?.UpdateStage($"正在生成 {generated + 1}/{plans.Length}：{item.Name}");
                task?.UpdateProgress((double)generated / plans.Length);
                var sourceUnits = item.SourceUnits.Where(unit => unit.IsSelected).Select(unit => new DecorationSourceUnit
                {
                    TypeId = unit.TypeId, FileId = unit.FileId, MeshInfoIndex = unit.MeshInfoIndex,
                    BodyVariant = unit.BodyVariant.ToString(), Layer = unit.Layer.ToString(), IsCulling = unit.IsCulling
                }).ToArray();
                var plan = new DecorationPlanDocument
                {
                    Plan = new DecorationAttachmentPlan
                    {
                        TargetModGuids = [HostModId], TargetPart = item.TargetPart,
                        TargetBodyVariant = NormalizeBodyVariant(item.TargetBodyVariant), DualVariantMode = NormalizeDualVariantMode(item.DualVariantMode),
                        ReplaceWhenSourcePartLayerMatches = item.ReplaceWhenSourcePartLayerMatches,
                        SourcePartLayers = item.SourceUnits.Where(unit => unit.IsSelected)
                            .Select(unit => DecorationPlanningDefaults.ToPartLayerKey(unit.PartKind, unit.Layer)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    }
                };
                var displayName = $"{item.Name} - 装饰";
                if (AutoImport)
                {
                    var progress = task is null ? null : new Progress<DecorationOperationProgress>(value => task.UpdateStage($"{generated + 1}/{plans.Length}：{value.Stage} {value.Completed}/{value.Total}"));
                    var created = await _library.CreateDecorationAsync(item.SourceModId, sourceUnits, plan, displayName, item.PreparedEntries, default, progress).ConfigureAwait(true);
                    var sourceDirectory = _library.ResolveAbsolutePath(created.SourcePath);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory)) CopyOutput(sourceDirectory, Path.Combine(OutputDirectory, SanitizeFileName(displayName)));
                }
                else
                {
                    var output = Path.Combine(OutputDirectory, SanitizeFileName(displayName));
                    Directory.CreateDirectory(output);
                    plan.Name = displayName;
                    var progress = task is null ? null : new Progress<DecorationOperationProgress>(value => task.UpdateStage($"{generated + 1}/{plans.Length}：{value.Stage} {value.Completed}/{value.Total}"));
                    plan.Version = 3;
                    plan.SourceStorageMode = "PatchSnapshot";
                    plan.SourceUnits = sourceUnits.Select(unit => new DecorationSourceUnit { TypeId = unit.TypeId, FileId = unit.FileId, MeshInfoIndex = unit.MeshInfoIndex, BodyVariant = unit.BodyVariant, Layer = unit.Layer, IsCulling = unit.IsCulling }).ToList();
                    await new DecorationPatchSnapshotService(_fileResolver, _library.InformationReader).CaptureAsync(item.SourceNode, _library.ModsRootDirectory, sourceUnits, output, item.PreparedEntries, default, progress);
                    await File.WriteAllTextAsync(Path.Combine(output, "decoration.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })).ConfigureAwait(true);
                }
                generated++;
                State = $"已生成 {generated}/{plans.Length}：{item.Name}";
            }
            State = $"已完成：为“{HostName}”生成 {generated} 个装饰 Mod。";
            if (task is null) _notifications.Show(State);
            task?.MarkCompleted();
        }
        catch (Exception exception)
        {
            State = $"批量生成失败：{exception.Message}";
            LogService.Error($"批量生成装饰失败：主体={HostModId}，异常={exception}");
            if (task is null) _notifications.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(10));
            task?.MarkFailed(exception.Message);
        }
        finally
        {
            _isGenerating = false;
            OnPlanChanged();
        }
    }

    private static string NormalizeBodyVariant(string value) => value.Contains("健") ? "Stocky" : value.Contains("纤") ? "Slim" : "Dual";
    private static string NormalizeDualVariantMode(string value) => value.Contains("全部") ? "ApplyAllToBoth" : "AutoAssign";
    private static string SanitizeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static void CopyOutput(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var name in new[] { "decoration.json", "stocky.bin", "slim.bin" })
        {
            var source = Path.Combine(sourceDirectory, name);
            var destination = Path.Combine(destinationDirectory, name);
            if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
            else if (File.Exists(destination)) File.Delete(destination);
        }
    }
}

public sealed class DecorationBatchSourceModItem : BaseViewModel, IModListSelectable
{
    private readonly Action _changed;
    private bool _isSelected;
    public DecorationBatchSourceModItem(ModEntity mod, Action changed) { Mod = mod; _changed = changed; }
    public ModEntity Mod { get; }
    public string ModId => Mod.Guid;
    public string SelectionKey => ModId;
    public bool IsDecoration => false;
    public bool IsOption => Mod.IsOption;
    public string Name => Mod.Name;
    public string? Description => Mod.Description;
    public string? ImagePath => Mod.Image;
    public string AssetSummaryText => "作为装饰来源";
    public bool IsModelOutdated => false;
    public string UserStatusTitle => IsOption ? "选项 Mod" : "标准 Mod";
    public bool IsVisible => true;
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }
}

public sealed class DecorationBatchSourcePlanItem : BaseViewModel
{
    private readonly Action _changed;
    private string _targetBodyVariant = "双身形";
    private string _dualVariantMode = "自动从来源分配";
    private string _targetPart;
    private bool _replaceWhenSourcePartLayerMatches = true;
    public DecorationBatchSourcePlanItem(DecorationBatchSourceModItem source, ModNode sourceNode,
        IReadOnlyDictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>> preparedEntries,
        IEnumerable<DecorationSourceUnitItem> units, string targetPart, string targetBodyVariant, Action changed)
    {
        Source = source; SourceNode = sourceNode; PreparedEntries = preparedEntries; _targetPart = targetPart; _targetBodyVariant = targetBodyVariant; _changed = changed;
        SourceUnits = new ObservableCollection<DecorationSourceUnitItem>(units);
        foreach (var unit in SourceUnits) unit.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(DecorationSourceUnitItem.IsSelected)) _changed(); };
    }
    public DecorationBatchSourceModItem Source { get; }
    public ModNode SourceNode { get; }
    public string SourceModId => Source.ModId;
    public string Name => Source.Name;
    public string? Description => Source.Description;
    public string? ImagePath => Source.ImagePath;
	public IReadOnlyList<DecorationBatchSourceModItem> SourceItems => [Source];
    public IReadOnlyDictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>> PreparedEntries { get; }
    public ObservableCollection<DecorationSourceUnitItem> SourceUnits { get; }
    public IReadOnlyList<string> BodyVariants { get; } = ["双身形", "仅健壮", "仅纤细"];
    public IReadOnlyList<string> DualVariantModes { get; } = ["自动从来源分配", "来源全部附加到每一个身形"];
    public IReadOnlyList<string> TargetParts { get; } = ["Head", "Torso", "Hips", "LeftArm", "RightArm", "LeftLeg", "RightLeg"];
    public string TargetBodyVariant { get => _targetBodyVariant; set { if (SetField(ref _targetBodyVariant, value)) _changed(); } }
    public string DualVariantMode { get => _dualVariantMode; set { if (SetField(ref _dualVariantMode, value)) _changed(); } }
    public string TargetPart { get => _targetPart; set { if (SetField(ref _targetPart, value)) _changed(); } }
    public bool ReplaceWhenSourcePartLayerMatches { get => _replaceWhenSourcePartLayerMatches; set { if (SetField(ref _replaceWhenSourcePartLayerMatches, value)) _changed(); } }
}
