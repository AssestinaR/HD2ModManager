using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Unifies top-level patch discovery, sidecar grouping and Adaptation-owned AssetKey analysis into one content snapshot.
public sealed class ModContentFactsService : IAssetInventoryProducer, IAssetInventoryGenerationProvider
{
	private readonly IPatchFileNameParser _fileNameParser;
	private readonly IPatchGroupAnalysisProvider _analysisProvider;
	private readonly IExternalInstallFileFactsService _externalInstallFactsService;

	public ModContentFactsService(
		IPatchFileNameParser fileNameParser,
		IPatchGroupAnalysisProvider analysisProvider,
		IUnitVersionProbe? unitVersionProbe = null,
		IExternalInstallFileFactsService? externalInstallFactsService = null)
	{
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
		_analysisProvider = analysisProvider ?? throw new ArgumentNullException(nameof(analysisProvider));
		_externalInstallFactsService = externalInstallFactsService ?? new ExternalInstallFileFactsService();
	}

	public async ValueTask<ModContentFacts> GetNodeFactsAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);

		var directory = Path.GetFullPath(Path.Combine(modsRootDirectory, node.RelativePath));
		var issues = new List<CoreIssue>();
		if (!Directory.Exists(directory))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ModDirectoryMissing", $"Mod directory does not exist: {directory}", directory, node.Id));
			var missingBuiltUtc = DateTimeOffset.UtcNow;
			var missingGeneration = ComputeGeneration(Array.Empty<ModPatchGroupFileFact>());
			return new ModContentFacts(node.Id, node.RelativePath, missingGeneration, missingBuiltUtc, Array.Empty<ModPatchGroupFact>(), issues, ModAssetStructureFacts.Empty(missingGeneration, missingBuiltUtc) with { Issues = issues.ToArray() });
		}

		var discovered = DiscoverGroups(node, directory, issues, cancellationToken);
		IReadOnlyList<HD2ModAdaptation.Analysis.PatchGroupAnalysis> analyses;
		try
		{
			analyses = await _analysisProvider.AnalyzeNodeAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			analyses = Array.Empty<HD2ModAdaptation.Analysis.PatchGroupAnalysis>();
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "PatchAnalysisFailed", exception.Message, directory, node.Id, exception.ToString()));
		}

		var analysisByBasePath = analyses
			.GroupBy(analysis => Path.GetFullPath(analysis.Input.PatchTocFilePath), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
		var groups = new List<ModPatchGroupFact>();
		var analysesByGroup = new Dictionary<ModPatchGroupId, HD2ModAdaptation.Analysis.PatchGroupAnalysis>();
		foreach (var discoveredGroup in discovered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var groupIssues = new List<CoreIssue>(discoveredGroup.Issues);
			var assetKeys = new HashSet<AssetKey>();
			HD2ModAdaptation.Analysis.PatchGroupAnalysis? analysis = null;
			var baseFile = discoveredGroup.Files.FirstOrDefault(file => file.SidecarKind == PatchSidecarKind.Base);
			if (baseFile is not null && analysisByBasePath.TryGetValue(Path.GetFullPath(baseFile.FilePath), out var resolvedAnalysis))
			{
				analysis = resolvedAnalysis;
				analysesByGroup[discoveredGroup.Id] = analysis;
				foreach (var asset in analysis.Assets)
				{
					assetKeys.Add(new AssetKey(asset.AssetKey.TypeId, asset.AssetKey.FileId));
				}
				foreach (var issue in analysis.Issues)
				{
					groupIssues.Add(new CoreIssue(
						IsFatalAnalysisIssue(issue.Code) ? CoreIssueSeverity.Error : CoreIssueSeverity.Warning,
						issue.Code,
						issue.Message,
						issue.SourceFilePath ?? baseFile.FilePath,
						node.Id));
				}
			}
			else if (baseFile is not null)
			{
				groupIssues.Add(new CoreIssue(CoreIssueSeverity.Error, "PatchAnalysisMissing", "No Adaptation analysis was produced for the base patch.", baseFile.FilePath, node.Id));
			}

			issues.AddRange(groupIssues);
			groups.Add(new ModPatchGroupFact(discoveredGroup.Id, discoveredGroup.NormalizedOrder, discoveredGroup.Files, assetKeys, groupIssues));
		}

		var externalFacts = await _externalInstallFactsService.DetectAsync(directory, cancellationToken).ConfigureAwait(false);
		var allFiles = groups.SelectMany(group => group.Files).ToList();
		var contentGeneration = ComputeGeneration(allFiles, externalFacts.Files);
		var builtUtc = DateTimeOffset.UtcNow;
		foreach (var conflict in externalFacts.Conflicts)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ExternalInstallTargetConflict",
				$"Multiple external files target the same game path: {conflict.ConflictKey}",
				directory,
				node.Id));
		}
		return new ModContentFacts(
			node.Id,
			node.RelativePath,
			contentGeneration,
			builtUtc,
			groups,
			issues,
			BuildAssetStructure(contentGeneration, builtUtc, groups, analysesByGroup, issues),
			externalFacts.Files);
	}

	// Projects the analyzer's compact TOC facts into a stable, de-duplicated
	// structure. The logical AssetKey is stored once; every Patch-group occurrence
	// remains addressable so reused assets and multi-patch Mods are lossless.
	private static ModAssetStructureFacts BuildAssetStructure(
		string contentGeneration,
		DateTimeOffset builtUtc,
		IReadOnlyList<ModPatchGroupFact> groups,
		IReadOnlyDictionary<ModPatchGroupId, HD2ModAdaptation.Analysis.PatchGroupAnalysis> analysesByGroup,
		IReadOnlyList<CoreIssue> issues)
	{
		var occurrencesByKey = new Dictionary<AssetKey, List<ModAssetOccurrenceFact>>();
		var flagsByKey = new Dictionary<AssetKey, (bool Unit, bool Composite, bool Material, bool Texture, HD2ModAdaptation.Analysis.PatchLuaAssetFact? Lua)>();
		var references = new List<ModAssetStructureReferenceFact>();

		foreach (var group in groups)
		{
			if (!analysesByGroup.TryGetValue(group.Id, out var analysis)) continue;

			var entriesByKey = analysis.Entries
				.GroupBy(entry => entry.AssetKey)
				.ToDictionary(entry => entry.Key, entry => entry.First());
			foreach (var asset in analysis.Assets)
			{
				var key = new AssetKey(asset.AssetKey.TypeId, asset.AssetKey.FileId);
				entriesByKey.TryGetValue(asset.AssetKey, out var entry);
				var sourcePath = string.IsNullOrWhiteSpace(asset.SourceFilePath)
					? entry?.SourceFilePath ?? string.Empty
					: asset.SourceFilePath;
				var sourceName = entry?.SourceFileName
					?? (string.IsNullOrWhiteSpace(sourcePath) ? group.Id.ToString() : Path.GetFileName(sourcePath));
				var occurrence = new ModAssetOccurrenceFact(
					group.Id,
					sourcePath,
					sourceName,
					entry?.EntryIndex ?? 0,
					entry?.TocDataOffset ?? 0,
					entry?.StreamOffset ?? 0,
					entry?.GpuResourceOffset ?? 0,
					entry?.Unknown1 ?? 0,
					entry?.Unknown2 ?? 0,
					asset.TocDataSize,
					asset.StreamSize,
					asset.GpuResourceSize,
					entry?.Unknown3 ?? 0,
					entry?.Unknown4 ?? 0);

				if (!occurrencesByKey.TryGetValue(key, out var occurrences))
					occurrencesByKey[key] = occurrences = [];
				occurrences.Add(occurrence);
				var previous = flagsByKey.GetValueOrDefault(key);
				flagsByKey[key] = (
					previous.Unit || asset.IsUnit,
					previous.Composite || asset.IsCompositeUnit,
					previous.Material || asset.IsMaterial,
					previous.Texture || asset.IsTexture,
					previous.Lua ?? asset.Lua);
			}

			foreach (var reference in analysis.References)
			{
				references.Add(new ModAssetStructureReferenceFact(
					group.Id,
					new AssetKey(reference.SourceAssetKey.TypeId, reference.SourceAssetKey.FileId),
					new AssetKey(reference.TargetAssetKey.TypeId, reference.TargetAssetKey.FileId),
					reference.Kind,
					reference.PayloadRelativeOffset,
					reference.SlotId,
					reference.ReferenceIndex,
					reference.MeshInfoIndex,
					reference.IsPlaceholderMesh));
			}
		}

		var assets = occurrencesByKey
			.OrderBy(pair => pair.Key.TypeId)
			.ThenBy(pair => pair.Key.FileId)
			.Select(pair =>
			{
				var flags = flagsByKey[pair.Key];
				return new ModAssetStructureAssetFact(pair.Key, flags.Unit, flags.Composite, flags.Material, flags.Texture, pair.Value, Lua: flags.Lua);
			})
			.ToArray();
		var sourceArchives = groups
			.GroupBy(group => group.Id.SourceArchiveHex, StringComparer.OrdinalIgnoreCase)
			.Select(group => new ModAssetStructureSourceArchiveFact(
				group.Key,
				group.Select(item => item.Id).OrderBy(item => item.SourcePatchIndex).ToArray(),
				group.SelectMany(item => item.AssetKeys).Distinct().OrderBy(key => key.TypeId).ThenBy(key => key.FileId).ToArray()))
			.OrderBy(archive => archive.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new ModAssetStructureFacts(
			contentGeneration,
			builtUtc,
			assets,
			sourceArchives,
			[],
			references,
			issues.Distinct().ToArray());
	}

	private IReadOnlyList<DiscoveredPatchGroup> DiscoverGroups(ModNode node, string directory, ICollection<CoreIssue> issues, CancellationToken cancellationToken)
	{
		var parsed = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Select(path => (Path: path, Info: TryParse(path)))
			.Where(item => item.Info is not null)
			.Select(item => (item.Path, Info: item.Info!))
			.ToList();
		var result = new List<DiscoveredPatchGroup>();
		foreach (var archiveGroup in parsed.GroupBy(item => item.Info.ArchiveHex16, StringComparer.OrdinalIgnoreCase))
		{
			var sourceIndexes = archiveGroup.Select(item => item.Info.PatchIndex).Distinct().OrderBy(index => index).ToList();
			var normalizedBySourceIndex = sourceIndexes.Select((source, normalized) => (source, normalized)).ToDictionary(item => item.source, item => item.normalized);
			foreach (var patchGroup in archiveGroup.GroupBy(item => item.Info.PatchIndex).OrderBy(group => group.Key))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var groupIssues = new List<CoreIssue>();
				var files = patchGroup
					.OrderBy(item => item.Info.SidecarKind)
					.Select(item => CreateFileFact(item.Path, item.Info.SidecarKind))
					.ToList();
				if (files.All(file => file.SidecarKind != PatchSidecarKind.Base))
				{
					var path = files.FirstOrDefault()?.FilePath;
					var issue = new CoreIssue(CoreIssueSeverity.Error, "SidecarWithoutBase", "Patch sidecar has no base patch.", path, node.Id);
					groupIssues.Add(issue);
				}
				var id = new ModPatchGroupId(node.Id, archiveGroup.Key.ToLowerInvariant(), patchGroup.Key);
				result.Add(new DiscoveredPatchGroup(id, normalizedBySourceIndex[patchGroup.Key], files, groupIssues));
			}
		}
		return result;
	}

	private PatchFileNameInfo? TryParse(string path)
		=> _fileNameParser.TryParse(Path.GetFileName(path), out var info) ? info : null;

	private static ModPatchGroupFileFact CreateFileFact(string path, PatchSidecarKind kind)
	{
		var file = new FileInfo(path);
		return new ModPatchGroupFileFact(kind, path, file.Name, file.Length, file.LastWriteTimeUtc);
	}

	private static bool IsFatalAnalysisIssue(string code)
		=> code is "InvalidToc" or "MissingToc";

	public string ComputeGeneration(ModNode node, string modsRootDirectory)
	{
		var directory = Path.Combine(modsRootDirectory, node.RelativePath);
		if (!Directory.Exists(directory)) return ComputeGeneration(Array.Empty<ModPatchGroupFileFact>());
		var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Select(path => new FileInfo(path))
			.Select(info => (_fileNameParser.TryParse(info.Name, out var parsed), info, parsed))
			.Where(item => item.Item1)
			.Select(item => new ModPatchGroupFileFact(item.parsed!.SidecarKind, item.info.FullName, item.info.Name, item.info.Length, item.info.LastWriteTimeUtc))
			.ToList();
		var external = ExternalInstallFileFactsService.EnumerateCandidateFiles(directory)
			.Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path)))
			.Where(item => ExternalInstallPathMapper.TryMap(item.Relative, out _, out _, out _))
			.Select(item =>
			{
				var info = new FileInfo(item.Path);
				return new ExternalInstallFileFact(
					item.Relative.Replace('\\', '/'),
					ExternalInstallTargetRoot.GameBin,
					ExternalInstallPathMapper.TryMap(item.Relative, out _, out _, out var target) ? target : item.Relative.Replace('\\', '/'),
					info.Length,
					string.Empty,
					LastWriteTimeUtc: info.LastWriteTimeUtc);
			})
			.ToList();
		return ComputeGeneration(files, external);
	}

	private static string ComputeGeneration(IEnumerable<ModPatchGroupFileFact> files, IEnumerable<ExternalInstallFileFact>? externalFiles = null)
	{
		var builder = new StringBuilder();
		foreach (var file in files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase))
		{
			builder.Append(file.FileName.ToLowerInvariant()).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.UtcTicks).AppendLine();
		}
		foreach (var file in externalFiles ?? Array.Empty<ExternalInstallFileFact>())
		{
			builder.Append("external:").Append(file.SourceRelativePath.ToLowerInvariant()).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.UtcTicks).AppendLine();
		}
		builder.Append("asset-facts-v3").AppendLine();
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	private sealed record DiscoveredPatchGroup(
		ModPatchGroupId Id,
		int NormalizedOrder,
		IReadOnlyList<ModPatchGroupFileFact> Files,
		IReadOnlyList<CoreIssue> Issues);
}
