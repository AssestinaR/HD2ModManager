using System.Globalization;

namespace HD2SourceMigration.VpkConversion;

/// <summary>matcap 判定的结论。</summary>
/// <param name="HasMatCap">VMT 声明了 matcap（<c>$matcap</c> 或 <c>$matcaptexture</c>）。</param>
/// <param name="Tint"><c>$matcaptint</c> 三个分量的最大值。</param>
public readonly record struct MatCapVerdict(bool HasMatCap, double Tint)
{
	/// <summary>
	/// <b>matcap 就是这个表面本身</b>（而不是一层视角相关的叠加高光）。
	/// <para>
	/// 判据是 <c>tint ≥ 0.9</c>（<c>fix_appearance.py:38</c>）。命中时源侧整片底色是
	/// 近黑的 <c>(15,15,15)</c>、**整个观感都来自 matcap**；直接搬到 HD2 的 <c>advanced</c>
	/// 上就只剩那片近黑——**金属装饰件渲染成纯黑**。
	/// </para>
	/// <para>
	/// 没命中时 matcap 只是视角相关的叠加，<c>advanced</c> 的 PBR 自会处理，
	/// <b>不该烘</b>（烘了会把高光烤死在底色里）。
	/// </para>
	/// </summary>
	public bool MatCapIsTheSurface => HasMatCap && Tint >= MaterialAppearance.MatCapMinTint;
}

/// <summary>
/// NekoToon matcap 的判定与烘焙参数。移植自 <c>tmp/rig-lab/fix_appearance.py:27-42</c>。
/// <para>
/// 源侧的合成方式是<b>加法</b>：<c>appearance = basetexture_rgb + matcaptexture_rgb * matcaptint_rgb</c>
/// （<c>matcap</c> 模式 2）。HD2 的 <c>advanced</c> 没有这条通路，所以要把它烘进底色。
/// </para>
/// </summary>
public static class MaterialAppearance
{
	/// <summary>tint 到这个值以上就认为 matcap 承载了整个表面。</summary>
	public const double MatCapMinTint = 0.9;

	/// <summary>
	/// 底色"没有内容"的尺寸判据：长宽都不超过它就算占位图。
	/// <para>实测 <c>Ornament_metal_Underwear.vtf</c> 是一张 1×1、91 字节的占位图。</para>
	/// </summary>
	public const int PlaceholderMaxDimension = 4;

	/// <summary>
	/// 这张 matcap 是不是<b>金属</b>——按<b>名字</b>判，不看像素。
	/// <para>
	/// <b>为什么不用像素统计（实机 + 实测双重否定）。</b>
	/// </para>
	/// <list type="bullet">
	/// <item><b>"底色近黑 = 金属"不可靠</b>：它测的是<b>颜色变体</b>的属性，不是材质的属性。
	/// 实测同一条 <c>mat_metal</c> 的底图平均亮度：B1（服装色黑）<b>17.2</b>、
	/// B3（服装色白紫）<b>128.4</b>——差 7 倍。于是同一套金属件在深色款判成金属、
	/// 在浅色款一个都判不出来。</item>
	/// <item><b>"边缘密度"分不开金属与宝石</b>：实测 <c>matcap_jewel9</c> 平均梯度 4.84
	/// （像金属）而 <c>matcap_metal7</c> 11.15（像宝石），两类区间重叠。</item>
	/// </list>
	/// <para>
	/// <b>名字为什么可靠。</b>NekoToon 的 matcap 按材质类型命名，且<b>不随颜色变体变化</b>
	/// （<c>matcap_metal11</c> 在黑款白款里都叫这个）。这是<b>作者的声明</b>，
	/// 比从像素反推更接近源意。
	/// </para>
	/// <para>
	/// <b>非金属关键词优先</b>：先查 <c>jewel/gem/diamond/pearl/cloth/env/highlight/rim</c>，
	/// 这样"宝石/布料"这类明确的词不会被偶然包含的金属词压过。
	/// </para>
	/// <para>
	/// ⚠️ <b>曾经想把"金属词压过装饰词"改成两层优先级，改完又撤回了</b>——
	/// 因为真实数据不支持：MourningVeil 的 <c>Mat_Metal_gem_*</c> 材质名里确实有 metal，
	/// 但它们的 <c>$matcapture</c> 是 <c>matcap_jewel9</c>（纯宝石），判非金属是<b>对的</b>。
	/// 别再凭材质名去推翻 matcap 的结论——但 matcap 名<b>说不出所以然</b>时，材质名是第二个证据，
	/// 见 <see cref="IsMetallicMatcapName(string?, string?)"/>。
	/// </para>
	/// </summary>
	public static bool IsMetallicMatcapName(string? matcapName) => ClassifyName(matcapName) ?? false;

