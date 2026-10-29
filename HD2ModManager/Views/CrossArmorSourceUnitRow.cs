using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

/// <summary>
/// 替换护甲页左下角"来源部件"表的一行。
/// </summary>
/// <remarks>
/// 为什么要单独一张表：来源 Mod 到底带了哪些 unit，从 Patch TOC 就能立刻读出来；
/// 而"这个 unit 到底是真几何还是极小化占位"必须逐个解析网格（秒级）。
/// 所以先把全部 unit 列出来（部位/层/身形来自注册表格子，也是查库即得），
/// 可见性再后台补——页面因此能瞬间打开。
///
/// 「作为来源」是**用户意图**，不是事实：计算只给它一个默认值，用户点一下就覆盖，
/// 重算时保留用户的覆盖（否则每次重测都会把手工选择抹掉）。
/// </remarks>
public sealed class CrossArmorSourceUnitRow : BaseViewModel
{
	private bool isEnabled;
	private bool hasUserOverride;
	private bool isScanned;
	private string statusText = "待计算";

	public CrossArmorSourceUnitRow(AssetKey unitAssetKey, ArmorCell cell, UnitMeshBodyVariant body)
	{
		UnitAssetKey = unitAssetKey;
		Cell = cell;
		Body = body;
	}

	public AssetKey UnitAssetKey { get; }
	public ArmorCell Cell { get; }
	public UnitMeshBodyVariant Body { get; }

	public string PartText => Cell.IsKnown ? ArmorStructure.Display(Cell.Part) : "未定位";
	public string LayerText => Cell.IsKnown ? ArmorStructure.DisplayPieceSlot((uint)Cell.Layer) : "—";
	public string BodyText => Body switch
	{
		UnitMeshBodyVariant.Slim => "纤细",
		UnitMeshBodyVariant.Stocky => "壮硕",
		UnitMeshBodyVariant.Any => "通用",
		_ => "—",
	};
	public string IdText => $"0x{UnitAssetKey.FileId:x16}";

	/// <summary>披风不参与替换护甲链路（渲染路径不同），永远不能作为来源。</summary>
	public bool IsCape => Cell.IsCape;

	/// <summary>注册表里查不到格子的 unit：只能靠手动映射，不参与自动装箱。</summary>
	public bool IsUnplaced => !Cell.IsKnown;

	/// <summary>用户意图。点击切换。</summary>
	public bool IsEnabled
	{
		get => isEnabled;
		private set
		{
			if (!SetField(ref isEnabled, value)) return;
			OnPropertyChanged(nameof(EnabledText));
		}
	}

	/// <summary>用户是否手动改过这一行。重测只改没有覆盖过的行。</summary>
	public bool HasUserOverride
	{
		get => hasUserOverride;
		private set => SetField(ref hasUserOverride, value);
	}

	public string StatusText { get => statusText; private set => SetField(ref statusText, value); }

	/// <summary>这一行有没有拿到结论（不论结论是"可转移"还是"不可转移"）。</summary>
	public bool IsScanned => isScanned;

	/// <summary>
	/// 这一格的显示文字。全部是单字符或两字短词，避免不同状态宽度不同把行高顶变。
	/// </summary>
	public string EnabledText => IsCape
		? "披风"
		: !isScanned
			? "…"
			: IsEnabled ? "✓" : "x";

	public bool CanToggle => !IsCape && !IsUnplaced;

	public void Toggle()
	{
		if (!CanToggle) return;
		IsEnabled = !IsEnabled;
		HasUserOverride = true;
	}

	/// <summary>重测开始：回到"计算中"。</summary>
	public void BeginScan()
	{
		isScanned = false;
		StatusText = "计算中";
		OnPropertyChanged(nameof(EnabledText));
	}

	/// <summary>
	/// 重测结果。没有手动覆盖过的行跟着事实走；手动覆盖过的行保留用户选择，
	/// 但状态文字仍然照实显示——用户要能看见"我强行开了一个不可转移的部件"。
	/// </summary>
	public void ApplyScan(bool transferable, string status)
	{
		isScanned = true;
		StatusText = status;
		if (!HasUserOverride) IsEnabled = transferable && !IsCape && !IsUnplaced;
		OnPropertyChanged(nameof(EnabledText));
	}

	public void MarkUnscanned(string status)
	{
		isScanned = false;
		StatusText = status;
		IsEnabled = false;
		OnPropertyChanged(nameof(EnabledText));
	}
}
