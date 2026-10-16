using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HD2SourceMigration.Retarget;

/// <summary>一个 <c>.npy</c> 数组：描述符、形状与展开成一维的数据。</summary>
public sealed record NpyArray(string Name, string Descriptor, int[] Shape, Array Data)
{
	public int Rows => Shape.Length > 0 ? Shape[0] : 0;

	public int Columns => Shape.Length > 1 ? Shape[1] : 1;

	/// <summary>按行优先摊成 <c>double[rows, columns]</c>。任何浮点/整型 dtype 都能读。</summary>
	public double[,] AsDoubleMatrix()
	{
		var result = new double[Rows, Columns];
		for (var i = 0; i < Rows; i++)
		{
			for (var j = 0; j < Columns; j++)
			{
				var index = (i * Columns) + j;
				result[i, j] = Data switch
				{
					float[] f => f[index],
					double[] d => d[index],
					int[] n => n[index],
					long[] l => l[index],
					short[] s => s[index],
					byte[] b => b[index],
					_ => throw new NotSupportedException($"不支持的数组元素类型 {Data.GetType().Name}。"),
				};
			}
		}

		return result;
	}
}

/// <summary>
/// 最小的 <c>.npz</c>（zip + <c>.npy</c>）读写器。
/// <para>
/// 它存在的理由只有一个：<b>与 Python 实验线对拍</b>。<c>tmp/rig-lab/dump_blender.py</c> 把
/// <c>ours_bind</c> / <c>ours_game</c> / <c>src_tpose</c> 等数组写成 <c>model.npz</c>，
/// 而验收闸门是"C# 与 Python 的逐顶点输出差 &lt; 1e-6 m"——所以这一层必须能读 Python 写的文件，
/// 也能写出同构的文件供人工检查。刻意不引入 NumPy 兼容库：只需要 <c>&lt;f4</c>/<c>&lt;f8</c>/<c>&lt;i4</c>
/// 三种 dtype 与 C 序二维数组。
/// </para>
/// </summary>
public static class NpzFile
{
	private static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

