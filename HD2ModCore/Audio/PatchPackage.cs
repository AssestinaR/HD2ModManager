using System.Buffers.Binary;
using System.Text;

namespace HD2ModCore.Audio;

// Purpose: Lossless parse/rebuild of an HD2 .patch_N TOC for audio-only patches.
// Type header A/B fields, entry order and entry indices are preserved exactly (rewriting them corrupts non-audio resources).
public sealed class PatchPackageTypeHeader
{
	public ulong TypeId { get; init; }
	public uint A { get; init; } = 16;
	public uint B { get; init; } = 64;
}

public sealed class PatchPackageEntry
{
	public ulong FileId { get; init; }
	public ulong TypeId { get; init; }
	public ulong TocDataOffset { get; init; }
	public ulong StreamFileOffset { get; init; }
	public ulong GpuOffset { get; init; }
	public ulong Unk1 { get; init; }
	public ulong Unk2 { get; init; }
	public uint TocDataSize { get; init; }
	public uint StreamSize { get; init; }
	public uint GpuSize { get; init; }
	public uint Unk3 { get; init; }
	public uint Unk4 { get; init; }
	public uint Index { get; init; }
}

public sealed class PatchPackage
{
	public const uint PatchMagic = 0xF0000011u;
	public const ulong BankTypeId = 6006249203084351385UL;
	public const ulong DepTypeId = 12624162998411505776UL;
	public const ulong StreamTypeId = 5785811756662211598UL;
	public const ulong TextTypeId = 979299457696010195UL;
	private const int HeaderSize = 72;
	private const int TypeRecordSize = 32;
	private const int EntryRecordSize = 80;

	private PatchPackage(byte[] raw)
	{
		Raw = raw;
	}

	public byte[] Raw { get; }
	public uint Magic { get; private init; }
	public uint Unknown { get; private init; }
	public byte[] Unk4 { get; private init; } = [];
	public IReadOnlyList<PatchPackageTypeHeader> Types { get; private init; } = [];
	public IReadOnlyList<PatchPackageEntry> Entries { get; private init; } = [];

	/// <summary>True when only bank/dep/stream/text resources are present, i.e. rewriting cannot touch textures or units.</summary>
	public bool IsAudioOnly => Entries.All(entry =>
		entry.TypeId is BankTypeId or DepTypeId or StreamTypeId or TextTypeId);

