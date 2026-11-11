using System.Globalization;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>重构建的来源形态。**两档 sectional 系列只是来源不同**，动作名与结果一样。</summary>
public enum VpkRebuildSourceKind
{
	/// <summary>根上有来源 <c>.vpk</c>：交给转换器重跑。</summary>
	SourceVpk,

	/// <summary>根上有来源 patch + <c>sectional-source.json</c>：走 legacy2sectional。</summary>
	SectionalSource,
}

/// <summary>
/// 「用来源重构建」的底栏操作：选范围（仅自己 / 含子级）+ 并发数，再开跑。
/// <para>
/// <b>为什么从对话框改成底栏。</b>原来点一下要过两道 MessageBox（确认、要不要连子级），
/// 而"连不连子级"这件事本身就是一次选择，用底栏表达能同时把<b>并发数</b>也摆出来 ——
/// 子级一多，逐个跑要等很久，而并发直接决定内存与磁盘占用，这两件事该一起决定。
/// </para>
/// <para>
/// 状态机与全量重构那边一致：<b>待开始</b>（可改范围与并发、可取消）→ <b>重构建中</b>
/// （冻结输入、显示进度、可取消）→ <b>已结束</b>（显示结论，按钮变"关闭"）。
/// 结束后允许再点一次：有失败项时用户的第一反应就是再来一遍。
/// </para>
/// </summary>
public sealed class VpkRebuildBottomBarViewModel : BaseViewModel
{
	private readonly Func<VpkRebuildBottomBarViewModel, Task> start;
	private readonly Action close;
	private string concurrencyText = VpkBulkRebuildService.DefaultConcurrency.ToString(CultureInfo.InvariantCulture);
	private bool includeDescendants = true;
	private string status;
	private bool isRunning;
	private bool isFinished;
	private double progress;

	public VpkRebuildBottomBarViewModel(
		string modName,
		int descendantCount,
		VpkRebuildSourceKind sourceKind,
		Func<VpkRebuildBottomBarViewModel, Task> start,
		Action close)
	{
		ModName = modName ?? string.Empty;
		DescendantCount = descendantCount;
		SourceKind = sourceKind;
		this.start = start ?? throw new ArgumentNullException(nameof(start));
		this.close = close ?? throw new ArgumentNullException(nameof(close));
		// 没有子级时"含子级"没有意义，直接锁死为"仅自己"。
		includeDescendants = descendantCount != 0;
		status = sourceKind == VpkRebuildSourceKind.SectionalSource
			// sectional 档的来源是"根上的来源 patch"，范围已经记在依据文件里，
			// 所以这里不能再说"来源 VPK"，也不提供"含子级"（子级本来就是来源的一部分）。
			? "将用 Mod 根上那份来源 patch 重新转换（范围取自 sectional-source.json 记录的护甲/头盔）：保住目标护甲与 section 勾选。"
			: descendantCount == 0
				? "将用 Mod 文件夹里那份来源 VPK 重新转换：只重出这一个 Mod，保住目标护甲与 section 勾选。"
				: $"将用 Mod 文件夹里那份来源 VPK 重新转换：保住目标护甲与 section 勾选。另有 {descendantCount} 个子级也是转换产物，可选择是否一起重出。";
		// 不挂 canExecute：契约 §2.2 里没有"禁用"这一态，按钮永远可点。
		// "这次能不能跑"由 StartAsync 当场判一次，判不过就写进本栏已有的状态行。
		StartCommand = new RelayCommand(async _ => await StartAsync());
		CancelCommand = new RelayCommand(_ => OnCancelOrClose());
	}

	/// <summary>这次重构建的来源形态（决定跑哪条转换机）。</summary>
	public VpkRebuildSourceKind SourceKind { get; }

	public string ModName { get; }

	/// <summary>可以作为"子级"一起重构建的节点数（它们也是 VPK 转换产物）。</summary>
	public int DescendantCount { get; }

	public bool HasDescendants => DescendantCount != 0;

	public string DefaultHint
		=> $"默认 {VpkBulkRebuildService.DefaultConcurrency} 个，可设 {VpkBulkRebuildService.MinConcurrency}–{VpkBulkRebuildService.MaxConcurrency} 个；数字越大越快也越吃内存。";

	public RelayCommand StartCommand { get; }
	public RelayCommand CancelCommand { get; }

	/// <summary>
	/// 是否连子级一起重构建。
	/// <para>
	/// 默认开：主体换了、子级还是老产物，是那种"看起来做完了但游戏里对不上"的情况。
	/// 但用户完全可能只想换主体，所以给开关而不是替他决定。
	/// </para>
	/// </summary>
	public bool IncludeDescendants
	{
		get => includeDescendants;
		set
		{
			if (!HasDescendants) return;
			if (!SetField(ref includeDescendants, value)) return;
			OnPropertyChanged(nameof(ScopeText));
		}
	}

	public string ScopeText => !HasDescendants
		? "仅这一个 Mod"
		: IncludeDescendants
			? $"这一个 Mod + {DescendantCount} 个子级"
			: "仅这一个 Mod";

