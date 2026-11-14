using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using AdaptScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using AdaptPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>Mod 里的一个 patch TOC 文件：条目表和 TOC 字节（Unit / 材质的 TocData 就在里面）。</summary>
public sealed record KitModPatchFile(string Path, IReadOnlyList<AdaptEntry> Entries, byte[] TocBytes);

/// <summary>读出来的 Mod 内容：每个 patch 文件，加上规划器要的汇总。</summary>
public sealed record KitModContentScan(IReadOnlyList<KitModPatchFile> Files, KitConversionModContent Content);

/// <summary>
/// 读一个 Mod 节点的基底 patch（根 + 转换产物目录，<b>不含 <c>Overwrite/</c></b>），
/// 汇总 Unit / 材质 / 贴图以及 Mod 自己的引用关系。只读，不改任何文件。
/// </summary>
public static class KitModContentScanner
{
	public static async Task<KitModContentScan> ScanAsync(string nodeDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var scanner = new AdaptScanner();
		var unitReader = new UnitMaterialReferenceReader();
		var files = new List<KitModPatchFile>();
		var units = new HashSet<ulong>();
		var materials = new HashSet<ulong>();
		var textures = new HashSet<ulong>();
		var unitMaterials = new Dictionary<ulong, IReadOnlyList<ulong>>();
		var materialTextures = new Dictionary<ulong, IReadOnlyList<ulong>>();

		foreach (var path in ModNodePayloadLayout.EnumeratePayloadFilesByName(nodeDirectory).Values
			.Where(path => !IsSidecar(path) && ModNodePayloadLayout.IsPatchToc(path))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var entries = await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false);
			var toc = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			files.Add(new KitModPatchFile(path, entries, toc));
			foreach (var entry in entries)
			{
				var type = entry.AssetKey.TypeId;
				var id = entry.AssetKey.FileId;
				if (type is not (KitPollutionAnalyzer.UnitTypeId or KitPollutionAnalyzer.MaterialTypeId or KitPollutionAnalyzer.TextureTypeId))
					continue;
				var set = type == KitPollutionAnalyzer.UnitTypeId ? units : type == KitPollutionAnalyzer.MaterialTypeId ? materials : textures;
				if (!set.Add(id))
				{
					throw new InvalidDataException(
						$"同一个资源 0x{id:x16} 出现在这个 Mod 的多个 patch 里（比如材质变体包），转专属暂不支持这类 Mod。");
				}

				ReadReferences(toc, entry, unitReader, unitMaterials, materialTextures);
			}
		}

		if (files.Count == 0) throw new InvalidDataException("这个 Mod 里没有可读的 patch 文件。");
		return new KitModContentScan(files, new KitConversionModContent(units, materials, textures, unitMaterials, materialTextures));
	}

	private static void ReadReferences(
		byte[] toc,
		AdaptEntry entry,
		UnitMaterialReferenceReader unitReader,
		Dictionary<ulong, IReadOnlyList<ulong>> unitMaterials,
		Dictionary<ulong, IReadOnlyList<ulong>> materialTextures)
	{
		var type = entry.AssetKey.TypeId;
		var id = entry.AssetKey.FileId;
		var data = TocData(toc, entry);
		try
		{
			if (type == KitPollutionAnalyzer.UnitTypeId)
				unitMaterials[id] = unitReader.ReadReferenceBindings(data).Select(binding => binding.MaterialId).ToArray();
			else if (type == KitPollutionAnalyzer.MaterialTypeId)
				materialTextures[id] = ReadMaterialTextures(data).Select(reference => reference.TextureId).ToArray();
		}
		catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException)
		{
			throw new InvalidDataException($"读不懂 0x{id:x16} 的引用表：{exception.Message}", exception);
		}
	}

	internal static bool IsSidecar(string path)
		=> path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase);

	internal static ReadOnlySpan<byte> TocData(byte[] toc, AdaptEntry entry)
	{
		var offset = checked((int)entry.TocDataOffset);
		var size = checked((int)entry.TocDataSize);
		if (offset < 0 || size < 0 || offset + size > toc.Length)
			throw new InvalidDataException($"条目 0x{entry.AssetKey.FileId:x16} 的 TOC 数据在文件之外。");
		return toc.AsSpan(offset, size);
	}

	/// <summary>材质 payload 里的贴图引用（与 <see cref="StingrayMaterialReferenceReader"/> 同一套偏移），并给出每个引用在 payload 里的位置。</summary>
	internal static IReadOnlyList<(ulong TextureId, int PayloadOffset)> ReadMaterialTextures(ReadOnlySpan<byte> data)
	{
		const int numTexturesOffset = 64;
		const int numVariablesOffset = 104;
		const int textureRecordsOffset = 136;
		const int variableRecordSize = 20;
		if (data.Length < textureRecordsOffset) throw new InvalidDataException("材质 payload 太小。");
		var numTextures = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(numTexturesOffset, 4)));
		var numVariables = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(numVariablesOffset, 4)));
		var idsOffset = checked(textureRecordsOffset + numTextures * 4);
		_ = checked(idsOffset + numTextures * 8 + numVariables * variableRecordSize);
		if (data.Length < idsOffset + numTextures * 8) throw new EndOfStreamException("材质的贴图引用表被截断。");
		var result = new (ulong, int)[numTextures];
		for (var index = 0; index < numTextures; index++)
		{
			var position = idsOffset + index * 8;
			result[index] = (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(position, 8)), position);
		}

		return result;
	}
}

