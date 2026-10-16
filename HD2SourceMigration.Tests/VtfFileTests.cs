using System.Buffers.Binary;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// VTF 7.x 读取。
/// <para>
/// 这里钉的核心是 <b>mip 链顺序</b>：图像块是 <c>[缩略图][mipN-1]…[mip1][mip0]</c>，
/// 缩略图在<b>前</b>、<b>mip0 在块尾</b>。从前往后读（当成从大到小）<b>不会抛异常</b>，
/// 只会产出"mip 金字塔"——画面重复出现、每条带宽是上一条的一半。
/// 这正是第一批贴图被打坏的原因，所以用**负面对照**把它钉死。
/// </para>
/// </summary>
public sealed class VtfFileTests
{
	private const int HeaderSize = 80;   // VTF 7.2

	/// <summary>
	/// 造一个 4×4 / DXT1（每层恰好一个 8 字节块）/ 3 层的 VTF。
	/// 每层填满可区分的字节，这样"取错层"一眼可见。
	/// </summary>
	private static byte[] BuildSyntheticVtf()
	{
		byte[] mip0 = new byte[8];
		byte[] mip1 = new byte[8];
		byte[] mip2 = new byte[8];
		Array.Fill(mip2, (byte)0x22);
		Array.Fill(mip1, (byte)0x11);
		Array.Fill(mip0, (byte)0x00);

		var data = new byte[HeaderSize + mip0.Length + mip1.Length + mip2.Length];
		"VTF\0"u8.CopyTo(data);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), HeaderSize);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 4);    // width
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 4);    // height
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), 1);    // frames
		BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(48), 1f);   // bumpScale
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), 13);   // DXT1
		data[56] = 3;                                                    // mipCount
		data[57] = 0;                                                    // lowResFormat
		data[61] = 0;                                                    // lowResWidth = 0 -> 无缩略图
		data[62] = 0;
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(63), 1);    // depth

		// 块内顺序：mip2、mip1、mip0。
		mip2.CopyTo(data, HeaderSize);
		mip1.CopyTo(data, HeaderSize + 8);
		mip0.CopyTo(data, HeaderSize + 16);
		return data;
	}

	[Fact]
	public void MipChainIsPeeledFromTheTailSoIndexZeroIsTheFullResolutionImage()
	{
		var vtf = VtfFile.Read(BuildSyntheticVtf());

		Assert.Equal(4, vtf.Width);
		Assert.Equal(4, vtf.Height);
		Assert.Equal(3, vtf.MipCount);
		Assert.Equal("DXT1", vtf.FormatName);
		Assert.Equal(0, vtf.Trailing);

		// mip0 = 尾部的 8 个 0x00；mip1 = 0x11；mip2 = 0x22。
		Assert.All(vtf.Mips[0], value => Assert.Equal(0x00, value));
		Assert.All(vtf.Mips[1], value => Assert.Equal(0x11, value));
		Assert.All(vtf.Mips[2], value => Assert.Equal(0x22, value));
	}

	/// <summary>
	/// 负面对照：把图像块<b>从前往后</b>读（错误做法）得到的是 mip2，不是 mip0。
	/// 这条断言的作用是——只要有人把剥离方向改反，它立刻红。
	/// </summary>
	[Fact]
	public void ReadingTheChainFrontToBackWouldYieldTheWrongImage()
	{
		var data = BuildSyntheticVtf();
		var vtf = VtfFile.Read(data);

		var frontToBack = data.AsSpan(HeaderSize, 8).ToArray();
		Assert.All(frontToBack, value => Assert.Equal(0x22, value));       // 最前面那层是 mip2
		Assert.NotEqual(frontToBack, vtf.Mips[0]);                          // 而正确的 mip0 不是它
		Assert.Equal(vtf.Mips[2], frontToBack);
	}

	[Fact]
	public void OneByOneBlockFormatsStillOccupyAFullBlock()
	{
		// BC7(98 不是 VTF 格式号) 用 DXT1/BC5 验同一条规则：块格式按块数算，不是按像素数。
		Assert.Equal(8, VtfFile.MipSize(1, 1, 13));    // DXT1 一个 4×4 块 = 8B
		Assert.Equal(16, VtfFile.MipSize(1, 1, 37));   // BC5/ATI2N 一个块 = 16B
		Assert.Equal(8, VtfFile.MipSize(1024, 1024, 13) / (256 * 256));   // 每块 8B
		// 非压缩按像素算。
		Assert.Equal(12, VtfFile.MipSize(2, 2, 2));    // RGB888 = 3 B/px
	}

	[Fact]
	public void DxgiMappingOnlyCoversBlockCompressedFormats()
	{
		Assert.Equal(72u, VtfFile.ToDxgi(13));            // DXT1 -> BC1_UNORM_SRGB
		Assert.Equal(71u, VtfFile.ToDxgi(13, linear: true));
		Assert.Equal(83u, VtfFile.ToDxgi(37));            // BC5（线性，法线图）
		Assert.Equal(80u, VtfFile.ToDxgi(38));            // BC4（掩码）

		// 非压缩格式必须返回 null —— 调用方要显式处理，绝不能当 BC 直接用（会静默换通道）。
		Assert.Null(VtfFile.ToDxgi(0));    // RGBA8888
		Assert.Null(VtfFile.ToDxgi(2));    // RGB888
		Assert.False(VtfFile.IsBlockCompressed(2));
		Assert.True(VtfFile.IsBlockCompressed(13));
	}

	[Fact]
	public void ReadRejectsNonVtfTruncatedAndUnknownFormats()
	{
		Assert.Throws<InvalidDataException>(() => VtfFile.Read(new byte[200]));
		Assert.Throws<InvalidDataException>(() => VtfFile.Read(new byte[16]));

		var unknownFormat = BuildSyntheticVtf();
		BinaryPrimitives.WriteUInt32LittleEndian(unknownFormat.AsSpan(52), 999);
		Assert.Throws<InvalidDataException>(() => VtfFile.Read(unknownFormat));

		// 图像块越过 EOF 必须报错：静默截断会产出一张"看起来还行"的错图。
		var truncated = BuildSyntheticVtf()[..(HeaderSize + 16)];
		Assert.Throws<InvalidDataException>(() => VtfFile.Read(truncated));
	}

	/// <summary>
	/// 真实数据：从 L4D2 <c>pak01</c> 里读**原版模型贴图**。
	/// <para>
	/// 这条测试的价值全在"非内联"上：原版模型贴图的像素数据在<b>分卷</b>
	/// <c>pak01_000.vpk</c> 里（不在 <c>pak01_dir.vpk</c>）。分卷名必须去掉目录文件的
	/// <c>_dir</c> 后缀才能找到——不去掉时表现为"所有非内联条目都读不出来"，
	/// 而这是"从游戏取贴图"整条路的前提。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_StockModelTexturesReadFromPakVolumes()
	{
		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		var gameVpk = Path.Combine(gameRoot, "pak01_dir.vpk");
		if (!File.Exists(gameVpk) || !File.Exists(Path.Combine(gameRoot, "pak01_000.vpk")))
			return;

		using var archive = HD2SourceModel.VpkArchive.Open(gameVpk);
		var samples = archive.Entries
			.Where(entry => entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
			.Where(entry => entry.Path.StartsWith("materials/models/", StringComparison.OrdinalIgnoreCase))
			.OrderBy(entry => entry.Path, StringComparer.Ordinal)
			.Take(24)
			.ToArray();
		Assert.NotEmpty(samples);
		// 至少要有非内联的样本，否则这条测试没验到分卷那条路。
		Assert.Contains(samples, entry => !entry.IsInline);

		var compressed = 0;
		foreach (var entry in samples)
		{
			var vtf = VtfFile.Read(archive.Read(entry));
			Assert.True(vtf.Width > 0 && vtf.Height > 0, $"{entry.Path} 尺寸为 0");
			Assert.Equal(vtf.Mip0Size, vtf.Mips[0].Length);
			Assert.Equal(0, vtf.Trailing);
			if (VtfFile.ToDxgi(vtf.HighResFormat) is not null)
				compressed++;
		}

		// 原版模型贴图绝大多数是 BC 压缩的。
		Assert.True(compressed * 2 >= samples.Length,
			$"{samples.Length} 份原版模型贴图里只有 {compressed} 份是 BC 压缩格式");
	}
}
