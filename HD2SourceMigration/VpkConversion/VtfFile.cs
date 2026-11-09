using System.Buffers.Binary;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// Valve VTF 7.x 读取器 → 抽高分辨率 mip 链（供包成 DX10 DDS 交给 HD2 的 <c>Texture</c>）。
/// 移植自 <c>tmp/rig-lab/hd2_vtf.py</c>。
/// <para><b>头布局（实测值，注意几处与直觉不同）：</b></para>
/// <code>
/// v7.0 / v7.1 headerSize=64   v7.2 headerSize=80   v7.3 headerSize=80   v7.4 headerSize=88~96
/// +0   char[4] 签名 'VTF\0'      +4   u32 major      +8   u32 minor
/// +12  u32 headerSize
/// +16  u16 width                 +18  u16 height
/// +20  u32 flags                 +24  u16 frames     +26  u16 firstFrame
/// +32  f32[4] reflectivity       +48  f32 bumpScale
/// +52  u32 highResFormat         +56  u8  mipCount
/// +57  u8  lowResFormat          +61  u8  lowResWidth +62 u8 lowResHeight
/// +63  u16 depth                 ← **只有 v7.2 及以后才有**
/// </code>
/// <para>
/// <c>reflectivity</c> 从 32 起（不是 28）、<c>highResFormat</c> 从 52 起（不是 48）
/// —— <c>firstFrame</c> 与 <c>reflectivity</c> 之间还有 4 个字节。
/// </para>
/// <para>
/// <b><c>depth</c> 是 v7.2 才加的字段</b>（官方 <c>vtf.h</c>：<c>VTFFileHeaderSize(7,0)</c> 与
/// <c>(7,1)</c> 都等于 <c>sizeof(VTFFileHeaderV7_1_t)</c>，而 <c>depth</c> 在
/// <c>VTFFileHeaderV7_2_t</c> 里）。v7.0 / v7.1 的头部到 <c>+62</c> 就结束了，
/// <c>+63</c> 那两个字节其实是<b>低分辨率缩略图的首字节</b> —— 无条件读它会把 5 889…65 281
/// 这种压缩像素当成"层数"，链长算到 GB 级（2026-11-09 的 <c>牛牛rebone替换路易斯.vpk</c>：
/// 30 张贴图里 21 张 v7.1 全中，整包转换失败）。
/// </para>
/// <para><b>图像块布局（这是最容易错、且错了不报错的地方）：</b></para>
/// <code>
/// [ 低分辨率缩略图 ][ mip N-1 ] ... [ mip 1 ][ mip 0 ]
/// </code>
/// <para>
/// 缩略图在<b>最前</b>，高分辨率链<b>从小到大</b>，所以真正的全分辨率图像是整块<b>尾部</b>的
/// <c>mip_size(width, height)</c> 个字节，不是开头。从前往后读（当成从大到小）会得到
/// <b>"mip 金字塔"</b>：小层级被当成扫描线，画面重复出现、每条带宽是上一条的一半。
/// 这正是第一批贴图被打坏的原因（见 <c>hd2_vtf.py:37-38</c>）。
/// </para>
/// <para>
/// <b>不支持的格式一律返回 null，绝不静默降级</b>：非压缩 VTF 必须按格式拆包，
/// 硬当 BC 用会静默换通道（<c>hd2_texpack.py:118-149</c> 才做这件事）。
/// </para>
/// </summary>
public sealed record VtfFile
{
	public const int MinimumHeaderSize = 64;

	/// <summary>唯一受支持的主版本（7.0–7.4 共用一个头布局前缀，差别只在深度字段与资源目录）。</summary>
	public const int SupportedMajorVersion = 7;

	/// <summary><c>depth</c> 字段最早出现的次版本号：7.2 之前没有这个字段。</summary>
	public const int FirstMinorVersionWithDepth = 2;

	/// <summary>可信的层数上限；超过它一律按"头部损坏"报错，而不是让乘法溢出。</summary>
	public const int MaxDepth = 2048;

	/// <summary>这个次版本的头里有没有 <c>depth</c> 字段（7.0 / 7.1 没有）。</summary>
	public static bool HasDepthField(int minor) => minor >= FirstMinorVersionWithDepth;

	public int Major { get; private init; }

	public int Minor { get; private init; }

	public int HeaderSize { get; private init; }

	public int Width { get; private init; }

	public int Height { get; private init; }

	public uint Flags { get; private init; }

	public int Frames { get; private init; }

	public int FirstFrame { get; private init; }

	public float BumpScale { get; private init; }

	public uint HighResFormat { get; private init; }

	public byte LowResFormat { get; private init; }

	public int MipCount { get; private init; }

	public int LowResWidth { get; private init; }

