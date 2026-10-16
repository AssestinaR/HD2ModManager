using System.Buffers.Binary;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// BC7 编码（经社区工具链的 <c>texconv.exe</c>）。
/// <para>
/// 这里的关键不是"能编出东西"，而是<b>不肯编的时候要说话</b>：texconv 遇到不认识的输入会
/// 把文件原样复制、退出码仍然是 0。只看"文件存在"就会把未压缩的输入静默混进包里，
/// 上机表现为"贴图能看但完全不对"。所以断言重点在产物必须是<b>带 DX10 头的 BC7</b>。
/// </para>
/// <para>找不到 <c>texconv.exe</c> 时整体跳过——它来自社区工具链，不是每个 checkout 都有。</para>
/// </summary>
public sealed class Bc7EncoderTests
{
	/// <summary>造一份 8×8、未压缩 R8G8B8A8 的 DDS（texconv 认得的输入）。</summary>
	private static byte[] BuildUncompressedDds(int size)
	{
		var header = new byte[128];
		"DDDS"u8.CopyTo(header);   // 占位，下面用 u32 覆盖
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x20534444);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 124);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x000A1007);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)size);   // height
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)size);   // width
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)size);   // pitch
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 1);            // mipCount
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 32);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 0x41);         // DDPF_RGB|ALPHAPIXELS
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), 32);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), 0x00FF0000);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96), 0x0000FF00);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), 0x000000FF);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), 0xFF000000);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108), 0x1000);

		var pixels = new byte[size * size * 4];
		for (var i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = 0x20;
			pixels[i + 1] = 0x80;
			pixels[i + 2] = 0xC0;
			pixels[i + 3] = 0xFF;
		}

		return [.. header, .. pixels];
	}

	[Fact]
	public void EncodeProducesADx10Bc7Dds()
	{
		var texConv = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		if (texConv is null)
			return;

		var encoder = new Bc7Encoder(texConv);
		var output = encoder.EncodeToBc7(BuildUncompressedDds(8), srgb: true);

		Assert.Equal("DX10", System.Text.Encoding.ASCII.GetString(output, 84, 4));
		Assert.Equal(Hd2TextureAsset.Bc7UnormSrgb, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(128)));
		Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(16)));   // width
		Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(12)));   // height
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28)));   // mipCount

		// 8×8 的 BC7 = 2×2 个 4×4 块 × 16B = 64B。
		Assert.Equal(64, output.Length - Hd2TextureAsset.DdsHeaderSize);

		// 编出来的东西必须能直接喂给 Texture 资产的 TocData 构造。
		var texture = new Hd2TextureAsset();
		texture.FromDds(output);
		Assert.Equal("BC7_UNORM_SRGB", texture.FormatName);
		Assert.Equal(64, texture.Pixels.Length);
	}

	[Fact]
	public void LinearEncodingUsesTheNonSrgbFormat()
	{
		var texConv = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		if (texConv is null)
			return;

		var output = new Bc7Encoder(texConv).EncodeToBc7(BuildUncompressedDds(4), srgb: false);
		Assert.Equal(Hd2TextureAsset.Bc7Unorm, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(128)));
		// 4×4 = 一个块 = 16B（1×1 也是 16B，块格式不会更小）。
		Assert.Equal(16, output.Length - Hd2TextureAsset.DdsHeaderSize);
	}

	[Fact]
	public void OneByOneStillOccupiesAFullBlock()
	{
		var texConv = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		if (texConv is null)
			return;

		var output = new Bc7Encoder(texConv).EncodeToBc7(BuildUncompressedDds(1), srgb: false);
		Assert.Equal(16, output.Length - Hd2TextureAsset.DdsHeaderSize);
	}

	/// <summary>
	/// 不认识的输入必须被<b>拒绝</b>，而不是原样复制。这里是拿一段随机字节冒充 DDS：
	/// texconv 可能仍以 0 退出，所以判据只能是"产物是不是真的 BC7"。
	/// </summary>
	[Fact]
	public void GarbageInputIsRejectedRatherThanSilentlyCopied()
	{
		var texConv = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		if (texConv is null)
			return;

		var encoder = new Bc7Encoder(texConv);
		var garbage = new byte[512];
		Random.Shared.NextBytes(garbage);
		// 输入连 DDS 头都不是；要么抛，要么产出的东西不满足 BC7 契约——两者都算通过。
		try
		{
			var output = encoder.EncodeToBc7(garbage, srgb: false);
			Assert.Fail($"垃圾输入竟然编出了 {output.Length} 字节的产物");
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			// 期望路径。
		}
	}

	[Fact]
	public void LocateReturnsNullInsteadOfThrowingWhenTexConvIsAbsent()
	{
		// 显式指一个不存在的路径时不能抛，应当回落到向上搜索；搜不到就返回 null。
		var found = Bc7Encoder.Locate(@"Z:\definitely\not\here\texconv.exe", Path.GetTempPath());
		Assert.True(found is null || File.Exists(found));
	}

	[Fact]
	public void ConstructorRejectsAMissingExecutable()
	{
		Assert.Throws<FileNotFoundException>(() => new Bc7Encoder(@"Z:\definitely\not\here\texconv.exe"));
	}
}
