using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

// 这一层必须显式别名：HD2ModCore 自己也有 PatchTocScanner / PatchEntryPayloadReader（命名空间
// HD2ModCore.Infrastructure 就在外层），不别名的话编译器会挑中 Core 那一套，返回值的条目类型
// 与 PatchArchiveWriter 要的不是同一个。
using AdaptAddition = HD2ModAdaptation.PatchReconstruction.PatchArchiveAdditionalEntry;
using AdaptEdit = HD2ModAdaptation.PatchReconstruction.PatchUnitMeshEditResult;
using AdaptEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using AdaptScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using AdaptWriter = HD2ModAdaptation.PatchReconstruction.PatchArchiveWriter;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>「把来源 unit 0x… 写到目标键 0x…」的一步。同一个来源可以有多条（每个身形一条）。</summary>
internal sealed record SectionalUnitRename(string SourceFileId, CoreAssetKey Target);

/// <summary>P4b：一个合并好的 unit 载荷，要写到这些目标键上（每个键一份副本，字节相同）。</summary>
internal sealed record SectionalMergedOutput(IReadOnlyList<CoreAssetKey> Targets, SectionalMergedUnit Unit);

/// <param name="RewrittenUnitCount">真正写出去的改名副本数（含合并产物的副本）。</param>
/// <param name="HiddenUnitCount">写成隐藏壳的目标 unit 数。</param>
/// <param name="HiddenTriangles">隐藏壳的三角形总数。</param>
/// <param name="HiddenMeshCount">隐藏壳里有几个 mesh。**每个 mesh 恰好 1 个三角形**，所以两者必须相等。</param>
internal sealed record SectionalUnitPatchResult(
	int SourceUnitCount,
	int RewrittenUnitCount,
	int HiddenUnitCount,
	int HiddenTriangles,
	int HiddenMeshCount,
	IReadOnlyList<string> Warnings);

