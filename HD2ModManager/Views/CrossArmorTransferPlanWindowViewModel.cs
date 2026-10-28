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

	public IReadOnlyList<CrossArmorTransferEquipmentRow> SourceArmorChoices => sourceArmorChoices ??= BuildSourceChoices("Armor");
	public IReadOnlyList<CrossArmorTransferEquipmentRow> SourceHelmetChoices => sourceHelmetChoices ??= BuildSourceChoices("Helmet");
	public CrossArmorCandidateOutputPageViewModel? CandidateOutput { get => candidateOutput; private set { if (ReferenceEquals(candidateOutput, value)) return; candidateOutput = value; OnPropertyChanged(); } }
	public IReadOnlyList<CrossArmorTransferEquipmentRow> TargetChoices { get; }
	/// <summary>左下角"来源部件"表：来源 Patch 里的全部 unit，不等网格解析。</summary>
	public BulkObservableCollection<CrossArmorSourceUnitRow> SourceUnitRows { get; } = new();
	public bool IsScanningSourceUnits
	{
		get => isScanningSourceUnits;
		private set
		{
			if (!SetField(ref isScanningSourceUnits, value)) return;
			OnPropertyChanged(nameof(CanRescanSourceUnits));
			RescanSourceUnitsCommand.RaiseCanExecuteChanged();
		}
	}
	public string SourceScanState { get => sourceScanState; private set => SetField(ref sourceScanState, value); }
	public bool CanRescanSourceUnits => !isScanningSourceUnits && sourceScan is not null;
	public RelayCommand RescanSourceUnitsCommand { get; }
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
	public bool IsPlanning { get => isPlanning; private set { if (isPlanning == value) return; isPlanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerateCandidate)); } }
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
		: GetManualSourceChoices(SelectedTargetMapping);

	// Keep same-part sources at the top for the two quick actions, but retain every
	// readable source so the context menu can still apply an intentional cross-part
	// override when automatic prediction is not suitable.
	public IReadOnlyList<EquipmentUnitPart> GetManualSourceChoices(CrossArmorTransferMapping target)
	{
		ArgumentNullException.ThrowIfNull(target);
		return SourceParts
			.Where(IsUsableManualSource)
			.OrderByDescending(part => part.PartKind == target.Target.PartKind)
			.ThenByDescending(part => part.Layer == target.Target.Layer)
			.ThenByDescending(part => part.BodyVariant == target.Target.BodyVariant)
			.ThenByDescending(part => part.BodyVariant == UnitMeshBodyVariant.Any)
			.ThenByDescending(part => part.HasRenderableGeometry)
			.ThenByDescending(part => part.TriangleCount)
			.ThenByDescending(part => part.VertexCount)
			.ThenByDescending(part => part.StoredBytes)
			.ThenBy(part => part.PartKind)
			.ThenBy(part => part.Layer)
			.ThenBy(part => part.BodyVariant)
			.ThenBy(part => part.UnitAssetKey.FileId)
			.ThenBy(part => part.MeshInfoIndex)
			.ToArray();
	}

	private static bool IsUsableManualSource(EquipmentUnitPart part)
		=> part.PartKind != UnitMeshPartKind.Unknown
			&& part.Confidence == 100
			&& !part.IsCullingMesh;

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
	public bool CanGenerateCandidate => plan?.CanContinue == true && !IsPlanning && !candidateGenerationRunning;
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
	private IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates;
	private readonly IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates;
	private readonly IModInformationReader informationReader;
	private bool targetReplacementFactsAvailable;
	private readonly Func<CancellationToken, Task<GameDataArchiveBrowserSnapshot?>>? targetReplacementLoader;
	// 清单级事实已经把全部 unit 键给我们了；网格解析（判断可转移/极小化）可以晚一步做。
	private readonly Func<CancellationToken, Task<ModEquipmentSourceFacts>>? sourceScan;
	private IReadOnlyList<CrossArmorTransferEquipmentRow>? sourceArmorChoices;
	private IReadOnlyList<CrossArmorTransferEquipmentRow>? sourceHelmetChoices;
	private bool isScanningSourceUnits;
	private string sourceScanState = "待计算可见部件";
	/// <summary>最近一次重测的失败原因；有值时汇总条显示失败，而不是"0 / N"。</summary>
	private string? sourceScanFailure;

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
		IModInformationReader? informationReader = null,
		IReadOnlyList<AssetKey>? sourceUnitKeys = null,
		Func<CancellationToken, Task<ModEquipmentSourceFacts>>? sourceScan = null)
	{
		this.catalogService = catalogService;
		this.sourceCandidates = sourceCandidates;
		this.targetCandidates = targetCandidates;
		this.targetReplacementLoader = targetReplacementLoader;
		// The plan may lazily request the source TOC. Keep that read behind the
		// library's shared invalidation and diagnostics boundary.
		this.informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
		this.sourceScan = sourceScan;
		Title = "跨护甲计划";
		SourcePatchTocPath = sourcePatchTocPath;
		GameDataDirectory = gameDataDirectory;
		Paths = paths ?? throw new ArgumentNullException(nameof(paths));
		PreparedSourceEntries = preparedSourceEntries ?? throw new ArgumentNullException(nameof(preparedSourceEntries));
		var overlays = targetReplacementSnapshot is not null && !targetReplacementSnapshot.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)
			? targetReplacementSnapshot.Archives
			.ToDictionary(item => item.Archive.PackageName, item => item.Overlay, StringComparer.OrdinalIgnoreCase)
			: null;
		targetReplacementFactsAvailable = overlays is not null && targetCandidates.Where(entry => !entry.IsCustomModel).All(entry => overlays.ContainsKey(entry.ArchiveId));
		TargetChoices = targetCandidates.Select(entry => new CrossArmorTransferEquipmentRow(entry, IsConfirmedUnreplaced(entry, overlays))).ToArray();
		foreach (var target in TargetChoices) target.PropertyChanged += OnTargetChoicePropertyChanged;
		RefreshPlanCommand = new RelayCommand(_ => QueueRefreshPlan(), _ => !IsPlanning);
		ApplyManualMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null && SelectedManualSource is not null) SetManualMapping(SelectedTargetMapping, SelectedManualSource); }, _ => CanEditSelectedMapping && SelectedManualSource is not null);
		SuppressSelectedMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null) SuppressAutomaticMapping(SelectedTargetMapping); }, _ => CanEditSelectedMapping);
		RestoreSelectedMappingCommand = new RelayCommand(_ => { if (SelectedTargetMapping is not null) RestoreAutomaticMapping(SelectedTargetMapping); }, _ => CanEditSelectedMapping);
		SelectAllTargetsCommand = new RelayCommand(_ => SetAllTargetsSelected(true));
		InvertTargetSelectionCommand = new RelayCommand(_ => SetAllTargetsSelected(null));
		SelectAllUnreplacedTargetsCommand = new RelayCommand(_ => SelectAllUnreplacedTargets(), _ => CanSelectAllUnreplacedTargets);
		RescanSourceUnitsCommand = new RelayCommand(_ => _ = ScanSourceUnitsAsync(), _ => CanRescanSourceUnits);
		BuildSourceUnitRows(sourceUnitKeys ?? sourceCandidates.SelectMany(entry => entry.Parts).Select(part => part.UnitAssetKey).Distinct().ToArray());
		SelectedSourceArmor = SourceArmorChoices.FirstOrDefault()?.Entry;
		SelectedSourceHelmet = SourceHelmetChoices.FirstOrDefault()?.Entry;
		if (targetReplacementSnapshot is null && targetReplacementLoader is not null)
			_ = LoadTargetReplacementFactsAsync();
		// 清单级打开时还没有任何来源候选（网格没解析），立刻在后台补一次。
		if (this.sourceScan is not null && sourceCandidates.Count == 0)
			_ = ScanSourceUnitsAsync();
	}

	/// <summary>
	/// 用"来源 Patch 的全部 Unit 键"建表。部位/层/身形来自注册表格子（查库即得），
	/// 可见性留空等后台补——这一步不解析任何网格，所以页面能瞬间打开。
	/// </summary>
	private void BuildSourceUnitRows(IReadOnlyList<AssetKey> unitKeys)
	{
		// 全量装备目录里有每个 unit 的格子；不在其中的就是"未定位"。
		var byUnit = new Dictionary<AssetKey, EquipmentUnitPart>();
		foreach (var entry in targetCandidates)
		{
			foreach (var part in entry.Parts)
			{
				if (!byUnit.TryGetValue(part.UnitAssetKey, out var existing)
					|| (part.Cell.IsKnown && !existing.Cell.IsKnown))
					byUnit[part.UnitAssetKey] = part;
			}
		}

		// 已经知道可转移的 unit（清单级打开时通常为空；重测后会有值）。
		var transferable = sourceCandidates
			.SelectMany(entry => entry.Parts)
			.Select(part => part.UnitAssetKey)
			.ToHashSet();

		var rows = unitKeys
			.Select(key => byUnit.TryGetValue(key, out var part)
				? new CrossArmorSourceUnitRow(key, part.Cell, part.BodyVariant)
				: new CrossArmorSourceUnitRow(key, ArmorCell.Unknown, UnitMeshBodyVariant.Unknown))
			.OrderBy(row => row.Cell.IsKnown ? row.Cell.Slot : int.MaxValue)
			.ThenBy(row => row.Cell.IsKnown ? row.Cell.Layer : int.MaxValue)
			.ThenBy(row => row.UnitAssetKey.FileId)
			.ToArray();

		foreach (var row in rows)
		{
			if (row.IsCape) row.MarkUnscanned("披风不参与");
			else if (row.IsUnplaced) row.MarkUnscanned("注册表里没有这个 unit");
			else if (transferable.Contains(row.UnitAssetKey)) row.ApplyScan(true, "可转移");
			else if (sourceCandidates.Count != 0) row.ApplyScan(false, "极小化占位");
		}

		SourceUnitRows.ReplaceWith(rows, ListTransitionKind.Replace);
		// 全部"未定位"是这条链路上最容易让人以为"算坏了"的状态：它不是计算失败，而是
		// 这些 unit 在注册表里没有权威部位。留一条日志，用户报障时能立刻区分两者。
		var unplaced = rows.Count(row => row.IsUnplaced);
		if (unplaced != 0)
			LogService.Info($"替换护甲来源部件：共 {rows.Length} 个 unit，其中 {unplaced} 个在注册表里查不到部位归属（不能作为来源）。");
		UpdateSourceScanState();
	}

	/// <summary>
	/// 重测一次可见部件：解析来源 Unit 的网格，判断哪些是真几何、哪些是极小化占位。
	/// 这是这条链路上唯一的重活（实测 2.5 秒），所以它不在页面打开的路径上。
	/// </summary>
	public async Task ScanSourceUnitsAsync()
	{
		if (sourceScan is null || IsScanningSourceUnits) return;
		IsScanningSourceUnits = true;
		sourceScanFailure = null;
		SourceScanState = "正在计算可见部件…";
		foreach (var row in SourceUnitRows) row.BeginScan();
		try
		{
			// 整体丢线程池：这一步要解析来源 Unit 的网格（实测 2.5 秒），绝不能压在 UI 线程上。
			// 续体不能靠 ConfigureAwait(true) 回 UI 线程——构造点一旦在后台线程上就没有
			// 同步上下文可回，改绑定集合会抛 CollectionView 线程异常。所以显式送回去。
			var facts = await Task.Run(() => sourceScan(CancellationToken.None)).ConfigureAwait(false);
			await ApplyOnUiThreadAsync(() =>
			{
				sourceCandidates = facts.SourceCandidates;
				// 来源候选变了，护甲/头盔下拉也要重建，并尽量保留用户原来的选择。
				var armorArchive = SelectedSourceArmor?.ArchiveId;
				var helmetArchive = SelectedSourceHelmet?.ArchiveId;
				sourceArmorChoices = null;
				sourceHelmetChoices = null;
				OnPropertyChanged(nameof(SourceArmorChoices));
				OnPropertyChanged(nameof(SourceHelmetChoices));
				SelectedSourceArmor = SourceArmorChoices.FirstOrDefault(row => string.Equals(row.Entry.ArchiveId, armorArchive, StringComparison.OrdinalIgnoreCase))?.Entry
					?? SourceArmorChoices.FirstOrDefault()?.Entry;
				SelectedSourceHelmet = SourceHelmetChoices.FirstOrDefault(row => string.Equals(row.Entry.ArchiveId, helmetArchive, StringComparison.OrdinalIgnoreCase))?.Entry
					?? SourceHelmetChoices.FirstOrDefault()?.Entry;
				BuildSourceUnitRows(facts.SourceUnitKeys.Count != 0
					? facts.SourceUnitKeys
					: SourceUnitRows.Select(row => row.UnitAssetKey).ToArray());
				QueueRefreshPlan();
			}).ConfigureAwait(false);
		}
		// 这里是整条链路上唯一的重活，任何异常都必须留下痕迹：以前只接 IO/数据异常，
		// 线程异常会从 fire-and-forget 的任务里逸出成"未观察任务异常"，用户看到的是
		// 表格永远停在"待计算"、一句提示都没有。
		catch (Exception exception)
		{
			LogService.Error($"替换护甲：计算来源可见部件失败：{exception}");
			// 只记在字段里，让 finally 的汇总去渲染——否则这里写的"计算失败"会被
			// UpdateSourceScanState 立刻覆盖掉，用户永远看不到失败原因。
			sourceScanFailure = exception.Message;
			foreach (var row in SourceUnitRows) row.MarkUnscanned("计算失败");
		}
		finally
		{
			IsScanningSourceUnits = false;
			UpdateSourceScanState();
		}
	}

	/// <summary>
	/// 汇总条：多少 unit 作为来源，以及"为什么一个都选不了"。
	/// </summary>
	/// <remarks>
	/// 表格里的 unit 全部"未定位"时，只写"0 / N"等于什么都不解释——用户会以为算坏了。
	/// 真正的原因是这些 unit 在注册表里查不到权威部位（自定义素体 / 新增 unit 没有
	/// GameData 归属），而按格子装箱的规划器只认权威部位，所以它们不能作为来源。
	/// </remarks>
	private void UpdateSourceScanState()
	{
		if (IsScanningSourceUnits) return;
		if (sourceScanFailure is { Length: > 0 } failure)
		{
			SourceScanState = $"计算失败：{failure}";
			return;
		}

		var enabled = SourceUnitRows.Count(row => row.IsEnabled);
		var total = SourceUnitRows.Count;
		if (total == 0)
		{
			SourceScanState = "来源 Patch 里没有 unit";
			return;
		}

		var unplaced = SourceUnitRows.Count(row => row.IsUnplaced);
		var summary = $"{enabled} / {total} 个 unit 作为来源";
		SourceScanState = unplaced == 0
			? summary
			: unplaced == total
				? $"{summary} · 这 {total} 个 unit 在注册表里查不到部位归属，不能作为来源"
				: $"{summary}（{unplaced} 个查不到部位归属，不能作为来源）";
	}

	/// <summary>用户在「作为来源」列点了一下之后重算计划。纯内存操作，不需要重新扫网格。</summary>
	public void RefreshPlanAfterSourceToggle()
	{
		UpdateSourceScanState();
		QueueRefreshPlan();
	}

	private IReadOnlyList<CrossArmorTransferEquipmentRow> BuildSourceChoices(string category)
		=> sourceCandidates
			.Where(entry => string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
			.Select(entry => new CrossArmorTransferEquipmentRow(entry))
			.ToArray();

	/// <summary>
	/// 只把"作为来源"打开的那些 unit 交给规划器。用户关掉的部件不参与自动装箱，
	/// 但它们仍然在表格里——这是用户唯一能精确控制"哪些部件要出现"的手段。
	/// </summary>
	private IReadOnlyList<EquipmentUnitCatalogEntry> EnabledSourceCandidates()
	{
		var enabled = SourceUnitRows.Where(row => row.IsEnabled).Select(row => row.UnitAssetKey).ToHashSet();
		if (enabled.Count == 0) return [];
		return sourceCandidates
			.Select(entry => entry with { Parts = entry.Parts.Where(part => enabled.Contains(part.UnitAssetKey)).ToArray() })
			.Where(entry => entry.Parts.Count != 0)
			.ToArray();
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
			targetReplacementFactsAvailable = TargetChoices.Where(target => !target.Entry.IsCustomModel).All(target => overlays.ContainsKey(target.Entry.ArchiveId));
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
		if (current.Entry.IsCustomModel) yield break;
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
		RestoreAutomaticMapping(target);
	}

	public void RestoreAutomaticMapping(CrossArmorTransferMapping target)
	{
		ArgumentNullException.ThrowIfNull(target);
		var removedManualMapping = manualMappings.Remove(target.PhysicalTarget);
		var removedSuppression = suppressedTargets.Remove(target.PhysicalTarget);
		if (removedManualMapping || removedSuppression) QueueRefreshPlan();
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

	/// <summary>
	/// 把"改绑定集合"的那段收尾送回 UI 线程执行。
	/// </summary>
	/// <remarks>
	/// <see cref="SourceUnitRows"/> 与 <see cref="TargetArchiveFilters"/> 直接绑在
	/// DataGrid / ComboBox 上，而 WPF 的 CollectionView 只允许在**创建它的** Dispatcher
	/// 线程上收到 CollectionChanged；换个线程改就抛 <see cref="NotSupportedException"/>，
	/// 消息是"该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection
	/// 进行的更改"。这条链路的入口是后台线程（见
	/// <c>ModDetailsPageViewModel.PlanCrossArmorTransferAsync</c>），所以这里显式兜一道：
	/// 只要构造点或调用点再被挪到后台，页面也不会静默失效。
	/// 没有 Application 时（单元测试、CLI 宿主）直接就地执行。
	/// </remarks>
	private static Task ApplyOnUiThreadAsync(Action apply)
	{
		ArgumentNullException.ThrowIfNull(apply);
		var dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher is null || dispatcher.CheckAccess())
		{
			apply();
			return Task.CompletedTask;
		}
		return dispatcher.InvokeAsync(apply).Task;
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
			var enabledSourceCandidates = EnabledSourceCandidates();
			var nextPlan = await Task.Run(() => catalogService.CreatePlanAsync(enabledSourceCandidates, targetCandidates, SelectedSource?.ArchiveId, SelectedSourceBodyVariant, BodyVariantPreference, LayerPreference, targetIds, manualMappings.Values.ToArray(), suppressedTargets.Select(target => new CrossArmorManualSuppression(target)).ToArray(), AllowManualMappings, additionalSources, writePlannerDebugLog: true, plannerKind: CrossArmorPlannerKind.Cells, cancellationToken: cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
			await ApplyOnUiThreadAsync(() =>
			{
				// 代次检查必须在 UI 线程上重做一次：规划在池线程跑完之后，用户可能已经又点了一次。
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
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) { }
		catch (Exception exception)
		{
			if (ReferenceEquals(planCancellation, currentCancellation) && !cancellationToken.IsCancellationRequested)
			{
				plan = null;
				PlanState = $"计划读取失败：{exception.Message}";
				LogService.Error($"替换护甲计划：重新规划失败。原因={exception}");
			}
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
	public string Display => Entry.IsCustomModel
		? $"{Entry.DisplayName}（自定义素体，{Entry.Parts.Count} 个启用组件）"
		: $"{Entry.DisplayName}（{Entry.Category}，{Entry.Parts.Count} 个可见部件）";
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
