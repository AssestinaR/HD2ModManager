using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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

	private enum AvailableArmorKindFilter
	{
		AutomaticCombination,
		ArmorOnly,
		HelmetOnly,
	}

	private readonly RuntimeArmorRegistryService service;
	private readonly NotificationService notifications;
	private readonly IProfileDeploymentCoordinator deploymentCoordinator;
	private readonly ModLibraryService library;
	private readonly SelectionCoordinator selection;
	private readonly Task? displayFactsReady;
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
	private AvailableArmorKindFilter availableArmorKindFilter;
	private uint? selectedProviderId;
	private IReadOnlyList<uint> selectedProviderMemberIds = [];
	private uint? componentEditorOwnerId;
	private bool componentEditorIsTarget;
	private IReadOnlyList<RuntimeArmorRegistryModelPiece> componentEditorSourcePieces = [];
	private uint? componentEditorSourceOverrideOwner;
	private int componentEditorRequest;
	private readonly HashSet<uint> pendingProviderIds = [];
	private IReadOnlyList<RuntimeArmorCatalogEntry> cachedCatalog = [];
	private IReadOnlyDictionary<string, ArmorDisplayFacts> cachedArmorDisplayFacts = new Dictionary<string, ArmorDisplayFacts>(StringComparer.OrdinalIgnoreCase);
	private IReadOnlyList<RuntimeArmorRegistryModel> modelDefinitions = [];
	private IReadOnlyList<RuntimeArmorRegistryMapping> modelMappings = [];
	private IReadOnlyList<RuntimeArmorRegistryPieceOverride> pieceOverrides = [];
	private IReadOnlyList<RuntimeArmorRegistryTargetMode> targetModes = [];
	private bool componentEditorFollowSource = true;
	private bool hasLoadedCatalog;
	// 首次加载是"单飞"的：启动预热与用户首开共用同一个 Task。
	// 没有它就有两个坑——预热与首开各读一遍；或者预热还在跑时用户点开页面，
	// RefreshAsync 因 IsBusy 直接返回，页面停在空状态。
	private readonly object catalogLoadGate = new();
	private Task? catalogLoadTask;
	private int workspaceRevision;
	private CancellationTokenSource? workspaceSaveCancellation;
	private readonly SemaphoreSlim workspaceSaveGate = new(1, 1);
	private Task workspaceSaveTask = Task.CompletedTask;
	private Exception? workspaceSaveError;
	private static readonly ConcurrentDictionary<string, Task> PendingWorkspaceSaves = new(StringComparer.OrdinalIgnoreCase);

	public ArmorRegistryPageViewModel(
		ModLibraryService library,
		NotificationService notifications,
		IProfileDeploymentCoordinator deploymentCoordinator,
		SelectionCoordinator selection,
		Task? displayFactsReady = null)
	{
		Title = "运行时护甲注册";
		this.library = library ?? throw new ArgumentNullException(nameof(library));
		this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		this.deploymentCoordinator = deploymentCoordinator ?? throw new ArgumentNullException(nameof(deploymentCoordinator));
		this.selection = selection ?? throw new ArgumentNullException(nameof(selection));
		// 取数闸门：显示事实（图标 + 覆盖）只有在"库快照已加载 **且** 启动部署已落定"之后才算得对。
		// 启动预热会在窗口刚 Loaded 时就把本页视图建出来并触发 OnLoaded，
		// 那比上面两件事都早——所以这里必须等，不能靠"预热总是晚于它们"这种时序假设。
		this.displayFactsReady = displayFactsReady;
		this.selection.SelectionChanged += OnGlobalSelectionChanged;
		ProvidersView = CollectionViewSource.GetDefaultView(Providers);
		AvailableArmorsView = CollectionViewSource.GetDefaultView(AvailableArmors);
		ProviderDetailItemsView = CollectionViewSource.GetDefaultView(ProviderDetailItems);
		ProvidersView.Filter = FilterProvider;
		AvailableArmorsView.Filter = FilterAvailableArmor;
		ProviderDetailItemsView.Filter = FilterProviderDetailItem;
		service = CoreServices.CreateRuntimeArmorRegistryService(SettingsService.CreateStoragePaths());
		// 衣柜的权威数据在工作区，和护甲注册工作区同一个目录（见 wardrobe-schema-v2 笔记 §14）。
		Wardrobe = new WardrobeTreeViewModel(
			WardrobeWorkspaceFile.ResolvePath(SettingsService.CreateStoragePaths().ModsDirectory));
		RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy && !HasChanges);
		// 应用按钮**始终可点**：没有改动时点它等于把当前协议重投一次——这在
		// "Steam 校验完整性" 之后是必须的（注册表被还原了，映射得重新应用一次）。
		// 10 秒冷却只用于防止连点产生一串 revision。
		ApplyCommand = new AsyncRelayCommand(
			_ => ApplyAsync(),
			_ => !IsBusy && !applyCooldownActive);
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
	/// <summary>「视觉组件」卡片里的 section 树（无 3D）。与将来的 3D 窗口共用同一份核心树。</summary>
	public WardrobeTreeViewModel Wardrobe { get; }
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

	public string AvailableArmorKindFilterText => availableArmorKindFilter switch
	{
		AvailableArmorKindFilter.ArmorOnly => "类型：仅护甲",
		AvailableArmorKindFilter.HelmetOnly => "类型：仅头盔",
		_ => "类型：自动组合",
	};

	public void CycleAvailableArmorKindFilter()
	{
		availableArmorKindFilter = availableArmorKindFilter switch
		{
			AvailableArmorKindFilter.AutomaticCombination => AvailableArmorKindFilter.ArmorOnly,
			AvailableArmorKindFilter.ArmorOnly => AvailableArmorKindFilter.HelmetOnly,
			_ => AvailableArmorKindFilter.AutomaticCombination,
		};
		OnPropertyChanged(nameof(AvailableArmorKindFilterText));
		RebuildProjections();
		ProvidersView.Refresh();
		ProviderDetailItemsView.Refresh();
		OnPropertyChanged(nameof(AvailableSummary));
		OnPropertyChanged(nameof(ProvidersSummary));
		OnPropertyChanged(nameof(ProviderDetailSummary));
	}

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
		componentEditorRequest++;
		selection.SelectionChanged -= OnGlobalSelectionChanged;
		FlushWorkspaceSave();
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
			OnPropertyChanged(nameof(ComponentEditorEditable));
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

	// 应用成功后有 10 秒冷却，纯粹防连点（避免产生一串 revision）。冷却结束后按钮恢复可点，
	// 与"有没有改动"无关。
	private bool applyCooldownActive;

	private void ApplyCanExecuteChanged() => ApplyCommand.RaiseCanExecuteChanged();

	private async Task RunApplyCooldownAsync()
	{
		applyCooldownActive = true;
		ApplyCanExecuteChanged();
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(10));
		}
		catch (TaskCanceledException)
		{
			// 页面关闭时取消：冷却状态无所谓，下次应用会重新开始。
		}
		applyCooldownActive = false;
		ApplyCanExecuteChanged();
	}

	public bool IsProviderDetailOpen => selectedProviderId is not null;
	public bool IsComponentEditorOpen => componentEditorOwnerId is not null;
	public bool ComponentEditorIsTarget => componentEditorIsTarget;
	public bool IsComponentEditorCustomModel
		=> !componentEditorIsTarget && componentEditorOwnerId is { } id
			&& Choices.TryGetValue(id, out var choice)
			&& choice.IsCustomModel;
	public bool ComponentEditorFollowSource
	{
		get => componentEditorFollowSource;
		set
		{
			if (!SetField(ref componentEditorFollowSource, value)) return;
			if (componentEditorIsTarget)
				PopulateComponentEditor();
			MarkComponentEditorDirty();
			OnPropertyChanged(nameof(ComponentEditorEditable));
			ConfirmAddComponentRowCommand.RaiseCanExecuteChanged();
		}
	}
	public bool ComponentEditorEditable => !IsBusy && (!ComponentEditorIsTarget || !ComponentEditorFollowSource);
	/// <summary>可选部位：直接来自 ArmorStructure，10 个部位一个不少（含披风与双肩）。</summary>
	public IReadOnlyList<ArmorComponentSlotOption> ComponentSlotOptions { get; } =
		ArmorStructure.All
			.Select(part => new ArmorComponentSlotOption(
				ArmorStructure.SlotOf(part) ?? 0,
				ArmorStructure.Display(part)))
			.ToArray();
	private uint newComponentSlot = 2;
	public uint NewComponentSlot
	{
		get => newComponentSlot;
		set => SetField(ref newComponentSlot, value);
	}
	public string ComponentEditorTitle => componentEditorOwnerId is { } id
		? (Choices.TryGetValue(id, out var choice) ? $"←  {choice.Name} · 视觉组件" : $"←  护甲 {id} · 视觉组件")
		: "视觉组件";
	public string ComponentEditorName => componentEditorOwnerId is { } id
		? Choices.TryGetValue(id, out var choice) ? choice.Name : $"护甲 {id}"
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
		? Providers.FirstOrDefault(item => item.MemberItemIds.SequenceEqual(selectedProviderMemberIds))?.Name
			?? (selectedProviderMemberIds.Count > 1
				? string.Join(" + ", selectedProviderMemberIds.Select(memberId =>
					Choices.TryGetValue(memberId, out var member) ? member.Name : $"未知护甲 {memberId}"))
				: Choices.TryGetValue(id, out var choice) ? choice.Name : $"未知护甲 {id}")
		: "视觉映射目标";
	public string ProviderDetailHeaderTitle => $"←  {ProviderDetailTitle}";
	public string AvailableRowActionToolTip => IsProviderDetailOpen ? "注册到该视觉对象" : "转为视觉对象";
	public string ProviderDetailSummary => selectedProviderId is not null
		? providerDetailSearchText.Length == 0
			? $"{Mappings.Count(row => selectedProviderMemberIds.Contains(row.Source.ItemId))} 个映射目标"
			: $"{ProviderDetailItemsView.Cast<object>().Count()} / {Mappings.Count(row => selectedProviderMemberIds.Contains(row.Source.ItemId))} 个映射目标"
		: "选择一个视觉来源";
	private Dictionary<uint, RuntimeArmorChoiceViewModel> Choices { get; set; } = [];

	public AsyncRelayCommand RefreshCommand { get; }
	public AsyncRelayCommand ApplyCommand { get; }
	public RelayCommand OpenRegistryFolderCommand { get; }
	public RelayCommand AddCustomModelCommand { get; }
	public RelayCommand RemoveCustomModelCommand { get; }
	public RelayCommand AddComponentPieceCommand { get; }
	public RelayCommand ConfirmAddComponentRowCommand { get; }

	// 页面首次打开：目录、快照与覆盖事实都还没有，走一次完整加载。
	// 加载是单飞的（见 EnsureCatalogLoadedAsync），所以它与启动预热**共用同一次**。
	public Task InitializeAsync() => hasLoadedCatalog ? RefreshRuntimeStatusAsync() : EnsureCatalogLoadedAsync(isWarmUp: false);

	// 启动预热：在用户还没点开护甲页时，先把目录、快照与最贵的覆盖事实算好。
	// 谁先到谁发起，后来者等同一个 Task——预热与首开不会各读一遍，
	// 也不会出现"预热在跑、首开被 IsBusy 挡回"的空页面。
	public Task WarmUpAsync() => hasLoadedCatalog ? Task.CompletedTask : EnsureCatalogLoadedAsync(isWarmUp: true);

	private Task EnsureCatalogLoadedAsync(bool isWarmUp)
	{
		lock (catalogLoadGate)
			return catalogLoadTask ??= LoadCatalogOnceAsync(isWarmUp);
	}

	private async Task LoadCatalogOnceAsync(bool isWarmUp)
	{
		try
		{
			await RefreshAsync(isWarmUp).ConfigureAwait(true);
		}
		catch (Exception exception)
		{
			// 取数是三条入口（OnLoaded / WarmUpAsync / ShellViewModel 直接 InitializeAsync）共用的唯一出口。
			// RefreshAsync 只把它认识的那几类异常转成状态文本；能落到这里的是它没预料到的
			// （最典型：ApplySnapshot 对缺失护甲抛 KeyNotFoundException）。
			// 让异常从这里逃出去，就是从一个 async void 的 UI 事件处理器逃到全局 ——
			// 用户看到的是"管理器遇到未处理错误"整个弹窗，而页面本身其实完全有状态可以显示。
			// 所以这里**兜住但不当没发生**：记 Error 日志 + 把失败写进页面状态。
			LogService.Error($"护甲映射页取数失败：{exception}");
			StatusTitle = "读取失败";
			StatusDetail = exception.Message;
		}
		finally
		{
			// 失败时 hasLoadedCatalog 仍为 false，清掉账本让下一次打开能重试。
			lock (catalogLoadGate) catalogLoadTask = null;
		}
	}

	public async Task FlushWorkspaceAsync()
	{
		FlushWorkspaceSave();
		await workspaceSaveTask;
		await workspaceSaveGate.WaitAsync();
		workspaceSaveGate.Release();
		if (workspaceSaveError is not null)
			throw new IOException("护甲组件自动保存失败，请先解决文件写入问题。", workspaceSaveError);
	}

	public async Task ReloadWorkspaceAsync()
	{
		CloseComponentEditor();
		var snapshot = await service.ReadSnapshotAsync();
		ApplySnapshot(snapshot, rebuildChoices: true, resetNavigation: false);
	}

	public async Task BeginExternalWorkspaceEditAsync()
	{
		if (IsBusy) throw new InvalidOperationException("护甲映射正在执行其他操作，请稍后重试。");
		IsBusy = true;
		try { await FlushWorkspaceAsync(); }
		catch { IsBusy = false; throw; }
	}

	public void EndExternalWorkspaceEdit() => IsBusy = false;

	private async Task RefreshAsync(bool isWarmUp = false)
	{
		if (IsBusy) return;
		IsBusy = true;
		try
		{
			// 目录读取（SQLite）与快照读取都会在调用线程上同步跑完，覆盖事实更要读索引与全部已部署 patch。
			// 预热发生在启动后的空闲时刻，绝不能把这些压到 UI 线程上——所以预热路径整体丢到线程池，
			// 只有最后的 ApplySnapshot 回到 UI 线程（它要改绑定到视图的集合）。
			var inputs = isWarmUp
				? await Task.Run(LoadCatalogInputsAsync).ConfigureAwait(true)
				: await LoadCatalogInputsAsync().ConfigureAwait(true);
			if (inputs is not { } loaded) return;
			cachedCatalog = loaded.Catalog;
			cachedArmorDisplayFacts = loaded.ArmorDisplayFacts;
			hasLoadedCatalog = true;
			ApplySnapshot(loaded.Snapshot, rebuildChoices: true, resetNavigation: true);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			StatusTitle = "读取失败";
			StatusDetail = exception.Message;
			// 预热失败不该给"从没打开过这个页面"的用户弹错误——留一条日志即可。
			// 状态栏仍然写失败原因，用户真打开时看到的是"读取失败"而不是假装没有护甲被覆盖。
			if (isWarmUp) LogService.Warn($"预热运行时护甲注册失败：{exception.Message}");
			else notifications.Show($"读取运行时护甲注册失败：{exception.Message}", NotificationLevel.Error, TimeSpan.FromSeconds(10), "护甲注册");
		}
		finally
		{
			IsBusy = false;
		}
	}

	// 读取目录、快照与覆盖事实：纯 IO + 纯投影，不触碰任何绑定到视图的集合，
	// 因此可以安全地在任意线程上执行（预热时就是线程池）。
	// 失败返回 null 而不是空值——"读不到"和"没有护甲"是两件事，调用方不该把后者当成前者。
	private async Task<(RuntimeArmorRegistrySnapshot Snapshot, IReadOnlyList<RuntimeArmorCatalogEntry> Catalog, IReadOnlyDictionary<string, ArmorDisplayFacts> ArmorDisplayFacts)?> LoadCatalogInputsAsync()
	{
		// 先等取数闸门。它没放行时算出来的显示事实是错的：图标查不到（库快照还没加载），
		// 覆盖也可能算在**马上就要被启动部署改掉**的那份部署上。
		// 错数据一旦算完就会被标成"已加载"，用户之后打开页面看到的就是这一份。
		if (displayFactsReady is not null) await displayFactsReady.ConfigureAwait(false);
		if (PendingWorkspaceSaves.TryGetValue(service.WorkspacePath, out var pending))
			await pending.ConfigureAwait(false);
		var catalogTask = service.ReadArmorCatalogAsync().AsTask();
		var snapshotTask = service.ReadSnapshotAsync().AsTask();
		await Task.WhenAll(catalogTask, snapshotTask).ConfigureAwait(false);
		var snapshot = await snapshotTask.ConfigureAwait(false);
		var catalog = await catalogTask.ConfigureAwait(false);
		var armorDisplayFacts = await ReadArmorDisplayFactsAsync(catalog).ConfigureAwait(false);
		return (snapshot, catalog, armorDisplayFacts);
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

	// 作用：把"映射引用的护甲 id"解析成可显示的选项，**永不抛**。
	//
	// 为什么必须有这个兜底：映射的目标 id 来自工作区/协议文件，而它能不能解析取决于
	// 当前这台机器上的资产索引 —— 索引缺失、过期、或工作区来自另一台机器/另一个模组库时，
	// 目标就可能不在 `Choices` 里。上面那条 `map=` 循环已经为这种目标补了 `Unknown`，
	// 但模型映射（`map_model`）的 `SourceItemId` 恒为 null，会被那条循环跳过，
	// 于是模型映射的目标**从来没有得到过兜底**，直接用索引器就是 KeyNotFoundException。
	//
	// 兜底不等于静默：缺一次记一条 WARN（同一个 id 只会记一次，因为下面会把它补进 `Choices`），
	// 页面上则照实显示"未知护甲 {id}"。
	private RuntimeArmorChoiceViewModel ResolveChoice(uint itemId)
	{
		if (Choices.TryGetValue(itemId, out var choice)) return choice;
		LogService.Warn($"护甲映射引用了当前资产索引中不存在的护甲 {itemId}，已按“未知护甲”显示。");
		choice = RuntimeArmorChoiceViewModel.Unknown(itemId);
		Choices[itemId] = choice;
		return choice;
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
					: null,
					kitKind: item.KitKind,
					setId: item.SetId,
					dlcId: item.DlcId))
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
					ResolveChoice(mapping.TargetItemId),
					ResolveChoice(SyntheticModelItemId(model.Key)),
					model.Key));
				continue;
			}
			if (mapping.SourceItemId is not { } sourceItemId) continue;
			Mappings.Add(new RuntimeArmorMappingRowViewModel(
				ResolveChoice(mapping.TargetItemId),
				ResolveChoice(sourceItemId)));
		}
		if (resetNavigation)
		{
			pendingProviderIds.Clear();
			selectedProviderId = null;
			selectedProviderMemberIds = [];
			OnPropertyChanged(nameof(IsProviderDetailOpen));
		}
		else
		{
			pendingProviderIds.Clear();
			if (selectedProviderId is not null
				&& !selectedProviderMemberIds.Any(memberId =>
					snapshot.Document.Mappings.Any(mapping => mapping.SourceItemId == memberId)))
			{
				selectedProviderId = null;
				selectedProviderMemberIds = [];
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
		modelDefinitionsDirty = snapshot.HasUnappliedChanges;
		HasChanges = snapshot.HasUnappliedChanges;
	}

	private async Task<IReadOnlyDictionary<string, ArmorDisplayFacts>> ReadArmorDisplayFactsAsync(
		IReadOnlyList<RuntimeArmorCatalogEntry> catalog)
	{
		var result = new Dictionary<string, ArmorDisplayFacts>(StringComparer.OrdinalIgnoreCase);
		var gameData = SettingsService.GetGameDataFolder();
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return result;
		var archiveIds = catalog
			.Select(item => item.ArchiveId)
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (archiveIds.Count == 0) return result;

		try
		{
			// 只问"这批护甲被谁覆盖"，不再整包构建 GameData 归档浏览器。
			// 旧路径为了两个字段去构建整个浏览器，而那一步会为**每一个** Mod 请求一次资产清单
			// （逐包解析 patch）——这正是护甲页首开卡顿的根因。
			// 覆盖结论只取决于"已部署且有归属的资产"，与 Mod 库、激活配置都无关（设计文档 §4 #23、§23）。
			// ConfigureAwait(false)：本方法只读库快照与内存字典，不需要回到 UI 线程；
			// 让后面的逐护甲投影留在调用方所在的线程上（预热路径即线程池）。
			var coverage = await library.InformationCenter.RequestArmorCoverageAsync(
				gameData,
				archiveIds,
				new ModInformationRequest(ModInformationKind.ArmorCoverage, "ArmorRegistryPage")).ConfigureAwait(false);
			if (coverage.Data is null)
			{
				// 索引未建或生产失败都表现为 Data 为空。前者是正常状态，后者需要留下线索。
				if (coverage.Status == ModInformationStatus.Failed)
					LogService.Warn($"读取护甲覆盖事实失败：{string.Join("；", coverage.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");
				return result;
			}

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
				// 只有索引里确实存在的归档才会出现在覆盖事实里；不在其中的保持"未知"而不是"未覆盖"。
				// 图标仍在这里解析：它是对赢家节点的库内查找（内存字典 + 父链），代价可忽略，
				// 而且放进事实会让"换个图标"变成"覆盖事实重算"——与设计文档 §7 护栏 3 正好相反。
				if (!coverage.Data.Archives.TryGetValue(item.ArchiveId, out var entry)) continue;
				var imagePath = entry.CoverWinnerNodeId is { } winner && nodesById.ContainsKey(winner)
					? FindModIconInParentChain(winner.Value, parents)
					: null;
				result[item.ArchiveId] = new ArmorDisplayFacts(imagePath, entry.HasEffectiveUnitOverride);
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
		if (provider is null || !Providers.Any(item => item.SelectionKey == provider.SelectionKey)) return;
		foreach (var item in AvailableArmors.Concat(Providers).Concat(ProviderDetailItems))
			item.IsSelected = false;
		selection.Clear();
		selectedProviderId = provider.ItemId;
		selectedProviderMemberIds = provider.MemberItemIds.ToArray();
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
		selectedProviderMemberIds = [];
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
		// The target owns overrides, never the component structure. Resolve its
		// actual mapping rather than whichever provider happens to be selected.
		var source = isTarget
			? Mappings.FirstOrDefault(row => row.Target.ItemId == item.ItemId)?.Source
			: item;
		if (source is null) return;
		var request = ++componentEditorRequest;
		var model = CustomModels.FirstOrDefault(model =>
			string.Equals(model.Key, source.ModelKey, StringComparison.Ordinal))?.ToDomain();
		if (source.IsCustomModel && model is null) return;
		// 组合护甲（头盔 + 护甲成套）的 unit 分散在**多个 item** 上，只查 ItemId 会漏掉
		// 除第一件之外的全部 unit——这正是"树上少了很多 unit"的来源。
		var members = source.MemberItemIds.Count > 0 ? source.MemberItemIds : [source.ItemId];
		var pieces = model?.Pieces;
		if (pieces is null)
		{
			if (members.Count == 1)
			{
				pieces = await service.ReadArmorPiecesAsync(members[0]);
			}
			else
			{
				var merged = new List<RuntimeArmorRegistryModelPiece>();
				var seen = new HashSet<(uint, string, uint)>();
				foreach (var member in members)
				{
					foreach (var piece in await service.ReadArmorPiecesAsync(member))
					{
						if (seen.Add((piece.Body, piece.Unit, piece.Slot)))
							merged.Add(piece);
					}
				}
				pieces = merged;
			}
		}
		if (request != componentEditorRequest) return;
		if (isTarget && !Mappings.Any(row => row.Target.ItemId == item.ItemId && row.Source.ItemId == source.ItemId))
			return;
		componentEditorOwnerId = item.ItemId;
		componentEditorIsTarget = isTarget;
		componentEditorFollowSource = targetModes.FirstOrDefault(mode => mode.TargetItemId == item.ItemId)?.FollowSource ?? true;
		componentEditorSourcePieces = source.IsCustomModel ? RuntimeArmorModelStructure.Normalize(pieces) : pieces;
		componentEditorSourceOverrideOwner = source.IsCustomModel ? model?.SourceItemId : source.ItemId;
		PopulateComponentEditor();
		// 树要的 unit 表与"组件结构编辑器"要的不是一回事：
		//   · 自定义素体：它自己就是权威，unit 表来自注册工作区的 models[].pieces；
		//   · 普通护甲：来自**游戏注册文件**（generated_customization_armor_sets）。
		// 后者不走 game_data_authority_pieces 那张派生表——实测那张表会丢行
		// （RE-2310 头盔套 0 行、护甲套缺一个槽位），而注册文件是它的源头。
		IReadOnlyList<string> treeUnitIds;
		IReadOnlyList<uint> missingArmourItems = Array.Empty<uint>();
		var gameData = SettingsService.GetGameDataFolder();
		if (model is not null)
		{
			treeUnitIds = (model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
				.Select(piece => piece.Unit)
				.Where(unit => !string.IsNullOrWhiteSpace(unit))
				.ToArray();
		}
		else if (!string.IsNullOrWhiteSpace(gameData) && Directory.Exists(gameData))
		{
			// 走信息中心而不是直接读注册文件：编辑器和将来的 3D 窗口问同一批条目时只会算一次。
			var table = await library.InformationCenter
				.RequestArmourUnitTableAsync(
					gameData,
					members.ToHashSet(),
					new ModInformationRequest(ModInformationKind.ArmourUnitTable, "ArmorRegistryPage"))
				.ConfigureAwait(true);
			treeUnitIds = table.Data?.ForItems(members) ?? Array.Empty<string>();
			missingArmourItems = table.Data?.MissingItems ?? Array.Empty<uint>();
			if (treeUnitIds.Count == 0)
			{
				// 注册文件读不到时退回件表：宁可少 unit，也不能整棵树空掉。
				treeUnitIds = componentEditorSourcePieces.Select(piece => piece.Unit).ToArray();
			}
		}
		else
		{
			treeUnitIds = componentEditorSourcePieces.Select(piece => piece.Unit).ToArray();
		}

		// section 结构同样走信息中心：它按"一批 unit 键"记忆，与 section 映射表共用一份描述。
		IReadOnlyList<WardrobeSectionMapUnit> treeUnits = Array.Empty<WardrobeSectionMapUnit>();
		IReadOnlyList<string> missingUnitIds = Array.Empty<string>();
		if (treeUnitIds.Count > 0 && !string.IsNullOrWhiteSpace(gameData) && Directory.Exists(gameData))
		{
			var structure = await library.InformationCenter
				.RequestUnitSectionStructureAsync(
					gameData,
					treeUnitIds.ToHashSet(StringComparer.Ordinal),
					new ModInformationRequest(ModInformationKind.UnitSectionStructure, "ArmorRegistryPage"))
				.ConfigureAwait(true);
			treeUnits = structure.Data?.ToMapUnits() ?? Array.Empty<WardrobeSectionMapUnit>();
			missingUnitIds = structure.Data?.MissingUnitIds ?? Array.Empty<string>();
		}

		if (request != componentEditorRequest) return;
		// 树的 section 数据来自当前部署的 patch 与原版数据包，所以编辑器看到的就是游戏实际加载的几何。
		// 它和组件结构是两条独立的轴：组件结构决定"用哪些 unit"，树决定"这些 unit 的哪些 section 不可见"。
		Wardrobe.Load(
			item.ItemId,
			source.ItemId,
			isTarget,
			item.Name,
			treeUnits,
			missingUnitIds,
			missingArmourItems);
		if (request != componentEditorRequest) return;
		OnPropertyChanged(nameof(IsComponentEditorOpen));
		OnPropertyChanged(nameof(ComponentEditorIsTarget));
		OnPropertyChanged(nameof(IsComponentEditorCustomModel));
		OnPropertyChanged(nameof(ComponentEditorFollowSource));
		OnPropertyChanged(nameof(ComponentEditorEditable));
		OnPropertyChanged(nameof(ComponentEditorTitle));
		OnPropertyChanged(nameof(ComponentEditorName));
		ConfirmAddComponentRowCommand.RaiseCanExecuteChanged();
		selection.Clear();
	}

	private void PopulateComponentEditor()
	{
		if (componentEditorOwnerId is not { } owner || !Choices.TryGetValue(owner, out var item)) return;
		var pieces = RuntimeArmorComponentProjection.Project(componentEditorSourcePieces,
			componentEditorSourceOverrideOwner, componentEditorIsTarget ? owner : null,
			componentEditorFollowSource, pieceOverrides);
		ComponentEditorPieces.Clear();
		foreach (var piece in pieces)
		{
			var normalizedPiece = piece with { IsDefaultBase = piece.IsDefaultBase || IsLegacyDefaultBasePiece(item, piece) };
			ComponentEditorPieces.Add(new RuntimeArmorModelPieceEditorViewModel(
				normalizedPiece,
				MarkComponentEditorDirty,
				IsComponentEditorCustomModel,
				RemoveComponentPiece));
		}
		RebuildComponentEditorGroups();
	}

	private static bool IsLegacyDefaultBasePiece(RuntimeArmorChoiceViewModel item, RuntimeArmorRegistryModelPiece piece)
		=> item.IsCustomModel
			&& piece.PieceType == 0
			&& piece.Slot is 2 or 3 or 4 or 5 or 6 or 7;

	public void CloseComponentEditor()
	{
		componentEditorRequest++;
		if (!IsComponentEditorOpen) return;
		FlushWorkspaceSave();
		componentEditorOwnerId = null;
		componentEditorIsTarget = false;
		componentEditorFollowSource = true;
		componentEditorSourcePieces = [];
		componentEditorSourceOverrideOwner = null;
		ComponentEditorPieces.Clear();
		ComponentEditorGroups.Clear();
		ComponentEditorRows.Clear();
		Wardrobe.Clear();
		OnPropertyChanged(nameof(IsComponentEditorOpen));
		OnPropertyChanged(nameof(ComponentEditorIsTarget));
		OnPropertyChanged(nameof(IsComponentEditorCustomModel));
		OnPropertyChanged(nameof(ComponentEditorFollowSource));
		OnPropertyChanged(nameof(ComponentEditorEditable));
		OnPropertyChanged(nameof(ComponentEditorTitle));
		OnPropertyChanged(nameof(ComponentEditorName));
	}

	public void PromoteAvailableAsProviders(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return;
		var ids = keys
			.SelectMany(key => AvailableArmors
				.FirstOrDefault(item => string.Equals(item.SelectionKey, key, StringComparison.OrdinalIgnoreCase))
				?.MemberItemIds ?? [])
			.ToArray();
		if (ids.Length == 0 || ids.Any(id => !AvailableArmors.Any(item => item.MemberItemIds.Contains(id)))) return;
		foreach (var id in ids)
			pendingProviderIds.Add(id);
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
		if (IsBusy || item is null || !item.IsCustomModel) return;
		var model = CustomModels.FirstOrDefault(candidate =>
			string.Equals(candidate.Key, item.ModelKey, StringComparison.Ordinal));
		RemoveCustomModel(model);
	}

	public void RenameCustomModelItem(RuntimeArmorChoiceViewModel? item)
	{
		if (IsBusy || item is null || !item.IsCustomModel || item.ModelKey is not { } key) return;
		var model = CustomModels.FirstOrDefault(candidate =>
			string.Equals(candidate.Key, key, StringComparison.Ordinal));
		if (model is null) return;
		if (Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
		shell.DismissToolBottomBars();
		shell.BottomBar.BeginCustomNameEdit(model.Name, async name =>
		{
			if (IsBusy) throw new InvalidOperationException("护甲映射正在执行其他操作，请稍后重试。");
			if (!CustomModels.Contains(model)) throw new InvalidOperationException("该自定义素体已被删除。");
			model.Rename(name);
			var choice = RuntimeArmorChoiceViewModel.CustomModel(item.ItemId, model.Name, model.Key);
			Choices[item.ItemId] = choice;
			for (var index = 0; index < Mappings.Count; index++)
				if (Mappings[index].Source.ItemId == item.ItemId)
					Mappings[index] = Mappings[index] with { Source = choice };
			RebuildProjections();
			OnPropertyChanged(nameof(ComponentEditorTitle));
			OnPropertyChanged(nameof(ComponentEditorName));
			await FlushWorkspaceAsync();
			await library.RefreshAssetSummariesAsync();
		});
	}

	public bool CanAssignFromAvailable(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return false;
		return keys.All(key => AvailableArmors.Any(armor => armor.SelectionKey == key));
	}

	public void AssignFromAvailable(IReadOnlyList<string> keys)
	{
		if (!CanAssignFromAvailable(keys)) return;
		var ids = keys
			.SelectMany(key => AvailableArmors.First(item => item.SelectionKey == key).MemberItemIds)
			.Distinct()
			.ToArray();
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
			selectedProviderMemberIds = [providerId];
			OnPropertyChanged(nameof(IsProviderDetailOpen));
			OnPropertyChanged(nameof(ProviderDetailTitle));
			OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
			OnPropertyChanged(nameof(AvailableRowActionToolTip));
			OnPropertyChanged(nameof(ProviderDetailSummary));
			RebuildProjections();
			selection.Clear();
			return;
		}

		var sourceIds = selectedProviderMemberIds.Count > 0
			? selectedProviderMemberIds
			: [selectedProviderId.Value];
		var sourceMembers = sourceIds
			.Where(Choices.ContainsKey)
			.Select(sourceId =>
			{
				var sourceChoice = Choices[sourceId];
				return new RuntimeArmorProviderMember(
					sourceChoice.ItemId,
					sourceChoice.KitKind,
					sourceChoice.IsCustomModel);
			})
			.ToArray();
		foreach (var id in ids)
		{
			if (!Choices.TryGetValue(id, out var targetChoice)
				|| Mappings.Any(mapping => mapping.Target.ItemId == id)
				|| Providers.Any(provider => provider.MemberItemIds.Contains(id))) continue;
			var sourceId = RuntimeArmorMappingPairing.FindUniqueSourceForTarget(sourceMembers, targetChoice.KitKind);
			if (sourceId is null || sourceId == id || !Choices.TryGetValue(sourceId.Value, out var sourceChoice)) continue;
			Mappings.Add(new RuntimeArmorMappingRowViewModel(
				targetChoice,
				sourceChoice,
				sourceChoice.IsCustomModel ? sourceChoice.ModelKey : null));
		}
		foreach (var sourceId in sourceIds)
			if (ids.Any(id => Mappings.Any(mapping => mapping.Source.ItemId == sourceId && mapping.Target.ItemId == id)))
				pendingProviderIds.Remove(sourceId);
		RebuildProjections();
		RecomputeHasChanges();
		selection.Clear();
	}

	public bool CanReleaseToAvailable(IReadOnlyList<string> keys)
	{
		if (IsBusy || keys.Count == 0) return false;
		var values = keys
			.SelectMany(key => FindDisplayedItem(key)?.MemberItemIds ?? [])
			.Distinct()
			.ToArray();
		if (values.Length == 0) return false;
		var sourceIds = Providers.SelectMany(provider => provider.MemberItemIds).ToHashSet();
		var customSourceIds = Providers.Where(provider => provider.IsCustomModel)
			.SelectMany(provider => provider.MemberItemIds).ToHashSet();
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
		var ids = keys
			.SelectMany(key => FindDisplayedItem(key)?.MemberItemIds ?? [])
			.ToHashSet();
		var sourceIds = Providers.SelectMany(provider => provider.MemberItemIds).ToHashSet();
		var customSourceIds = Providers.Where(provider => provider.IsCustomModel)
			.SelectMany(provider => provider.MemberItemIds)
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
			if (selectedProviderMemberIds.Any(selectedSources.Contains)) CloseProvider();
		}
		else
		{
			var removed = Mappings.Where(mapping => ids.Contains(mapping.Target.ItemId)).ToArray();
			foreach (var mapping in removed) Mappings.Remove(mapping);
			if (selectedProviderId is not null
				&& selectedProviderMemberIds.All(currentSource =>
					!pendingProviderIds.Contains(currentSource)
					&& !Mappings.Any(mapping => mapping.Source.ItemId == currentSource)))
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

		Providers.ReplaceWith(BuildDisplayChoices(sourceIds)
			.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase),
			ListTransitionKind.Replace);
		AvailableArmors.ReplaceWith(BuildDisplayChoices(Choices.Values
				.Where(choice => !managedIds.Contains(choice.ItemId))
				.Select(choice => choice.ItemId))
			.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase),
			ListTransitionKind.Replace);

		var detailItems = new List<RuntimeArmorChoiceViewModel>();
		if (selectedProviderId is not null && selectedProviderMemberIds.Count > 0)
		{
			var detailTargetIds = Mappings
				.Where(mapping => selectedProviderMemberIds.Contains(mapping.Source.ItemId))
				.Select(mapping => mapping.Target.ItemId)
				.Distinct()
				.ToArray();
			foreach (var target in BuildDisplayChoices(detailTargetIds)
				.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase))
			{
				target.SetRolePresentation(target.IsCombined ? "组合映射目标" : target.KitKind == GameDataEquipmentKitKind.Helmet ? "仅头盔" : "仅护甲", null);
				detailItems.Add(target);
			}
		}
		ProviderDetailItems.ReplaceWith(detailItems, ListTransitionKind.Replace);
		foreach (var item in AvailableArmors)
			item.SetRolePresentation(
				item.KitKind == GameDataEquipmentKitKind.Helmet ? "头盔" : "护甲",
				null);
		foreach (var item in Providers)
			item.SetRolePresentation(
				item.IsCustomModel
					? "自定义视觉来源"
					: item.IsCombined
						? "组合视觉来源"
						: item.IsHelmet ? "头盔视觉来源" : "护甲视觉来源",
				Mappings.Count(mapping => item.MemberItemIds.Contains(mapping.Source.ItemId)));

		OnPropertyChanged(nameof(ProvidersSummary));
		OnPropertyChanged(nameof(AvailableSummary));
		OnPropertyChanged(nameof(ProviderDetailTitle));
		OnPropertyChanged(nameof(ProviderDetailHeaderTitle));
		OnPropertyChanged(nameof(ProviderDetailSummary));
		OnPropertyChanged(nameof(HasMappings));
	}

	private RuntimeArmorChoiceViewModel? FindDisplayedItem(string selectionKey)
		=> AvailableArmors.Concat(Providers).Concat(ProviderDetailItems)
			.FirstOrDefault(item => string.Equals(item.SelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase));

	private IReadOnlyList<RuntimeArmorChoiceViewModel> BuildDisplayChoices(IEnumerable<uint> ids)
	{
		var members = ids
			.Distinct()
			.Where(Choices.ContainsKey)
			.Select(id => Choices[id])
			.ToArray();
		if (availableArmorKindFilter != AvailableArmorKindFilter.AutomaticCombination)
		{
			var desired = availableArmorKindFilter == AvailableArmorKindFilter.HelmetOnly
				? GameDataEquipmentKitKind.Helmet
				: GameDataEquipmentKitKind.Armor;
			return members
				.Where(item => item.IsCustomModel || item.KitKind == desired)
				.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
				.ToArray();
		}
		var result = new List<RuntimeArmorChoiceViewModel>();
		foreach (var group in members.GroupBy(item => item.CombinationKey, StringComparer.OrdinalIgnoreCase))
		{
			var armor = group.Where(item => item.KitKind == GameDataEquipmentKitKind.Armor).ToArray();
			var helmets = group.Where(item => item.KitKind == GameDataEquipmentKitKind.Helmet).ToArray();
			if (armor.Length == 1 && helmets.Length == 1)
				result.Add(RuntimeArmorChoiceViewModel.Composite(armor[0], helmets[0]));
			else
				result.AddRange(group);
		}
		return result;
	}

	private bool FilterProvider(object item)
		=> item is RuntimeArmorChoiceViewModel choice
			&& MatchesKindFilter(choice)
			&& MatchesSearch(choice, providersSearchText);

	private bool FilterAvailableArmor(object item)
	{
		if (item is not RuntimeArmorChoiceViewModel choice || !MatchesSearch(choice, availableSearchText))
			return false;
		if (availableArmorKindFilter == AvailableArmorKindFilter.ArmorOnly
			&& choice.KitKind != GameDataEquipmentKitKind.Armor)
			return false;
		if (availableArmorKindFilter == AvailableArmorKindFilter.HelmetOnly
			&& choice.KitKind != GameDataEquipmentKitKind.Helmet)
			return false;
		return availableArmorUnitFilter switch
		{
			AvailableArmorUnitFilter.HasEffectiveUnitOverride => choice.HasEffectiveUnitOverride == true,
			AvailableArmorUnitFilter.NoEffectiveUnitOverride => choice.HasEffectiveUnitOverride == false,
			_ => true,
		};
	}

	private bool FilterProviderDetailItem(object item)
		=> item is RuntimeArmorChoiceViewModel choice
			&& MatchesKindFilter(choice)
			&& MatchesSearch(choice, providerDetailSearchText);

	private bool MatchesKindFilter(RuntimeArmorChoiceViewModel choice)
		=> availableArmorKindFilter switch
		{
			AvailableArmorKindFilter.ArmorOnly => choice.IsCustomModel || choice.KitKind == GameDataEquipmentKitKind.Armor,
			AvailableArmorKindFilter.HelmetOnly => choice.IsCustomModel || choice.KitKind == GameDataEquipmentKitKind.Helmet,
			_ => true,
		};

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
		// A revert is an edit too: supersede any queued non-default snapshot.
		if (hasLoadedCatalog)
			ScheduleWorkspaceSave();
	}

	private void ScheduleWorkspaceSave()
	{
		workspaceSaveCancellation?.Cancel();
		var cancellation = new CancellationTokenSource();
		workspaceSaveCancellation = cancellation;
		// Snapshot UI collections on the UI thread, not after ConfigureAwait(false).
		var document = BuildWorkspaceDocument();
		workspaceSaveTask = SaveWorkspaceAfterDelayAsync(cancellation, document);
		PendingWorkspaceSaves[service.WorkspacePath] = workspaceSaveTask;
	}

	private void FlushWorkspaceSave()
	{
		if (workspaceSaveCancellation is null) return;
		workspaceSaveCancellation.Cancel();
		var cancellation = new CancellationTokenSource();
		workspaceSaveCancellation = cancellation;
		workspaceSaveTask = SaveWorkspaceAfterDelayAsync(cancellation, BuildWorkspaceDocument(), immediate: true);
		PendingWorkspaceSaves[service.WorkspacePath] = workspaceSaveTask;
	}

	private async Task SaveWorkspaceAfterDelayAsync(CancellationTokenSource cancellation, RuntimeArmorRegistryDocument document, bool immediate = false)
	{
		try
		{
			if (!immediate)
				await Task.Delay(400, cancellation.Token);
			await workspaceSaveGate.WaitAsync(cancellation.Token);
			try
			{
				await service.SaveWorkspaceAsync(document, cancellation.Token);
				workspaceSaveError = null;
			}
			finally { workspaceSaveGate.Release(); }
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			workspaceSaveError = exception;
			LogService.Warn($"自动保存护甲注册工作区失败：{exception.Message}");
			StatusTitle = "自动保存失败";
			StatusDetail = exception.Message;
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
			modelDefinitions = CustomModels.Select(model => model.ToDomain()).ToArray();
			modelDefinitionsDirty = true;
			RecomputeHasChanges();
			return;
		}
		// Following is a view of the source, not a target override snapshot.
		// Preserve manual choices while temporarily following the source.
		if (!componentEditorIsTarget || !componentEditorFollowSource)
		{
			var overrides = pieceOverrides.Where(item => item.OwnerItemId != owner).ToList();
			foreach (var piece in ComponentEditorPieces)
				overrides.Add(new RuntimeArmorRegistryPieceOverride(
					owner, piece.Body, piece.Unit, piece.Slot, piece.PieceType, piece.Enabled));
			pieceOverrides = overrides;
		}
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
		if (NewComponentSlot == 0)
		{
			ComponentEditorPieces.Add(CreateEditorPiece(
				new RuntimeArmorRegistryModelPiece(3, NewGeneratedUnit(seed), NewComponentSlot, pieceType, 0, true, false)));
		}
		else
		{
			ComponentEditorPieces.Add(CreateEditorPiece(
				new RuntimeArmorRegistryModelPiece(0, NewGeneratedUnit(seed), NewComponentSlot, pieceType, 0, true, false)));
			ComponentEditorPieces.Add(CreateEditorPiece(
				new RuntimeArmorRegistryModelPiece(1, NewGeneratedUnit(seed + 1), NewComponentSlot, pieceType, 0, true, false)));
		}
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
		if (piece.Body == 3)
			foreach (var reserved in ComponentEditorPieces.Where(other => other.IsReserved
				&& other.Slot == piece.Slot && other.PieceType == piece.PieceType).ToArray())
				ComponentEditorPieces.Remove(reserved);
		RebuildComponentEditorGroups();
		MarkComponentEditorDirty();
	}

	private void RebuildComponentEditorGroups()
	{
		ComponentEditorGroups.Clear();
		ComponentEditorRows.Clear();
		foreach (var slotGroup in ComponentEditorPieces.Where(piece => !piece.IsReserved)
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
					slotGroup.Key,
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
		ReplaceComponentStructure(RuntimeArmorModelStructure.Merge(ComponentEditorPieces.Select(p => p.ToDomain()), slot, pieceType));
	}

	private void SplitComponentRow(uint slot, uint pieceType)
	{
		if (MessageBox.Show(
				"拆分为双身形可能启用备用 Unit ID；若对应 Patch 尚未准备，该身形将不会显示。是否继续？",
				"确认拆分组件",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning) != MessageBoxResult.Yes)
			return;
		ReplaceComponentStructure(RuntimeArmorModelStructure.Split(ComponentEditorPieces.Select(p => p.ToDomain()), slot, pieceType, () => NewGeneratedUnit()));
	}

	private void ReplaceComponentStructure(IReadOnlyList<RuntimeArmorRegistryModelPiece> pieces)
	{
		ComponentEditorPieces.Clear();
		foreach (var piece in pieces) ComponentEditorPieces.Add(CreateEditorPiece(piece));
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
		=> new(piece, MarkComponentEditorDirty, IsComponentEditorCustomModel, RemoveComponentPiece);

	private static int SlotOrder(uint slot) => ArmorStructure.Order(slot);

	private void MarkModelDefinitionsDirty()
	{
		modelDefinitions = CustomModels.Select(model => model.ToDomain()).ToArray();
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

	private async Task RefreshAssetLabelsAfterModelDeletionAsync()
	{
		try
		{
			await FlushWorkspaceAsync();
			await library.RefreshAssetSummariesAsync();
		}
		catch (Exception exception)
		{
			LogService.Warn($"删除素体后刷新资产标签失败：{exception.Message}");
		}
	}

	private void RemoveCustomModel(RuntimeArmorModelEditorViewModel? model)
	{
		if (model is null || !CustomModels.Contains(model)) return;
		var syntheticId = SyntheticModelItemId(model.Key);
		var removed = Mappings.Where(mapping => mapping.Source.ItemId == syntheticId).ToArray();
		var released = removed.Select(mapping => mapping.Target.ItemId).Append(syntheticId).ToHashSet();
		if (componentEditorOwnerId is { } owner && released.Contains(owner)) CloseComponentEditor();
		if (selectedProviderId == syntheticId) CloseProvider();
		CustomModels.Remove(model);
		Choices.Remove(syntheticId);
		foreach (var mapping in removed) Mappings.Remove(mapping);
		pendingProviderIds.Remove(syntheticId);
		pieceOverrides = pieceOverrides.Where(item => !released.Contains(item.OwnerItemId)).ToArray();
		targetModes = targetModes.Where(item => !released.Contains(item.TargetItemId)).ToArray();
		selection.Clear();
		RebuildProjections();
		MarkModelDefinitionsDirty();
		OnPropertyChanged(nameof(HasCustomModels));
		OnPropertyChanged(nameof(CustomModelCount));
		_ = RefreshAssetLabelsAfterModelDeletionAsync();
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
		// 与 CanExecute 保持一致：冷却中或正在忙时不可点；没有改动也允许重投。
		if (IsBusy || applyCooldownActive) return;
		IsBusy = true;
		try
		{
			workspaceSaveCancellation?.Cancel();
			await workspaceSaveTask;
			// Drain an older in-flight write as well as the latest debounce task.
			await workspaceSaveGate.WaitAsync();
			workspaceSaveGate.Release();
			var sourceIds = Mappings.Select(mapping => mapping.Source.ItemId).ToHashSet();
			var targetIds = Mappings.Select(mapping => mapping.Target.ItemId).ToHashSet();
			if (sourceIds.Overlaps(targetIds))
				throw new InvalidOperationException("当前协议包含“视觉来源同时作为映射目标”的链式关系。请先移除冲突映射；GUI 不会创建或保存此类关系。");

			var desired = Mappings
				.Select(row => row.Source.IsCustomModel
					? new RuntimeArmorRegistryMapping(row.Target.ItemId, null, row.Source.ModelKey)
					: new RuntimeArmorRegistryMapping(row.Target.ItemId, row.Source.ItemId))
				.ToArray();
			if (!IsGameRunning() && Enabled && desired.Any(mapping => mapping.IsModelMapping))
			{
				var deployment = await deploymentCoordinator.FlushAsync();
				if (deployment.Stage != ProfileDeploymentStage.Completed)
					throw new InvalidOperationException(deployment.Message ?? "模型包部署失败，尚未编译护甲注册。");
			}
			var result = await service.ApplyAsync(
				fingerprint,
				Enabled,
				desired,
				CustomModels.Select(model => model.ToDomain()).ToArray(),
				pieceOverrides,
				targetModes,
				gameDataDirectory: SettingsService.GetGameDataFolder());
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
			if (selectedProviderId is not null && !selectedProviderMemberIds.Any(sourceIds.Contains))
			{
				selectedProviderId = null;
				selectedProviderMemberIds = [];
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
			var runtimeLogSummary = await service.ReadRecentRuntimeLogSummaryAsync();
			var skippedSummary = runtimeLogSummary
				.Where(line => line.Contains("partial_apply:", StringComparison.Ordinal)
					&& line.Contains(
						"revision=" + result.Document.Revision.ToString(CultureInfo.InvariantCulture),
						StringComparison.Ordinal))
				.TakeLast(8)
				.ToArray();
			var runtimeErrorSummary = runtimeLogSummary
				.Where(line => line.Contains("apply_error:", StringComparison.Ordinal)
					&& line.Contains(
						"revision=" + result.Document.Revision.ToString(CultureInfo.InvariantCulture),
						StringComparison.Ordinal))
				.TakeLast(4)
				.ToArray();
			if (runtimeReportedApply)
			{
				StatusTitle = "游戏已同步";
				StatusDetail = $"运行时状态已报告 revision {result.Document.Revision}，当前为{GetRuntimeMode(snapshot)}。";
				if (skippedSummary.Length > 0)
				{
					StatusTitle = "游戏已同步（部分目标跳过）";
					StatusDetail += $" 本次有 {skippedSummary.Length} 条目标未应用，请查看日志。";
					notifications.Show(
						$"护甲注册已应用，但有 {skippedSummary.Length} 个目标被跳过。请查看运行时日志中的错误汇总。",
						NotificationLevel.Warning,
						TimeSpan.FromSeconds(10),
						"护甲注册",
						"armor-registry-apply");
				}
				else
				{
				notifications.Show(
					$"护甲外观注册已在线应用：revision {result.Document.Revision}。",
					NotificationLevel.Info,
					TimeSpan.FromSeconds(6),
					"护甲注册",
					"armor-registry-apply");
				}
			}
			else
			{
				UpdateStatus(snapshot);
				if (snapshot.RuntimeProcessRunning)
				{
					var detail = runtimeErrorSummary.Length == 0
						? "协议已保存，但游戏运行时尚未确认；请稍后刷新状态。"
						: $"运行时报告了 {runtimeErrorSummary.Length} 条错误：{string.Join(" | ", runtimeErrorSummary)}";
					notifications.Show(
						detail,
						runtimeErrorSummary.Length == 0 ? NotificationLevel.Warning : NotificationLevel.Error,
						TimeSpan.FromSeconds(8),
						"护甲注册",
						"armor-registry-apply");
					StatusDetail = detail;
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
			// 应用结束进入 10 秒冷却；冷却结束后按钮重新可用（即便没有新的改动）。
			_ = RunApplyCooldownAsync();
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

		// 追加"我们这边算得准不准"的说明。上面几段讲的是**游戏那边**的状态，
		// 下面两条讲的是**页面本身**可能显示不完整的原因——只追加、不覆盖，
		// 而且绝不因为这些问题就少显示映射：映射是用户自己攒出来的资产，看不见比看见得不全更糟。
		//
		// 为什么需要说：工作区在模组库里、协议在用户目录里，两者生命周期不同（回归文档 §13）。
		// 用户换了一个解压目录之后，本库没有工作区，页面只能显示协议副本——那是他映射的最后一份存留。
		// 显示它是对的，但不告诉他"这不是本库的工作区"，他会以为映射本来就这么少。
		var advisories = new List<string>();
		if (snapshot.DocumentSource is RuntimeArmorRegistryDocumentSource.ProtocolFallback)
		{
			var owner = string.IsNullOrWhiteSpace(snapshot.ProtocolOwnerWorkspace)
				? "未知（该协议由更早的版本应用，没有留下归属记录）"
				: snapshot.ProtocolOwnerWorkspace;
			advisories.Add($"当前模组库没有工作区文件，正在显示协议副本；该协议上次由 {owner} 应用。"
				+ "如果这不是你要用的模组库，请到设置页确认模组库地址。");
		}
		if (hasLoadedCatalog && cachedCatalog.Count == 0)
		{
			advisories.Add("资产索引缺失或为空，护甲名称无法解析（映射会显示为“未知护甲”）；"
				+ "请到设置页建立资产索引。");
		}
		if (advisories.Count > 0)
			StatusDetail = $"{StatusDetail} {string.Join(" ", advisories)}";
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
	private readonly IReadOnlyList<uint> memberItemIds;

	public RuntimeArmorChoiceViewModel(
		uint itemId,
		string archiveId,
		string displayName,
		string? imagePath = null,
		bool? hasEffectiveUnitOverride = null,
		bool isUnknown = false,
		bool isCustomModel = false,
		string? modelKey = null,
		GameDataEquipmentKitKind kitKind = GameDataEquipmentKitKind.Armor,
		IReadOnlyList<uint>? memberItemIds = null,
		uint setId = 0,
		uint dlcId = 0)
	{
		ItemId = itemId;
		ArchiveId = archiveId;
		Name = displayName;
		ImagePath = imagePath;
		HasEffectiveUnitOverride = hasEffectiveUnitOverride;
		IsUnknown = isUnknown;
		IsCustomModel = isCustomModel;
		ModelKey = modelKey;
		KitKind = kitKind;
		SetId = setId;
		DlcId = dlcId;
		this.memberItemIds = memberItemIds is { Count: > 0 } ? memberItemIds : [itemId];
	}

	public uint ItemId { get; }
	public string ArchiveId { get; }
	public string Name { get; }
	public string? ImagePath { get; }
	public bool? HasEffectiveUnitOverride { get; }
	public bool IsUnknown { get; }
	public bool IsCustomModel { get; }
	public string? ModelKey { get; }
	public GameDataEquipmentKitKind KitKind { get; }
	public uint SetId { get; }
	public uint DlcId { get; }
	public string CombinationKey => SetId != 0
		? $"{DlcId}:{SetId}"
		: "archive:" + ArchiveId;
	public bool IsHelmet => KitKind == GameDataEquipmentKitKind.Helmet;
	public bool IsArmor => KitKind == GameDataEquipmentKitKind.Armor;
	public IReadOnlyList<uint> MemberItemIds => memberItemIds;
	public bool IsCombined => memberItemIds.Count > 1;
	public string ListKey => SelectionKey;
	public string SelectionKey => IsCombined
		? "combo:" + string.Join("+", memberItemIds.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)))
		: ItemId.ToString(CultureInfo.InvariantCulture);
	public bool IsDecoration => false;
	public string Display => $"{Name} · {ArchiveId}";
	public string ItemIdText => ItemId.ToString(CultureInfo.InvariantCulture);
	public bool IsSelected { get => isSelected; set => SetField(ref isSelected, value); }
	public ModListRowPresentation RowPresentation { get => rowPresentation; private set => SetField(ref rowPresentation, value); }

	public void SetRolePresentation(string? role, int? count)
	{
		if (IsCombined && role is not null)
			role = role.StartsWith("自定义", StringComparison.Ordinal)
				? role
				: "组合";
		var summary = role switch
		{
			"组合" when count is { } value => value == 0 ? "组合 · 尚无映射目标" : $"组合 · {value} 个目标",
			"组合视觉来源" when count is { } value => value == 0 ? "组合视觉来源 · 尚无映射目标" : $"组合视觉来源 · {value} 个目标",
			"头盔视觉来源" when count is { } value => value == 0 ? "头盔视觉来源 · 尚无映射目标" : $"头盔视觉来源 · {value} 个目标",
			"护甲视觉来源" when count is { } value => value == 0 ? "护甲视觉来源 · 尚无映射目标" : $"护甲视觉来源 · {value} 个目标",
			"视觉来源" when count is { } value => value == 0 ? "视觉来源 · 尚无映射目标" : $"视觉来源 · {value} 个目标",
			"自定义视觉来源" when count is { } value => value == 0 ? "自定义视觉来源 · 尚无映射目标" : $"自定义视觉来源 · {value} 个目标",
			"映射目标" => "映射目标",
			"组合映射目标" => "组合映射目标",
			"仅头盔" => "仅头盔",
			"仅护甲" => "仅护甲",
			_ => null,
		};
		RowPresentation = new ModListRowPresentation(TypeLabel: role, TypeTone: ModListRowTone.Accent, Summary: summary);
	}

	public static RuntimeArmorChoiceViewModel Unknown(uint itemId)
		=> new(itemId, itemId.ToString(CultureInfo.InvariantCulture), $"未知护甲 {itemId}", isUnknown: true);

	public static RuntimeArmorChoiceViewModel CustomModel(uint itemId, string name, string key)
		=> new(itemId, key, name, isCustomModel: true, modelKey: key);

	public static RuntimeArmorChoiceViewModel Composite(
		RuntimeArmorChoiceViewModel armor,
		RuntimeArmorChoiceViewModel helmet)
	{
		var members = new[] { armor.ItemId, helmet.ItemId };
		return new RuntimeArmorChoiceViewModel(
			armor.ItemId,
			armor.ArchiveId,
			$"{armor.Name} + {helmet.Name}",
			armor.ImagePath ?? helmet.ImagePath,
			armor.HasEffectiveUnitOverride == true || helmet.HasEffectiveUnitOverride == true,
			kitKind: GameDataEquipmentKitKind.Unknown,
			memberItemIds: members,
			setId: armor.SetId,
			dlcId: armor.DlcId);
	}
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
				SourceItemId is null,
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
				piece, changed, SourceItemId is null, RemovePiece));
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
		IsReserved = piece.IsReserved;
		OriginalBody = piece.OriginalBody;
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
	public string BodyLabel
	{
		get
		{
			var body = ArmorStructure.ToBodyVariant(Body);
			return body == HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Other
				? Body.ToString(CultureInfo.InvariantCulture)
				: ArmorStructure.DisplayBody(body);
		}
	}

	public string SlotLabel
	{
		get
		{
			var part = ArmorStructure.FromSlot(Slot);
			return part == ArmorPart.Unknown ? $"槽位 {Slot}" : ArmorStructure.Display(part);
		}
	}
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
		=> new(Body, Unit, Slot, PieceType, Tone, Enabled, IsDefaultBase, IsReserved, OriginalBody);

	public bool IsDefaultBase { get; }
	public bool IsReserved { get; }
	public uint? OriginalBody { get; }
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
		uint slot,
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
		var canChangeStructure = canEditStructure && slot != 0;
		MergeAnyCommand = new RelayCommand(_ => mergeAny(), _ => canChangeStructure);
		SplitCommand = new RelayCommand(_ => split(), _ => canChangeStructure);
		AddMissingStockyCommand = new RelayCommand(_ => addStocky(), _ => canChangeStructure);
		AddMissingSlimCommand = new RelayCommand(_ => addSlim(), _ => canChangeStructure);
		CanEditStructure = canChangeStructure;
	}

	public uint PieceType { get; }
	public RuntimeArmorModelPieceEditorViewModel? Stocky { get; }
	public RuntimeArmorModelPieceEditorViewModel? Slim { get; }
	public RuntimeArmorModelPieceEditorViewModel? Any { get; }
	public bool IsAny => Any is not null;
	public string SlotLabel => (Any ?? Stocky ?? Slim)?.SlotLabel ?? "组件";
	public string LayerLabel => ArmorStructure.DescribePieceSlot(PieceType);
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
