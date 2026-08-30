using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Encodings.Web;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// Purpose: Presents a no-write source-to-target equipment transfer preview before cross-armor Patch output is implemented.
public sealed class CrossArmorTransferPlanWindowViewModel : PageViewModel
{
	private EquipmentUnitCatalogEntry? selectedSourceArmor;
	private EquipmentUnitCatalogEntry? selectedSourceHelmet;
	private UnitMeshBodyVariant? selectedSourceBodyVariant = UnitMeshBodyVariant.Any;
	private CrossArmorBodyVariantPreference bodyVariantPreference = CrossArmorBodyVariantPreference.Slim;
	private CrossArmorLayerPreference layerPreference = CrossArmorLayerPreference.Armor;
	private bool allowManualMappings;
	private bool autoHideUnmappedTargetUnits = true;
	private CrossArmorTransferPlan? plan;
	private readonly Dictionary<CrossArmorPhysicalTargetKey, CrossArmorManualMapping> manualMappings = new();
	private readonly HashSet<CrossArmorPhysicalTargetKey> suppressedTargets = new();
	private bool candidateGenerationRunning;
	private CancellationTokenSource? planCancellation;
	private int planGeneration;
	private bool isPlanning;
	private string planState = "正在准备跨护甲计划。";
	private CrossArmorTransferMapping? selectedTargetMapping;
	private EquipmentUnitPart? selectedManualSource;
	private CrossArmorTargetArchiveFilterOption? selectedTargetArchiveFilter;
	private CrossArmorCandidateOutputPageViewModel? candidateOutput;
	private bool autoSelectSharedParts = true;
	private bool autoSelectMatchingHelmet = true;
	private bool applyingTargetSelection;

