using System.Globalization;
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

	/// <summary>
	/// <c>$iris</c>：<c>EyeRefract</c>（以及老的 <c>Eyes</c>）的<b>眼球颜色图</b>。
	/// <para>
	/// <b>这不是冷门键，是那套着色器唯一的颜色来源。</b>Valve 的
	/// <c>materialsystem/stdshaders/eye_refract.cpp</c> 里，<c>EyeRefract</c> 的参数表
	/// 只有 <c>$iris</c> / <c>$corneatexture</c> / <c>$ambientoccltexture</c> / <c>$envmap</c>
	/// 四张贴图，<b>没有 <c>$basetexture</c></b>；L4D2 自己的 <c>producer_eyeball_*.vmt</c>
	/// 也是这么写的。
	/// </para>
	/// <para>
	/// 少了它，眼球材质就是"既没有 <c>$basetexture</c> 也没有 <c>$matcapture</c>" ⇒ 判失败 ⇒
	/// 用兜底灰材质占位：上机是<b>两颗灰球</b>（用户报的「眼睛没了」）。
	/// </para>
	/// </summary>
	public string? IrisTexture => Value("$iris");

	/// <summary><c>$matcaptexture</c>，没有时回落到 <c>$basetexture</c>（VMT 的常见写法）。</summary>
	public string? MatCapTexture => Value("$matcaptexture") ?? (HasMatCap ? BaseTexture : null);

	public bool HasMatCap => IsEnabled("$matcap") || Value("$matcaptexture") is not null;

	/// <summary>
	/// <c>$MATCAP2TEXTURE</c>：第二张 matcap。NekoToon 里它是<b>加法叠加</b>的一层，
	/// 通常比第一张更亮，负责把金属推到"镀铬"的亮度。
	/// <para>
	/// <b>为什么这一条不能省。</b>实测 Lilium_Sancta 的金属件：第一张 matcap
	/// （<c>Platinum_Silver</c>，均值 129）只是那颗铬球的图案，真正让它亮起来的是
	/// <c>$MATCAP2TEXTURE "11"</c>（均值 199～206，暖白）。少了这一层，
	/// 金属就只能靠环境反射发亮——实测观感是"氧化过的铝"。
	/// </para>
	/// </summary>
	public string? MatCap2Texture => Value("$matcap2texture");

	/// <summary>
	/// 第二张 matcap 是否<b>真的生效</b>。
	/// <para>
	/// ⚠️ 判据必须是 <c>$MATCAP2</c>（混合模式）而不是"写了 <c>$MATCAP2TEXTURE</c>"：
	/// 实测 <c>mat_ornament_petal_shoes</c> 只写了 <c>$MATCAP2TEXTURE</c> 却没写
	/// <c>$MATCAP2</c>，源侧那张图<b>根本没参与渲染</b>。照"有图就加"处理会把花瓣鞋
	/// 凭空提亮一档。
	/// </para>
	/// </summary>
	public bool HasMatCap2 => MatCap2Texture is not null && IsEnabled("$matcap2");

	/// <summary><c>$MATCAP2TINT</c>（0..1，取三分量最大值）。缺失时按 1.0——源侧默认全强度叠加。</summary>
	public double MatCap2Tint
	{
		get
		{
			var raw = Value("$matcap2tint");
			// ⚠️ 有些 VMT 把 `$MATCAP2TINT` 误写成第二个 `$matcaptint`（作者笔误），
			// 那种情况解析出来就是 matcap1 的 tint，语义上等价于"没单独写"，按 1.0 处理更安全。
			if (string.IsNullOrWhiteSpace(raw))
				return 1.0;
			var tint = MaterialAppearance.ParseTint(raw);
			return tint <= 0 ? 1.0 : tint;
		}
	}

	/// <summary>
	/// "这组想要透明"：<c>$translucent</c> 或 <c>$alphatest</c> 非 0。
	/// <para>
	/// 注意这只是<b>意愿</b>，不是结论：装饰件常写 <c>$alphatest 1</c> 但贴图是 1×1 无 alpha。
	/// Python 侧因此要求"VMT 开关<b>且</b>贴图真有 alpha"两条同时成立才切成 alphaclip 模板
	/// （<c>fix_appearance.py</c> 的规则 A，M7 只按开关判、漏了 3 组）。
	/// </para>
	/// <para>
	/// ⚠️ <b>两种"想要透明"在 HD2 里的命运完全不同，不要混用</b>——见
	/// <see cref="WantsAlphaTest"/> 与 <see cref="WantsTranslucency"/>。
	/// </para>
	/// </summary>
	public bool WantsTransparency => WantsAlphaTest || WantsTranslucency;

	/// <summary>
	/// <c>$alphatest 1</c>：<b>硬裁</b>（alpha 低于参考值就丢弃像素）。
	/// <para>
	/// 这个语义与 HD2 的 <c>alphaclip</c> 模板<b>一致</b>，可以照搬。实测 serenade 的
	/// 蕾丝件（<c>lace_white_*</c>，18 个）全是这种，它们的 alpha 就是"蕾丝眼"的形状。
	/// </para>
	/// </summary>
	public bool WantsAlphaTest => IsEnabled("$alphatest");

	/// <summary>
	/// <c>$translucent 1</c>：<b>混合</b>（按 alpha 与背景做 alpha blending，半透明）。
	/// <para>
	/// ⚠️ <b>这个语义 HD2 表达不了，绝不能当 <c>alphaclip</c> 处理。</b>
	/// <c>alphaclip</c> 是硬裁：把"半透明"当成"低于阈值就消失"，会把大半表面直接删掉。
	/// 实测 serenade 的 <c>_1__white</c>（袖套 / 丝袜）就是这种写法，它的 alpha 是
	/// <b>UV 图集里的形状遮罩</b>，分布 p50=0 / p90=27 / p95=223 ——
	/// 按阈值 0.5 硬裁会<b>裁掉 58.8%</b> 的像素，游戏里表现为"整片袖套消失，只剩袖口一点"；
	/// 换阈值 0.144 也只从 58.8% 降到 56.8%，所以 A/B 两档看起来毫无区别。
	/// </para>
	/// <para>
	/// HD2 里带底色贴图槽的模板只有 <c>advanced</c>（完全没有透明度）与
	/// <c>alphaclip</c>（硬裁），<c>translucent</c> 模板只有纯色 <c>BaseColor</c>、没有贴图槽
	/// （见官方 modding wiki 的材质页）。所以这类材质只能<b>退成不透明</b>——
	/// 那至少是"看得见、颜色对"，比整片消失好。转换器会把这件事写进报告的提示里。
	/// </para>
	/// </summary>
	public bool WantsTranslucency => IsEnabled("$translucent");

	/// <summary>
	/// <c>$allowalphatocoverage 1</c>：<b>抖动透明</b>——用 MSAA 的覆盖位把渐变 alpha 变成
	/// "这一小块区域里画几个像素"，视觉上近似半透明，但不进混合队列（没有排序问题）。
	/// <para>
	/// <b>这一条是能把半透明做对的关键。</b>实测 Lilium_Sancta 那套衣服：VMT 里
	/// <c>$translucent 1</c> 是被注释掉的，真正生效的是 <c>$alphatest 1</c>
	/// + <c>$alphatestreference 0.5</c> + <c>$allowalphatocoverage 1</c>，
	/// 而底色 alpha 有 <b>76%</b> 落在中间值——作者要的就是抖动半透明，不是硬裁。
	/// </para>
	/// <para>
	/// HD2 侧用 <c>alphaclip</c> 复现：把遮罩抖成二值（见 <c>SourceMaterialConverter.DitherAlpha</c>），
	/// 远处被 TAA 累积平均成透明度。实机 A/B 已确认观感与源侧薄纱一致。
	/// </para>
	/// </summary>
	public bool WantsAlphaToCoverage => IsEnabled("$allowalphatocoverage");

	/// <summary>
	/// 这个材质该走<b>抖动</b>而不是硬裁：源侧要么要混合（<c>$translucent</c>），
	/// 要么自己就写着抖动透明（<c>$allowalphatocoverage</c>）。
	/// <para>
	/// 剩下的纯 <c>$alphatest</c>（蕾丝、镂空这类二值 alpha）继续硬裁——那才是源侧原意：
	/// Source 的 <c>$alphatest</c> 就是"低于参考值直接丢弃"。
	/// </para>
	/// </summary>
	public bool WantsDitheredTransparency => WantsTranslucency || WantsAlphaToCoverage;

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

	/// <summary>
	/// <see cref="ParseBlock"/> 合并多段数值时的判据：这个 token 本身就是一个数。
	/// <para>
	/// 用它而不是"token 不以 <c>$</c> 开头"：后者会把 <c>vgui/white</c> 这类值也当成可合并的
	/// 续段，于是 <c>$basetexture a b</c> 会被拼成 <c>"a b"</c>——那不是源侧的意思。
	/// </para>
	/// </summary>
	private static bool IsNumericToken(string token)
		=> token.Length > 0
			&& double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

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

			// ⚠️ **不加引号的多段值要一起吃进来。**
			//
			// KeyValues 的语法只允许一个 token，但真实 VMT 里 `$color 0.1792 0.1792 0.1792`
			// 这种写法很常见（实测 milltina 的 6 个金属件全是），而按单 token 解析会得到
			// `$color = "0.1792"`，剩下两个分量变成两条**垃圾键**（键名就叫 `0.1792`）。
			// 后果是颜色丢掉两个通道，而且丢得不声不响：三个本该不同的金属色最后变成同一块灰。
			//
			// 合并规则刻意收得很紧：**当前值已经是纯数字**，且下一个 token 也是纯数字，才并进来。
			// 数字不可能是键名，所以这条规则不会把下一个键的值吞掉；
			// 遇到 `[`、`"`、`$` 开头的 token 或花括号一律停。
			var value = tokens[index];
			index++;
			// 判据看的是**第一个 token**：拼过之后 `"0.462 0.322"` 自己当然不是一个数，
			// 拿拼接结果去再判一次会只吃到两个分量（第三个被当成键名）。
			var numeric = IsNumericToken(value);
			while (numeric && index < tokens.Count && IsNumericToken(tokens[index]))
			{
				value = value + " " + tokens[index];
				index++;
			}

			block.Values[key] = value;
		}

		return block;
	}

	private static Dictionary<string, string> Normalize(Dictionary<string, string> source)
	{
		foreach (var pair in source.ToArray())
		{
			if (pair.Key is "$basetexture" or "$bumpmap" or "$matcaptexture" or "$matcap2texture" or "$emissiontexture")
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
