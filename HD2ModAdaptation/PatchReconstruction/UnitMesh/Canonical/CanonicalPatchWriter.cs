using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Writes an independently ordered canonical patch using the SDK slim TOC layout by default.
// Purpose: Writes an independent canonical patch from finalized, payload-owned entries.
// SDK references: CreatePatchFromActive(), AddEntryToPatchID(), TocEntry.Serialize(), TocEntry.SerializeData(), and StreamToc.Serialize().
public interface ICanonicalPatchWriter
{
	ValueTask<PatchArchiveFileWriteResult> WriteAsync(
		CanonicalPatchSession session,
		string outputDirectoryPath,
		string patchFileName = "canonical.patch_0",
		byte[]? headerTemplateTocData = null,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default);
}

public sealed class CanonicalPatchWriter : ICanonicalPatchWriter
{
	private const uint TocMagic = 4026531857;
	private const int LegacyHeaderPrefixSize = 60;
	private const int SdkHeaderPrefixSize = 72;
	private const int TypeSize = 32;
	private const int EntrySize = 80;
	private const int Alignment = 64;

	public async ValueTask<PatchArchiveFileWriteResult> WriteAsync(
		CanonicalPatchSession session,
		string outputDirectoryPath,
		string patchFileName = "canonical.patch_0",
		byte[]? headerTemplateTocData = null,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(patchFileName);
		if (!session.IsFinalized || !session.IsValid) throw new InvalidOperationException("Canonical patch writer accepts only valid finalized sessions.");
		if (session.DependencyClosureValidation != CanonicalDependencyClosureValidation.Valid)
			throw new InvalidOperationException("Canonical patch writer requires a valid dependency closure.");
		var entries = OrderEntries(session.Entries, headerTemplateTocData);
		if (entries.Any(entry => entry.Ownership == CanonicalPatchEntryOwnership.SourceRetained)) throw new InvalidOperationException("Canonical output cannot retain source entries.");
		if (entries.GroupBy(entry => entry.Key).Any(group => group.Count() != 1)) throw new InvalidOperationException("Canonical output contains duplicate entry keys.");

		var output = Path.GetFullPath(outputDirectoryPath);
		Directory.CreateDirectory(output);
		var tocPath = Path.Combine(output, patchFileName);
		var streamPath = tocPath + ".stream";
		var gpuPath = tocPath + ".gpu_resources";
		EnsureWritable(tocPath, overwriteExisting);
		EnsureWritable(streamPath, overwriteExisting);
		EnsureWritable(gpuPath, overwriteExisting);

		var header = BuildHeader(entries, headerTemplateTocData);
		var entryTableOffset = checked((long)header.Length);
		var payloadOffset = checked(entryTableOffset + entries.Length * EntrySize);
		var written = new List<PatchTocEntry>(entries.Length);
		long tocLength;
		long streamLength;
		long gpuLength;
		await using (var toc = new FileStream(tocPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
		await using (var stream = new FileStream(streamPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
		await using (var gpu = new FileStream(gpuPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
		{
			await toc.WriteAsync(header, cancellationToken).ConfigureAwait(false);
			toc.Position = payloadOffset;
			var gpuOffsetsByPayload = new Dictionary<string, ulong>(StringComparer.Ordinal);
			var gpuIdentitiesByReference = new Dictionary<byte[], string>(ByteArrayReferenceComparer.Instance);
			var gpuIdentitiesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var tocData = entry.TocData;
				var streamData = entry.StreamData;
				var gpuData = entry.GpuData;
				var tocDataOffset = checked((ulong)toc.Position);
				var tocLengthForEntry = GetPayloadLength(tocData, entry.TocDataPath, entry.TocDataSource);
				await WritePayloadAsync(toc, tocData, entry.TocDataPath, entry.TocDataSource, cancellationToken).ConfigureAwait(false);
				var streamLengthForEntry = GetPayloadLength(streamData, entry.StreamDataPath, entry.StreamDataSource);
				var streamOffset = await WriteAlignedAsync(stream, streamData, entry.StreamDataPath, entry.StreamDataSource, cancellationToken).ConfigureAwait(false);
				var gpuLengthForEntry = GetPayloadLength(gpuData, entry.GpuDataPath, entry.GpuDataSource);
				var gpuOffset = 0UL;
				if (gpuLengthForEntry != 0)
				{
					var pathIdentityKey = entry.GpuDataPath is null
						? null
						: $"{Path.GetFullPath(entry.GpuDataPath)}:{gpuLengthForEntry}";
					var gpuIdentity = gpuData is not null && gpuIdentitiesByReference.TryGetValue(gpuData, out var cachedIdentity)
						? cachedIdentity
						: pathIdentityKey is not null && gpuIdentitiesByPath.TryGetValue(pathIdentityKey, out var cachedPathIdentity)
							? cachedPathIdentity
							: await GetGpuPayloadIdentityAsync(gpuData, entry.GpuDataPath, entry.GpuDataSource, gpuLengthForEntry, cancellationToken).ConfigureAwait(false);
					if (gpuData is not null) gpuIdentitiesByReference.TryAdd(gpuData, gpuIdentity);
					if (pathIdentityKey is not null) gpuIdentitiesByPath.TryAdd(pathIdentityKey, gpuIdentity);
					if (!gpuOffsetsByPayload.TryGetValue(gpuIdentity, out gpuOffset))
					{
						gpuOffset = await WriteAlignedAsync(gpu, gpuData, entry.GpuDataPath, entry.GpuDataSource, cancellationToken).ConfigureAwait(false);
						gpuOffsetsByPayload.Add(gpuIdentity, gpuOffset);
					}
				}
				var serialized = new PatchTocEntry(entry.Key, tocPath, patchFileName, tocDataOffset, streamOffset, gpuOffset, entry.Unknown1, entry.Unknown2,
					checked((uint)tocLengthForEntry), checked((uint)streamLengthForEntry), checked((uint)gpuLengthForEntry), entry.Unknown3, entry.Unknown4, checked((uint)index + 1));
				written.Add(serialized);
			}
			toc.Position = entryTableOffset;
			foreach (var entry in written)
			{
				var data = new byte[EntrySize];
				WriteEntry(data, entry);
				await toc.WriteAsync(data, cancellationToken).ConfigureAwait(false);
			}
			var minimumTocLength = checked((long)entries.Length * 256);
			if (toc.Length < minimumTocLength)
				toc.SetLength(minimumTocLength);
			tocLength = toc.Length;
			streamLength = stream.Length;
			gpuLength = gpu.Length;
		}
		if (streamLength == 0) File.Delete(streamPath);
		if (gpuLength == 0) File.Delete(gpuPath);
		return new(output, tocPath, streamPath, gpuPath, tocLength, streamLength, gpuLength);
	}

	private static byte[] BuildHeader(IReadOnlyList<CanonicalPatchSessionEntry> entries, byte[]? template)
	{
		var headerPrefixSize = ResolveHeaderPrefixSize(template);
		var typeIds = ResolveTypeOrder(entries, template, headerPrefixSize);
		var result = new byte[headerPrefixSize + typeIds.Length * TypeSize];
		if (template is { Length: >= LegacyHeaderPrefixSize })
			template.AsSpan(0, headerPrefixSize).CopyTo(result);
		Write32(result, 0, TocMagic);
		Write32(result, 4, checked((uint)typeIds.Length));
		Write32(result, 8, checked((uint)entries.Count));
		for (var i = 0; i < typeIds.Length; i++)
		{
			var offset = headerPrefixSize + i * TypeSize;
			Write64(result, offset + 8, typeIds[i]);
			Write64(result, offset + 16, checked((ulong)entries.Count(entry => entry.Key.TypeId == typeIds[i])));
			Write32(result, offset + 24, 16);
			Write32(result, offset + 28, 64);
		}
		return result;
	}

	private static CanonicalPatchSessionEntry[] OrderEntries(IReadOnlyList<CanonicalPatchSessionEntry> entries, byte[]? template)
	{
		var headerPrefixSize = ResolveHeaderPrefixSize(template);
		var typeOrder = ResolveTypeOrder(entries, template, headerPrefixSize)
			.Select((typeId, index) => (typeId, index))
			.ToDictionary(item => item.typeId, item => item.index);
		return entries.Select((entry, index) => (entry, index))
			.OrderBy(item => typeOrder[item.entry.Key.TypeId])
			.ThenBy(item => item.index)
			.Select(item => item.entry)
			.ToArray();
	}

	private static ulong[] ResolveTypeOrder(IReadOnlyList<CanonicalPatchSessionEntry> entries, byte[]? template, int headerPrefixSize)
	{
		var entryTypeIds = entries.Select(entry => entry.Key.TypeId).Distinct().ToArray();
		if (template is null || template.Length < headerPrefixSize || template.Length < 12) return entryTypeIds;
		var templateTypeCount = BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(4, 4));
		var templateTableEnd = checked(headerPrefixSize + checked((int)templateTypeCount * TypeSize));
		if (template.Length < templateTableEnd) return entryTypeIds;
		var templateTypeIds = Enumerable.Range(0, checked((int)templateTypeCount))
			.Select(index => BinaryPrimitives.ReadUInt64LittleEndian(template.AsSpan(headerPrefixSize + index * TypeSize + 8, 8)))
			.Where(entryTypeIds.Contains)
			.Distinct()
			.ToArray();
		return templateTypeIds.Concat(entryTypeIds.Where(typeId => !templateTypeIds.Contains(typeId))).ToArray();
	}

	private static int ResolveHeaderPrefixSize(byte[]? template)
	{
		if (template is null || template.Length == 0) return SdkHeaderPrefixSize;
		if (template.Length >= SdkHeaderPrefixSize) return SdkHeaderPrefixSize;
		if (template.Length >= LegacyHeaderPrefixSize) return LegacyHeaderPrefixSize;
		throw new InvalidDataException("Canonical header template is shorter than the supported TOC header layouts.");
	}

	private static async ValueTask<ulong> WriteAlignedAsync(FileStream file, byte[]? data, string? path, CanonicalPayloadSourceRange? sourceRange, CancellationToken cancellationToken)
	{
		var length = GetPayloadLength(data, path, sourceRange);
		if (length == 0) return 0;
		var padding = (Alignment - (int)(file.Position % Alignment)) % Alignment;
		if (padding != 0) await file.WriteAsync(new byte[padding], cancellationToken).ConfigureAwait(false);
		var offset = checked((ulong)file.Position);
		await WritePayloadAsync(file, data, path, sourceRange, cancellationToken).ConfigureAwait(false);
		return offset;
	}

	private static async ValueTask WritePayloadAsync(FileStream destination, byte[]? data, string? path, CanonicalPayloadSourceRange? sourceRange, CancellationToken cancellationToken)
	{
		if (data is not null)
		{
			await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
			return;
		}
		if (path is not null)
		{
			await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
			await source.CopyToAsync(destination, 65536, cancellationToken).ConfigureAwait(false);
			return;
		}
		if (sourceRange is null) throw new InvalidDataException("Canonical payload has neither memory data, staged file, nor source range.");
		await using var rangedSource = new FileStream(sourceRange.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
		if (sourceRange.Offset > (ulong)rangedSource.Length || sourceRange.Offset + sourceRange.Length > (ulong)rangedSource.Length)
			throw new InvalidDataException("Canonical source payload range is outside its source file.");
		rangedSource.Position = checked((long)sourceRange.Offset);
		await CopyExactlyAsync(rangedSource, destination, sourceRange.Length, cancellationToken).ConfigureAwait(false);
	}

	private static int GetPayloadLength(byte[]? data, string? path, CanonicalPayloadSourceRange? sourceRange)
		=> data?.Length ?? (path is not null ? checked((int)new FileInfo(path).Length) : checked((int)(sourceRange?.Length ?? 0)));

	// A source range is immutable for the duration of one write and is the common fast-path
	// for direct Unit reuse. Other payloads use a byte-content identity.
	private static async ValueTask<string> GetGpuPayloadIdentityAsync(byte[]? data, string? path, CanonicalPayloadSourceRange? sourceRange, int length, CancellationToken cancellationToken)
	{
		if (sourceRange is not null)
			return $"range:{Path.GetFullPath(sourceRange.FilePath)}:{sourceRange.Offset}:{sourceRange.Length}";
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		if (data is not null)
		{
			hash.AppendData(data);
		}
		else if (path is not null)
		{
			await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
			await AppendHashAsync(source, hash, checked((uint)length), cancellationToken).ConfigureAwait(false);
		}
		else
		{
			throw new InvalidDataException("Canonical GPU payload has neither memory data, staged file, nor source range.");
		}
		return $"sha256:{length}:{Convert.ToHexString(hash.GetHashAndReset())}";
	}

	private static async ValueTask AppendHashAsync(Stream source, IncrementalHash hash, uint length, CancellationToken cancellationToken)
	{
		var buffer = new byte[65536];
		var remaining = checked((int)length);
		while (remaining > 0)
		{
			var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
			if (read == 0) throw new EndOfStreamException("Canonical GPU payload ended before its declared length.");
			hash.AppendData(buffer, 0, read);
			remaining -= read;
		}
	}

	private static async ValueTask CopyExactlyAsync(Stream source, Stream destination, uint length, CancellationToken cancellationToken)
	{
		var buffer = new byte[checked((int)Math.Min(length, 65536u))];
		var remaining = checked((int)length);
		while (remaining > 0)
		{
			var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
			if (read == 0) throw new EndOfStreamException("Canonical source payload ended before its declared range.");
			await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			remaining -= read;
		}
	}

	private static void WriteEntry(byte[] data, PatchTocEntry entry)
	{
		Write64(data, 0, entry.AssetKey.FileId); Write64(data, 8, entry.AssetKey.TypeId); Write64(data, 16, entry.TocDataOffset);
		Write64(data, 24, entry.StreamOffset); Write64(data, 32, entry.GpuResourceOffset); Write64(data, 40, entry.Unknown1); Write64(data, 48, entry.Unknown2);
		Write32(data, 56, entry.TocDataSize); Write32(data, 60, entry.StreamSize); Write32(data, 64, entry.GpuResourceSize); Write32(data, 68, entry.Unknown3); Write32(data, 72, entry.Unknown4); Write32(data, 76, entry.EntryIndex);
	}
	private static void EnsureWritable(string path, bool overwrite) { if (!overwrite && File.Exists(path)) throw new IOException($"Output file already exists: {path}"); }
	private sealed class ByteArrayReferenceComparer : IEqualityComparer<byte[]>
	{
		public static readonly ByteArrayReferenceComparer Instance = new();
		public bool Equals(byte[]? left, byte[]? right) => ReferenceEquals(left, right);
		public int GetHashCode(byte[] value) => RuntimeHelpers.GetHashCode(value);
	}
	private static void Write32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
	private static void Write64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), value);
}
