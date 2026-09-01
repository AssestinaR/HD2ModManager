using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Binary;
using K4os.Compression.LZ4;

namespace HD2ModCore.Infrastructure;

// 作用：读取 HD2 新版 slim/bundled game data，将虚拟 archive id 解析到 bundles*.nxa 中的 TOC 数据。
// Purpose: Resolves HD2 slim/bundled game data archive ids into TOC bytes stored in bundles*.nxa files.
public sealed class GameDataPackageResolver : IGameDataPackageResolver
{
	private const uint TocMagic = 4026531857;
	private const uint DsarMagic = 0x52415344;
	private const byte ChunkStart = 0x02;
	private const byte CompressionUncompressed = 0x00;
	private const byte CompressionLz4 = 0x03;

	private readonly string _gameDataDirectory;
	private readonly Dictionary<string, PackageInfo> _packages = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Dictionary<long, int>> _bundleChunkOffsets = new(StringComparer.OrdinalIgnoreCase);
	private bool _initialized;

	public GameDataPackageResolver(string gameDataDirectory)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		_gameDataDirectory = Path.GetFullPath(gameDataDirectory);
	}

	public bool IsSlimVersion => !File.Exists(Path.Combine(_gameDataDirectory, "9ba626afa44a3aa3"));

	public async ValueTask<GameDataPackageToc?> GetPackageTocAsync(string packageName, CancellationToken cancellationToken = default)
	{
		var fullPath = Path.Combine(_gameDataDirectory, Path.GetFileName(packageName));
		if (File.Exists(fullPath) && new FileInfo(fullPath).Length < 12)
		{
			return null;
		}
		var type = await GetPackageTypeAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var toc = type switch
		{
			PackageStorageType.Legacy => await ReadLegacyTocAsync(fullPath, cancellationToken).ConfigureAwait(false),
			PackageStorageType.Dsar => await GetResourceFromDsarAsync(fullPath, 0, cancellationToken).ConfigureAwait(false),
			PackageStorageType.Bundled => await GetBundledPackageTocAsync(Path.GetFileName(packageName), cancellationToken).ConfigureAwait(false),
			_ => null,
		};

		// `data` also contains data libraries (for example the explicit armor-set
		// relationship table).  They can be DSAR-backed, but they are not archive
		// TOCs and must not enter the reverse asset index as broken archives.
		return toc is null || toc.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(0, sizeof(uint))) != TocMagic
			? null
			: new GameDataPackageToc(toc, type is PackageStorageType.Dsar or PackageStorageType.Bundled);
	}

	public async ValueTask<byte[]?> GetPackageResourceAsync(string packageName, ulong resourceOffset, uint resourceSize, CancellationToken cancellationToken = default)
	{
		var normalized = Path.GetFileName(packageName);
		var fullPath = Path.Combine(_gameDataDirectory, normalized);
		var type = await GetPackageTypeAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var offset = checked((long)resourceOffset);
		var data = type switch
		{
			PackageStorageType.Legacy => await ReadLegacyResourceAsync(fullPath, offset, checked((int)resourceSize), cancellationToken).ConfigureAwait(false),
			PackageStorageType.Dsar => await GetResourceFromDsarAsync(fullPath, offset, cancellationToken).ConfigureAwait(false),
			PackageStorageType.Bundled => await GetBundledPackageResourceAsync(normalized, offset, resourceSize, cancellationToken).ConfigureAwait(false),
			_ => null,
		};

		if (data is null || data.Length == 0)
		{
			return null;
		}

		if (resourceSize > 0 && data.Length > resourceSize)
		{
			Array.Resize(ref data, checked((int)resourceSize));
		}

		return data;
	}

	public async ValueTask<IReadOnlyList<string>> GetPackageNamesAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		if (!Directory.Exists(_gameDataDirectory))
		{
			return Array.Empty<string>();
		}

		var directPackages = Directory.EnumerateFiles(_gameDataDirectory)
			.Select(Path.GetFileName)
			.Where(name => !string.IsNullOrWhiteSpace(name)
				&& !name.Contains(".patch", StringComparison.OrdinalIgnoreCase)
				&& Path.GetExtension(name) is "")
			.Cast<string>()
			.ToArray();

		// 新版目录可同时存在直接 archive 与 bundles.nxa。两者共同构成当前 Game Data；
		// 只返回 bundle 清单会使 SQLite 索引漏掉直接资源，反之亦然。
		return directPackages
			.Concat(_packages.Keys.Where(IsBasePackageName))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public async ValueTask<string> GetPackageFingerprintAsync(string packageName, CancellationToken cancellationToken = default)
	{
		var normalized = Path.GetFileName(packageName);
		var fullPath = Path.Combine(_gameDataDirectory, normalized);
		var type = await GetPackageTypeAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var builder = new StringBuilder();
		builder.Append(normalized).Append('|').Append(type).Append('|');

		if (type is PackageStorageType.Legacy or PackageStorageType.Dsar)
		{
			AppendFileFingerprint(builder, fullPath);
			return Hash(builder.ToString());
		}

		if (type == PackageStorageType.Bundled)
		{
			await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
			if (!_packages.TryGetValue(normalized, out var package))
			{
				builder.Append("missing");
				return Hash(builder.ToString());
			}

			builder.Append(package.Size).Append('|');
			foreach (var entry in package.Entries.OrderBy(e => e.OriginalArchiveOffset).ThenBy(e => e.BundleIndex).ThenBy(e => e.StartOffset))
			{
				builder.Append(entry.OriginalArchiveOffset).Append('@').Append(entry.StartOffset).Append('@').Append(entry.BundleIndex).Append('|');
				AppendFileFingerprint(builder, Path.Combine(_gameDataDirectory, $"bundles.{entry.BundleIndex:00}.nxa"));
			}
		}

		return Hash(builder.ToString());
	}

	private async ValueTask<byte[]?> GetBundledPackageTocAsync(string packageName, CancellationToken cancellationToken)
	{
		var header = await ReadBundledPackageRangeAsync(packageName, 0, 12, cancellationToken).ConfigureAwait(false);
		if (header is null || header.Length < 12 || BinaryPrimitivesLE.ReadUInt32(header.AsSpan(0, 4)) != TocMagic)
		{
			return null;
		}

		var typeCount = BinaryPrimitivesLE.ReadUInt32(header.AsSpan(4, 4));
		var fileCount = BinaryPrimitivesLE.ReadUInt32(header.AsSpan(8, 4));
		var legacyLength = checked(60 + checked((int)typeCount * 32) + checked((int)fileCount * 80));
		var slimLength = checked(72 + checked((int)typeCount * 32) + checked((int)fileCount * 80));
		return await ReadBundledPackageRangeAsync(packageName, 0, Math.Max(legacyLength, slimLength), cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<byte[]?> ReadBundledPackageRangeAsync(string packageName, long resourceOffset, int resourceSize, CancellationToken cancellationToken)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		if (!_packages.TryGetValue(packageName, out var package) || package.Entries.Count == 0)
		{
			return null;
		}
		if (resourceOffset < 0 || resourceSize <= 0 || resourceOffset >= package.Size)
		{
			return null;
		}

		var requestedEnd = Math.Min(package.Size, checked(resourceOffset + resourceSize));
		var range = new byte[checked((int)(requestedEnd - resourceOffset))];
		var orderedEntries = package.Entries
			.OrderBy(e => e.OriginalArchiveOffset)
			.ToArray();

		for (var i = 0; i < orderedEntries.Length; i++)
		{
			var entry = orderedEntries[i];
			var nextOffset = i + 1 < orderedEntries.Length ? orderedEntries[i + 1].OriginalArchiveOffset : package.Size;
			var copyStart = Math.Max(resourceOffset, entry.OriginalArchiveOffset);
			var copyEnd = Math.Min(requestedEnd, nextOffset);
			if (copyEnd <= copyStart)
			{
				continue;
			}
			var bytesBeforeRange = checked((int)(copyStart - entry.OriginalArchiveOffset));
			var bytesToRead = checked((int)(copyEnd - entry.OriginalArchiveOffset));

			var resources = await GetBundledResourcesAsync(
				Path.Combine(_gameDataDirectory, $"bundles.{entry.BundleIndex:00}.nxa"),
				entry.StartOffset,
				bytesToRead,
				cancellationToken).ConfigureAwait(false);
			var combined = Combine(resources);
			if (combined.Length <= bytesBeforeRange)
			{
				continue;
			}
			var copyLength = Math.Min(combined.Length - bytesBeforeRange, checked((int)(copyEnd - copyStart)));
			Buffer.BlockCopy(combined, bytesBeforeRange, range, checked((int)(copyStart - resourceOffset)), copyLength);
		}

		return range;
	}

	private async ValueTask<byte[]?> GetBundledPackageResourceAsync(string packageName, long resourceOffset, uint resourceSize, CancellationToken cancellationToken)
	{
		return resourceSize == 0
			? Array.Empty<byte>()
			: await ReadBundledPackageRangeAsync(packageName, resourceOffset, checked((int)resourceSize), cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<IReadOnlyList<byte[]>> GetBundledResourcesAsync(string bundlePath, long startOffset, int size, CancellationToken cancellationToken)
	{
		var resources = new List<byte[]>();
		var currentSize = 0;
		while (currentSize < size)
		{
			var resource = await GetResourceFromDsarAsync(bundlePath, startOffset + currentSize, cancellationToken).ConfigureAwait(false);
			if (resource.Length == 0)
			{
				break;
			}

			currentSize += resource.Length;
			resources.Add(resource);
		}

		return resources;
	}

	private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		if (_initialized)
		{
			return;
		}

		_initialized = true;
		var bundleDatabase = Path.Combine(_gameDataDirectory, "bundles.nxa");
		if (!File.Exists(bundleDatabase))
		{
			return;
		}

		await BuildBundleChunkOffsetMapsAsync(cancellationToken).ConfigureAwait(false);
		var bundleContents = await DecompressDsarAsync(bundleDatabase, cancellationToken).ConfigureAwait(false);
		if (bundleContents.Length < 0x18)
		{
			return;
		}

		var packageCount = ReadInt32(bundleContents, 0x10);
		for (var n = 0; n < packageCount; n++)
		{
			var packageOffset = 0x18 + n * 0x18;
			if (packageOffset + 0x18 > bundleContents.Length)
			{
				break;
			}

			var packageSize = ReadInt64(bundleContents, packageOffset);
			var nameOffset = ReadInt32(bundleContents, packageOffset + 8);
			var itemsCount = ReadInt32(bundleContents, packageOffset + 12);
			var itemsOffset = ReadInt32(bundleContents, packageOffset + 16);
			var name = ReadNullTerminatedString(bundleContents, nameOffset);
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			var package = new PackageInfo(name, packageSize);
			for (var i = 0; i < itemsCount; i++)
			{
				var itemOffset = itemsOffset + 0x10 * i;
				if (itemOffset + 0x10 > bundleContents.Length)
				{
					break;
				}

				package.Entries.Add(new BundleEntryInfo(
					ReadInt64(bundleContents, itemOffset),
					// The SDK stores this as an unsigned 32-bit offset into the
					// uncompressed bundle.  Current bundles legitimately set the
					// high bit, so reading it as Int32 makes valid GPU ranges
					// negative and makes the chunk map lookup fail.
					ReadUInt32(bundleContents, itemOffset + 8),
					bundleContents[itemOffset + 0x0F]));
			}

			_packages[name] = package;
		}
	}

	private async ValueTask BuildBundleChunkOffsetMapsAsync(CancellationToken cancellationToken)
	{
		foreach (var path in Directory.EnumerateFiles(_gameDataDirectory))
		{
			var name = Path.GetFileName(path);
			var extension = Path.GetExtension(name);
			if (name.Contains(".patch", StringComparison.OrdinalIgnoreCase)
				|| !(extension.Length == 0 || string.Equals(extension, ".stream", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".nxa", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".gpu_resources", StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
			if (stream.Length < 12)
			{
				continue;
			}

			var header = new byte[12];
			await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
			if (BinaryPrimitivesLE.ReadUInt32(header.AsSpan(0, 4)) != DsarMagic)
			{
				continue;
			}

			var chunkCount = BinaryPrimitivesLE.ReadUInt32(header.AsSpan(8, 4));
			var map = new Dictionary<long, int>();
			for (var i = 0; i < chunkCount; i++)
			{
				stream.Position = 0x20 + i * 0x20;
				var chunkHeader = new byte[8];
				await ReadExactAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
				map[(long)BinaryPrimitivesLE.ReadUInt64(chunkHeader)] = i;
			}

			_bundleChunkOffsets[name] = map;
		}
	}

	private async ValueTask<byte[]> GetResourceFromDsarAsync(string bundlePath, long resourceOffset, CancellationToken cancellationToken)
	{
		var bundleName = Path.GetFileName(bundlePath);
		if (!_bundleChunkOffsets.TryGetValue(bundleName, out var offsets))
		{
			offsets = await BuildSingleBundleChunkOffsetMapAsync(bundlePath, cancellationToken).ConfigureAwait(false);
			_bundleChunkOffsets[bundleName] = offsets;
		}

		if (!offsets.TryGetValue(resourceOffset, out var chunkIndex))
		{
			return Array.Empty<byte>();
		}

		var chunks = new List<byte[]>();
		await using var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var chunkCount = await ReadDsarChunkCountAsync(stream, cancellationToken).ConfigureAwait(false);
		while (chunkIndex < chunkCount)
		{
			var chunk = await ReadDsarChunkAsync(stream, chunkIndex, cancellationToken).ConfigureAwait(false);
			if ((chunk.ChunkType & ChunkStart) != 0 && chunks.Count > 0)
			{
				break;
			}

			chunks.Add(chunk.Data);
			chunkIndex++;
		}

		return Combine(chunks);
	}

	private static async ValueTask<byte[]> DecompressDsarAsync(string filePath, CancellationToken cancellationToken)
	{
		var chunks = new List<byte[]>();
		await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var chunkCount = await ReadDsarChunkCountAsync(stream, cancellationToken).ConfigureAwait(false);
		for (var i = 0; i < chunkCount; i++)
		{
			chunks.Add((await ReadDsarChunkAsync(stream, i, cancellationToken).ConfigureAwait(false)).Data);
		}

		return Combine(chunks);
	}

	private static async ValueTask<Dictionary<long, int>> BuildSingleBundleChunkOffsetMapAsync(string path, CancellationToken cancellationToken)
	{
		var map = new Dictionary<long, int>();
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var chunkCount = await ReadDsarChunkCountAsync(stream, cancellationToken).ConfigureAwait(false);
		for (var i = 0; i < chunkCount; i++)
		{
			stream.Position = 0x20 + i * 0x20;
			var chunkHeader = new byte[8];
			await ReadExactAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
			map[(long)BinaryPrimitivesLE.ReadUInt64(chunkHeader)] = i;
		}

		return map;
	}

	private static async ValueTask<int> ReadDsarChunkCountAsync(Stream stream, CancellationToken cancellationToken)
	{
		stream.Position = 0;
		var header = new byte[12];
		await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
		if (BinaryPrimitivesLE.ReadUInt32(header.AsSpan(0, 4)) != DsarMagic)
		{
			return 0;
		}

		return checked((int)BinaryPrimitivesLE.ReadUInt32(header.AsSpan(8, 4)));
	}

	private static async ValueTask<DsarChunk> ReadDsarChunkAsync(Stream stream, int chunkIndex, CancellationToken cancellationToken)
	{
		stream.Position = 0x20 + chunkIndex * 0x20;
		var header = new byte[0x20];
		await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

		var compressedOffset = (long)BinaryPrimitivesLE.ReadUInt64(header.AsSpan(8, 8));
		var uncompressedSize = checked((int)BinaryPrimitivesLE.ReadUInt32(header.AsSpan(16, 4)));
		var compressedSize = checked((int)BinaryPrimitivesLE.ReadUInt32(header.AsSpan(20, 4)));
		var compressionType = header[24];
		var chunkType = header[25];

		stream.Position = compressedOffset;
		var compressed = new byte[compressedSize];
		await ReadExactAsync(stream, compressed, cancellationToken).ConfigureAwait(false);

		if (compressionType == CompressionUncompressed)
		{
			return new DsarChunk(chunkType, compressed);
		}

		if (compressionType != CompressionLz4)
		{
			throw new InvalidDataException($"Unsupported DSAR compression type: {compressionType}");
		}

		var output = new byte[uncompressedSize];
		var decoded = LZ4Codec.Decode(compressed, output);
		if (decoded < 0)
		{
			throw new InvalidDataException("Failed to decompress DSAR LZ4 chunk.");
		}

		return new DsarChunk(chunkType, output);
	}

	private static async ValueTask<PackageStorageType> GetPackageTypeAsync(string fullPath, CancellationToken cancellationToken)
	{
		if (!File.Exists(fullPath))
		{
			return PackageStorageType.Bundled;
		}

		await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var header = new byte[4];
		await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
		return BinaryPrimitivesLE.ReadUInt32(header) == DsarMagic ? PackageStorageType.Dsar : PackageStorageType.Legacy;
	}

	private static async ValueTask<byte[]?> ReadLegacyTocAsync(string fullPath, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var header = new byte[12];
		await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
		if (BinaryPrimitivesLE.ReadUInt32(header.AsSpan(0, 4)) != TocMagic)
		{
			return null;
		}

		var numTypes = BinaryPrimitivesLE.ReadUInt32(header.AsSpan(4, 4));
		var numFiles = BinaryPrimitivesLE.ReadUInt32(header.AsSpan(8, 4));
		var bytesToRead = checked(60 + checked((int)numTypes * 32) + checked((int)numFiles * 80));
		stream.Position = 0;
		var buffer = new byte[bytesToRead];
		await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
		return buffer;
	}

	private static async ValueTask<byte[]?> ReadLegacyResourceAsync(string fullPath, long offset, int size, CancellationToken cancellationToken)
	{
		if (size <= 0 || !File.Exists(fullPath))
		{
			return null;
		}

		await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (offset < 0 || offset + size > stream.Length)
		{
			return null;
		}

		stream.Position = offset;
		var buffer = new byte[size];
		await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
		return buffer;
	}

	private static void AppendFileFingerprint(StringBuilder builder, string path)
	{
		if (!File.Exists(path))
		{
			builder.Append(Path.GetFileName(path)).Append("=missing;");
			return;
		}

		var file = new FileInfo(path);
		builder.Append(Path.GetFileName(path)).Append('=').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks).Append(';');
	}

	private static string Hash(string value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static byte[] Combine(IReadOnlyList<byte[]> chunks)
	{
		var total = chunks.Sum(x => x.Length);
		var result = new byte[total];
		var offset = 0;
		foreach (var chunk in chunks)
		{
			Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
			offset += chunk.Length;
		}

		return result;
	}

	private static long ReadInt64(byte[] data, int offset)
		=> BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));

	private static int ReadInt32(byte[] data, int offset)
		=> BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

	private static uint ReadUInt32(byte[] data, int offset)
		=> BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

	private static bool IsBasePackageName(string name)
		=> !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
			&& !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase);

	private static string ReadNullTerminatedString(byte[] data, int offset)
	{
		if (offset < 0 || offset >= data.Length)
		{
			return string.Empty;
		}

		var end = offset;
		while (end < data.Length && data[end] != 0)
		{
			end++;
		}

		return Encoding.UTF8.GetString(data, offset, end - offset);
	}

	private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		var totalRead = 0;
		while (totalRead < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				throw new EndOfStreamException();
			}
			totalRead += read;
		}
	}

	private enum PackageStorageType
	{
		Legacy,
		Dsar,
		Bundled,
	}

	private sealed record PackageInfo(string Name, long Size)
	{
		public List<BundleEntryInfo> Entries { get; } = new();
	}

	private sealed record BundleEntryInfo(long OriginalArchiveOffset, long StartOffset, int BundleIndex);

	private sealed record DsarChunk(byte ChunkType, byte[] Data);
}
