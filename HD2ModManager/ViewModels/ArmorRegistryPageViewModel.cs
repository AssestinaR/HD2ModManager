using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

public sealed class ArmorRegistryPageViewModel : FullWorkspacePageViewModel
{
	private enum AvailableArmorUnitFilter
	{
		All,
		HasEffectiveUnitOverride,
		NoEffectiveUnitOverride,
	}

	private readonly RuntimeArmorRegistryService service;
	private readonly NotificationService notifications;
	private readonly IProfileDeploymentCoordinator deploymentCoordinator;
	private readonly ModLibraryService library;
	private readonly SelectionCoordinator selection;
	private string fingerprint = "missing";
	private bool enabled;
	private bool isBusy;
	private bool hasChanges;
	private bool modelDefinitionsDirty;
	private bool baselineEnabled;
	private HashSet<(uint Target, uint Source)> baselineMappings = [];
	private string statusTitle = "尚未读取";
	private string statusDetail = "正在等待读取运行时护甲注册状态。";
	private string revisionText = "revision —";
	private string providersSearchText = string.Empty;
	private string availableSearchText = string.Empty;
	private string providerDetailSearchText = string.Empty;
	private AvailableArmorUnitFilter availableArmorUnitFilter;
	private uint? selectedProviderId;
	private uint? componentEditorOwnerId;
	private bool componentEditorIsTarget;
	private readonly HashSet<uint> pendingProviderIds = [];
	private IReadOnlyList<RuntimeArmorCatalogEntry> cachedCatalog = [];
	private IReadOnlyDictionary<string, ArmorDisplayFacts> cachedArmorDisplayFacts = new Dictionary<string, ArmorDisplayFacts>(StringComparer.OrdinalIgnoreCase);
	private IReadOnlyList<RuntimeArmorRegistryModel> modelDefinitions = [];
	private IReadOnlyList<RuntimeArmorRegistryMapping> modelMappings = [];
	private IReadOnlyList<RuntimeArmorRegistryPieceOverride> pieceOverrides = [];
	private IReadOnlyList<RuntimeArmorRegistryTargetMode> targetModes = [];
	private bool componentEditorFollowSource = true;
	private bool hasLoadedCatalog;
	private int workspaceRevision;
	private CancellationTokenSource? workspaceSaveCancellation;

