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
	public bool IsMeaningful => Spread >= SpreadThreshold && Coverage >= CoverageThreshold;

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
		}

		return new AlphaStats(min, max, (double)below / pixels);
	}
}