	public IReadOnlyList<CrossArmorTransferEquipmentRow> SourceArmorChoices { get; }
	public IReadOnlyList<CrossArmorTransferEquipmentRow> SourceHelmetChoices { get; }
	public CrossArmorCandidateOutputPageViewModel? CandidateOutput { get => candidateOutput; private set { if (ReferenceEquals(candidateOutput, value)) return; candidateOutput = value; OnPropertyChanged(); } }
	public IReadOnlyList<CrossArmorTransferEquipmentRow> TargetChoices { get; }
	public bool AutoSelectSharedParts
	{
		get => autoSelectSharedParts;
		set { if (autoSelectSharedParts == value) return; autoSelectSharedParts = value; OnPropertyChanged(); }
	}
	public bool AutoSelectMatchingHelmet
	{
		get => autoSelectMatchingHelmet;
		set { if (autoSelectMatchingHelmet == value) return; autoSelectMatchingHelmet = value; OnPropertyChanged(); }
	}
	public string SourcePatchTocPath { get; }
	public string GameDataDirectory { get; }
	public StoragePaths Paths { get; }
	public IModInformationReader InformationReader => informationReader;
	public IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> PreparedSourceEntries { get; private set; }
	private Task? preparedEntriesTask;
	public RelayCommand RefreshPlanCommand { get; }
	public RelayCommand ApplyManualMappingCommand { get; }
	public RelayCommand SuppressSelectedMappingCommand { get; }
	public RelayCommand RestoreSelectedMappingCommand { get; }
	public RelayCommand SelectAllTargetsCommand { get; }
	public RelayCommand InvertTargetSelectionCommand { get; }
	public RelayCommand SelectAllUnreplacedTargetsCommand { get; }
	public bool CanSelectAllUnreplacedTargets => targetReplacementFactsAvailable;
	public bool IsPlanning { get => isPlanning; private set { if (isPlanning == value) return; isPlanning = value; OnPropertyChanged(); } }
	public string PlanState { get => planState; private set { if (planState == value) return; planState = value; OnPropertyChanged(); } }
	public CrossArmorTransferMapping? SelectedTargetMapping
	{
		get => selectedTargetMapping;
		set
		{
			if (ReferenceEquals(selectedTargetMapping, value)) return;
			selectedTargetMapping = value;
			selectedManualSource = null;
			OnPropertyChanged();
			OnPropertyChanged(nameof(ManualSourceChoices));
			OnPropertyChanged(nameof(SelectedManualSource));
			RaiseManualMappingCommandStates();
		}
	}
	public EquipmentUnitPart? SelectedManualSource
	{
		get => selectedManualSource;
		set { if (selectedManualSource == value) return; selectedManualSource = value; OnPropertyChanged(); RaiseManualMappingCommandStates(); }
	}
	public IReadOnlyList<EquipmentUnitPart> ManualSourceChoices => SelectedTargetMapping is null
		? Array.Empty<EquipmentUnitPart>()
		: SourceParts.Where(part => part.PartKind == SelectedTargetMapping.Target.PartKind).ToArray();
	public bool CanEditSelectedMapping => SelectedTargetMapping is not null;
	public CrossArmorBodyVariantPreference BodyVariantPreference
	{
		get => bodyVariantPreference;
		set { if (bodyVariantPreference == value) return; bodyVariantPreference = value; OnPropertyChanged(); QueueRefreshPlan(); }
	}
	public CrossArmorLayerPreference LayerPreference
	{
		get => layerPreference;
		set { if (layerPreference == value) return; layerPreference = value; OnPropertyChanged(); QueueRefreshPlan(); }
	}
	public bool AllowManualMappings
	{
		get => allowManualMappings;
		set
		{
			if (allowManualMappings == value) return;
			allowManualMappings = value;
			OnPropertyChanged();
			RaiseManualMappingCommandStates();
			QueueRefreshPlan();
		}
	}
	public bool AutoHideUnmappedTargetUnits
	{
		get => autoHideUnmappedTargetUnits;
		set
		{
			if (autoHideUnmappedTargetUnits == value) return;
			autoHideUnmappedTargetUnits = value;
			OnPropertyChanged();
		}
	}
	// Cross-armor production is intentionally locked to direct source Unit
	// reuse.  Canonical remains available to Same-key repair and other generic
	// Patch workflows, but is no longer a production route from this page.
	public bool DirectSourceUnitReuse => true;
	public EquipmentUnitCatalogEntry? SelectedSourceArmor
	{
		get => selectedSourceArmor;
		set
		{
			if (ReferenceEquals(selectedSourceArmor, value)) return;
			selectedSourceArmor = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SelectedSource));
			OnPropertyChanged(nameof(SourceParts));
			OnPropertyChanged(nameof(ManualSourceChoices));
			QueueRefreshPlan();
		}
	}
	public EquipmentUnitCatalogEntry? SelectedSourceHelmet
	{
		get => selectedSourceHelmet;
		set
		{
			if (ReferenceEquals(selectedSourceHelmet, value)) return;
			selectedSourceHelmet = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SelectedSource));
			OnPropertyChanged(nameof(SourceParts));
			OnPropertyChanged(nameof(ManualSourceChoices));
			QueueRefreshPlan();
		}
	}
	public EquipmentUnitCatalogEntry? SelectedSource => SelectedSourceArmor ?? SelectedSourceHelmet;
	public UnitMeshBodyVariant? SelectedSourceBodyVariant
	{
		get => selectedSourceBodyVariant;
		set
		{
			if (selectedSourceBodyVariant == value) return;
			selectedSourceBodyVariant = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SourceParts));
			OnPropertyChanged(nameof(ManualSourceChoices));
			QueueRefreshPlan();
		}
	}
	public IReadOnlyList<EquipmentUnitPart> SourceParts => new[] { SelectedSourceArmor, SelectedSourceHelmet }.Where(source => source is not null).SelectMany(source => source!.Parts)
		.GroupBy(part => new { part.UnitAssetKey, part.PartKind, part.Layer, part.BodyVariant })
		.Select(group => group.OrderByDescending(part => part.StoredBytes).ThenBy(part => part.MeshInfoIndex).First())
		.Where(part => SelectedSourceBodyVariant is null or UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any || part.BodyVariant == SelectedSourceBodyVariant || part.BodyVariant == UnitMeshBodyVariant.Any)
		.OrderBy(part => part.PartKind).ThenBy(part => part.Layer).ThenBy(part => part.UnitAssetKey.FileId).ToArray()
		?? Array.Empty<EquipmentUnitPart>();
	public IReadOnlyList<CrossArmorTransferMapping> TargetMappings => plan?.Mappings
		.OrderBy(mapping => mapping.Target.PartKind)
		.ThenByDescending(mapping => mapping.Target.StoredBytes)
		.ThenBy(mapping => mapping.Target.Layer)
		.ThenBy(mapping => mapping.PhysicalTarget.UnitAssetKey.FileId)
		.ToArray()
		?? Array.Empty<CrossArmorTransferMapping>();
	public BulkObservableCollection<CrossArmorTargetArchiveFilterOption> TargetArchiveFilters { get; } = new();
	public CrossArmorTargetArchiveFilterOption? SelectedTargetArchiveFilter
	{
		get => selectedTargetArchiveFilter;
		set { if (selectedTargetArchiveFilter == value) return; selectedTargetArchiveFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredTargetMappings)); }
	}
	public IReadOnlyList<CrossArmorTransferMapping> FilteredTargetMappings => TargetMappings
		.Where(mapping => SelectedTargetArchiveFilter?.ArchiveId is null || mapping.UsedByArchiveIds.Contains(SelectedTargetArchiveFilter.ArchiveId, StringComparer.OrdinalIgnoreCase))
		.ToArray();
	public IReadOnlyList<CrossArmorTransferImpact> Impacts => plan?.Impacts ?? Array.Empty<CrossArmorTransferImpact>();
	public bool CanGenerateCandidate => plan?.CanContinue == true && !candidateGenerationRunning;
	public bool CandidateGenerationRunning
	{
		get => candidateGenerationRunning;
		set { if (candidateGenerationRunning == value) return; candidateGenerationRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerateCandidate)); }
	}
	public string Summary => plan is null
		? "请选择来源装备和至少一个目标装备。"
		: $"来源逻辑部件 {SourceParts.Count}；目标 Unit {TargetMappings.Count}；预计命中 {TargetMappings.Sum(mapping => mapping.HitCount)} 次，替换 {TargetMappings.Count(mapping => mapping.WillReplace)} 个 Unit；预计极小化 {TargetMappings.Count(mapping => !mapping.WillReplace)} 个 Unit；受影响装备 {Impacts.Select(impact => impact.ArchiveId).Distinct().Count()}。";
	public string Issues => plan is null ? string.Empty : string.Join(Environment.NewLine, plan.Issues.Select(issue => $"{issue.Severity}: {issue.Message}"));

	private readonly IEquipmentUnitCatalogService catalogService;
	private readonly IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates;
	private readonly IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates;
	private readonly IModInformationReader informationReader;
	private bool targetReplacementFactsAvailable;
	private readonly Func<CancellationToken, Task<GameDataArchiveBrowserSnapshot?>>? targetReplacementLoader;

	public CrossArmorTransferPlanWindowViewModel(
		IEquipmentUnitCatalogService catalogService,
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		string sourcePatchTocPath,
		string gameDataDirectory,
		IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> preparedSourceEntries,
		StoragePaths paths,
		GameDataArchiveBrowserSnapshot? targetReplacementSnapshot = null,
		Func<CancellationToken, Task<GameDataArchiveBrowserSnapshot?>>? targetReplacementLoader = null,
		IModInformationReader? informationReader = null)
	{
		this.catalogService = catalogService;
		this.sourceCandidates = sourceCandidates;
		this.targetCandidates = targetCandidates;
		this.targetReplacementLoader = targetReplacementLoader;
		// The plan may lazily request the source TOC. Keep that read behind the
		// library's shared invalidation and diagnostics boundary.
		this.informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
		Title = "跨护甲计划";
		SourcePatchTocPath = sourcePatchTocPath;
		GameDataDirectory = gameDataDirectory;
		Paths = paths ?? throw new ArgumentNullException(nameof(paths));
		PreparedSourceEntries = preparedSourceEntries ?? throw new ArgumentNullException(nameof(preparedSourceEntries));
		SourceArmorChoices = sourceCandidates.Where(entry => string.Equals(entry.Category, "Armor", StringComparison.OrdinalIgnoreCase)).Select(entry => new CrossArmorTransferEquipmentRow(entry)).ToArray();
		SourceHelmetChoices = sourceCandidates.Where(entry => string.Equals(entry.Category, "Helmet", StringComparison.OrdinalIgnoreCase)).Select(entry => new CrossArmorTransferEquipmentRow(entry)).ToArray();
		var overlays = targetReplacementSnapshot is not null && !targetReplacementSnapshot.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)
			? targetReplacementSnapshot.Archives
			.ToDictionary(item => item.Archive.PackageName, item => item.Overlay, StringComparer.OrdinalIgnoreCase)
			: null;
		targetReplacementFactsAvailable = overlays is not null && targetCandidates.All(entry => overlays.ContainsKey(entry.ArchiveId));
		TargetChoices = targetCandidates.Select(entry => new CrossArmorTransferEquipmentRow(entry, IsConfirmedUnreplaced(entry, overlays))).ToArray();
		foreach (var target in TargetChoices) target.PropertyChanged += OnTargetChoicePropertyChanged;
		RefreshPlanCommand = new RelayCommand(_ => QueueRefreshPlan(), _ => !IsPlanning);
		ApplyManualMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null && SelectedManualSource is not null) SetManualMapping(SelectedTargetMapping, SelectedManualSource); }, _ => CanEditSelectedMapping && SelectedManualSource is not null);
		SuppressSelectedMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null) SuppressAutomaticMapping(SelectedTargetMapping); }, _ => CanEditSelectedMapping);
		RestoreSelectedMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null) RestoreAutomaticMapping(SelectedTargetMapping); }, _ => CanEditSelectedMapping);
		SelectAllTargetsCommand = new RelayCommand(_ => SetAllTargetsSelected(true));
		InvertTargetSelectionCommand = new RelayCommand(_ => SetAllTargetsSelected(null));
		SelectAllUnreplacedTargetsCommand = new RelayCommand(_ => SelectAllUnreplacedTargets(), _ => CanSelectAllUnreplacedTargets);
		SelectedSourceArmor = SourceArmorChoices.FirstOrDefault()?.Entry;
		SelectedSourceHelmet = SourceHelmetChoices.FirstOrDefault()?.Entry;
		if (targetReplacementSnapshot is null && targetReplacementLoader is not null)
			_ = LoadTargetReplacementFactsAsync();
	}

	private async Task LoadTargetReplacementFactsAsync()
	{
		try
		{
			var snapshot = await targetReplacementLoader!(CancellationToken.None).ConfigureAwait(true);
			if (snapshot is null || snapshot.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)) return;
			var overlays = snapshot.Archives.ToDictionary(item => item.Archive.PackageName, item => item.Overlay, StringComparer.OrdinalIgnoreCase);
			foreach (var target in TargetChoices)
				target.IsConfirmedUnreplaced = IsConfirmedUnreplaced(target.Entry, overlays);
			targetReplacementFactsAvailable = TargetChoices.All(target => overlays.ContainsKey(target.Entry.ArchiveId));
			OnPropertyChanged(nameof(CanSelectAllUnreplacedTargets));
			SelectAllUnreplacedTargetsCommand.RaiseCanExecuteChanged();
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
		{
			LogService.Info($"替换护甲计划：延迟读取目标替换状态失败，辅助按钮保持不可用。原因={exception.Message}");
		}
	}

	public async Task EnsurePreparedSourceEntriesAsync(CancellationToken cancellationToken = default)
	{
		if (PreparedSourceEntries.Count != 0) return;
		preparedEntriesTask ??= LoadPreparedSourceEntriesAsync(cancellationToken);
		await preparedEntriesTask.ConfigureAwait(false);
	}

	private async Task LoadPreparedSourceEntriesAsync(CancellationToken cancellationToken)
	{
		var result = await informationReader.ReadPatchIndexAsync(
			new ModInformationReadRequest(
				SourcePatchTocPath,
				ModInformationRequestContext.Create(ModInformationCacheScope.None, operationName: "CrossArmorPlanPreparedEntries"),
				ContentView: ModInformationContentView.Source),
			cancellationToken).ConfigureAwait(false);
		if (result.Data is null)
		{
			var message = result.State.Diagnostics.Count == 0
				? "无法读取来源 Patch TOC。"
				: string.Join("；", result.State.Diagnostics.Select(issue => issue.Message));
			throw new InvalidDataException(message);
		}
		PreparedSourceEntries = result.Data.Entries;
		OnPropertyChanged(nameof(PreparedSourceEntries));
	}

	private void OnTargetChoicePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (eventArgs.PropertyName != nameof(CrossArmorTransferEquipmentRow.IsSelected)) return;
		if (applyingTargetSelection) return;
		if (sender is CrossArmorTransferEquipmentRow changed) ApplyAutomaticTargetSelection(changed);
		QueueRefreshPlan();
	}

	private void ApplyAutomaticTargetSelection(CrossArmorTransferEquipmentRow changed)
	{
		if (!AutoSelectSharedParts && !AutoSelectMatchingHelmet) return;
		applyingTargetSelection = true;
		try
		{
			var pending = new Queue<CrossArmorTransferEquipmentRow>();
			var visited = new HashSet<CrossArmorTransferEquipmentRow>();
			pending.Enqueue(changed);
			while (pending.Count > 0)
			{
				var current = pending.Dequeue();
				if (!visited.Add(current)) continue;
				foreach (var related in RelatedTargetChoices(current))
				{
					if (related.IsSelected == current.IsSelected) continue;
					related.IsSelected = current.IsSelected;
					pending.Enqueue(related);
				}
			}
		}
		finally { applyingTargetSelection = false; }
	}

	private IEnumerable<CrossArmorTransferEquipmentRow> RelatedTargetChoices(CrossArmorTransferEquipmentRow current)
	{
		if (AutoSelectSharedParts && IsArmor(current.Entry))
		{
			var units = current.Entry.Parts.Select(part => part.UnitAssetKey).ToHashSet();
			foreach (var candidate in TargetChoices.Where(row => !ReferenceEquals(row, current) && IsArmor(row.Entry) && row.Entry.Parts.Any(part => units.Contains(part.UnitAssetKey)))) yield return candidate;
		}
		if (AutoSelectMatchingHelmet)
		{
			var counterpartCategory = IsArmor(current.Entry) ? "Helmet" : "Armor";
			foreach (var candidate in TargetChoices.Where(row => string.Equals(row.Entry.Category, counterpartCategory, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Entry.DisplayName, current.Entry.DisplayName, StringComparison.OrdinalIgnoreCase))) yield return candidate;
		}
	}

	private static bool IsArmor(EquipmentUnitCatalogEntry entry) => string.Equals(entry.Category, "Armor", StringComparison.OrdinalIgnoreCase);

	public void ExportJson(string outputPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		QueueRefreshPlan();
		var export = new
		{
			format = "hd2-cross-armor-transfer-plan-v1",
			exportedAtUtc = DateTimeOffset.UtcNow,
			isReadOnlyPlan = true,
			selectedSourceArchiveId = SelectedSource?.ArchiveId,
			replacementRoute = "DirectSourceUnitReuse",
			selectedSourceBodyVariant = SelectedSourceBodyVariant?.ToString() ?? "Any",
			bodyVariantPreference = BodyVariantPreference.ToString(),
			layerPreference = LayerPreference.ToString(),
			manualSuppressions = suppressedTargets.Select(target => new { unitAssetKey = ToAssetKeyExport(target.UnitAssetKey) }).ToArray(),
			selectedTargetArchiveIds = TargetChoices.Where(row => row.IsSelected).Select(row => row.Entry.ArchiveId).ToArray(),
			sourceArmorChoices = SourceArmorChoices.Select(row => ToChoiceExport(row)).ToArray(),
			sourceHelmetChoices = SourceHelmetChoices.Select(row => ToChoiceExport(row)).ToArray(),
			targetChoices = TargetChoices.Select(row => ToChoiceExport(row)).ToArray(),
			plan = plan is null ? null : new
			{
				selectedSource = plan.SelectedSource is null ? null : ToEquipmentExport(plan.SelectedSource),
				selectedTargets = plan.SelectedTargets.Select(ToEquipmentExport).ToArray(),
				mappings = plan.Mappings.Select(mapping => new
				{
					physicalTarget = new { unitAssetKey = ToAssetKeyExport(mapping.PhysicalTarget.UnitAssetKey) },
					target = ToPartExport(mapping.Target),
					source = mapping.Source is null ? null : ToPartExport(mapping.Source),
					mapping.WillReplace,
					mapping.HitCount,
					mapping.IsManual,
					mapping.IsSuppressed,
					mapping.UsedByArchiveIds,
					mapping.UsedByDisplayNames,
					mapping.Reason
				}).ToArray(),
				impacts = plan.Impacts.Select(impact => new
				{
					impact.ArchiveId,
					impact.DisplayName,
					sharedUnitAssetKey = ToAssetKeyExport(impact.SharedUnitAssetKey),
					partKind = impact.PartKind.ToString(),
					layer = impact.Layer.ToString()
				}).ToArray(),
				issues = plan.Issues.Select(issue => new { severity = issue.Severity.ToString(), issue.Code, issue.Message }).ToArray()
			}
		};
		File.WriteAllText(outputPath, JsonSerializer.Serialize(export, new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		}));
	}

	public void ExportAuditJson(string outputPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		if (plan is null) throw new InvalidOperationException("请先生成跨护甲计划。");
		var audit = new
		{
			format = "hd2-cross-armor-plan-audit-v1",
			exportedAtUtc = DateTimeOffset.UtcNow,
			selectedTargets = plan.SelectedTargets.Select(target => new { target.ArchiveId, target.DisplayName, target.Category }).ToArray(),
			summary = new
			{
				physicalTargetCount = plan.Mappings.Count,
				replacementCount = plan.Mappings.Count(mapping => mapping.WillReplace),
				hitCount = plan.Mappings.Sum(mapping => mapping.HitCount),
				sharedPhysicalTargetCount = plan.Mappings.Count(mapping => mapping.UsedByArchiveIds.Count > 1)
			},
			mappings = plan.Mappings.Select(mapping => new
			{
				physicalTarget = new { unitAssetKey = ToAssetKeyExport(mapping.PhysicalTarget.UnitAssetKey) },
				target = ToPartExport(mapping.Target),
				source = mapping.Source is null ? null : ToPartExport(mapping.Source),
				mapping.WillReplace,
				mapping.HitCount,
				mapping.Reason,
				mapping.UsedByArchiveIds,
				mapping.UsedByDisplayNames,
				sharedPhysicalTarget = mapping.UsedByArchiveIds.Count > 1,
				bodyVariantExact = mapping.Source?.BodyVariant == mapping.Target.BodyVariant,
				usesAnyBodyVariant = mapping.Source?.BodyVariant == UnitMeshBodyVariant.Any || mapping.Target.BodyVariant == UnitMeshBodyVariant.Any,
				layerExact = mapping.Source?.Layer == mapping.Target.Layer,
				semanticFamilyExact = mapping.Source is not null && SemanticFamily(mapping.Source.SemanticName) == SemanticFamily(mapping.Target.SemanticName)
			}).ToArray()
		};
		File.WriteAllText(outputPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
	}

	public void AttachCandidateOutput(CrossArmorCandidateOutputPageViewModel output) => CandidateOutput = output;

	public void SetManualMapping(CrossArmorTransferMapping target, EquipmentUnitPart source)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(source);
		suppressedTargets.Remove(target.PhysicalTarget);
		manualMappings[target.PhysicalTarget] = new CrossArmorManualMapping(target.PhysicalTarget, source.UnitAssetKey);
		QueueRefreshPlan();
	}

	public void SuppressAutomaticMapping(CrossArmorTransferMapping target)
	{
		ArgumentNullException.ThrowIfNull(target);
		manualMappings.Remove(target.PhysicalTarget);
		suppressedTargets.Add(target.PhysicalTarget);
		QueueRefreshPlan();
	}

	public void ClearManualMapping(CrossArmorTransferMapping target)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (manualMappings.Remove(target.PhysicalTarget)) QueueRefreshPlan();
	}

	public void RestoreAutomaticMapping(CrossArmorTransferMapping target)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (suppressedTargets.Remove(target.PhysicalTarget)) QueueRefreshPlan();
	}

	public void SetAllTargetsSelected(bool? selected)
	{
		applyingTargetSelection = true;
		try
		{
			foreach (var target in TargetChoices) target.IsSelected = selected ?? !target.IsSelected;
		}
		finally { applyingTargetSelection = false; }
		QueueRefreshPlan();
	}

	private void SelectAllUnreplacedTargets()
	{
		applyingTargetSelection = true;
		try
		{
			foreach (var target in TargetChoices.Where(target => target.IsConfirmedUnreplaced)) target.IsSelected = true;
		}
		finally { applyingTargetSelection = false; }
		QueueRefreshPlan();
	}

	private static bool IsConfirmedUnreplaced(EquipmentUnitCatalogEntry entry, IReadOnlyDictionary<string, GameDataArchiveOverlay>? overlays)
	{
		if (overlays is null || !overlays.TryGetValue(entry.ArchiveId, out var overlay)) return false;
		var effectiveUnitKeys = overlay.EffectiveAssets.Select(asset => asset.AssetKey).ToHashSet();
		return entry.Parts.Count > 0 && entry.Parts.All(part => !effectiveUnitKeys.Contains(part.UnitAssetKey));
	}

	private void QueueRefreshPlan()
	{
		_ = RefreshPlanAsync();
	}

	private async Task RefreshPlanAsync()
	{
		planCancellation?.Cancel();
		planCancellation?.Dispose();
		var currentCancellation = new CancellationTokenSource();
		planCancellation = currentCancellation;
		var cancellationToken = currentCancellation.Token;
		var generation = ++planGeneration;
		IsPlanning = true;
		PlanState = "正在重新规划来源与目标 Unit 部件。";
		RefreshPlanCommand.RaiseCanExecuteChanged();
		var targetIds = TargetChoices.Where(row => row.IsSelected).Select(row => row.Entry.ArchiveId).ToArray();
		try
		{
			var additionalSources = new[] { SelectedSourceArmor, SelectedSourceHelmet }.Where(source => source is not null && !ReferenceEquals(source, SelectedSource)).Select(source => source!.ArchiveId).ToArray();
			var nextPlan = await Task.Run(() => catalogService.CreatePlanAsync(sourceCandidates, targetCandidates, SelectedSource?.ArchiveId, SelectedSourceBodyVariant, BodyVariantPreference, LayerPreference, targetIds, manualMappings.Values.ToArray(), suppressedTargets.Select(target => new CrossArmorManualSuppression(target)).ToArray(), AllowManualMappings, additionalSources, cancellationToken: cancellationToken).AsTask(), cancellationToken);
			if (!ReferenceEquals(planCancellation, currentCancellation) || cancellationToken.IsCancellationRequested || generation != planGeneration) return;
			plan = nextPlan;
			PlanState = "计划已更新。";
			OnPropertyChanged(nameof(SourceParts));
			OnPropertyChanged(nameof(TargetMappings));
			RefreshTargetArchiveFilters();
			OnPropertyChanged(nameof(FilteredTargetMappings));
			OnPropertyChanged(nameof(Impacts));
			OnPropertyChanged(nameof(Summary));
			OnPropertyChanged(nameof(Issues));
			OnPropertyChanged(nameof(CanGenerateCandidate));
			OnPropertyChanged(nameof(ManualSourceChoices));
			RaiseManualMappingCommandStates();
		}
		catch (OperationCanceledException) { }
		catch (Exception exception)
		{
			if (ReferenceEquals(planCancellation, currentCancellation) && !cancellationToken.IsCancellationRequested) PlanState = $"计划读取失败：{exception.Message}";
		}
		finally
		{
			if (ReferenceEquals(planCancellation, currentCancellation) && generation == planGeneration)
			{
				IsPlanning = false;
				RefreshPlanCommand.RaiseCanExecuteChanged();
				SelectAllUnreplacedTargetsCommand.RaiseCanExecuteChanged();
			}
			if (ReferenceEquals(planCancellation, currentCancellation)) planCancellation = null;
			currentCancellation.Dispose();
		}
	}

	public CrossArmorTransferPlan? GetCurrentPlan() => plan;

	public override void Dispose()
	{
		planCancellation?.Cancel();
		planCancellation?.Dispose();
	}

	private void RaiseManualMappingCommandStates()
	{
		OnPropertyChanged(nameof(CanEditSelectedMapping));
		ApplyManualMappingCommand?.RaiseCanExecuteChanged();
		SuppressSelectedMappingCommand?.RaiseCanExecuteChanged();
		RestoreSelectedMappingCommand?.RaiseCanExecuteChanged();
	}

	private void RefreshTargetArchiveFilters()
	{
		var selectedArchiveIds = TargetChoices.Where(row => row.IsSelected).Select(row => row.Entry.ArchiveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var filters = new List<CrossArmorTargetArchiveFilterOption>
		{
			new CrossArmorTargetArchiveFilterOption("全部", null)
		};
		foreach (var archive in TargetMappings
			.Where(mapping => mapping.UsedByArchiveIds.Any(selectedArchiveIds.Contains))
			.SelectMany(mapping => mapping.UsedByArchiveIds)
			.Where(selectedArchiveIds.Contains)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase))
		{
			var friendlyName = TargetChoices.FirstOrDefault(choice => string.Equals(choice.Entry.ArchiveId, archive, StringComparison.OrdinalIgnoreCase))?.Entry.DisplayName ?? archive;
			filters.Add(new CrossArmorTargetArchiveFilterOption(friendlyName, archive));
		}
		TargetArchiveFilters.ReplaceWith(filters);
		selectedTargetArchiveFilter = TargetArchiveFilters[0];
		OnPropertyChanged(nameof(SelectedTargetArchiveFilter));
	}

	private static object ToEquipmentExport(EquipmentUnitCatalogEntry entry) => new
	{
		entry.ArchiveId,
		entry.Category,
		entry.DisplayName,
		parts = entry.Parts.Select(ToPartExport).ToArray()
	};

	private static object ToChoiceExport(CrossArmorTransferEquipmentRow row) => new
	{
		row.Entry.ArchiveId,
		row.Entry.Category,
		row.Entry.DisplayName,
		partCount = row.Entry.Parts.Count,
		row.IsSelected
	};

	private static object ToPartExport(EquipmentUnitPart part) => new
	{
		unitAssetKey = ToAssetKeyExport(part.UnitAssetKey),
		meshId = $"0x{part.MeshId:x8}",
		partKind = part.PartKind.ToString(),
		layer = part.Layer.ToString(),
		bodyVariant = part.BodyVariant.ToString(),
		part.SemanticName,
		part.Confidence,
		part.SharedArchiveIds
	};

	private static string SemanticFamily(string semanticName)
	{
		var name = semanticName.Trim().ToLowerInvariant();
		if (name.StartsWith("g_torso_arm_l_", StringComparison.Ordinal)) return "torso-arm-l";
		if (name.StartsWith("g_torso_arm_r_", StringComparison.Ordinal)) return "torso-arm-r";
		if (name.StartsWith("g_arm_l", StringComparison.Ordinal)) return "arm-l";
		if (name.StartsWith("g_arm_r", StringComparison.Ordinal)) return "arm-r";
		if (name.StartsWith("g_legs_hips_undergarment", StringComparison.Ordinal)) return "hips-undergarment";
		if (name.StartsWith("g_legs_hips", StringComparison.Ordinal)) return "hips";
		if (name.StartsWith("g_leg_undergarment_l", StringComparison.Ordinal)) return "leg-undergarment-l";
		if (name.StartsWith("g_leg_undergarment_r", StringComparison.Ordinal)) return "leg-undergarment-r";
		if (name.StartsWith("g_leg_l", StringComparison.Ordinal)) return "leg-l";
		if (name.StartsWith("g_leg_r", StringComparison.Ordinal)) return "leg-r";
		if (name.StartsWith("g_torso_undergarment", StringComparison.Ordinal)) return "torso-undergarment";
		if (name.StartsWith("g_torso", StringComparison.Ordinal)) return "torso";
		return name;
	}

	private static object ToAssetKeyExport(AssetKey key) => new
	{
		typeId = $"0x{key.TypeId:x16}",
		fileId = $"0x{key.FileId:x16}"
	};
}