	public ArmorRegistryPageViewModel(
		ModLibraryService library,
		NotificationService notifications,
		IProfileDeploymentCoordinator deploymentCoordinator,
		SelectionCoordinator selection)
	{
		Title = "运行时护甲注册";
		this.library = library ?? throw new ArgumentNullException(nameof(library));
		this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		this.deploymentCoordinator = deploymentCoordinator ?? throw new ArgumentNullException(nameof(deploymentCoordinator));
		this.selection = selection ?? throw new ArgumentNullException(nameof(selection));
		this.selection.SelectionChanged += OnGlobalSelectionChanged;
		ProvidersView = CollectionViewSource.GetDefaultView(Providers);
		AvailableArmorsView = CollectionViewSource.GetDefaultView(AvailableArmors);
		ProviderDetailItemsView = CollectionViewSource.GetDefaultView(ProviderDetailItems);
		ProvidersView.Filter = FilterProvider;
		AvailableArmorsView.Filter = FilterAvailableArmor;
		ProviderDetailItemsView.Filter = FilterProviderDetailItem;
		service = CoreServices.CreateRuntimeArmorRegistryService(SettingsService.CreateStoragePaths());
		RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy && !HasChanges);
		ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => !IsBusy && HasChanges);
		OpenRegistryFolderCommand = new RelayCommand(OpenRegistryFolder);
		AddCustomModelCommand = new RelayCommand(_ => AddCustomModel(), _ => !IsBusy);
		RemoveCustomModelCommand = new RelayCommand(
			parameter => RemoveCustomModel(parameter as RuntimeArmorModelEditorViewModel),
			_ => !IsBusy);
		AddComponentPieceCommand = new RelayCommand(_ => AddComponentPiece(),
			_ => IsComponentEditorCustomModel && !IsBusy);
		ConfirmAddComponentRowCommand = new RelayCommand(_ => AddComponentRow(),
			_ => IsComponentEditorCustomModel && ComponentEditorEditable && !IsBusy);
		PageActions.Add(new PageActionViewModel("＋", "新增自定义素体", AddCustomModelCommand,
			order: 10, kind: "ArmorCreateModel"));
		PageActions.Add(new PageActionViewModel("□", "打开运行时目录", OpenRegistryFolderCommand,
			order: 20, kind: "OpenFolder"));
		PageActions.Add(new PageActionViewModel("↻", "刷新", RefreshCommand,
			order: 30, kind: "Refresh"));
	}

	public BulkObservableCollection<RuntimeArmorChoiceViewModel> AvailableArmors { get; } = [];
	public BulkObservableCollection<RuntimeArmorChoiceViewModel> Providers { get; } = [];
	public BulkObservableCollection<RuntimeArmorChoiceViewModel> ProviderDetailItems { get; } = [];
	public BulkObservableCollection<RuntimeArmorModelEditorViewModel> CustomModels { get; } = [];
	public ObservableCollection<RuntimeArmorModelPieceEditorViewModel> ComponentEditorPieces { get; } = [];
	public ObservableCollection<RuntimeArmorComponentGroupViewModel> ComponentEditorGroups { get; } = [];
	public ObservableCollection<RuntimeArmorComponentRowViewModel> ComponentEditorRows { get; } = [];
	public ICollectionView AvailableArmorsView { get; }
	public ICollectionView ProvidersView { get; }
	public ICollectionView ProviderDetailItemsView { get; }
	public ObservableCollection<RuntimeArmorMappingRowViewModel> Mappings { get; } = [];

	public string ProvidersSearchText
	{
		get => providersSearchText;
		set
		{
			if (!SetField(ref providersSearchText, value ?? string.Empty)) return;
			ProvidersView.Refresh();
			OnPropertyChanged(nameof(ProvidersSummary));
		}
	}

	public string AvailableSearchText
	{
		get => availableSearchText;
		set
		{
			if (!SetField(ref availableSearchText, value ?? string.Empty)) return;
			AvailableArmorsView.Refresh();
			OnPropertyChanged(nameof(AvailableSummary));
		}
	}

	public string AvailableArmorUnitFilterText => availableArmorUnitFilter switch
	{
		AvailableArmorUnitFilter.HasEffectiveUnitOverride => "Unit 覆盖：已命中",
		AvailableArmorUnitFilter.NoEffectiveUnitOverride => "Unit 覆盖：未命中",
		_ => "Unit 覆盖：全部",
	};

	public void CycleAvailableArmorUnitFilter()
	{
		availableArmorUnitFilter = availableArmorUnitFilter switch
		{
			AvailableArmorUnitFilter.All => AvailableArmorUnitFilter.HasEffectiveUnitOverride,
			AvailableArmorUnitFilter.HasEffectiveUnitOverride => AvailableArmorUnitFilter.NoEffectiveUnitOverride,
			_ => AvailableArmorUnitFilter.All,
		};
		OnPropertyChanged(nameof(AvailableArmorUnitFilterText));
		AvailableArmorsView.Refresh();
		OnPropertyChanged(nameof(AvailableSummary));
	}

	public string ProviderDetailSearchText
	{
		get => providerDetailSearchText;
		set
		{
			if (!SetField(ref providerDetailSearchText, value ?? string.Empty)) return;
			ProviderDetailItemsView.Refresh();
			OnPropertyChanged(nameof(ProviderDetailSummary));
		}
	}

	public override void Dispose()
	{
		selection.SelectionChanged -= OnGlobalSelectionChanged;
		workspaceSaveCancellation?.Cancel();
		workspaceSaveCancellation?.Dispose();
		base.Dispose();
	}

	public bool Enabled
	{
		get => enabled;
		set
		{
			if (!SetField(ref enabled, value)) return;
			RecomputeHasChanges();
		}
	}

	public bool IsBusy
	{
		get => isBusy;
		private set
		{
			if (!SetField(ref isBusy, value)) return;
			RaiseCommandStates();
		}
	}

	public bool HasChanges
	{
		get => hasChanges;
		private set
		{
			if (!SetField(ref hasChanges, value)) return;
			ApplyCommand.RaiseCanExecuteChanged();
			RefreshCommand.RaiseCanExecuteChanged();
		}
	}

	public bool IsProviderDetailOpen => selectedProviderId is not null;
	public bool IsComponentEditorOpen => componentEditorOwnerId is not null;
	public bool ComponentEditorIsTarget => componentEditorIsTarget;
	public bool IsComponentEditorCustomModel
		=> componentEditorOwnerId is { } id
			&& Choices.TryGetValue(id, out var choice)
			&& choice.IsCustomModel;
	public bool ComponentEditorFollowSource
	{
		get => componentEditorFollowSource;
		set
		{
			if (!SetField(ref componentEditorFollowSource, value)) return;
			MarkComponentEditorDirty();
			OnPropertyChanged(nameof(ComponentEditorEditable));
			ConfirmAddComponentRowCommand.RaiseCanExecuteChanged();
		}
	}
	public bool ComponentEditorEditable => !ComponentEditorIsTarget || !ComponentEditorFollowSource;
	public IReadOnlyList<ArmorComponentSlotOption> ComponentSlotOptions { get; } =
	[
		new(1, "头盔"), new(2, "胸部"), new(6, "左臂"), new(7, "右臂"),
		new(3, "胯部"), new(4, "左腿"), new(5, "右腿")
	];
	private uint newComponentSlot = 2;
	public uint NewComponentSlot
	{
		get => newComponentSlot;
		set => SetField(ref newComponentSlot, value);
	}
	public string ComponentEditorTitle => componentEditorOwnerId is { } id
		? (Choices.TryGetValue(id, out var choice) ? $"←  {choice.Name} · 视觉组件" : $"←  护甲 {id} · 视觉组件")
		: "视觉组件";
	public bool HasMappings => Mappings.Count != 0;
	public bool HasCustomModels => CustomModels.Count != 0;
	public IReadOnlyList<RuntimeArmorRegistryModel> ModelDefinitions => modelDefinitions;
	public int CustomModelCount => modelDefinitions.Count;
	public string StatusTitle { get => statusTitle; private set => SetField(ref statusTitle, value); }
	public string StatusDetail { get => statusDetail; private set => SetField(ref statusDetail, value); }
	public string RevisionText { get => revisionText; private set => SetField(ref revisionText, value); }
	public string RegistryPath => service.RegistryRoot;
	public string WorkspacePath => service.WorkspacePath;
	public string ProvidersSummary => providersSearchText.Length == 0
		? $"{Providers.Count} 个视觉来源"
		: $"{ProvidersView.Cast<object>().Count()} / {Providers.Count} 个视觉来源";
	public string AvailableSummary => availableSearchText.Length == 0
		&& availableArmorUnitFilter == AvailableArmorUnitFilter.All
			? $"{AvailableArmors.Count} 个未管理护甲"
		: $"{AvailableArmorsView.Cast<object>().Count()} / {AvailableArmors.Count} 个未管理护甲";
	public string ProviderDetailTitle => selectedProviderId is { } id
		? Choices.TryGetValue(id, out var choice) ? choice.Name : $"未知护甲 {id}"
		: "视觉映射目标";
	public string ProviderDetailHeaderTitle => $"←  {ProviderDetailTitle}";
	public string AvailableRowActionToolTip => IsProviderDetailOpen ? "注册到该视觉对象" : "转为视觉对象";
	public string ProviderDetailSummary => selectedProviderId is { } id
		? providerDetailSearchText.Length == 0
			? $"{Mappings.Count(row => row.Source.ItemId == id)} 个映射目标"
			: $"{ProviderDetailItemsView.Cast<object>().Count()} / {Mappings.Count(row => row.Source.ItemId == id)} 个映射目标"
		: "选择一个视觉来源";
	private Dictionary<uint, RuntimeArmorChoiceViewModel> Choices { get; set; } = [];

	public AsyncRelayCommand RefreshCommand { get; }
	public AsyncRelayCommand ApplyCommand { get; }
	public RelayCommand OpenRegistryFolderCommand { get; }
	public RelayCommand AddCustomModelCommand { get; }
	public RelayCommand RemoveCustomModelCommand { get; }
	public RelayCommand AddComponentPieceCommand { get; }
	public RelayCommand ConfirmAddComponentRowCommand { get; }

	public Task InitializeAsync() => hasLoadedCatalog ? RefreshRuntimeStatusAsync() : RefreshAsync();

	private async Task RefreshAsync()
	{
		if (IsBusy) return;
		IsBusy = true;
		try
		{
			var catalogTask = service.ReadArmorCatalogAsync().AsTask();
			var snapshotTask = service.ReadSnapshotAsync().AsTask();
			await Task.WhenAll(catalogTask, snapshotTask);
			var snapshot = await snapshotTask;
			var catalog = await catalogTask;
			var armorDisplayFacts = await ReadArmorDisplayFactsAsync(catalog);
			cachedCatalog = catalog;
			cachedArmorDisplayFacts = armorDisplayFacts;
			hasLoadedCatalog = true;
			ApplySnapshot(snapshot, rebuildChoices: true, resetNavigation: true);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			StatusTitle = "读取失败";
			StatusDetail = exception.Message;
			notifications.Show($"读取运行时护甲注册失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(10), "护甲注册");
		}
		finally
		{
			IsBusy = false;
		}
	}

	public async Task RefreshRuntimeStatusAsync()
	{
		if (IsBusy) return;
		try
		{
			var snapshot = await service.ReadSnapshotAsync();
			if (!HasChanges && !string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
				ApplySnapshot(snapshot, rebuildChoices: hasLoadedCatalog, resetNavigation: false);
			else
				UpdateStatus(snapshot);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException)
		{
			LogService.Warn($"刷新护甲注册运行时状态失败：{exception.Message}");
		}
	}

	private void ApplySnapshot(
		RuntimeArmorRegistrySnapshot snapshot,
		bool rebuildChoices,
		bool resetNavigation)
	{
		if (rebuildChoices)
		{
			Choices = cachedCatalog
				.Select(item => new RuntimeArmorChoiceViewModel(
					item.ItemId,
					item.ArchiveId,
					item.DisplayName,
					cachedArmorDisplayFacts.GetValueOrDefault(item.ArchiveId)?.ImagePath,
					cachedArmorDisplayFacts.TryGetValue(item.ArchiveId, out var facts)
						? facts.HasEffectiveUnitOverride
						: null))
				.ToDictionary(item => item.ItemId);
		}
		foreach (var mapping in snapshot.Document.Mappings)
		{
			if (mapping.SourceItemId is not { } sourceItemId) continue;
			if (!Choices.ContainsKey(mapping.TargetItemId))
				Choices[mapping.TargetItemId] = RuntimeArmorChoiceViewModel.Unknown(mapping.TargetItemId);
			if (!Choices.ContainsKey(sourceItemId))
				Choices[sourceItemId] = RuntimeArmorChoiceViewModel.Unknown(sourceItemId);
		}

		Mappings.Clear();
		modelDefinitions = snapshot.Document.ModelDefinitions;
		foreach (var model in modelDefinitions)
		{
			var syntheticId = SyntheticModelItemId(model.Key);
			Choices[syntheticId] = RuntimeArmorChoiceViewModel.CustomModel(
				syntheticId, model.Name ?? model.Key, model.Key);
		}
		modelMappings = [];
		pieceOverrides = snapshot.Document.PieceOverrideDefinitions;
		targetModes = snapshot.Document.TargetModeDefinitions;
		CustomModels.ReplaceWith(
			modelDefinitions.Select(model => new RuntimeArmorModelEditorViewModel(model, MarkModelDefinitionsDirty)),
			ListTransitionKind.Replace);
		modelDefinitionsDirty = false;
		OnPropertyChanged(nameof(HasCustomModels));
		OnPropertyChanged(nameof(ModelDefinitions));
		OnPropertyChanged(nameof(CustomModelCount));
		foreach (var mapping in snapshot.Document.Mappings)
		{
			if (mapping.IsModelMapping)
			{
				var model = modelDefinitions.FirstOrDefault(item =>
					string.Equals(item.Key, mapping.ModelKey, StringComparison.Ordinal));
				if (model is null) continue;
				Mappings.Add(new RuntimeArmorMappingRowViewModel(
					Choices[mapping.TargetItemId],
					Choices[SyntheticModelItemId(model.Key)],
					model.Key));
				continue;
			}
			if (mapping.SourceItemId is not { } sourceItemId) continue;
			Mappings.Add(new RuntimeArmorMappingRowViewModel(
				Choices[mapping.TargetItemId],
				Choices[sourceItemId]));
		}
		if (resetNavigation)
		{
			pendingProviderIds.Clear();
			selectedProviderId = null;
			OnPropertyChanged(nameof(IsProviderDetailOpen));
		}
		else
		{
			pendingProviderIds.Clear();
			if (selectedProviderId is { } selected
				&& !snapshot.Document.Mappings.Any(mapping => mapping.SourceItemId == selected))
			{
				selectedProviderId = null;
				OnPropertyChanged(nameof(IsProviderDetailOpen));
			}
		}

		selection.Clear();
		RebuildProjections();
		enabled = snapshot.Document.Enabled;
		OnPropertyChanged(nameof(Enabled));
		baselineEnabled = enabled;
		baselineMappings = GetMappingKeys();
		fingerprint = snapshot.Fingerprint;
		workspaceRevision = snapshot.Document.Revision;
		RevisionText = $"revision {snapshot.Document.Revision} · {Mappings.Count} 条映射";
		UpdateStatus(snapshot);
		HasChanges = false;
	}

	private async Task<IReadOnlyDictionary<string, ArmorDisplayFacts>> ReadArmorDisplayFactsAsync(
		IReadOnlyList<RuntimeArmorCatalogEntry> catalog)
	{
		var result = new Dictionary<string, ArmorDisplayFacts>(StringComparer.OrdinalIgnoreCase);
		var gameData = SettingsService.GetGameDataFolder();
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return result;

		try
		{
			var browser = CoreServices.CreateGameDataArchiveBrowserService(
				SettingsService.CreateStoragePaths(),
				library.InformationCenter);
			var snapshot = await browser.BuildAsync(library.Snapshot, library.ModsRootDirectory, gameData);
			if (snapshot is null) return result;

			var parents = new Dictionary<Guid, IReadOnlyList<Guid>>();
			var nodesById = library.Snapshot.Nodes;
			var explicitParentRelations = new HashSet<Guid>();
			foreach (var node in library.Snapshot.Nodes.Values)
			{
				var declaredHosts = (node.Metadata.HostModGuids ?? [])
					.Select(host => Guid.TryParse(host, out var id) ? (Guid?)id : null)
					.Where(id => id is not null)
					.Select(id => id!.Value)
					.Where(id => library.Snapshot.Nodes.Keys.Any(key => key.Value == id))
					.ToArray();
				if (declaredHosts.Length > 0)
				{
					parents[node.Id.Value] = declaredHosts;
					explicitParentRelations.Add(node.Id.Value);
				}
			}
			foreach (var node in library.Snapshot.Nodes.Values)
			{
				foreach (var child in node.Children)
				{
					if (explicitParentRelations.Contains(child.Value)) continue;
					if (!parents.TryGetValue(child.Value, out var existing))
						parents[child.Value] = [node.Id.Value];
					else
						parents[child.Value] = existing.Append(node.Id.Value).ToArray();
				}
			}

			foreach (var item in catalog)
			{
				var archive = snapshot.Archives.FirstOrDefault(candidate =>
					string.Equals(candidate.Archive.PackageName, item.ArchiveId, StringComparison.OrdinalIgnoreCase));
				if (archive is null) continue;

				var hasEffectiveUnitOverride = archive.Overlay.EffectiveAssets.Any(asset =>
					asset.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId
					&& asset.WinnerNodeId is not null);
				var unitWinners = archive.Overlay.EffectiveAssets
					.Where(asset => asset.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId)
					.Where(asset => asset.WinnerNodeId is not null)
					.GroupBy(asset => asset.WinnerNodeId!.Value)
					.OrderByDescending(group => group.Count())
					.ThenByDescending(group => group.Max(asset => asset.TargetPatchIndex))
					.ThenBy(group => group.Key.Value);
				var unitWinner = unitWinners.FirstOrDefault()?.Key;

				var directWinner = unitWinner ?? archive.Overlay.EffectiveAssets
					.Where(asset => asset.WinnerNodeId is not null)
					.GroupBy(asset => asset.WinnerNodeId!.Value)
					.OrderByDescending(group => group.Count())
					.ThenByDescending(group => group.Max(asset => asset.TargetPatchIndex))
					.ThenBy(group => group.Key.Value)
					.Select(group => (ModNodeId?)group.Key)
					.FirstOrDefault();
				var imagePath = directWinner is { } winner && nodesById.ContainsKey(winner)
					? FindModIconInParentChain(winner.Value, parents)
					: null;
				result[item.ArchiveId] = new ArmorDisplayFacts(imagePath, hasEffectiveUnitOverride);
			}
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidOperationException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			LogService.Warn($"读取护甲覆盖图标失败：{exception.Message}");
		}

		return result;
	}

	private string? FindModIconInParentChain(Guid nodeId, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> parents)
	{
		var visited = new HashSet<Guid>();
		var queue = new Queue<Guid>();
		queue.Enqueue(nodeId);
		while (queue.TryDequeue(out var current))
		{
			if (!visited.Add(current)) continue;
			var mod = library.Get(current.ToString("N"));
			var icon = new[] { mod?.Image, mod?.IconPath, mod?.ThumbImage }
				.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
			if (icon is not null) return icon;
			if (parents.TryGetValue(current, out var parentIds))
			{
				foreach (var parentId in parentIds) queue.Enqueue(parentId);
			}
		}
		return null;
	}

	public void ApplyProviderSelection(IReadOnlyList<string> selectedKeys)
	{
		ApplyMutuallyExclusiveSelection("ArmorRegistry:Providers", Providers, selectedKeys);
	}

	public void ApplyDetailSelection(IReadOnlyList<string> selectedKeys)
	{
		ApplyMutuallyExclusiveSelection("ArmorRegistry:Targets", ProviderDetailItems, selectedKeys);
	}

	public void ApplyAvailableSelection(IReadOnlyList<string> selectedKeys)
	{
		ApplyMutuallyExclusiveSelection("ArmorRegistry:Available", AvailableArmors, selectedKeys);
	}

	private void ApplyMutuallyExclusiveSelection(
		string scope,
		IEnumerable<RuntimeArmorChoiceViewModel> selectedItems,
		IReadOnlyList<string> selectedKeys)
	{
		var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var item in AvailableArmors.Concat(Providers).Concat(ProviderDetailItems))
			item.IsSelected = false;
		foreach (var item in selectedItems)
			item.IsSelected = selected.Contains(item.SelectionKey);
		selection.Replace(scope, selectedKeys);
	}

	private void OnGlobalSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (e.Scope?.StartsWith("ArmorRegistry:", StringComparison.Ordinal) == true) return;
		foreach (var item in AvailableArmors.Concat(Providers).Concat(ProviderDetailItems))
			item.IsSelected = false;
	}

	public void OpenProviderDetails(RuntimeArmorChoiceViewModel? provider)
	{
		if (provider is null || !Providers.Any(item => item.ItemId == provider.ItemId)) return;
		foreach (var item in AvailableArmors.Concat(Providers).Concat(ProviderDetailItems))
			item.IsSelected = false;
		selection.Clear();
		selectedProviderId = provider.ItemId;
		OnPropertyChanged(nameof(IsProviderDetailOpen));
		OnPropertyChanged(nameof(ProviderDetailTitle));
		OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
		OnPropertyChanged(nameof(AvailableRowActionToolTip));
		OnPropertyChanged(nameof(ProviderDetailSummary));
		RebuildProjections();
	}

	public void CloseProvider()
	{
		if (selectedProviderId is null) return;
		selectedProviderId = null;
		OnPropertyChanged(nameof(IsProviderDetailOpen));
		OnPropertyChanged(nameof(ProviderDetailTitle));
		OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
		OnPropertyChanged(nameof(AvailableRowActionToolTip));
		OnPropertyChanged(nameof(ProviderDetailSummary));
		selection.Clear();
		RebuildProjections();
	}

	public async Task OpenComponentEditorAsync(RuntimeArmorChoiceViewModel? item, bool isTarget)
	{
		if (item is null || IsBusy) return;
		var model = modelDefinitions.FirstOrDefault(model =>
			string.Equals(model.Key, item.ArchiveId, StringComparison.OrdinalIgnoreCase))?.Pieces;
		var pieces = model;
		if (pieces is null || pieces.Count == 0)
			pieces = await service.ReadArmorPiecesAsync(item.ItemId);
		componentEditorOwnerId = item.ItemId;
		componentEditorIsTarget = isTarget;
		componentEditorFollowSource = targetModes.FirstOrDefault(mode => mode.TargetItemId == item.ItemId)?.FollowSource ?? true;
		ComponentEditorPieces.Clear();
		foreach (var piece in pieces)
		{
			var normalizedPiece = piece with { IsDefaultBase = piece.IsDefaultBase || IsLegacyDefaultBasePiece(item, piece) };
			var enabled = pieceOverrides
				.FirstOrDefault(overrideItem => overrideItem.OwnerItemId == item.ItemId
					&& overrideItem.Body == normalizedPiece.Body
					&& overrideItem.Slot == normalizedPiece.Slot
					&& overrideItem.PieceType == normalizedPiece.PieceType
					&& string.Equals(overrideItem.Unit, normalizedPiece.Unit, StringComparison.OrdinalIgnoreCase))
				?.Enabled ?? true;
			ComponentEditorPieces.Add(new RuntimeArmorModelPieceEditorViewModel(
				normalizedPiece with { Enabled = enabled },
				MarkComponentEditorDirty,
				item.IsCustomModel && !normalizedPiece.IsDefaultBase,
				RemoveComponentPiece));
		}
		RebuildComponentEditorGroups();
		OnPropertyChanged(nameof(IsComponentEditorOpen));
		OnPropertyChanged(nameof(ComponentEditorIsTarget));
		OnPropertyChanged(nameof(IsComponentEditorCustomModel));
		OnPropertyChanged(nameof(ComponentEditorFollowSource));
		OnPropertyChanged(nameof(ComponentEditorEditable));
		OnPropertyChanged(nameof(ComponentEditorTitle));
		ConfirmAddComponentRowCommand.RaiseCanExecuteChanged();
		selection.Clear();
	}

	private static bool IsLegacyDefaultBasePiece(RuntimeArmorChoiceViewModel item, RuntimeArmorRegistryModelPiece piece)
		=> item.IsCustomModel
			&& piece.PieceType == 0
			&& piece.Slot is 2 or 3 or 4 or 5 or 6 or 7;

	public void CloseComponentEditor()
	{
		if (!IsComponentEditorOpen) return;
		componentEditorOwnerId = null;
		componentEditorIsTarget = false;
		componentEditorFollowSource = true;
		ComponentEditorPieces.Clear();
		ComponentEditorGroups.Clear();
		ComponentEditorRows.Clear();
		OnPropertyChanged(nameof(IsComponentEditorOpen));
		OnPropertyChanged(nameof(ComponentEditorIsTarget));
		OnPropertyChanged(nameof(IsComponentEditorCustomModel));
		OnPropertyChanged(nameof(ComponentEditorFollowSource));
		OnPropertyChanged(nameof(ComponentEditorEditable));
		OnPropertyChanged(nameof(ComponentEditorTitle));
	}

	public void PromoteAvailableAsProviders(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return;
		var ids = keys
			.Select(key => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (uint?)id : null)
			.ToArray();
		if (ids.Any(id => id is null) || ids.Any(id => !AvailableArmors.Any(item => item.ItemId == id))) return;
		foreach (var id in ids)
			pendingProviderIds.Add(id!.Value);
		RebuildProjections();
		RecomputeHasChanges();
		selection.Clear();
	}

	public void ReleaseProviderSelection(IReadOnlyList<string> keys) => ReleaseToAvailable(keys);
	public void ReleaseTargetSelection(IReadOnlyList<string> keys) => ReleaseToAvailable(keys);

	public void AssignAvailableItem(RuntimeArmorChoiceViewModel? item)
	{
		if (item is not null) AssignFromAvailable([item.SelectionKey]);
	}

	public void ReleaseProviderItem(RuntimeArmorChoiceViewModel? item)
	{
		if (item is not null) ReleaseToAvailable([item.SelectionKey]);
	}

	public void ReleaseTargetItem(RuntimeArmorChoiceViewModel? item)
	{
		if (item is not null) ReleaseToAvailable([item.SelectionKey]);
	}

	public void DeleteCustomModelItem(RuntimeArmorChoiceViewModel? item)
	{
		if (item is null || !item.IsCustomModel) return;
		var model = CustomModels.FirstOrDefault(candidate =>
			string.Equals(candidate.Key, item.ModelKey, StringComparison.Ordinal));
		RemoveCustomModel(model);
	}

	public void RenameCustomModelItem(RuntimeArmorChoiceViewModel? item)
	{
		if (item is null || !item.IsCustomModel || item.ModelKey is not { } key) return;
		var model = CustomModels.FirstOrDefault(candidate =>
			string.Equals(candidate.Key, key, StringComparison.Ordinal));
		if (model is null) return;
		var name = Microsoft.VisualBasic.Interaction.InputBox(
			"请输入自定义视觉来源名称：",
			"重命名自定义素体",
			model.Name);
		if (string.IsNullOrWhiteSpace(name)) return;
		model.Rename(name);
		Choices[item.ItemId] = RuntimeArmorChoiceViewModel.CustomModel(item.ItemId, model.Name, model.Key);
		RebuildProjections();
	}

	public bool CanAssignFromAvailable(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return false;
		return keys.All(key =>
			uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
			&& AvailableArmors.Any(armor => armor.ItemId == id));
	}

	public void AssignFromAvailable(IReadOnlyList<string> keys)
	{
		if (!CanAssignFromAvailable(keys)) return;
		var ids = keys.Select(key => uint.Parse(key, CultureInfo.InvariantCulture)).ToArray();
		if (selectedProviderId is null)
		{
			if (ids.Length > 1)
			{
				foreach (var id in ids)
					pendingProviderIds.Add(id);
				RebuildProjections();
				RecomputeHasChanges();
				selection.Clear();
				return;
			}

			var providerId = ids[0];
			if (!Choices.ContainsKey(providerId)) return;
			pendingProviderIds.Add(providerId);
			selectedProviderId = providerId;
			OnPropertyChanged(nameof(IsProviderDetailOpen));
			OnPropertyChanged(nameof(ProviderDetailTitle));
			OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
			OnPropertyChanged(nameof(AvailableRowActionToolTip));
			OnPropertyChanged(nameof(ProviderDetailSummary));
			RebuildProjections();
			selection.Clear();
			return;
		}

		var source = selectedProviderId.Value;
		var sourceChoice = Choices[source];
		foreach (var id in ids)
		{
			if (id == source
				|| Mappings.Any(mapping => mapping.Target.ItemId == id)
				|| Providers.Any(provider => provider.ItemId == id))
				continue;
			Mappings.Add(new RuntimeArmorMappingRowViewModel(
				Choices[id],
				sourceChoice,
				sourceChoice.IsCustomModel ? sourceChoice.ModelKey : null));
		}
		if (ids.Any(id => Mappings.Any(mapping => mapping.Source.ItemId == source && mapping.Target.ItemId == id)))
			pendingProviderIds.Remove(source);
		RebuildProjections();
		RecomputeHasChanges();
		selection.Clear();
	}

	public bool CanReleaseToAvailable(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return false;
		var ids = keys
			.Select(key => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (uint?)id : null)
			.ToArray();
		if (ids.Any(id => id is null)) return false;
		var values = ids.Select(id => id!.Value).ToArray();
		var sourceIds = Providers.Select(provider => provider.ItemId).ToHashSet();
		var customSourceIds = Providers.Where(provider => provider.IsCustomModel)
			.Select(provider => provider.ItemId).ToHashSet();
		var selectedSources = values.Where(sourceIds.Contains)
			.Where(id => !customSourceIds.Contains(id))
			.Distinct().ToArray();
		if (selectedSources.Length > 0)
			return true;
		if (values.Any(customSourceIds.Contains))
			return false;
		return values.All(id => Mappings.Any(mapping => mapping.Target.ItemId == id));
	}

	public void ReleaseToAvailable(IReadOnlyList<string> keys)
	{
		if (!CanReleaseToAvailable(keys)) return;
		var ids = keys.Select(key => uint.Parse(key, CultureInfo.InvariantCulture)).ToHashSet();
		var sourceIds = Providers.Select(provider => provider.ItemId).ToHashSet();
		var customSourceIds = Providers.Where(provider => provider.IsCustomModel)
			.Select(provider => provider.ItemId)
			.ToHashSet();
		var selectedSources = ids.Where(sourceIds.Contains)
			.Where(id => !customSourceIds.Contains(id))
			.ToArray();
		var releasedIds = selectedSources.Length > 0
			? selectedSources.ToHashSet()
			: ids;
		pieceOverrides = pieceOverrides.Where(item => !releasedIds.Contains(item.OwnerItemId)).ToArray();
		targetModes = targetModes.Where(item => !releasedIds.Contains(item.TargetItemId)).ToArray();
		if (selectedSources.Length > 0)
		{
			var removed = Mappings.Where(mapping => selectedSources.Contains(mapping.Source.ItemId)).ToArray();
			foreach (var mapping in removed) Mappings.Remove(mapping);
			foreach (var sourceId in selectedSources) pendingProviderIds.Remove(sourceId);
			if (selectedProviderId is { } detailSource && selectedSources.Contains(detailSource)) CloseProvider();
		}
		else
		{
			var removed = Mappings.Where(mapping => ids.Contains(mapping.Target.ItemId)).ToArray();
			foreach (var mapping in removed) Mappings.Remove(mapping);
			if (selectedProviderId is { } currentSource
				&& !pendingProviderIds.Contains(currentSource)
				&& !Mappings.Any(mapping => mapping.Source.ItemId == currentSource))
				CloseProvider();
		}
		RebuildProjections();
		RecomputeHasChanges();
		selection.Clear();
	}

	private void RebuildProjections()
	{
		var sourceIds = Mappings.Select(mapping => mapping.Source.ItemId)
			.Concat(pendingProviderIds)
			.Concat(CustomModels.Select(model => SyntheticModelItemId(model.Key)))
			.ToHashSet();
		var targetIds = Mappings.Select(mapping => mapping.Target.ItemId).ToHashSet();
		var managedIds = sourceIds.Concat(targetIds).ToHashSet();

		Providers.ReplaceWith(sourceIds
			.Where(Choices.ContainsKey)
			.Select(id => Choices[id])
			.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase),
			ListTransitionKind.Replace);
		AvailableArmors.ReplaceWith(Choices.Values
			.Where(choice => !managedIds.Contains(choice.ItemId))
			.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase),
			ListTransitionKind.Replace);

		var detailItems = new List<RuntimeArmorChoiceViewModel>();
		if (selectedProviderId is { } selected && Choices.TryGetValue(selected, out var provider))
		{
			foreach (var target in Mappings
				.Where(mapping => mapping.Source.ItemId == selected)
				.Select(mapping => mapping.Target)
				.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase))
			{
				target.SetRolePresentation("映射目标", null);
				detailItems.Add(target);
			}
		}
		ProviderDetailItems.ReplaceWith(detailItems, ListTransitionKind.Replace);
		foreach (var item in AvailableArmors) item.SetRolePresentation(null, null);
		foreach (var item in Providers)
			item.SetRolePresentation(
				item.IsCustomModel ? "自定义视觉来源" : "视觉来源",
				Mappings.Count(mapping => mapping.Source.ItemId == item.ItemId));

		OnPropertyChanged(nameof(ProvidersSummary));
		OnPropertyChanged(nameof(AvailableSummary));
		OnPropertyChanged(nameof(ProviderDetailTitle));
		OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
		OnPropertyChanged(nameof(ProviderDetailSummary));
		OnPropertyChanged(nameof(HasMappings));
	}

	private bool FilterProvider(object item)
		=> item is RuntimeArmorChoiceViewModel choice && MatchesSearch(choice, providersSearchText);

	private bool FilterAvailableArmor(object item)
	{
		if (item is not RuntimeArmorChoiceViewModel choice || !MatchesSearch(choice, availableSearchText))
			return false;
		return availableArmorUnitFilter switch
		{
			AvailableArmorUnitFilter.HasEffectiveUnitOverride => choice.HasEffectiveUnitOverride == true,
			AvailableArmorUnitFilter.NoEffectiveUnitOverride => choice.HasEffectiveUnitOverride == false,
			_ => true,
		};
	}

	private bool FilterProviderDetailItem(object item)
		=> item is RuntimeArmorChoiceViewModel choice && MatchesSearch(choice, providerDetailSearchText);

	private static bool MatchesSearch(RuntimeArmorChoiceViewModel choice, string query)
	{
		var term = query.Trim();
		return term.Length == 0
			|| choice.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
			|| choice.ArchiveId.Contains(term, StringComparison.OrdinalIgnoreCase)
			|| choice.ItemIdText.Contains(term, StringComparison.OrdinalIgnoreCase);
	}

	private sealed record ArmorDisplayFacts(string? ImagePath, bool HasEffectiveUnitOverride);

	private HashSet<(uint Target, uint Source)> GetMappingKeys()
		=> Mappings.Select(mapping => (mapping.Target.ItemId, mapping.Source.ItemId)).ToHashSet();

	private void RecomputeHasChanges()
	{
		HasChanges = modelDefinitionsDirty
			|| enabled != baselineEnabled
			|| !GetMappingKeys().SetEquals(baselineMappings);
		if (HasChanges && hasLoadedCatalog)
			ScheduleWorkspaceSave();
	}

	private void ScheduleWorkspaceSave()
	{
		workspaceSaveCancellation?.Cancel();
		workspaceSaveCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		workspaceSaveCancellation = cancellation;
		_ = SaveWorkspaceAfterDelayAsync(cancellation);
	}

	private async Task SaveWorkspaceAfterDelayAsync(CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(400, cancellation.Token).ConfigureAwait(false);
			var document = BuildWorkspaceDocument();
			await service.SaveWorkspaceAsync(document, cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			LogService.Warn($"自动保存护甲注册工作区失败：{exception.Message}");
		}
		finally
		{
			if (ReferenceEquals(workspaceSaveCancellation, cancellation))
				workspaceSaveCancellation = null;
			cancellation.Dispose();
		}
	}

	private RuntimeArmorRegistryDocument BuildWorkspaceDocument()
	{
		var mappings = Mappings.Select(row => row.Source.IsCustomModel
				? new RuntimeArmorRegistryMapping(row.Target.ItemId, null, row.Source.ModelKey)
				: new RuntimeArmorRegistryMapping(row.Target.ItemId, row.Source.ItemId))
			.ToArray();
		return new RuntimeArmorRegistryDocument(
			workspaceRevision,
			Enabled,
			mappings,
			CustomModels.Select(model => model.ToDomain()).ToArray(),
			pieceOverrides,
			targetModes);
	}

	private void MarkComponentEditorDirty()
	{
		if (componentEditorOwnerId is not { } owner) return;
		if (Choices.TryGetValue(owner, out var ownerChoice) && ownerChoice.IsCustomModel
			&& ownerChoice.ModelKey is { } modelKey)
		{
			var customModel = CustomModels.FirstOrDefault(model =>
				string.Equals(model.Key, modelKey, StringComparison.Ordinal));
			customModel?.ReplacePieces(ComponentEditorPieces.Select(piece => piece.ToDomain()));
			modelDefinitionsDirty = true;
			RecomputeHasChanges();
			return;
		}
		var overrides = pieceOverrides
			.Where(item => item.OwnerItemId != owner)
			.ToList();
		foreach (var piece in ComponentEditorPieces)
		{
			overrides.Add(new RuntimeArmorRegistryPieceOverride(
				owner, piece.Body, piece.Unit, piece.Slot, piece.PieceType, piece.Enabled));
		}
		pieceOverrides = overrides;
		var modes = targetModes.Where(item => item.TargetItemId != owner).ToList();
		if (componentEditorIsTarget)
			modes.Add(new RuntimeArmorRegistryTargetMode(owner, componentEditorFollowSource));
		targetModes = modes;
		modelDefinitionsDirty = true;
		RecomputeHasChanges();
	}

	private void AddComponentPiece() => AddComponentRow();

	private void AddComponentRow()
	{
		if (!IsComponentEditorCustomModel || !ComponentEditorEditable) return;
		var pieceType = ComponentEditorPieces
			.Where(piece => piece.Slot == NewComponentSlot)
			.Select(piece => piece.PieceType)
			.DefaultIfEmpty(0u)
			.Max() + 1;
		var seed = ComponentEditorPieces.Count + 1;
		ComponentEditorPieces.Add(CreateEditorPiece(
			new RuntimeArmorRegistryModelPiece(0, NewGeneratedUnit(seed), NewComponentSlot, pieceType, 0, true, false)));
		ComponentEditorPieces.Add(CreateEditorPiece(
			new RuntimeArmorRegistryModelPiece(1, NewGeneratedUnit(seed + 1), NewComponentSlot, pieceType, 0, true, false)));
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private void RemoveComponentPiece(RuntimeArmorModelPieceEditorViewModel piece)
	{
		if (!IsComponentEditorCustomModel || !piece.CanRemove) return;
		if (MessageBox.Show(
				"删除 Unit 可能破坏已经准备好的 Patch 包。是否继续？",
				"确认删除 Unit",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning) != MessageBoxResult.Yes)
			return;
		ComponentEditorPieces.Remove(piece);
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private void RebuildComponentEditorGroups()
	{
		ComponentEditorGroups.Clear();
		ComponentEditorRows.Clear();
		foreach (var slotGroup in ComponentEditorPieces
			.GroupBy(piece => piece.Slot)
			.OrderBy(group => SlotOrder(group.Key)))
		{
			var rows = new List<RuntimeArmorComponentRowViewModel>();
			foreach (var typeGroup in slotGroup
				.GroupBy(piece => piece.PieceType)
				.OrderBy(group => group.Key))
			{
				var pieces = typeGroup.ToArray();
				var any = pieces.FirstOrDefault(piece => piece.Body == 3);
				var stocky = pieces.FirstOrDefault(piece => piece.Body == 0);
				var slim = pieces.FirstOrDefault(piece => piece.Body == 1);
				rows.Add(new RuntimeArmorComponentRowViewModel(
					typeGroup.Key,
					stocky,
					slim,
					any,
					() => MergeComponentRowToAny(slotGroup.Key, typeGroup.Key),
					() => SplitComponentRow(slotGroup.Key, typeGroup.Key),
						() => AddMissingComponent(slotGroup.Key, typeGroup.Key, 0),
						() => AddMissingComponent(slotGroup.Key, typeGroup.Key, 1),
						IsComponentEditorCustomModel));
			}
			ComponentEditorGroups.Add(new RuntimeArmorComponentGroupViewModel(
				slotGroup.Key,
				slotGroup.First().SlotLabel,
				rows));
			foreach (var row in rows)
				ComponentEditorRows.Add(row);
		}
	}

	private void MergeComponentRowToAny(uint slot, uint pieceType)
	{
		if (MessageBox.Show(
				"合并为 Any 可能改变现有 Unit 的身形注册，并影响已经准备好的 Patch。是否继续？",
				"确认合并组件",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning) != MessageBoxResult.Yes)
			return;
		var pieces = ComponentEditorPieces.Where(piece => piece.Slot == slot && piece.PieceType == pieceType).ToArray();
		var source = pieces.FirstOrDefault(piece => piece.Body == 0)
			?? pieces.FirstOrDefault(piece => piece.Body == 1)
			?? pieces.FirstOrDefault(piece => piece.Body == 3);
		if (source is null) return;
		var any = pieces.FirstOrDefault(piece => piece.Body == 3);
		if (any is null)
			source.SetBody(3);
		foreach (var piece in pieces.Where(piece => piece.Body != 3))
			piece.Enabled = false;
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private void SplitComponentRow(uint slot, uint pieceType)
	{
		var any = ComponentEditorPieces.FirstOrDefault(piece =>
			piece.Slot == slot && piece.PieceType == pieceType && piece.Body == 3);
		if (any is null) return;
		any.Enabled = false;
		if (!ComponentEditorPieces.Any(piece => piece.Slot == slot && piece.PieceType == pieceType && piece.Body == 0))
			ComponentEditorPieces.Add(CreateEditorPiece(
				new RuntimeArmorRegistryModelPiece(0, NewGeneratedUnit(), slot, pieceType, any.Tone, true, false)));
		if (!ComponentEditorPieces.Any(piece => piece.Slot == slot && piece.PieceType == pieceType && piece.Body == 1))
			ComponentEditorPieces.Add(CreateEditorPiece(
				new RuntimeArmorRegistryModelPiece(1, NewGeneratedUnit(), slot, pieceType, any.Tone, true, false)));
		foreach (var piece in ComponentEditorPieces.Where(piece =>
			piece.Slot == slot && piece.PieceType == pieceType && piece.Body is 0 or 1))
			piece.Enabled = true;
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private void AddMissingComponent(uint slot, uint pieceType, uint body)
	{
		if (ComponentEditorPieces.Any(piece => piece.Slot == slot && piece.PieceType == pieceType && piece.Body == body))
			return;
		var existing = ComponentEditorPieces.FirstOrDefault(piece => piece.Slot == slot && piece.PieceType == pieceType);
		if (existing is null) return;
		ComponentEditorPieces.Add(CreateEditorPiece(
			new RuntimeArmorRegistryModelPiece(body, NewGeneratedUnit(), slot, pieceType, existing.Tone, true, false)));
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private static string NewGeneratedUnit(int salt = 0)
		=> ((ulong)DateTime.UtcNow.Ticks << 12 | (uint)Random.Shared.Next(1, 4095) ^ (uint)salt)
			.ToString("x16", CultureInfo.InvariantCulture);

	private RuntimeArmorModelPieceEditorViewModel CreateEditorPiece(RuntimeArmorRegistryModelPiece piece)
		=> new(piece, MarkComponentEditorDirty, IsComponentEditorCustomModel && !piece.IsDefaultBase, RemoveComponentPiece);

	private static int SlotOrder(uint slot) => slot switch
	{
		1 => 0, 2 => 1, 6 => 2, 7 => 3, 3 => 4, 4 => 5, 5 => 6, _ => 100 + (int)slot
	};

	private void MarkModelDefinitionsDirty()
	{
		modelDefinitionsDirty = true;
		RecomputeHasChanges();
	}

	private void AddCustomModel()
	{
		var used = CustomModels.Select(model => model.Key)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var index = 1;
		while (used.Contains($"custom-{index}")) index++;
		var key = $"custom-{index}";
		var model = new RuntimeArmorRegistryModel(
			key,
			null,
			CreateDefaultCustomModelPieces(),
			key);
		var editor = new RuntimeArmorModelEditorViewModel(model, MarkModelDefinitionsDirty);
		CustomModels.Add(editor);
		Choices[SyntheticModelItemId(key)] = RuntimeArmorChoiceViewModel.CustomModel(
			SyntheticModelItemId(key), key, key);
		RebuildProjections();
		MarkModelDefinitionsDirty();
		OnPropertyChanged(nameof(HasCustomModels));
		OnPropertyChanged(nameof(CustomModelCount));
	}

	private void RemoveCustomModel(RuntimeArmorModelEditorViewModel? model)
	{
		if (model is null || !CustomModels.Contains(model)) return;
		if (modelMappings.Any(mapping => string.Equals(mapping.ModelKey, model.Key, StringComparison.Ordinal)))
		{
			notifications.Show(
				$"自定义素体 {model.Key} 仍被映射使用，不能删除。",
				NotificationLevel.Warning,
				TimeSpan.FromSeconds(6),
				"护甲注册");
			return;
		}
		CustomModels.Remove(model);
		var syntheticId = SyntheticModelItemId(model.Key);
		Choices.Remove(syntheticId);
		foreach (var mapping in Mappings.Where(mapping => mapping.Source.ItemId == syntheticId).ToArray())
			Mappings.Remove(mapping);
		MarkModelDefinitionsDirty();
		OnPropertyChanged(nameof(HasCustomModels));
		OnPropertyChanged(nameof(CustomModelCount));
	}

	private static IReadOnlyList<RuntimeArmorRegistryModelPiece> CreateDefaultCustomModelPieces()
	{
		var slots = new[] { 2u, 6u, 7u, 3u, 4u, 5u };
		var pieces = new List<RuntimeArmorRegistryModelPiece>(slots.Length * 2);
		var seed = (ulong)DateTime.UtcNow.Ticks & 0xfffffffUL;
		var counter = 0UL;
		foreach (var slot in slots)
		{
			foreach (var body in new[] { 0u, 1u })
			{
				var unit = (seed << 20 | counter++).ToString("x16", CultureInfo.InvariantCulture);
				pieces.Add(new RuntimeArmorRegistryModelPiece(
					body, unit, slot, 0, 0, true, true));
			}
		}
		return pieces;
	}

	private static uint SyntheticModelItemId(string key)
	{
		unchecked
		{
			uint hash = 2166136261;
			foreach (var character in key)
				hash = (hash ^ character) * 16777619;
			return 0x80000000u | (hash & 0x7fffffffu);
		}
	}

	private async Task ApplyAsync()
	{
		if (IsBusy || !HasChanges) return;
		IsBusy = true;
		try
		{
			var sourceIds = Mappings.Select(mapping => mapping.Source.ItemId).ToHashSet();
			var targetIds = Mappings.Select(mapping => mapping.Target.ItemId).ToHashSet();
			if (sourceIds.Overlaps(targetIds))
				throw new InvalidOperationException("当前协议包含“视觉来源同时作为映射目标”的链式关系。请先移除冲突映射；GUI 不会创建或保存此类关系。");

			var desired = Mappings
				.Select(row => row.Source.IsCustomModel
					? new RuntimeArmorRegistryMapping(row.Target.ItemId, null, row.Source.ModelKey)
					: new RuntimeArmorRegistryMapping(row.Target.ItemId, row.Source.ItemId))
				.ToArray();
			var result = await service.ApplyAsync(
				fingerprint,
				Enabled,
				desired,
				CustomModels.Select(model => model.ToDomain()).ToArray(),
				pieceOverrides,
				targetModes);
			modelDefinitions = result.Document.ModelDefinitions;
			pieceOverrides = result.Document.PieceOverrideDefinitions;
			targetModes = result.Document.TargetModeDefinitions;
			workspaceRevision = result.Document.Revision;
			modelDefinitionsDirty = false;
			OnPropertyChanged(nameof(ModelDefinitions));
			OnPropertyChanged(nameof(CustomModelCount));
			fingerprint = result.Fingerprint;
			RevisionText = $"revision {result.Document.Revision} · {desired.Length} 条映射";
			baselineEnabled = Enabled;
			baselineMappings = GetMappingKeys();
			HasChanges = false;
			pendingProviderIds.Clear();
			if (selectedProviderId is { } selected && !sourceIds.Contains(selected))
			{
				selectedProviderId = null;
				OnPropertyChanged(nameof(IsProviderDetailOpen));
			}
			RebuildProjections();

			var snapshot = await WaitForRuntimeAsync(result.Document.Revision);
			var gameRunning = IsGameRunning();
			if (!gameRunning)
			{
				StatusTitle = Enabled ? "正在部署运行时" : "正在移除运行时";
				StatusDetail = Enabled
					? "协议已保存，正在把管理器托管的运行时组件部署到活动配置。"
					: "停用协议已发布，正在从活动配置移除运行时组件。";
				var deployment = await deploymentCoordinator.FlushAsync();
				if (deployment.Stage != ProfileDeploymentStage.Completed)
					throw new InvalidOperationException(deployment.Message ?? "活动配置部署失败。");
			}
			var runtimeReportedApply = RuntimeReportedRevision(snapshot, result.Document.Revision);
			if (runtimeReportedApply)
			{
				StatusTitle = "游戏已同步";
				StatusDetail = $"运行时状态已报告 revision {result.Document.Revision}，当前为{GetRuntimeMode(snapshot)}。";
				notifications.Show(
					$"护甲外观注册已在线应用：revision {result.Document.Revision}。",
					NotificationLevel.Info,
					TimeSpan.FromSeconds(6),
					"护甲注册",
					"armor-registry-apply");
			}
			else
			{
				UpdateStatus(snapshot);
				if (snapshot.RuntimeProcessRunning)
				{
					notifications.Show(
						"协议已保存，但游戏运行时尚未确认；请稍后刷新状态。",
						NotificationLevel.Warning,
						TimeSpan.FromSeconds(8),
						"护甲注册",
						"armor-registry-apply");
				}
				else if (gameRunning)
				{
					StatusTitle = "协议已保存";
					StatusDetail = $"游戏正在运行，但暂时没有 revision {result.Document.Revision} 的运行时回执。状态文件或进程探测不可用不代表映射未生效，请以游戏内效果为准，稍后可刷新确认。";
					notifications.Show(
						"护甲映射协议已更新；未取得运行时回执，当前状态无法确认，但这不代表映射未生效。",
						NotificationLevel.Info,
						TimeSpan.FromSeconds(8),
						"护甲注册",
						"armor-registry-apply");
				}
				else
				{
					notifications.Show("协议已保存，将在下次启动游戏时应用。", NotificationLevel.Info, TimeSpan.FromSeconds(6), "护甲注册", "armor-registry-apply");
				}
			}
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException)
		{
			notifications.Show($"应用护甲注册失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(10), "护甲注册");
			StatusTitle = "应用失败";
			StatusDetail = exception.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task<RuntimeArmorRegistrySnapshot> WaitForRuntimeAsync(int revision)
	{
		var snapshot = await service.ReadSnapshotAsync();
		if (RuntimeReportedRevision(snapshot, revision)) return snapshot;
		for (var attempt = 0; attempt < 8; attempt++)
		{
			await Task.Delay(250);
			snapshot = await service.ReadSnapshotAsync();
			if (RuntimeReportedRevision(snapshot, revision)
				|| snapshot.Document.Revision != revision)
				break;
		}
		return snapshot;
	}

	private static bool RuntimeReportedRevision(RuntimeArmorRegistrySnapshot snapshot, int revision)
		=> string.Equals(snapshot.RuntimeStatus.GetValueOrDefault("state"), "ready", StringComparison.Ordinal)
			&& int.TryParse(
				snapshot.RuntimeStatus.GetValueOrDefault("revision"),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var runtimeRevision)
			&& runtimeRevision == revision;

	private static string GetRuntimeMode(RuntimeArmorRegistrySnapshot snapshot)
		=> snapshot.RuntimeStatus.GetValueOrDefault("deployment_mode") switch
		{
			"shared_loader" => "Bingus 插件模式",
			"direct" => "管理器独占模式",
			_ => "运行时模式未知",
		};

	private void UpdateStatus(RuntimeArmorRegistrySnapshot snapshot)
	{
		if (snapshot.RuntimeAcknowledged)
		{
			var mode = snapshot.RuntimeStatus.GetValueOrDefault("deployment_mode") switch
			{
				"shared_loader" => "Bingus 插件模式",
				"direct" => "管理器独占模式",
				_ => "运行时模式未知",
			};
			StatusTitle = "游戏已同步";
			StatusDetail = $"运行中的游戏已确认 revision {snapshot.Document.Revision}，当前为{mode}。";
		}
		else if (snapshot.RuntimeProcessRunning)
		{
			var runtimeRevision = snapshot.RuntimeStatus.GetValueOrDefault("revision", "未知");
			var state = snapshot.RuntimeStatus.GetValueOrDefault("state", "未知");
			StatusTitle = "等待游戏同步";
			StatusDetail = $"运行时状态 {state}，当前 revision {runtimeRevision}，目标 revision {snapshot.Document.Revision}。";
		}
		else
		{
			StatusTitle = snapshot.ProtocolExists ? "等待游戏启动" : "尚未创建协议";
			StatusDetail = snapshot.ProtocolExists
				? snapshot.Document.Enabled
					? "配置及托管运行时已部署；启动游戏后会自动定位并应用。"
					: "映射已停用，托管运行时会在部署时从游戏 Data 中移除。"
				: "拖入视觉来源并添加映射后将创建外置协议。";
		}
	}

	private void RaiseCommandStates()
	{
		RefreshCommand.RaiseCanExecuteChanged();
		ApplyCommand.RaiseCanExecuteChanged();
	}

	private void OpenRegistryFolder()
	{
		Directory.CreateDirectory(service.RegistryRoot);
		Process.Start(new ProcessStartInfo(service.RegistryRoot) { UseShellExecute = true });
	}

	private static bool IsGameRunning()
	{
		try
		{
			return Process.GetProcessesByName("helldivers2").Any(process =>
			{
				using (process) return !process.HasExited;
			});
		}
		catch (InvalidOperationException)
		{
			return true;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return true;
		}
	}
}

public sealed class RuntimeArmorChoiceViewModel : BaseViewModel, IModListSelectable
{
	private bool isSelected;
	private ModListRowPresentation rowPresentation = ModListRowPresentation.Empty;

	public RuntimeArmorChoiceViewModel(
		uint itemId,
		string archiveId,
		string displayName,
		string? imagePath = null,
		bool? hasEffectiveUnitOverride = null,
		bool isUnknown = false,
		bool isCustomModel = false,
		string? modelKey = null)
	{
		ItemId = itemId;
		ArchiveId = archiveId;
		Name = displayName;
		ImagePath = imagePath;
		HasEffectiveUnitOverride = hasEffectiveUnitOverride;
		IsUnknown = isUnknown;
		IsCustomModel = isCustomModel;
		ModelKey = modelKey;
	}

	public uint ItemId { get; }
	public string ArchiveId { get; }
	public string Name { get; }
	public string? ImagePath { get; }
	public bool? HasEffectiveUnitOverride { get; }
	public bool IsUnknown { get; }
	public bool IsCustomModel { get; }
	public string? ModelKey { get; }
	public string ListKey => SelectionKey;
	public string SelectionKey => ItemId.ToString(CultureInfo.InvariantCulture);
	public bool IsDecoration => false;
	public string Display => $"{Name} · {ArchiveId}";
	public string ItemIdText => ItemId.ToString(CultureInfo.InvariantCulture);
	public bool IsSelected { get => isSelected; set => SetField(ref isSelected, value); }
	public ModListRowPresentation RowPresentation { get => rowPresentation; private set => SetField(ref rowPresentation, value); }

	public void SetRolePresentation(string? role, int? count)
	{
		var summary = role switch
		{
			"视觉来源" when count is { } value => value == 0 ? "视觉来源 · 尚无映射目标" : $"视觉来源 · {value} 个目标",
			"自定义视觉来源" when count is { } value => value == 0 ? "自定义视觉来源 · 尚无映射目标" : $"自定义视觉来源 · {value} 个目标",
			"映射目标" => "映射目标",
			_ => null,
		};
		RowPresentation = new ModListRowPresentation(TypeLabel: role, TypeTone: ModListRowTone.Accent, Summary: summary);
	}

	public static RuntimeArmorChoiceViewModel Unknown(uint itemId)
		=> new(itemId, itemId.ToString(CultureInfo.InvariantCulture), $"未知护甲 {itemId}", isUnknown: true);

	public static RuntimeArmorChoiceViewModel CustomModel(uint itemId, string name, string key)
		=> new(itemId, key, name, isCustomModel: true, modelKey: key);
}

public sealed record RuntimeArmorMappingRowViewModel(
	RuntimeArmorChoiceViewModel Target,
	RuntimeArmorChoiceViewModel Source,
	string? ModelKey = null);

public sealed class RuntimeArmorModelEditorViewModel : BaseViewModel
{
	private readonly Action changed;
	private bool isExpanded = true;
	private int nextPieceIndex;

	public RuntimeArmorModelEditorViewModel(RuntimeArmorRegistryModel model, Action changed)
	{
		Key = model.Key;
		Name = model.Name ?? model.Key;
		SourceItemId = model.SourceItemId;
		this.changed = changed;
		var baseSlots = new HashSet<uint> { 2, 3, 4, 5, 6, 7 };
		Pieces = new ObservableCollection<RuntimeArmorModelPieceEditorViewModel>(
			(model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
			.Select(piece => new RuntimeArmorModelPieceEditorViewModel(
				piece with
				{
					IsDefaultBase = piece.IsDefaultBase
						|| (SourceItemId is null && piece.PieceType == 0 && baseSlots.Contains(piece.Slot))
				},
				changed,
				SourceItemId is null && !piece.IsDefaultBase && !(piece.PieceType == 0 && baseSlots.Contains(piece.Slot)),
				RemovePiece)));
		nextPieceIndex = Pieces.Count;
		AddPieceCommand = new RelayCommand(_ => AddPiece(), _ => SourceItemId is null);
	}

	public string Key { get; }
	public string Name { get; private set; }
	public uint? SourceItemId { get; }
	public ObservableCollection<RuntimeArmorModelPieceEditorViewModel> Pieces { get; }
	public RelayCommand AddPieceCommand { get; }
	public bool IsExpanded
	{
		get => isExpanded;
		set => SetField(ref isExpanded, value);
	}

	public RuntimeArmorRegistryModel ToDomain()
		=> new(Key, SourceItemId, Pieces.Select(piece => piece.ToDomain()).ToArray(), Name);

	public void Rename(string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return;
		Name = name.Trim();
		changed();
		OnPropertyChanged(nameof(Name));
	}

	public void ReplacePieces(IEnumerable<RuntimeArmorRegistryModelPiece> pieces)
	{
		Pieces.Clear();
		foreach (var piece in pieces)
		{
			Pieces.Add(new RuntimeArmorModelPieceEditorViewModel(
				piece, changed, SourceItemId is null && !piece.IsDefaultBase, RemovePiece));
		}
		nextPieceIndex = Pieces.Count;
	}

	private void AddPiece()
	{
		var unit = ((ulong)DateTime.UtcNow.Ticks << 12 | (uint)nextPieceIndex++)
			.ToString("x16", CultureInfo.InvariantCulture);
		Pieces.Add(new RuntimeArmorModelPieceEditorViewModel(
			new RuntimeArmorRegistryModelPiece(3, unit, 2, 100 + (uint)nextPieceIndex, 0, true),
			changed, true, RemovePiece));
		changed();
	}

	public void RemovePiece(RuntimeArmorModelPieceEditorViewModel? piece)
	{
		if (piece is null || !piece.CanRemove || !Pieces.Remove(piece)) return;
		changed();
	}
}

public sealed class RuntimeArmorModelPieceEditorViewModel : BaseViewModel
{
	private readonly Action changed;
	private bool enabled;
	private readonly bool canRemove;
	private readonly Action<RuntimeArmorModelPieceEditorViewModel>? remove;

	public RuntimeArmorModelPieceEditorViewModel(
		RuntimeArmorRegistryModelPiece piece,
		Action changed,
		bool allowRemove = false,
		Action<RuntimeArmorModelPieceEditorViewModel>? remove = null)
	{
		Body = piece.Body;
		Unit = piece.Unit;
		Slot = piece.Slot;
		PieceType = piece.PieceType;
		Tone = piece.Tone;
		IsDefaultBase = piece.IsDefaultBase;
		enabled = piece.Enabled;
		canRemove = allowRemove;
		this.remove = remove;
		RemoveCommand = new RelayCommand(_ => remove?.Invoke(this), _ => CanRemove);
		this.changed = changed;
	}

	public uint Body { get; private set; }
	public string Unit { get; }
	public uint Slot { get; }
	public uint PieceType { get; }
	public byte Tone { get; }
	public string BodyLabel => Body switch
	{
		3 => "Any",
		0 => "Stocky",
		1 => "Slim",
		_ => Body.ToString(CultureInfo.InvariantCulture),
	};
	public string SlotLabel => Slot switch
	{
		1 => "头盔",
		2 => "胸部",
		3 => "胯部",
		4 => "左腿",
		5 => "右腿",
		6 => "左臂",
		7 => "右臂",
		_ => $"槽位 {Slot}",
	};
	public bool Enabled
	{
		get => enabled;
		set
		{
			if (!SetField(ref enabled, value)) return;
			changed();
		}
	}
	public bool CanRemove => canRemove;
	public RelayCommand RemoveCommand { get; }

	public RuntimeArmorRegistryModelPiece ToDomain()
		=> new(Body, Unit, Slot, PieceType, Tone, Enabled, IsDefaultBase);

	public bool IsDefaultBase { get; }
	public void SetBody(uint body)
	{
		if (Body == body) return;
		Body = body;
		OnPropertyChanged(nameof(BodyLabel));
		changed();
	}
}

public sealed class RuntimeArmorComponentGroupViewModel
{
	public RuntimeArmorComponentGroupViewModel(
		uint slot,
		string title,
		IReadOnlyList<RuntimeArmorComponentRowViewModel> rows)
	{
		Slot = slot;
		Title = title;
		Rows = rows;
	}

	public uint Slot { get; }
	public string Title { get; }
	public IReadOnlyList<RuntimeArmorComponentRowViewModel> Rows { get; }
}

public sealed class RuntimeArmorComponentRowViewModel
{
	public RuntimeArmorComponentRowViewModel(
		uint pieceType,
		RuntimeArmorModelPieceEditorViewModel? stocky,
		RuntimeArmorModelPieceEditorViewModel? slim,
		RuntimeArmorModelPieceEditorViewModel? any,
		Action mergeAny,
		Action split,
		Action addStocky,
		Action addSlim,
		bool canEditStructure)
	{
		PieceType = pieceType;
		Stocky = stocky;
		Slim = slim;
		Any = any;
		MergeAnyCommand = new RelayCommand(mergeAny);
		SplitCommand = new RelayCommand(split);
		AddMissingStockyCommand = new RelayCommand(_ => addStocky());
		AddMissingSlimCommand = new RelayCommand(_ => addSlim());
		CanEditStructure = canEditStructure;
	}

	public uint PieceType { get; }
	public RuntimeArmorModelPieceEditorViewModel? Stocky { get; }
	public RuntimeArmorModelPieceEditorViewModel? Slim { get; }
	public RuntimeArmorModelPieceEditorViewModel? Any { get; }
	public bool IsAny => Any is not null;
	public string StructureActionLabel => IsAny ? "拆分为双身形" : "合并为 Any";
	public RelayCommand MergeAnyCommand { get; }
	public RelayCommand SplitCommand { get; }
	public RelayCommand AddMissingStockyCommand { get; }
	public RelayCommand AddMissingSlimCommand { get; }
	public bool CanEditStructure { get; }
	public bool HasMissingStocky => !IsAny && Stocky is null;
	public bool HasMissingSlim => !IsAny && Slim is null;
	public RelayCommand StructureActionCommand => IsAny ? SplitCommand : MergeAnyCommand;
}

public sealed record ArmorComponentSlotOption(uint Slot, string Name);
