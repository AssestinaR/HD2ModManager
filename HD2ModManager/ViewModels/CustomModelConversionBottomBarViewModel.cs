using HD2ModCore.Domain;
using HD2ModManager.Services;
using Microsoft.Win32;

namespace HD2ModManager.ViewModels;

public sealed class CustomModelConversionBottomBarViewModel : BaseViewModel
{
	private readonly Func<CustomModelConversionBottomBarViewModel, Task> generate;
	private string name;
	private string outputDirectory = SettingsService.GetResolvedDefaultExportPath();
	private string status = "加载中…正在检查护甲来源";
	private bool isRunning;
	private CustomModelSourceOption? selectedArmor;
	private CustomModelSourceOption? selectedHelmet;
	public CustomModelConversionBottomBarViewModel(string name, Func<CustomModelConversionBottomBarViewModel, Task> generate)
	{
		this.name = name;
		this.generate = generate;
		GenerateCommand = new AsyncRelayCommand(_ => GenerateAsync(), _ => !IsRunning && SelectedSources.Count > 0
			&& !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(OutputDirectory));
		BrowseCommand = new RelayCommand(_ =>
		{
			var dialog = new OpenFolderDialog { Title = "选择自定义素体输出目录", InitialDirectory = OutputDirectory };
			if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
		});
	}
	public string Name { get => name; set { if (SetField(ref name, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public string OutputDirectory { get => outputDirectory; set { if (SetField(ref outputDirectory, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public string Status { get => status; set => SetField(ref status, value); }
	private bool importToLibrary;
	public bool ImportToLibrary { get => importToLibrary; set => SetField(ref importToLibrary, value); }
	public bool IsRunning { get => isRunning; private set { SetField(ref isRunning, value); GenerateCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CanEdit)); } }
	public bool CanEdit => !IsRunning;
	public BulkObservableCollection<CustomModelSourceOption> ArmorSources { get; } = [];
	public BulkObservableCollection<CustomModelSourceOption> HelmetSources { get; } = [];
	public CustomModelSourceOption? SelectedArmor { get => selectedArmor; set { if (SetField(ref selectedArmor, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public CustomModelSourceOption? SelectedHelmet { get => selectedHelmet; set { if (SetField(ref selectedHelmet, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public IReadOnlyList<EquipmentUnitCatalogEntry> SelectedSources
		=> new[] { SelectedArmor?.Source, SelectedHelmet?.Source }.OfType<EquipmentUnitCatalogEntry>().ToArray();
	public AsyncRelayCommand GenerateCommand { get; }
	public RelayCommand BrowseCommand { get; }
	public void SetSources(IEnumerable<EquipmentUnitCatalogEntry> sources)
	{
		var available = sources.ToArray();
		ArmorSources.ReplaceWith(new[] { new CustomModelSourceOption(null) }.Concat(available
			.Where(s => s.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase)).Select(s => new CustomModelSourceOption(s))), ListTransitionKind.Replace);
		HelmetSources.ReplaceWith(new[] { new CustomModelSourceOption(null) }.Concat(available
			.Where(s => s.Category.Equals("Helmet", StringComparison.OrdinalIgnoreCase)).Select(s => new CustomModelSourceOption(s))), ListTransitionKind.Replace);
		SelectedArmor = ArmorSources.Skip(1).FirstOrDefault() ?? ArmorSources[0];
		SelectedHelmet = HelmetSources.Skip(1).FirstOrDefault() ?? HelmetSources[0];
		Status = available.Length == 0 ? "无效：没有含独占 Unit 的可见护甲/头盔来源" : "可选一份护甲和一份头盔；不需要的类别请选择「不包含」· 原 Mod 不会被修改";
	}
	private async Task GenerateAsync()
	{
		IsRunning = true;
		try { await generate(this); }
		catch (OperationCanceledException) { Status = "已取消生成；如已生成输出或登记素体，将保留已完成的结果。"; }
		catch (Exception exception) { Status = "生成失败：" + exception.Message; LogService.Error(exception.ToString()); }
		finally { IsRunning = false; }
	}
}

public sealed record CustomModelSourceOption(EquipmentUnitCatalogEntry? Source)
{
	public string DisplayName => Source?.DisplayName ?? "不包含";
}

public sealed record CustomModelConversionBottomBarRow(CustomModelConversionBottomBarViewModel Operation, bool IsOutput)
{
	public bool IsOptions => !IsOutput;
}
