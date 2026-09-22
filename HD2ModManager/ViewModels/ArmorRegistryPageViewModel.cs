using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

public sealed class ArmorRegistryPageViewModel : FullWorkspacePageViewModel
{
	private readonly RuntimeArmorRegistryService service;
	private readonly NotificationService notifications;
	private string fingerprint = "missing";
	private bool enabled;
	private bool isBusy;
	private bool hasChanges;
	private string statusTitle = "尚未读取";
	private string statusDetail = "正在等待读取运行时护甲注册状态。";
	private string revisionText = "revision —";
	private RuntimeArmorChoiceViewModel? selectedTarget;
	private RuntimeArmorChoiceViewModel? selectedSource;

	public ArmorRegistryPageViewModel(NotificationService notifications)
	{
		Title = "运行时护甲注册";
		this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		service = CoreServices.CreateRuntimeArmorRegistryService(SettingsService.CreateStoragePaths());
		RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy && !HasChanges);
		ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => !IsBusy && HasChanges);
		AddMappingCommand = new RelayCommand(_ => AddMapping(), _ => CanAddMapping);
		RemoveMappingCommand = new RelayCommand(RemoveMapping, value => value is RuntimeArmorMappingRowViewModel && !IsBusy);
		OpenRegistryFolderCommand = new RelayCommand(OpenRegistryFolder);
	}

	public ObservableCollection<RuntimeArmorChoiceViewModel> ArmorChoices { get; } = new();
	public ObservableCollection<RuntimeArmorMappingRowViewModel> Mappings { get; } = new();

	public bool Enabled
	{
		get => enabled;
		set
		{
			if (!SetField(ref enabled, value)) return;
			MarkChanged();
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

	public bool HasMappings => Mappings.Count != 0;
	public string StatusTitle { get => statusTitle; private set => SetField(ref statusTitle, value); }
	public string StatusDetail { get => statusDetail; private set => SetField(ref statusDetail, value); }
	public string RevisionText { get => revisionText; private set => SetField(ref revisionText, value); }
	public string RegistryPath => service.RegistryRoot;

	public RuntimeArmorChoiceViewModel? SelectedTarget
	{
		get => selectedTarget;
		set
		{
			if (!SetField(ref selectedTarget, value)) return;
			OnPropertyChanged(nameof(CanAddMapping));
			AddMappingCommand.RaiseCanExecuteChanged();
		}
	}

	public RuntimeArmorChoiceViewModel? SelectedSource
	{
		get => selectedSource;
		set
		{
			if (!SetField(ref selectedSource, value)) return;
			OnPropertyChanged(nameof(CanAddMapping));
			AddMappingCommand.RaiseCanExecuteChanged();
		}
	}

	public bool CanAddMapping => !IsBusy
		&& SelectedTarget is not null
		&& SelectedSource is not null
		&& SelectedTarget.ItemId != SelectedSource.ItemId
		&& Mappings.All(row => row.Target.ItemId != SelectedTarget.ItemId);

	public AsyncRelayCommand RefreshCommand { get; }
	public AsyncRelayCommand ApplyCommand { get; }
	public RelayCommand AddMappingCommand { get; }
	public RelayCommand RemoveMappingCommand { get; }
	public RelayCommand OpenRegistryFolderCommand { get; }

	public Task InitializeAsync() => RefreshAsync();

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
			var choices = catalog
				.Select(item => new RuntimeArmorChoiceViewModel(item.ItemId, item.ArchiveId, item.DisplayName))
				.ToDictionary(item => item.ItemId);
			foreach (var mapping in snapshot.Document.Mappings)
			{
				if (!choices.ContainsKey(mapping.TargetItemId))
					choices[mapping.TargetItemId] = RuntimeArmorChoiceViewModel.Unknown(mapping.TargetItemId);
				if (!choices.ContainsKey(mapping.SourceItemId))
					choices[mapping.SourceItemId] = RuntimeArmorChoiceViewModel.Unknown(mapping.SourceItemId);
			}

			ArmorChoices.Clear();
			foreach (var choice in choices.Values
				.OrderBy(item => item.IsUnknown)
				.ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(item => item.ArchiveId, StringComparer.OrdinalIgnoreCase))
			{
				ArmorChoices.Add(choice);
			}

			Mappings.Clear();
			foreach (var mapping in snapshot.Document.Mappings)
			{
				Mappings.Add(new RuntimeArmorMappingRowViewModel(
					choices[mapping.TargetItemId],
					choices[mapping.SourceItemId]));
			}
			enabled = snapshot.Document.Enabled;
			OnPropertyChanged(nameof(Enabled));
			OnPropertyChanged(nameof(HasMappings));
			fingerprint = snapshot.Fingerprint;
			RevisionText = $"revision {snapshot.Document.Revision} · {Mappings.Count} 条映射";
			UpdateStatus(snapshot);
			SelectedTarget = null;
			SelectedSource = null;
			HasChanges = false;
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

	private void AddMapping()
	{
		if (!CanAddMapping || SelectedTarget is null || SelectedSource is null) return;
		Mappings.Add(new RuntimeArmorMappingRowViewModel(SelectedTarget, SelectedSource));
		SelectedTarget = null;
		SelectedSource = null;
		OnPropertyChanged(nameof(HasMappings));
		MarkChanged();
	}

	private void RemoveMapping(object? value)
	{
		if (value is not RuntimeArmorMappingRowViewModel row || !Mappings.Remove(row)) return;
		OnPropertyChanged(nameof(HasMappings));
		MarkChanged();
	}

	private async Task ApplyAsync()
	{
		if (IsBusy || !HasChanges) return;
		IsBusy = true;
		try
		{
			var desired = Mappings
				.Select(row => new RuntimeArmorRegistryMapping(row.Target.ItemId, row.Source.ItemId))
				.ToArray();
			var result = await service.ApplyAsync(fingerprint, Enabled, desired);
			fingerprint = result.Fingerprint;
			RevisionText = $"revision {result.Document.Revision} · {desired.Length} 条映射";
			HasChanges = false;

			var snapshot = await WaitForRuntimeAsync(result.Document.Revision);
			UpdateStatus(snapshot);
			if (snapshot.RuntimeAcknowledged)
			{
				notifications.Show(
					$"护甲外观注册已在线应用：revision {result.Document.Revision}。",
					NotificationLevel.Info,
					TimeSpan.FromSeconds(6),
					"护甲注册",
					"armor-registry-apply");
			}
			else if (snapshot.RuntimeProcessRunning)
			{
				notifications.Show(
					"协议已保存，但游戏运行时尚未确认；请稍后刷新状态。",
					NotificationLevel.Warning,
					TimeSpan.FromSeconds(8),
					"护甲注册",
					"armor-registry-apply");
			}
			else
			{
				notifications.Show(
					"协议已保存，将在下次启动游戏时应用。",
					NotificationLevel.Info,
					TimeSpan.FromSeconds(6),
					"护甲注册",
					"armor-registry-apply");
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
		if (!snapshot.RuntimeProcessRunning || snapshot.RuntimeAcknowledged) return snapshot;
		for (var attempt = 0; attempt < 8; attempt++)
		{
			await Task.Delay(250);
			snapshot = await service.ReadSnapshotAsync();
			if (!snapshot.RuntimeProcessRunning
				|| snapshot.RuntimeAcknowledged
				|| snapshot.Document.Revision != revision)
				break;
		}
		return snapshot;
	}

	private void UpdateStatus(RuntimeArmorRegistrySnapshot snapshot)
	{
		if (snapshot.RuntimeAcknowledged)
		{
			StatusTitle = "游戏已同步";
			StatusDetail = $"运行中的游戏已确认 revision {snapshot.Document.Revision}。";
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
				? "配置已保存在外置协议中，启动游戏后会自动定位并应用。"
				: "添加映射并应用后将创建外置协议。";
		}
	}

	private void MarkChanged()
	{
		HasChanges = true;
		OnPropertyChanged(nameof(CanAddMapping));
		AddMappingCommand.RaiseCanExecuteChanged();
	}

	private void RaiseCommandStates()
	{
		RefreshCommand.RaiseCanExecuteChanged();
		ApplyCommand.RaiseCanExecuteChanged();
		AddMappingCommand.RaiseCanExecuteChanged();
		RemoveMappingCommand.RaiseCanExecuteChanged();
		OnPropertyChanged(nameof(CanAddMapping));
	}

	private void OpenRegistryFolder()
	{
		Directory.CreateDirectory(service.RegistryRoot);
		Process.Start(new ProcessStartInfo(service.RegistryRoot) { UseShellExecute = true });
	}
}

public sealed record RuntimeArmorChoiceViewModel(
	uint ItemId,
	string ArchiveId,
	string DisplayName,
	bool IsUnknown = false)
{
	public string Display => $"{DisplayName} · {ArchiveId}";
	public string ItemIdText => ItemId.ToString();

	public static RuntimeArmorChoiceViewModel Unknown(uint itemId)
		=> new(itemId, itemId.ToString(), $"未知护甲 {itemId}", true);
}

public sealed record RuntimeArmorMappingRowViewModel(
	RuntimeArmorChoiceViewModel Target,
	RuntimeArmorChoiceViewModel Source);
