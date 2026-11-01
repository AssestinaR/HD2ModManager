using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Directly reconstructs and writes an adapted patch archive with all required dependency entries.
public sealed class PatchArchiveWriter
{
	private const uint TocMagic = 4026531857;
	private const int LegacyTypeOffset = 60;
	private const int SlimTypeOffset = 72;
	private const int TypeSize = 32;
	private const int EntrySize = 80;
	private const int Alignment = 64;
	private readonly IPatchTocScanner scanner;
	private readonly IPatchEntryPayloadReader payloadReader;

	public PatchArchiveWriter(IPatchTocScanner? scanner = null, IPatchEntryPayloadReader? payloadReader = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
	}

	public async ValueTask<PatchArchiveFileWriteResult> WriteAsync(
		string sourcePatchTocPath,
		string outputDirectoryPath,
		IReadOnlyCollection<PatchUnitMeshEditResult> edits,
		IReadOnlyCollection<PatchArchiveAdditionalEntry>? additionalEntries = null,
		IReadOnlyCollection<PatchTocEntry>? removedEntries = null,
		bool overwriteExisting = false,
		bool preserveOriginalStream = true,
		byte[]? headerTemplateTocData = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchTocPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentNullException.ThrowIfNull(edits);
		var source = Path.GetFullPath(sourcePatchTocPath);
		var output = Path.GetFullPath(outputDirectoryPath);
		if (string.Equals(Path.GetDirectoryName(source), output, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output directory must differ from the source patch directory.");
		var originalToc = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
		var layout = ResolveLayout(originalToc);
		var headerTemplate = headerTemplateTocData is { Length: > 0 } ? headerTemplateTocData : originalToc;
		var headerLayout = ResolveLayout(headerTemplate);
		var entries = await scanner.ScanEntriesAsync(source, cancellationToken).ConfigureAwait(false);
		var editMap = BuildEditMap(source, edits);
		var compositeMap = BuildCompositeMap(source, edits);
		var removed = BuildRemovalSet(source, removedEntries);
		var additions = additionalEntries ?? Array.Empty<PatchArchiveAdditionalEntry>();
		var retained = entries.Where(entry => !removed.Contains(Key(entry))).ToArray();
		ValidateAdditions(retained, additions);
		var useTemplateTypesOnly = !ReferenceEquals(headerTemplate, originalToc);
		var sources = OrderSources(headerTemplate, headerLayout, BuildSources(source, retained, additions));
		var header = BuildHeader(headerTemplate, headerLayout, sources.Select(item => item.Entry).ToArray(), useTemplateTypesOnly);
		var entryOffset = (long)header.Length;
		Directory.CreateDirectory(output);
		var tocPath = Path.Combine(output, Path.GetFileName(source));
		var streamPath = tocPath + ".stream";
		var gpuPath = tocPath + ".gpu_resources";
		EnsureWritable(tocPath, overwriteExisting); EnsureWritable(streamPath, overwriteExisting); EnsureWritable(gpuPath, overwriteExisting);
		long tocLength;
		long streamLength;
		long gpuLength;
		await using (var toc = new FileStream(tocPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
		await using (var stream = new FileStream(streamPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
		await using (var gpu = new FileStream(gpuPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
		{
			await toc.WriteAsync(header, cancellationToken).ConfigureAwait(false);
			var tocAppendPosition = entryOffset + (long)sources.Count * EntrySize;
			toc.SetLength(tocAppendPosition);
			if (preserveOriginalStream && File.Exists(source + ".stream"))
			{
				await using var originalStream = new FileStream(source + ".stream", FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
				await originalStream.CopyToAsync(stream, 65536, cancellationToken).ConfigureAwait(false);
			}
			var written = new List<PatchTocEntry>(sources.Count);
			var gpuOffsetsByPayload = new Dictionary<string, ulong>(StringComparer.Ordinal);
			foreach (var item in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var entry = item.Entry;
				var hasUnitEdit = editMap.TryGetValue(Key(entry), out var edit);
				var hasCompositeEdit = item.Addition is null && !hasUnitEdit && compositeMap.TryGetValue(entry.AssetKey, out edit);
				var original = item.Addition is null
					? hasUnitEdit ? edit!.OriginalPayload : await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false)
					: new PatchEntryPayload(entry, item.Addition.TocData, item.Addition.StreamData, item.Addition.GpuResourceData);
				var tocData = item.Addition?.TocData ?? (hasUnitEdit ? edit!.TocData : hasCompositeEdit ? edit!.CompositeTocData! : original.TocData);
				var streamData = item.Addition?.StreamData ?? original.StreamData;
				var gpuData = item.Addition?.GpuResourceData ?? (hasUnitEdit ? edit!.GpuResourceData : hasCompositeEdit ? edit!.CompositeGpuResourceData! : original.GpuResourceData);
				toc.Position = tocAppendPosition;
				await toc.WriteAsync(tocData, cancellationToken).ConfigureAwait(false);
				tocAppendPosition = toc.Position;
				var streamOffset = entry.StreamOffset;
				if (streamData.Length > 0 && (!preserveOriginalStream || item.Addition is not null))
				{
					Pad(stream);
					streamOffset = (ulong)stream.Position;
					await stream.WriteAsync(streamData, cancellationToken).ConfigureAwait(false);
				}
				var gpuOffset = 0UL;
				if (gpuData.Length > 0)
				{
					var gpuIdentity = GetGpuPayloadIdentity(gpuData);
					if (!gpuOffsetsByPayload.TryGetValue(gpuIdentity, out gpuOffset))
					{
						Pad(gpu);
						gpuOffset = (ulong)gpu.Position;
						await gpu.WriteAsync(gpuData, cancellationToken).ConfigureAwait(false);
						gpuOffsetsByPayload.Add(gpuIdentity, gpuOffset);
					}
				}
				var updated = entry with { TocDataOffset = (ulong)(tocAppendPosition - tocData.Length), StreamOffset = streamOffset, GpuResourceOffset = gpuOffset, TocDataSize = checked((uint)tocData.Length), StreamSize = checked((uint)streamData.Length), GpuResourceSize = checked((uint)gpuData.Length), EntryIndex = checked((uint)written.Count + 1) };
				written.Add(updated);
				var entryData = new byte[EntrySize];
				WriteEntry(entryData, 0, updated);
				toc.Position = entryOffset + (written.Count - 1) * EntrySize;
				await toc.WriteAsync(entryData, cancellationToken).ConfigureAwait(false);
			}
			if (tocAppendPosition < sources.Count * 256L) tocAppendPosition = sources.Count * 256L;
			toc.SetLength(tocAppendPosition);
			tocLength = tocAppendPosition;
			streamLength = stream.Length;
			gpuLength = gpu.Length;
		}
		if (streamLength == 0) File.Delete(streamPath);
		if (gpuLength == 0) File.Delete(gpuPath);
		else
		{
			// **物理参数容器（HD2PBX01）跟随**：这一步是"按条目重写一个 patch"，
			// 尾部附加的数据不在条目里，所以不会自动跟过来。实测踩过两次：
			// 护甲标准化（部署前）与应用为 RE2310（custom-model convert）重写之后容器消失，
			// 运行时读不到参数 → 退回外部 rig 文件 → 拿别的模型的参数去驱动。
			// 协议允许容器放在受引用范围**之后**，所以原样搬到输出末尾即可，不影响任何 TOC 偏移。
			var sourceGpuPath = source + ".gpu_resources";
			// 规则只有一份实现（PhysBoneCarrier.CarryFromAnyAsync），这里只是喂来源。
			if (await PhysBoneCarrier.CarryFromAnyAsync([sourceGpuPath], gpuPath, cancellationToken).ConfigureAwait(false))
				gpuLength = new FileInfo(gpuPath).Length;
		}
		return new PatchArchiveFileWriteResult(output, tocPath, streamPath, gpuPath, tocLength, streamLength, gpuLength);
	}

	private static List<Source> BuildSources(string path, IReadOnlyList<PatchTocEntry> retained, IReadOnlyCollection<PatchArchiveAdditionalEntry> additions)
	{
		var sources = retained.Select(entry => new Source(entry, null)).ToList();
		sources.AddRange(additions.Select(addition => new Source(new PatchTocEntry(addition.AssetKey, path, Path.GetFileName(path), Unknown1: addition.Unknown1, Unknown2: addition.Unknown2, Unknown3: addition.Unknown3, Unknown4: addition.Unknown4), addition)));
		return sources;
	}

	private static List<Source> OrderSources(byte[] data, Layout layout, IReadOnlyList<Source> sources)
	{
		var existing = ReadTypes(data, layout).Select(record => record.TypeId).ToArray();
		var known = existing.ToHashSet();
		var order = existing.Concat(sources.Select(item => item.Entry.AssetKey.TypeId).Where(id => !known.Contains(id)).Distinct().OrderBy(id => id)).Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
		return sources.Select((source, index) => (source, index)).OrderBy(item => order[item.source.Entry.AssetKey.TypeId]).ThenBy(item => item.index).Select(item => item.source).ToList();
	}

	private static byte[] BuildHeader(byte[] original, Layout layout, IReadOnlyList<PatchTocEntry> entries, bool useEntryTypesOnly = false)
	{
		var originalTypes = ReadTypes(original, layout);
		var template = originalTypes.FirstOrDefault(record => record.TypeId != 0).Data;
		var entryTypeIds = entries.Select(entry => entry.AssetKey.TypeId).Distinct().ToHashSet();
		var baseTypes = useEntryTypesOnly ? originalTypes.Where(record => entryTypeIds.Contains(record.TypeId)).ToArray() : originalTypes.ToArray();
		var known = baseTypes.Select(record => record.TypeId).ToHashSet();
		var extra = entries.Select(entry => entry.AssetKey.TypeId).Where(id => !known.Contains(id)).Distinct().OrderBy(id => id).Select(id => NewType(id, template)).ToArray();
		var types = baseTypes.Concat(extra).ToArray(); var result = new byte[layout.TypeOffset + types.Length * TypeSize];
		original.AsSpan(0, layout.TypeOffset).CopyTo(result); Write32(result, 4, (uint)types.Length); Write32(result, 8, (uint)entries.Count);
		for (var i = 0; i < types.Length; i++) types[i].Data.CopyTo(result.AsSpan(layout.TypeOffset + i * TypeSize));
		var counts = entries.GroupBy(entry => entry.AssetKey.TypeId).ToDictionary(group => group.Key, group => (ulong)group.Count());
		for (var i = 0; i < types.Length; i++) { counts.TryGetValue(types[i].TypeId, out var count); Write64(result, layout.TypeOffset + i * TypeSize + 16, count); }
		return result;
	}

	private static Layout ResolveLayout(ReadOnlySpan<byte> data)
	{
		if (data.Length < 12 || Read32(data, 0) != TocMagic) throw new InvalidDataException("Invalid patch TOC.");
		var typeCount = Read32(data, 4); var entryCount = Read32(data, 8); var legacy = LegacyTypeOffset + checked((int)typeCount * TypeSize); var slim = SlimTypeOffset + checked((int)typeCount * TypeSize);
		var slimScore = Score(data, SlimTypeOffset, slim, typeCount, entryCount);
		var legacyScore = Score(data, LegacyTypeOffset, legacy, typeCount, entryCount);
		return typeCount != 0 && slimScore >= legacyScore ? new Layout(SlimTypeOffset, typeCount) : new Layout(LegacyTypeOffset, typeCount);
	}

	private static int Score(ReadOnlySpan<byte> data, int typeOffset, int entryOffset, uint typeCount, uint entryCount)
	{
		if (data.Length < entryOffset + checked((int)entryCount * EntrySize)) return int.MinValue;
		var types = new HashSet<ulong>(); var declared = 0UL;
		for (var i = 0; i < typeCount; i++) { var offset = typeOffset + i * TypeSize; types.Add(Read64(data, offset + 8)); declared += Read64(data, offset + 16); }
		var score = declared == entryCount ? 1000 : 0;
		for (var i = 0; i < entryCount; i++) { var offset = entryOffset + i * EntrySize; if (Read64(data, offset) != 0) score++; if (types.Contains(Read64(data, offset + 8))) score += 10; }
		return score;
	}

	private static List<TypeRecord> ReadTypes(byte[] data, Layout layout) => Enumerable.Range(0, checked((int)layout.TypeCount)).Select(index => { var raw = data.AsSpan(layout.TypeOffset + index * TypeSize, TypeSize).ToArray(); return new TypeRecord(Read64(raw, 8), raw); }).ToList();
	private static TypeRecord NewType(ulong typeId, byte[]? template) { var data = template?.Length == TypeSize ? template.ToArray() : new byte[TypeSize]; Write64(data, 8, typeId); Write64(data, 16, 0); Write32(data, 24, 16); Write32(data, 28, 64); return new TypeRecord(typeId, data); }
	private static Dictionary<EntryKey, PatchUnitMeshEditResult> BuildEditMap(string path, IReadOnlyCollection<PatchUnitMeshEditResult> edits) { var result = new Dictionary<EntryKey, PatchUnitMeshEditResult>(); foreach (var edit in edits) { ValidatePath(path, edit.Entry); if (!result.TryAdd(Key(edit.Entry), edit)) throw new InvalidDataException("Duplicate patch entry edit."); } return result; }
	private static Dictionary<AssetKey, PatchUnitMeshEditResult> BuildCompositeMap(string path, IReadOnlyCollection<PatchUnitMeshEditResult> edits) { var result = new Dictionary<AssetKey, PatchUnitMeshEditResult>(); foreach (var edit in edits.Where(edit => edit.CompositeAssetKey.HasValue)) { ValidatePath(path, edit.Entry); if (edit.CompositeTocData is null || edit.CompositeGpuResourceData is null || !result.TryAdd(edit.CompositeAssetKey!.Value, edit)) throw new InvalidDataException("Invalid or duplicate composite edit."); } return result; }
	private static HashSet<EntryKey> BuildRemovalSet(string path, IReadOnlyCollection<PatchTocEntry>? removed) { var result = new HashSet<EntryKey>(); foreach (var entry in removed ?? Array.Empty<PatchTocEntry>()) { ValidatePath(path, entry); result.Add(Key(entry)); } return result; }
	private static void ValidateAdditions(IReadOnlyList<PatchTocEntry> entries, IReadOnlyCollection<PatchArchiveAdditionalEntry> additions) { var keys = entries.Select(entry => entry.AssetKey).ToHashSet(); foreach (var addition in additions) { if (!keys.Add(addition.AssetKey)) throw new InvalidDataException($"Duplicate additional asset {addition.AssetKey.TypeId:x16}/{addition.AssetKey.FileId:x16}."); } }
	private static void ValidatePath(string path, PatchTocEntry entry) { if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(entry.SourceFilePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("All entries must belong to the patch being rebuilt."); }
	private static EntryKey Key(PatchTocEntry entry) => new(entry.EntryIndex, entry.AssetKey.TypeId, entry.AssetKey.FileId);
	private static async ValueTask<byte[]> ReadOptionalAsync(string path, CancellationToken cancellationToken) => File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : Array.Empty<byte>();
	private static void EnsureWritable(string path, bool overwrite) { if (!overwrite && File.Exists(path)) throw new IOException($"Output file already exists: {path}"); }
	private static void Pad(Stream stream) { var count = (Alignment - (int)(stream.Position % Alignment)) % Alignment; for (var i = 0; i < count; i++) stream.WriteByte(0); }
	private static void WriteEntry(byte[] data, int offset, PatchTocEntry entry) { Write64(data, offset, entry.AssetKey.FileId); Write64(data, offset + 8, entry.AssetKey.TypeId); Write64(data, offset + 16, entry.TocDataOffset); Write64(data, offset + 24, entry.StreamOffset); Write64(data, offset + 32, entry.GpuResourceOffset); Write64(data, offset + 40, entry.Unknown1); Write64(data, offset + 48, entry.Unknown2); Write32(data, offset + 56, entry.TocDataSize); Write32(data, offset + 60, entry.StreamSize); Write32(data, offset + 64, entry.GpuResourceSize); Write32(data, offset + 68, entry.Unknown3); Write32(data, offset + 72, entry.Unknown4); Write32(data, offset + 76, entry.EntryIndex); }
	private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
	private static string GetGpuPayloadIdentity(byte[] data) => $"{data.Length}:{Convert.ToHexString(SHA256.HashData(data))}";
	private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
	private static void Write32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
	private static void Write64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), value);
	private readonly record struct EntryKey(uint Index, ulong TypeId, ulong FileId);
	private readonly record struct Layout(int TypeOffset, uint TypeCount);
	private readonly record struct TypeRecord(ulong TypeId, byte[] Data);
	private readonly record struct Source(PatchTocEntry Entry, PatchArchiveAdditionalEntry? Addition);
}
