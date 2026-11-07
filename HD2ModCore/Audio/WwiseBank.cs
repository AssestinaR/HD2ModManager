using System.Buffers.Binary;
using System.Text;

namespace HD2ModCore.Audio;

// Purpose: Minimal Wwise soundbank reader/writer used to rebuild outdated audio Mod banks against the current official bank.
// Ported from the standalone HD2SoundFix tool; behaviour is kept byte-for-byte compatible with its "safe" engine
// (official HIRC is reused untouched, only embedded media is merged).
public sealed record WwiseBankChunk(string Tag, byte[] Data);

public sealed record WwiseHircEntry(int Offset, byte Type, uint Size, uint Id);

public static class WwiseBank
{
	private const uint SoundPluginId = 262145;

	public static IReadOnlyList<WwiseBankChunk> ParseChunks(byte[] bank)
	{
		ArgumentNullException.ThrowIfNull(bank);
		var list = new List<WwiseBankChunk>();
		var offset = 0;
		while (offset + 8 <= bank.Length)
		{
			var printable = true;
			for (var i = 0; i < 4; i++)
			{
				var c = bank[offset + i];
				if (c < 0x20 || c > 0x7E) { printable = false; break; }
			}
			if (!printable) break;
			var tag = Encoding.ASCII.GetString(bank, offset, 4);
			var size = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(offset + 4, 4));
			if ((long)offset + 8 + size > bank.Length) break;
			list.Add(new WwiseBankChunk(tag, bank.AsSpan(offset + 8, (int)size).ToArray()));
			offset += 8 + (int)size;
		}
		return list;
	}

	public static WwiseBankChunk? Find(IReadOnlyList<WwiseBankChunk> chunks, string tag)
		=> chunks.FirstOrDefault(chunk => chunk.Tag == tag);

	public static Dictionary<uint, WwiseHircEntry> ParseHirc(byte[] hirc)
	{
		var map = new Dictionary<uint, WwiseHircEntry>();
		if (hirc.Length < 4) return map;
		var count = BinaryPrimitives.ReadUInt32LittleEndian(hirc);
		var offset = 4;
		for (var i = 0; i < count; i++)
		{
			if (offset + 9 > hirc.Length) break;
			var size = BinaryPrimitives.ReadUInt32LittleEndian(hirc.AsSpan(offset + 1, 4));
			var id = BinaryPrimitives.ReadUInt32LittleEndian(hirc.AsSpan(offset + 5, 4));
			map[id] = new WwiseHircEntry(offset, hirc[offset], size, id);
			offset += 5 + (int)size;
		}
		return map;
	}

	public static Dictionary<uint, byte[]> ExtractMedia(IReadOnlyList<WwiseBankChunk> chunks)
	{
		var map = new Dictionary<uint, byte[]>();
		var didx = Find(chunks, "DIDX");
		var data = Find(chunks, "DATA");
		if (didx is null || data is null) return map;
		var count = didx.Data.Length / 12;
		for (var i = 0; i < count; i++)
		{
			var id = BinaryPrimitives.ReadUInt32LittleEndian(didx.Data.AsSpan(i * 12, 4));
			var off = BinaryPrimitives.ReadUInt32LittleEndian(didx.Data.AsSpan(i * 12 + 4, 4));
			var size = BinaryPrimitives.ReadUInt32LittleEndian(didx.Data.AsSpan(i * 12 + 8, 4));
			if ((long)off + size > data.Data.Length) continue;
			map[id] = data.Data.AsSpan((int)off, (int)size).ToArray();
		}
		return map;
	}

	/// <summary>Rebuilds a bank: official BKHD/HIRC/other chunks, DIDX/DATA regenerated from <paramref name="media"/>.</summary>
	public static byte[] Build(byte[] officialBank, IReadOnlyDictionary<uint, byte[]> media)
	{
		var chunks = ParseChunks(officialBank);
		var bkhd = Find(chunks, "BKHD") ?? throw new InvalidDataException("官方 bank 缺少 BKHD");
		var hirc = Find(chunks, "HIRC") ?? throw new InvalidDataException("官方 bank 缺少 HIRC");
		using var output = new MemoryStream();
		WriteChunk(output, "BKHD", bkhd.Data);

		var order = media.Keys.OrderBy(id => id).ToArray();
		using var didx = new MemoryStream();
		using var data = new MemoryStream();
		for (var i = 0; i < order.Length; i++)
		{
			var payload = media[order[i]];
			WriteUInt32(didx, order[i]);
			WriteUInt32(didx, (uint)data.Length);
			WriteUInt32(didx, (uint)payload.Length);
			data.Write(payload, 0, payload.Length);
			if (i != order.Length - 1)
			{
				var pad = (-payload.Length) & 15;
				for (var k = 0; k < pad; k++) data.WriteByte(0);
			}
		}
		WriteChunk(output, "DIDX", didx.ToArray());
		WriteChunk(output, "DATA", data.ToArray());
		WriteChunk(output, "HIRC", hirc.Data);
		foreach (var chunk in chunks)
		{
			if (chunk.Tag is "BKHD" or "DIDX" or "DATA" or "HIRC") continue;
			WriteChunk(output, chunk.Tag, chunk.Data);
		}
		return output.ToArray();
	}

	/// <summary>
	/// Throws when the rebuilt bank is internally inconsistent: media count drift, or an embedded sound
	/// source whose media is missing although the official bank embeds it.
	/// </summary>
	public static void Verify(byte[] bank, IReadOnlyDictionary<uint, byte[]> expectedMedia, IReadOnlySet<uint> officialMedia)
	{
		var chunks = ParseChunks(bank);
		var hirc = Find(chunks, "HIRC") ?? throw new InvalidDataException("校验失败：缺少 HIRC");
		var entries = ParseHirc(hirc.Data);
		var media = ExtractMedia(chunks);
		if (media.Count != expectedMedia.Count)
			throw new InvalidDataException($"校验失败：媒体数量不一致 {media.Count}/{expectedMedia.Count}");
		foreach (var entry in entries.Values)
		{
			if (entry.Type != 2) continue;
			var body = entry.Offset + 9;
			if (body + 9 > hirc.Data.Length) continue;
			if (BinaryPrimitives.ReadUInt32LittleEndian(hirc.Data.AsSpan(body, 4)) != SoundPluginId) continue;
			if (hirc.Data[body + 4] != 0) continue;
			var sourceId = BinaryPrimitives.ReadUInt32LittleEndian(hirc.Data.AsSpan(body + 5, 4));
			// The official bank itself has no embedded media for streamed/special sources; that is not an error.
			if (!officialMedia.Contains(sourceId)) continue;
			if (!media.ContainsKey(sourceId))
				throw new InvalidDataException($"校验失败：音源无媒体 {sourceId:X8}");
		}
	}

	private static void WriteChunk(Stream stream, string tag, byte[] data)
	{
		stream.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
		WriteUInt32(stream, (uint)data.Length);
		stream.Write(data, 0, data.Length);
	}

	private static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
		stream.Write(buffer);
	}
}