	public int LowResHeight { get; private init; }

	public int Depth { get; private init; }

	/// <summary>
	/// <c>mip0</c> 到 <c>mipN-1</c>，<b>已按正确顺序还原</b>（下标 0 = 全分辨率）。
	/// <para>
	/// <b>每层只含一帧</b>：多帧 VTF 取第 0 帧，长度恒等于 <see cref="MipSize"/>(该层尺寸)。
	/// 多帧图（L4D2 的换装贴图靠 VMT 的 <c>$frame</c> 随机选一套）在 HD2 侧没有对应机制，
	/// 只能固定取一帧；而"每层塞进全部帧"的旧行为会让下游按单帧算的长度校验失败
	/// （<c>SourceMaterialConverter.BuildSourceDds</c> → <c>Hd2TextureAsset.BuildDds</c>），
	/// 表现为整张贴图被退回、材质退成兜底灰。
	/// </para>
	/// </summary>
	public IReadOnlyList<byte[]> Mips { get; private init; } = [];

	/// <summary>低分辨率缩略图原始字节（未解码）。</summary>
	public byte[] LowRes { get; private init; } = [];

	/// <summary>图像块之后剩余的字节数。实测 Lunalice 的包应当为 0。</summary>
	public int Trailing { get; private init; }

	public string FormatName => Formats.TryGetValue(HighResFormat, out var name) ? name : $"FMT{HighResFormat}";

