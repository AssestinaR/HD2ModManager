using System.Diagnostics;

namespace HD2ModManager.Services;

/// <summary>游戏进程探测。映射页「应用」与低污染模式自动流程共用同一份判断：探测失败一律按「在运行」处理。</summary>
public static class GameProcessProbe
{
	public static bool IsGameRunning()
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
