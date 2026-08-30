using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    // 作用：展示单个 Mod 的派生信息与文件组概览。
    public class ModDetailsPageViewModel : PageViewModel
    {
        private readonly ModLibraryService _library;
        private readonly ProfileService _profiles;
        private readonly DerivedStateCoordinator _derivedState;
        private readonly NotificationService? _notifications;
        private readonly BackgroundTaskService? _backgroundTasks;
        private readonly SelectionCoordinator? _selection;
        private readonly IMaterialPackagingApplicationService _materialPackaging;
        private readonly IMaterialDeliveryFactsService _materialDeliveryFacts;
        private readonly IEquipmentUnitCatalogService _equipmentUnitCatalog;
		private readonly IModEquipmentSourceFactsReader _equipmentSourceFactsReader;
        private readonly IAdvancedModAnalysisService _advancedAnalysis;
        private readonly ISourceUnitEligibilityService _sourceUnitEligibility;
        private readonly IPatchGraphDiagnosticsService _patchGraphDiagnostics;
        private ModMaterialPackagingState? _materialState;
		private bool _sameKeyReconstructionRunning;
        private bool _advancedAnalysisRunning;
        private bool _advancedAnalysisReady;
		private bool _advancedAnalysisHasEquipment;
        private IReadOnlyList<PatchGroupAnalysis>? _cachedAdvancedAnalyses;
        private ModNodeId? _cachedAdvancedAnalysesNodeId;
        private MaterialDeliveryFacts? _cachedMaterialDeliveryFacts;
        private ModNodeId? _cachedMaterialDeliveryFactsNodeId;
        private bool _dependencyGraphTestRunning;
        private bool _dependencyGraphComparisonRunning;
        private bool _helmetMergeRunning;
        private readonly IAdvancedModAssetQueryService _advancedAssetQueryService;
		private readonly StoragePaths _paths;
        private readonly EventHandler<DerivedStateSnapshot> _snapshotChangedHandler;
        private readonly ModListStateController<HostAttachmentListState, ModCardViewModel> _hostAttachments;
        private CancellationTokenSource? _advancedDetailsCancellation;
        private bool _advancedDetailsLoaded;
        private bool _disposed;
        private IReadOnlyList<AdvancedModAssetRow> _allAdvancedAssets = Array.Empty<AdvancedModAssetRow>();
        private ModContentFacts? _detailContentFacts;
        private string _advancedAssetQuery = string.Empty;
        private bool _advancedOnlyIssues;
        private int _detailRequestGeneration;
        private ModUserStatus? _userStatus;
        private IReadOnlyList<HostAttachmentListState> _hostAttachmentStates = Array.Empty<HostAttachmentListState>();

        public string ModId { get; }
        public ModEntity? Mod { get; private set; }
        public string Name => Mod?.Name ?? "未找到 Mod";
        public string Description => Mod?.Description ?? string.Empty;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public string SourcePath => Mod?.SourcePath ?? string.Empty;
        public string Image => Mod?.Image ?? string.Empty;
        public int FileGroupCount => Mod?.FileGroups?.Count ?? 0;
        public string ProfileStatus { get; private set; } = "未加入当前配置";
        public string FileIntegritySummary { get; private set; } = "未检查";
        public string ConflictSummary { get; private set; } = "未检查";
        public string AssetTagsString { get; private set; } = "未解析";
        public string AssetListSummary { get; private set; } = "未解析";
        public string AssetOverrideSummary { get; private set; } = "未检查";
		public string MaterialDiagnosticSummary { get; private set; } = "当前没有活动配置或材质诊断正在更新。";
        public string UnitCompatibilitySummary { get; private set; } = "模型版本尚未检测。";
        public bool IsModelOutdated { get; private set; }
        public string DataIndexSummary { get; private set; } = "跨 Mod 资产索引尚未读取。";
        public string UserStatusTitle { get; private set; } = "状态未知";
        public string UserStatusSummary { get; private set; } = "正在读取状态。";
        public bool ShowUserStatusCard => CanUsePatchTools && _userStatus?.Kind == ModUserStatusKind.Overridden;
        public bool ShowOutdatedCompatibility => CanUsePatchTools && IsModelOutdated;
        public string MaterialPackagingSummary { get; private set; } = "材质操作基于导入后的轻量引用图；无需执行高级分析。";
        public string MaterialDeliverySummary { get; private set; } = "导入后的轻量引用图完成后即可读取材质交付事实。";
        public string SameKeyReconstructionSummary { get; private set; } = "仅更新失效 Unit，并将结果直接写入 Manager 的 Output 文件夹；不会自动导入或部署。";
        public string AdvancedAnalysisSummary { get; private set; } = "尚未执行高级分析；点击“模型解析”后才读取 Unit 完整结构。";
        public string DependencyGraphTestSummary { get; private set; } = "仅读取 Unit 材质绑定表与 Material 贴图表；结果会写入 logs。";
        public string DependencyGraphComparisonSummary { get; private set; } = "可用完整 Unit 解析对比轻量引用链的去重关系集合。";
        public bool CanRunAdvancedAnalysis => !_disposed && !_advancedAnalysisRunning && HasPatchGroups && TryGetCurrentNode(out _);
		public bool CanRunDependencyGraphTest => !_disposed && !_dependencyGraphTestRunning && TryGetCurrentNode(out _);
        public bool CanCompareDependencyGraph => !_disposed && !_dependencyGraphComparisonRunning && TryGetCurrentNode(out _);
        // 内嵌材质是否存在属于轻量 ReferenceGraph 事实；包装检查只负责写出前的最终安全复核。
        public bool IsOption => Mod?.IsOption == true;
        public bool IsReShade => Mod?.IsReShade == true;
        public bool IsStandardMod => !IsDecoration && !IsOption && !IsReShade;
        public bool HasPatchContent => Mod?.HasPatchContent == true;
        // Option Mods are still real patch-bearing packages. Only decorations
        // and empty logical hosts are excluded from patch tools.
        public bool CanUsePatchTools => !IsDecoration && HasPatchContent;
        public bool CanPatchResourceRepackaging => !_disposed && CanUsePatchTools && TryGetCurrentNode(out _);
        public bool CanRepairPatch => HasPatchContent;
        public bool CanShowDecorationPlan => !IsDecoration && HasPatchContent;
        public bool CanSplitEmbeddedMaterials => !_disposed && CanUsePatchTools
            && ((_materialState?.HasEmbeddedMaterials == true) || (_cachedMaterialDeliveryFacts?.EmbeddedMaterialCount > 0))
            && TryGetCurrentNode(out _);
        public bool CanReplaceEmbeddedMaterials => !_disposed && CanUsePatchTools && TryGetCurrentNode(out _);
        public bool CanEmbedExternalMaterials => !_disposed && CanUsePatchTools && TryGetCurrentNode(out _);
        public bool CanRebuildSameKey => !_disposed && CanRepairPatch && !_sameKeyReconstructionRunning && TryGetCurrentNode(out _);
        public bool CanPlanCrossArmorTransfer => !_disposed && CanUsePatchTools && TryGetCurrentNode(out _);
        public bool IsHelmetToArmorMerged => Mod?.IsHelmetToArmorMerged == true;
        public string HelmetMergeToggleText => IsHelmetToArmorMerged ? "取消将头盔合并到盔甲" : "临时将头盔合并到盔甲";
        public bool CanPlanHelmetToArmorMerge => !_disposed && CanUsePatchTools && TryGetCurrentNode(out _);
        public bool CanToggleHelmetToArmorMerge => !_disposed && CanUsePatchTools && !_helmetMergeRunning && TryGetCurrentNode(out _);
        public bool IsDecoration => Mod?.IsDecoration == true;
        public bool CanPlanDecoration => !_disposed && CanShowDecorationPlan && TryGetCurrentNode(out _);
        public bool CanEditDecoration => !_disposed && IsDecoration && TryGetCurrentNode(out _);
        public string DecorationSummary { get; private set; } = "正在读取装饰计划。";
        public ModListProjectionSource<ModCardViewModel> HostDecorations => _hostAttachments.Items;
        // Empty standard hosts still own their option list; an option becomes a
        // decoration host only when it owns patch content that can be rebuilt.
        public bool CanHostAttachments => !IsDecoration && !IsReShade && (IsStandardMod || (IsOption && HasPatchContent));
        public bool HasHostDecorations => CanHostAttachments && _hostAttachments.VisibleCount > 0;
        private bool HasPatchGroups => Mod?.FileGroups?.Count > 0;
        public BulkObservableCollection<AdvancedModAssetRowViewModel> AdvancedAssets { get; } = new();
        public string AdvancedAssetQuery { get => _advancedAssetQuery; set { if (SetField(ref _advancedAssetQuery, value)) ApplyAdvancedAssetFilter(); } }
        public bool AdvancedOnlyIssues { get => _advancedOnlyIssues; set { if (SetField(ref _advancedOnlyIssues, value)) ApplyAdvancedAssetFilter(); } }
        public string AdvancedAssetState { get; private set; } = "正在加载稳定资产事实。";
        public string PatchSummary => Mod?.FileGroups == null || Mod.FileGroups.Count == 0
            ? "没有 patch 文件组"
            : string.Join(Environment.NewLine, Mod.FileGroups.Select(g => $"{g.HexPrefix}.patch_{g.PatchN}"));

        public RelayCommand RefreshCommand { get; }
        public RelayCommand UpdateImageCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand AddToProfileCommand { get; }
        public ICommand DeleteCommand { get; }
        public RelayCommand OpenAdvancedDetailsCommand { get; }
        public RelayCommand SplitEmbeddedMaterialsCommand { get; }
        public RelayCommand ReplaceEmbeddedMaterialsCommand { get; }
        public RelayCommand EmbedExternalMaterialsCommand { get; }
        public RelayCommand PatchResourceRepackagingCommand { get; }
        public RelayCommand RebuildSameKeyCommand { get; }
        public RelayCommand PlanCrossArmorTransferCommand { get; }
        public RelayCommand PlanHelmetToArmorMergeCommand { get; }
        public RelayCommand ToggleHelmetToArmorMergeCommand { get; }
        public RelayCommand PlanDecorationCommand { get; }
        public RelayCommand PlanBatchDecorationCommand { get; }
        public RelayCommand EditDecorationCommand { get; }
        public RelayCommand RunAdvancedAnalysisCommand { get; }
		public RelayCommand RunDependencyGraphTestCommand { get; }
        public RelayCommand CompareDependencyGraphCommand { get; }

        public ModDetailsPageViewModel(ModLibraryService library, ProfileService profiles, DerivedStateCoordinator derivedState, string modId, NotificationService? notifications = null, BackgroundTaskService? backgroundTasks = null, SelectionCoordinator? selection = null)
        {
            Title = "Mod 详情";
            _library = library;
            _profiles = profiles;
            _derivedState = derivedState;
            _notifications = notifications;
			_backgroundTasks = backgroundTasks;
            _selection = selection;
            _paths = SettingsService.CreateStoragePaths();
			_materialPackaging = CoreServices.CreateMaterialPackagingApplicationService(_derivedState.InformationCenter);
            _materialDeliveryFacts = CoreServices.CreateMaterialDeliveryFactsService(_paths, _derivedState.InformationCenter);
            _equipmentUnitCatalog = CoreServices.CreateEquipmentUnitCatalogService(_paths);
			_equipmentSourceFactsReader = _library.EquipmentSourceFactsReader;
            _advancedAnalysis = CoreServices.CreateAdvancedModAnalysisService(_paths, _derivedState.InformationCenter);
            _sourceUnitEligibility = CoreServices.CreateSourceUnitEligibilityService();
            _patchGraphDiagnostics = CoreServices.CreatePatchGraphDiagnosticsService(_library.InformationReader);
            _advancedAssetQueryService = CoreServices.CreateAdvancedModAssetQueryService(_paths, _derivedState.InformationCenter);
            _hostAttachments = new ModListStateController<HostAttachmentListState, ModCardViewModel>(
                state => state.Guid,
                state => state.PresentationVersion,
                CreateHostAttachmentCard,
                (card, state) => card.ApplyAttachmentState(state.Mod, state.IsSelected, state.DecorationStatus, state.ContentVersion),
                card => card.SelectionKey);
            _hostAttachments.Committed += OnHostAttachmentProjectionCommitted;
            ModId = modId;
            RefreshCommand = new RelayCommand(Refresh);
            UpdateImageCommand = new RelayCommand(async path => await UpdateImageAsync(path), path => path is string imagePath && File.Exists(imagePath));
            OpenFolderCommand = new RelayCommand(OpenFolder);
            AddToProfileCommand = new RelayCommand(AddToProfile);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync);
            OpenAdvancedDetailsCommand = new RelayCommand(OpenAdvancedDetails);
            SplitEmbeddedMaterialsCommand = new RelayCommand(_ => OpenMaterialPackaging(splitEmbeddedMaterials: true), _ => CanSplitEmbeddedMaterials);
            ReplaceEmbeddedMaterialsCommand = new RelayCommand(_ => OpenMaterialPackaging(splitEmbeddedMaterials: false, requireAllExternalMaterials: false), _ => CanReplaceEmbeddedMaterials);
            EmbedExternalMaterialsCommand = new RelayCommand(_ => OpenMaterialPackaging(splitEmbeddedMaterials: false, requireAllExternalMaterials: true), _ => CanEmbedExternalMaterials);
            PatchResourceRepackagingCommand = new RelayCommand(_ => OpenPatchResourceRepackaging(), _ => CanPatchResourceRepackaging);
            RebuildSameKeyCommand = new RelayCommand(_ => OpenSameKeyRebuild(), _ => CanRebuildSameKey);
            PlanCrossArmorTransferCommand = new RelayCommand(async _ => await PlanCrossArmorTransferAsync(), _ => CanPlanCrossArmorTransfer);
            PlanHelmetToArmorMergeCommand = new RelayCommand(_ => OpenHelmetToArmorMergePlan(), _ => CanPlanHelmetToArmorMerge);
            ToggleHelmetToArmorMergeCommand = new RelayCommand(async _ => await ToggleHelmetToArmorMergeAsync(), _ => CanToggleHelmetToArmorMerge);
            PlanDecorationCommand = new RelayCommand(_ => OpenDecorationPlan(), _ => CanPlanDecoration);
            PlanBatchDecorationCommand = new RelayCommand(_ => OpenBatchDecorationPlan(), _ => CanPlanDecoration);
            EditDecorationCommand = new RelayCommand(_ => OpenDecorationEditor(), _ => CanEditDecoration);
            RunAdvancedAnalysisCommand = new RelayCommand(async _ => await RunAdvancedAnalysisAsync(), _ => CanRunAdvancedAnalysis);
			RunDependencyGraphTestCommand = new RelayCommand(async _ => await RunDependencyGraphTestAsync(), _ => CanRunDependencyGraphTest);
			CompareDependencyGraphCommand = new RelayCommand(async _ => await CompareDependencyGraphAsync(), _ => CanCompareDependencyGraph);
            _snapshotChangedHandler = (_, _) => RunOnUiThread(() =>
            {
                if (_disposed) return;
                Refresh();
                if (CanUsePatchTools) _ = RefreshInformationProductsAsync();
                if (_advancedDetailsLoaded) _ = RefreshAdvancedDetailsAsync();
            });
            _derivedState.SnapshotChanged += _snapshotChangedHandler;
            if (_selection is not null) _selection.SelectionChanged += OnSelectionChanged;
            Refresh();
            if (IsDecoration) RefreshDecorationSummary();
            if (CanUsePatchTools) _ = RefreshInformationProductsAsync();
        }

        private async Task RefreshInformationProductsAsync()
        {
            if (_disposed || !TryGetCurrentNode(out var node)) return;
            try
            {
                // 先请求轻量材质事实，使“拆分内嵌材质”不必等待后续 Unit 版本检查或包装器扫描。
                _ = RefreshMaterialDeliveryFactsAsync(CancellationToken.None);
                var assetInventory = await _derivedState.InformationCenter.RequestAssetInventoryAsync(
                    node,
                    _library.ModsRootDirectory,
                    new ModInformationRequest(ModInformationKind.AssetInventory, "ModDetails"));
                var unitVersion = await _derivedState.InformationCenter.RequestUnitVersionAsync(
                    node,
                    _library.ModsRootDirectory,
                    new ModInformationRequest(ModInformationKind.UnitVersion, "ModDetails"));
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    _detailContentFacts = assetInventory.Data;
                    if (unitVersion.Data is { } facts)
                    {
                        UnitCompatibilitySummary = facts.Report.Summary;
                        IsModelOutdated = facts.Report.IsOutdated;
                    }
                    else
                    {
                        UnitCompatibilitySummary = unitVersion.Status == ModInformationStatus.Unavailable
                            ? "模型版本检测不可用。"
                            : "模型版本检测失败。";
                        IsModelOutdated = false;
                    }
                    OnPropertyChanged(nameof(UnitCompatibilitySummary));
                    OnPropertyChanged(nameof(IsModelOutdated));
                    OnPropertyChanged(nameof(ShowOutdatedCompatibility));
                    RefreshAssetStatus();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    UnitCompatibilitySummary = $"模型版本检测失败：{exception.Message}";
                    IsModelOutdated = false;
                    OnPropertyChanged(nameof(UnitCompatibilitySummary));
                    OnPropertyChanged(nameof(IsModelOutdated));
                    OnPropertyChanged(nameof(ShowOutdatedCompatibility));
                });
            }
        }

        private async Task UpdateImageAsync(object? parameter)
        {
            if (parameter is not string sourceImagePath || Mod is null) return;

            var modDirectory = _library.ResolveAbsolutePath(Mod.SourcePath);
            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory)) return;

            var destination = Path.Combine(modDirectory, "icon" + Path.GetExtension(sourceImagePath).ToLowerInvariant());
            try
            {
                await Task.Run(() => File.Copy(sourceImagePath, destination, overwrite: true)).ConfigureAwait(false);
                var updatedMod = Mod;
                updatedMod.Image = destination;
                await _library.AddAsync(updatedMod).ConfigureAwait(false);
                await _library.SaveAsync().ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    if (TryGetCurrentNode(out var updatedNode))
                        _ = _derivedState.InformationCenter.InvalidateNodeAsync(updatedNode.Id);
                    Refresh();
                    _notifications?.Show($"已更新图像：{updatedMod.Name}");
                    _ = RegenerateThumbnailAsync(destination);
                });
            }
            catch (Exception exception)
            {
                RunOnUiThread(() => _notifications?.Show($"更新图像失败：{exception.Message}", NotificationLevel.Error));
            }
        }

        private async Task RegenerateThumbnailAsync(string imagePath)
        {
            try
            {
                if (TryGetCurrentNode(out var node))
                {
                    var facts = await _library.RequestThumbnailAsync(ModId, "UserRefresh", requireFresh: true).ConfigureAwait(false);
                    if (facts.Data is { } thumbnailFacts)
                        await ThumbnailService.EnsureThumbnailAsync(thumbnailFacts, 72).ConfigureAwait(false);
                }
                await _derivedState.RefreshAsync().ConfigureAwait(false);
            }
            catch
            {
                // 图像已经保存；缩略图失败时由下次库页刷新重试。
            }
        }

        public async Task RefreshAdvancedDetailsAsync()
        {
            if (_disposed) return;
            _advancedDetailsLoaded = true;
            _advancedDetailsCancellation?.Cancel();
            _advancedDetailsCancellation?.Dispose();
            _advancedDetailsCancellation = new CancellationTokenSource();
            var cancellationToken = _advancedDetailsCancellation.Token;
            try
            {
                // 高级分析和材质交付候选均不得因页面 Loaded 自动生产；高级分析只由“模型解析”按钮启动。
                await RefreshAdvancedAssetsAsync(cancellationToken).ConfigureAwait(false);

                // 材质包装检查是独立的附加信息，失败时不得阻止轻量资产表格显示。
                try
                {
                    await RefreshMaterialPackagingStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 页面明确取消刷新时保持取消语义；资产表格已经先完成加载。
                    throw;
                }
                catch (Exception exception)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        MaterialPackagingSummary = $"材质分析失败：{exception.Message}";
                        OnPropertyChanged(nameof(MaterialPackagingSummary));
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // The advanced window or its owning detail page was closed.
            }
        }

        public void CancelAdvancedDetails()
        {
            _advancedDetailsLoaded = false;
            _advancedDetailsCancellation?.Cancel();
        }

        private async Task RunAdvancedAnalysisAsync()
        {
            if (!TryGetCurrentNode(out var node)) return;
            await RunOnUiThreadAsync(() =>
            {
                _advancedAnalysisRunning = true;
                AdvancedAnalysisSummary = "正在读取 Unit 完整结构、材质和贴图引用…";
                OnPropertyChanged(nameof(AdvancedAnalysisSummary));
                OnPropertyChanged(nameof(CanRunAdvancedAnalysis));
                RunAdvancedAnalysisCommand.RaiseCanExecuteChanged();
            });
            try
            {
                var result = await Task.Run(() => _advancedAnalysis.AnalyzeAsync(node, _library.ModsRootDirectory).AsTask());
                await RunOnUiThreadAsync(() =>
                {
                    AdvancedAnalysisSummary = result.Issues.Count == 0
                        ? $"高级分析完成：{result.BuiltUtc:yyyy-MM-dd HH:mm:ss}。"
                        : $"高级分析完成，但发现 {result.Issues.Count} 项读取提醒。";
                    _advancedAnalysisReady = result.IsReady;
                });
                await RefreshAdvancedEquipmentStateAsync(node).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await RunOnUiThreadAsync(() =>
                {
                    _advancedAnalysisReady = false;
					_advancedAnalysisHasEquipment = false;
                    AdvancedAnalysisSummary = $"高级分析失败：{exception.Message}";
                    _notifications?.Show(AdvancedAnalysisSummary, NotificationLevel.Error, TimeSpan.FromSeconds(10));
                });
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    _advancedAnalysisRunning = false;
                    OnPropertyChanged(nameof(AdvancedAnalysisSummary));
                    OnPropertyChanged(nameof(CanRunAdvancedAnalysis));
                    RunAdvancedAnalysisCommand.RaiseCanExecuteChanged();
                    RaiseMaterialCommandStates();
                    RaiseSameKeyReconstructionCommandState();
                });
            }
        }

        private async Task RefreshAdvancedEquipmentStateAsync(ModNode node)
        {
            try
            {
                var analyses = await _advancedAnalysis.GetRequiredAnalysesAsync(node, _library.ModsRootDirectory).ConfigureAwait(false);
                _cachedAdvancedAnalyses = analyses;
				_cachedAdvancedAnalysesNodeId = node.Id;
                var unitKeys = analyses.SelectMany(analysis => analysis.Assets)
                    .Where(asset => asset.AssetKey.TypeId == 0xe0a48d0be9a7453f)
                    .Select(asset => new AssetKey(asset.AssetKey.TypeId, asset.AssetKey.FileId))
                    .ToHashSet();
                var entries = await _equipmentUnitCatalog.GetEntriesAsync(unitKeys).ConfigureAwait(false);
                _advancedAnalysisHasEquipment = entries.Any(entry => string.Equals(entry.Category, "Armor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Category, "Helmet", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _advancedAnalysisHasEquipment = false;
            }
        }

        private async Task RunDependencyGraphTestAsync()
        {
            if (!TryGetCurrentNode(out var node)) return;
            var requestGeneration = _detailRequestGeneration;
            _dependencyGraphTestRunning = true;
            DependencyGraphTestSummary = "正在执行轻量引用链测试…";
            OnPropertyChanged(nameof(DependencyGraphTestSummary));
            OnPropertyChanged(nameof(CanRunDependencyGraphTest));
            RunDependencyGraphTestCommand.RaiseCanExecuteChanged();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var analyses = await Task.Run(() => _patchGraphDiagnostics.AnalyzeDependencyGraphAsync(node, _library.ModsRootDirectory).AsTask());
                if (!IsCurrentDetailRequest(node, requestGeneration)) return;
                stopwatch.Stop();
                var assets = analyses.Sum(analysis => analysis.Assets.Count);
                var unitMaterials = analyses.Sum(analysis => analysis.References.Count(reference => reference.Kind == HD2ModAdaptation.Analysis.PatchReferenceKind.UnitMaterial));
                var materialTextures = analyses.Sum(analysis => analysis.References.Count(reference => reference.Kind == HD2ModAdaptation.Analysis.PatchReferenceKind.MaterialTexture));
                var issues = analyses.SelectMany(analysis => analysis.Issues).ToArray();
                var reportPath = WriteDependencyGraphTestReport(node, analyses, stopwatch.Elapsed, issues);
                var health = AssessDependencyGraph(analyses);
                DependencyGraphTestSummary = $"轻量引用链{(health.IsNormal ? "正常" : "发现异常")}：{assets} 个资源，Unit→Material {unitMaterials} 条，Material→Texture {materialTextures} 条，耗时 {stopwatch.ElapsedMilliseconds} ms。已写入 JSON 报告。";
                LogService.Info($"轻量引用链测试：Mod={node.Metadata.Name} ({node.Id.Value:N})，Patch组={analyses.Count}，资源={assets}，Unit→Material={unitMaterials}，Material→Texture={materialTextures}，问题={issues.Length}，耗时={stopwatch.ElapsedMilliseconds}ms。JSON={reportPath}。详情页未写入缓存。" + (issues.Length == 0 ? string.Empty : $" 问题：{string.Join(" | ", issues.Take(10).Select(issue => $"{issue.Code}: {issue.Message}"))}"));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                DependencyGraphTestSummary = $"轻量引用链测试失败：{exception.Message}";
                LogService.Error($"轻量引用链测试失败：Mod={node.Metadata.Name}，耗时={stopwatch.ElapsedMilliseconds}ms，错误={exception}");
            }
            finally
            {
                _dependencyGraphTestRunning = false;
                OnPropertyChanged(nameof(DependencyGraphTestSummary));
                OnPropertyChanged(nameof(CanRunDependencyGraphTest));
                RunDependencyGraphTestCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task CompareDependencyGraphAsync()
        {
            if (!TryGetCurrentNode(out var node)) return;
            var requestGeneration = _detailRequestGeneration;
            _dependencyGraphComparisonRunning = true;
            DependencyGraphComparisonSummary = "正在分别读取轻量与完整引用链并比较去重关系…";
            OnPropertyChanged(nameof(DependencyGraphComparisonSummary));
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var lightweight = await Task.Run(() => _patchGraphDiagnostics.AnalyzeDependencyGraphAsync(node, _library.ModsRootDirectory).AsTask());
                var full = await Task.Run(() => _patchGraphDiagnostics.AnalyzeFullPatchGraphAsync(node, _library.ModsRootDirectory).AsTask());
                if (!IsCurrentDetailRequest(node, requestGeneration)) return;
                stopwatch.Stop();
                var comparison = CompareReferenceSets(lightweight, full);
                var reportPath = WriteDependencyGraphComparisonReport(node, lightweight, full, comparison, stopwatch.Elapsed);
                DependencyGraphComparisonSummary = comparison.IsMatch
                    ? $"轻量链与完整分析的去重引用集合一致，耗时 {stopwatch.ElapsedMilliseconds} ms。"
                    : $"发现差异：仅轻量 {comparison.OnlyLightweight.Length} 条、仅完整 {comparison.OnlyFull.Length} 条；请查看 JSON。";
                LogService.Info($"轻量/完整引用链对比：Mod={node.Metadata.Name}，一致={comparison.IsMatch}，仅轻量={comparison.OnlyLightweight.Length}，仅完整={comparison.OnlyFull.Length}，耗时={stopwatch.ElapsedMilliseconds}ms，JSON={reportPath}。");
            }
            catch (Exception exception)
            {
                DependencyGraphComparisonSummary = $"引用链对比失败：{exception.Message}";
                LogService.Error($"轻量/完整引用链对比失败：Mod={node.Metadata.Name}，错误={exception}");
            }
            finally
            {
                _dependencyGraphComparisonRunning = false;
                OnPropertyChanged(nameof(DependencyGraphComparisonSummary));
                OnPropertyChanged(nameof(CanCompareDependencyGraph));
                CompareDependencyGraphCommand.RaiseCanExecuteChanged();
            }
        }

        private string WriteDependencyGraphTestReport<TAnalysis, TIssue>(ModNode node, IEnumerable<TAnalysis> analyses, TimeSpan elapsed, IReadOnlyList<TIssue> issues)
        {
            var serializedGroups = analyses
                .Select((analysis, index) => new
                {
                    Index = index,
                    Analysis = JsonSerializer.SerializeToElement(analysis)
                })
                .ToArray();

            var resources = serializedGroups
                .SelectMany(group => ReadJsonArrayProperty(group.Analysis, "Assets").Select(asset => new
                {
                    OwnerPatchGroup = ReadJsonStringProperty(group.Analysis, "Input", "PatchTocFilePath") ?? $"PatchGroup#{group.Index + 1}",
                    Resource = asset
                }))
                .ToArray();

            var referenceChains = serializedGroups
                .SelectMany(group => ReadJsonArrayProperty(group.Analysis, "References").Select(reference => new
                {
                    OwnerPatchGroup = ReadJsonStringProperty(group.Analysis, "Input", "PatchTocFilePath") ?? $"PatchGroup#{group.Index + 1}",
                    Reference = reference
                }))
                .GroupBy(item => item.Reference.GetRawText(), StringComparer.Ordinal)
                .Select(group => new
                {
                    Reference = group.First().Reference,
                    Owners = group.Select(item => item.OwnerPatchGroup).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    DuplicateCount = group.Count()
                })
                .ToArray();

            var issueDetails = issues
                .Select(issue => JsonSerializer.SerializeToElement(issue))
                .ToArray();
			var health = AssessDependencyGraph(serializedGroups.Select(group => group.Analysis));
            var isNormal = issueDetails.Length == 0 && health.IsNormal;
            var report = new
            {
                GeneratedUtc = DateTime.UtcNow,
                Mod = new
                {
                    Id = node.Id.Value,
                    Name = node.Metadata.Name
                },
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                Normality = new
                {
                    IsNormal = isNormal,
                    Judgment = isNormal ? "正常" : "存在需要检查的引用链读取或语义问题",
                    Explanation = isNormal
                        ? "所有 Patch 组均已完成轻量引用链读取，引用类型与目标资源类型一致，且未返回诊断问题。"
						: $"读取或语义校验发现问题：读取诊断 {issueDetails.Length} 项，引用语义问题 {health.Problems.Length} 项。"
                },
                Summary = new
                {
                    PatchGroupCount = serializedGroups.Length,
                    ResourceCount = resources.Length,
                    UniqueReferenceChainCount = referenceChains.Length,
                    DuplicateReferenceCount = referenceChains.Sum(chain => chain.DuplicateCount - 1),
                    IssueCount = issueDetails.Length
                },
                ResourceOwnership = resources,
                DeduplicatedReferenceChains = referenceChains,
				SemanticValidation = health,
                Issues = issueDetails,
                PatchGroups = serializedGroups.Select(group => group.Analysis).ToArray()
            };

            var reportDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(
                reportDirectory,
                $"DependencyGraph-{SanitizeFileName(node.Metadata.Name)}-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }), Encoding.UTF8);
            return reportPath;
        }

        private static DependencyGraphHealth AssessDependencyGraph(IEnumerable<JsonElement> analyses)
        {
            var assets = analyses.SelectMany(analysis => ReadJsonArrayProperty(analysis, "Assets"))
                .Select(asset => asset.GetProperty("AssetKey").GetRawText())
                .ToHashSet(StringComparer.Ordinal);
            var assetTypes = analyses.SelectMany(analysis => ReadJsonArrayProperty(analysis, "Assets"))
                .ToDictionary(asset => asset.GetProperty("AssetKey").GetRawText(), asset => new
                {
                    IsUnit = asset.GetProperty("IsUnit").GetBoolean(),
                    IsMaterial = asset.GetProperty("IsMaterial").GetBoolean(),
                    IsTexture = asset.GetProperty("IsTexture").GetBoolean()
                }, StringComparer.Ordinal);
            var problems = new List<object>();
            foreach (var reference in analyses.SelectMany(analysis => ReadJsonArrayProperty(analysis, "References")))
            {
                var kind = reference.GetProperty("Kind").GetInt32();
                var source = reference.GetProperty("SourceAssetKey").GetRawText();
                var target = reference.GetProperty("TargetAssetKey").GetRawText();
                var sourceIsExpected = assetTypes.TryGetValue(source, out var sourceType) && (kind == 0 ? sourceType.IsUnit : sourceType.IsMaterial);
                var targetIsEmbedded = assets.Contains(target);
                var targetIsExpected = !targetIsEmbedded || (assetTypes.TryGetValue(target, out var targetType) && (kind == 0 ? targetType.IsMaterial : targetType.IsTexture));
                if (!sourceIsExpected || !targetIsExpected)
                {
                    problems.Add(new { Kind = kind == 0 ? "UnitMaterial" : "MaterialTexture", Source = source, Target = target, SourceIsExpected = sourceIsExpected, TargetIsExpected = targetIsExpected, TargetIsEmbedded = targetIsEmbedded });
                }
            }
            return new DependencyGraphHealth(problems.Count == 0, problems.ToArray());
        }

        private static DependencyGraphHealth AssessDependencyGraph<TAnalysis>(IEnumerable<TAnalysis> analyses)
            => AssessDependencyGraph(analyses.Select(analysis => JsonSerializer.SerializeToElement(analysis)));

        private static ReferenceSetComparison CompareReferenceSets<TAnalysis>(IEnumerable<TAnalysis> lightweight, IEnumerable<TAnalysis> full)
        {
            var light = BuildReferenceSet(lightweight);
            var complete = BuildReferenceSet(full);
            return new ReferenceSetComparison(
                light.Except(complete, StringComparer.Ordinal).ToArray(),
                complete.Except(light, StringComparer.Ordinal).ToArray());
        }

        private static HashSet<string> BuildReferenceSet<TAnalysis>(IEnumerable<TAnalysis> analyses)
            => analyses.SelectMany(analysis => ReadJsonArrayProperty(JsonSerializer.SerializeToElement(analysis), "References"))
                .Select(reference => $"{reference.GetProperty("Kind").GetInt32()}|{reference.GetProperty("SourceAssetKey").GetRawText()}|{reference.GetProperty("TargetAssetKey").GetRawText()}")
                .ToHashSet(StringComparer.Ordinal);

        private static string WriteDependencyGraphComparisonReport<TAnalysis>(ModNode node, IEnumerable<TAnalysis> lightweight, IEnumerable<TAnalysis> full, ReferenceSetComparison comparison, TimeSpan elapsed)
        {
            var reportDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, $"DependencyGraphComparison-{SanitizeFileName(node.Metadata.Name)}-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                GeneratedUtc = DateTime.UtcNow,
                Mod = new { node.Id.Value, node.Metadata.Name },
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                IsMatch = comparison.IsMatch,
                OnlyLightweight = comparison.OnlyLightweight,
                OnlyFull = comparison.OnlyFull,
                LightweightHealth = AssessDependencyGraph(lightweight),
                FullHealth = AssessDependencyGraph(full)
            }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            return reportPath;
        }

        private sealed record DependencyGraphHealth(bool IsNormal, object[] Problems);
        private sealed record ReferenceSetComparison(string[] OnlyLightweight, string[] OnlyFull)
        {
            public bool IsMatch => OnlyLightweight.Length == 0 && OnlyFull.Length == 0;
        }

        private static IEnumerable<JsonElement> ReadJsonArrayProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
                ? property.EnumerateArray().Select(item => item.Clone()).ToArray()
                : Array.Empty<JsonElement>();
        }

        private static string? ReadJsonStringProperty(JsonElement element, string parentPropertyName, string propertyName)
        {
            return element.TryGetProperty(parentPropertyName, out var parent)
                && parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private async Task RefreshAdvancedAssetsAsync(CancellationToken cancellationToken)
        {
            if (Mod is null || !TryParseNodeId(Mod.Guid, out var nodeId))
            {
                await RunOnUiThreadAsync(() =>
                {
                    AdvancedAssetState = "稳定资产事实不可用：当前 Mod 标识无效。";
                    AdvancedAssets.ReplaceWith(Array.Empty<AdvancedModAssetRowViewModel>());
                    OnPropertyChanged(nameof(AdvancedAssetState));
                });
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                AdvancedAssetState = "正在读取稳定资产事实…";
                OnPropertyChanged(nameof(AdvancedAssetState));
            });
            try
            {
                var active = _profiles.ActiveProfile;
                var graph = active is null ? null : _derivedState.Snapshot.ExpectedGraph;
                var diagnostics = active is null ? null : _derivedState.Snapshot.MaterialDiagnostics;
                _allAdvancedAssets = await _advancedAssetQueryService.QueryAsync(nodeId, _library.Snapshot, graph, diagnostics, cancellationToken);
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                await RunOnUiThreadAsync(() =>
                {
                    AdvancedAssetState = _allAdvancedAssets.Count == 0
                        ? "稳定资产事实读取完成，但当前没有 AssetKey（轻量引用图可能尚未完成）。"
                        : $"稳定资产事实读取完成：共 {_allAdvancedAssets.Count} 个 AssetKey（轻量引用图）";
                    ApplyAdvancedAssetFilter();
                });
            }
            catch (OperationCanceledException)
            {
                await RunOnUiThreadAsync(() =>
                {
                    AdvancedAssetState = "稳定资产事实读取已取消。";
                    OnPropertyChanged(nameof(AdvancedAssetState));
                });
                throw;
            }
            catch (Exception exception)
            {
                await RunOnUiThreadAsync(() =>
                {
                    AdvancedAssetState = $"稳定资产事实读取失败：{exception.Message}";
                    AdvancedAssets.ReplaceWith(Array.Empty<AdvancedModAssetRowViewModel>());
                    OnPropertyChanged(nameof(AdvancedAssetState));
                });
            }
        }

        private void ApplyAdvancedAssetFilter()
        {
            var query = AdvancedAssetQuery.Trim();
            var rows = _allAdvancedAssets
                .Where(row => !AdvancedOnlyIssues || !string.IsNullOrWhiteSpace(row.DiagnosticSummary))
                .Where(row => string.IsNullOrWhiteSpace(query)
                    || row.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.ResourceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.TargetSummary.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.AssetKey.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(row => new AdvancedModAssetRowViewModel(row))
                .ToList();
            AdvancedAssets.ReplaceWith(rows);
            OnPropertyChanged(nameof(AdvancedAssetState));
        }

        public void Refresh()
        {
            _detailRequestGeneration++;
            Mod = _library.Get(ModId);
            OnPropertyChanged(nameof(Mod));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(Image));
            OnPropertyChanged(nameof(FileGroupCount));
            OnPropertyChanged(nameof(PatchSummary));
            RefreshDerivedStatus();
            RefreshHostDecorations();
            OnPropertyChanged(nameof(ProfileStatus));
            OnPropertyChanged(nameof(FileIntegritySummary));
            OnPropertyChanged(nameof(ConflictSummary));
            OnPropertyChanged(nameof(AssetTagsString));
            OnPropertyChanged(nameof(AssetListSummary));
            OnPropertyChanged(nameof(AssetOverrideSummary));
            OnPropertyChanged(nameof(UnitCompatibilitySummary));
            OnPropertyChanged(nameof(IsModelOutdated));
            OnPropertyChanged(nameof(ShowOutdatedCompatibility));
            OnPropertyChanged(nameof(DataIndexSummary));
			OnPropertyChanged(nameof(MaterialDiagnosticSummary));
            OnPropertyChanged(nameof(UserStatusTitle));
            OnPropertyChanged(nameof(UserStatusSummary));
            OnPropertyChanged(nameof(ShowUserStatusCard));
            OnPropertyChanged(nameof(MaterialPackagingSummary));
			OnPropertyChanged(nameof(MaterialDeliverySummary));
            OnPropertyChanged(nameof(SameKeyReconstructionSummary));
			OnPropertyChanged(nameof(AdvancedAnalysisSummary));
            OnPropertyChanged(nameof(CanRunAdvancedAnalysis));
			OnPropertyChanged(nameof(DependencyGraphTestSummary));
			OnPropertyChanged(nameof(CanPlanCrossArmorTransfer));
			OnPropertyChanged(nameof(IsHelmetToArmorMerged));
			OnPropertyChanged(nameof(HelmetMergeToggleText));
			OnPropertyChanged(nameof(CanPlanHelmetToArmorMerge));
			OnPropertyChanged(nameof(CanToggleHelmetToArmorMerge));
			OnPropertyChanged(nameof(CanPlanDecoration));
			OnPropertyChanged(nameof(IsDecoration));
            OnPropertyChanged(nameof(IsOption));
            OnPropertyChanged(nameof(IsReShade));
            OnPropertyChanged(nameof(IsStandardMod));
            OnPropertyChanged(nameof(HasPatchContent));
            OnPropertyChanged(nameof(CanHostAttachments));
            OnPropertyChanged(nameof(CanUsePatchTools));
            OnPropertyChanged(nameof(CanPatchResourceRepackaging));
            OnPropertyChanged(nameof(CanShowDecorationPlan));
            OnPropertyChanged(nameof(HasHostDecorations));
            RaiseMaterialCommandStates();
            RaiseSameKeyReconstructionCommandState();
        }

        private void RefreshDerivedStatus()
        {
            ProfileStatus = BuildProfileStatus();
            FileIntegritySummary = BuildFileIntegritySummary();
            ConflictSummary = BuildConflictSummary();
            RefreshAssetStatus();
        }

        private void RefreshAssetStatus()
        {
            AssetTagsString = "未解析";
            AssetListSummary = "未解析";
            AssetOverrideSummary = "未检查";
            DataIndexSummary = "跨 Mod 资产索引尚未读取。";

            if (Mod == null)
            {
                AssetTagsString = "未找到 Mod";
                AssetListSummary = "未找到 Mod";
                AssetOverrideSummary = "未找到 Mod";
                return;
            }
            if (!TryParseNodeId(Mod.Guid, out var nodeId) || !_library.Snapshot.Nodes.TryGetValue(nodeId, out var node))
            {
                AssetTagsString = "无法识别 Mod ID";
                AssetListSummary = "无法识别 Mod ID";
                AssetOverrideSummary = "无法识别 Mod ID";
                return;
            }

            var statuses = _derivedState.ProjectStatuses(_profiles.SelectedProfileId);
            if (statuses.TryGetValue(nodeId, out var userStatus))
            {
                _userStatus = userStatus;
                UserStatusTitle = userStatus.Title;
                UserStatusSummary = userStatus.Summary;
            }
            else
            {
                _userStatus = null;
                UserStatusTitle = "状态未知";
                UserStatusSummary = "正在读取状态。";
            }
            OnPropertyChanged(nameof(ShowUserStatusCard));
            var derived = _library.GetDerivedData(Mod.Guid);
            var summary = derived?.AssetSummary;
            if (summary == null)
            {
				var facts = _detailContentFacts ?? derived?.ContentFacts ?? _derivedState.Snapshot.ContentFacts.GetValueOrDefault(nodeId);
				if (facts is not null)
				{
					var assets = facts.Structure.Assets.Select(asset => asset.AssetKey).ToArray();
					AssetTagsString = "稳定 Patch 事实";
                    AssetListSummary = assets.Length == 0
                        ? "未发现可解析资产"
                        : $"共 {assets.Length} 个 AssetKey：Unit {assets.Count(asset => asset.TypeId == 0xe0a48d0be9a7453f)}，Material {assets.Count(asset => asset.TypeId == 0xeac0b497876adedf)}，Texture {assets.Count(asset => asset.TypeId == 0xcd4238c6a0c69e32)}。高级详情可查看完整稳定资产表。";
                }
                else
                {
                    AssetTagsString = "资产未解析";
                    AssetListSummary = "稳定资产事实正在导入分析。";
                }
                AssetOverrideSummary = BuildCachedAssetOverrideSummary(nodeId);
                _ = RefreshDataIndexSummaryAsync(nodeId, facts);
                return;
            }
            AssetTagsString = BuildAssetTagTreeText(summary);
            AssetListSummary = summary.Assets.Count == 0
                ? "未发现可解析资产"
                : string.Join(Environment.NewLine, summary.Assets.Take(80).Select(a => a.DisplayName));
            if (summary.Assets.Count > 80) AssetListSummary += Environment.NewLine + $"... 另有 {summary.Assets.Count - 80} 个资产";
            AssetOverrideSummary = BuildCachedAssetOverrideSummary(nodeId);
            MaterialDiagnosticSummary = BuildMaterialDiagnosticSummary(nodeId);
            _ = RefreshDataIndexSummaryAsync(nodeId, _detailContentFacts ?? derived?.ContentFacts ?? _derivedState.Snapshot.ContentFacts.GetValueOrDefault(nodeId));
        }

		private async Task RefreshDataIndexSummaryAsync(ModNodeId nodeId, ModContentFacts? facts)
		{
			if (facts is null) return;
			try
			{
				var assets = facts.Structure.Assets.Select(asset => asset.AssetKey).Distinct().ToArray();
                var summary = await _derivedState.InformationCenter.GetAssetRelationSummaryAsync(assets, nodeId);
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    DataIndexSummary = summary.Status switch
                    {
                        ModDataIndexStatus.Unavailable => "跨 Mod 资产索引尚未就绪/不可用。",
                        ModDataIndexStatus.Partial => "跨 Mod 资产索引正在补齐。",
                        _ => $"跨 Mod 索引：关联提供者 {summary.ProviderCount} 个，引用消费者 {summary.ConsumerCount} 个。"
                    };
                    OnPropertyChanged(nameof(DataIndexSummary));
                });
            }
            catch (Exception exception)
            {
                RunOnUiThread(() =>
                {
                    if (_disposed) return;
                    DataIndexSummary = $"跨 Mod 资产索引不可用：{exception.Message}";
                    OnPropertyChanged(nameof(DataIndexSummary));
                });
            }
        }

        private string BuildMaterialDiagnosticSummary(ModNodeId nodeId)
        {
            var active = _profiles.ActiveProfile;
            if (active is null) return "当前没有活动配置。";
            var diagnostics = _derivedState.Snapshot.MaterialDiagnostics;
            if (diagnostics is null || diagnostics.ProfileId != active.Id || diagnostics.ProfileRevision != active.Revision) return "材质诊断正在后台更新。";
            var items = diagnostics.Items.Where(item => item.NodeId == nodeId).ToArray();
            return items.Length == 0
                ? "未发现当前有效资源图中的材质依赖异常。"
                : string.Join(Environment.NewLine, items.Take(20).Select(item => $"{MaterialDiagnosticPrefix(item.Kind)}{item.Summary}：{item.Detail}")) + (items.Length > 20 ? $"{Environment.NewLine}... 另有 {items.Length - 20} 项" : string.Empty);
        }

        private static string MaterialDiagnosticPrefix(ProfileMaterialDiagnosticKind kind) => kind switch
        {
            ProfileMaterialDiagnosticKind.CurrentGameMaterialFallback => "✓ ",
            ProfileMaterialDiagnosticKind.CurrentGameMaterialCandidate => "⚠ ",
            _ => string.Empty
        };

        private static string BuildAssetTagTreeText(ModAssetSummary summary)
        {
            if (summary.TargetGroups.Count == 0)
            {
                return summary.DerivedTags.Count == 0 ? "无资产标签" : string.Join("，", summary.DerivedTags);
            }

            var builder = new StringBuilder();
            foreach (var group in summary.TargetGroups)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine($"{group.Category}（{group.AssetCount}）");
                for (var i = 0; i < group.Items.Count; i++)
                {
                    var item = group.Items[i];
                    var marker = i == group.Items.Count - 1 ? "└─" : "├─";
                    var typeSummary = item.TypeNames.Count == 0 ? "类型未知" : string.Join("，", item.TypeNames);
                    builder.AppendLine($"{marker} {item.DisplayName}（{item.AssetCount}，{typeSummary}）");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private string BuildCachedAssetOverrideSummary(ModNodeId nodeId)
        {
            var active = _profiles.ActiveProfile;
            if (active == null) return "当前没有活动配置";
            if (!active.Entries.Any(e => e.NodeId == nodeId)) return "当前 Mod 未加入活动配置";
            var graph = _derivedState.Snapshot.ExpectedGraph;
            if (graph is null || graph.ProfileId != active.Id || graph.ProfileRevision != active.Revision) return "预计覆盖数据正在后台更新。";
            var coverage = graph.Coverages.FirstOrDefault(c => c.NodeId == nodeId);
            var relatedChains = graph.AssetChains.Where(c => c.Entries.Any(e => e.NodeId == nodeId)).ToList();
            if (relatedChains.Count == 0)
            {
                return coverage == null ? "未发现资产级覆盖" : $"未发现资产级覆盖；共 {coverage.TotalAssetKeys} 个 AssetKey";
            }

            var lost = relatedChains.Count(c => c.Winner.NodeId != nodeId);
            var won = relatedChains.Count(c => c.Winner.NodeId == nodeId);
            var status = coverage is { FullyOverridden: true }
                ? "当前 Mod 的 AssetKey 已全部被后加载 Mod 覆盖"
                : coverage is { PartiallyOverridden: true }
                    ? $"当前 Mod 有 {coverage.OverriddenAssetKeys}/{coverage.TotalAssetKeys} 个 AssetKey 被覆盖"
                    : "当前 Mod 覆盖了其他 Mod 的资产";

            var lines = new List<string> { $"{status}；胜出 {won}，失效 {lost}" };
            lines.AddRange(relatedChains.Take(20).Select(chain =>
            {
                var current = chain.Entries.First(e => e.NodeId == nodeId);
                var displayName = $"{current.Mapping.FileDisplayName}（{current.Mapping.TypeDisplayName}）";
                return current.IsWinner
                    ? $"覆盖 {string.Join(" -> ", chain.Entries.Where(e => e.NodeId != nodeId).Select(e => e.ModName))}：{displayName}"
                    : $"被 {chain.Winner.ModName} 覆盖：{displayName}";
            }));
            if (relatedChains.Count > 20)
            {
                lines.Add($"... 另有 {relatedChains.Count - 20} 条覆盖关系");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private void OpenFolder()
        {
            if (Mod == null) return;
            var abs = _library.GetDerivedData(Mod.Guid)?.AbsoluteDirectory ?? _library.ResolveAbsolutePath(Mod.SourcePath);
            if (string.IsNullOrWhiteSpace(abs)) return;
            if (!System.IO.Directory.Exists(abs)) System.IO.Directory.CreateDirectory(abs);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = abs, UseShellExecute = true });
        }

        private void OpenAdvancedDetails()
        {
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            {
                shell.OpenAdvancedModDetails(ModId);
            }
        }

        private async Task RefreshMaterialPackagingStateAsync(CancellationToken cancellationToken)
        {
            if (Mod == null || !TryParseNodeId(Mod.Guid, out var nodeId) || !_library.Snapshot.Nodes.TryGetValue(nodeId, out var node)) return;
            try
            {
                _materialState = await _materialPackaging.InspectAsync(node, _library.ModsRootDirectory, cancellationToken);
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                MaterialPackagingSummary = $"需要材质 {_materialState.RequiredMaterialCount}；内嵌 {_materialState.EmbeddedMaterialCount}；外部 {_materialState.ExternalMaterialCount}；内嵌贴图 {_materialState.EmbeddedTextureCount}";
                if (_materialState.Blockers.Count != 0) MaterialPackagingSummary += Environment.NewLine + string.Join(Environment.NewLine, _materialState.Blockers);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                _materialState = null;
                MaterialPackagingSummary = $"材质分析失败：{exception.Message}";
            }
            RunOnUiThread(() => { OnPropertyChanged(nameof(MaterialPackagingSummary)); RaiseMaterialCommandStates(); });
        }

        private async Task RefreshMaterialDeliveryFactsAsync(CancellationToken cancellationToken)
        {
            if (Mod is null || !TryParseNodeId(Mod.Guid, out var nodeId)) return;
            try
            {
				var facts = await _materialDeliveryFacts.GetAsync(nodeId, _library.Snapshot, cancellationToken, includeCandidates: true, includeGameDataMapping: false);
                _cachedMaterialDeliveryFacts = facts;
				_cachedMaterialDeliveryFactsNodeId = nodeId;
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                var lines = new List<string>
                {
                    $"交付模式：{MaterialDeliveryModeName(facts.Mode)}",
                    $"Unit {facts.UnitCount}；需要材质 {facts.RequiredMaterialCount}；内嵌 {facts.EmbeddedMaterialCount}；外部 {facts.ExternalMaterialCount}；缺失内嵌贴图 {facts.MissingEmbeddedTextureCount}"
                };
                if (facts.Candidates.Count != 0)
                {
                    lines.Add("库内材质候选：" + string.Join("；", facts.Candidates.Take(3).Select(candidate => $"{candidate.Name}（覆盖 {candidate.CoveredMaterialCount}，缺贴图 {candidate.MissingTextureCount}{(candidate.IsComplete ? "，完整" : string.Empty)}）")) + (facts.Candidates.Count > 3 ? $"；另有 {facts.Candidates.Count - 3} 个" : string.Empty));
                }
                lines.Add($"纯材质 Mod：{(facts.IsMaterialOnly ? "是" : "否")}；可映射原版材质 {facts.GameDataMappedMaterialKeys?.Count ?? 0} 个；自身材质引用 {facts.SelfMaterialReferences?.Count ?? 0} 条");
                lines.AddRange(facts.Notices);
                MaterialDeliverySummary = string.Join(Environment.NewLine, lines);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                MaterialDeliverySummary = $"稳定材质交付事实读取失败：{exception.Message}";
            }
            RunOnUiThread(() =>
            {
                OnPropertyChanged(nameof(MaterialDeliverySummary));
                RaiseMaterialCommandStates();
            });
        }

        private static string MaterialDeliveryModeName(MaterialDeliveryMode mode) => mode switch
        {
            MaterialDeliveryMode.NoMaterialDependencies => "无材质依赖",
            MaterialDeliveryMode.MaterialOnly => "纯材质 Mod",
            MaterialDeliveryMode.EmbeddedComplete => "内嵌闭包完整（整体重建）",
            MaterialDeliveryMode.EmbeddedIncomplete => "内嵌闭包不完整",
            MaterialDeliveryMode.ExternalResolved => "外部材质已解析（仅重建模型）",
            MaterialDeliveryMode.ExternalUnresolved => "外部材质未解析",
            MaterialDeliveryMode.Mixed => "内嵌与外部混用",
            _ => "未知"
        };

        private void OpenSameKeyRebuild()
        {
            if (!TryGetCurrentNode(out _)) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
            shell.OpenSameKeyRebuild(new SameKeyRebuildBottomBarViewModel(RebuildSameKeyAsync));
        }

        public async Task ToggleDecorationForCurrentHostAsync(ModCardViewModel? card)
        {
            if (card is null || Mod is null || !CanHostAttachments || (!card.Mod.IsDecoration && !card.Mod.IsOption)) return;
            if (card.Mod.IsOption)
            {
                var optionMessage = await _library.ToggleOptionForHostAsync(card.Mod.Guid, Mod.Guid).ConfigureAwait(true);
                _notifications?.Show(optionMessage);
                RefreshHostDecorations();
                return;
            }
            try
            {
                var enabled = _library.IsDecorationEnabledForHost(card.Mod.Guid, Mod.Guid);
                var result = await _library.SetDecorationEnabledForHostAsync(card.Mod.Guid, Mod.Guid, !enabled).ConfigureAwait(true);
                _notifications?.Show(result.StatusText);
                RefreshHostDecorations();
            }
            catch (Exception exception)
            {
                LogService.Error($"主体详情启用装饰失败：主体={Mod.Guid}，装饰={card.Mod.Guid}，异常={exception}");
                _notifications?.Show($"启用装饰失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(8));
            }
        }

        public void OpenModFolder(string modId)
        {
            var mod = _library.Get(modId);
            if (mod is null) return;
            var path = _library.GetDerivedData(mod.Guid)?.AbsoluteDirectory ?? _library.ResolveAbsolutePath(mod.SourcePath);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        public async Task DeleteDecorationAsync(string decorationId)
        {
            var decoration = _library.Get(decorationId);
            if (decoration?.IsDecoration != true) return;
            var deletion = await _library.RemoveAsync(decorationId).ConfigureAwait(true);
            _notifications?.Show(
                ModLibraryService.DescribeDeletionResult(deletion),
                deletion.HasFailures ? NotificationLevel.Warning : NotificationLevel.Info);
            if (!deletion.RemovedNodeIds.Any(id => string.Equals(id.Value.ToString("N"), decorationId, StringComparison.OrdinalIgnoreCase))) return;
            Refresh();
        }

        private void RefreshHostDecorations()
        {
            if (Mod is null || !CanHostAttachments)
            {
                _hostAttachmentStates = Array.Empty<HostAttachmentListState>();
                _hostAttachments.Submit(_hostAttachmentStates, _hostAttachmentStates.Select(state => state.Guid));
                OnPropertyChanged(nameof(HasHostDecorations));
                return;
            }

            _hostAttachmentStates = _library.GetAttachmentsForHost(Mod.Guid)
                .Select(attachment => HostAttachmentListState.Create(
                    attachment,
                    IsHostDecorationSelected(attachment.Guid),
                    attachment.IsDecoration
                        ? (_library.IsDecorationEnabledForHost(attachment.Guid, Mod.Guid) ? "已为当前 Mod 启用。" : "未为当前 Mod 启用。")
                        : (_library.IsOptionEnabledForHost(attachment.Guid, Mod.Guid) ? "已为当前 Mod 启用。" : "未为当前 Mod 启用。")))
                .ToArray();
            _hostAttachments.Submit(_hostAttachmentStates, _hostAttachmentStates.Select(state => state.Guid));
            OnPropertyChanged(nameof(HasHostDecorations));
        }

        private static ModCardViewModel CreateHostAttachmentCard(HostAttachmentListState state)
        {
            var card = new ModCardViewModel(
                state.Mod,
                state.IsSelected,
                decorationStatus: state.DecorationStatus);
            card.ApplyAttachmentState(state.Mod, state.IsSelected, state.DecorationStatus, state.ContentVersion);
            return card;
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
            => RunOnUiThread(RefreshHostDecorationSelection);

        private bool IsHostDecorationSelected(string decorationId)
        {
            var selection = _selection;
            return selection is not null
                && string.Equals(selection.Scope, $"DecorationHost:{ModId}", StringComparison.OrdinalIgnoreCase)
                && selection.SelectedIds.Contains(decorationId, StringComparer.OrdinalIgnoreCase);
        }

        private void RefreshHostDecorationSelection()
        {
            if (_hostAttachmentStates.Count == 0) return;
            _hostAttachmentStates = _hostAttachmentStates
                .Select(state => state.WithSelection(IsHostDecorationSelected(state.Guid)))
                .ToArray();
            _hostAttachments.SubmitContent(_hostAttachmentStates, commitImmediately: true);
        }

        private void OnHostAttachmentProjectionCommitted(object? sender, ModListStateCommittedEventArgs e)
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HasHostDecorations));
        }

        private async Task<bool> HasCurrentGameDataIndexAsync()
        {
            try
            {
                var gameDataDirectory = SettingsService.GetGameDataFolder();
                if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory) || !File.Exists(_paths.ArchiveHashesPath))
                    return false;

                var archiveHashes = await File.ReadAllTextAsync(_paths.ArchiveHashesPath).ConfigureAwait(false);
                var index = CoreServices.CreateAssetArchiveIndexService(_paths);
                return (await index.GetIndexStatusAsync(gameDataDirectory, archiveHashes).ConfigureAwait(false)).IsCurrent;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
            {
                return false;
            }
        }

        private async Task RebuildSameKeyAsync(string outputRootDirectory, bool importToLibrary)
        {
            if (!TryGetCurrentNode(out var source)) return;
            var sourcePatchPaths = FindBasePatchPaths(source);
            if (sourcePatchPaths.Length == 0)
            {
                var message = "尚未进行当前 Mod 的高级分析，请先点击“模型解析”建立高级 Unit 分析缓存。";
                message = "Current Mod has no usable base Patch file.";
                _notifications?.Show(message, NotificationLevel.Info, TimeSpan.FromSeconds(10));
                SameKeyReconstructionSummary = message;
                OnPropertyChanged(nameof(SameKeyReconstructionSummary));
                return;
            }
            if (!await HasCurrentGameDataIndexAsync().ConfigureAwait(false))
            {
                const string message = "请在设置页重建资产索引";
                SameKeyReconstructionSummary = message;
                OnPropertyChanged(nameof(SameKeyReconstructionSummary));
                _notifications?.Show(message, NotificationLevel.Error, TimeSpan.FromSeconds(10));
                return;
            }
            var outputRoot = string.IsNullOrWhiteSpace(outputRootDirectory)
                ? SettingsService.GetResolvedDefaultExportPath()
                : outputRootDirectory;
            var destination = Path.Combine(outputRoot, $"{SanitizeFileName(source.Metadata.Name)}+{DateTime.Now:yyyyMMdd-HHmmssfff}+SameKey重建");
            _sameKeyReconstructionRunning = true;
            SameKeyReconstructionSummary = "正在读取 Payload 并生成 Same-key 重建结果…";
            LogService.Info($"修复 patch 开始：Mod={source.Metadata.Name}，节点={source.Id.Value:N}，输出={destination}。");
            OnPropertyChanged(nameof(SameKeyReconstructionSummary));
            RaiseSameKeyReconstructionCommandState();
            BackgroundTaskItem? task = null;
            var operationId = Guid.NewGuid();
            try
            {
                task = _backgroundTasks?.Enqueue(
                    BackgroundTaskKind.RepairMods,
                    "重建 Same-key Mod",
                    "单项修复",
                    "Mod 详情",
                    "重建并验证当前版本 Unit。",
                    canCancel: false,
                    presentation: BackgroundTaskPresentation.Foreground);
                task?.MarkRunning("正在生成同 ID Canonical 重建计划");
                await using var resourceMonitor = ResourceUsageMonitor.Start(operationId, "Same-key重建");
                var uiContext = SynchronizationContext.Current
                    ?? (System.Windows.Application.Current?.Dispatcher is { } dispatcher
                        ? new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher)
                        : new SynchronizationContext());
                var bridge = task is null ? null : new OperationProgressBridge(new BackgroundTaskOperationTarget(task), operationId, uiContext);
				var sameKeyReconstruction = CoreServices.CreateModSameKeyReconstructionService(_paths, _derivedState.InformationCenter, _library.InformationReader);
                var result = await Task.Run(() => sameKeyReconstruction.GenerateCandidateAsync(
                    source,
                    _library.ModsRootDirectory,
                    SettingsService.GetGameDataFolder(),
                    destination, task?.CancellationToken ?? CancellationToken.None, bridge is null ? null : new InlineProgress<OperationProgressEvent>(bridge.Apply), operationId).AsTask());
                if (!result.IsSuccessful)
                {
                    task?.SetOutputArtifacts(result.OutputDirectory, result.ReportMarkdownPath);
                    task?.MarkFailed(string.Join("；", result.Issues.Select(issue => issue.Message).Take(3)));
                    SameKeyReconstructionSummary = $"重建失败：{string.Join("；", result.Issues.Select(issue => issue.Message).Take(3))}";
                    LogService.Error($"修复 patch 失败：Mod={source.Metadata.Name}，输出={destination}，问题={string.Join(" | ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");
                    if (bridge is null) _notifications?.Show(SameKeyReconstructionSummary, NotificationLevel.Error, TimeSpan.FromSeconds(12));
                    return;
                }

                var warningCount = result.Issues.Count(issue => issue.Severity == CoreIssueSeverity.Warning);
                SameKeyReconstructionSummary = warningCount == 0
                    ? $"重建完成：Unit {result.OutputUnitCount}；替换 mesh {result.ReplacementMeshCount}；极小化 mesh {result.MinifiedMeshCount}。输出：{result.OutputDirectory}"
                    : $"重建完成，但有 {warningCount} 项 Unit 保留/兼容提醒：Unit {result.OutputUnitCount}；替换 mesh {result.ReplacementMeshCount}；极小化 mesh {result.MinifiedMeshCount}。请查看报告：{result.ReportMarkdownPath ?? result.OutputDirectory}";
                if (importToLibrary && !string.IsNullOrWhiteSpace(result.OutputDirectory))
                {
                    await _library.ReplaceStoredFilesAsync(source.Id, result.OutputDirectory, CancellationToken.None);
                }
                task?.SetOutputArtifacts(result.OutputDirectory, result.ReportMarkdownPath);
                task?.MarkCompleted();
                LogService.Info($"修复 patch 完成：Mod={source.Metadata.Name}，Unit={result.OutputUnitCount}，替换Mesh={result.ReplacementMeshCount}，极小化Mesh={result.MinifiedMeshCount}，警告={warningCount}，输出={result.OutputDirectory}。");
                if (bridge is null) _notifications?.Show(importToLibrary ? "Same-key 重建结果已替换当前 Mod，并开始重建缓存。" : "Same-key 重建结果已写入 Output 文件夹。", NotificationLevel.Info, TimeSpan.FromSeconds(8));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
            {
                task?.SetOutputArtifacts(destination, Path.Combine(destination, "canonical-report.md"));
                task?.MarkFailed(exception.Message);
                SameKeyReconstructionSummary = $"重建失败：{exception.Message}";
                LogService.Error($"修复 patch 异常：Mod={source.Metadata.Name}，输出={destination}，错误={exception}");
                if (task is null) _notifications?.Show(SameKeyReconstructionSummary, NotificationLevel.Error, TimeSpan.FromSeconds(12));
            }
            catch (OperationCanceledException)
            {
                task?.MarkCanceled();
                SameKeyReconstructionSummary = "重建已取消。";
                if (task is null) _notifications?.Show("Same-key 重建已取消。", NotificationLevel.Info, TimeSpan.FromSeconds(8));
            }
            finally
            {
                _sameKeyReconstructionRunning = false;
                OnPropertyChanged(nameof(SameKeyReconstructionSummary));
                RaiseSameKeyReconstructionCommandState();
            }
        }

        private async Task PlanCrossArmorTransferAsync()
        {
            if (!TryGetCurrentNode(out var source)) return;
            try
            {
				var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
                LogService.Info($"替换护甲计划开始：Mod={source.Metadata.Name}，节点={source.Id.Value:N}。");
                _notifications?.Show("替换护甲：正在读取 GameData 装备部件目录…", NotificationLevel.Info, TimeSpan.FromSeconds(30));
                _notifications?.Show("替换护甲：正在读取来源 Patch 并匹配 GameData 部位目录…", NotificationLevel.Info, TimeSpan.FromSeconds(30));
				if (!await HasCurrentGameDataIndexAsync().ConfigureAwait(false))
				{
					const string message = "请在设置页重建资产索引";
                    LogService.Info($"替换护甲计划结束：Mod={source.Metadata.Name}，基础 GameData 资产索引不可用或已过期。");
                    _notifications?.Show(message, NotificationLevel.Error, TimeSpan.FromSeconds(10));
                    return;
                }

				var operationId = Guid.NewGuid();
				var sourceFacts = await _equipmentSourceFactsReader.ReadAsync(
					source,
					_library.ModsRootDirectory,
					new ModInformationRequest(
						ModInformationKind.AssetInventory,
						"CrossArmorPlan",
						Context: ModInformationRequestContext.Create(
							ModInformationCacheScope.None,
							operationId,
							operationName: "CrossArmorPlan"))
					{
						Property = ModInformationPropertyKind.UnitPartMapping
					},
					CancellationToken.None).ConfigureAwait(false);
				var sourcePatchPaths = sourceFacts.SourcePatchTocPaths.ToArray();
				var sourceCandidates = sourceFacts.SourceCandidates;
				var allCandidates = sourceFacts.TargetCandidates;
				var transferableSourceUnitKeys = sourceCandidates
					.SelectMany(entry => entry.Parts)
					.Select(part => part.UnitAssetKey)
					.ToHashSet();
				var catalogPartCount = allCandidates.Sum(entry => entry.Parts.Count);
				var matchedPartCount = sourceCandidates.Sum(entry => entry.Parts.Count);
				var readableUnitCount = sourceFacts.Units.Count(unit => unit.IsReadable);
				var hiddenUnitCount = sourceFacts.Units.Count(unit => unit.IsHidden);
				var failedUnitCount = sourceFacts.Units.Count(unit => !unit.IsReadable);
				LogService.Info($"替换护甲计划阶段：统一来源读取完成，耗时={preparationTimer.ElapsedMilliseconds}ms，源Patch={sourcePatchPaths.Length}，扫描Unit={sourceFacts.Units.Count}，可读={readableUnitCount}，隐藏={hiddenUnitCount}，失败={failedUnitCount}。");
				LogService.Info($"替换护甲来源诊断：Mod={source.Metadata.Name}，模式=统一读取器，源Patch={sourcePatchPaths.Length}，可转移Unit={transferableSourceUnitKeys.Count}，GameData部件={catalogPartCount}，保留部件={matchedPartCount}，Unit目录={allCandidates.Count}。");
				if (sourceFacts.Issues.Count != 0)
					LogService.Info($"替换护甲来源读取诊断：{string.Join(" | ", sourceFacts.Issues.Take(6).Select(issue => $"{issue.Code}: {issue.Message}"))}");
				if (sourcePatchPaths.Length == 0)
				{
					const string message = "当前 Mod 没有可读取的 Patch 主文件。";
					_notifications?.Show(message, NotificationLevel.Info, TimeSpan.FromSeconds(10));
					return;
				}
				if (allCandidates.Count == 0)
				{
					const string message = "请在设置页重建 Unit 部位";
					LogService.Info($"替换护甲计划结束：Mod={source.Metadata.Name}，GameData 护甲 Unit 部位目录为空。");
					_notifications?.Show(message, NotificationLevel.Error, TimeSpan.FromSeconds(10));
					return;
				}
                if (sourceCandidates.Count == 0)
                {
                    LogService.Info($"替换护甲计划结束：Mod={source.Metadata.Name}，没有可转移的真实 Unit 几何。");
                    _notifications?.Show("当前 Mod 没有可转移的真实 Unit 几何：索引未匹配、Unit 无法读取或所有匹配 mesh 均已极小化。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
                    return;
                }
                if (sourcePatchPaths.Length != 1)
                {
                    LogService.Info($"替换护甲计划结束：Mod={source.Metadata.Name}，Patch组数={sourcePatchPaths.Length}，当前流程要求一个 Patch 主文件组。");
                    _notifications?.Show("跨护甲验证候选目前仅支持源 Mod 含一个 Patch 主文件组。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
                    return;
                }
                var browser = CoreServices.CreateGameDataArchiveBrowserService(_paths, _derivedState.InformationCenter);
                var viewModel = new HD2ModManager.Views.CrossArmorTransferPlanWindowViewModel(
                    _equipmentUnitCatalog, sourceCandidates, allCandidates, sourcePatchPaths[0], SettingsService.GetGameDataFolder(),
					sourceFacts.GetPreparedEntries(sourcePatchPaths[0]), _paths, null,
					cancellationToken => browser.BuildAsync(_library.Snapshot, _library.ModsRootDirectory, SettingsService.GetGameDataFolder(), cancellationToken).AsTask(),
					_library.InformationReader);
                await OpenCrossArmorPlanOnUiThreadAsync(viewModel).ConfigureAwait(false);
				preparationTimer.Stop();
                LogService.Info($"替换护甲计划完成：Mod={source.Metadata.Name}，源候选={sourceCandidates.Count}，目标候选={allCandidates.Count}，源Patch={sourcePatchPaths[0]}。");
                _notifications?.Show("替换护甲：映射计划已准备完成，请确认目标部件后生成候选。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                LogService.Error($"替换护甲计划异常：Mod={source.Metadata.Name}，错误={exception}");
                RunOnUiThread(() => _notifications?.Show($"读取跨护甲计划失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(12)));
            }
        }

        private static async Task OpenCrossArmorPlanOnUiThreadAsync(HD2ModManager.Views.CrossArmorTransferPlanWindowViewModel viewModel)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.OpenCrossArmorPlan(viewModel);
                return;
            }
            await dispatcher.InvokeAsync(() =>
            {
                if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.OpenCrossArmorPlan(viewModel);
            }).Task.ConfigureAwait(false);
        }

        private string[] FindBasePatchPaths(ModNode source)
        {
            var directory = Path.Combine(_library.ModsRootDirectory, source.RelativePath);
            if (!Directory.Exists(directory)) return Array.Empty<string>();
            var parser = CoreServices.CreatePatchFileNameParser();
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void OpenMaterialPackaging(bool splitEmbeddedMaterials, bool requireAllExternalMaterials = false)
        {
            if (!TryGetCurrentNode(out var source)) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
            shell.OpenMaterialPackaging(this, new MaterialPackagingPageViewModel(
                source,
                _library.ModsRootDirectory,
                _materialPackaging,
                _library,
                _notifications,
                splitEmbeddedMaterials,
                requireAllExternalMaterials,
                _materialState,
                _cachedMaterialDeliveryFactsNodeId == source.Id ? _cachedMaterialDeliveryFacts : null));
        }

        private void OpenPatchResourceRepackaging()
        {
            if (Mod is null || !CanPatchResourceRepackaging) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenPatchResourceRepackaging(Mod.Guid);
        }

        private bool TryGetCurrentNode(out ModNode node)
        {
            node = default!;
            return Mod != null && TryParseNodeId(Mod.Guid, out var nodeId) && _library.Snapshot.Nodes.TryGetValue(nodeId, out node!);
        }

        private bool IsCurrentDetailRequest(ModNode node, int requestGeneration)
            => !_disposed
                && requestGeneration == _detailRequestGeneration
                && TryGetCurrentNode(out var currentNode)
                && currentNode.Id == node.Id;

        private void RaiseMaterialCommandStates()
        {
            OnPropertyChanged(nameof(CanSplitEmbeddedMaterials)); OnPropertyChanged(nameof(CanReplaceEmbeddedMaterials)); OnPropertyChanged(nameof(CanEmbedExternalMaterials));
            SplitEmbeddedMaterialsCommand.RaiseCanExecuteChanged(); ReplaceEmbeddedMaterialsCommand.RaiseCanExecuteChanged(); EmbedExternalMaterialsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanPatchResourceRepackaging));
            PatchResourceRepackagingCommand.RaiseCanExecuteChanged();
        }

        private void RaiseSameKeyReconstructionCommandState()
        {
            OnPropertyChanged(nameof(CanRebuildSameKey));
			OnPropertyChanged(nameof(CanPlanCrossArmorTransfer));
            RebuildSameKeyCommand.RaiseCanExecuteChanged();
			PlanCrossArmorTransferCommand.RaiseCanExecuteChanged();
			PlanHelmetToArmorMergeCommand.RaiseCanExecuteChanged();
			ToggleHelmetToArmorMergeCommand.RaiseCanExecuteChanged();
			PlanDecorationCommand.RaiseCanExecuteChanged();
			PlanBatchDecorationCommand.RaiseCanExecuteChanged();
        }

        private static string SanitizeFileName(string name) => string.Concat(name.Select(character => System.IO.Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();

        private void AddToProfile()
        {
            if (Mod == null) return;
            if (Mod.IsDecoration || Mod.IsOption)
            {
                _notifications?.Show(Mod.IsDecoration ? "装饰 Mod 不能加入配置；请在目标主 Mod 中启用它。" : "选项 Mod 不能加入配置；请在目标主 Mod 中启用它。", NotificationLevel.Info);
                return;
            }
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
            {
                _ = shell.AddModToSelectedProfileAsync(Mod.Guid, Mod.Name);
            }
        }

        private void OpenDecorationPlan()
        {
            if (Mod is null || !CanShowDecorationPlan) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenDecorationPlan(Mod.Guid);
        }

        private void OpenHelmetToArmorMergePlan()
        {
            if (Mod is null || !CanPlanHelmetToArmorMerge) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenHelmetToArmorMergePlan(Mod.Guid);
        }

        private async Task ToggleHelmetToArmorMergeAsync()
        {
            if (Mod is null || !CanToggleHelmetToArmorMerge) return;
            var enable = !IsHelmetToArmorMerged;
            _helmetMergeRunning = true;
            OnPropertyChanged(nameof(CanToggleHelmetToArmorMerge));
            ToggleHelmetToArmorMergeCommand.RaiseCanExecuteChanged();
            var task = _backgroundTasks?.Enqueue(
                BackgroundTaskKind.Other,
                enable ? "临时将头盔合并到盔甲" : "取消头盔合并",
                Mod.Name,
                origin: "Mod 详情",
                userVisibleReason: enable ? "生成并启用当前 Mod 的头盔合并 Overwrite。" : "移除头盔合并状态并恢复当前 Mod 的原始/装饰 Overwrite。",
                canCancel: true,
                presentation: BackgroundTaskPresentation.Foreground);
            try
            {
                task?.MarkRunning(enable ? "正在合并" : "正在恢复");
                var result = await _library.SetHelmetToArmorMergedAsync(Mod.Guid, enable, task?.CancellationToken ?? CancellationToken.None).ConfigureAwait(true);
                task?.MarkCompleted();
                Refresh();
                _notifications?.Show(enable
                    ? $"已临时将头盔合并到盔甲：胸部 Unit {result.MergedArmorUnitCount}，隐藏头盔 {result.HiddenHelmetUnitCount}。"
                    : "已取消将头盔合并到盔甲。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                task?.MarkCanceled();
                _notifications?.Show("头盔合并操作已取消。", NotificationLevel.Info);
            }
            catch (Exception exception)
            {
                task?.MarkFailed(exception.Message);
                _notifications?.Show($"头盔合并操作失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(12));
            }
            finally
            {
                _helmetMergeRunning = false;
                OnPropertyChanged(nameof(CanToggleHelmetToArmorMerge));
                ToggleHelmetToArmorMergeCommand.RaiseCanExecuteChanged();
            }
        }

        private void OpenBatchDecorationPlan()
        {
            if (Mod is null || !CanShowDecorationPlan) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell)
                shell.OpenBatchDecorationPlan(Mod.Guid);
        }

        private void OpenDecorationEditor()
        {
            if (Mod is null || !CanEditDecoration) return;
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.OpenDecorationEditor(Mod.Guid);
        }

        private void RefreshDecorationSummary()
        {
            try
            {
                var path = Path.Combine(_library.ResolveAbsolutePath(Mod?.SourcePath), "decoration.json");
                var plan = JsonSerializer.Deserialize<DecorationPlanDocument>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (plan is null) return;
                DecorationSummary = $"来源快照：{plan.SourceUnits.Count} 个 Unit，{FileGroupCount} 个 Patch 组。\n目标部位：{plan.Plan.TargetPart}；目标身形：{plan.Plan.TargetBodyVariant}；双身形：{plan.Plan.DualVariantMode}。\n来源层级：{(plan.Plan.SourcePartLayers.Count == 0 ? "未记录" : string.Join("、", plan.Plan.SourcePartLayers))}；来源目标一致时{(plan.Plan.ReplaceWhenSourcePartLayerMatches ? "替换主体几何" : "直接注入")}。\n目标主体：{plan.Plan.TargetModGuids.Count} 个。";
                OnPropertyChanged(nameof(DecorationSummary));
            }
            catch { DecorationSummary = "装饰计划无法读取。"; OnPropertyChanged(nameof(DecorationSummary)); }
        }

        private async Task DeleteAsync()
        {
            if (Mod == null) return;
            var name = Mod.Name;
            var targets = ModDeletionConfirmation.ConfirmTargets(
                _library,
                [Mod.Guid],
                $"确定删除 Mod“{name}”？\n这会同时删除库中的已存储文件。");
            if (targets is null) return;
            ThumbnailService.CancelPendingGeneration();
            var deletion = await _library.RemoveManyAsync(targets).ConfigureAwait(true);
            _notifications?.Show(
                ModLibraryService.DescribeDeletionResult(deletion),
                deletion.HasFailures ? NotificationLevel.Warning : NotificationLevel.Info);
            if (!deletion.RemovedNodeIds.Any(id => string.Equals(id.Value.ToString("N"), ModId, StringComparison.OrdinalIgnoreCase))) return;
            Mod = null;
            var shell = System.Windows.Application.Current?.MainWindow as HD2ModManager.MainWindow;
            var vm = shell?.DataContext as ShellViewModel;
            vm?.Navigate(HD2ModManager.Enums.WorkspaceMode.LibraryOnly);
        }

        private string BuildProfileStatus()
        {
            if (Mod == null) return "未找到 Mod";
            var active = _profiles.ActiveProfile;
            if (active == null) return "当前没有活动配置";
            if (!TryParseNodeId(Mod.Guid, out var nodeId)) return "无法识别 Mod ID";
            var entry = active.Entries.FirstOrDefault(e => e.NodeId == nodeId);
            return entry == null
                ? $"未加入活动配置：{active.Name}"
                : $"已启用在活动配置：{active.Name}，顺序 {entry.LoadOrder}";
        }

        private string BuildFileIntegritySummary()
        {
            if (Mod == null) return "未找到 Mod";
            var derived = _library.GetDerivedData(Mod.Guid);
            var modDirectory = derived?.AbsoluteDirectory ?? _library.ResolveAbsolutePath(Mod.SourcePath);
            if (string.IsNullOrWhiteSpace(modDirectory) || !System.IO.Directory.Exists(modDirectory)) return "目录不存在";
            var patchFiles = derived?.PatchFiles ?? Array.Empty<IndexedPatchFile>();
            if (patchFiles.Count == 0) return "没有 patch 文件组";

            var lines = new List<string>();
            foreach (var group in patchFiles.Where(f => f.SidecarKind == PatchSidecarKind.Base).OrderBy(f => f.ArchiveHex16, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.NormalizedOrder))
            {
                var baseName = group.FileName;
                var baseExists = System.IO.File.Exists(System.IO.Path.Combine(modDirectory, baseName));
                var streamExists = System.IO.File.Exists(System.IO.Path.Combine(modDirectory, baseName + ".stream"));
                var gpuExists = System.IO.File.Exists(System.IO.Path.Combine(modDirectory, baseName + ".gpu_resources"));
                lines.Add($"{baseName}：{(baseExists ? "主体存在" : "缺少主体")}，stream {(streamExists ? "存在" : "无")}，gpu_resources {(gpuExists ? "存在" : "无")}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private string BuildConflictSummary()
        {
            if (Mod == null) return "未找到 Mod";
            var active = _profiles.ActiveProfile;
            if (active == null) return "当前没有活动配置，无法检测冲突";
            if (!TryParseNodeId(Mod.Guid, out var nodeId)) return "无法识别 Mod ID";
            var graph = _derivedState.Snapshot.ExpectedGraph;
            if (graph is null || graph.ProfileId != active.Id || graph.ProfileRevision != active.Revision) return "冲突数据正在后台更新。";
            var overlaps = graph.ArchiveOverlaps.Where(overlap => overlap.NodeIds.Contains(nodeId)).ToList();
            return overlaps.Count == 0 ? "未发现与当前配置的潜在重叠" : $"与 {overlaps.Sum(overlap => overlap.NodeIds.Count - 1)} 个 Mod 存在 {overlaps.Count} 个 archive 级潜在重叠";
        }

        private static bool TryParseNodeId(string value, out ModNodeId nodeId)
        {
            if (Guid.TryParse(value, out var guid))
            {
                nodeId = new ModNodeId(guid);
                return true;
            }

            nodeId = default;
            return false;
        }

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

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _derivedState.SnapshotChanged -= _snapshotChangedHandler;
            if (_selection is not null) _selection.SelectionChanged -= OnSelectionChanged;
            _advancedDetailsCancellation?.Cancel();
            _advancedDetailsCancellation?.Dispose();
            _advancedDetailsCancellation = null;
            _hostAttachments.Committed -= OnHostAttachmentProjectionCommitted;
            _hostAttachments.Dispose();
        }
    }

    internal sealed record HostAttachmentListState(
        string Guid,
        ModEntity Mod,
        bool IsSelected,
        string? DecorationStatus,
        string ContentVersion,
        string PresentationVersion)
    {
        public static HostAttachmentListState Create(ModEntity mod, bool isSelected, string? decorationStatus)
        {
            var contentVersion = string.Join('\u001f',
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
                decorationStatus);
            return new HostAttachmentListState(
                mod.Guid,
                mod,
                isSelected,
                decorationStatus,
                contentVersion,
                BuildPresentationVersion(contentVersion, isSelected));
        }

        public HostAttachmentListState WithSelection(bool isSelected)
            => IsSelected == isSelected
                ? this
                : this with
                {
                    IsSelected = isSelected,
                    PresentationVersion = BuildPresentationVersion(ContentVersion, isSelected)
                };

        private static string BuildPresentationVersion(string contentVersion, bool isSelected)
            => string.Join('\u001f', contentVersion, isSelected);
    }

}