// Purpose: Provides one selectable Armor or Helmet archive for the no-write transfer preview.
public sealed class CrossArmorTransferEquipmentRow : INotifyPropertyChanged
{
	private bool isSelected;
	public event PropertyChangedEventHandler? PropertyChanged;
	public EquipmentUnitCatalogEntry Entry { get; }
	public string Display => $"{Entry.DisplayName}（{Entry.Category}，{Entry.Parts.Count} 个可见部件）";
	public bool IsConfirmedUnreplaced { get; internal set; }
	public bool IsSelected
	{
		get => isSelected;
		set
		{
			if (isSelected == value) return;
			isSelected = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
		}
	}
	public CrossArmorTransferEquipmentRow(EquipmentUnitCatalogEntry entry, bool isConfirmedUnreplaced = false)
	{
		Entry = entry;
		IsConfirmedUnreplaced = isConfirmedUnreplaced;
	}
}

// Purpose: Keeps a friendly selected-target label separate from the archive ID required for plan filtering.
public sealed record CrossArmorTargetArchiveFilterOption(string DisplayName, string? ArchiveId);

// Purpose: Formats one Unit mesh fact for the simplified source list.
public sealed class CrossArmorSourcePartRow
{
	public CrossArmorSourcePartRow(EquipmentUnitPart part) => Part = part;
	public EquipmentUnitPart Part { get; }
	public string PartText => $"{PartName(Part.PartKind)} / {LayerName(Part.Layer)}";
	public string UnitText => $"0x{Part.UnitAssetKey.FileId:x16}";
	public string Detail => $"Unit 0x{Part.UnitAssetKey.FileId:x16}，{VariantName(Part.BodyVariant)}，{Part.SemanticName}";
	private static string PartName(UnitMeshPartKind kind) => kind.ToString();
	private static string LayerName(UnitMeshPartLayer layer) => layer.ToString();
	private static string VariantName(UnitMeshBodyVariant variant) => variant.ToString();
}
