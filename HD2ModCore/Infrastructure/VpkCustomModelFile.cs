using System.Text.Json;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Mod 目录里的 <c>custom-model.json</c>：VPK 转换登记"这套 Unit 是一具素体"的地方。
/// <para>
/// <b>为什么放在 Core 而不是管理器。</b>它回答的问题与界面无关：
/// "这个素体是谁的"。两个地方都要用它——详情页据此把默认目标设成"自己的素体"，
/// 护甲映射页据此拒绝单独删除它。放进 Core 才能离线测，也避免两边各写一份读法。
/// </para>
/// </summary>
public static class VpkCustomModelFile
{
	/// <summary>文件名（写在 Mod 根目录，与 <c>vpk2patch/</c> 平级）。</summary>
	public const string FileName = "custom-model.json";

	/// <summary>读一个 Mod 目录登记的素体 key；没有或读不懂返回 <c>null</c>。</summary>
	public static string? TryReadKey(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory))
			return null;
		var path = Path.Combine(modDirectory, FileName);
		if (!File.Exists(path))
			return null;
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.TryGetProperty("key", out var key) ? key.GetString() : null;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 整个 Mod 库里被 VPK 模组"持有"的素体 key。
	/// <para>
	/// <b>为什么要扫库而不是查注册表。</b>注册表里没有"这个素体属于哪个 Mod"这一维，
	/// 而 <c>custom-model.json</c> 就在 Mod 目录里——那是唯一不会漂移的事实来源。
	/// </para>
	/// </summary>
	public static IReadOnlySet<string> ReadOwnedKeys(string modsDirectory)
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
			return keys;
		IEnumerable<string> files;
		try
		{
			files = Directory.EnumerateFiles(modsDirectory, FileName, SearchOption.AllDirectories);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return keys;
		}

		foreach (var file in files)
		{
			var key = TryReadKey(Path.GetDirectoryName(file)!);
			if (!string.IsNullOrWhiteSpace(key))
				keys.Add(key);
		}

		return keys;
	}
}
