using System.Numerics;

namespace HD2ModAdaptation.Preview;

/// <summary>预览渲染设置。与相机分开：相机决定"从哪看"，这里决定"怎么画"。</summary>
public sealed record PreviewRenderOptions
{
	/// <summary>输出宽度（像素）。</summary>
	public int Width { get; init; } = 1024;

	/// <summary>输出高度（像素）。</summary>
	public int Height { get; init; } = 1024;

	/// <summary>超采样倍数（线性）。2 = 内部 2 倍宽高渲染后 2×2 盒式下采样。</summary>
	public int Supersample { get; init; } = 2;

	/// <summary>背景。透明底输出 alpha，其余填不透明底色。</summary>
	public PreviewBackground Background { get; init; } = PreviewBackground.Transparent;

	/// <summary>按材质槽给不同色相；关掉就是单一黏土色。</summary>
	public bool TintByMaterial { get; init; } = true;

	/// <summary>剔除背面。默认<b>不剔</b>：缠绕方向反了的网格会整块消失，那是比多画更糟的失败。</summary>
	public bool CullBackFaces { get; init; }

	/// <summary>**内部**渲染像素上限（宽×高×超采样²），挡掉误传的巨大分辨率。</summary>
	/// <remarks>
	/// 用内部像素而不是输出像素：1024²/ss2 与 2048²/ss2 分别是 4.2M / 16.7M，
	/// 而 4096²/ss2 是 67M 像素 × 8 字节 ≈ 536 MB —— 必须在分配之前就拒掉。
	/// </remarks>
	public const long MaximumInternalPixels = 4096L * 4096L;

	/// <summary>规范化并对明显错误的设置抛错（不静默夹紧，否则用户不知道图为什么变小了）。</summary>
	public PreviewRenderOptions Validate()
	{
		if (Width <= 0 || Height <= 0)
			throw new ArgumentOutOfRangeException(nameof(Width), "输出尺寸必须是正数。");
		if (Supersample is < 1 or > 4)
			throw new ArgumentOutOfRangeException(nameof(Supersample), "超采样倍数只支持 1..4。");
		if ((long)Width * Height * Supersample * Supersample > MaximumInternalPixels)
			throw new ArgumentOutOfRangeException(
				nameof(Width),
				$"{Width}×{Height} 超采样 {Supersample}× 等于 {(long)Width * Height * Supersample * Supersample} 内部像素，超过 {MaximumInternalPixels} 上限。");
		return this;
	}

	/// <summary>内部渲染尺寸（应用超采样）。</summary>
	public (int Width, int Height) InternalSize
		=> (Width * Supersample, Height * Supersample);
}

/// <summary>背景。</summary>
public enum PreviewBackground
{
	/// <summary>透明底。</summary>
	Transparent = 0,

	/// <summary>中灰。</summary>
	Gray = 1,

	/// <summary>黑。</summary>
	Black = 2,

	/// <summary>白。</summary>
	White = 3,
}

/// <summary>
/// 一张渲染结果：**预乘 alpha** 的 8 位 RGBA。
/// <para>
/// 预乘是为了让超采样下采样在边缘正确（直接平均非预乘颜色会在轮廓外渗出一圈黑边）。
/// 写 PNG 之前再反预乘。
/// </para>
/// </summary>
public sealed class PreviewImage
{
	public PreviewImage(int width, int height, byte[] pixels)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		ArgumentNullException.ThrowIfNull(pixels);
		if (pixels.Length != width * height * 4)
			throw new ArgumentException("像素缓冲长度与尺寸不符。", nameof(pixels));
		Width = width;
		Height = height;
		Pixels = pixels;
	}

	public int Width { get; }

	public int Height { get; }

	/// <summary>预乘 alpha 的 RGBA，逐行从上到下。</summary>
	public byte[] Pixels { get; }

	/// <summary>取某点的预乘颜色（调试与测试用）。</summary>
	public (byte R, byte G, byte B, byte A) this[int x, int y]
	{
		get
		{
			ArgumentOutOfRangeException.ThrowIfNegative(x);
			ArgumentOutOfRangeException.ThrowIfNegative(y);
			if (x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x));
			var offset = (y * Width + x) * 4;
			return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
		}
	}

	/// <summary>被覆盖（alpha &gt; 0）的像素数，用于"图是不是空的"这种自检。</summary>
	public int CoveredPixels()
	{
		var covered = 0;
		for (var index = 3; index < Pixels.Length; index += 4)
		{
			if (Pixels[index] != 0) covered++;
		}
		return covered;
	}

	/// <summary>把预乘缓冲还原成非预乘（写 PNG 前用）。</summary>
	public byte[] ToStraightAlpha()
	{
		var output = new byte[Pixels.Length];
		for (var offset = 0; offset < Pixels.Length; offset += 4)
		{
			var alpha = Pixels[offset + 3];
			if (alpha == 0)
			{
				output[offset] = 0;
				output[offset + 1] = 0;
				output[offset + 2] = 0;
				output[offset + 3] = 0;
				continue;
			}

			var scale = 255f / alpha;
			output[offset] = ClampByte(Pixels[offset] * scale);
			output[offset + 1] = ClampByte(Pixels[offset + 1] * scale);
			output[offset + 2] = ClampByte(Pixels[offset + 2] * scale);
			output[offset + 3] = alpha;
		}
		return output;
	}

	/// <summary>2×2（或 N×N）盒式下采样。输入是预乘的，所以直接平均就是对的。</summary>
	public PreviewImage DownsampleBox(int factor)
	{
		if (factor <= 1) return this;
		var width = Math.Max(1, Width / factor);
		var height = Math.Max(1, Height / factor);
		var output = new byte[width * height * 4];
		var samples = (float)(factor * factor);
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				int r = 0, g = 0, b = 0, a = 0;
				for (var sy = 0; sy < factor; sy++)
				{
					var sourceY = y * factor + sy;
					if (sourceY >= Height) break;
					for (var sx = 0; sx < factor; sx++)
					{
						var sourceX = x * factor + sx;
						if (sourceX >= Width) break;
						var offset = (sourceY * Width + sourceX) * 4;
						r += Pixels[offset];
						g += Pixels[offset + 1];
						b += Pixels[offset + 2];
						a += Pixels[offset + 3];
					}
				}

				var target = (y * width + x) * 4;
				output[target] = ClampByte(r / samples);
				output[target + 1] = ClampByte(g / samples);
				output[target + 2] = ClampByte(b / samples);
				output[target + 3] = ClampByte(a / samples);
			}
		}

		return new PreviewImage(width, height, output);
	}

	private static byte ClampByte(float value)
		=> value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)MathF.Round(value, MidpointRounding.AwayFromZero);
}