	/// <summary>
	/// 并发数的**文本**。用字符串而不是 int：绑定到文本框时，"清空再输入 5"这种中间态必须允许，
	/// 否则删到空就会立刻被写回一个数字，用户没法改。
	/// </summary>
	public string ConcurrencyText
	{
		get => concurrencyText;
		set
		{
			var sanitized = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
			if (sanitized.Length > 3) sanitized = sanitized[..3];
			// 超范围直接夹到边界：比留着一个"待会儿会报错"的值友好。
			if (int.TryParse(sanitized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			{
				if (parsed > VpkBulkRebuildService.MaxConcurrency) sanitized = VpkBulkRebuildService.MaxConcurrency.ToString(CultureInfo.InvariantCulture);
				else if (parsed < VpkBulkRebuildService.MinConcurrency) sanitized = VpkBulkRebuildService.MinConcurrency.ToString(CultureInfo.InvariantCulture);
			}

			if (!SetField(ref concurrencyText, sanitized)) return;
			OnPropertyChanged(nameof(HasValidConcurrency));
			OnPropertyChanged(nameof(CanEditConcurrency));
			StartCommand.RaiseCanExecuteChanged();
		}
	}

	public bool HasValidConcurrency
		=> int.TryParse(concurrencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			&& parsed >= VpkBulkRebuildService.MinConcurrency
			&& parsed <= VpkBulkRebuildService.MaxConcurrency;

	public int Concurrency => VpkBulkRebuildService.NormalizeConcurrency(
		int.TryParse(concurrencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: VpkBulkRebuildService.DefaultConcurrency);

	// 只看 IsRunning：跑完之后按钮写的是"再跑一遍"，那时并发数必须还能改 ——
	// 否则"再跑一遍"只能原样重放上一次的并发，想换个数就得先把底栏关掉再开一次。
	public bool CanEditConcurrency => !IsRunning;

	public string Status { get => status; set => SetField(ref status, value); }

	public bool IsRunning
	{
		get => isRunning;
		private set
		{
			if (!SetField(ref isRunning, value)) return;
			OnPropertyChanged(nameof(CanEditConcurrency));
			OnPropertyChanged(nameof(StartText));
			StartCommand.RaiseCanExecuteChanged();
		}
	}

	public bool IsFinished
	{
		get => isFinished;
		private set
		{
			if (!SetField(ref isFinished, value)) return;
			OnPropertyChanged(nameof(CanEditConcurrency));
			OnPropertyChanged(nameof(StartText));
			OnPropertyChanged(nameof(CancelText));
			StartCommand.RaiseCanExecuteChanged();
		}
	}

	public double Progress
	{
		get => progress;
		set
		{
			if (!SetField(ref progress, value)) return;
			OnPropertyChanged(nameof(HasProgress));
		}
	}

	public bool HasProgress => progress > 0d;

	public string StartText => IsFinished ? "再跑一遍" : "开始重构建";
	public string CancelText => IsFinished ? "关闭" : "取消";

	private async Task StartAsync()
	{
		// 按钮永远可点，所以两个"不能跑"的判断都挪到这里：判不过就把原因与下一步写进
		// 本栏已有的状态行，不做静默 return —— 点了没反应和灰着不知道为啥是同一类毛病。
		if (IsRunning)
		{
			Status = "正在重构建，等这一轮跑完再点；要中途停下就点「取消」。";
			return;
		}

		if (!HasValidConcurrency)
		{
			// 输入框只放行数字、越界会被夹到边界，所以真正能漏过去的只有"被清空"这一种；
			// 范围与当前值仍然一起说出来，用户才知道填什么就能继续。
			var current = string.IsNullOrWhiteSpace(concurrencyText) ? "（空）" : concurrencyText;
			Status = $"并发数量必须是 {VpkBulkRebuildService.MinConcurrency}–{VpkBulkRebuildService.MaxConcurrency} 之间的整数，现在填的是「{current}」——填好之后再点这个按钮。";
			return;
		}

		// 重跑时把"已结束"退回去：否则这一轮里「关闭」会一直挂在取消上，文本承诺了另一个分支。
		IsFinished = false;
		IsRunning = true;
		Progress = 0d;
		try
		{
			await start(this);
			IsFinished = true;
		}
		catch (OperationCanceledException)
		{
			Status = "已取消；已经重建完的部分保留。";
			IsFinished = true;
		}
		catch (Exception exception)
		{
			Status = "重构建失败：" + exception.Message;
			LogService.Error(exception.ToString());
			IsFinished = true;
		}
		finally
		{
			IsRunning = false;
		}
	}

	private void OnCancelOrClose()
	{
		if (!IsRunning)
		{
			close();
			return;
		}

		// 运行中：让外部那个 CancellationToken 生效（start 的回调持有它）。
		CancelRequested?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>运行中点「取消」时触发；由持有 CancellationTokenSource 的一方接住。</summary>
	public event EventHandler? CancelRequested;
}

/// <summary>底栏两行：<c>Options</c> 是"选范围与并发 + 开始"，<c>Progress</c> 是进度与结论。</summary>
public sealed record VpkRebuildBottomBarRow(VpkRebuildBottomBarViewModel Operation, bool IsProgressRow)
{
	public bool IsOptions => !IsProgressRow;
}
