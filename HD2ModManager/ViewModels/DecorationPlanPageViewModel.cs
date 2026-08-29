using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// Purpose: Captures a reusable decoration attachment plan before the Canonical append engine is available.
public sealed class DecorationPlanPageViewModel : PageViewModel
{
    private readonly ModLibraryService _library;
    private readonly NotificationService _notifications;
    private readonly IModEquipmentSourceFactsReader _equipmentSourceFacts;
    private readonly IModFileResolver _modFileResolver;
    private readonly BackgroundTaskService? _backgroundTasks;
    private readonly string? _editingDecorationId;
    private readonly Guid _sourceFactsSessionId = Guid.NewGuid();
    private string _targetBodyVariant = "双身形";
    private string _dualVariantMode = "自动从来源分配";
    private string _targetPart = "Torso";
    private string _outputDirectory;
    private bool _autoImport = true;
    private bool _showPotentialCulling;
    private bool _showOptions = true;
	private bool _replaceWhenSourcePartLayerMatches = true;
    private string _targetQuery = string.Empty;
    private IReadOnlyList<DecorationSourceUnitItem> _allSourceUnits = Array.Empty<DecorationSourceUnitItem>();
    private IReadOnlyList<DecorationTargetModItem> _allTargetMods = Array.Empty<DecorationTargetModItem>();
    private IReadOnlyDictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>> _preparedSourceEntries = new Dictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>>(StringComparer.OrdinalIgnoreCase);
    private string _state = "正在读取来源 Unit。";

