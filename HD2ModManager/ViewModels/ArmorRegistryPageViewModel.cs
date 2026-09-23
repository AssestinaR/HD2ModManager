using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
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
	private readonly HashSet<uint> pendingProviderIds = [];
	private IReadOnlyList<RuntimeArmorCatalogEntry> cachedCatalog = [];
	private IReadOnlyDictionary<string, ArmorDisplayFacts> cachedArmorDisplayFacts = new Dictionary<string, ArmorDisplayFacts>(StringComparer.OrdinalIgnoreCase);
	private bool hasLoadedCatalog;

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
	}

	public BulkObservableCollection<RuntimeArmorChoiceViewModel> AvailableArmors { get; } = [];
	public BulkObservableCollection<RuntimeArmorChoiceViewModel> Providers { get; } = [];
	public BulkObservableCollection<RuntimeArmorChoiceViewModel> ProviderDetailItems { get; } = [];
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
	public bool HasMappings => Mappings.Count != 0;
	public string StatusTitle { get => statusTitle; private set => SetField(ref statusTitle, value); }
	public string StatusDetail { get => statusDetail; private set => SetField(ref statusDetail, value); }
	public string RevisionText { get => revisionText; private set => SetField(ref revisionText, value); }
	public string RegistryPath => service.RegistryRoot;
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
			if (!Choices.ContainsKey(mapping.TargetItemId))
				Choices[mapping.TargetItemId] = RuntimeArmorChoiceViewModel.Unknown(mapping.TargetItemId);
			if (!Choices.ContainsKey(mapping.SourceItemId))
				Choices[mapping.SourceItemId] = RuntimeArmorChoiceViewModel.Unknown(mapping.SourceItemId);
		}

		Mappings.Clear();
		foreach (var mapping in snapshot.Document.Mappings)
			Mappings.Add(new RuntimeArmorMappingRowViewModel(
				Choices[mapping.TargetItemId],
				Choices[mapping.SourceItemId]));
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
		foreach (var id in ids)
		{
			if (id == source
				|| Mappings.Any(mapping => mapping.Target.ItemId == id)
				|| Providers.Any(provider => provider.ItemId == id))
				continue;
			Mappings.Add(new RuntimeArmorMappingRowViewModel(Choices[id], Choices[source]));
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
		var selectedSources = values.Where(sourceIds.Contains).Distinct().ToArray();
		if (selectedSources.Length > 0)
			return selectedSources.Length == values.Length;
		return values.All(id => Mappings.Any(mapping => mapping.Target.ItemId == id));
	}

	public void ReleaseToAvailable(IReadOnlyList<string> keys)
	{
		if (!CanReleaseToAvailable(keys)) return;
		var ids = keys.Select(key => uint.Parse(key, CultureInfo.InvariantCulture)).ToHashSet();
		var sourceIds = Providers.Select(provider => provider.ItemId).ToHashSet();
		var selectedSources = ids.Where(sourceIds.Contains).ToArray();
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
		foreach (var item in Providers) item.SetRolePresentation("视觉来源", Mappings.Count(mapping => mapping.Source.ItemId == item.ItemId));

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
		HasChanges = enabled != baselineEnabled || !GetMappingKeys().SetEquals(baselineMappings);
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
				.Select(row => new RuntimeArmorRegistryMapping(row.Target.ItemId, row.Source.ItemId))
				.ToArray();
			var result = await service.ApplyAsync(fingerprint, Enabled, desired);
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
		bool isUnknown = false)
	{
		ItemId = itemId;
		ArchiveId = archiveId;
		Name = displayName;
		ImagePath = imagePath;
		HasEffectiveUnitOverride = hasEffectiveUnitOverride;
		IsUnknown = isUnknown;
	}

	public uint ItemId { get; }
	public string ArchiveId { get; }
	public string Name { get; }
	public string? ImagePath { get; }
	public bool? HasEffectiveUnitOverride { get; }
	public bool IsUnknown { get; }
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
			"映射目标" => "映射目标",
			_ => null,
		};
		RowPresentation = new ModListRowPresentation(TypeLabel: role, TypeTone: ModListRowTone.Accent, Summary: summary);
	}

	public static RuntimeArmorChoiceViewModel Unknown(uint itemId)
		=> new(itemId, itemId.ToString(CultureInfo.InvariantCulture), $"未知护甲 {itemId}", isUnknown: true);
}

public sealed record RuntimeArmorMappingRowViewModel(
	RuntimeArmorChoiceViewModel Target,
	RuntimeArmorChoiceViewModel Source);