/// <summary>
/// 把 <c>sectional/</c> 的 unit patch 重写成"改名副本 + 隐藏壳"的 patch。
/// <para>
/// <b>这不是换甲引擎。</b>它只做三件事：把来源条目按新键复制（payload 字节原样）、
/// 给没用到的目标键塞一份隐藏壳、把来源条目从输出里去掉。没有几何重建、没有骨骼对齐、
/// 没有 LOD 展开——来源 unit 本身就是合法的 HD2 unit，换 key 就该能用。
/// </para>
/// <para>
/// <b>GPU 不重复。</b>同一个来源被复制到多个键时共用同一个 <c>byte[]</c> 实例；
/// 写入侧按内容哈希去重，所以两份副本不会让文件翻倍。隐藏壳的所有条目共用同一份字节。
/// </para>
/// </summary>
internal static class SectionalUnitPatchWriter
{
	/// <summary>重写一个 unit patch。</summary>
	/// <param name="sourcePatchPath">来源 patch（<c>sectional/&lt;archive&gt;.patch_0</c>）。</param>
	/// <param name="outputDirectory">输出目录（**必须**与来源目录不同，写入侧会拒绝覆盖自己）。</param>
	/// <param name="renames">要写出去的改名副本。</param>
	/// <param name="hiddenKeys">要顶成隐藏壳的目标 unit 键。</param>
	/// <param name="hiddenSource">隐藏壳载荷来源。</param>
	internal static async Task<SectionalUnitPatchResult> WriteAsync(
		string sourcePatchPath,
		string outputDirectory,
		IReadOnlyList<SectionalUnitRename> renames,
		IReadOnlyList<CoreAssetKey> hiddenKeys,
		ISectionalHiddenUnitSource hiddenSource,
		CancellationToken cancellationToken = default,
		IReadOnlyList<SectionalMergedOutput>? mergedOutputs = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		ArgumentNullException.ThrowIfNull(renames);
		ArgumentNullException.ThrowIfNull(hiddenKeys);
		ArgumentNullException.ThrowIfNull(hiddenSource);

		var entries = await new AdaptScanner()
			.ScanEntriesAsync(sourcePatchPath, cancellationToken)
			.ConfigureAwait(false);
		var unitEntries = entries
			.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			.ToArray();
		if (unitEntries.Length == 0)
			throw new InvalidDataException($"来源 patch 里没有 unit 条目：{Path.GetFileName(sourcePatchPath)}");

		var byFileId = new Dictionary<string, AdaptEntry>(StringComparer.Ordinal);
		foreach (var entry in unitEntries)
			byFileId[FileId(entry.AssetKey.FileId)] = entry;

		var warnings = new List<string>();
		var reader = new AdaptPayloadReader();
		var payloads = new Dictionary<ulong, AdaptPayload>();

		async ValueTask<AdaptPayload> ReadPayloadAsync(AdaptEntry entry)
		{
			if (payloads.TryGetValue(entry.AssetKey.FileId, out var cached))
				return cached;
			var payload = await reader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			payloads[entry.AssetKey.FileId] = payload;
			return payload;
		}

		var additions = new List<AdaptAddition>(renames.Count + hiddenKeys.Count);
		foreach (var rename in renames)
		{
			if (!byFileId.TryGetValue(rename.SourceFileId, out var entry))
			{
				warnings.Add($"来源 unit 0x{rename.SourceFileId} 不在 patch 里：这一份不写出去。");
				continue;
			}

			var payload = await ReadPayloadAsync(entry).ConfigureAwait(false);
			additions.Add(new AdaptAddition(
				new AdaptKey(rename.Target.TypeId, rename.Target.FileId),
				payload.TocData,
				payload.StreamData,
				payload.GpuResourceData,
				entry.Unknown1,
				entry.Unknown2,
				entry.Unknown3,
				entry.Unknown4));
		}

		// P4b：合并产物不是来源条目的原样拷贝，载荷由合并器给；来源条目仍整体从输出里移除。
		var mergedCopies = 0;
		foreach (var output in mergedOutputs ?? [])
		{
			foreach (var target in output.Targets)
			{
				additions.Add(new AdaptAddition(
					new AdaptKey(target.TypeId, target.FileId),
					output.Unit.TocData,
					output.Unit.StreamData,
					output.Unit.GpuResourceData,
					output.Unit.Unknown1,
					output.Unit.Unknown2,
					output.Unit.Unknown3,
					output.Unit.Unknown4));
				mergedCopies++;
			}

			warnings.AddRange(output.Unit.Warnings);
		}

		var hiddenTriangles = 0;
		var hiddenMeshCount = 0;
		var hiddenCount = 0;
		if (hiddenKeys.Count != 0)
		{
			// 底座取 TOC 最小的那个：确定性（可复现）且产出最小。
			var baseEntry = unitEntries
				.OrderBy(entry => entry.TocDataSize)
				.ThenBy(entry => entry.AssetKey.FileId)
				.First();
			var basePayload = await ReadPayloadAsync(baseEntry).ConfigureAwait(false);
			var hidden = hiddenSource.Create(baseEntry, basePayload);
			hiddenTriangles = CountTriangles(hidden);
			hiddenMeshCount = hidden.MeshCount;

			foreach (var key in hiddenKeys)
			{
				additions.Add(new AdaptAddition(
					new AdaptKey(key.TypeId, key.FileId),
					hidden.TocData,
					Array.Empty<byte>(),
					hidden.GpuResourceData,
					hidden.Unknown1,
					hidden.Unknown2,
					hidden.Unknown3,
					hidden.Unknown4));
				hiddenCount++;
			}
		}

		await new AdaptWriter().WriteAsync(
			sourcePatchPath,
			outputDirectory,
			edits: Array.Empty<AdaptEdit>(),
			additionalEntries: additions,
			removedEntries: unitEntries,
			overwriteExisting: true,
			// 来源条目全部被移除，剩下的只有我们新加的条目：逐字节搬来源的 stream 没有意义。
			preserveOriginalStream: false,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return new SectionalUnitPatchResult(
			unitEntries.Length, renames.Count + mergedCopies, hiddenCount, hiddenTriangles, hiddenMeshCount, warnings);
	}

	/// <summary>把隐藏壳重新解析一遍、数它还剩几个三角形（验收用：必须恰好 1 个）。</summary>
	private static int CountTriangles(SectionalHiddenUnitPayload hidden)
	{
		var model = new UnitMeshReader().Read(hidden.TocData, hidden.GpuResourceData);
		return model.Meshes.Sum(mesh => mesh.Sections.Sum(section => (int)(section.NumIndices / 3)));
	}

	private static string FileId(ulong fileId)
		=> fileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
