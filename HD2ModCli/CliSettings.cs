using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2ModCli;

/// <summary>
/// 直接读写管理器目录里的 <c>settings.json</c>。
/// <para>
/// <b>为什么需要它。</b>有些开关只存在于这个文件里——真映射模式、物理模式——而它们平时
/// 只能通过界面改。用户（或替他跑命令的 agent）在没有 GUI 的时候就被卡住了：
/// 明明能诊断出"开关是 2"，却没有任何办法改它。
/// </para>
/// <para>
/// <b>为什么用 JsonObject 而不是强类型。</b>这份文件里有时间戳、间隔之类的字段，
/// 强类型往返一次就可能把未知字段吃掉或改掉格式。按节点改，其它键原样保留。
/// </para>
/// </summary>
internal static class CliSettings
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string ResolvePath(CliWorkspace workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (!string.IsNullOrWhiteSpace(workspace.SettingsPath)) return workspace.SettingsPath;
		var root = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");
		return Path.Combine(root, "settings.json");
	}

	public static JsonObject? TryRead(string path)
	{
		try
		{
			if (!File.Exists(path)) return null;
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>按键名取值（大小写不敏感，和 Windows 上用户书写习惯一致）。</summary>
	public static bool TryGet(JsonObject document, string key, out JsonNode? value)
	{
		foreach (var pair in document)
		{
			if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				value = pair.Value;
				return true;
			}
		}
		value = null;
		return false;
	}

	/// <summary>
	/// 改一个键。值按字面量推断类型：<c>true</c>/<c>false</c> → 布尔，纯整数 → 数字，其余 → 字符串。
	/// 键不存在就新增（用调用方给的写法）。
	/// </summary>
	public static string Set(JsonObject document, string key, string rawValue)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		ArgumentNullException.ThrowIfNull(rawValue);

		var node = InferValue(rawValue.Trim());
		foreach (var existing in document.Select(pair => pair.Key).ToArray())
		{
			if (!string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)) continue;
			document[existing] = node;
			return existing;
		}
		document[key] = node;
		return key;
	}

	private static JsonNode InferValue(string text)
	{
		if (bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
		if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
			System.Globalization.CultureInfo.InvariantCulture, out var number))
			return JsonValue.Create(number);
		return JsonValue.Create(text);
	}

	public static string? TryWrite(string path, JsonObject document)
	{
		try
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
			File.WriteAllText(path, document.ToJsonString(JsonOptions) + "\n",
				new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return exception.Message;
		}
	}
}
