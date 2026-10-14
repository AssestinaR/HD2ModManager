using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli;

// A file-in / file-out primitive layer for Patch payloads. Every command in
// this file is deliberately stateless: it reads explicit source paths and
// writes only into an explicit --output directory outside the Mod library.
// No library registration, no plan file, and no write lease is involved, so an
// external script can chain these commands freely to build workflows the
// desktop Manager never exposed.

internal sealed record CliPatchEntrySummary(
	string TypeId,
	string FileId,
	string Kind,
	uint EntryIndex,
	long TocDataSize,
	long StreamSize,
	long GpuResourceSize);

internal sealed record CliPatchArchiveSummary(
	string TocPath,
	string TocFileName,
	int EntryCount,
	long TocFileSize,
	long StreamFileSize,
	long GpuResourceFileSize,
	IReadOnlyList<CliPatchEntrySummary> Entries);

internal sealed record CliPatchEntryExportResult(
	string OutputDirectory,
	string KeyDirectory,
	IReadOnlyList<string> Files,
	long TocDataSize,
	long StreamSize,
	long GpuResourceSize);

internal sealed record CliPatchPrimitiveExecutionResult(
	bool Success,
	string? OutputDirectory,
	IReadOnlyList<string> WrittenFiles,
	int AffectedEntryCount,
	IReadOnlyList<string> Diagnostics);

internal static class CliPatchPrimitives
{
	internal const ulong UnitTypeId = 0xe0a48d0be9a7453f;

	private static readonly IPatchTocScanner Scanner = new PatchTocScanner();
	private static readonly IPatchEntryPayloadReader PayloadReader = new PatchEntryPayloadReader();

	// --- resolution helpers -------------------------------------------------

