using System.Globalization;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 「一键重构所有 VPK 类模组」的底栏操作。
/// <para>
/// <b>为什么点菜单不是直接就开跑。</b>这批活的代价是"每个 Mod 几分钟 × 一百多个"，
/// 而并发数又直接决定内存与磁盘的占用 —— 这两件事都该由用户在开始前决定一次，
/// 而不是让他点完发现电脑卡死再去找哪里能停。
/// </para>
/// <para>
/// 状态机只有三段：<b>待开始</b>（可改并发、可取消＝关掉底栏）→ <b>重构中</b>（并发冻结、
/// 显示进度、可取消）→ <b>已结束</b>（显示结论、按钮变成"关闭"）。
/// 结束后允许再点一次：上一次有失败项时，用户的第一反应就是再来一遍。
/// </para>
/// </summary>
public sealed class VpkBulkRebuildBottomBarViewModel : BaseViewModel
{
	private readonly Func<VpkBulkRebuildBottomBarViewModel, Task> start;
	private readonly Action close;
	private CancellationTokenSource? cancellation;
	private string concurrencyText = VpkBulkRebuildService.DefaultConcurrency.ToString(CultureInfo.InvariantCulture);
	private string status;
	private bool isRunning;
	private bool isFinished;
	private double progress;

	public VpkBulkRebuildBottomBarViewModel(
		string title,
		string origin,
		int totalCount,
		Func<VpkBulkRebuildBottomBarViewModel, Task> start,
		Action close)
	{
		Title = string.IsNullOrWhiteSpace(title) ? "批量重构" : title;
		Origin = string.IsNullOrWhiteSpace(origin) ? "批量重构" : origin;
		TotalCount = totalCount;
		this.start = start ?? throw new ArgumentNullException(nameof(start));
		this.close = close ?? throw new ArgumentNullException(nameof(close));
		status = $"将重构 {totalCount} 个 sectional 系列 Mod：各自按自己记着的配置（目标护甲与 section 勾选）重出一遍产物。";
		StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsRunning && HasValidConcurrency);
		CancelCommand = new RelayCommand(_ => OnCancelOrClose());
	}

	/// <summary>这次批量重构叫什么（底栏标题、后台任务标题都用它）。</summary>
	public string Title { get; }

	/// <summary>从哪个入口发起（消息中心与日志的来源）。</summary>
	public string Origin { get; }

	public int TotalCount { get; }
	public string DefaultHint => $"默认 {VpkBulkRebuildService.DefaultConcurrency} 个，可设 {VpkBulkRebuildService.MinConcurrency}–{VpkBulkRebuildService.MaxConcurrency} 个；数字越大越快也越吃内存。";

	public RelayCommand StartCommand { get; }
	public RelayCommand CancelCommand { get; }

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
			// 超范围直接夹到边界：比留着一个"待会儿会报错"的值友好，也和底栏里处理位置编号的做法一致。
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

	public bool HasValidConcurrency => int.TryParse(ConcurrencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
		&& value >= VpkBulkRebuildService.MinConcurrency
		&& value <= VpkBulkRebuildService.MaxConcurrency;

	/// <summary>实际使用的并发数：填了非法值就退回默认，而不是让整批跑不起来。</summary>
	public int Concurrency => HasValidConcurrency
		? VpkBulkRebuildService.NormalizeConcurrency(int.Parse(ConcurrencyText, CultureInfo.InvariantCulture))
		: VpkBulkRebuildService.DefaultConcurrency;

	public bool CanEditConcurrency => !IsRunning;

	public string Status { get => status; private set => SetField(ref status, value); }

	public bool IsRunning
	{
		get => isRunning;
		private set
		{
			if (!SetField(ref isRunning, value)) return;
			OnPropertyChanged(nameof(CanEditConcurrency));
			OnPropertyChanged(nameof(StartText));
			OnPropertyChanged(nameof(CancelText));
			StartCommand.RaiseCanExecuteChanged();
		}
	}

	public bool IsFinished
	{
		get => isFinished;
		private set
		{
			if (!SetField(ref isFinished, value)) return;
			OnPropertyChanged(nameof(StartText));
			OnPropertyChanged(nameof(CancelText));
			StartCommand.RaiseCanExecuteChanged();
		}
	}

	public double Progress { get => progress; private set { if (SetField(ref progress, value)) OnPropertyChanged(nameof(HasProgress)); } }
	public bool HasProgress => progress > 0d;

	public string StartText => IsRunning ? "重构中…" : IsFinished ? "再跑一遍" : "开始重构";
	public string CancelText => IsRunning ? "取消" : "关闭";

	/// <summary>进行中用这个 token 取消（底栏按钮与消息中心都能取消）。</summary>
	public CancellationToken Token => cancellation?.Token ?? CancellationToken.None;

	private async Task StartAsync()
	{
		if (IsRunning || !HasValidConcurrency) return;
		cancellation?.Dispose();
		cancellation = new CancellationTokenSource();
		IsFinished = false;
		IsRunning = true;
		Progress = 0d;
		Status = $"正在重构建 {TotalCount} 个 VPK 类模组（并发 {Concurrency}）…";
		try
		{
			await start(this);
		}
		finally
		{
			IsRunning = false;
		}
	}

	public void ReportStage(string stage) => Status = stage;

	public void ReportItemFinished(int done, int total, string name)
	{
		if (total > 0) Progress = (double)done / total;
		Status = $"已完成 {done}/{total}：{name}";
	}

	/// <summary>批量结束：结论放这里，逐条细节走消息中心的报告。</summary>
	public void ReportFinished(string summary, bool canceled)
	{
		IsFinished = true;
		Progress = canceled && Progress < 1d ? Progress : 1d;
		Status = summary;
	}

	public void RequestCancel()
	{
		if (!IsRunning) return;
		Status = "正在取消…（已经在转换的那几个会跑完或自行退出，未开始的不会开始）";
		try { cancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private void OnCancelOrClose()
	{
		if (IsRunning)
		{
			RequestCancel();
			return;
		}

		close();
	}
}

public enum VpkBulkRebuildBottomBarRowKind
{
	Options,
	Progress,
}

public sealed record VpkBulkRebuildBottomBarRowViewModel(
	VpkBulkRebuildBottomBarViewModel Operation,
	VpkBulkRebuildBottomBarRowKind Kind)
{
	public bool IsOptions => Kind == VpkBulkRebuildBottomBarRowKind.Options;
	public bool IsProgress => Kind == VpkBulkRebuildBottomBarRowKind.Progress;
}
