using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli;

/// <summary>
/// A direct, no-cache inspection of one registered Mod's physical Patch files.
/// It intentionally constructs <see cref="PatchGroupAnalyzer"/> itself rather
/// than using ModInformationCenter, ModInformationReader, or SQLite-backed
/// facts. The command is therefore safe for Agents to use as a preflight.
/// </summary>
public sealed record CliModAnalysisDocument(
	string Schema,
	int Version,
	DateTimeOffset AnalyzedUtc,
	CliModAnalysisSource Source,
	IReadOnlyList<CliModAnalysisPatchGroup> PatchGroups,
	IReadOnlyList<CliModAnalysisDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.mod-analysis";
	public const int CurrentVersion = 1;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking)
		&& PatchGroups.All(group => !group.Diagnostics.Any(diagnostic => diagnostic.Blocking));
}

public sealed record CliModAnalysisSource(
	string NodeId,
	string Name,
	string Kind,
	string RelativePath,
	string Directory,
	int RegisteredPatchGroupCount);

public sealed record CliModAnalysisPatchFile(
	string FileName,
	bool Exists,
	bool IsReparsePoint,
	long Length,
	DateTimeOffset? LastWriteUtc);

public sealed record CliModAnalysisPatchGroup(
	string PatchFileName,
	string ArchiveHex16,
	int PatchIndex,
	bool IsRegistered,
	CliModAnalysisPatchFile Toc,
	CliModAnalysisPatchFile Stream,
	CliModAnalysisPatchFile GpuResources,
	string AnalyzerVersion,
	string Depth,
	string ReferenceScope,
	bool IsSuccessful,
	IReadOnlyList<CliModAnalysisEntry> Entries,
	IReadOnlyList<CliModAnalysisReference> References,
	IReadOnlyList<CliModAnalysisDiagnostic> Diagnostics);

public sealed record CliModAnalysisAssetKey(string TypeId, string FileId);

public sealed record CliModAnalysisEntry(
	uint EntryIndex,
	CliModAnalysisAssetKey AssetKey,
	string TypeClass,
	uint TocDataSize,
	uint StreamSize,
	uint GpuResourceSize,
	ulong TotalPayloadSize,
	int OutboundReferenceCount,
	CliModAnalysisUnit? Unit,
	string? LuaKind);

public sealed record CliModAnalysisUnit(
	bool IsReadable,
	string? ReadError,
	CliModAnalysisAssetKey? CompositeAssetKey,
	IReadOnlyList<CliModAnalysisMesh> Meshes);

public sealed record CliModAnalysisMesh(
	int MeshInfoIndex,
	string MeshId,
	int LodIndex,
	bool IsVisual,
	bool IsTransferable,
	string SemanticName,
	string BodyType,
	string Slot,
	string PieceType,
	uint VertexCount,
	uint TriangleCount,
	uint SectionCount,
	uint VertexStride,
	IReadOnlyList<string> MaterialSlotIds,
	IReadOnlyList<string> MaterialIds);

public sealed record CliModAnalysisReference(
	CliModAnalysisAssetKey SourceAssetKey,
	CliModAnalysisAssetKey TargetAssetKey,
	string Kind,
	string PayloadRelativeOffset,
	string? SlotId,
	int? ReferenceIndex,
	int? MeshInfoIndex,
	bool IsPlaceholderMesh);

public sealed record CliModAnalysisDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? PatchFileName = null,
	uint? EntryIndex = null,
	CliModAnalysisAssetKey? AssetKey = null,
	string? Details = null);

