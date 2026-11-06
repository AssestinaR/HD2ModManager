using System.Buffers.Binary;

namespace HD2GameItemAssets;

/// <summary>
/// Reads the verified path-hash-to-Archive mapping stored in the game's
/// <c>hash_lookup</c> Asset. This is deliberately a narrow reader: it exposes
/// only the final map whose values are installed Archive identifiers.
/// <para>
/// <b>解析过程中一个异常都不抛。</b>载荷前面可能带一段未公开的头部，所以要逐个偏移试着解析；
/// 一次尝试里任何一个字段对不上都只意味着"这个起点不对"。以前用 <c>throw</c> 表示这件事，
/// 于是找一次真正的载荷起点就要抛几千个异常——在调试器下每一个都会被打印出来，
/// 界面直接卡死（实测就是这样）。现在游标记住失败原因并停下，调用方只看到 <c>false</c>。
/// </para>
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
	/// 读 path-hash → Archive 映射。读不出来时返回 <c>false</c>，并把**第一个**起点的
	/// 失败原因带出来（那是最有信息量的一个：它说明这份数据到底哪里不像 hash_lookup）。
	/// </summary>
	public static bool TryReadArchiveByPath(
		ReadOnlySpan<byte> data,
		out IReadOnlyDictionary<ulong, ulong> archiveByPath,
		out string? error)
	{
		var direct = ParseArchiveByPath(data, out error);
		if (direct is not null)
		{
			archiveByPath = direct;
			error = null;
			return true;
		}

		var limit = Math.Min(data.Length, MaximumPayloadHeaderScan);
		for (var start = 1; start < limit; start++)
		{
			var shifted = ParseArchiveByPath(data[start..], out _);
			if (shifted is null) continue;
			archiveByPath = shifted;
			error = null;
			return true;
		}

		archiveByPath = EmptyMap;
		return false;
	}

	/// <summary>
	/// 严格版本：读不出来就抛一次异常。给"必须拿到映射"的调用方和测试用；
	/// 只是想尽力试一下的调用方请用 <see cref="TryReadArchiveByPath"/>。
	/// </summary>
	public static IReadOnlyDictionary<ulong, ulong> ReadArchiveByPath(ReadOnlySpan<byte> data)
		=> TryReadArchiveByPath(data, out var archiveByPath, out var error)
			? archiveByPath
			: throw new InvalidDataException(error ?? "hash_lookup could not be parsed.");

	private static readonly Dictionary<ulong, ulong> EmptyMap = new();

	/// <returns>解析成功返回映射；失败返回 <c>null</c> 并在 <paramref name="error"/> 里说明原因。</returns>
	private static IReadOnlyDictionary<ulong, ulong>? ParseArchiveByPath(ReadOnlySpan<byte> data, out string? error)
	{
		var reader = new BinaryReaderCursor(data);

		var parentCount = reader.ReadCount("parent collection count");
		for (var index = 0u; index < parentCount && !reader.Failed; index++)
		{
			var count = reader.ReadCount("parent hash count");
			var emptyMarkers = 0;
			while (count == 0 && !reader.Failed)
			{
				if (++emptyMarkers > MaximumEmptyParentCountMarkers)
				{
					reader.Fail("hash_lookup has too many empty parent-count markers.");
					break;
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
		var archiveByPath = new Dictionary<ulong, ulong>((int)Math.Min(archiveMapCount, 1_000_000));
		for (var index = 0u; index < archiveMapCount && !reader.Failed; index++)
		{
			var path = reader.ReadUInt64("Archive path mapping key");
			var archive = reader.ReadUInt64("Archive path mapping value");
			if (reader.Failed) break;
			if (path == 0 || archive == 0)
			{
				continue;
			}

			if (archiveByPath.TryGetValue(path, out var existing) && existing != archive)
			{
				reader.Fail($"hash_lookup maps path 0x{path:x16} to multiple Archive IDs.");
				break;
			}
			archiveByPath[path] = archive;
		}

		reader.SkipTrees(reader.ReadCount("lookup tree collection 4"), "lookup tree collection 4");
		var endMarker = reader.ReadUInt32("end marker");
		if (reader.Failed)
		{
			error = reader.Failure;
			return null;
		}
		if (endMarker != EndMarker)
		{
			error = $"hash_lookup has invalid end marker 0x{endMarker:x8}.";
			return null;
		}
		if (reader.Remaining != 0)
		{
			error = $"hash_lookup has {reader.Remaining} unexpected bytes after the Archive mapping.";
			return null;
		}

		error = null;
		return archiveByPath;
	}

	/// <summary>
	/// 只读游标。**越界、超限、格式不符都只置失败标志**，不抛异常——这个解析器是用来
	/// "试探多个起点"的，抛异常会让几千次尝试变成几千次异常。
	/// </summary>
	private ref struct BinaryReaderCursor(ReadOnlySpan<byte> data)
	{
		private readonly ReadOnlySpan<byte> _data = data;
		private int _position;
		private bool _failed;
		private string? _failure;

		public readonly bool Failed => _failed;
		public readonly string? Failure => _failure;
		public readonly int Remaining => _data.Length - _position;

		public void Fail(string message)
		{
			if (_failed) return;
			_failed = true;
			_failure = message;
		}

		public uint ReadCount(string field)
		{
			var count = ReadUInt32(field);
			if (_failed) return 0;
			if (count > MaximumCollectionCount)
			{
				Fail($"hash_lookup {field} {count} exceeds the safety limit.");
				return 0;
			}
			return count;
		}

		public uint ReadUInt32(string field)
		{
			if (_failed) return 0;
			if (!TryEnsure(sizeof(uint), field)) return 0;
			var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, sizeof(uint)));
			_position += sizeof(uint);
			return value;
		}

		public ulong ReadUInt64(string field)
		{
			if (_failed) return 0;
			if (!TryEnsure(sizeof(ulong), field)) return 0;
			var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, sizeof(ulong)));
			_position += sizeof(ulong);
			return value;
		}

		public void SkipElements(uint count, int elementSize, string field)
		{
			if (_failed) return;
			var byteCount = (long)count * elementSize;
			if (byteCount > int.MaxValue)
			{
				Fail($"hash_lookup {field} is too large.");
				return;
			}
			if (!TryEnsure((int)byteCount, field)) return;
			_position += (int)byteCount;
		}

		public void SkipTrees(uint count, string field)
		{
			for (var index = 0u; index < count && !_failed; index++)
			{
				SkipElements(1, sizeof(uint) * 2, $"{field} header");
				SkipElements(ReadCount($"{field} entry count"), sizeof(ulong) * 2, $"{field} entries");
			}
		}

		private bool TryEnsure(int count, string field)
		{
			if (count >= 0 && count <= Remaining) return true;
			Fail($"hash_lookup is truncated while reading {field} at 0x{_position:x}.");
			return false;
		}
	}
}
