using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 最小复现：**"先解码过一张贴图"这件事会不会改变后续编码的结果**。
/// <para>
/// 背景（第 23–25 轮）：打开 matcap 接线之后，真实模型上出现
/// 「同一张 1×1 RGB888 贴图，先被 matcap 解码走过一次，再走底色编码就失败」，
/// 失败表现是 texconv **把输入原样拷了出来**（产物 128+4 字节，不是 BC7）。
/// </para>
/// <para>
/// 这里把那个现象压到<b>不需要游戏数据</b>的最小形式：一张合成的 1×1 <c>R8G8B8A8</c> DDS，
/// 分别（a）直接编码、（b）先解码一次再编码。两条路径的产物**必须一致** ——
/// 编码器不该有"被谁先调用过"这种隐藏状态。
/// </para>
/// <para>texconv 缺失时整体跳过。</para>
/// </summary>
public sealed class Bc7TinyTextureTests
{
	private const uint Rgba8Unorm = 28;

	private static Bc7Encoder? OpenEncoder()
	{
		var path = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		return path is null ? null : new Bc7Encoder(path);
	}

	/// <summary>一张 1×1 R8G8B8A8 的 DDS —— 128 字节头 + 4 字节像素。</summary>
	private static byte[] TinyDds(byte r = 10, byte g = 20, byte b = 30, byte a = 255) =>
		Hd2TextureAsset.BuildDds(1, 1, Rgba8Unorm, [[r, g, b, a]]);

	/// <summary>(a) 对照组：直接编码。这条一直是通过的。</summary>
	[Fact]
	public void EncodingATinyTextureDirectlyProducesBc7()
	{
		var encoder = OpenEncoder();
		if (encoder is null)
			return;

		var bc7 = encoder.EncodeToBc7(TinyDds(), srgb: true);
		Assert.True(bc7.Length >= 148, $"BC7 产物只有 {bc7.Length} 字节，texconv 大概把输入原样拷了出来");
		// 148 = 完整 DX10 头（4 魔数 + 124 头 + 20 的 DX10 扩展），16 = 1×1 也占满一个 BC7 块。
		// **不是 128**：128 是没有 DX10 扩展的传统头——那个长度出现就意味着 texconv 没编码。
		Assert.Equal(164, bc7.Length);
	}

	/// <summary>
	/// (b) 复现点：**先解码一次同一张**，再编码。
	/// <para>
	/// 期望与 (a) 完全一致。如果这条红了，就说明编码器有隐藏状态，
	/// 而且根因在 <c>Bc7Encoder.Transcode</c> 的进程/临时文件处理上
	/// （GUID 临时目录、固定输入名 <c>input.dds</c>、<c>-o</c> 指向工作目录即原地覆盖）。
	/// </para>
	/// </summary>
	[Fact]
	public void DecodingFirstDoesNotChangeTheEncodingResult()
	{
		var encoder = OpenEncoder();
		if (encoder is null)
			return;

		var source = TinyDds();
		var fresh = encoder.EncodeToBc7(source, srgb: true);

		// 先解码一次**同一张**，再来编码。
		_ = encoder.DecodeToRgba(source);
		var afterDecode = encoder.EncodeToBc7(source, srgb: true);

		Assert.Equal(fresh.Length, afterDecode.Length);
		Assert.True(afterDecode.AsSpan().SequenceEqual(fresh),
			$"先解码一次之后产物变了（{fresh.Length} → {afterDecode.Length} 字节）——编码器有隐藏状态");
	}

	/// <summary>
	/// (c) 更贴近真实调用顺序：先解码**另一张**（模拟 matcap），再解码+编码目标那张。
	/// <para>
	/// 真实那条路是"先为一批 matcap 各解码一次，再来转换底色"，所以顺序是
	/// <c>decode(别的) → decode(目标) → encode(目标)</c>。
	/// </para>
	/// </summary>
	[Fact]
	public void DecodingADifferentTextureFirstDoesNotChangeTheEncodingResult()
	{
		var encoder = OpenEncoder();
		if (encoder is null)
			return;

		var target = TinyDds(10, 20, 30);
		var fresh = encoder.EncodeToBc7(target, srgb: true);

		_ = encoder.DecodeToRgba(TinyDds(200, 100, 50));
		var afterOtherDecode = encoder.EncodeToBc7(target, srgb: true);

		Assert.True(afterOtherDecode.AsSpan().SequenceEqual(fresh),
			$"先解码过另一张之后产物变了（{fresh.Length} → {afterOtherDecode.Length} 字节）");
	}

	/// <summary>
	/// (d) 真实序列的完整形状：解码(目标) → 解码(目标) → 编码(目标)。
	/// matcap 那条路对同一张 1×1 贴图可能既解码又编码，所以把重复解码也覆盖上。
	/// </summary>
	[Fact]
	public void RepeatedDecodesDoNotChangeTheEncodingResult()
	{
		var encoder = OpenEncoder();
		if (encoder is null)
			return;

		var target = TinyDds(10, 20, 30);
		var fresh = encoder.EncodeToBc7(target, srgb: true);

		for (var i = 0; i < 3; i++)
			_ = encoder.DecodeToRgba(target);
		var afterRepeats = encoder.EncodeToBc7(target, srgb: true);

		Assert.True(afterRepeats.AsSpan().SequenceEqual(fresh),
			$"重复解码之后产物变了（{fresh.Length} → {afterRepeats.Length} 字节）");
	}
}
