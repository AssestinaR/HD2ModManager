using System.Globalization;
using System.Text.RegularExpressions;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 从本地 <c>steamapps/appmanifest_&lt;appid&gt;.acf</c> 读游戏版本。
///
/// 这是 Steam 自己认定的版本，不需要任何网络请求，也不依赖第三方接口。
/// 用途：判断"游戏是否更新过"，从而决定真映射模式的原始备份是否还有效。
/// </summary>
public sealed partial record SteamAppManifest(
	string BuildId,
	long LastUpdatedUnix,
	int StateFlags)
{
	/// <summary>StateFlags 的"完整安装"位。只有它置位时 buildid 才可信。</summary>
	public const int FullyInstalled = 4;

	public bool IsFullyInstalled => (StateFlags & FullyInstalled) != 0;

	/// <summary>Helldivers 2 的 Steam AppID。</summary>
	public const uint Helldivers2AppId = 553850;

	[GeneratedRegex("\"buildid\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
	private static partial Regex BuildIdPattern();

	[GeneratedRegex("\"LastUpdated\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
	private static partial Regex LastUpdatedPattern();

	[GeneratedRegex("\"StateFlags\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
	private static partial Regex StateFlagsPattern();

	/// <summary>
	/// 从游戏 data 目录推出 manifest 路径：<c>&lt;steamapps&gt;/common/Helldivers 2/data</c>。
	/// </summary>
	public static string ResolveManifestPath(string gameDataDirectory, uint appId = Helldivers2AppId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		var steamApps = Path.GetFullPath(Path.Combine(gameDataDirectory, "..", "..", ".."));
		return Path.Combine(steamApps, $"appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf");
	}

	/// <summary>读不到或格式不认识时返回 null——调用方应跳过版本判据。</summary>
	public static SteamAppManifest? TryRead(string manifestPath)
	{
		try
		{
			if (!File.Exists(manifestPath)) return null;
			return Parse(File.ReadAllText(manifestPath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static SteamAppManifest? Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		var buildId = BuildIdPattern().Match(text);
		if (!buildId.Success) return null;
		var lastUpdated = LastUpdatedPattern().Match(text);
		var stateFlags = StateFlagsPattern().Match(text);
		return new SteamAppManifest(
			buildId.Groups["value"].Value.Trim(),
			lastUpdated.Success && long.TryParse(lastUpdated.Groups["value"].Value, out var updated) ? updated : 0,
			stateFlags.Success && int.TryParse(stateFlags.Groups["value"].Value, out var flags) ? flags : 0);
	}

	/// <summary>
	/// 可信的版本标识：只有完整安装且 buildid 非空时才返回，否则 null。
	/// 更新进行中（StateFlags 不含 4）时版本号会短暂变化，不能当作判据。
	/// </summary>
	public string? TrustedBuildId
		=> IsFullyInstalled && !string.IsNullOrWhiteSpace(BuildId) ? BuildId : null;
}
