using System.Buffers.Binary;

namespace HD2GameItemAssets;

/// <summary>
/// One validated <c>LDLD</c> record from a generated GameData data library.
/// The parser intentionally copies each payload: callers can retain a record
/// after the source buffer has gone out of scope without relying on unsafe
/// slices or mutable backing storage.
/// </summary>
public sealed record GameDataLibraryRecord(
	int Index,
	uint Version,
	uint TypeId,
	ulong PlatformValue,
	byte[] Payload);

/// <summary>Reads the common generated data-library envelope used by GameData.</summary>
public static class GameDataDataLibraryReader
{
	private const int HeaderSize = 24;
	private const uint MaximumRecordCount = 65_536;

	public static IReadOnlyList<GameDataLibraryRecord> Read(ReadOnlySpan<byte> data)
	{
		if (data.Length < sizeof(uint))
		{
			throw new InvalidDataException("Generated data library is missing its record count.");
		}

		var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
		if (recordCount > MaximumRecordCount)
		{
			throw new InvalidDataException($"Generated data library declares {recordCount} records, exceeding the safety limit.");
		}

		var records = new List<GameDataLibraryRecord>(checked((int)recordCount));
		var cursor = sizeof(uint);
		for (var index = 0; index < recordCount; index++)
		{
			if (cursor > data.Length - HeaderSize)
			{
				throw new InvalidDataException($"Generated data library record {index} header is truncated at 0x{cursor:x}.");
			}

			if (!data.Slice(cursor, 4).SequenceEqual("LDLD"u8))
			{
				throw new InvalidDataException($"Generated data library record {index} has invalid magic at 0x{cursor:x}.");
			}

			var version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 4, sizeof(uint)));
			var typeId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 8, sizeof(uint)));
			var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 12, sizeof(uint)));
			var platformValue = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor + 16, sizeof(ulong)));
			cursor += HeaderSize;
			if (payloadSize > data.Length - cursor)
			{
				throw new InvalidDataException($"Generated data library record {index} payload is truncated at 0x{cursor:x}.");
			}

			records.Add(new GameDataLibraryRecord(
				checked((int)index),
				version,
				typeId,
				platformValue,
				data.Slice(cursor, checked((int)payloadSize)).ToArray()));
			cursor += checked((int)payloadSize);
		}

		if (cursor != data.Length)
		{
			throw new InvalidDataException($"Generated data library has {data.Length - cursor} unexpected trailing bytes after record {recordCount}.");
		}

		return records;
	}
}