	/// <summary>
	/// 造一个<b>只有尺寸</b>的占位 VTF。
	/// <para>
	/// 用途只有一个：源材质<b>没有 <c>$basetexture</c></b> 时（NekoToon 的金属/宝石件把
	/// <c>$basetexture</c> 注释掉，观感 100% 来自 <c>$matcapture</c>），
	/// 我们要合成一张纯色底色，而下游（<see cref="SourceMaterialConverter"/>）按
	/// <see cref="Width"/>/<see cref="Height"/> 建 DDS。给它一个"只有尺寸"的对象，
	/// 比给一堆假字节更不容易被误当成真像素。
	/// </para>
	/// <para>拿它去读 <see cref="Mips"/> 一定拿到空数组——不要那样用。</para>
	/// </summary>
	public static VtfFile Synthetic(int width, int height)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), "合成 VTF 的尺寸必须为正。");
		return new VtfFile { Width = width, Height = height, MipCount = 1, Frames = 1 };
	}

	/// <summary>VTF 格式号 → 名字。含两个非标准的 L4D2/CSGO 压缩掩码（37=BC5、38=BC4）。</summary>
	public static readonly IReadOnlyDictionary<uint, string> Formats = new Dictionary<uint, string>
	{
		[0] = "RGBA8888", [1] = "ABGR8888", [2] = "RGB888", [3] = "BGR888", [4] = "RGB565",
		[5] = "I8", [6] = "IA88", [7] = "P8", [8] = "A8", [9] = "RGB888_BLUESCREEN",
		[10] = "BGR888_BLUESCREEN", [11] = "ARGB8888", [12] = "BGRA8888", [13] = "DXT1",
		[14] = "DXT3", [15] = "DXT5", [16] = "BGRX8888", [17] = "BGR565", [18] = "BGRX5551",
		[19] = "BGRA4444", [20] = "DXT1_ONEBITALPHA", [21] = "BGRA5551", [22] = "UV88",
		[23] = "UVWQ8888", [24] = "RGBA16161616F", [25] = "RGBA16161616", [26] = "UVLX8888",
		[37] = "BC5_ATI2N", [38] = "BC4_ATI1N",
	};

	/// <summary>格式号 → (每像素字节, 块字节 或 0, 块边长)。</summary>
	private static readonly IReadOnlyDictionary<uint, (int BytesPerPixel, int BlockBytes, int BlockSide)> FormatInfo =
		new Dictionary<uint, (int, int, int)>
		{
			[0] = (4, 0, 0), [1] = (4, 0, 0), [2] = (3, 0, 0), [3] = (3, 0, 0), [4] = (2, 0, 0),
			[5] = (1, 0, 0), [6] = (2, 0, 0), [7] = (1, 0, 0), [8] = (1, 0, 0),
			[9] = (3, 0, 0), [10] = (3, 0, 0), [11] = (4, 0, 0), [12] = (4, 0, 0),
			[13] = (0, 8, 4), [14] = (0, 16, 4), [15] = (0, 16, 4),
			[16] = (4, 0, 0), [17] = (2, 0, 0), [18] = (2, 0, 0), [19] = (2, 0, 0),
			[20] = (0, 8, 4), [21] = (2, 0, 0), [22] = (2, 0, 0), [23] = (4, 0, 0),
			[24] = (8, 0, 0), [25] = (8, 0, 0), [26] = (4, 0, 0),
			[37] = (0, 16, 4), [38] = (0, 8, 4),
		};

	/// <summary>VTF 压缩格式 → DXGI（sRGB 变体）。只有 BC 系列是精确映射，其余要走 texconv。</summary>
	private static readonly IReadOnlyDictionary<uint, uint> ToDxgiSrgb = new Dictionary<uint, uint>
	{
		[13] = 72,   // DXT1 -> BC1_UNORM_SRGB
		[20] = 72,   // DXT1_ONEBITALPHA -> BC1_UNORM_SRGB
		[14] = 75,   // DXT3 -> BC2_UNORM_SRGB
		[15] = 78,   // DXT5 -> BC3_UNORM_SRGB
		[37] = 83,   // BC5 -> BC5_UNORM（线性：法线图）
		[38] = 80,   // BC4 -> BC4_UNORM（线性：掩码）
	};

	private static readonly IReadOnlyDictionary<uint, uint> ToDxgiLinear = new Dictionary<uint, uint>
	{
		[13] = 71, [20] = 71, [14] = 74, [15] = 77, [37] = 83, [38] = 80,
	};

	/// <summary>该格式是否按 4×4 块压缩。<b>1×1 也占满一个块。</b></summary>
	public static bool IsBlockCompressed(uint format) =>
		FormatInfo.TryGetValue(format, out var info) && info.BlockBytes != 0;

	/// <summary>映射到 DXGI 格式号；非 BC 格式返回 <c>null</c>（调用方必须自己处理，别当 BC 用）。</summary>
	public static uint? ToDxgi(uint format, bool linear = false) =>
		(linear ? ToDxgiLinear : ToDxgiSrgb).TryGetValue(format, out var dxgi) ? dxgi : null;

	/// <summary>一个 mip 层的字节数。块格式按块数算，<c>1×1</c> 也是整整一个块。</summary>
	public static int MipSize(int width, int height, uint format)
	{
		if (!FormatInfo.TryGetValue(format, out var info))
			throw new InvalidDataException($"未知的 VTF 格式 {format}，无法计算 mip 尺寸。");
		if (info.BlockBytes == 0)
			return checked(width * height * info.BytesPerPixel);
		var blocksWide = Math.Max(1, (width + info.BlockSide - 1) / info.BlockSide);
		var blocksHigh = Math.Max(1, (height + info.BlockSide - 1) / info.BlockSide);
		return checked(blocksWide * blocksHigh * info.BlockBytes);
	}

	public static VtfFile Read(ReadOnlySpan<byte> data)
	{
		if (data.Length < MinimumHeaderSize)
			throw new InvalidDataException($"VTF 只有 {data.Length} 字节，头部至少要 {MinimumHeaderSize}。");
		if (!data[..4].SequenceEqual("VTF\0"u8))
			throw new InvalidDataException("不是 VTF（签名不是 'VTF\\0'）。");

		// **版本先读，再决定有没有 depth。**
		//
		// 7.x 之外的头布局没有任何依据（签名相同、后续字段全靠版本约定），
		// 按 7.x 硬猜只会得到"看着像图、其实是错位解出来的图"，所以明确拒绝。
		var major = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
		var minor = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
		if (major != SupportedMajorVersion)
			throw new InvalidDataException(
				$"不支持的 VTF 版本 {major}.{minor}：本工具只认 VTF {SupportedMajorVersion}.x"
				+ $"（7.0–7.4 的头布局，且 depth 只有 7.2+ 才有）。");

		var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
		var width = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
		var height = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
		var frames = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
		var highResFormat = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
		var lowResFormat = data[57];
		var lowResWidth = data[61];
		var lowResHeight = data[62];
		// depth 是 v7.2 才加的字段：v7.0 / v7.1 的 +63 是缩略图首字节（见类型注释）。
		var depth = HasDepthField(minor) ? BinaryPrimitives.ReadUInt16LittleEndian(data[63..]) : 1;
		var mipCount = data[56];

		var vtf = new VtfFile
		{
			Major = major,
			Minor = minor,
			HeaderSize = headerSize,
			Width = width,
			Height = height,
			Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
			Frames = frames == 0 ? 1 : frames,
			FirstFrame = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]),
			BumpScale = BinaryPrimitives.ReadSingleLittleEndian(data[48..]),
			HighResFormat = highResFormat,
			LowResFormat = lowResFormat,
			MipCount = mipCount == 0 ? 1 : mipCount,
			LowResWidth = lowResWidth,
			LowResHeight = lowResHeight,
			Depth = depth == 0 ? 1 : depth,
		};

		// depth 再离谱也超不过 2048 层（真实素材是 1，最多到几百的纹理数组）。
		// 挡住它是为了让"头部损坏"以一句能读的话失败，而不是在下面的乘法里溢出成
		// `OverflowException：Arithmetic operation resulted in an overflow.`——那种报错
		// 既说不出是哪张图、也说不出是什么问题。
		if (vtf.Depth is < 1 or > MaxDepth)
			throw new InvalidDataException(
				$"VTF depth={vtf.Depth} 不可信（v{major}.{minor}，正常是 1；超过 {MaxDepth} 一律当成头部损坏）。");

		if (!FormatInfo.ContainsKey(highResFormat))
			throw new InvalidDataException($"未知的 VTF 高分辨率格式 {highResFormat}（v{major}.{minor}）。");

		// 每层的字节数：尺寸每层减半（下限 1）；整块 = 单帧单面 × frames × depth。
		//
		// ⚠️ 两个尺寸必须分开记：**块内偏移/块尾定位用整块**（帧在同一 mip 块内依次排列），
		// **对外暴露的 Mips 长度用单帧**。混成一个值的后果是多帧贴图每层多出 frames 倍数据，
		// 下游按单帧算的长度校验（Hd2TextureAsset.BuildDds）必然失败，整张贴图被退回兜底。
		//
		// 尺寸字段来自文件，可能是任意垃圾值（65535×65535 乘出来就超过 int）。
		// 这里的 `checked` 只用来兜"头部损坏"，所以把 `OverflowException` 翻成
		// 一句能定位的 InvalidDataException —— 让**任何调用方**都不必再认识溢出这两字。
		int[] sizes;
		int[] imageSizes;
		int lowSize;
		try
		{
			sizes = new int[vtf.MipCount];
			imageSizes = new int[vtf.MipCount];
			for (var mip = 0; mip < vtf.MipCount; mip++)
			{
				var w = Math.Max(1, vtf.Width >> mip);
				var h = Math.Max(1, vtf.Height >> mip);
				imageSizes[mip] = checked(MipSize(w, h, highResFormat) * vtf.Depth);
				sizes[mip] = checked(imageSizes[mip] * vtf.Frames);
			}

			lowSize = (vtf.LowResWidth != 0 && vtf.LowResHeight != 0)
				? MipSize(vtf.LowResWidth, vtf.LowResHeight, lowResFormat)
				: 0;
		}
		catch (OverflowException)
		{
			throw new InvalidDataException(
				$"VTF 尺寸不可信（v{major}.{minor}）：{vtf.Width}×{vtf.Height} {vtf.FormatName} "
				+ $"mips={vtf.MipCount} frames={vtf.Frames} depth={vtf.Depth} —— 头部字段超出可表示范围。");
		}

		var chainStart = checked(headerSize + lowSize);
		var chainEnd = checked(chainStart + sizes.Sum());

		// 越界必须报错。静默截断会产出一张"看起来还行"的错图，比崩掉难查得多。
		// 报错里带上版本与各段尺寸：这类问题下一次要靠这几行定位，而不是再猜一轮。
		if (chainEnd > data.Length)
			throw new InvalidDataException(
				$"VTF 图像块越过文件末尾：v{major}.{minor} header={headerSize} {vtf.Width}×{vtf.Height} "
				+ $"{vtf.FormatName} mips={vtf.MipCount} frames={vtf.Frames} depth={vtf.Depth} "
				+ $"低分辨率={vtf.LowResWidth}×{vtf.LowResHeight} 需要 {chainEnd} 字节，实际只有 {data.Length}。");

		var lowRes = data.Slice(headerSize, lowSize).ToArray();

		// ⚠️ 从**尾部**往前剥：mip0 是块尾，最后剥出来的才是全分辨率图。
		// 每块里第 0 帧在最前面，所以只截块首的 imageSizes[mip] 个字节（多帧取第 0 帧）。
		var mips = new byte[vtf.MipCount][];
		var offset = chainEnd;
		for (var mip = 0; mip < vtf.MipCount; mip++)
		{
			offset -= sizes[mip];
			mips[mip] = data.Slice(offset, imageSizes[mip]).ToArray();
		}

		return vtf with
		{
			Mips = mips,
			LowRes = lowRes,
			Trailing = data.Length - chainEnd,
		};
	}

	public int Mip0Size => MipSize(Width, Height, HighResFormat);

	public string Summary() =>
		$"{Width}x{Height} {FormatName} mips={MipCount} frames={Frames} " +
		$"lo={LowResWidth}x{LowResHeight}/{(Formats.TryGetValue(LowResFormat, out var lo) ? lo : LowResFormat.ToString())} " +
		$"trailing={Trailing}";
}
