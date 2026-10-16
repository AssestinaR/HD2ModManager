using System.Text;

namespace HD2SourceModel;

/// <summary>
/// VPK 归档里的一个条目。
/// <para>
/// <see cref="Preload"/> 是内联在目录树里的小段数据，位于条目头之后、紧邻的
/// <c>preloadLength</c> 字节；真实文件内容是 <c>Preload + 归档数据</c>。
/// </para>
/// A single entry in a VPK archive.  <see cref="Preload"/> is the inline payload stored in
/// the directory tree right after the entry header; the real file is preload followed by
/// the archived bytes.
/// </summary>
public sealed record VpkEntry(
	string Path,
	string Extension,
	string Directory,
	string FileName,
	ushort ArchiveIndex,
	uint Offset,
	uint Length,
	byte[] Preload)
{
	/// <summary>数据是否就在本文件内（<c>0x7FFF</c>），而不是分卷的 <c>_000.vpk</c> 里。</summary>
	public bool IsInline => ArchiveIndex == VpkArchive.InlineArchiveIndex;

	/// <summary>文件总长度：内联段 + 归档段。</summary>
	public long TotalLength => Preload.Length + Length;
}

/// <summary>
/// 只读的 VPK 归档。
/// <para>
/// 格式要点（都是踩过的坑）：目录树里<b>没有</b> <c>num_extensions</c> 字段，扩展名 / 目录名 /
/// 文件名三层各自用空字符串终止；条目头之后还要跳过 <c>preloadLength</c> 字节；
/// 文件内数据的偏移是 <c>headerSize + treeSize + entryOffset</c>。
/// </para>
/// <para>
/// <b>条目步长与版本号无关</b>：按规范 v1 条目是 16 字节、v2 是 18 字节（多一个终止符），
/// 但实测本机 97 个求生之路 2 的 VPK <b>全部</b>是"版本号写 1、条目却是 18 字节"。
/// 目录树里没有条目数量字段，所以步长对不对只能靠"解析完必须正好吃掉整棵树"来判定，
/// 于是两种步长都试一遍。
/// </para>
/// <para>
/// A read-only VPK archive.  The format details that bite: the tree has <b>no</b>
/// <c>num_extensions</c> field (extension/directory/filename levels are each terminated by an
/// empty string), the entry header is followed by <c>preloadLength</c> bytes that must be
/// skipped, and inline data sits at <c>headerSize + treeSize + entryOffset</c>.
/// The directory-entry stride does <b>not</b> follow the version number: every one of the 97
/// Left 4 Dead 2 archives on hand declares version 1 yet stores 18-byte entries, so both strides
/// are attempted and the one that consumes the tree exactly wins.
/// </para>
/// </summary>
public sealed class VpkArchive : IDisposable
{
	public const uint Magic = 0x55AA1234;
	public const ushort InlineArchiveIndex = 0x7FFF;

	private const int HeaderSizeV1 = 12;
	private const int HeaderSizeV2 = 28;

	/// <summary>v1 规范里的条目步长（无终止符）；实测文件基本不用。</summary>
	private const int EntrySizeV1 = 16;

	/// <summary>v2 规范的条目步长（含 2 字节终止符）；实测文件实际使用。</summary>
	private const int EntrySizeV2 = 18;

	private readonly string _path;
	private readonly Dictionary<string, VpkEntry> _byPath;
	private FileStream? _stream;
	private bool _disposed;

	private VpkArchive(string path, int version, long dataOffset, Dictionary<string, VpkEntry> byPath)
	{
		_path = path;
		Version = version;
		DataOffset = dataOffset;
		_byPath = byPath;
	}

	public int Version { get; }

	/// <summary>文件内归档数据的起始偏移（<c>headerSize + treeSize</c>）。</summary>
	public long DataOffset { get; }

	public IReadOnlyCollection<VpkEntry> Entries => _byPath.Values;

	public static VpkArchive Open(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var full = Path.GetFullPath(path);
		using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

		var magic = reader.ReadUInt32();
		if (magic != Magic)
			throw new InvalidDataException($"不是 VPK 文件（magic=0x{magic:X8}）：{full}");

		var version = reader.ReadInt32();
		var treeSize = reader.ReadUInt32();
		var headerSize = version == 1 ? HeaderSizeV1 : HeaderSizeV2;
		if (version is not (1 or 2))
			throw new InvalidDataException($"不支持的 VPK 版本 {version}：{full}");

		stream.Position = headerSize;
		var tree = reader.ReadBytes((int)treeSize);
		if (tree.Length != treeSize)
			throw new InvalidDataException($"VPK 目录树被截断：{full}");

		// 先按实测通用的 18 字节试，再退回规范里的 16 字节。
		if (TryParseTree(tree, EntrySizeV2, full, out var byPath)
			|| TryParseTree(tree, EntrySizeV1, full, out byPath))
		{
			return new VpkArchive(full, version, headerSize + treeSize, byPath);
		}

		throw new InvalidDataException($"VPK 目录树无法解析（16 与 18 字节条目都试过）：{full}");
	}

