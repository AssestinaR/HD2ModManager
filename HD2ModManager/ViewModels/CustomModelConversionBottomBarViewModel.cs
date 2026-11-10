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
		// 「生成」不设 canExecute：按钮永远可点，"名称为空 / 没选来源 / 正在跑"都由 GenerateAsync 当场说清。
		GenerateCommand = new AsyncRelayCommand(_ => GenerateAsync());
	}

	public string Name { get => name; set => SetField(ref name, value); }

	/// <summary>中间产物目录。<b>不暴露给用户选</b>——生成完必定导入库，没有"只导出"这条路。</summary>
	public string OutputDirectory { get; }

	public string Status { get => status; set => SetField(ref status, value); }
	public bool IsRunning { get => isRunning; private set { SetField(ref isRunning, value); OnPropertyChanged(nameof(CanEdit)); } }
	public bool CanEdit => !IsRunning;
	public BulkObservableCollection<CustomModelSourceOption> ArmorSources { get; } = [];
	public BulkObservableCollection<CustomModelSourceOption> HelmetSources { get; } = [];
	public CustomModelSourceOption? SelectedArmor { get => selectedArmor; set => SetField(ref selectedArmor, value); }
	public CustomModelSourceOption? SelectedHelmet { get => selectedHelmet; set => SetField(ref selectedHelmet, value); }
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
		Status = available.Length == 0
			// 这条提示会命中"已经是自定义素体"的 Mod —— 而那正是库里绝大多数。
			// 只写"无效"等于让用户对着一个禁用的按钮猜原因，所以把判据和出路都写出来。
			? "这个 Mod 没有可用的来源：只有「取代原版护甲/头盔」的 Mod 才有独占 Unit 可当来源。"
				+ "已经是自定义素体的 Mod 换不出东西来 —— 它不需要这一步。"
			: "可选一份护甲和一份头盔；不需要的类别请选择「不包含」";
	}

	private async Task GenerateAsync()
	{
		// 三种"不能生成"在这里说清；用户点了就必须知道为什么没动静。
		if (IsRunning)
		{
			Status = "正在生成，完成前不会开始第二次。";
			return;
		}

		if (string.IsNullOrWhiteSpace(Name))
		{
			Status = "先给新条目起个名字（会作为库里的 Mod 名），然后再点「生成」。";
			return;
		}

		if (SelectedSources.Count == 0)
		{
			Status = "还没有选来源：护甲、头盔至少选一份（两份都要时各选一份）。";
			return;
		}

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