    public DecorationPlanPageViewModel(ModLibraryService library, NotificationService notifications, string sourceModId, BackgroundTaskService? backgroundTasks = null, string? editingDecorationId = null)
    {
        _library = library;
        _notifications = notifications;
        _equipmentSourceFacts = _library.EquipmentSourceFactsReader;
        _modFileResolver = CoreServices.CreateModFileResolver();
        _backgroundTasks = backgroundTasks;
        _editingDecorationId = editingDecorationId;
        SourceModId = sourceModId;
        SourceName = library.Get(sourceModId)?.Name ?? sourceModId;
        _outputDirectory = SettingsService.GetResolvedDefaultExportPath();
        BodyVariants = ["双身形", "仅健壮", "仅纤细"];
        DualVariantModes = ["自动从来源分配", "来源全部附加到每一个身形"];
        TargetParts = ["Head", "Torso", "Hips", "LeftArm", "RightArm", "LeftLeg", "RightLeg"];
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync());
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        ToggleTargetSearchCommand = new RelayCommand(() =>
        {
            ShowTargetSearch = !ShowTargetSearch;
            OnPropertyChanged(nameof(ShowTargetSearch));
        });
        Title = IsEditing ? "编辑装饰 Mod" : "生成装饰 Mod";
        _ = LoadAsync();
    }

    public string SourceModId { get; }
    public string SourceName { get; }
    public bool IsEditing => !string.IsNullOrWhiteSpace(_editingDecorationId);
    public string SubmitText => IsEditing ? "保存" : "生成";
    public bool ShowOutputSettings => !IsEditing;
    public ObservableCollection<DecorationSourceUnitItem> SourceUnits { get; } = new();
    public BulkObservableCollection<DecorationTargetModItem> TargetMods { get; } = new(item => item.SelectionKey);
    public IReadOnlyList<string> BodyVariants { get; }
    public IReadOnlyList<string> DualVariantModes { get; }
    public IReadOnlyList<string> TargetParts { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand ToggleTargetSearchCommand { get; }
    public bool CanGenerate => SourceUnits.Any(item => item.IsSelected) && _allTargetMods.Any(item => item.IsSelected);
    public string State { get => _state; private set => SetField(ref _state, value); }
    public string TargetBodyVariant { get => _targetBodyVariant; set => SetField(ref _targetBodyVariant, value); }
    public string DualVariantMode { get => _dualVariantMode; set => SetField(ref _dualVariantMode, value); }
    public string TargetPart { get => _targetPart; set => SetField(ref _targetPart, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetField(ref _outputDirectory, value); }
    public bool AutoImport { get => _autoImport; set => SetField(ref _autoImport, value); }
	public bool ReplaceWhenSourcePartLayerMatches { get => _replaceWhenSourcePartLayerMatches; set => SetField(ref _replaceWhenSourcePartLayerMatches, value); }
    public bool ShowPotentialCulling
    {
        get => _showPotentialCulling;
        set
        {
            if (!SetField(ref _showPotentialCulling, value)) return;
            RefreshSourceUnits();
        }
    }
    public bool ShowTargetSearch { get; private set; }
    public bool ShowOptions
    {
        get => _showOptions;
        set
        {
            if (!SetField(ref _showOptions, value)) return;
            OnPropertyChanged(nameof(OptionFilterText));
            RefreshTargetVisibility();
        }
    }
    public string OptionFilterText => ShowOptions ? "隐藏选项" : "显示选项";
    public string TargetQuery
    {
        get => _targetQuery;
        set
        {
            if (!SetField(ref _targetQuery, value)) return;
            RefreshTargetVisibility();
        }
    }

    public override void Dispose()
    {
        // Source facts are intentionally retained while this editor is open so
        // toggling selections does not re-read the same Patch Units.
        _library.InformationReader.ClearSession(_sourceFactsSessionId);
        base.Dispose();
    }

    private async Task LoadAsync()
    {
        try
        {
            var source = _library.Snapshot.Nodes.Values.FirstOrDefault(node => node.Id.Value.ToString("N") == SourceModId);
            if (source is null) { State = "来源 Mod 已不存在。"; return; }
            var sourceFacts = await _equipmentSourceFacts.ReadAsync(
                source,
                _library.ModsRootDirectory,
                new ModInformationRequest(
                    ModInformationKind.AssetInventory,
                    "DecorationPlan",
                    Context: ModInformationRequestContext.Create(
                        ModInformationCacheScope.Session,
                        sessionId: _sourceFactsSessionId,
                        operationName: "DecorationPlan"))
                {
                    Property = ModInformationPropertyKind.UnitGeometrySummary
                });
            _preparedSourceEntries = sourceFacts.PreparedEntries;
            var preferredArchive = DecorationPlanningDefaults.SelectPreferredArchiveId(sourceFacts.SourceCandidates);
            _allSourceUnits = sourceFacts.SourceCandidates
                .SelectMany(entry => entry.Parts.Select(part => new { entry.ArchiveId, Part = part }))
                .GroupBy(item => new { item.ArchiveId, item.Part.UnitAssetKey, item.Part.MeshInfoIndex, item.Part.PartKind, item.Part.BodyVariant, item.Part.Layer })
                .Select(group => new DecorationSourceUnitItem(group.Key.ArchiveId, group.First().Part, OnSelectionChanged)
                {
                    IsSelected = string.Equals(group.Key.ArchiveId, preferredArchive, StringComparison.OrdinalIgnoreCase)
                })
                .OrderBy(item => item.PartKind)
                .ThenBy(item => item.BodyVariant)
                .ThenBy(item => item.Layer)
                .ThenBy(item => item.FileId)
                .ToArray();
			RefreshSourceUnits();
			TargetPart = DecorationPlanningDefaults.ResolveTargetPart(_allSourceUnits.Where(item => item.IsSelected).Select(item => item.Part));
			TargetBodyVariant = DecorationPlanningDefaults.ResolveTargetBodyVariant(_allSourceUnits.Where(item => item.IsSelected).Select(item => item.Part));
            _allTargetMods = _library.All()
                .Where(mod => !mod.IsDecoration && !mod.IsReShade && !string.Equals(mod.Guid, SourceModId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(mod => mod.Name)
                .Select(mod => new DecorationTargetModItem(mod.Guid, mod.Name, mod.Description, mod.Image, mod.IsOption, OnSelectionChanged))
                .ToArray();
            if (IsEditing)
            {
                var planPath = Path.Combine(_library.ResolveAbsolutePath(_library.Get(_editingDecorationId!)?.SourcePath), "decoration.json");
                var saved = JsonSerializer.Deserialize<DecorationPlanDocument>(await File.ReadAllTextAsync(planPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (saved is not null)
                {
                    var selectedSources = saved.SourceUnits.Select(unit => (unit.TypeId, unit.FileId, unit.MeshInfoIndex, unit.IsCulling)).ToHashSet();
                    foreach (var item in _allSourceUnits) item.IsSelected = selectedSources.Contains((item.TypeId, item.FileId, item.MeshInfoIndex, item.IsCulling));
                    TargetPart = saved.Plan.TargetPart;
                    TargetBodyVariant = saved.Plan.TargetBodyVariant == "Stocky" ? "仅健壮" : saved.Plan.TargetBodyVariant == "Slim" ? "仅纤细" : "双身形";
                    DualVariantMode = saved.Plan.DualVariantMode == "ApplyAllToBoth" ? "来源全部附加到每一个身形" : "自动从来源分配";
                    ReplaceWhenSourcePartLayerMatches = saved.Plan.ReplaceWhenSourcePartLayerMatches;
                    var targets = saved.Plan.TargetModGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var target in _allTargetMods) target.IsSelected = targets.Contains(target.ModId);
                }
            }
            RefreshTargetVisibility();
            State = SourceUnits.Count == 0 ? "来源中没有可识别的 Unit。" : $"已读取 {SourceUnits.Count} 个 Unit；选择后将写入装饰计划。";
        }
        catch (Exception exception)
        {
            State = $"读取来源失败：{exception.Message}";
        }
    }

    private void RefreshSourceUnits()
    {
        SourceUnits.Clear();
        foreach (var item in _allSourceUnits.Where(item => ShowPotentialCulling || !item.IsCulling)) SourceUnits.Add(item);
        OnSelectionChanged();
    }

    private void RefreshTargetVisibility()
    {
        var query = TargetQuery.Trim();
        var visible = _allTargetMods.Where(item =>
            (ShowOptions || !item.IsOption)
            && (query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)));
        TargetMods.ReplaceWith(visible, ListTransitionKind.Filter);
    }

    public void ApplyTargetSelection(IReadOnlyList<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allTargetMods) item.IsSelected = selected.Contains(item.ModId);
        // ModListPanel may synchronize several rows in one request. The page owns
        // the command state, so do not rely on individual row setters to refresh it.
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private async Task GenerateAsync()
    {
        var sourceUnits = SourceUnits.Where(item => item.IsSelected).Select(item => new DecorationSourceUnit
        {
            TypeId = item.TypeId,
            FileId = item.FileId,
            MeshInfoIndex = item.MeshInfoIndex,
            BodyVariant = item.BodyVariant.ToString(),
            Layer = item.Layer.ToString(),
            IsCulling = item.IsCulling
        }).ToList();
        var targetModGuids = _allTargetMods.Where(item => item.IsSelected).Select(item => item.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sourceUnits.Count == 0 || targetModGuids.Count == 0)
        {
            State = sourceUnits.Count == 0 && targetModGuids.Count == 0
                ? "请选择来源 Unit 和目标 Mod。"
                : sourceUnits.Count == 0 ? "请选择至少一个来源 Unit。" : "请选择至少一个目标 Mod。";
            _notifications.Show(State, NotificationLevel.Info, TimeSpan.FromSeconds(8));
            return;
        }
        var plan = new DecorationPlanDocument
        {
            Plan = new DecorationAttachmentPlan
            {
                TargetPart = TargetPart,
                TargetBodyVariant = NormalizeBodyVariant(TargetBodyVariant),
                DualVariantMode = NormalizeDualVariantMode(DualVariantMode),
				ReplaceWhenSourcePartLayerMatches = ReplaceWhenSourcePartLayerMatches,
				SourcePartLayers = sourceUnits.Select(unit => DecorationPlanningDefaults.ToPartLayerKey(
					_allSourceUnits.First(item => item.TypeId == unit.TypeId && item.FileId == unit.FileId && item.MeshInfoIndex == unit.MeshInfoIndex).PartKind,
					_allSourceUnits.First(item => item.TypeId == unit.TypeId && item.FileId == unit.FileId && item.MeshInfoIndex == unit.MeshInfoIndex).Layer)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                TargetModGuids = targetModGuids
            }
        };
        var displayName = IsEditing ? SourceName : $"{SourceName} - 装饰";
        var task = _backgroundTasks?.Enqueue(
            BackgroundTaskKind.Other,
            "生成装饰 Mod",
            displayName,
            "装饰生成",
            presentation: BackgroundTaskPresentation.Foreground);
        task?.MarkRunning("正在准备来源模型");
        var progress = task is null ? null : new Progress<DecorationOperationProgress>(item =>
        {
            task.UpdateStage(item.Stage);
            task.UpdateProgress(item.Fraction);
        });
        try
        {
            if (IsEditing)
            {
                plan.Version = 3;
                plan.SourceStorageMode = "PatchSnapshot";
                plan.SourceUnits = sourceUnits;
                await _library.UpdateDecorationPlanAsync(_editingDecorationId!, plan);
                State = "装饰 Mod 已保存；已重建启用它的主体。";
                if (task is null) _notifications.Show("装饰 Mod 已保存。");
                task?.MarkCompleted();
                return;
            }
            if (AutoImport)
            {
                var created = await _library.CreateDecorationAsync(SourceModId, sourceUnits, plan, displayName, _preparedSourceEntries, default, progress);
                var libraryDirectory = _library.ResolveAbsolutePath(created.SourcePath);
                var outputDirectory = Path.Combine(OutputDirectory, SanitizeFileName(displayName));
                if (!string.IsNullOrWhiteSpace(libraryDirectory)) CopyDecorationFiles(libraryDirectory, outputDirectory);
                State = "装饰计划已导入模组库。";
                if (task is null) _notifications.Show($"已生成装饰 Mod：{created.Name}");
                task?.MarkCompleted();
                if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.OpenModDetails(created.Guid);
                return;
            }

            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, SanitizeFileName(displayName));
            var source = _library.Snapshot.Nodes.Values.Single(node => node.Id.Value.ToString("N") == SourceModId);
            plan.Name = displayName;
            plan.Version = 3;
            plan.SourceStorageMode = "PatchSnapshot";
            plan.SourceUnits = sourceUnits.Select(unit => new DecorationSourceUnit { TypeId = unit.TypeId, FileId = unit.FileId, MeshInfoIndex = unit.MeshInfoIndex, BodyVariant = unit.BodyVariant, Layer = unit.Layer, IsCulling = unit.IsCulling }).ToList();
            await new DecorationPatchSnapshotService(_modFileResolver, _library.InformationReader)
                .CaptureAsync(source, _library.ModsRootDirectory, sourceUnits, path, _preparedSourceEntries, default, progress);
            await File.WriteAllTextAsync(Path.Combine(path, "decoration.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            State = $"计划已写出：{path}";
            if (task is null) _notifications.Show("装饰计划已写入输出目录。");
            task?.MarkCompleted();
        }
        catch (Exception exception)
        {
            State = $"生成失败：{exception.Message}";
            LogService.Error($"装饰 Mod 生成失败：来源={SourceModId}，错误={exception}");
            if (task is null) _notifications.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(10));
            task?.MarkFailed(exception.Message);
        }
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string NormalizeBodyVariant(string value)
        => value.Contains("健") ? "Stocky" : value.Contains("纤") ? "Slim" : "Dual";

    private static void CopyDecorationFiles(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static string NormalizeDualVariantMode(string value)
        => value.Contains("全部") ? "ApplyAllToBoth" : "AutoAssign";
}

public sealed class DecorationSourceUnitItem : BaseViewModel
{
    private readonly Action _changed;
    private bool _isSelected;
    public DecorationSourceUnitItem(string archiveId, EquipmentUnitPart part, Action changed)
    {
		ArchiveId = archiveId;
        Part = part;
        TypeId = part.UnitAssetKey.TypeId;
        FileId = part.UnitAssetKey.FileId;
        _changed = changed;
    }
    public EquipmentUnitPart Part { get; }
	public string ArchiveId { get; }
    public ulong TypeId { get; }
    public ulong FileId { get; }
    public int MeshInfoIndex => Part.MeshInfoIndex;
    public string Label => $"0x{FileId:x16}";
    public UnitMeshPartKind PartKind => Part.PartKind;
    public UnitMeshBodyVariant BodyVariant => Part.BodyVariant;
    public UnitMeshPartLayer Layer => Part.Layer;
    public bool IsCulling => Part.IsCullingMesh || Part.Layer == UnitMeshPartLayer.Culling;
    public string StoredBitsText => Part.StoredBytes <= 0 ? "未知" : $"{Part.StoredBytes * 8:N0} bit";
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }
}

public sealed class DecorationTargetModItem : BaseViewModel, IModListSelectable
{
    private readonly Action _changed;
    private bool _isSelected;
    private bool _isVisible = true;
    public DecorationTargetModItem(string modId, string name, string? description, string? image, bool isOption, Action changed)
    {
        ModId = modId;
        Name = name;
        Description = description;
        Image = image;
        IsOption = isOption;
        _changed = changed;
    }
    public string ModId { get; }
    public string SelectionKey => ModId;
    public bool IsDecoration => false;
    public bool IsOption { get; }
    public string Name { get; }
    public string? Description { get; }
    public string? Image { get; }
    public string? ImagePath => Image;
    public string AssetSummaryText => "";
    public bool IsModelOutdated => false;
    public string UserStatusTitle => "候选 Mod";
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) _changed(); } }
}
