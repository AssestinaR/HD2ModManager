namespace HD2SourceMigration.VpkConversion;

/// <summary>alpha 通道的统计量。</summary>
/// <param name="Min">最小值（0..255）。</param>
/// <param name="Max">最大值（0..255）。</param>
/// <param name="Coverage">alpha &lt; 250 的像素占比（0..1）。</param>
public readonly record struct AlphaStats(byte Min, byte Max, double Coverage)
{
	/// <summary>动态范围。</summary>
	public double Spread => Max - Min;

	/// <summary>
	/// 这片 alpha 是否<b>真的</b>在表达镂空。
	/// <para>
	/// 两个阈值缺一不可（<c>fix_appearance.py:95-98</c>）：
	/// </para>
	/// <list type="bullet">
	/// <item><b>Spread ≥ 3</b>：挡住"整片都是同一个值"的假 alpha。DXT1 解出来恒为 255，
	/// 只看覆盖率会被误判成"有 alpha"。</item>
	/// <item><b>Coverage ≥ 0.5%</b>：挡住"只有零星几个像素透明"的噪声。</item>
	/// </list>
	/// <para>
	/// 这条判据存在的理由很具体：装饰件常写 <c>$alphatest 1</c>，但贴图是 1×1 无 alpha 的
	/// RGB888。**只看 VMT 开关会误判**——M7 就是只按开关判、漏了 3 组。
	/// </para>
	/// </summary>
	public bool IsMeaningful => Spread >= SpreadThreshold && Coverage >= CoverageThreshold
		&& VisibleCoverage >= MinimumVisibleCoverage;

	/// <summary>
	/// 这片 alpha <b>基本是空的</b>（整片 ≈ 0）。
	/// <para>
	/// <b>它不等于「整片半透明」。</b>半透明的意思是"还能看见，只是淡"；而 alpha 恒为 0 的
	/// 含义是"这一件完全不画"。作者的薄纱裙不会写成 0 —— 所以这份数据在绝大多数情况下
	/// 表示<b>"没有 alpha 数据"</b>（VTF 没有 alpha 通道、或解码出来就是 0），而不是作者的意图。
	/// </para>
	/// <para>
	/// 实测事故（2026-11-04，`二阶堂希罗` 的 `hirocloth2` 裙子）：alpha 整片 ≈ 0，
	/// 被 <see cref="IsUniformlyTranslucent"/> 认成"整体半透明" → 走 `alphaclip` →
	/// 遮罩整张黑 → <b>裙子整片消失</b>，报告里一个字都没有。把阈值降到 0.144 也没用，
	/// 因为 0 &lt; 0.144 同样被裁。所以这一档必须单独识别出来。
	/// </para>
	/// </summary>
	public bool IsEmpty => Max <= EmptyMax;

	/// <summary>
	/// 这片 alpha <b>几乎全是不透明</b>（≥95% 的像素 alpha ≥ 250）。
	/// <para>
	/// <b>为什么需要这条。</b>有些作者把「低不透明度」当「不透明」写：整片 alpha 恒定在
	/// 180~240 之间。它会被 <see cref="IsUniformlyTranslucent"/> 认成"整体半透明"，
	/// 于是走 alphaclip + 抖动 —— 实机表现是<b>整件身体变半透明、还带一层噪点</b>
	/// （远看像满脸雀斑）。作者的原意显然不是这样，所以这一档直接判不透明。
	/// </para>
	/// <para>口径按<b>像素占比</b>，不按平均值：一块小黑点不该把整张图拉成半透明。</para>
	/// </summary>
	public bool IsEffectivelyOpaque => Coverage <= EffectiveOpaqueCoverageMax;

	/// <summary>「几乎全不透明」阈值：非不透明像素（alpha &lt; 250）占比 ≤ 5%。</summary>
	public const double EffectiveOpaqueCoverageMax = 0.05;

	/// <summary>
	/// 这片 alpha <b>几乎全是透明</b>（≥95% 的像素 alpha &lt; 128）。
	/// <para>
	/// 这一档既不该按不透明渲染（会得到实心板），也不该走抖动（会得到噪点壳）；
	/// 它交给 HD2 的 <c>translucent</c> 模板做成<b>纯色透明</b>（玻璃/塑料观感）。
	/// </para>
	/// <para>
	/// <b>必须排除 <see cref="IsEmpty"/>。</b>"整片 0"是"没有 alpha 数据"，不是"作者要它透明"：
	/// 前者按隐藏处理，后者才该给透明材质。
	/// </para>
	/// </summary>
	public bool IsEffectivelyTransparent => !IsEmpty && TransparentShare >= EffectiveTransparentShareMin;

	/// <summary>「几乎全透明」阈值：真正透明的像素（alpha &lt; 32）占比 ≥ 95%。</summary>
	public const double EffectiveTransparentShareMin = 0.95;

	/// <summary>低于这个值的 alpha 视为"没有 alpha 数据"，而不是"很透明"。</summary>
	public const byte EmptyMax = 8;

	/// <summary>
	/// 这片 alpha 是<b>整片均匀半透明</b>（没有形状，只是整体压了一个不透明度）。
	/// <para>
	/// <b>为什么必须单独认这一类。</b><see cref="IsMeaningful"/> 要求 <c>Spread ≥ 3</c>，
	/// 于是"整张图 alpha 恒为 102"会被判成"没有 alpha"——可它恰恰是源侧最直白的一种表达：
	/// <b>这个表面整体是 40% 不透明</b>。
	/// </para>
	/// <para>
	/// 实测代价：MourningVeil 的鞋跟（<c>mat_gem1_shoes</c>）在 L4D2 里是<b>半透明玻璃</b>，
	/// 底图 alpha 恒为 102；我们把它判成不透明 → 实机变成一片不透明白塑料，
	/// 用户看到的就是"玻璃鞋跟变成了金属/塑料"。
	/// </para>
	/// <para>
	/// 阈值取 <c>Max ≤ 200</c>（≥ 22% 透明）而不是 <c>&lt; 255</c>：差几个 8 位量化单位的
	/// 不透明图很常见，把那种当半透明会让"本该不透明的东西突然透了"——那个方向的误判更难查。
	/// </para>
	/// <para>
	/// <b>必须排除 <see cref="IsEmpty"/>。</b>"整片 0"不是"整片半透明"：前者走 alphaclip 会把
	/// 整件裁光（遮罩全黑），后者才该走。见 <see cref="IsEmpty"/> 里那起实测事故。
	/// </para>
	/// </summary>
	public bool IsUniformlyTranslucent => !IsEmpty
		&& Spread < SpreadThreshold
		&& Max >= MinimumUniformTranslucentAlpha
		&& Max <= UniformTranslucentMax;

	/// <summary>
	/// 均匀 alpha 的<b>下限</b>：低于它就不是"这件东西整体半透明"，而是"几乎没有不透明信息"。
	/// <para>
	/// <b>实测事故（2026-11-04，`二阶堂希罗` 的裙子）。</b>整包 37 个材质的底色 alpha 都恒为
	/// <c>13</c>（≈5% 不透明、零动态范围）。旧判据把它当"整体半透明 5%" → <c>alphaclip</c> →
	/// 抖动后只剩约 5% 的像素 → <b>整条裙子在游戏里干净地消失</b>，而报告里一个字都没有。
	/// 一件衣服不可能是 95% 透明的，所以这一档必须按"没有 alpha 数据"处理。
	/// </para>
	/// <para>
	/// 取 26（≈10%）：实测真半透明的玻璃鞋跟是 <c>102</c>（40%），离这条线很远；
	/// 而 5% 这一档在任何真实资产里都不是"设计"，是数据问题。
	/// </para>
	/// </summary>
	public const byte MinimumUniformTranslucentAlpha = 26;

	/// <summary>
	/// 遮罩里<b>能看见</b>的像素占比（alpha ≥ 128 的部分）。
	/// <para>
	/// <b>为什么需要它。</b><see cref="IsMeaningful"/> 原来只要求"有 0.5% 的像素不是全不透明"，
	/// 于是「99.5% 全透明、0.5% 是亮的」这种图会被判成"alpha 在表达形状"——可它的实际效果是
	/// <b>整件东西几乎全被裁掉</b>。实测（2026-11-04，`二阶堂希罗` 的裙子）：动态范围 0..255 齐全、
	/// 覆盖率 99.5%，判据全过，进游戏后裙子干净地消失。
	/// </para>
	/// <para>
	/// 真正的镂空件（蕾丝 / 镂空花纹）绝大多数像素是可见的，只有一小部分是"眼"。
	/// 所以"能看见的比例"才是那个该被守住的量：低于 <see cref="MinimumVisibleCoverage"/> 时，
	/// 这份 alpha 更像数据问题，而不是作者的形状。
	/// </para>
	/// </summary>
	public double VisibleCoverage { get; init; } = 1.0;

	/// <summary>
	/// **真正透明**的像素占比（alpha &lt; <see cref="TransparentAlphaMax"/>）。
	/// <para>
	/// 与 <see cref="VisibleCoverage"/> 的区别是判据方向：后者问"有多少看得见"，这里问
	/// "有多少根本没画"。**不能用 `1 - VisibleCoverage` 代替**：整片 alpha=102 的玻璃件
	/// （40% 不透明）在 VisibleCoverage 里算 0，会被误当成"几乎全透明"。
	/// </para>
	/// </summary>
	public double TransparentShare { get; init; }

	/// <summary>低于这个 alpha 视为"这一像素没画"。取 32 ≈ 12%。</summary>
	public const byte TransparentAlphaMax = 32;

	/// <summary>可见像素占比的下限。低于它就不认为 alpha 在表达形状。</summary>
	public const double MinimumVisibleCoverage = 0.05;

	/// <summary>整片均匀半透明时 alpha 的上限（≥ 22% 透明才算）。</summary>
	public const byte UniformTranslucentMax = 200;

	public const double SpreadThreshold = 3.0;

	public const double CoverageThreshold = 0.005;
}

/// <summary>从未压缩 RGBA 像素量 alpha 的分布。</summary>
public static class TextureAlpha
{
	/// <summary>量一片 RGBA 像素的 alpha。<paramref name="rgba"/> 必须是每像素 4 字节。</summary>
	public static AlphaStats Measure(ReadOnlySpan<byte> rgba)
	{
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");

		byte min = 255;
		byte max = 0;
		var below = 0;
		var visible = 0;
		var transparent = 0;
		var pixels = rgba.Length / 4;
		for (var offset = 3; offset < rgba.Length; offset += 4)
		{
			var alpha = rgba[offset];
			if (alpha < min)
				min = alpha;
			if (alpha > max)
				max = alpha;
			if (alpha < 250)
				below++;
			if (alpha >= 128)
				visible++;
			if (alpha < AlphaStats.TransparentAlphaMax)
				transparent++;
		}

		return new AlphaStats(min, max, (double)below / pixels)
		{
			VisibleCoverage = (double)visible / pixels,
			TransparentShare = (double)transparent / pixels,
		};
	}
}
