using System.Text;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// Source 材质文件（<c>.vmt</c>，Valve KeyValues 文本）的解析结果。
/// <para>
/// <b>支持 <c>patch</c> 材质</b>（<c>include</c> + <c>insert</c> + <c>replace</c>）。
/// 这类 VMT 在 L4D2 模组里非常常见：整套服装/发型/部件用一份公共基础材质，各自只写
/// "我要换掉哪几个键"。实测一个 34 份 VMT 的模组包里 29 份是 <c>patch</c>；
/// 不解析它们就等于整套材质凭空消失（表现为"材质包导入成功但游戏里没变"）。
/// </para>
/// <para>
/// <b>解析是扁平的</b>：只保留顶层键值（含 patch 展开后的结果）。<c>Proxies</c> 之类的
/// 子块仍然跳过——本管线用不到。
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
	/// <summary>include 链的深度上限。模组里正常只有一层；设上限是为了防环形引用。</summary>
	private const int MaxIncludeDepth = 8;

	private readonly Dictionary<string, string> values;

	private VmtDocument(
		string shader,
		Dictionary<string, string> values,
		bool isPatch = false,
		string? includePath = null)
	{
		Shader = shader;
		this.values = values;
		IsPatch = isPatch;
		IncludePath = includePath;
	}

	/// <summary>着色器名（第一个 token），例如 <c>VertexLitGeneric</c>。patch 材质是展开后继承到的那个。</summary>
	public string Shader { get; }

	/// <summary>这份 VMT 是不是 <c>patch</c> 型（include + insert/replace）。</summary>
	public bool IsPatch { get; }

	/// <summary>patch 材质的 <c>include</c> 目标（已规范化），否则为 <c>null</c>。</summary>
	public string? IncludePath { get; }

	/// <summary>展开后的键值表（patch 已合并 include 目标 + insert + replace）。</summary>
	public IReadOnlyDictionary<string, string> Values => values;

	/// <summary><c>$basetexture</c>，路径一律规范成小写正斜杠。没有（或是占位值）则为 <c>null</c>。</summary>
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
	/// 解析一份 VMT（不解析 patch 的 include：拿不到被包含的材质时，patch 型只能得到空表）。
	/// 需要展开 patch 时用带 <c>includeReader</c> 的重载。
	/// </summary>
	public static VmtDocument Parse(ReadOnlySpan<byte> bytes) => Parse(bytes, null);

	/// <summary>
	/// 解析一份 VMT，并用 <paramref name="includeReader"/> 展开 <c>patch</c> 材质。
	/// <para>
	/// <paramref name="includeReader"/> 收的是**材质短名**（无目录、无扩展名），返回它的字节或 null。
	/// 传 null 时 patch 材质退化成"只有自己的 insert/replace"，通常拿不到 <c>$basetexture</c>。
	/// </para>
	/// </summary>
	public static VmtDocument Parse(ReadOnlySpan<byte> bytes, Func<string, byte[]?>? includeReader)
		=> ParseCore(bytes, includeReader, 0, null);

	private static VmtDocument ParseCore(
		ReadOnlySpan<byte> bytes,
		Func<string, byte[]?>? includeReader,
		int depth,
		HashSet<string>? visited)
	{
		var text = DecodeText(bytes);
		var tokens = Tokenize(text);
		if (tokens.Count == 0)
			return new VmtDocument(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

		var shader = tokens[0];
		var index = 1;
		while (index < tokens.Count && tokens[index] != "{")
			index++;
		index++;
		var root = ParseBlock(tokens, ref index);

		if (!shader.Equals("patch", StringComparison.OrdinalIgnoreCase))
			return new VmtDocument(shader, Normalize(root.Values));

		var includePath = root.Values.GetValueOrDefault("include");
		var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var resolvedShader = string.Empty;

		if (includeReader is not null
			&& !string.IsNullOrWhiteSpace(includePath)
			&& depth < MaxIncludeDepth)
		{
			visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var key = NormalizePath(includePath);
			// 环形 include 只可能出现在坏模组里，但一旦成环就是栈溢出，必须挡。
			if (visited.Add(key))
			{
				var includedBytes = includeReader(Path.GetFileNameWithoutExtension(key));
				if (includedBytes is not null)
				{
					var included = ParseCore(includedBytes, includeReader, depth + 1, visited);
					foreach (var pair in included.values)
						merged[pair.Key] = pair.Value;
					resolvedShader = included.Shader;
				}
			}
		}

		// insert 与 replace 都是"覆盖"语义。
		// 这一条是**样本逼出来的**，别按字面"补缺"理解：GF2 的 33 份子材质全部用 insert 写真正的
		// 底色，而 include 的 shader/vmt_base.vmt 里把 `$basetexture` 写成占位值 "basetexture"
		// 并注释"由 patch VMT 替换"。若 insert 不覆盖，这整套材质在源引擎里就找不到底色贴图，
		// 作者不可能没发现。早期实现按"insert 只补缺"处理，结果整包 34 份材质全部合成失败，
		// 游戏里就是"模型进来但没材质"。
		if (root.Children.TryGetValue("insert", out var insert))
		{
			foreach (var pair in insert.Values)
				merged[pair.Key] = pair.Value;
		}

		if (root.Children.TryGetValue("replace", out var replace))
		{
			foreach (var pair in replace.Values)
				merged[pair.Key] = pair.Value;
		}

		return new VmtDocument(
			string.IsNullOrEmpty(resolvedShader) ? shader : resolvedShader,
			Normalize(merged),
			isPatch: true,
			includePath: includePath);
	}

	private sealed class Block
	{
		public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, Block> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>解析一个 <c>{</c> … <c>}</c> 块；子块进 <see cref="Block.Children"/>，其余进键值表。</summary>
	private static Block ParseBlock(List<string> tokens, ref int index)
	{
		var block = new Block();
		while (index < tokens.Count)
		{
			var token = tokens[index];
			if (token == "}")
			{
				index++;
				break;
			}

			// 容错的 '{'：缺了键名的块直接进（VMT 是手写的，不因为格式瑕疵整份丢掉）。
			if (token == "{")
			{
				index++;
				continue;
			}

			var key = token.ToLowerInvariant();
			index++;
			if (index >= tokens.Count)
				break;

			if (tokens[index] == "{")
			{
				index++;
				block.Children[key] = ParseBlock(tokens, ref index);
				continue;
			}

			block.Values[key] = tokens[index];
			index++;
		}

		return block;
	}

	private static Dictionary<string, string> Normalize(Dictionary<string, string> source)
	{
		foreach (var pair in source.ToArray())
		{
			if (pair.Key is "$basetexture" or "$bumpmap" or "$matcaptexture" or "$emissiontexture")
				source[pair.Key] = NormalizePath(pair.Value);
		}

		return source;
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