	/// <summary>
	/// 解析目录树；返回 <c>false</c> 表示这个条目步长读不通（越界、重复路径、或没有正好吃掉整棵树），
	/// 调用方应当换另一种步长再试。返回 <c>true</c> 时 <paramref name="byPath"/> 一定已填充完整。
	/// </summary>
	private static bool TryParseTree(
		byte[] tree, int entrySize, string source, out Dictionary<string, VpkEntry> byPath)
	{
		byPath = new Dictionary<string, VpkEntry>(StringComparer.OrdinalIgnoreCase);
		var position = 0;

		// 返回 null 表示"没有终止符"——树被读歪了，不是"这里确实没有名字"。
		string? ReadString()
		{
			var start = position;
			while (position < tree.Length && tree[position] != 0)
				position++;

			if (position >= tree.Length)
				return null;

			var value = Encoding.UTF8.GetString(tree, start, position - start);
			position++;
			return value;
		}

		while (true)
		{
			var extension = ReadString();
			if (extension is null)
				return false;
			if (extension.Length == 0)
				break;

			while (true)
			{
				var directory = ReadString();
				if (directory is null)
					return false;
				if (directory.Length == 0)
					break;

				while (true)
				{
					var fileName = ReadString();
					if (fileName is null)
						return false;
					if (fileName.Length == 0)
						break;

					if (position + entrySize > tree.Length)
						return false;

					// CRC 本工具不校验，读掉即可。
					_ = BitConverter.ToUInt32(tree, position);
					var preloadLength = BitConverter.ToUInt16(tree, position + 4);
					var archiveIndex = BitConverter.ToUInt16(tree, position + 6);
					var offset = BitConverter.ToUInt32(tree, position + 8);
					var length = BitConverter.ToUInt32(tree, position + 12);
					position += entrySize;

					if (position + preloadLength > tree.Length)
						return false;

					var preload = new byte[preloadLength];
					Array.Copy(tree, position, preload, 0, preloadLength);
					position += preloadLength;

					// 扩展名为单个空格表示"没有扩展名"；目录名为空或空格表示"根目录"。
					var name = extension == " " ? fileName : $"{fileName}.{extension}";
					var relative = directory is "" or " " ? name : $"{directory}/{name}";
					var entry = new VpkEntry(
						relative.TrimStart('/'), extension, directory, fileName,
						archiveIndex, offset, length, preload);

					// 同一路径出现两次说明步长选错了。
					if (!byPath.TryAdd(entry.Path, entry))
						return false;
				}
			}
		}

		// 唯一的"自洽"判据：解析完必须正好停在树的末尾。
		return position == tree.Length;
	}

	/// <summary>按路径查找条目；大小写不敏感，反斜杠会被归一成斜杠。</summary>
	public VpkEntry? Find(string path)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return _byPath.GetValueOrDefault(path.Replace('\\', '/').TrimStart('/'));
	}

	/// <summary>枚举指定扩展名的条目（不含点，例如 <c>mdl</c>）。</summary>
	public IEnumerable<VpkEntry> WithExtension(string extension)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(extension);
		var wanted = extension.TrimStart('.');
		return _byPath.Values.Where(entry =>
			string.Equals(entry.Extension, wanted, StringComparison.OrdinalIgnoreCase));
	}

	public bool TryRead(VpkEntry entry, out byte[] data)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ObjectDisposedException.ThrowIf(_disposed, this);

		var payload = new byte[entry.Length];
		if (entry.Length > 0)
		{
			if (entry.IsInline)
			{
				_stream ??= new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
				_stream.Position = DataOffset + entry.Offset;
				if (_stream.Read(payload, 0, payload.Length) != payload.Length)
				{
					data = [];
					return false;
				}
			}
			else
			{
				// 分卷名要去掉目录文件名的 `_dir` 后缀：`pak01_dir.vpk` 的数据在
				// `pak01_000.vpk` 里，不是 `pak01_dir_000.vpk`。不去掉的话分卷永远找不到，
				// 表现为所有非内联条目都读不出来（L4D2 原版模型贴图全是这种情况）。
				var stem = Path.GetFileNameWithoutExtension(_path);
				if (stem.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
					stem = stem[..^4];
				var volume = Path.Combine(
					Path.GetDirectoryName(_path) ?? string.Empty,
					$"{stem}_{entry.ArchiveIndex:D3}.vpk");
				if (!File.Exists(volume))
				{
					data = [];
					return false;
				}

				using var volumeStream = new FileStream(volume, FileMode.Open, FileAccess.Read, FileShare.Read);
				volumeStream.Position = entry.Offset;
				if (volumeStream.Read(payload, 0, payload.Length) != payload.Length)
				{
					data = [];
					return false;
				}
			}
		}

		if (entry.Preload.Length == 0)
		{
			data = payload;
			return true;
		}

		data = [.. entry.Preload, .. payload];
		return true;
	}

	public byte[] Read(VpkEntry entry) =>
		TryRead(entry, out var data)
			? data
			: throw new InvalidDataException($"VPK 条目读不出来（可能是分卷缺失）：{entry.Path}");

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_stream?.Dispose();
		_stream = null;
	}
}
