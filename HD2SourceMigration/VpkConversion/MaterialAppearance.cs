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
	/// 底色"没有内容"的亮度判据：平均亮度低于它就当整片近黑。
	/// <para>实测那些"观感全来自 matcap"的金属件，底色是 <c>(15,15,15)</c>。</para>
	/// </summary>
	public const double NearBlackMeanLuminance = 40;

	/// <summary>
	/// 底色"没有内容"的平坦度判据：亮度标准差低于它就当整片同色。
	/// <para>
	/// 需要这一条是因为金属装饰件的底色常常是一张**纯色灰图**（实测 <c>gray1</c> / <c>gray</c>），
	/// 既不近黑也不是 1×1，但它确实没有细节——观感全来自 matcap 与 PBR。
	/// 真照片/真贴图的标准差远大于此值，不会误伤。
	/// </para>
	/// </summary>
	public const double FlatLuminanceStdDev = 6;

	/// <summary>
	/// 底色是不是**没有自己的外观**（占位图、整片近黑、或整片同色）。
	/// <para>
	/// <b>为什么光有 <c>tint ≥ 0.9</c> 不够。</b>那一条的原始依据是"底色近黑、观感全来自 matcap"，
	/// 但 <c>tint</c> 高本身并不说明底色没内容——装饰件写 <c>$matcaptint "[1 1 1 1]"</c> 是常态，
	/// 意思是"按全强度叠加 matcap"，底色照样可能是一张真图。
	/// 只看 tint 就会把这类材质误判成"matcap 就是表面"：
	/// 底色被 matcap 顶掉、alphaclip 被关掉，于是**镂空的花瓣变成不透明白方块 + 金属色**
	/// （Lilium_Sancta 的花瓣材质就是这么坏的）。
	/// </para>
	/// </summary>
	public static bool IsContentFreeBase(int width, int height, ReadOnlySpan<byte> rgba)
	{
		if (width <= PlaceholderMaxDimension && height <= PlaceholderMaxDimension)
			return true;
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			return false;

		// 平均亮度与标准差（Rec.601 权重）。一趟遍历算完。
		var pixels = rgba.Length / 4;
		double sum = 0;
		double sumSquares = 0;
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			var luminance = (0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]);
			sum += luminance;
			sumSquares += luminance * luminance;
		}

		var mean = sum / pixels;
		if (mean < NearBlackMeanLuminance)
			return true;
		var variance = Math.Max(0, (sumSquares / pixels) - (mean * mean));
		return Math.Sqrt(variance) < FlatLuminanceStdDev;
	}


	/// <summary>烘 matcap 时写进 <c>advanced</c> 槽 3（<c>IlluminateData</c> = 金属度）的值。</summary>
	public const byte MetallicValue = 191;

	/// <summary>烘 matcap 时写进 <c>advanced</c> 槽 2 <b>alpha</b>（粗糙度）的值。</summary>
	public const byte RoughnessValue = 89;

	/// <summary>判定一份 VMT 的 matcap 情况。</summary>
	public static MatCapVerdict Evaluate(VmtDocument vmt)
	{
		ArgumentNullException.ThrowIfNull(vmt);
		if (!vmt.HasMatCap)
			return new MatCapVerdict(false, 0);
		return new MatCapVerdict(true, ParseTint(vmt.Value("$matcaptint")));
	}

	/// <summary>
	/// 解析 <c>$matcaptint</c>，取三个分量的最大值。
	/// <para>
	/// 源侧会写成 <c>{1 1 1}</c>、<c>"0.9 0.9 0.9"</c>、<c>"255 255 255"</c> 等几种形式，
	/// 所以这里做归一化：带花括号的先去掉，大于 1 的按 0..255 归一到 0..1。
	/// </para>
	/// <para><b>缺失时返回 0</b>——没有 tint 就等于没有 matcap 强度，不该烘。</para>
	/// </summary>
	public static double ParseTint(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return 0;
		var cleaned = raw.Replace("{", string.Empty, StringComparison.Ordinal)
			.Replace("}", string.Empty, StringComparison.Ordinal)
			.Replace(",", " ", StringComparison.Ordinal);
		var values = cleaned
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
			.Where(value => !double.IsNaN(value))
			.ToArray();
		if (values.Length == 0)
			return 0;

		var max = values.Max();
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