/// <summary>
/// 内容指纹：生成转专属记录时 Mod 的<b>基底</b>载荷长什么样（文件名、长度、最后写入时间）。
/// 不含 <c>Overwrite/</c>，所以转专属自己写的派生 patch 不会让指纹变掉；Mod 被更新（基底变了）才会变。
/// </summary>
public static class KitInstanceContentFingerprint
{
	public static string Compute(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var builder = new StringBuilder("kit-instance-content:v1\n");
		if (Directory.Exists(nodeDirectory))
		{
			foreach (var (name, path) in ModNodePayloadLayout.EnumeratePayloadFilesByName(nodeDirectory)
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!name.Contains(".patch_", StringComparison.OrdinalIgnoreCase)) continue;
				var info = new FileInfo(path);
				builder.Append(name.ToLowerInvariant()).Append('|')
					.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
			}
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}
}

/// <summary>
/// 派生 patch 写入器（规划 §6.2 / §6.3）：按 <see cref="KitConversionPlan"/> 改 ID、去掉不再输出的条目，
/// 写进该 Mod 节点目录的 <c>Overwrite/</c>，与根 patch 同名。<b>原 Mod 文件一个字节都不动。</b>
/// <para>
/// 复用 <see cref="PatchArchiveWriter"/>（它负责 TOC / stream / gpu 的重排与对齐），这里只决定「去掉哪些、加回哪些」。
/// 先写进暂存目录，全部成功后才搬进 <c>Overwrite/</c>；任何一步失败，暂存与已搬入的文件都清掉，不留半份。
/// </para>
/// </summary>
public static class KitDerivedPatchWriter
{
	public const string StagePrefix = ".kit-instance-stage-";

	/// <summary>要写的 patch 文件名（含 sidecar 后缀），供冲突检查与预览。</summary>
	public static IReadOnlyList<string> AffectedPatchNames(KitModContentScan scan, KitConversionPlan plan)
		=> scan.Files.Where(file => BuildEdits(file, scan, plan).HasWork).Select(file => Path.GetFileName(file.Path)).ToArray();

	public static async Task<IReadOnlyList<string>> WriteAsync(
		string nodeDirectory, KitModContentScan scan, KitConversionPlan plan, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		ArgumentNullException.ThrowIfNull(scan);
		ArgumentNullException.ThrowIfNull(plan);
		if (!plan.Succeeded) throw new InvalidOperationException("转专属计划不成立，不写文件：" + plan.FailureReason);

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);
		var work = scan.Files.Select(file => (File: file, Edits: BuildEdits(file, scan, plan))).Where(item => item.Edits.HasWork).ToArray();
		if (work.Length == 0) throw new InvalidOperationException("没有任何 patch 需要改写，不写文件。");