	/// <summary>
	/// 判金属：<b>先看 matcap 名，它说不出所以然时再看材质名</b>。
	/// <para>
	/// <b>为什么需要第二个证据。</b>实测 Lunalice：21 个 <c>ornament_metal_*</c> 材质
	/// （项链、项圈、腰带、护腕、裙撑、头饰、小刀…）用的 matcap 文件名叫
	/// <c>11</c>——一个纯数字。名字里没有任何判定线索，于是 21 个金属件<b>全部判成非金属</b>，
	/// 而它们的模板是 <c>advanced</c>（本来可以给金属度）。用户看到的就是"金属没有金属效果"。
	/// </para>
	/// <para>
	/// 而它们的材质名 <c>ornament_metal_chain</c> 其实是<b>同样明确的作者声明</b>。
	/// 所以规则是：matcap 名给出结论就信它（它更具体、且是决定观感的那个图）；
	/// 给不出结论时退回材质名；两者都说不出所以然才是非金属。
	/// </para>
	/// <para>
	/// 这样不会破坏已有的正确判定：MourningVeil 的 <c>matcap_jewel9</c> 仍然判宝石、
	/// <c>matcap_metal11</c> 仍然判金属，与材质名无关。
	/// </para>
	/// </summary>
	/// <param name="matcapName">matcap 贴图名（可能是个纯数字，这时它不提供信息）。</param>
	/// <param name="materialName">源材质名（VMT 名）。第二个证据源。</param>
	public static bool IsMetallicMatcapName(string? matcapName, string? materialName)
		=> ClassifyName(matcapName) ?? ClassifyName(materialName) ?? false;

	/// <summary>
	/// 按名字判金属：有明确结论返回 <c>true/false</c>，<b>名字里没有任何线索时返回 <c>null</c></b>。
	/// <para>
	/// 三态是刻意的：只有区分"判为非金属"和"这个名字我看不出来"，才能在上面那个两参重载里
	/// 正确地退回第二个证据源。二态会让"`11` 看不出来"和"`jewel9` 是宝石"混成同一件事。
	/// </para>
	/// </summary>
	private static bool? ClassifyName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return null;
		var lower = name.ToLowerInvariant();

		foreach (var marker in NonMetallicMatcapMarkers)
		{
			if (lower.Contains(marker, StringComparison.Ordinal))
				return false;
		}

		foreach (var marker in MetallicMatcapMarkers)
		{
			if (lower.Contains(marker, StringComparison.Ordinal))
				return true;
		}

