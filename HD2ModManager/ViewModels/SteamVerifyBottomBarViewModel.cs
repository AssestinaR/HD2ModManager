using System.Windows.Threading;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 底栏上的"Steam 校验后续操作"提示条：一行说明 + 一个主按钮 + 可选取消，带倒计时自动执行。
///
/// 两个用途：
///   1. 校验结束后提醒用户刷新资产索引状态（60 秒后自动刷新）；
///   2. 刷新后若状态过期，询问是否重建（10 秒后自动重建，可取消）。
///
/// 倒计时走 <see cref="DispatcherTimer"/>，因为属性更新必须回到 UI 线程。
/// </summary>
public sealed class SteamVerifyBottomBarViewModel : BaseViewModel
{
	private readonly DispatcherTimer timer;
	private Action? onElapsed;
	private int remaining;

	public SteamVerifyBottomBarViewModel(
		string message,
		string primaryText,
		Action primary,
		string? cancelText = null,
		Action? cancel = null)
	{
		Message = message;
		PrimaryText = primaryText;
		CancelText = cancelText;
		IsCancelVisible = cancelText is not null;
		PrimaryCommand = new RelayCommand(() =>
		{
			Stop();
			primary();
		});
		CancelCommand = new RelayCommand(() =>
		{
			Stop();
			cancel?.Invoke();
		});
		timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) => Tick();
	}

	public string Message { get; }
	public string PrimaryText { get; }
	public RelayCommand PrimaryCommand { get; }
	public string? CancelText { get; }
	public bool IsCancelVisible { get; }
	public RelayCommand CancelCommand { get; }

	public string CountdownText => remaining > 0 ? $"{remaining} 秒后自动{PrimaryText}" : string.Empty;

	/// <summary>开始倒计时；到点时执行 <paramref name="elapsed"/>（按钮先点则取消倒计时）。</summary>
	public void StartCountdown(int seconds, Action elapsed)
	{
		remaining = seconds;
		onElapsed = elapsed;
		OnPropertyChanged(nameof(CountdownText));
		timer.Start();
	}

	public void Stop()
	{
		timer.Stop();
		onElapsed = null;
		remaining = 0;
		OnPropertyChanged(nameof(CountdownText));
	}

	private void Tick()
	{
		if (--remaining > 0)
		{
			OnPropertyChanged(nameof(CountdownText));
			return;
		}
		var action = onElapsed;
		Stop();
		action?.Invoke();
	}
}
