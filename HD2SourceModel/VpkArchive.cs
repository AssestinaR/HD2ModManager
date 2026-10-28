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

	/// <summary>
	/// 目录树里的名字**不一定是 UTF-8**。同一个包里可以混着写：中文模组常用 GBK（CP936）存
	/// <c>头部</c> / <c>大腿</c> 这类 vgui 图名。按 UTF-8 硬解会把它们变成一串 U+FFFD，
	/// 于是 <c>大腿</c> 与 <c>倒地</c> 解出来一模一样、被当成重复路径，整棵树判失败
	/// （2026-10-28 用户样本 <c>秋桦 仁心仁术 shader 基础组件.vpk</c> 就是 201 个条目全部正确、
	/// 只因这两个名字撞车而整包打不开）。GBK 是兜底解码器，不是首选。
	/// </summary>
	private static readonly Encoding FallbackNameEncoding = CreateFallbackNameEncoding();

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private static Encoding CreateFallbackNameEncoding()
	{
		try
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return Encoding.GetEncoding(936);
		}
		catch (Exception)
		{
			// 拿不到 CP936 时退回单字节直通：路径会变成乱码，但至少不会把整棵树判死。
			return Encoding.Latin1;
		}
	}

	/// <summary>先按严格 UTF-8 解，失败再按 GBK 解。两种都解不了才用替换字符（不会抛）。</summary>
	private static string DecodeName(ReadOnlySpan<byte> raw)
	{
		if (raw.Length == 0)
			return string.Empty;

		if (IsAscii(raw))
			return Encoding.ASCII.GetString(raw);

		try
		{
			return StrictUtf8.GetString(raw);
		}
		catch (DecoderFallbackException)
		{
			return FallbackNameEncoding.GetString(raw);
		}
	}

	private static bool IsAscii(ReadOnlySpan<byte> raw)
	{
		foreach (var value in raw)
		{
			if (value >= 0x80)
				return false;
		}

		return true;
	}

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

	/// <summary>
	/// 目录树里"同一路径出现两次"的条目数。正常打包器不会产生这种条目；
	/// 出现即说明包本身有问题（本类保留先出现的那条，不因此拒绝打开整包）。
	/// </summary>
	public int DuplicateEntryCount { get; private init; }

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

		// 目录树里没有条目数量字段，步长只能靠"正好吃完整棵树"判定，两种步长都试。
		// 重复路径不再直接判死（见 DecodeName 的注释：名字解码差异会制造假重复），
		// 只在两种步长都能吃完树时用来挑更干净的那个。
		var candidates = new List<(int EntrySize, Dictionary<string, VpkEntry> ByPath, int Duplicates)>();
		foreach (var entrySize in new[] { EntrySizeV2, EntrySizeV1 })
		{
			if (TryParseTree(tree, entrySize, full, out var byPath, out var duplicates))
				candidates.Add((entrySize, byPath, duplicates));
		}

		if (candidates.Count > 0)
		{
			var best = candidates
				.OrderBy(candidate => candidate.Duplicates)
				.ThenBy(candidate => candidate.EntrySize == EntrySizeV2 ? 0 : 1)
				.First();
			// 有重复条目时优先用"没有重复"的那种步长；两种都有重复就取重复少的那个，把数量报出去。
			return new VpkArchive(full, version, headerSize + treeSize, best.ByPath)
			{
				DuplicateEntryCount = best.Duplicates,
			};
		}

		throw new InvalidDataException($"VPK 目录树无法解析（16 与 18 字节条目都试过）：{full}");
	}

	/// <summary>
	/// 解析目录树；返回 <c>false</c> 表示这个条目步长读不通（越界、字符串没有终止符、或没有正好吃掉整棵树），
	/// 调用方应当换另一种步长再试。返回 <c>true</c> 时 <paramref name="byPath"/> 一定已填充完整。
	/// <para>
	/// <paramref name="duplicates"/> 是"同一路径出现两次"的次数：<b>不再</b>因为重复而判失败
	/// （保留先出现的那条），但要报给调用方，好让上层在两种步长都通时挑干净的那个。
	/// </para>
	/// </summary>
	private static bool TryParseTree(
		byte[] tree, int entrySize, string source, out Dictionary<string, VpkEntry> byPath, out int duplicates)
	{
		byPath = new Dictionary<string, VpkEntry>(StringComparer.OrdinalIgnoreCase);
		duplicates = 0;
		var position = 0;

		// 返回 false 表示"没有终止符"——树被读歪了，不是"这里确实没有名字"。
		bool TryReadString(out ReadOnlySpan<byte> value)
		{
			var start = position;
			while (position < tree.Length && tree[position] != 0)
				position++;

			if (position >= tree.Length)
			{
				value = default;
				return false;
			}

			value = tree.AsSpan(start, position - start);
			position++;
			return true;
		}

		while (true)
		{
			if (!TryReadString(out var extensionBytes))
				return false;

			var extension = DecodeName(extensionBytes);
			if (extension.Length == 0)
				break;

			while (true)
			{
				if (!TryReadString(out var directoryBytes))
					return false;

				var directory = DecodeName(directoryBytes);
				if (directory.Length == 0)
					break;

				while (true)
				{
					if (!TryReadString(out var fileNameBytes))
						return false;

					var fileName = DecodeName(fileNameBytes);
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

					if (!byPath.TryAdd(entry.Path, entry))
						duplicates++;
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