/// <summary>
/// Read-only adapter from Adaptation's PatchGroupAnalyzer contracts into a
/// stable Agent protocol. It never constructs ModInformationCenter and makes
/// no calls to manager-owned cache, database, or derived-data services.
/// </summary>
internal static class CliModAnalysis
{
	public static async ValueTask<CliModAnalysisDocument> BuildAsync(
		CliWorkspace workspace,
		string nodeSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(nodeSelector))
			throw new CliUserException("ModAnalysisSourceRequired", "mod analyze 需要一个 Mod GUID 或唯一 GUID 前缀。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, nodeSelector);
		var sourceDirectory = ResolveNodeDirectory(workspace.LibraryRoot!, node);
		var diagnostics = new List<CliModAnalysisDiagnostic>();
		var source = new CliModAnalysisSource(
			CliProjections.FormatId(node.Id),
			node.Metadata.Name,
			node.Metadata.Kind.ToString().ToLowerInvariant(),
			node.RelativePath,
			sourceDirectory,
			node.PatchGroups.Count);

		if (!Directory.Exists(sourceDirectory))
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisSourceDirectoryMissing", "error", true,
				"该 Mod 的已登记存储目录不存在；无法读取 Patch 内容。", Details: sourceDirectory));
			return new CliModAnalysisDocument(
				CliModAnalysisDocument.CurrentSchema,
				CliModAnalysisDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				source,
				Array.Empty<CliModAnalysisPatchGroup>(),
				DistinctDiagnostics(diagnostics));
		}
		if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisSourceDirectoryReparsePoint", "error", true,
				"该 Mod 的存储目录是重解析点/链接；CLI 不会在未经确认的库边界外读取 Patch。", Details: sourceDirectory));
			return new CliModAnalysisDocument(
				CliModAnalysisDocument.CurrentSchema,
				CliModAnalysisDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				source,
				Array.Empty<CliModAnalysisPatchGroup>(),
				DistinctDiagnostics(diagnostics));
		}

		IReadOnlyList<CliModDiscoveredPatch> discovered;
		try
		{
			discovered = DiscoverBasePatches(sourceDirectory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisPatchEnumerationFailed", "error", true,
				$"无法枚举该 Mod 的 Patch 文件：{exception.Message}", Details: exception.ToString()));
			return new CliModAnalysisDocument(
				CliModAnalysisDocument.CurrentSchema,
				CliModAnalysisDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				source,
				Array.Empty<CliModAnalysisPatchGroup>(),
				DistinctDiagnostics(diagnostics));
		}

		var registered = node.PatchGroups
			.Select(group => PatchKey(group.ArchiveHex16, group.PatchIndex))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var discoveredKeys = discovered.Select(item => PatchKey(item.Info.ArchiveHex16, item.Info.PatchIndex)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var group in node.PatchGroups.Where(group => !discoveredKeys.Contains(PatchKey(group.ArchiveHex16, group.PatchIndex))))
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisRegisteredPatchMissing", "warning", false,
				$"library.json 登记的 Patch 在当前 Mod 目录中不存在：{group.ArchiveHex16}.patch_{group.PatchIndex}。"));
		}
		foreach (var patch in discovered.Where(item => !registered.Contains(PatchKey(item.Info.ArchiveHex16, item.Info.PatchIndex))))
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisUnregisteredPatch", "warning", false,
				$"当前 Mod 目录包含未登记的 Patch：{patch.Info.FullFileName}。", patch.Info.FullFileName));
		}
		if (discovered.Count == 0)
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisPatchGroupsMissing", "warning", false,
				"该 Mod 目录中未发现可解析的 .patch_n 主文件；可能是逻辑主体、纯 ReShade Mod，或其存储已不完整。"));
		}

		var analyzer = new PatchGroupAnalyzer();
		var groups = new List<CliModAnalysisPatchGroup>();
		foreach (var patch in discovered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			groups.Add(await AnalyzePatchAsync(patch, registered.Contains(PatchKey(patch.Info.ArchiveHex16, patch.Info.PatchIndex)), analyzer, cancellationToken).ConfigureAwait(false));
		}

		return new CliModAnalysisDocument(
			CliModAnalysisDocument.CurrentSchema,
			CliModAnalysisDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			source,
			groups,
			DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<CliModAnalysisPatchGroup> AnalyzePatchAsync(
		CliModDiscoveredPatch patch,
		bool isRegistered,
		PatchGroupAnalyzer analyzer,
		CancellationToken cancellationToken)
	{
		var before = CaptureFiles(patch.TocPath);
		var diagnostics = new List<CliModAnalysisDiagnostic>();
		PatchGroupAnalysis? analysis = null;
		try
		{
			analysis = await analyzer.AnalyzeAsync(
				new PatchGroupInput(patch.TocPath, patch.TocPath + ".stream", patch.TocPath + ".gpu_resources"),
				cancellationToken).ConfigureAwait(false);
			diagnostics.AddRange(analysis.Issues.Select(issue => CreateDiagnostic(issue, patch.Info.FullFileName, analysis)));
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or EndOfStreamException
			or OverflowException)
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisPatchReadFailed", "error", true,
				$"无法分析 Patch：{exception.Message}", patch.Info.FullFileName, Details: exception.ToString()));
		}

		var after = CaptureFiles(patch.TocPath);
		if (!Equals(before, after))
		{
			diagnostics.Add(new CliModAnalysisDiagnostic(
				"ModAnalysisSourceChangedDuringAnalysis", "error", true,
				"Patch 或其 sidecar 在分析期间发生变化；返回结果可能不完整，请等待写入完成后重试。", patch.Info.FullFileName));
		}

		var entries = analysis is null ? Array.Empty<CliModAnalysisEntry>() : CreateEntries(analysis);
		var references = analysis is null ? Array.Empty<CliModAnalysisReference>() : CreateReferences(analysis);
		return new CliModAnalysisPatchGroup(
			patch.Info.FullFileName,
			patch.Info.ArchiveHex16,
			patch.Info.PatchIndex,
			isRegistered,
			after.Toc,
			after.Stream,
			after.GpuResources,
			analysis?.AnalyzerVersion ?? "unavailable",
			analysis?.Depth.ToString().ToLowerInvariant() ?? "full",
			"asset-key",
			analysis?.IsSuccessful == true,
			entries,
			references,
			DistinctDiagnostics(diagnostics));
	}

	private static IReadOnlyList<CliModAnalysisEntry> CreateEntries(PatchGroupAnalysis analysis)
	{
		var factsByKey = analysis.Assets
			.GroupBy(asset => asset.AssetKey)
			.ToDictionary(group => group.Key, group => group.First());
		var unitsByEntryIndex = analysis.PreparedSourceUnits
			.GroupBy(unit => unit.Entry.EntryIndex)
			.ToDictionary(group => group.Key, group => group.First());
		var referencesBySource = analysis.References
			.GroupBy(reference => reference.SourceAssetKey)
			.ToDictionary(group => group.Key, group => group.Count());
		return analysis.Entries
			.OrderBy(entry => entry.EntryIndex)
			.ThenBy(entry => entry.AssetKey.TypeId)
			.ThenBy(entry => entry.AssetKey.FileId)
			.Select(entry =>
			{
				factsByKey.TryGetValue(entry.AssetKey, out var fact);
				unitsByEntryIndex.TryGetValue(entry.EntryIndex, out var sourceUnit);
				referencesBySource.TryGetValue(entry.AssetKey, out var referenceCount);
				return new CliModAnalysisEntry(
					entry.EntryIndex,
					CreateAssetKey(entry.AssetKey),
					ResolveTypeClass(fact, entry.AssetKey),
					entry.TocDataSize,
					entry.StreamSize,
					entry.GpuResourceSize,
					checked((ulong)entry.TocDataSize + entry.StreamSize + entry.GpuResourceSize),
					referenceCount,
					sourceUnit is null ? null : CreateUnit(sourceUnit),
					fact?.Lua?.Kind);
			})
			.ToArray();
	}

	private static IReadOnlyList<CliModAnalysisReference> CreateReferences(PatchGroupAnalysis analysis)
		=> analysis.References
			.OrderBy(reference => reference.SourceAssetKey.TypeId)
			.ThenBy(reference => reference.SourceAssetKey.FileId)
			.ThenBy(reference => reference.Kind)
			.ThenBy(reference => reference.ReferenceIndex ?? int.MinValue)
			.Select(reference => new CliModAnalysisReference(
				CreateAssetKey(reference.SourceAssetKey),
				CreateAssetKey(reference.TargetAssetKey),
				ResolveReferenceKind(reference.Kind),
				$"0x{reference.PayloadRelativeOffset:x8}",
				reference.SlotId is { } slot ? $"0x{slot:x8}" : null,
				reference.ReferenceIndex,
				reference.MeshInfoIndex,
				reference.IsPlaceholderMesh))
			.ToArray();

	private static CliModAnalysisUnit CreateUnit(SourceUnitPreparation unit)
		=> new(
			unit.IsReadable,
			unit.ReadError,
			unit.CompositeAssetKey is { } composite ? CreateAssetKey(composite) : null,
			unit.Meshes
				.OrderBy(mesh => mesh.LodIndex)
				.ThenBy(mesh => mesh.MeshInfoIndex)
				.Select(mesh => new CliModAnalysisMesh(
					mesh.MeshInfoIndex,
					$"0x{mesh.MeshId:x8}",
					mesh.LodIndex,
					mesh.IsVisual,
					mesh.IsTransferable,
					mesh.SemanticName,
					mesh.BodyType,
					mesh.Slot,
					mesh.PieceType,
					mesh.VertexCount,
					mesh.TriangleCount,
					mesh.SectionCount,
					mesh.VertexStride,
					mesh.MaterialSlotIds.Select(id => $"0x{id:x8}").ToArray(),
					mesh.MaterialIds.Select(id => $"0x{id:x16}").ToArray()))
				.ToArray());

	private static CliModAnalysisDiagnostic CreateDiagnostic(
		PatchAnalysisIssue issue,
		string patchFileName,
		PatchGroupAnalysis analysis)
	{
		var blocking = issue.Code is "MissingToc" or "InvalidToc";
		var entry = issue.AssetKey is { } assetKey
			? analysis.Entries.FirstOrDefault(candidate => candidate.AssetKey == assetKey)
			: null;
		return new CliModAnalysisDiagnostic(
			issue.Code,
			blocking ? "error" : "warning",
			blocking,
			issue.Message,
			patchFileName,
			entry?.EntryIndex,
			issue.AssetKey is { } key ? CreateAssetKey(key) : null,
			issue.SourceFilePath);
	}

	private static CliModAnalysisAssetKey CreateAssetKey(AdaptationAssetKey key)
		=> new($"0x{key.TypeId:x16}", $"0x{key.FileId:x16}");

	private static string ResolveTypeClass(PatchAssetFact? fact, AdaptationAssetKey key)
	{
		if (fact?.IsLua == true) return "lua";
		if (fact?.IsUnit == true) return "unit";
		if (fact?.IsCompositeUnit == true) return "composite-unit";
		if (fact?.IsMaterial == true) return "material";
		if (fact?.IsTexture == true) return "texture";
		return "other";
	}

	private static string ResolveReferenceKind(PatchReferenceKind kind)
		=> kind switch
		{
			PatchReferenceKind.UnitMaterial => "unitMaterial",
			PatchReferenceKind.MaterialTexture => "materialTexture",
			_ => kind.ToString(),
		};

	private static IReadOnlyList<CliModDiscoveredPatch> DiscoverBasePatches(string sourceDirectory)
	{
		var parser = CoreServices.CreatePatchFileNameParser();
		var result = new List<CliModDiscoveredPatch>();
		foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			if (!parser.TryParse(Path.GetFileName(path), out var parsed)
				|| parsed is null
				|| parsed.SidecarKind != PatchSidecarKind.Base)
			{
				continue;
			}
			result.Add(new CliModDiscoveredPatch(path, parsed));
		}
		return result;
	}

	private static CliModPatchFiles CaptureFiles(string tocPath)
		=> new(
			CaptureFile(tocPath),
			CaptureFile(tocPath + ".stream"),
			CaptureFile(tocPath + ".gpu_resources"));

	private static CliModAnalysisPatchFile CaptureFile(string path)
	{
		var name = Path.GetFileName(path);
		if (!File.Exists(path)) return new CliModAnalysisPatchFile(name, false, false, 0, null);
		var info = new FileInfo(path);
		var attributes = File.GetAttributes(path);
		return new CliModAnalysisPatchFile(name, true, (attributes & FileAttributes.ReparsePoint) != 0, info.Length, info.LastWriteTimeUtc);
	}

	private static string ResolveNodeDirectory(string libraryRoot, ModNode node)
	{
		var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, node.RelativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
		{
			throw new CliUserException("ModAnalysisSourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		}
		return directory;
	}

	private static string PatchKey(string archiveHex16, int patchIndex)
		=> $"{archiveHex16}:{patchIndex}";

	private static IReadOnlyList<CliModAnalysisDiagnostic> DistinctDiagnostics(IEnumerable<CliModAnalysisDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Message}\u001f{item.PatchFileName}\u001f{item.EntryIndex}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.PatchFileName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.EntryIndex)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();

	private sealed record CliModDiscoveredPatch(string TocPath, PatchFileNameInfo Info);
	private sealed record CliModPatchFiles(
		CliModAnalysisPatchFile Toc,
		CliModAnalysisPatchFile Stream,
		CliModAnalysisPatchFile GpuResources);
}
