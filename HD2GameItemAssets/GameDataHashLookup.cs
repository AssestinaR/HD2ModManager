using System.Buffers.Binary;

namespace HD2GameItemAssets;

/// <summary>
/// Reads the verified path-hash-to-Archive mapping stored in the game's
/// <c>hash_lookup</c> Asset. This is deliberately a narrow reader: it exposes
/// only the final map whose values are installed Archive identifiers.
/// </summary>
public static class GameDataHashLookupReader
{
	private const uint MaximumCollectionCount = 4_000_000;
	private const int MaximumEmptyParentCountMarkers = 32;
	private const uint EndMarker = 0xDEADBEE7;

	/// <summary>
	/// 载荷前面可能带一段未公开的头部。实测 <c>hash_lookup 0x7056bc19c69f0f07</c>
	/// 的 TocData 前 3792 字节是头部，真正的载荷是它的后缀——直接解析会读到底部的
	/// 0 而不是结束标记。载荷必须正好在缓冲区末尾结束，所以向后找起点不会误判。
	/// </summary>
	private const int MaximumPayloadHeaderScan = 64 * 1024;

	/// <summary>
	/// 读 path-hash → Archive 映射。先按"载荷就是整个缓冲区"解析；失败时再向后找真正的载荷起点，
	/// 这样游戏换个头部大小也不会让整块映射静默消失。
	/// </summary>
	public static IReadOnlyDictionary<ulong, ulong> ReadArchiveByPath(ReadOnlySpan<byte> data)
	{
		try
		{
			return ParseArchiveByPath(data);
		}
		catch (InvalidDataException) when (data.Length > 1)
		{
			var limit = Math.Min(data.Length, MaximumPayloadHeaderScan);
			for (var start = 1; start < limit; start++)
			{
				try
				{
					return ParseArchiveByPath(data[start..]);
				}
				catch (InvalidDataException)
				{
					// 这个起点不是载荷开头，继续往后找。
				}
			}

			throw;
		}
	}

	private static IReadOnlyDictionary<ulong, ulong> ParseArchiveByPath(ReadOnlySpan<byte> data)
	{
		var reader = new BinaryReaderCursor(data);

		var parentCount = reader.ReadCount("parent collection count");
		for (var index = 0u; index < parentCount; index++)
		{
			var count = reader.ReadCount("parent hash count");
			var emptyMarkers = 0;
			while (count == 0)
			{
				if (++emptyMarkers > MaximumEmptyParentCountMarkers)
				{
					throw new InvalidDataException("hash_lookup has too many empty parent-count markers.");
				}
				count = reader.ReadCount("parent hash count");
			}
			reader.SkipElements(count, sizeof(ulong), "parent hashes");
		}

		reader.SkipElements(reader.ReadCount("hash collection 1"), sizeof(ulong), "hash collection 1");
		reader.SkipElements(reader.ReadCount("hash collection 2"), sizeof(ulong), "hash collection 2");
		reader.SkipElements(reader.ReadCount("mapping collection 1"), sizeof(ulong) * 2, "mapping collection 1");
		reader.SkipElements(reader.ReadCount("hash collection 3"), sizeof(ulong), "hash collection 3");
		_ = reader.ReadUInt32("hash_lookup type marker");
		reader.SkipElements(reader.ReadCount("thin mapping collection"), sizeof(uint) * 3, "thin mapping collection");
		reader.SkipElements(reader.ReadCount("thin hash collection"), sizeof(ulong), "thin hash collection");
		reader.SkipElements(reader.ReadCount("hash collection 4"), sizeof(ulong), "hash collection 4");
		reader.SkipElements(reader.ReadCount("mapping collection 2"), sizeof(ulong) * 2, "mapping collection 2");
		reader.SkipTrees(reader.ReadCount("lookup tree collection 1"), "lookup tree collection 1");
		reader.SkipElements(reader.ReadCount("mapping collection 3"), sizeof(ulong) * 2, "mapping collection 3");
		reader.SkipTrees(reader.ReadCount("lookup tree collection 2"), "lookup tree collection 2");
		reader.SkipElements(reader.ReadCount("mapping collection 4"), sizeof(ulong) * 2, "mapping collection 4");
		reader.SkipTrees(reader.ReadCount("lookup tree collection 3"), "lookup tree collection 3");

		var archiveMapCount = reader.ReadCount("Archive path mapping count");
		var archiveByPath = new Dictionary<ulong, ulong>(checked((int)Math.Min(archiveMapCount, 1_000_000)));
		for (var index = 0u; index < archiveMapCount; index++)
		{
			var path = reader.ReadUInt64("Archive path mapping key");
			var archive = reader.ReadUInt64("Archive path mapping value");
			if (path == 0 || archive == 0)
			{
				continue;
			}

			if (archiveByPath.TryGetValue(path, out var existing) && existing != archive)
			{
				throw new InvalidDataException($"hash_lookup maps path 0x{path:x16} to multiple Archive IDs.");
			}
			archiveByPath[path] = archive;
		}

		reader.SkipTrees(reader.ReadCount("lookup tree collection 4"), "lookup tree collection 4");
		var endMarker = reader.ReadUInt32("end marker");
		if (endMarker != EndMarker)
		{
			throw new InvalidDataException($"hash_lookup has invalid end marker 0x{endMarker:x8}.");
		}
		if (reader.Remaining != 0)
		{
			throw new InvalidDataException($"hash_lookup has {reader.Remaining} unexpected bytes after the Archive mapping.");
		}

		return archiveByPath;
	}

	private ref struct BinaryReaderCursor(ReadOnlySpan<byte> data)
	{
		private readonly ReadOnlySpan<byte> _data = data;
		private int _position;

		public int Remaining => _data.Length - _position;

		public uint ReadCount(string field)
		{
			var count = ReadUInt32(field);
			if (count > MaximumCollectionCount)
			{
				throw new InvalidDataException($"hash_lookup {field} {count} exceeds the safety limit.");
			}
			return count;
		}

		public uint ReadUInt32(string field)
		{
			Ensure(sizeof(uint), field);
			var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, sizeof(uint)));
			_position += sizeof(uint);
			return value;
		}

		public ulong ReadUInt64(string field)
		{
			Ensure(sizeof(ulong), field);
			var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, sizeof(ulong)));
			_position += sizeof(ulong);
			return value;
		}

		public void SkipElements(uint count, int elementSize, string field)
		{
			var byteCount = checked((long)count * elementSize);
			if (byteCount > int.MaxValue)
			{
				throw new InvalidDataException($"hash_lookup {field} is too large.");
			}
			Ensure(checked((int)byteCount), field);
			_position += checked((int)byteCount);
		}

		public void SkipTrees(uint count, string field)
		{
			for (var index = 0u; index < count; index++)
			{
				SkipElements(1, sizeof(uint) * 2, $"{field} header");
				SkipElements(ReadCount($"{field} entry count"), sizeof(ulong) * 2, $"{field} entries");
			}
		}

		private void Ensure(int count, string field)
		{
			if (count < 0 || count > Remaining)
			{
				throw new InvalidDataException($"hash_lookup is truncated while reading {field} at 0x{_position:x}.");
			}
		}
	}
}
