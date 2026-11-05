using System.Globalization;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

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
		Func<VpkRebuildBottomBarViewModel, Task> start,
		Action close)
	{
		ModName = modName ?? string.Empty;
		DescendantCount = descendantCount;
		this.start = start ?? throw new ArgumentNullException(nameof(start));
		this.close = close ?? throw new ArgumentNullException(nameof(close));
		// 没有子级时"含子级"没有意义，直接锁死为"仅自己"。
		includeDescendants = descendantCount != 0;
		status = descendantCount == 0
			? "将用 Mod 文件夹里那份来源 VPK 重新转换：只重出这一个 Mod，保住目标护甲与 section 勾选。"
			: $"将用 Mod 文件夹里那份来源 VPK 重新转换：保住目标护甲与 section 勾选。另有 {descendantCount} 个子级也是转换产物，可选择是否一起重出。";
		StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning && HasValidConcurrency);
		CancelCommand = new RelayCommand(_ => OnCancelOrClose());
	}

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

	public bool CanEditConcurrency => !IsRunning && !IsFinished;

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
		if (IsRunning) return;
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
