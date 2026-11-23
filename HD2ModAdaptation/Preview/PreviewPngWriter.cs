using System.IO.Compression;
using System.Text;

namespace HD2ModAdaptation.Preview;

/// <summary>
/// 最小 PNG 编码器：8 位 RGB / RGBA、无交错、单 IDAT、filter 恒 0。
/// <para>
/// 为什么自己写而不是引第三方：全仓库只有 LZ4 / Sqlite / Sodium 三个依赖，
/// 为了一张预览图引进 ImageSharp / SkiaSharp 不划算。PNG 的无损编码本身只需要
/// "zlib 流 + 每块 CRC32"，而 zlib 由 BCL 的 <see cref="ZLibStream"/> 提供。
/// </para>
/// <para>
/// <b>确定性</b>：不写 iCCP / gAMA / sRGB / tIME / 任何 text 块，压缩级别固定，
/// 于是同样的像素永远产出同样的字节。
/// </para>
/// </summary>
public static class PreviewPngWriter
{
	private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

	private static readonly uint[] CrcTable = BuildCrcTable();

	/// <summary>编码成 PNG 字节。<paramref name="includeAlpha"/> 为假时丢 alpha、写 RGB（背景不透明时更小）。</summary>
	public static byte[] Encode(PreviewImage image, bool includeAlpha = true)
	{
		ArgumentNullException.ThrowIfNull(image);
		var straight = image.ToStraightAlpha();
		var channels = includeAlpha ? 4 : 3;
		var colorType = includeAlpha ? (byte)6 : (byte)2;

		using var output = new MemoryStream(straight.Length / 4 * channels + 1024);
		output.Write(Signature);

		var header = new byte[13];
		WriteBigEndian(header, 0, (uint)image.Width);
		WriteBigEndian(header, 4, (uint)image.Height);
		header[8] = 8;          // bit depth
		header[9] = colorType;
		header[10] = 0;         // compression: deflate
		header[11] = 0;         // filter: adaptive
		header[12] = 0;         // interlace: none
		WriteChunk(output, "IHDR", header);

		WriteChunk(output, "IDAT", CompressScanlines(straight, image.Width, image.Height, channels));
		WriteChunk(output, "IEND", []);
		return output.ToArray();
	}

	/// <summary>每行前面加一个 filter 字节 0（None），然后整段过 zlib。</summary>
	private static byte[] CompressScanlines(byte[] straightRgba, int width, int height, int channels)
	{
		var stride = width * channels;
		var raw = new byte[checked(height * (stride + 1))];
		for (var y = 0; y < height; y++)
		{
			var destination = (int)((long)y * (stride + 1));
			raw[destination] = 0;
			var source = y * width * 4;
			if (channels == 4)
			{
				Array.Copy(straightRgba, source, raw, destination + 1, stride);
			}
			else
			{
				for (var x = 0; x < width; x++)
				{
					var from = source + x * 4;
					var to = destination + 1 + x * 3;
					raw[to] = straightRgba[from];
					raw[to + 1] = straightRgba[from + 1];
					raw[to + 2] = straightRgba[from + 2];
				}
			}
		}

		using var compressed = new MemoryStream(raw.Length / 2 + 64);
		using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
		{
			zlib.Write(raw);
		}
		return compressed.ToArray();
	}

	private static void WriteChunk(Stream output, string type, byte[] data)
	{
		var typeBytes = Encoding.ASCII.GetBytes(type);
		var length = new byte[4];
		WriteBigEndian(length, 0, (uint)data.Length);
		output.Write(length);
		output.Write(typeBytes);
		output.Write(data);

		var crcBytes = new byte[4];
		WriteBigEndian(crcBytes, 0, Crc32(typeBytes, data));
		output.Write(crcBytes);
	}

	private static void WriteBigEndian(byte[] buffer, int offset, uint value)
	{
		buffer[offset] = (byte)(value >> 24);
		buffer[offset + 1] = (byte)(value >> 16);
		buffer[offset + 2] = (byte)(value >> 8);
		buffer[offset + 3] = (byte)value;
	}

	private static uint[] BuildCrcTable()
	{
		var table = new uint[256];
		for (uint index = 0; index < 256; index++)
		{
			var value = index;
			for (var bit = 0; bit < 8; bit++)
			{
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			}
			table[index] = value;
		}
		return table;
	}

	private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
	{
		var crc = 0xFFFFFFFFu;
		foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		return ~crc;
	}
}
