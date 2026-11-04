using HD2ModCore.Domain;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

public sealed class CustomModelConversionBottomBarViewModel : BaseViewModel
{
	private readonly Func<CustomModelConversionBottomBarViewModel, Task> generate;
	private string name;
	private string status = "加载中…正在检查护甲来源";
	private bool isRunning;
	private CustomModelSourceOption? selectedArmor;
	private CustomModelSourceOption? selectedHelmet;

	public CustomModelConversionBottomBarViewModel(string name, Func<CustomModelConversionBottomBarViewModel, Task> generate)
	{
		this.name = name;
		this.generate = generate;
		// 生成目录不再让用户选：产物一定是"生成完立刻导入库"，中间目录只是过路。
		// 留一个固定值给它，是为了让生成流程有个干净的落点（导入完它会跟着 Mod 目录走）。
		OutputDirectory = SettingsService.GetResolvedDefaultExportPath();
		GenerateCommand = new AsyncRelayCommand(_ => GenerateAsync(), _ => !IsRunning && SelectedSources.Count > 0
			&& !string.IsNullOrWhiteSpace(Name));
	}

	public string Name { get => name; set { if (SetField(ref name, value)) GenerateCommand.RaiseCanExecuteChanged(); } }

	/// <summary>中间产物目录。<b>不暴露给用户选</b>——生成完必定导入库，没有"只导出"这条路。</summary>
	public string OutputDirectory { get; }

	public string Status { get => status; set => SetField(ref status, value); }
	public bool IsRunning { get => isRunning; private set { SetField(ref isRunning, value); GenerateCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CanEdit)); } }
	public bool CanEdit => !IsRunning;
	public BulkObservableCollection<CustomModelSourceOption> ArmorSources { get; } = [];
	public BulkObservableCollection<CustomModelSourceOption> HelmetSources { get; } = [];
	public CustomModelSourceOption? SelectedArmor { get => selectedArmor; set { if (SetField(ref selectedArmor, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public CustomModelSourceOption? SelectedHelmet { get => selectedHelmet; set { if (SetField(ref selectedHelmet, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
	public IReadOnlyList<EquipmentUnitCatalogEntry> SelectedSources
		=> new[] { SelectedArmor?.Source, SelectedHelmet?.Source }.OfType<EquipmentUnitCatalogEntry>().ToArray();
	public AsyncRelayCommand GenerateCommand { get; }

	public void SetSources(IEnumerable<EquipmentUnitCatalogEntry> sources)
	{
		var available = sources.ToArray();
		ArmorSources.ReplaceWith(new[] { new CustomModelSourceOption(null) }.Concat(available
			.Where(s => s.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase)).Select(s => new CustomModelSourceOption(s))), ListTransitionKind.Replace);
		HelmetSources.ReplaceWith(new[] { new CustomModelSourceOption(null) }.Concat(available
			.Where(s => s.Category.Equals("Helmet", StringComparison.OrdinalIgnoreCase)).Select(s => new CustomModelSourceOption(s))), ListTransitionKind.Replace);
		SelectedArmor = ArmorSources.Skip(1).FirstOrDefault() ?? ArmorSources[0];
		SelectedHelmet = HelmetSources.Skip(1).FirstOrDefault() ?? HelmetSources[0];
		Status = available.Length == 0 ? "无效：没有含独占 Unit 的可见护甲/头盔来源" : "可选一份护甲和一份头盔；不需要的类别请选择「不包含」";
	}

	private async Task GenerateAsync()
	{
		IsRunning = true;
		try { await generate(this); }
		catch (OperationCanceledException) { Status = "已取消生成；如已生成或登记素体，将保留已完成的结果。"; }
		catch (Exception exception) { Status = "生成失败：" + exception.Message; LogService.Error(exception.ToString()); }
		finally { IsRunning = false; }
	}
}

public sealed record CustomModelSourceOption(EquipmentUnitCatalogEntry? Source)
{
	public string DisplayName => Source?.DisplayName ?? "不包含";
}

/// <summary>
/// 底栏的两行：<c>Main</c> 是输入行（名称 / 护甲 / 头盔 / 生成），<c>Status</c> 是状态行。
/// <para>
/// 之前这里除了输入行还注册了一行"输出位置 + 浏览 + 生成 + 状态"，而状态那一行
/// <c>MaxWidth=800</c> 会折行 —— 于是两行宿主里塞了三行内容。生成目录本来就不该让用户选
/// （产物必定导入库），所以那一行整条去掉，状态单独占第二行。
/// </para>
/// </summary>
public sealed record CustomModelConversionBottomBarRow(CustomModelConversionBottomBarViewModel Operation, bool IsStatusRow)
{
	public bool IsMain => !IsStatusRow;
}