	public static PatchPackage Parse(byte[] raw)
	{
		ArgumentNullException.ThrowIfNull(raw);
		if (raw.Length < HeaderSize) throw new InvalidDataException("不是 HD2 补丁包：文件过短。");
		var magic = BinaryPrimitives.ReadUInt32LittleEndian(raw);
		if (magic != PatchMagic) throw new InvalidDataException("不是 HD2 补丁包。");
		var numTypes = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
		var numFiles = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
		var tocEnd = HeaderSize + (long)numTypes * TypeRecordSize + (long)numFiles * EntryRecordSize;
		if (tocEnd > raw.Length) throw new InvalidDataException("补丁 TOC 超出文件长度。");

		var types = new List<PatchPackageTypeHeader>((int)numTypes);
		var offset = HeaderSize;
		for (var i = 0; i < numTypes; i++)
		{
			types.Add(new PatchPackageTypeHeader
			{
				TypeId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset + 8)),
				A = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 24)),
				B = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 28)),
			});
			offset += TypeRecordSize;
		}

		var entries = new List<PatchPackageEntry>((int)numFiles);
		for (var i = 0; i < numFiles; i++)
		{
			var o = offset + i * EntryRecordSize;
			entries.Add(new PatchPackageEntry
			{
				FileId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o)),
				TypeId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 8)),
				TocDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 16)),
				StreamFileOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 24)),
				GpuOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 32)),
				Unk1 = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 40)),
				Unk2 = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(o + 48)),
				TocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 56)),
				StreamSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 60)),
				GpuSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 64)),
				Unk3 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 68)),
				Unk4 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 72)),
				Index = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o + 76)),
			});
		}

		return new PatchPackage(raw)
		{
			Magic = magic,
			Unknown = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12)),
			Unk4 = raw.AsSpan(16, 56).ToArray(),
			Types = types,
			Entries = entries,
		};
	}

	public byte[] TocData(PatchPackageEntry entry)
	{
		var offset = (long)entry.TocDataOffset;
		var length = (long)entry.TocDataSize;
		if (offset < 0 || offset + length > Raw.Length) return [];
		return Raw.AsSpan((int)offset, (int)length).ToArray();
	}

	/// <summary>The friendly bank name stored in the matching dependency resource (e.g. "content/audio/wep_railgun").</summary>
	public string? DepName(ulong fileId)
	{
		foreach (var entry in Entries)
		{
			if (entry.TypeId != DepTypeId || entry.FileId != fileId) continue;
			var offset = (long)entry.TocDataOffset;
			if (offset + 8 > Raw.Length) return null;
			var size = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan((int)offset + 4));
			if (offset + 8 + size > Raw.Length) return null;
			return Encoding.UTF8.GetString(Raw, (int)offset + 8, (int)size).TrimEnd('\0');
		}
		return null;
	}

	/// <summary>
	/// Writes a new package where the given banks are replaced. Stream data of untouched entries is carried over from
	/// <paramref name="originalStream"/>. Every other TOC field is preserved.
	/// </summary>
	public byte[] Rebuild(IReadOnlyDictionary<ulong, byte[]> newBanks, byte[] originalStream, out byte[] newStream)
	{
		var entries = Entries;
		var count = entries.Count;
		var tocData = new byte[count][];
		var streamData = new byte[]?[count];
		var tocSizes = new uint[count];

		for (var i = 0; i < count; i++)
		{
			var entry = entries[i];
			if (entry.TypeId == BankTypeId)
			{
				if (newBanks.TryGetValue(entry.FileId, out var bank))
				{
					var block = new byte[16 + bank.Length];
					block[0] = 0xD8; block[1] = 0x2F; block[2] = 0x76; block[3] = 0x78;
					BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)bank.Length);
					BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), entry.FileId);
					Buffer.BlockCopy(bank, 0, block, 16, bank.Length);
					tocData[i] = block;
					tocSizes[i] = (uint)block.Length;
				}
				else
				{
					tocData[i] = TocData(entry);
					tocSizes[i] = entry.TocDataSize;
				}
			}
			else
			{
				tocData[i] = TocData(entry);
				tocSizes[i] = entry.TocDataSize;
				if (entry.StreamSize > 0)
				{
					if (originalStream.LongLength < (long)entry.StreamFileOffset + entry.StreamSize)
						throw new InvalidDataException("补丁声明了流数据但缺少 .stream 文件");
					streamData[i] = originalStream.AsSpan((int)entry.StreamFileOffset, (int)entry.StreamSize).ToArray();
				}
			}
		}

		var counts = entries.GroupBy(entry => entry.TypeId).ToDictionary(group => group.Key, group => group.Count());
		var headers = new List<PatchPackageTypeHeader>();
		var seen = new HashSet<ulong>();
		foreach (var type in Types)
		{
			if (counts.GetValueOrDefault(type.TypeId) > 0 && seen.Add(type.TypeId)) headers.Add(type);
		}
		foreach (var entry in entries)
		{
			if (seen.Add(entry.TypeId)) headers.Add(new PatchPackageTypeHeader { TypeId = entry.TypeId });
		}

		using var output = new MemoryStream();
		WriteUInt32(output, Magic);
		WriteUInt32(output, (uint)headers.Count);
		WriteUInt32(output, (uint)count);
		WriteUInt32(output, Unknown);
		output.Write(Unk4, 0, Unk4.Length);
		foreach (var header in headers)
		{
			WriteUInt64(output, 0);
			WriteUInt64(output, header.TypeId);
			WriteUInt64(output, (ulong)counts.GetValueOrDefault(header.TypeId));
			WriteUInt32(output, header.A);
			WriteUInt32(output, header.B);
		}

		var tocBase = (long)HeaderSize + TypeRecordSize * (long)headers.Count;
		var currentToc = tocBase + EntryRecordSize * (long)count + 8;
		var currentStream = 0L;
		var tocOffsets = new long[count];
		var streamOffsets = new long[count];
		for (var i = 0; i < count; i++)
		{
			tocOffsets[i] = currentToc;
			currentToc += Align16(tocData[i].Length);
			streamOffsets[i] = currentStream;
			if (streamData[i] is not null) currentStream += Align16(streamData[i]!.Length);
		}

		for (var i = 0; i < count; i++)
		{
			var entry = entries[i];
			WriteUInt64(output, entry.FileId);
			WriteUInt64(output, entry.TypeId);
			WriteUInt64(output, (ulong)tocOffsets[i]);
			WriteUInt64(output, (ulong)streamOffsets[i]);
			WriteUInt64(output, entry.GpuOffset);
			WriteUInt64(output, entry.Unk1);
			WriteUInt64(output, entry.Unk2);
			WriteUInt32(output, tocSizes[i]);
			WriteUInt32(output, streamData[i] is not null ? (uint)streamData[i]!.Length : entry.StreamSize);
			WriteUInt32(output, entry.GpuSize);
			WriteUInt32(output, entry.Unk3);
			WriteUInt32(output, entry.Unk4);
			WriteUInt32(output, entry.Index);
		}
		for (var i = 0; i < 8; i++) output.WriteByte(0);
		for (var i = 0; i < count; i++)
		{
			output.Write(tocData[i], 0, tocData[i].Length);
			var pad = Align16(tocData[i].Length) - tocData[i].Length;
			for (var k = 0; k < pad; k++) output.WriteByte(0);
		}
		var minimumSize = 256L * count;
		while (output.Length < minimumSize) output.WriteByte(0);

		using var stream = new MemoryStream();
		for (var i = 0; i < count; i++)
		{
			if (streamData[i] is null) continue;
			stream.Write(streamData[i]!, 0, streamData[i]!.Length);
			var pad = Align16(streamData[i]!.Length) - streamData[i]!.Length;
			for (var k = 0; k < pad; k++) stream.WriteByte(0);
		}
		newStream = stream.ToArray();
		return output.ToArray();
	}

	private static long Align16(long value) => (value + 15) & ~15L;

	private static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteUInt64(Stream stream, ulong value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
		stream.Write(buffer);
	}
}
