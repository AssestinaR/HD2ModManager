using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// DDS 的 mip 长度与 pitch。这两个字段各有两个语义，混用会产出"头与数据不符"的 DDS，
/// 而 texconv 遇到那种输入会**退出码 0、把输入原样退回** ——
/// 表现离原因非常远（第 23–27 轮追了四轮的"132 字节"就是这个）。
/// </summary>
public sealed class DdsLayoutTests
{
	private const uint Rgba8Unorm = 28;
	private const uint Bc7Unorm = 98;

	[Theory]
	[InlineData(1, 1, Rgba8Unorm, 4)]        // 未压缩：1 像素 × 4
	[InlineData(4, 4, Rgba8Unorm, 64)]
	[InlineData(1, 1, Bc7Unorm, 16)]         // **1×1 也占满一个 BC7 块**
	[InlineData(4, 4, Bc7Unorm, 16)]
	[InlineData(5, 5, Bc7Unorm, 64)]         // ceil(5/4)=2 块 × 2 块 × 16
	[InlineData(1, 8, Bc7Unorm, 32)]         // 1×2 块 × 16
	public void ExpectedMipSizeHandlesBlockAndPixelFormats(int width, int height, uint format, int expected)
	{
		Assert.Equal(expected, Hd2TextureAsset.ExpectedMipSize(width, height, format));
	}

	/// <summary>
	/// **这条就是第 23–27 轮那个 bug 的守卫。**
	/// 把 132 字节（128 头 + 4 像素）当成 1×1 的像素传进来必须被拒，
	/// 而不是静静产出一份 texconv 读不懂的 DDS。
	/// </summary>
	[Fact]
	public void BuildDdsRefusesABufferThatLooksLikeADdsInsteadOfPixels()
	{
		var wrong = new byte[132];   // 128 字节头 + 4 字节像素
		var exception = Assert.Throws<InvalidDataException>(
			() => Hd2TextureAsset.BuildDds(1, 1, Rgba8Unorm, [wrong]));
		Assert.Contains("应当是 4 字节", exception.Message);
		Assert.Contains("实际 132 字节", exception.Message);

		// 正确长度必须能过——守卫不能把对的也拦住。
		var right = Hd2TextureAsset.BuildDds(1, 1, Rgba8Unorm, [new byte[4]]);
		Assert.True(right.Length > 0);
	}

	[Fact]
	public void BuildDdsRefusesAWrongLengthAtAnyMipLevel()
	{
		// mip0（4×4=64 字节）对、mip1（2×2=16 字节）错。
		Assert.Throws<InvalidDataException>(() =>
			Hd2TextureAsset.BuildDds(4, 4, Rgba8Unorm, [new byte[64], new byte[99]]));

		// 两级都对就没问题。
		var ok = Hd2TextureAsset.BuildDds(4, 4, Rgba8Unorm, [new byte[64], new byte[16]]);
		Assert.True(ok.Length > 0);
	}

	/// <summary>
	/// pitch 字段：压缩格式填 <c>width</c>（出货包实测如此），
	/// 未压缩格式必须填 <c>width × 每像素字节数</c>——它是**字节**行距。
	/// </summary>
	[Fact]
	public void PitchIsWidthForBlockFormatsAndByteStrideForUncompressed()
	{
		static uint Read(uint format, int width)
		{
			var dds = Hd2TextureAsset.BuildDds(
				width, 1, format,
				[new byte[Hd2TextureAsset.ExpectedMipSize(width, 1, format)]]);
			return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(20));
		}

		Assert.Equal(8u * 4, Read(Rgba8Unorm, 8));        // 未压缩：8 像素 × 4 字节
		Assert.Equal(256u * 4, Read(Rgba8Unorm, 256));
		// 压缩格式按块的**行**数（每行 4 像素一个块）。
		Assert.Equal(8u, Read(Bc7Unorm, 8));
	}

	[Fact]
	public void UnknownOrZeroSizesAreRejected()
	{
		Assert.Throws<InvalidDataException>(() => Hd2TextureAsset.BuildDds(1, 1, 12345, [new byte[4]]));
		Assert.Throws<ArgumentOutOfRangeException>(() => Hd2TextureAsset.ExpectedMipSize(0, 1, Rgba8Unorm));
		Assert.Throws<InvalidDataException>(() => Hd2TextureAsset.ExpectedMipSize(1, 1, 12345));
	}
}
