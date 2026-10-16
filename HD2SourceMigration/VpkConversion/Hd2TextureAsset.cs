using System.Buffers.Binary;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// HD2 <c>Texture</c> 资产的 TocData 读写 + DX10 DDS 头构造。
/// 移植自 <c>tmp/rig-lab/hd2_asset.py:210-301</c>（其本身移植自 SDK <c>stingray/texture.py</c>）。
/// <para>
/// 布局（TocData 恒为 340 字节）：
/// </para>
/// <code>
/// u32 UnkId, u32 Unk1(=0), u32 Unk2(=0xFFFFFFFF)
/// 15 × StingrayMipmapInfo(u32 Start, u32 BytesLeft, u16 Height, u16 Width)
/// 148B DDS 头（**必须**带 DX10 扩展）
/// </code>
/// <para>
/// 像素字节在 GPU 侧车里，<b>TocData 里的 mipmap 表按 SDK 的做法整段清零</b>
/// （<c>hd2_asset.py:269</c>；实测参考包的 15 条也全是 0）。
/// </para>
/// <para>
/// 回归测法（Python 侧用的）：对没改过的贴图断言 <c>WriteToc() == 原 TocData</c>。
/// </para>
/// </summary>
public sealed class Hd2TextureAsset
{
	public const int TocSize = 12 + (15 * 12) + DdsHeaderSize;

	public const int DdsHeaderSize = 148;

	public const int MipmapInfoCount = 15;

	private const int DdsHeaderOffset = 12 + (15 * 12);   // 0xC0 = 192

	// DDS 头内的关键偏移。
	private const int DdsHeightOffset = 12;
	private const int DdsWidthOffset = 16;
	private const int DdsPitchOffset = 20;
	private const int DdsMipCountOffset = 28;
	private const int DdsPixelFormatSizeOffset = 76;
	private const int DdsPixelFormatFlagsOffset = 80;
	private const int DdsFourCcOffset = 84;
	private const int DdsCapsOffset = 108;
	private const int DdsDxgiFormatOffset = 128;
	private const int DdsResourceDimensionOffset = 132;
	private const int DdsArraySizeOffset = 140;

	public uint UnkId { get; set; }

	public byte[] DdsHeader { get; set; } = new byte[DdsHeaderSize];

	/// <summary>像素字节（BC 块）。GPU 侧车里的实际内容，长度见 <see cref="Pixels"/> 的赋值方。</summary>
	public byte[] Pixels { get; set; } = [];

