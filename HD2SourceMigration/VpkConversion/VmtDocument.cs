using System.Text;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// Source 材质文件（<c>.vmt</c>，Valve KeyValues 文本）的解析结果。
/// <para>
/// 只做<b>扁平的顶层键值表</b>：VMT 里嵌套块（<c>Proxies</c>、<c>Patch</c>）对本管线没用，
/// 一律跳过。移植自 <c>tmp/rig-lab/build_patch.py</c> 里与 VMT 相关的判定。
/// </para>
/// <para>
/// <b>哪些键真的被用到</b>（照 Python 侧现状，不替它加戏）：
/// </para>
/// <list type="bullet">
/// <item><c>$basetexture</c> —— 分组键 + 底色来源，唯一必备的键。</item>
/// <item><c>$translucent</c> / <c>$alphatest</c> —— 非 0 = "这组想要透明"。仅开开关，
/// 真正的镂空信息在贴图的 alpha 通道里。</item>
/// <item><c>$matcap</c> / <c>$matcaptexture</c> / <c>$matcaptint</c> —— matcap 分支
/// （金属装饰件的底色要靠它烘）。</item>
/// <item><c>$bumpmap</c> / <c>$outline</c> —— <b>Python 侧解析了但没接</b>；<c>$emission*</c> 完全没接。
/// 这里保留读取能力（值读得到），但上层不要以为它们已经生效。</item>
/// </list>
/// </summary>
public sealed class VmtDocument
{
	private readonly Dictionary<string, string> values;

	private VmtDocument(string shader, Dictionary<string, string> values)
	{
		Shader = shader;
		this.values = values;
	}

	/// <summary>着色器名（第一个 token），例如 <c>VertexLitGeneric</c>。</summary>
	public string Shader { get; }

	public IReadOnlyDictionary<string, string> Values => values;

	/// <summary><c>$basetexture</c>，路径一律规范成小写正斜杠。没有则为 <c>null</c>。</summary>
	public string? BaseTexture => Value("$basetexture");

	/// <summary><c>$bumpmap</c>。Python 侧解析了但没用上。</summary>
	public string? BumpMap => Value("$bumpmap");

	/// <summary><c>$matcaptexture</c>，没有时回落到 <c>$basetexture</c>（VMT 的常见写法）。</summary>
	public string? MatCapTexture => Value("$matcaptexture") ?? (HasMatCap ? BaseTexture : null);

	public bool HasMatCap => IsEnabled("$matcap") || Value("$matcaptexture") is not null;

	/// <summary>
	/// "这组想要透明"：<c>$translucent</c> 或 <c>$alphatest</c> 非 0。
	/// <para>
	/// 注意这只是<b>意愿</b>，不是结论：装饰件常写 <c>$alphatest 1</c> 但贴图是 1×1 无 alpha。
	/// Python 侧因此要求"VMT 开关<b>且</b>贴图真有 alpha"两条同时成立才切成 alphaclip 模板
	/// （<c>fix_appearance.py</c> 的规则 A，M7 只按开关判、漏了 3 组）。
	/// </para>
	/// </summary>
	public bool WantsTransparency => IsEnabled("$translucent") || IsEnabled("$alphatest");

	public string? Value(string key) => values.GetValueOrDefault(key.ToLowerInvariant());

	/// <summary>把 <c>1</c>/<c>true</c>/<c>yes</c> 之类的常见写法都当作开。</summary>
	public bool IsEnabled(string key)
	{
		var raw = Value(key);
		if (string.IsNullOrWhiteSpace(raw))
			return values.ContainsKey(key.ToLowerInvariant());   // 裸键（无值）也当作开
		return raw.Trim().ToLowerInvariant() switch
		{
			"0" or "false" or "no" or "off" => false,
			_ => true,
		};
	}

	/// <summary>
	/// 解析一份 VMT。容错优先：VMT 是手写的，缺引号、多注释、有 BOM 都常见，
	/// 这里不因为格式小瑕疵就整份丢掉——丢掉的代价是"这组材质没有底色"，比报错更难查。
	/// </summary>
	public static VmtDocument Parse(ReadOnlySpan<byte> bytes)
	{
		var text = DecodeText(bytes);
		var tokens = Tokenize(text);
		if (tokens.Count == 0)
			return new VmtDocument(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

		var shader = tokens[0];
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// 顶层块：shader 之后的第一个 '{' 到配对的 '}'。深度 > 1 的键值一律不进表。
		var index = 1;
		while (index < tokens.Count && tokens[index] != "{")
			index++;
		index++;
		var depth = 1;
		while (index < tokens.Count && depth > 0)
		{
			var token = tokens[index];
			if (token == "{")
			{
				depth++;
				index++;
				continue;
			}

			if (token == "}")
			{
				depth--;
				index++;
				continue;
			}

			if (depth != 1)
			{
				index++;
				continue;
			}

			// 顶层键：下一个 token 是值；若它直接是 '{'，说明是嵌套块，跳过。
			var key = token.ToLowerInvariant();
			index++;
			if (index >= tokens.Count)
				break;
			if (tokens[index] == "{")
			{
				values.TryAdd(key, string.Empty);
				continue;
			}

			values[key] = tokens[index];
			index++;
		}

		foreach (var pair in values.ToArray())
		{
			if (pair.Key is "$basetexture" or "$bumpmap" or "$matcaptexture")
				values[pair.Key] = NormalizePath(pair.Value);
		}

		return new VmtDocument(shader, values);
	}

	/// <summary>材质路径规范：反斜杠转正斜杠、合并重复斜杠、去掉 <c>materials/</c> 前缀、转小写。</summary>
	public static string NormalizePath(string path)
	{
		var normalized = path.Replace('\\', '/').Trim();
		while (normalized.Contains("//", StringComparison.Ordinal))
			normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
		if (normalized.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
			normalized = normalized["materials/".Length..];
		return normalized.ToLowerInvariant();
	}

	private static string DecodeText(ReadOnlySpan<byte> bytes)
	{
		// 去 UTF-8 BOM；VMT 是 ASCII/ANSI 文本，用 UTF-8 解足够。
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			bytes = bytes[3..];
		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>
	/// KeyValues 的分词：引号串、<c>{</c> <c>}</c>、裸 token；<c>//</c> 到行尾是注释。
	/// </summary>
	private static List<string> Tokenize(string text)
	{
		var tokens = new List<string>();
		var index = 0;
		while (index < text.Length)
		{
			var current = text[index];
			if (char.IsWhiteSpace(current))
			{
				index++;
				continue;
			}

			if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
			{
				while (index < text.Length && text[index] != '\n')
					index++;
				continue;
			}

			if (current is '{' or '}')
			{
				tokens.Add(current.ToString());
				index++;
				continue;
			}

			if (current == '"')
			{
				index++;
				var builder = new StringBuilder();
				while (index < text.Length && text[index] != '"')
				{
					// KeyValues 里 \" 是转义引号，其余反斜杠原样保留（路径要用）。
					if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] == '"')
					{
						builder.Append('"');
						index += 2;
						continue;
					}

					builder.Append(text[index]);
					index++;
				}

				index++;   // 收尾引号
				tokens.Add(builder.ToString());
				continue;
			}

			var bare = new StringBuilder();
			while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"'))
			{
				bare.Append(text[index]);
				index++;
			}

			if (bare.Length != 0)
				tokens.Add(bare.ToString());
		}

		return tokens;
	}
}
