using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptationScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;

namespace HD2ModCore.Infrastructure;

/// <summary>构建映射表时被跳过的 unit，以及跳过它的原因。</summary>
public sealed record WardrobeSectionMapSkippedUnit(string FileId, string Reason);

/// <summary>构建结果：文档本身 + 被跳过的 unit（跳过的必须能说出来，否则就是静默丢数据）。</summary>
public sealed record WardrobeSectionMapBuildResult(
	WardrobeSectionMapDocument Document,
	IReadOnlyList<WardrobeSectionMapSkippedUnit> Skipped);

/// <summary>
/// <c>sections.json</c> 的读写与构建。规格见
/// <c>HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md</c> §11–§12。
///
/// 这是**构建产物**：由 patch 现算出来，不手写。它存在的唯一理由是——
/// 掩码只记语义身份，编辑器和 CLI 需要一份"身份 → 偏移"的对照才能把点击翻译成写入；
/// 运行时不需要它，运行时自己从内存里的 TOC 走一遍（见 notes 里那份实测）。
///
/// 三个字段在这里被钉死成常量并**在读取时校验**：节记录大小、`numIndices` 偏移、
/// 偏移口径。它们是运行时硬编码的假设，如果哪天构建管线改了布局却让旧表继续通过，
/// 后果是"关掉了别的字段"——最难查、也最危险的一种失效。宁可直接拒绝读。
///
/// This is a build artefact computed from a patch, never hand-written. Its only job is to
/// map semantic identity to offsets for the editor and the CLI; the runtime walks the
/// in-memory TOC instead. The three layout constants are re-validated on read because they
/// are hard-coded runtime assumptions: a build that changed them must fail loudly here
/// rather than zero the wrong field later.
/// </summary>
public static class WardrobeSectionMapFile
{
	/// <summary>文件默认名，与 <c>wardrobe.masks</c> 放在一起。</summary>
	public const string FileName = "sections.json";

	/// <summary>上限：实测部署包 23 个 unit / 160 个 section 约 90 KiB，留足一个数量级。</summary>
	public const int MaximumBytes = 64 * 1024 * 1024;

	/// <summary>
	/// 序列化选项。命名策略用 camelCase 而不是逐字段写 <c>JsonPropertyName</c>：
	/// 记录的属性名本来就是规范名的 PascalCase 形式，一处策略就能保证两边永不走偏。
	/// </summary>
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	// --- 构建 ---------------------------------------------------------------

	/// <summary>
	/// 从一个 patch 的 TOC 现算映射表。只读：打开文件、解析、返回，不写任何东西。
	/// 单个 unit 解析失败不会中断整次构建——一个坏 unit 不该让整份表产不出来，
	/// 但失败必须进 <c>Skipped</c>，否则用户会以为"这个 unit 没有 section"。
	/// </summary>
	public static async ValueTask<WardrobeSectionMapBuildResult> BuildAsync(
		string patchTocPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocPath);
		var fullPath = Path.GetFullPath(patchTocPath);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException($"找不到 patch 文件：{fullPath}", fullPath);

		var scanner = new AdaptationScanner();
		var entries = await scanner.ScanEntriesAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var reader = new PatchUnitMeshReader();
		var units = new List<WardrobeSectionMapUnit>();
		var skipped = new List<WardrobeSectionMapSkippedUnit>();
		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
				continue;

			var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			PatchUnitMesh unit;
			try
			{
				unit = await reader.ReadAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
			{
				// 解析失败是数据问题，不是程序错误：记下原因继续。
				skipped.Add(new WardrobeSectionMapSkippedUnit(fileId, $"解析失败：{exception.Message}"));
				continue;
			}

			var built = BuildUnit(fileId, unit.Model);
			if (built is null)
			{
				skipped.Add(new WardrobeSectionMapSkippedUnit(fileId, "没有可用的 LOD 家族（缺 LOD0 或全部是静态件）"));
				continue;
			}

			units.Add(built);
		}

