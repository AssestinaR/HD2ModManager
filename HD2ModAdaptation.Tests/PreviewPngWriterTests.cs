using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HD2ModAdaptation.Preview;
using Xunit;

namespace HD2ModAdaptation.Tests;

// 作用：PNG 编码器的结构自检。没有第三方解码器，所以这里自己做"读回来自验"：
// 走完签名 / IHDR 字段 / 每块 CRC / 解压 IDAT / 逐像素比对，等价于一个最小解码器。
public sealed class PreviewPngWriterTests
{
	[Fact]
	public void Encode_WritesSignatureAndHeaderForRgba()
	{
		var image = SolidImage(3, 2, 10, 20, 30, 255);
		var png = PreviewPngWriter.Encode(image);

		Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
		var chunks = ReadChunks(png);
		Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(chunk => chunk.Type).ToArray());
		Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data.AsSpan(0, 4)));
		Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data.AsSpan(4, 4)));
		Assert.Equal(8, chunks[0].Data[8]);      // bit depth
		Assert.Equal(6, chunks[0].Data[9]);      // color type: RGBA
		Assert.Equal(0, chunks[0].Data[12]);     // interlace: none
	}

	[Fact]
	public void Encode_EveryChunkHasAValidCrc()
	{
		var png = PreviewPngWriter.Encode(SolidImage(5, 5, 1, 2, 3, 255));
		foreach (var chunk in ReadChunks(png))
		{
			Assert.Equal(chunk.StoredCrc, Crc32(Encoding.ASCII.GetBytes(chunk.Type), chunk.Data));
		}
	}

	[Fact]
	public void Encode_RoundTripsPixelsThroughZlib()
	{
		// 一张 2×2 的图，四个角颜色不同：能抓出通道错位与行序颠倒。
		var image = new PreviewImage(2, 2,
		[
			255, 0, 0, 255,
			0, 255, 0, 255,
			0, 0, 255, 255,
			17, 34, 51, 255,
		]);

		var decoded = DecodeRgba(PreviewPngWriter.Encode(image));
		Assert.Equal(image.ToStraightAlpha(), decoded);
	}

	[Fact]
	public void Encode_WithoutAlphaWritesRgbColorType()
	{
		var png = PreviewPngWriter.Encode(SolidImage(2, 2, 5, 6, 7, 255), includeAlpha: false);
		var header = ReadChunks(png)[0].Data;
		Assert.Equal(2, header[9]);   // color type: RGB
		var decoded = DecodeRgba(png);
		Assert.Equal(new byte[] { 5, 6, 7, 255, 5, 6, 7, 255, 5, 6, 7, 255, 5, 6, 7, 255 }, decoded);
	}

	[Fact]
	public void Encode_IsDeterministic()
	{
		var image = SolidImage(7, 3, 9, 8, 7, 255);
		Assert.Equal(PreviewPngWriter.Encode(image), PreviewPngWriter.Encode(image));
	}

	[Fact]
	public void Encode_KeepsAlphaInStraightSpace()
	{
		// 预乘缓冲里 (128,0,0,128) 代表"半透明纯红"，PNG 里应当写 (255,0,0,128)。
		var image = new PreviewImage(1, 1, [128, 0, 0, 128]);
		var decoded = DecodeRgba(PreviewPngWriter.Encode(image));
		Assert.Equal(new byte[] { 255, 0, 0, 128 }, decoded);
	}

	// ---- 最小 PNG 读取器 ---------------------------------------------------------

	private sealed record Chunk(string Type, byte[] Data, uint StoredCrc);

	private static List<Chunk> ReadChunks(byte[] png)
	{
		var chunks = new List<Chunk>();
		var offset = 8;
		while (offset + 8 <= png.Length)
		{
			var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
			var type = Encoding.ASCII.GetString(png, offset + 4, 4);
			var data = png.AsSpan(offset + 8, length).ToArray();
			var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
			chunks.Add(new Chunk(type, data, crc));
			offset += 12 + length;
			if (type == "IEND") break;
		}
		return chunks;
	}

	/// <summary>只支持本编码器写出的形态：8 位、无交错、filter 0。</summary>
	private static byte[] DecodeRgba(byte[] png)
	{
		var chunks = ReadChunks(png);
		var header = chunks[0].Data;
		var width = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
		var height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
		var channels = header[9] switch { 6 => 4, 2 => 3, var other => throw new InvalidDataException($"不支持的颜色类型 {other}") };

		var compressed = chunks.Where(chunk => chunk.Type == "IDAT").SelectMany(chunk => chunk.Data).ToArray();
		using var input = new MemoryStream(compressed);
		using var zlib = new ZLibStream(input, CompressionMode.Decompress);
		using var raw = new MemoryStream();
		zlib.CopyTo(raw);
		var scanlines = raw.ToArray();

		var stride = width * channels;
		Assert.Equal(height * (stride + 1), scanlines.Length);
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			var row = y * (stride + 1);
			Assert.Equal(0, scanlines[row]);   // filter: None
			for (var x = 0; x < width; x++)
			{
				var from = row + 1 + x * channels;
				var to = (y * width + x) * 4;
				pixels[to] = scanlines[from];
				pixels[to + 1] = scanlines[from + 1];
				pixels[to + 2] = scanlines[from + 2];
				pixels[to + 3] = channels == 4 ? scanlines[from + 3] : (byte)255;
			}
		}
		return pixels;
	}

	private static PreviewImage SolidImage(int width, int height, byte r, byte g, byte b, byte a)
	{
		var pixels = new byte[width * height * 4];
		for (var offset = 0; offset < pixels.Length; offset += 4)
		{
			pixels[offset] = r;
			pixels[offset + 1] = g;
			pixels[offset + 2] = b;
			pixels[offset + 3] = a;
		}
		return new PreviewImage(width, height, pixels);
	}

	private static uint Crc32(byte[] type, byte[] data)
	{
		var crc = 0xFFFFFFFFu;
		foreach (var value in type) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
		foreach (var value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
		return ~crc;
	}

	private static readonly uint[] Table = BuildTable();

	private static uint[] BuildTable()
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
}