	public static string RequireExistingPatchToc(string path, string command)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("PatchTocPathRequired", $"{command} 需要一个 Patch TOC（<name>.patch_*）文件路径。");
		string full;
		try { full = Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("PatchTocPathInvalid", $"Patch 路径无效：{path}");
		}
		if (!File.Exists(full))
			throw new CliUserException("PatchTocNotFound", $"Patch TOC 文件不存在：{full}。本组命令作用于任意 Patch 文件路径，不要求它已登记进 Mod 库。");
		return full;
	}

	public static AdaptationAssetKey ParseAssetKey(string typeId, string fileId)
	{
		if (string.IsNullOrWhiteSpace(typeId) || string.IsNullOrWhiteSpace(fileId))
			throw new CliUserException("PatchAssetKeyRequired", "请同时提供十六进制 TypeID 与 FileID，例如 0xe0a48d0be9a7453f 0x0123456789abcdef。");
		return new AdaptationAssetKey(ParseHex64(typeId, "TypeID"), ParseHex64(fileId, "FileID"));
	}

	/// <summary>
	/// Resolves a --keys argument into AssetKeys. Accepts either inline
	/// "0xTYPEID:0xFILEID,0xTYPEID:0xFILEID" text, an "@file.json" indirection
	/// pointing at a JSON array, or the byproduct of "gamedata equipment list"
	/// (an object with a "keys" array). This keeps scripts able to pipe one
	/// command's JSON straight into the next.
	/// </summary>
	public static IReadOnlyList<AdaptationAssetKey> ResolveKeys(string? raw, string command)
	{
		if (string.IsNullOrWhiteSpace(raw))
			throw new CliUserException("PatchKeysRequired", $"{command} 需要 --keys，可传内联 \"0xTYPEID:0xFILEID,...\"、@keys.json 或 equipment list 的输出。");
		var text = raw.Trim();
		if (text.StartsWith("@", StringComparison.Ordinal))
		{
			var path = text[1..];
			if (!File.Exists(path))
				throw new CliUserException("PatchKeysFileNotFound", $"未找到 --keys 引用的文件：{path}");
			text = File.ReadAllText(path);
		}
		if (text.TrimStart().StartsWith("[", StringComparison.Ordinal) || text.TrimStart().StartsWith("{", StringComparison.Ordinal))
			return ParseKeysJson(text, command);
		return ParseKeysInline(text, command);
	}

	private static IReadOnlyList<AdaptationAssetKey> ParseKeysJson(string json, string command)
	{
		var keys = new List<AdaptationAssetKey>();
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(json);
			var root = document.RootElement;
			System.Text.Json.JsonElement array;
			if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				array = root;
			}
			else if (root.ValueKind == System.Text.Json.JsonValueKind.Object && TryFindKeysArray(root, out var nested))
			{
				// Accept a nested envelope too, e.g. the raw output of
				// "gamedata equipment list" ({ ok, equipment: { keys: [...] } }).
				array = nested;
			}
			else
			{
				throw new CliUserException("PatchKeysInvalid", $"{command} 的 --keys JSON 必须是数组，或含 \"keys\" 数组的对象（可直接使用 gamedata equipment list 的输出）。");
			}

			foreach (var element in array.EnumerateArray())
			{
				switch (element.ValueKind)
				{
					case System.Text.Json.JsonValueKind.String:
						keys.Add(ParseKeyToken(element.GetString()!, command));
						break;
					case System.Text.Json.JsonValueKind.Object:
						var typeId = element.TryGetProperty("typeId", out var t) ? t.GetString() : null;
						var fileId = element.TryGetProperty("fileId", out var f) ? f.GetString() : null;
						keys.Add(ParseAssetKey(typeId ?? string.Empty, fileId ?? string.Empty));
						break;
					default:
						throw new CliUserException("PatchKeysInvalid", $"{command} 的 --keys 数组元素必须是字符串或含 typeId/fileId 的对象。");
				}
			}
		}
		catch (System.Text.Json.JsonException exception)
		{
			throw new CliUserException("PatchKeysInvalid", $"{command} 的 --keys JSON 无法解析：{exception.Message}");
		}
		if (keys.Count == 0) throw new CliUserException("PatchKeysEmpty", $"{command} 的 --keys 未解析出任何条目。");
		return keys;
	}

	private static bool TryFindKeysArray(System.Text.Json.JsonElement element, out System.Text.Json.JsonElement array)
	{
		// Breadth-first search for the first "keys" array so a script can feed
		// any command's JSON envelope in directly, however deeply nested.
		var queue = new Queue<System.Text.Json.JsonElement>();
		queue.Enqueue(element);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (current.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
			if (current.TryGetProperty("keys", out var keys) && keys.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				array = keys;
				return true;
			}
			foreach (var property in current.EnumerateObject())
				if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
					queue.Enqueue(property.Value);
		}
		array = default;
		return false;
	}

	private static IReadOnlyList<AdaptationAssetKey> ParseKeysInline(string text, string command)
	{
		var keys = text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => ParseKeyToken(token, command))
			.ToArray();
		if (keys.Length == 0) throw new CliUserException("PatchKeysEmpty", $"{command} 的 --keys 未解析出任何条目。");
		return keys;
	}

	private static AdaptationAssetKey ParseKeyToken(string token, string command)
	{
		var parts = token.Split(new[] { ':', '/', '|' }, StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
			throw new CliUserException("PatchKeysInvalid", $"{command} 的 --keys 条目必须形如 0xTYPEID:0xFILEID，当前值：{token}");
		return ParseAssetKey(parts[0], parts[1]);
	}

	private static ulong ParseHex64(string raw, string label)
	{
		var text = raw.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
		if (text.Length == 0 || text.Length > 16 || !ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value))
			throw new CliUserException("PatchAssetKeyInvalid", $"{label} 必须是 1~16 位十六进制数（可带 0x 前缀），当前值：{raw}");
		return value;
	}

	public static string DescribeKind(ulong typeId) => typeId switch
	{
		UnitTypeId => "unit",
		PatchUnitMeshReader.BoneTypeId => "bone",
		PatchUnitMeshReader.CompositeUnitTypeId => "composite-unit",
		_ => "asset",
	};

	// --- read side ----------------------------------------------------------

	public static async ValueTask<CliPatchArchiveSummary> DescribeAsync(string tocPath, bool withEntries, CancellationToken cancellationToken = default)
	{
		var full = RequireExistingPatchToc(tocPath, "patch entries list");
		var entries = await Scanner.ScanEntriesAsync(full, cancellationToken).ConfigureAwait(false);
		var summaries = withEntries
			? entries.Select(entry => new CliPatchEntrySummary(
				$"0x{entry.AssetKey.TypeId:x16}",
				$"0x{entry.AssetKey.FileId:x16}",
				DescribeKind(entry.AssetKey.TypeId),
				entry.EntryIndex,
				entry.TocDataSize,
				entry.StreamSize,
				entry.GpuResourceSize)).ToArray()
			: Array.Empty<CliPatchEntrySummary>();
		return new CliPatchArchiveSummary(
			full,
			Path.GetFileName(full),
			entries.Count,
			new FileInfo(full).Length,
			SidecarLength(full + ".stream"),
			SidecarLength(full + ".gpu_resources"),
			summaries);
	}

	private static long SidecarLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

	public static async ValueTask<AdaptationPatchTocEntry> RequireEntryAsync(string tocPath, AdaptationAssetKey key, CancellationToken cancellationToken = default)
	{
		var full = RequireExistingPatchToc(tocPath, "patch entry");
		var entries = await Scanner.ScanEntriesAsync(full, cancellationToken).ConfigureAwait(false);
		var match = entries.FirstOrDefault(entry => entry.AssetKey == key);
		if (match is null)
			throw new CliUserException(
				"PatchEntryNotFound",
				$"Patch '{Path.GetFileName(full)}' 中不存在 0x{key.TypeId:x16}/0x{key.FileId:x16}。可先用 hd2mod patch entries list 枚举全部条目。");
		return match;
	}

	public static async ValueTask<CliPatchEntryExportResult> ExportEntryAsync(
		string tocPath,
		AdaptationAssetKey key,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		var entry = await RequireEntryAsync(tocPath, key, cancellationToken).ConfigureAwait(false);
		var payload = await PayloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);

		var root = CliOutput.NormalizeDirectoryPath(outputDirectory, "PatchEntryOutputInvalid");
		var keyDirectory = Path.Combine(root, $"0x{key.TypeId:x16}_{key.FileId:x16}");
		Directory.CreateDirectory(keyDirectory);

		var files = new List<string>();
		// Each payload range becomes its own bare file. toc/stream/gpu mirror the
		// three sidecars a Patch entry straddles, so a later script can inspect,
		// replace, or re-pack them without any library involvement.
		files.Add(await WriteSegmentAsync(keyDirectory, "toc.bin", payload.TocData, overwrite, cancellationToken).ConfigureAwait(false));
		files.Add(await WriteSegmentAsync(keyDirectory, "stream.bin", payload.StreamData, overwrite, cancellationToken).ConfigureAwait(false));
		files.Add(await WriteSegmentAsync(keyDirectory, "gpu_resources.bin", payload.GpuResourceData, overwrite, cancellationToken).ConfigureAwait(false));

		var manifest = new
		{
			ok = true,
			schema = "hd2mod.patch-entry-export/v1",
			source = Path.GetFullPath(entry.SourceFilePath),
			typeId = $"0x{key.TypeId:x16}",
			fileId = $"0x{key.FileId:x16}",
			kind = DescribeKind(key.TypeId),
			entryIndex = entry.EntryIndex,
			segments = new
			{
				toc = new { file = "toc.bin", size = payload.TocData.Length },
				stream = new { file = "stream.bin", size = payload.StreamData.Length },
				gpuResources = new { file = "gpu_resources.bin", size = payload.GpuResourceData.Length },
			},
		};
		files.Add(await WriteSegmentAsync(keyDirectory, "entry.json", System.Text.Encoding.UTF8.GetBytes(CliOutput.ToJson(manifest)), overwrite, cancellationToken).ConfigureAwait(false));

		return new CliPatchEntryExportResult(
			root, keyDirectory, files,
			payload.TocData.Length, payload.StreamData.Length, payload.GpuResourceData.Length);
	}

	private static async ValueTask<string> WriteSegmentAsync(string directory, string fileName, byte[] data, bool overwrite, CancellationToken cancellationToken)
	{
		var path = Path.Combine(directory, fileName);
		if (File.Exists(path) && !overwrite)
			throw new CliUserException("PatchEntryOutputAlreadyExists", $"输出文件已存在：{path}。如需覆盖，请显式传入 --overwrite。");
		await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
		return path;
	}

	// --- compose side -------------------------------------------------------

	/// <summary>
	/// Repacks a header template plus an arbitrary set of cross-source entries
	/// into one new Patch directory. This is the standalone form of the
	/// multi-source merge the desktop Manager performs internally when a helmet
	/// archive is folded into an armor archive.
	/// </summary>
	public static async ValueTask<CliPatchPrimitiveExecutionResult> ComposeAsync(
		string headerTemplateTocPath,
		IReadOnlyList<PatchSubsetSelection> selections,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		if (selections.Count == 0)
			throw new CliUserException("PatchComposeSelectionRequired", "patch compose 至少需要一个 --pick <patch.toc> <0xtypeid> <0xfileid> 选择项。");
		var header = RequireExistingPatchToc(headerTemplateTocPath, "patch compose");
		return await PublishAsync(
			outputDirectory,
			overwrite,
			async staged =>
			{
				var writer = new PatchSubsetWriter();
				var result = await writer.WriteSelectedAsync(header, staged, selections, overwriteExisting: true, cancellationToken).ConfigureAwait(false);
				return new[] { result.TocFilePath, result.StreamFilePath, result.GpuResourceFilePath };
			},
			selections.Count,
			cancellationToken).ConfigureAwait(false);
	}

	// --- edit side ----------------------------------------------------------

	/// <summary>
	/// Performs entry-level surgery on one existing Patch: add / remove /
	/// replace entries, or hide units in place, then publishes a complete new
	/// Patch directory. The source Patch is never modified.
	/// </summary>
	public static async ValueTask<CliPatchPrimitiveExecutionResult> EditAsync(
		string sourceTocPath,
		IReadOnlyList<AdaptationAssetKey> addKeys,
		IReadOnlyList<AdaptationAssetKey> removeKeys,
		IReadOnlyList<AdaptationAssetKey> hideKeys,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		if (addKeys.Count == 0 && removeKeys.Count == 0 && hideKeys.Count == 0)
			throw new CliUserException("PatchEditNothingToDo", "patch edit 需要至少一个 --add-key、--remove-key 或 --hide-unit 参数。");
		var source = RequireExistingPatchToc(sourceTocPath, "patch edit");
		var entries = await Scanner.ScanEntriesAsync(source, cancellationToken).ConfigureAwait(false);

		var additions = new List<PatchArchiveAdditionalEntry>();
		var removed = new List<AdaptationPatchTocEntry>();
		var edits = new List<PatchUnitMeshEditResult>();
		var diagnostics = new List<string>();

		// Additions come from the same Patch by default; a key that is absent is
		// reported rather than silently dropped.
		foreach (var key in addKeys)
		{
			var entry = entries.FirstOrDefault(candidate => candidate.AssetKey == key)
				?? throw new CliUserException("PatchEditAddMissing", $"patch edit --add-key 找不到条目 0x{key.TypeId:x16}/0x{key.FileId:x16}，且本次未提供外部来源。");
			var payload = await PayloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			additions.Add(new PatchArchiveAdditionalEntry(entry.AssetKey, payload.TocData, payload.StreamData, payload.GpuResourceData, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
		}

		foreach (var key in removeKeys)
		{
			var entry = entries.FirstOrDefault(candidate => candidate.AssetKey == key)
				?? throw new CliUserException("PatchEditRemoveMissing", $"patch edit --remove-key 找不到条目 0x{key.TypeId:x16}/0x{key.FileId:x16}。");
			removed.Add(entry);
		}

		foreach (var key in hideKeys)
		{
			var entry = entries.FirstOrDefault(candidate => candidate.AssetKey == key)
				?? throw new CliUserException("PatchEditHideMissing", $"patch edit --hide-unit 找不到条目 0x{key.TypeId:x16}/0x{key.FileId:x16}。");
			var hidden = await HideUnitCoreAsync(entry, entries, cancellationToken).ConfigureAwait(false);
			if (hidden.Edit is null)
			{
				diagnostics.AddRange(hidden.Diagnostics.Select(diagnostic => $"0x{key.FileId:x16}: {diagnostic}"));
				continue;
			}
			edits.Add(hidden.Edit);
		}

		if (edits.Count == 0 && additions.Count == 0 && removed.Count == 0)
			throw new CliUserException("PatchEditProducedNothing", $"patch edit 未产生任何可写条目：{string.Join("；", diagnostics)}");

		return await PublishAsync(
			outputDirectory,
			overwrite,
			async staged =>
			{
				var writer = new PatchArchiveWriter();
				var result = await writer.WriteAsync(
					source, staged,
					edits,
					additions,
					removed,
					overwriteExisting: true,
					preserveOriginalStream: false,
					headerTemplateTocData: null,
					cancellationToken).ConfigureAwait(false);
				return new[] { result.TocFilePath, result.StreamFilePath, result.GpuResourceFilePath };
			},
			edits.Count + additions.Count + removed.Count,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Generates a fresh hidden-Unit Patch from an arbitrary source Patch. The
	/// source is usually the installed vanilla Archive extraction; every
	/// requested Unit is minified to an invisible placeholder while its bone,
	/// material and stream declarations are preserved.
	/// </summary>
	public static async ValueTask<CliPatchPrimitiveExecutionResult> HideUnitsAsync(
		string sourceTocPath,
		IReadOnlyList<AdaptationAssetKey> keys,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		if (keys.Count == 0)
			throw new CliUserException("PatchHideUnitSelectionRequired", "patch hide-unit 至少需要一个 Unit AssetKey。");
		var source = RequireExistingPatchToc(sourceTocPath, "patch hide-unit");
		var entries = await Scanner.ScanEntriesAsync(source, cancellationToken).ConfigureAwait(false);

		var edits = new List<PatchUnitMeshEditResult>();
		var diagnostics = new List<string>();
		foreach (var key in keys)
		{
			var entry = entries.FirstOrDefault(candidate => candidate.AssetKey == key)
				?? throw new CliUserException("PatchHideUnitMissing", $"patch hide-unit 在 '{Path.GetFileName(source)}' 中找不到 Unit 条目 0x{key.TypeId:x16}/0x{key.FileId:x16}。");
			var hidden = await HideUnitCoreAsync(entry, entries, cancellationToken).ConfigureAwait(false);
			if (hidden.Edit is null)
			{
				diagnostics.AddRange(hidden.Diagnostics.Select(diagnostic => $"0x{key.FileId:x16}: {diagnostic}"));
				continue;
			}
			edits.Add(hidden.Edit);
		}

		if (edits.Count == 0)
			throw new CliUserException("PatchHideUnitProducedNothing", $"patch hide-unit 未生成任何隐藏 Unit：{string.Join("；", diagnostics)}");

		// A hidden-Unit Patch owns only the hidden Units, so it starts from an
		// empty selection over the same header and drops every other payload.
		return await PublishAsync(
			outputDirectory,
			overwrite,
			async staged =>
			{
				var writer = new PatchArchiveWriter();
				var result = await writer.WriteAsync(
					source, staged,
					edits,
					additionalEntries: null,
					removedEntries: entries.Where(entry => edits.All(edit => edit.Entry.AssetKey != entry.AssetKey)).ToArray(),
					overwriteExisting: true,
					preserveOriginalStream: false,
					headerTemplateTocData: null,
					cancellationToken).ConfigureAwait(false);
				return new[] { result.TocFilePath, result.StreamFilePath, result.GpuResourceFilePath };
			},
			edits.Count,
			cancellationToken).ConfigureAwait(false);
	}

	// --- shared hidden-Unit core -------------------------------------------

	internal sealed record HideUnitOutcome(PatchUnitMeshEditResult? Edit, IReadOnlyList<string> Diagnostics);

	/// <summary>
	/// The pure, reusable hidden-Unit transform: read one Unit entry, minify
	/// every visible mesh, rebuild bone palettes, the stream contract, and the
	/// final TocData/GPU payload. Exposed internally so scripts and higher-level
	/// commands share exactly one implementation.
	/// </summary>
	internal static async ValueTask<HideUnitOutcome> HideUnitCoreAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry> patchEntries,
		CancellationToken cancellationToken)
	{
		var diagnostics = new List<string>();
		if (entry.AssetKey.TypeId != UnitTypeId)
		{
			diagnostics.Add($"条目类型 0x{entry.AssetKey.TypeId:x16} 不是 Unit，已跳过。");
			return new HideUnitOutcome(null, diagnostics);
		}

		PatchUnitMesh unit;
		try
		{
			var reader = new PatchUnitMeshReader();
			unit = await reader.ReadAsync(entry, patchEntries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or KeyNotFoundException or FileNotFoundException)
		{
			diagnostics.Add($"读取 Unit 网格失败：{exception.Message}");
			return new HideUnitOutcome(null, diagnostics);
		}

		var generator = new HiddenUnitGenerator();
		var preparation = new CanonicalMeshPreparation();
		var paletteCompiler = new CanonicalLodBonePaletteCompiler();
		var streamCompiler = new CanonicalStreamContractCompiler();

		var finalMeshes = new List<UnitRawMeshData>(unit.Model.Meshes.Count);
		foreach (var mesh in unit.Model.Meshes)
		{
			var raw = unit.Model.RawMeshData.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
			var stream = unit.Model.Streams.FirstOrDefault(candidate => candidate.Index == (int)mesh.StreamIndex);
			if (raw is null || stream is null)
			{
				diagnostics.Add($"MeshInfo {mesh.Index} 的 RawMesh/stream 载荷不完整，无法重建。");
				return new HideUnitOutcome(null, diagnostics);
			}

			// Culling/proxy meshes carry no visible geometry; preserve them so the
			// unit keeps a valid LOD -1 contract. Only visible LODs are minified.
			if (raw.LodIndex == -1)
			{
				finalMeshes.Add(raw);
				continue;
			}

			var tiny = generator.Generate(raw, stream);
			if (!tiny.IsValid || tiny.Mesh is null)
			{
				diagnostics.AddRange(tiny.Diagnostics.Select(diagnostic => diagnostic.Message));
				return new HideUnitOutcome(null, diagnostics);
			}
			finalMeshes.Add(tiny.Mesh);
		}

		var compiledLayout = new CanonicalUnitMaterialLayoutCompiler().TryCompile(unit.Model, finalMeshes, Array.Empty<CanonicalMaterialSectionProvenance>());
		if (!compiledLayout.IsValid)
		{
			diagnostics.AddRange(compiledLayout.Diagnostics.Select(diagnostic => diagnostic.Message));
			return new HideUnitOutcome(null, diagnostics);
		}
		finalMeshes = compiledLayout.Meshes.ToList();

		var streamsResult = streamCompiler.TryCompile(unit.Model, finalMeshes);
		if (!streamsResult.IsValid)
		{
			diagnostics.AddRange(streamsResult.Diagnostics.Select(diagnostic => diagnostic.Message));
			return new HideUnitOutcome(null, diagnostics);
		}
		var targetModel = unit.Model with { Streams = streamsResult.Streams };

		var preparedMeshes = new List<UnitRawMeshData>(finalMeshes.Count);
		foreach (var raw in finalMeshes)
		{
			var stream = targetModel.Streams.Single(candidate => candidate.Index == (int)raw.StreamIndex);
			var prepared = preparation.TryPrepare(raw, stream);
			if (!prepared.IsValid || prepared.Mesh is null)
			{
				diagnostics.AddRange(prepared.Diagnostics.Select(diagnostic => diagnostic.Message));
				return new HideUnitOutcome(null, diagnostics);
			}
			preparedMeshes.Add(prepared.Mesh);
		}

		var rebuiltBones = targetModel.BoneInfos
			.Select((bone, index) => new CanonicalBoneInfoRebuild(index, bone))
			.ToArray();

		var rebuilder = new CanonicalUnitRebuilder();
		var rebuilt = rebuilder.TryRebuild(targetModel, unit.Payload.TocData, preparedMeshes, rebuiltBones, compiledLayout.Bindings);
		if (!rebuilt.IsValid || rebuilt.Output is null)
		{
			diagnostics.AddRange(rebuilt.Diagnostics.Select(diagnostic => diagnostic.Message));
			return new HideUnitOutcome(null, diagnostics);
		}

		var edit = new PatchUnitMeshEditResult(
			entry,
			unit.Payload,
			rebuilt.Output.TocData,
			rebuilt.Output.GpuData,
			CompositeAssetKey: null,
			CompositeTocData: null,
			CompositeGpuResourceData: null,
			ReplacementMaterialIds: null);
		return new HideUnitOutcome(edit, diagnostics);
	}

	// --- output publication -------------------------------------------------

	private static async ValueTask<CliPatchPrimitiveExecutionResult> PublishAsync(
		string outputDirectory,
		bool overwrite,
		Func<string, ValueTask<string[]>> writeInto,
		int affectedEntryCount,
		CancellationToken cancellationToken)
	{
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "PatchPrimitiveOutputInvalid");
		if ((File.Exists(target) || Directory.Exists(target)) && !overwrite)
		{
			throw new CliUserException(
				"PatchPrimitiveOutputAlreadyExists",
				$"输出目录已存在：{target}。本组底座命令只写新目录，如需覆盖请显式传入 --overwrite。 ");
		}

		var parent = Path.GetDirectoryName(target)
			?? throw new CliUserException("PatchPrimitiveOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-patch-primitive-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(staged);
			var written = await writeInto(staged).ConfigureAwait(false);
			if (Directory.Exists(target) || File.Exists(target))
			{
				if (!overwrite)
					throw new CliUserException("PatchPrimitiveOutputAlreadyExists", $"输出目录在生成过程中已出现：{target}。CLI 未发布本次结果。 ");
				Directory.Delete(target, recursive: true);
			}
			Directory.Move(staged, target);
			var files = written
				.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
				.Select(path => Path.Combine(target, Path.GetFileName(path)))
				.ToArray();
			return new CliPatchPrimitiveExecutionResult(true, target, files, affectedEntryCount, Array.Empty<string>());
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DirectoryNotFoundException
			or FileNotFoundException
			or OverflowException)
		{
			throw new CliUserException("PatchPrimitiveWriteFailed", $"Patch 底座写入未完成：{exception.Message}");
		}
		finally
		{
			if (Directory.Exists(staged))
			{
				try { Directory.Delete(staged, recursive: true); }
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
			}
		}
	}
}