		// 排序：编辑器画树和 CLI 输出都需要稳定顺序，否则每次构建的 diff 都是噪音。
		units.Sort((left, right) => string.CompareOrdinal(left.FileId, right.FileId));
		var gpuPath = fullPath + ".gpu_resources";
		var document = new WardrobeSectionMapDocument(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch(
				fullPath,
				await Sha256Async(fullPath, cancellationToken).ConfigureAwait(false),
				new FileInfo(fullPath).Length,
				File.Exists(gpuPath) ? await Sha256Async(gpuPath, cancellationToken).ConfigureAwait(false) : null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			units);
		return new WardrobeSectionMapBuildResult(document, skipped);
	}

	private static WardrobeSectionMapUnit? BuildUnit(string fileId, UnitMeshModel model)
	{
		// 只有 lod >= 0 的 mesh 属于 LOD 家族；lod = -1 的那些是独立的单节静态件。
		var family = model.Meshes.Where(mesh => mesh.LodIndex >= 0).OrderBy(mesh => mesh.LodIndex).ToArray();
		if (family.Length == 0)
			return null;

		// 基准 mesh = 0 级 LOD 里 section 最多的那个，与运行时的 canonical_mesh 规则一致。
		// 这条规则必须两边一模一样：不一致就会出现"编辑器关了 A、运行时关了 B"。
		var canonical = family
			.Where(mesh => mesh.LodIndex == 0)
			.OrderByDescending(mesh => mesh.Sections.Count)
			.ThenBy(mesh => mesh.Index)
			.FirstOrDefault();
		if (canonical is null)
			return null;

		var materialBySlot = model.Materials
			.GroupBy(binding => binding.SectionId)
			.ToDictionary(group => group.Key, group => group.First().MaterialId);
		var count = canonical.Sections.Count;
		var consistent = family.All(mesh => mesh.Sections.Count == count);
		var sections = new List<WardrobeSectionMapEntry>(count);
		for (var index = 0; index < count; index++)
		{
			var section = canonical.Sections[index];
			var toggles = new List<WardrobeSectionToggle>();
			// 只收数量与基准相等的 LOD：数量不等说明节序不可对齐，按同一下标写会关错几何。
			foreach (var mesh in family.Where(mesh => mesh.Sections.Count == count))
			{
				var target = mesh.Sections[index];
				// 节记录本身不存下标；下标就是它在本 mesh 节表里的位置。
				// 这里能直接用 index，是因为上面只收节数与基准相等的 LOD——数量不等就没法按下标对齐。
				toggles.Add(new WardrobeSectionToggle(
					mesh.LodIndex,
					mesh.Index,
					index,
					Hex(target.Offset + WardrobeSectionMapDocument.ExpectedNumIndicesOffset),
					target.NumIndices));
			}

			var materialId = materialBySlot.TryGetValue(section.MaterialSlotId, out var raw) ? raw : 0UL;
			var triangles = WardrobeSectionFingerprint.TrianglesFromIndexCount(section.NumIndices);
			sections.Add(new WardrobeSectionMapEntry(
				index,
				NormalizeMaterialId(materialId),
				triangles,
				Fingerprint(materialId, triangles),
				toggles));
		}

		// unit 级语义取自 LOD0 的第一个 mesh，而不是上面那个"section 最多的"：
		// 定制信息（slot/bodyType/weight）在同一 unit 的各级 LOD 上一致，取谁都不会错，
		// 取第一个只是为了让结果与既有工具逐字相同。
		var head = model.Meshes.FirstOrDefault(mesh => mesh.LodIndex == 0) ?? model.Meshes.FirstOrDefault();
		var semantic = head?.SemanticInfo;
		return new WardrobeSectionMapUnit(
			fileId,
			semantic?.Slot ?? string.Empty,
			semantic?.PieceType ?? string.Empty,
			semantic?.BodyType ?? string.Empty,
			semantic?.Weight ?? string.Empty,
			count,
			consistent,
			sections);
	}