		return null;
	}

	/// <summary>名字里出现这些词 → <b>不是</b>金属（优先于金属词）。</summary>
	private static readonly string[] NonMetallicMatcapMarkers =
	[
		"jewel", "gem", "diamond", "pearl", "cloth", "env", "highlight", "rim",
		"skin", "eye", "hair", "tattoo", "scale", "feather",
	];

	/// <summary>名字里出现这些词 → 金属。</summary>
	private static readonly string[] MetallicMatcapMarkers =
	[
		"metal", "silver", "platinum", "magnet", "steel", "chrome", "iron",
		"gold", "brass", "copper", "bronze", "aluminium", "aluminum",
	];

	/// <summary>
	/// 从<b>贴图名</b>猜它是不是一张"平色占位图"，返回 sRGB 字节值；猜不出返回 <c>null</c>。
	/// <para>
	/// <b>用途：底图缺失时的兜底颜色。</b>模组里常把纯色底图直接命名成 <c>black</c> /
	/// <c>white</c> / <c>gray</c>（NekoToon 的金属件底色就是 <c>gray1</c>）。
	/// 实测 MourningVeil 的 B3 包引用了一张不存在的 <c>matchacheese/MourningVeil/black</c>
	/// —— 名字已经说明它要的是**纯黑**，退成中性灰就错了。
	/// </para>
	/// <para>
	/// 只认<b>整名匹配</b>（可带数字后缀，如 <c>gray1</c>），不做子串匹配：
	/// <c>blacktop</c> / <c>blackcable</c> 这类是真实贴图，不是平色。
	/// </para>
	/// </summary>
	public static byte? TryParseFlatColourName(string? textureName)
	{
		if (string.IsNullOrWhiteSpace(textureName))
			return null;

		// 去掉目录与末尾数字（gray1 → gray）。
		var stem = textureName.Replace('\\', '/');
		stem = stem[(stem.LastIndexOf('/') + 1)..].ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

		return stem switch
		{
			"black" or "blackcolor" => 0,
			"white" or "whitecolor" => 255,
			"gray" or "grey" or "graycolor" => 128,
			_ => null,
		};
	}

	/// <summary>
	/// 解析源侧的平涂底色 <c>$color</c> / <c>$color2</c> 成 sRGB 字节。
	/// <para>
	/// <b>为什么需要它。</b>有一类材质<b>根本没有自己的底色图</b>：
	/// <c>$basetexture "vgui/white"</c>（<c>vgui/white</c> 是引擎内置图，任何 VPK 里都没有它）
	/// 加上 <c>$color</c> 给颜色，再用 <c>$phong</c> + <c>$envmap</c> 做金属感。
	/// 实测 milltina 的 6 个金属件全是这个写法：<c>silver</c> 是
	/// <c>$color 0.1792 0.1792 0.1792</c>、<c>gold</c> 是 <c>0.462 0.322 0</c>、
	/// <c>gemstone</c> 是 <c>0.811 0 0</c>。不读这个键，它们的颜色只能按图片名去猜
	/// （<c>white</c> → 纯白），于是银、金、宝石红全部变成同一块灰。
	/// </para>
	/// <para>
	/// <b>为什么按线性解释。</b><c>$color</c> 在源侧参与的是线性光照，作者写
	/// <c>0.462</c> 表达的是"这块表面反射 46% 的红光"，不是"46% 的 sRGB 亮度"。
	/// 我们的底色是 sRGB 编码的贴图，所以必须过一次 <see cref="LinearToSrgbByte"/>；
	/// 直接乘 255 会把所有金属都压暗一档（0.462 会变成 118 而不是 180）。
	/// </para>
	/// <para>
	/// <b><c>$color</c> 优先于 <c>$color2</c>。</b>实测需要这个键的包都写 <c>$color</c>；
	/// 而 <c>$color2</c> 在大量源包里是默认值 <c>[1 1 1]</c>，优先读它会把颜色抹成白。
	/// 分量不足 3 个时返回 false —— 单个数不是颜色，猜它只会造出没有依据的色相。
	/// </para>
	/// </summary>
	public static bool TryParseFlatColor(string? raw, out byte red, out byte green, out byte blue)
	{
		red = green = blue = 0;
		if (string.IsNullOrWhiteSpace(raw))
			return false;

		// 与 ParseTint 同一套归一化：两种括号、逗号分隔都见过。
		var cleaned = raw
			.Replace("[", string.Empty, StringComparison.Ordinal)
			.Replace("]", string.Empty, StringComparison.Ordinal)
			.Replace("{", string.Empty, StringComparison.Ordinal)
			.Replace("}", string.Empty, StringComparison.Ordinal)
			.Replace(",", " ", StringComparison.Ordinal);
		var components = cleaned
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
			.Where(double.IsFinite)
			.Take(3)
			.ToArray();
		if (components.Length < 3)
			return false;

		// 255 制与 0..1 制都要认。判据取三通道最大值（与 ParseTint 同一条口径），
		// 逐通道判断会把 `[1 .5 .5]` 这种"红到顶 + 半强度绿蓝"拆成两种量纲。
		var divisor = components.Max() > 1.0 ? 255.0 : 1.0;
		red = LinearToSrgbByte(components[0] / divisor);
		green = LinearToSrgbByte(components[1] / divisor);
		blue = LinearToSrgbByte(components[2] / divisor);
		return true;
	}

	/// <summary>
	/// matcap 承载表面但<b>不是</b>金属时，底色被顶掉后用什么（线性反射率）。
	/// <para>
	/// 存在的理由：有些件的底色是<b>近黑占位图</b>（实测 Lilium 的 <c>m_metal</c> 是
	/// 1×1、(15,15,15)），而 matcap 名字是宝石系——保留源底色会得到一块<b>纯黑</b>；
	/// 拿 matcap 图当底色又会采到球面上的一块随机区域（"生铁"那个坑）。
	/// 所以给一个中性浅灰：宝石/玻璃看着像"亮的塑料"，是这里能做到的最不坏的近似。
	/// </para>
	/// </summary>
	public const double NonMetallicSurfaceF0 = 0.6;

	/// <summary>
	/// 底色"没有内容"的亮度判据：平均亮度低于它就当整片近黑。
	/// <para>实测那些"观感全来自 matcap"的金属件，底色是 <c>(15,15,15)</c>。</para>
	/// </summary>
	public const double NearBlackMeanLuminance = 40;

	/// <summary>
	/// 底色是不是**没有自己的颜色**：占位图（≤4×4），或整片近黑。
	/// <para>
	/// <b>为什么光有 <c>tint ≥ 0.9</c> 不够。</b>装饰件写 <c>$matcaptint "[1 1 1 1]"</c> 是常态，
	/// 意思是"按全强度叠加 matcap"，底色照样可能是一张真图。
	/// </para>
	/// <para>
	/// <b>⚠️ 曾经还有第三条"整片同色"，已删除（实机逼出来的）。</b>
	/// 它的依据是"金属件的底色是一张纯色灰图（<c>gray1</c> / <c>gray</c>）"，
	/// 但实测那两张<b>都是 1×1</b>——占位图那一条已经覆盖，这条是冗余的。而它的代价很实在：
	/// </para>
	/// <list type="bullet">
	/// <item>MourningVeil 的<b>玻璃鞋跟</b>（<c>mat_gem1_shoes</c>，64×64 纯色 197）
	/// 被当成"没有颜色" → 判成金属 → 实机是"玻璃变成了金属"。</item>
	/// </list>
	/// <para>
	/// 一条<b>纯色但真实</b>的底色仍然<b>是一个颜色</b>：它只是没有花纹，不是没有颜色。
	/// "没有颜色"只能由"占位图"或"近黑"来表达。
	/// </para>
	/// <para>
	/// <b>⚠️ 也刻意不看 alpha。</b>alpha 表达的是<b>形状</b>（镂空/半透明），与"颜色从哪来"正交。
	/// 曾经要求 <c>!baseAlpha.IsMeaningful</c>，后果是：凡是靠 alpha 镂空的金属件
	/// （NekoToon 里金属饰品普遍写 <c>$alphatest 1</c>）全部判不成金属 ——
	/// 实测 MourningVeil 93 个材质里<b>一个金属都没有</b>，正是用户报的
	/// "相当多金属饰品没有金属光泽"。
	/// </para>
	/// </summary>
	public static bool IsContentFreeBase(int width, int height, ReadOnlySpan<byte> rgba)
	{
		if (width <= PlaceholderMaxDimension && height <= PlaceholderMaxDimension)
			return true;
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			return false;

		// 平均亮度（Rec.601 权重）。
		var pixels = rgba.Length / 4;
		double sum = 0;
		for (var offset = 0; offset < rgba.Length; offset += 4)
			sum += (0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]);

		return sum / pixels < NearBlackMeanLuminance;
	}


	/// <summary>
	/// 烘 matcap 时写进 <c>advanced</c> 槽 3（<c>IlluminateData</c> = 金属度）的值。
	/// <para>
	/// <b>255（= 1.0）不是随手取的。</b>源侧这些件在 NekoToon 里的合成是
	/// <c>matcap1(替换) + matcap2(加法)</c>，两面都叠满，最终表面<b>几乎没有漫反射</b>——
	/// 那就是纯金属。原来写 191（0.75）留了 25% 的漫反射，实机观感正是用户报的
	/// "氧化过的铝 / 珍珠的反光色，不够金属"。
	/// </para>
	/// </summary>
	public const byte MetallicValue = 255;

	/// <summary>
	/// 烘 matcap 时写进 <c>advanced</c> 槽 2 <b>alpha</b>（粗糙度）的值。
	/// <para>
	/// <b>51（= 0.20）是实机对照选出来的。</b>走过的三个值：
	/// </para>
	/// <list type="bullet">
	/// <item><b>89（0.35）</b>——"氧化过的铝 / 珍珠的反光色，不够亮"。</item>
	/// <item><b>20（0.08）</b>——镀铬镜面。看着更"对"，但<b>实机反而更暗</b>：
	/// 高光瓣太窄，等于把环境探针原样照出来，结果是"几个亮点 + 大片暗"。</item>
	/// <item><b>51（0.20）</b>——高光瓣变宽，把周围更亮的环境积分进来，暗部被填平，
	/// 整体比 0.08 更亮，同时仍保住金属感。</item>
	/// </list>
	/// <para>
	/// <b>结论（反直觉但可复现）：在 HD2 的环境探针下，往"粗糙"方向调反而更亮。</b>
	/// </para>
	/// </summary>
	public const byte RoughnessValue = 51;

	/// <summary><see cref="RoughnessValue"/> 的 0..1 形式（CLI 的 <c>--metal-roughness</c> 默认值）。</summary>
	public const double DefaultMetalRoughness = RoughnessValue / 255.0;

	/// <summary>
	/// 源侧声明了、但 <b>HD2 的材质模板没有对应槽</b>的特性，聚成一句话。
	/// <para>
	/// <b>为什么必须说出来。</b>这些键现在是"读了没人用"或者"根本没读"——用户看到的是
	/// "这件衣服内侧不见了/没有光泽/不发光"，而报告里一个字都没有，
	/// 于是只能靠猜（或者怀疑是我们把东西弄丢了）。
	/// 把"源侧要这个、我们给不了"讲清楚，才算把现象和原因接上。
	/// </para>
	/// <para>
	/// <b>刻意不收 <c>$bumpmap</c>。</b>它几乎每个材质都有，逐条报会把真正稀有的警告淹掉
	/// ——法线图这件事应该按**整包**统计一次（"N 个材质带法线图，尚未接入"），
	/// 那是观测层的事，不在这个逐材质的出口里。
	/// </para>
	/// <para>返回 <c>null</c> 表示这条材质没有声明这类特性（不给报告添噪）。</para>
	/// </summary>
	public static string? DescribeUnexpressibleFeatures(VmtDocument vmt)
	{
		ArgumentNullException.ThrowIfNull(vmt);
		var missing = new List<string>(4);

		// 双面渲染：HD2 的材质模板没有 cull 开关，只能单面。
		if (vmt.IsEnabled("$nocull"))
			missing.Add("$nocull（双面渲染）");

		// 自发光：我们目前只接 `$emissiontexture`（写进 advanced 槽 5 的 alpha）。
		// `$selfillum` 走的是另一条路（底色 alpha / lightwarp），没有接线。
		if (vmt.IsEnabled("$selfillum"))
			missing.Add("$selfillum（自发光）");

		// 高光 / 环境反射：HD2 用 PBR 的粗糙度+金属度表达，无法照搬 Phong 的参数。
		if (vmt.IsEnabled("$phong") || !string.IsNullOrWhiteSpace(vmt.Value("$envmap")))
			missing.Add("$phong/$envmap（高光与环境反射）");

		// 细节层：advanced 有 DetailNormal 槽，但我们没有往下写。
		if (!string.IsNullOrWhiteSpace(vmt.Value("$detail")))
			missing.Add("$detail（细节法线层）");

		// 光照 warp：整体亮度曲线，HD2 没有等价物。
		if (!string.IsNullOrWhiteSpace(vmt.Value("$lightwarptexture")))
			missing.Add("$lightwarptexture（整体亮度曲线）");

		// 法线 alpha 里编码的反射遮罩：没有消费点。
		if (vmt.IsEnabled("$normalmapalphaenvmapmask"))
			missing.Add("$normalmapalphaenvmapmask（反射遮罩）");

		if (missing.Count == 0)
			return null;

		return "源侧还声明了这些 HD2 表达不了的特性，已按最接近的方式处理（观感会有差异）："
			+ string.Join("、", missing);
	}

	/// <summary>判定一份 VMT 的 matcap 情况。</summary>
	public static MatCapVerdict Evaluate(VmtDocument vmt)
	{
		ArgumentNullException.ThrowIfNull(vmt);
		if (!vmt.HasMatCap)
			return new MatCapVerdict(false, 0);
		return new MatCapVerdict(true, ParseTint(vmt.Value("$matcaptint")));
	}

	/// <summary><c>$matcaptint</c> 里参与强度判断的分量个数。第 4 个分量是 <b>alpha</b>，不是强度。</summary>
	public const int TintComponentCount = 3;

	/// <summary>
	/// 解析 <c>$matcaptint</c> / <c>$emissiontint</c>，取<b>前三个分量（RGB）</b>的最大值。
	/// <para>
	/// 源侧会写成 <c>{1 1 1}</c>、<c>"0.9 0.9 0.9"</c>、<c>"255 255 255"</c>、
	/// <c>"[.216 .208 .204 1]"</c> 等几种形式，所以这里做归一化：
	/// 剥掉两种括号、按空格切分、大于 1 的按 0..255 归一到 0..1。
	/// </para>
	/// <para><b>缺失时返回 0</b>——没有 tint 就等于没有 matcap 强度，不该烘。</para>
	/// <para>
	/// <b>⚠️ 三件事必须同时成立，缺一个就会以另一种方式判错</b>（2026-11-03 复盘）：
	/// </para>
	/// <list type="number">
	/// <item>
	/// <b>两种括号都要剥。</b>真实 NekoToon VMT 写的是<b>方括号</b>
	/// （<c>[.216 .208 .204 1]</c>），而这里曾经只剥花括号，于是 <c>[.216</c> 和 <c>1]</c>
	/// 都解析失败 —— 恰好把<b>第 1 个 RGB 分量</b>丢掉了。首分量最大时直接归零：
	/// <c>[1 0 0 1]</c>（纯红 tint）会被判成"没有 matcap"，整件不烘。
	/// </item>
	/// <item>
	/// <b>只取前三个分量。</b>第 4 个是 alpha。花括号写法下它会参与比较：
	/// <c>{.2 .2 .2 1}</c> 曾经取到 <b>1.0</b>（弱 matcap 被当成满强度），
	/// 而 <c>MatCapIsTheSurface</c>（tint ≥ 0.9）是金属判定 / 底色顶掉 / 平均色计算
	/// 三条路的共同入口，一错就连锁错。
	/// </item>
	/// <item>
	/// <b>只接受有限数。</b><c>TryParse</c> 认 <c>NaN</c> / <c>Infinity</c> 的文本形式，
	/// 放进 <c>Max()</c> 会污染整组（NaN 参与比较的结果是不可预期的）。
	/// </item>
	/// </list>
	/// <para>
	/// ⚠️ <b>只做第 1 条（补剥方括号）会主动制造第 2 条那个误判</b>：方括号里的 alpha
	/// 一旦能解析，就会进 <c>Max()</c>。这两个修复必须同批。
	/// </para>
	/// </summary>
	public static double ParseTint(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return 0;

		// 方括号与花括号都是数组写法，两种都要剥；逗号是分隔符的另一种写法。
		var cleaned = raw
			.Replace("[", string.Empty, StringComparison.Ordinal)
			.Replace("]", string.Empty, StringComparison.Ordinal)
			.Replace("{", string.Empty, StringComparison.Ordinal)
			.Replace("}", string.Empty, StringComparison.Ordinal)
			.Replace(",", " ", StringComparison.Ordinal);

		// 按**位置**取前三个成功解析的分量；后面的分量（alpha）直接丢弃，不参与比较。
		var components = cleaned
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
			.Where(value => double.IsFinite(value))
			.Take(TintComponentCount)
			.ToArray();

		if (components.Length == 0)
			return 0;

		var max = components.Max();
		if (max <= 0)
			return 0;
		// 255 制与 0..1 制都要认；用 max 判断而不是固定除以 255，避免把 1.0 当成 0.0039。
		return max > 1.0 ? max / 255.0 : max;
	}

	/// <summary>
	/// 把 matcap 烘进底色：<c>clamp(base + matcapMean * tint)</c>。
	/// <para>
	/// <paramref name="matcapMean"/> 是 matcap 贴图在**该材质所占三角形上**的面积加权平均色
	/// （0..255）。用平均值而不是逐像素采样，是因为 HD2 这边底色和 matcap 没有逐像素对应关系
	/// ——源侧的 matcap 用的是另一套 UV。
	/// </para>
	/// <para>就地改写 RGB；alpha 不动（它的用途不同，且另有清零步骤）。</para>
	/// </summary>
	public static void BakeMatCap(byte[] rgba, double[] matcapMean, double tint)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		ArgumentNullException.ThrowIfNull(matcapMean);
		if (matcapMean.Length < 3)
			throw new ArgumentException("matcap 平均色至少要有 3 个分量。", nameof(matcapMean));
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");
		if (!double.IsFinite(tint) || tint < 0)
			throw new ArgumentOutOfRangeException(nameof(tint), $"tint 必须是非负有限数，实际是 {tint}。");

		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			for (var channel = 0; channel < 3; channel++)
			{
				var baked = rgba[offset + channel] + (matcapMean[channel] * tint);
				rgba[offset + channel] = (byte)Math.Clamp(Math.Round(baked), 0, 255);
			}
		}
	}

	/// <summary>
	/// 把整片 RGB 乘一个增益（clamp 到 0..255），alpha 不动。
	/// <para>
	/// <b>为什么 Albedo 模式也需要它。</b>matcap 当底色时，底色<b>就是</b>那张高光图；
	/// 而高光图的平均亮度远低于它最亮的那块，于是"金属度顶到 1 + 底色偏暗"在 PBR 里
	/// 就等于一块暗金属——实机反馈的"金属几乎没有反光、太黑了"。
	/// </para>
	/// <para>物理上更对的近似是"底色取金属的反射色（偏亮），高光交给 PBR 算"，增益就是往这个方向调。</para>
	/// </summary>
	public static void ScaleRgb(byte[] rgba, double gain)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		if (!double.IsFinite(gain) || gain <= 0)
			throw new ArgumentOutOfRangeException(nameof(gain), $"增益必须是非负有限数，实际是 {gain}。");
		if (gain == 1.0)
			return;
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");

		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			for (var channel = 0; channel < 3; channel++)
				rgba[offset + channel] = (byte)Math.Clamp(Math.Round(rgba[offset + channel] * gain), 0, 255);
		}
	}

	/// <summary>线性 0..1 的反射率 → sRGB 编码后的 0..255（与 BC7_UNORM_SRGB 的像素含义一致）。</summary>
	public static byte LinearToSrgbByte(double linear)
	{
		linear = Math.Clamp(linear, 0, 1);
		var srgb = linear <= 0.0031308
			? linear * 12.92
			: (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
		return (byte)Math.Clamp(Math.Round(srgb * 255.0), 0, 255);
	}

	/// <summary>
	/// 把整片 RGBA 涂成<b>中性金属的 F0</b>：丢掉亮度图案，也<b>丢掉色相</b>，
	/// 整片抬到 <paramref name="targetLinear"/> 这个反射率。
	/// <para>
	/// <b>为什么是"涂平"而不是"乘增益"。</b>实测 serenade 的 <c>matcap_metal7</c> 是双峰的
	/// （中位 66 / p90 231 / 均值 106）。乘 1.6 就有 32% 的像素撞到 255，而均值只到 138
	/// （F0 0.25）；要乘到 5.0 才勉强 F0 0.66，那时 64% 像素已是纯白——图案毁掉了，还是不够亮。
	/// 因为高光那一半先撞顶，换算关系严重非线性。
	/// </para>
	/// <para>
	/// <b>为什么连色相也丢掉（这是实机逼出来的结论，别"优化"回去）。</b>
	/// 曾经用整张图的平均色当色相，于是金色 matcap 出金色、银色出银色。但那些 matcap
	/// <b>不在材质自己的包里</b>：实测 <c>Platinum_Silver</c> 在 A1、<c>11</c> 与
	/// <c>Matcap_diamond4</c> 在 C1/C2、<c>Matcap_metal8</c> 与 <c>Matcap_diamond5/6/7</c>
	/// 在 B1/B2/B3 —— 而 B1/B2/B3 与 C1/C2 都是"可选的颜色变体包"。
	/// 结果就是：<b>同一份源，装白款还是黑款、装金色系还是银色系，金属色都不一样</b>。
	/// </para>
	/// <para>
	/// 我们是<b>转换器</b>，不是给某一个 mod 做优化。所以金属色必须是一个与可选包无关的常量：
	/// 取中性（不冷也不暖），金/银的差别由使用者自己决定要不要保留（目前不保留）。
	/// 实机对照：中性银看着像镀铬、暖一点像镀镍，普通玩家分辨不出来。
	/// </para>
	/// <para>
	/// alpha 一律置 0：底色不承担自发光，也不承担形状。
	/// </para>
	/// </summary>
	public static void FlattenToMetalF0(byte[] rgba, double targetLinear)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");
		if (!double.IsFinite(targetLinear))
			throw new ArgumentOutOfRangeException(nameof(targetLinear), $"目标反射率必须是有限数，实际是 {targetLinear}。");

		var target = LinearToSrgbByte(targetLinear);
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset + 0] = target;
			rgba[offset + 1] = target;
			rgba[offset + 2] = target;
			rgba[offset + 3] = 0;
		}
	}

	/// <summary>造一张 <paramref name="size"/>×<paramref name="size"/> 的纯色 RGBA（用于金属度/粗糙度这类标量槽）。</summary>
	public static byte[] SolidRgba(int size, byte red, byte green, byte blue, byte alpha = 255)
	{
		if (size <= 0)
			throw new ArgumentOutOfRangeException(nameof(size), "尺寸必须为正。");
		var rgba = new byte[checked(size * size * 4)];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset + 0] = red;
			rgba[offset + 1] = green;
			rgba[offset + 2] = blue;
			rgba[offset + 3] = alpha;
		}

		return rgba;
	}
}
