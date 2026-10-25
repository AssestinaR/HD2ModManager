using System.Diagnostics;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 调用 Steam 校验游戏文件完整性，并等它结束。
///
/// 这是最后一道兜底：真映射模式会改写游戏安装目录里的注册表数据，任何情况下都能靠
/// "Steam 校验完整性"把文件恢复成官方版本。校验完成后调用方应清掉映射器记录的原始
/// 备份，让恢复出来的官方文件成为新基准——否则脏备份会一直被当成"原版"。
/// </summary>
public static class SteamIntegrityService
{
	/// <summary>没有任何状态变化时，等这么久就认为"本来就不需要修复"。</summary>
	static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);

	/// <summary>整体上限，超过就放弃等待（Steam 可能被关掉了）。</summary>
	static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	/// <summary>发起校验并等待结束。返回 true 表示已确认结束（或本来就不需要修复）。</summary>
	public static async Task<bool> VerifyAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		var manifestPath = SteamAppManifest.ResolveManifestPath(gameDataDirectory);

		Process.Start(new ProcessStartInfo(
			$"steam://validate/{SteamAppManifest.Helldivers2AppId}")
		{
			UseShellExecute = true,
		});

		var startedAt = DateTime.UtcNow;
		var sawActivity = false;
		while (DateTime.UtcNow - startedAt < Timeout)
		{
			await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
			var manifest = SteamAppManifest.TryRead(manifestPath);
			if (manifest is null) continue;

			if (!manifest.IsFullyInstalled)
			{
				// 正在更新/校验：StateFlags 会暂时离开"完整安装"。
				sawActivity = true;
				continue;
			}
			if (sawActivity) return true;
			// 一直停在"完整安装"说明文件本来就是好的，没有需要修复的东西。
			if (DateTime.UtcNow - startedAt > QuietPeriod) return true;
		}
		return false;
	}
}