	public int Width => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(DdsHeader.AsSpan(DdsWidthOffset)));

	public int Height => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(DdsHeader.AsSpan(DdsHeightOffset)));

	public int MipCount => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(DdsHeader.AsSpan(DdsMipCountOffset)));

	public int ArraySize => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(DdsHeader.AsSpan(DdsArraySizeOffset)));

	public uint DxgiFormat => BinaryPrimitives.ReadUInt32LittleEndian(DdsHeader.AsSpan(DdsDxgiFormatOffset));

	public string FormatName => KnownFormats.TryGetValue(DxgiFormat, out var name) ? name : $"DXGI_{DxgiFormat}";

	/// <summary>常见 DXGI 格式（含 BC1–BC5 全族）。名字来自 <c>hd2_asset.py:63-71</c>。</summary>
	public static readonly IReadOnlyDictionary<uint, string> KnownFormats = new Dictionary<uint, string>
	{
		[70] = "BC1_TYPELESS", [71] = "BC1_UNORM", [72] = "BC1_UNORM_SRGB",
		[73] = "BC2_TYPELESS", [74] = "BC2_UNORM", [75] = "BC2_UNORM_SRGB",
		[76] = "BC3_TYPELESS", [77] = "BC3_UNORM", [78] = "BC3_UNORM_SRGB",
		[79] = "BC4_TYPELESS", [80] = "BC4_UNORM", [81] = "BC4_SNORM",
		[82] = "BC5_TYPELESS", [83] = "BC5_UNORM", [84] = "BC5_SNORM",
		[87] = "B8G8R8A8_UNORM", [88] = "B8G8R8X8_UNORM",
		[28] = "R8G8B8A8_UNORM", [29] = "R8G8B8A8_UNORM_SRGB",
		[98] = "BC7_UNORM", [99] = "BC7_UNORM_SRGB",
	};

	/// <summary>
	/// <b>未压缩</b>格式的每像素字节数。只有出现在这里的格式才按"字节行距"填 DDS 的 pitch 字段。
	/// <para>
	/// 压缩格式填 <c>width</c>（实测如此），未压缩格式必须填 <c>width × 每像素字节数</c> ——
	/// 两者语义不同，不能混用。
	/// </para>
	/// </summary>
	public static readonly IReadOnlyDictionary<uint, int> BytesPerPixel = new Dictionary<uint, int>
	{
		[28] = 4,   // R8G8B8A8_UNORM
		[29] = 4,   // R8G8B8A8_UNORM_SRGB
		[87] = 4,   // B8G8R8A8_UNORM
		[91] = 4,   // B8G8R8A8_UNORM_SRGB
		[61] = 4,   // R8_UNORM
		[49] = 2,   // R8G8_UNORM
	};

	/// <summary>每个 BC 4×4 块占的字节数。<b>1×1 也占满一个块</b>，按 w*h 估算长度会算错。</summary>
	public static readonly IReadOnlyDictionary<uint, int> BlockBytes = new Dictionary<uint, int>
	{
		[70] = 8, [71] = 8, [72] = 8,
		[73] = 16, [74] = 16, [75] = 16,
		[76] = 16, [77] = 16, [78] = 16,
		[79] = 8, [80] = 8, [81] = 8,
		[82] = 16, [83] = 16, [84] = 16,
		[98] = 16, [99] = 16,
	};

	/// <summary>
	/// 某个 mip 级别在给定格式下应当占的字节数。
	/// <para>
	/// 压缩格式按 4×4 块算，且<b>不足 4 像素的边也占满一个块</b>（1×1 就是 16 字节）；
	/// 非压缩按像素算。两个都容易写错，所以只在这里实现一次。
	/// </para>
	/// </summary>
	public static int ExpectedMipSize(int width, int height, uint dxgiFormat)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正。");
		if (BytesPerPixel.TryGetValue(dxgiFormat, out var perPixel))
			return checked(width * height * perPixel);
		if (BlockBytes.TryGetValue(dxgiFormat, out var block))
			return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block);
		throw new InvalidDataException($"格式 {dxgiFormat} 既不在未压缩表也不在块表里，算不出 mip 长度。");
	}

	public const uint Bc7Unorm = 98;

	public const uint Bc7UnormSrgb = 99;

	public static Hd2TextureAsset Read(ReadOnlySpan<byte> tocData, ReadOnlySpan<byte> gpuPixels = default)
	{
		if (tocData.Length < TocSize)
			throw new InvalidDataException($"Texture TocData 只有 {tocData.Length} 字节，应为 {TocSize}。");

		var asset = new Hd2TextureAsset
		{
			UnkId = BinaryPrimitives.ReadUInt32LittleEndian(tocData),
			DdsHeader = tocData.Slice(DdsHeaderOffset, DdsHeaderSize).ToArray(),
			Pixels = gpuPixels.ToArray(),
		};

		// DDS 必须是 DX10 扩展头；没有它就拿不到 dxgiFormat，后续所有格式判断都会错。
		var fourCc = asset.DdsHeader.AsSpan(DdsFourCcOffset, 4);
		if (!fourCc.SequenceEqual("DX10"u8))
			throw new InvalidDataException($"DDS 头必须是 DX10 扩展（实际是 {System.Text.Encoding.ASCII.GetString(fourCc)}）。");
		return asset;
	}

	/// <summary>按 SDK 的做法写 TocData：mipmap 表整段清零。</summary>
	public byte[] WriteToc()
	{
		var output = new byte[TocSize];
		var span = output.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span, UnkId);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0xFFFFFFFF);
		// 12..192 的 15 条 mipmap info 保持零（new byte[] 已经清零）。
		DdsHeader.CopyTo(span[DdsHeaderOffset..]);
		return output;
	}

	public byte[] ToDds() => [.. DdsHeader, .. Pixels];

	public void FromDds(ReadOnlySpan<byte> dds)
	{
		if (dds.Length < DdsHeaderSize || !dds.Slice(DdsFourCcOffset, 4).SequenceEqual("DX10"u8))
			throw new InvalidDataException("DDS 必须带 DX10 扩展头。");
		DdsHeader = dds[..DdsHeaderSize].ToArray();
		Pixels = dds[DdsHeaderSize..].ToArray();
	}

	/// <summary>
	/// 造一个带 DX10 扩展头的 DDS。
	/// <para>
	/// <paramref name="mipChain"/> 是 mip0..mipN 的<b>已压缩</b>块，顺序就是文件顺序。
	/// 注意 HD2 出货只留 mip0（<c>mipCount = 1</c>）。
	/// </para>
	/// </summary>
	public static byte[] BuildDds(int width, int height, uint dxgiFormat, IReadOnlyList<byte[]> mipChain, int arraySize = 1)
	{		// 只要求"是已知格式"：pitch 字段按格式算（见下），所以这里不强制要求有块字节数。
		// 非压缩格式（如 R8G8B8A8_UNORM=28）也要能构造——源 VTF 有相当一部分是未压缩的。
		if (!KnownFormats.ContainsKey(dxgiFormat))
			throw new InvalidDataException($"不支持的 DXGI 格式 {dxgiFormat}。");
		if (mipChain.Count == 0)
			throw new ArgumentException("mip 链不能为空。", nameof(mipChain));

		// ⚠️ **逐级校验 mip 的实际字节数**。
		//
		// 不校验的后果非常隐蔽：DDS 头里写着尺寸与格式、后面却跟了长度不符的数据，
		// texconv 读不出自洽的输入就**走兜底路径、把输入原样输出**（退出码仍是 0），
		// 最终表现为"某个材质转换失败，产物只有 128+4 字节"。
		// 第 23–26 轮排查那个现象花了四轮，就是因为这里没有断言、坏输入被静静放行。
		//
		// 压缩与非压缩的算法不同：压缩按 4×4 块、**1×1 也占满一个块**；
		// 非压缩按像素。两者都容易写错，所以这里用同一处实现算期望值。
		var expectedWidth = width;
		var expectedHeight = height;
		for (var level = 0; level < mipChain.Count; level++)
		{
			var expected = ExpectedMipSize(expectedWidth, expectedHeight, dxgiFormat);
			if (mipChain[level].Length != expected)
			{
				throw new InvalidDataException(
					$"mip {level}（{expectedWidth}×{expectedHeight}，格式 {dxgiFormat}）应当是 {expected} 字节，" +
					$"实际 {mipChain[level].Length} 字节。头与数据不符的 DDS 会被 texconv 原样退回，必须先修数据。");
			}

			expectedWidth = Math.Max(1, expectedWidth / 2);
			expectedHeight = Math.Max(1, expectedHeight / 2);
		}

		// caps|height|width|pitch|linearsize，有 mip 链时再加 mipmapcount。
		var flags = 0x1u | 0x2u | 0x4u | 0x1000u | 0x80000u;
		if (mipChain.Count > 1)
			flags |= 0x20000u;

		var header = new byte[DdsHeaderSize];
		var span = header.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444);   // "DDS " 的 LE u32
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], flags);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsHeightOffset..], checked((uint)height));
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsWidthOffset..], checked((uint)width));
		// pitch 字段：**压缩格式填 width，未压缩格式填 width × 每像素字节数**。
		//
		// 依据：出货包里实测压缩格式就是 width（见 MATERIAL_PIPELINE_SPEC.md §3.1），
		// 且真正走 VTF→DDS→texconv 的那个构造器（`hd2_vtf.py:205`）也写 width。
		// 只有 `hd2_asset.py:289` 写块行字节数，那条不是生产路径——两边不一致时以实测与生产路径为准。
		//
		// ⚠️ 但未压缩格式不能照搬：DDS 头的 pitch 是**字节**行距。RGBA8 若填 width，
		// texconv 会按 width 字节推断出错误的行距，从而**放弃编码、把输入原样拷出来**
		// （表现为"texconv 产物只有 128+4 字节"）。这正是第 23 轮那次回归的根因。
		var pitch = BytesPerPixel.TryGetValue(dxgiFormat, out var perPixel)
			? checked((uint)(width * perPixel))
			: checked((uint)width);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsPitchOffset..], pitch);
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsMipCountOffset..], checked((uint)mipChain.Count));
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsPixelFormatSizeOffset..], 32);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsPixelFormatFlagsOffset..], 0x4);   // DDPF_FOURCC
		"DX10"u8.CopyTo(span[DdsFourCcOffset..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsCapsOffset..], 0x1000);            // DDSCAPS_TEXTURE
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsDxgiFormatOffset..], dxgiFormat);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsResourceDimensionOffset..], 3);    // TEXTURE2D
		BinaryPrimitives.WriteUInt32LittleEndian(span[136..], 0);
		BinaryPrimitives.WriteUInt32LittleEndian(span[DdsArraySizeOffset..], checked((uint)arraySize));
		BinaryPrimitives.WriteUInt32LittleEndian(span[144..], 0);

		var pixels = mipChain.Sum(mip => (long)mip.Length);
		var output = new byte[DdsHeaderSize + pixels];
		header.CopyTo(output, 0);
		var cursor = DdsHeaderSize;
		foreach (var mip in mipChain)
		{
			mip.CopyTo(output, cursor);
			cursor += mip.Length;
		}

		return output;
	}
}