	/// <summary>写一个 <c>.npz</c>。<paramref name="arrays"/> 的值须是 <c>double[,]</c> / <c>float[,]</c> / <c>int[,]</c>。</summary>
	public static void Write(string path, IReadOnlyDictionary<string, Array> arrays)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(arrays);

		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
		foreach (var (name, array) in arrays)
		{
			var entry = archive.CreateEntry(name + ".npy", CompressionLevel.NoCompression);
			using var entryStream = entry.Open();
			WriteNpy(entryStream, array);
		}
	}

	/// <summary>
	/// 读一个 <c>.npz</c>，键是去掉 <c>.npy</c> 后缀的条目名。
	/// <para>
	/// dtype 读不出来的条目会被<b>跳过</b>而不是抛异常：这个读取器的用途是"把 Python 侧的数值
	/// 拿出来对拍"，而 Python 的 <c>np.savez</c> 经常顺手把字符串数组（<c>&lt;U25</c> 之类）
	/// 一起写进去。为一个不参与比较的数组让整个对拍失败，是把手段当成了目的。
	/// </para>
	/// </summary>
	public static Dictionary<string, NpyArray> Read(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		var result = new Dictionary<string, NpyArray>(StringComparer.Ordinal);
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		foreach (var entry in archive.Entries)
		{
			if (!entry.Name.EndsWith(".npy", StringComparison.Ordinal))
				continue;
			using var entryStream = entry.Open();
			using var buffer = new MemoryStream();
			entryStream.CopyTo(buffer);
			var name = entry.Name[..^4];
			try
			{
				result[name] = ReadNpy(name, buffer.ToArray());
			}
			catch (NotSupportedException)
			{
				// 非数值 dtype：不参与对拍，跳过。
			}
		}

		return result;
	}

	private static void WriteNpy(Stream stream, Array array)
	{
		var (descriptor, data) = array switch
		{
			double[,] d => ("<f8", (Array)Flatten(d)),
			float[,] f => ("<f4", (Array)Flatten(f)),
			int[,] i => ("<i4", (Array)Flatten(i)),
			double[] d1 => ("<f8", (Array)d1),
			float[] f1 => ("<f4", (Array)f1),
			int[] i1 => ("<i4", (Array)i1),
			_ => throw new NotSupportedException($"npz 写入不支持 {array.GetType().Name}。"),
		};

		var shape = array.Rank == 1
			? $"({array.Length},)"
			: $"({array.GetLength(0)}, {array.GetLength(1)})";
		var header = $"{{'descr': '{descriptor}', 'fortran_order': False, 'shape': {shape}, }}";
		var baseLength = 10 + header.Length + 1;
		var padding = (64 - (baseLength % 64)) % 64;
		header += new string(' ', padding) + "\n";
		var headerBytes = Encoding.ASCII.GetBytes(header);
		if (headerBytes.Length > ushort.MaxValue)
			throw new InvalidOperationException("npy 头部过长。");

		stream.Write(Magic);
		stream.WriteByte(1);
		stream.WriteByte(0);
		Span<byte> length = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)headerBytes.Length);
		stream.Write(length);
		stream.Write(headerBytes);
		// 直接按内存布局写出。x64/ARM64 的 .NET 都是小端，而这里写的 dtype 全带 `<` 前缀，
		// 两者一致；这也是 NumPy 在同样平台上的行为。
		var byteCount = Buffer.ByteLength(data);
		var payload = new byte[byteCount];
		Buffer.BlockCopy(data, 0, payload, 0, byteCount);
		stream.Write(payload);
	}

	private static NpyArray ReadNpy(string name, byte[] content)
	{
		if (content.Length < 10 || !content.AsSpan(0, 6).SequenceEqual(Magic))
			throw new InvalidDataException($"不是 npy 数据：{name}");
		var major = content[6];
		var headerLength = major == 1
			? BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(8, 2))
			: (int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(8, 4));
		var headerOffset = major == 1 ? 10 : 12;
		var header = Encoding.ASCII.GetString(content, headerOffset, headerLength);

		var descriptor = ExtractValue(header, "'descr':") ?? throw new InvalidDataException($"npy 缺少 descr：{name}");
		var shapeText = ExtractValue(header, "'shape':") ?? throw new InvalidDataException($"npy 缺少 shape：{name}");
		var shape = ParseShape(shapeText);
		var count = 1;
		foreach (var dimension in shape)
			count *= dimension;

		var dataOffset = headerOffset + headerLength;
		var elementSize = descriptor switch
		{
			"<f4" or "<i4" => 4,
			"<f8" or "<i8" => 8,
			"<f2" => 2,
			"|u1" or "<u1" => 1,
			_ => throw new NotSupportedException($"不支持的 npy dtype {descriptor}（{name}）。"),
		};
		if (content.Length - dataOffset < count * elementSize)
			throw new InvalidDataException($"npy 数据被截断：{name}");

		var span = content.AsSpan(dataOffset, count * elementSize);
		Array data = descriptor switch
		{
			"<f4" => ReadFloats(span, count),
			"<f8" => ReadDoubles(span, count),
			"<i4" => ReadInts(span, count),
			"<i8" => ReadLongs(span, count),
			"<f2" => ReadHalves(span, count),
			_ => ReadBytes(span, count),
		};
		return new NpyArray(name, descriptor, shape, data);
	}

	private static float[] ReadFloats(ReadOnlySpan<byte> span, int count)
	{
		var result = new float[count];
		for (var i = 0; i < count; i++)
			result[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(i * 4)..]);
		return result;
	}

	private static double[] ReadDoubles(ReadOnlySpan<byte> span, int count)
	{
		var result = new double[count];
		for (var i = 0; i < count; i++)
			result[i] = BinaryPrimitives.ReadDoubleLittleEndian(span[(i * 8)..]);
		return result;
	}

	private static int[] ReadInts(ReadOnlySpan<byte> span, int count)
	{
		var result = new int[count];
		for (var i = 0; i < count; i++)
			result[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4)..]);
		return result;
	}

	private static long[] ReadLongs(ReadOnlySpan<byte> span, int count)
	{
		var result = new long[count];
		for (var i = 0; i < count; i++)
			result[i] = BinaryPrimitives.ReadInt64LittleEndian(span[(i * 8)..]);
		return result;
	}

	private static float[] ReadHalves(ReadOnlySpan<byte> span, int count)
	{
		var result = new float[count];
		for (var i = 0; i < count; i++)
			result[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span[(i * 2)..]));
		return result;
	}

	private static byte[] ReadBytes(ReadOnlySpan<byte> span, int count) => span[..count].ToArray();

	/// <summary>
	/// 从 npy 头部字典里取一个值。⚠️ 不能用"找下一个逗号"来切——<c>shape</c> 的值是
	/// <c>(121704, 3)</c>，它<b>自己就含逗号</b>，按逗号切会得到 <c>(121704</c>，形状被读成
	/// 一维 121704，于是三列里只读到第一列。
	/// </summary>
	private static string? ExtractValue(string header, string key)
	{
		var index = header.IndexOf(key, StringComparison.Ordinal);
		if (index < 0)
			return null;
		var start = index + key.Length;
		while (start < header.Length && header[start] == ' ')
			start++;
		if (start >= header.Length)
			return null;

		if (header[start] == '(')
		{
			var depth = 0;
			for (var i = start; i < header.Length; i++)
			{
				if (header[i] == '(')
					depth++;
				else if (header[i] == ')')
				{
					depth--;
					if (depth == 0)
						return header[start..(i + 1)];
				}
			}

			return null;
		}

		var end = header.IndexOf(',', start);
		if (end < 0)
			end = header.IndexOf('}', start);
		if (end < 0)
			return null;
		return header[start..end].Trim().Trim('\'');
	}

	private static int[] ParseShape(string text)
	{
		var trimmed = text.Trim().Trim('(', ')');
		if (trimmed.Length == 0)
			return [];
		var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Select(int.Parse).ToArray();
	}

	private static double[] Flatten(double[,] source)
	{
		var result = new double[source.Length];
		var index = 0;
		foreach (var value in source)
			result[index++] = value;
		return result;
	}

	private static float[] Flatten(float[,] source)
	{
		var result = new float[source.Length];
		var index = 0;
		foreach (var value in source)
			result[index++] = value;
		return result;
	}

	private static int[] Flatten(int[,] source)
	{
		var result = new int[source.Length];
		var index = 0;
		foreach (var value in source)
			result[index++] = value;
		return result;
	}
}