		// 冲突检查必须在写任何东西之前：Overwrite/ 里已有同名文件（装饰重建等功能的产物）就拒绝。
		foreach (var item in work)
		{
			var name = Path.GetFileName(item.File.Path);
			var clash = SuffixedNames(name).FirstOrDefault(candidate => File.Exists(Path.Combine(overwrite, candidate)));
			if (clash is not null)
				throw new InvalidOperationException(
					$"这个 Mod 的 Overwrite 目录里已经有 {clash}（其它功能的派生产物，或你手工放进去的），转专属会顶掉它，已拒绝。"
					+ "请先把它移走，或先撤销那个功能的产物。");
		}

		var stage = Path.Combine(nodeDirectory, StagePrefix + Guid.NewGuid().ToString("N"));
		var moved = new List<string>();
		var createdOverwrite = false;
		try
		{
			Directory.CreateDirectory(stage);
			var writer = new PatchArchiveWriter();
			var payloads = new AdaptPayloadReader();
			foreach (var item in work)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var additions = new List<PatchArchiveAdditionalEntry>();
				foreach (var addition in item.Edits.Additions)
				{
					var payload = await payloads.ReadPayloadAsync(addition.Source, cancellationToken).ConfigureAwait(false);
					additions.Add(new PatchArchiveAdditionalEntry(
						addition.NewKey,
						addition.RewriteToc?.Invoke(payload.TocData) ?? payload.TocData,
						payload.StreamData,
						payload.GpuResourceData,
						addition.Source.Unknown1,
						addition.Source.Unknown2,
						addition.Source.Unknown3,
						addition.Source.Unknown4));
				}

				var result = await writer.WriteAsync(
					item.File.Path,
					stage,
					edits: [],
					additionalEntries: additions,
					removedEntries: item.Edits.Removed,
					overwriteExisting: false,
					preserveOriginalStream: false,
					cancellationToken: cancellationToken).ConfigureAwait(false);

				// 体检：写出来的 TOC 条目数 = 原条目数 − 去掉的 + 加回的。
				var written = await new AdaptScanner().ScanEntriesAsync(result.TocFilePath, cancellationToken).ConfigureAwait(false);
				var expected = item.File.Entries.Count - item.Edits.Removed.Count + additions.Count;
				if (written.Count != expected)
					throw new InvalidDataException($"写出的 {Path.GetFileName(result.TocFilePath)} 条目数 {written.Count} 与预期 {expected} 不符。");
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (!Directory.Exists(overwrite))
			{
				Directory.CreateDirectory(overwrite);
				createdOverwrite = true;
			}

			foreach (var staged in Directory.EnumerateFiles(stage).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
			{
				var destination = Path.Combine(overwrite, Path.GetFileName(staged));
				File.Move(staged, destination);
				moved.Add(destination);
			}

			return moved.Select(Path.GetFileName).OfType<string>().ToArray();
		}
		catch
		{
			foreach (var path in moved)
			{
				try { File.Delete(path); }
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* 尽力而为 */ }
			}

			if (createdOverwrite) TryDeleteEmptyDirectory(overwrite);
			throw;
		}
		finally
		{
			try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* 暂存残留不影响正确性 */ }
		}
	}

	/// <summary>撤销：只删记录里写下的那几个文件；Overwrite/ 因此空了才删目录。返回实际删掉的文件名。</summary>
	public static IReadOnlyList<string> Remove(string nodeDirectory, IEnumerable<string> derivedFiles)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		ArgumentNullException.ThrowIfNull(derivedFiles);
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);
		var removed = new List<string>();
		foreach (var name in derivedFiles)
		{
			// 记录里只该有纯文件名；带路径分隔符的一律不碰，防止被篡改的记录删到别处。
			if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) continue;
			var path = Path.Combine(overwrite, name);
			if (!File.Exists(path)) continue;
			File.SetAttributes(path, FileAttributes.Normal);
			File.Delete(path);
			removed.Add(name);
		}

		if (Directory.Exists(overwrite)) TryDeleteEmptyDirectory(overwrite);
		return removed;
	}

	private static void TryDeleteEmptyDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* 留着空目录无害 */ }
	}

	private static IEnumerable<string> SuffixedNames(string name) => [name, name + ".stream", name + ".gpu_resources"];

	// ---- 改写计划 ------------------------------------------------------------------------

	private sealed record Addition(AdaptEntry Source, AdaptKey NewKey, Func<byte[], byte[]>? RewriteToc);

	private sealed record FileEdits(List<AdaptEntry> Removed, List<Addition> Additions)
	{
		public bool HasWork => Removed.Count > 0 || Additions.Count > 0;
	}

	private static FileEdits BuildEdits(KitModPatchFile file, KitModContentScan scan, KitConversionPlan plan)
	{
		var removed = new List<AdaptEntry>();
		var additions = new List<Addition>();
		var moveMap = plan.UnitMoves.ToDictionary(move => move.From, move => move.To);
		var removedMaterials = plan.RemovedMaterials.ToHashSet();
		var removedTextures = plan.RemovedTextures.ToHashSet();
		var globalTextures = plan.GlobalTextures.ToHashSet();
		foreach (var entry in file.Entries)
		{
			var id = entry.AssetKey.FileId;
			switch (entry.AssetKey.TypeId)
			{
				case KitPollutionAnalyzer.UnitTypeId:
				{
					var needsRewrite = scan.Content.UnitMaterials.TryGetValue(id, out var materials)
						&& materials.Any(plan.MaterialRemaps.ContainsKey);
					var hasMove = moveMap.TryGetValue(id, out var target);
					if (!needsRewrite && !hasMove) break;
					removed.Add(entry);
					additions.Add(new Addition(
						entry,
						new AdaptKey(entry.AssetKey.TypeId, hasMove ? target : id),
						needsRewrite ? toc => RewriteUnitToc(toc, plan.MaterialRemaps) : null));
					break;
				}
				case KitPollutionAnalyzer.MaterialTypeId:
				{
					if (removedMaterials.Contains(id)) { removed.Add(entry); break; }
					if (!plan.MaterialRemaps.TryGetValue(id, out var renamed)) break;
					removed.Add(entry);
					additions.Add(new Addition(
						entry,
						new AdaptKey(entry.AssetKey.TypeId, renamed),
						plan.TextureRemaps.Count == 0 ? null : toc => RewriteMaterialToc(toc, plan.TextureRemaps)));
					break;
				}
				case KitPollutionAnalyzer.TextureTypeId:
				{
					if (removedTextures.Contains(id)) { removed.Add(entry); break; }
					if (!plan.TextureRemaps.TryGetValue(id, out var renamed)) break;
					additions.Add(new Addition(entry, new AdaptKey(entry.AssetKey.TypeId, renamed), null));
					// 原 ID 的条目：还有留下来的材质在用它（仍全局生效）就留着，否则去掉。
					if (!globalTextures.Contains(id)) removed.Add(entry);
					break;
				}
			}
		}

		return new FileEdits(removed, additions);
	}

	/// <summary>Unit 材质表里的 u64 原地替换（TOC 0x70 指向的表）；其余字节不动。</summary>
	public static byte[] RewriteUnitToc(byte[] toc, IReadOnlyDictionary<ulong, ulong> map)
	{
		var copy = (byte[])toc.Clone();
		foreach (var binding in new UnitMaterialReferenceReader().ReadReferenceBindings(copy))
		{
			if (map.TryGetValue(binding.MaterialId, out var replacement))
				BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(checked((int)binding.MaterialIdPayloadRelativeOffset), 8), replacement);
		}

		return copy;
	}

	/// <summary>材质 payload 里的贴图引用 u64 原地替换；其余字节不动。</summary>
	public static byte[] RewriteMaterialToc(byte[] toc, IReadOnlyDictionary<ulong, ulong> map)
	{
		var copy = (byte[])toc.Clone();
		foreach (var (textureId, offset) in KitModContentScanner.ReadMaterialTextures(copy))
		{
			if (map.TryGetValue(textureId, out var replacement))
				BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset, 8), replacement);
		}

		return copy;
	}
}