	/// <summary>材质 ID 统一成 16 位小写 hex、不带 0x；解析不出来（全零）时写成 none。</summary>
	public static string NormalizeMaterialId(ulong materialId)
		=> materialId == 0
			? WardrobeSectionFingerprint.MissingMaterial
			: materialId.ToString("x16", CultureInfo.InvariantCulture);

	/// <summary>指纹一律走 <see cref="WardrobeSectionFingerprint"/>，绝不在这里另拼一套。</summary>
	public static string Fingerprint(ulong materialId, uint triangles)
		=> materialId == 0
			? WardrobeSectionFingerprint.FormatWithoutMaterial(triangles)
			: WardrobeSectionFingerprint.Format(materialId, triangles);

	private static string Hex(uint value) => "0x" + value.ToString("x8", CultureInfo.InvariantCulture);

	private static async ValueTask<string> Sha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	// --- 读写 ---------------------------------------------------------------

	public static string Serialize(WardrobeSectionMapDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		return JsonSerializer.Serialize(document, JsonOptions) + "\n";
	}

	/// <summary>解析并校验布局常量与格式版本。任何一项不符都抛，不猜。</summary>
	public static WardrobeSectionMapDocument Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"sections.json 超过 {MaximumBytes} 字节上限。");

		WardrobeSectionMapDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<WardrobeSectionMapDocument>(text, JsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"sections.json 不是合法的 JSON：{exception.Message}", exception);
		}

		if (document is null)
			throw new InvalidDataException("sections.json 内容为空。");
		if (!string.Equals(document.Schema, WardrobeSectionMapDocument.CurrentSchema, StringComparison.Ordinal))
			throw new InvalidDataException(
				$"sections.json 的 schema 是 '{document.Schema}'，本程序只认 '{WardrobeSectionMapDocument.CurrentSchema}'；请用当前版本重新生成。");
		if (document.SectionRecordSize != WardrobeSectionMapDocument.ExpectedSectionRecordSize)
			throw new InvalidDataException(
				$"sections.json 的 sectionRecordSize 是 {document.SectionRecordSize}，运行时按 {WardrobeSectionMapDocument.ExpectedSectionRecordSize} 字节解析；拒绝使用。");
		if (document.NumIndicesOffsetInSection != WardrobeSectionMapDocument.ExpectedNumIndicesOffset)
			throw new InvalidDataException(
				$"sections.json 的 numIndicesOffsetInSection 是 {document.NumIndicesOffsetInSection}，运行时按 {WardrobeSectionMapDocument.ExpectedNumIndicesOffset} 写入；拒绝使用。");
		if (!string.Equals(document.Offsets, WardrobeSectionMapDocument.TocRelativeOffsets, StringComparison.Ordinal))
			throw new InvalidDataException(
				$"sections.json 的 offsets 是 '{document.Offsets}'，本程序只认 '{WardrobeSectionMapDocument.TocRelativeOffsets}'。");
		if (document.Patch is null || string.IsNullOrWhiteSpace(document.Patch.Sha256))
			throw new InvalidDataException("sections.json 缺少 patch.sha256：无法确认它对应哪份构建产物。");
		if (document.Units is null)
			throw new InvalidDataException("sections.json 缺少 units。");
		return document;
	}

	public static async ValueTask<WardrobeSectionMapDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var fullPath = Path.GetFullPath(path);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException($"找不到 section 映射表：{fullPath}", fullPath);
		var text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
		return Parse(text);
	}

	/// <summary>原子写：先写同目录临时文件再替换，避免编辑器读到半份表。</summary>
	public static async ValueTask WriteAsync(string path, WardrobeSectionMapDocument document, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(document);
		var text = Serialize(document);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"sections.json 超过 {MaximumBytes} 字节上限：{path}");
		var fullPath = Path.GetFullPath(path);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
				.ConfigureAwait(false);
			File.Move(temporaryPath, fullPath, overwrite: true);
		}
		finally
		{
			// 失败时不要留下临时文件：它们会被下次扫描当成垃圾，也会让目录变脏。
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}
}
