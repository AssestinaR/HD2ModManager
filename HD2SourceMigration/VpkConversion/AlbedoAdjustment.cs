namespace HD2SourceMigration.VpkConversion;

public enum AlbedoDarkenMode
{
	/// <summary><c>new = rgb * factor</c>：整体压暗。</summary>
	Multiply,

	/// <summary><c>new = 255 * (rgb/255)^factor</c>：压中间调，保住高光。因子 &gt; 1 才是压暗。</summary>
	Gamma,
}

/// <summary>
/// 把底色贴图压暗。移植自 <c>tmp/rig-lab/darken_albedo.py</c>。
/// <para>
/// <b>为什么需要它。</b>L4D2 的 NekoToon 会把 albedo 乘上一个 <c>≤0.30</c> 的 lightwarp；
/// HD2 的 <c>advanced</c> 是标准 PBR、没有这层上限。直接搬过来会<b>整体发白</b>，
/// 皮肤尤其明显。把 albedo 乘下去是最直接的补偿。
/// </para>
/// <para>
/// <b>只压 albedo，绝不碰法线/金属/粗糙。</b>后三者是线性 PBR 输入，
/// 压它们等于改材质参数而不是改颜色。
/// </para>
/// <para>
/// <b>跳过小图。</b>1×1 / 4×4 的占位图（纯色、灰度格）被压暗只会让"本该纯白的地方变灰"，
/// 所以只处理 <see cref="MinimumSize"/> 以上的贴图。默认 64 与 Python 侧一致。
/// </para>
/// <para>
/// ⚠️ <b>必须作用在 sRGB 编码的像素上</b>（编码成 <c>BC7_UNORM_SRGB</c> 之前）。
/// Python 侧两套配方都自洽（`R8G8B8A8_UNORM` 解 + `BC7_UNORM_SRGB` 编，
/// 或 `_SRGB` 解 + `_SRGB` 编），但**不能混搭**——混了会多做一次传递函数转换。
/// </para>
/// </summary>
public sealed record AlbedoAdjustment(AlbedoDarkenMode Mode, double Factor, int MinimumSize = 64)
{
	/// <summary>默认：乘 0.4（Python 侧的示例值）。</summary>
	public static AlbedoAdjustment Default { get; } = new(AlbedoDarkenMode.Multiply, 0.4);

	/// <summary>小图是占位图，压暗它们只会让本该纯白的地方变灰。</summary>
	public bool AppliesTo(int width, int height) => width >= MinimumSize && height >= MinimumSize;

	/// <summary>
	/// 就地改写 RGBA 的 RGB 通道。<paramref name="rgba"/> 必须是每像素 4 字节。
	/// <b>alpha 不动</b>（它在别处已被清零，而且镂空信息在遮罩贴图里，不在这里）。
	/// </summary>
	public void Apply(byte[] rgba, int width, int height)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");
		if (rgba.Length != checked(width * height * 4))
			throw new InvalidDataException(
				$"像素区长度 {rgba.Length} 与 {width}×{height} 不符，无法安全压暗。");
		if (!AppliesTo(width, height))
			return;
		if (!double.IsFinite(Factor) || Factor <= 0)
			throw new ArgumentOutOfRangeException(nameof(Factor), $"因子必须是正有限数，实际是 {Factor}。");

		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			for (var channel = 0; channel < 3; channel++)
			{
				var value = rgba[offset + channel];
				var adjusted = Mode == AlbedoDarkenMode.Multiply
					? value * Factor
					: 255.0 * Math.Pow(value / 255.0, Factor);
				rgba[offset + channel] = (byte)Math.Clamp(Math.Round(adjusted), 0, 255);
			}
		}
	}
}
