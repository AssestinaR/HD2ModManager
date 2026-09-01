using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.Analysis;

// Purpose: Builds a read-only Game Data archive index from canonical package TOCs without reading payloads.
public sealed class GameDataArchiveIndexer : IGameDataArchiveIndexer
{
	private const string SchemaVersion = "game-data-index-v2-stream-layouts";
	private const string ParserVersion = "package-toc-v1-unit-stream-layouts-v1";
	private readonly Func<string, IGameDataPackageResolver> resolverFactory;

	public GameDataArchiveIndexer(Func<string, IGameDataPackageResolver>? resolverFactory = null)
	{
		this.resolverFactory = resolverFactory ?? (directory => new GameDataPackageResolver(directory));
	}

	public async ValueTask<GameDataArchiveIndex> BuildAsync(GameDataArchiveInput input, IProgress<GameDataArchiveIndexProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(input);
		var directory = Path.GetFullPath(input.GameDataDirectory);
		if (!Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException($"Game Data directory was not found: {directory}");
		}

		var resolver = resolverFactory(directory);
		var packageNamesWereExplicit = input.PackageNames is not null;
		var packageNames = input.PackageNames?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
			?? (await resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false)).ToArray();
		var archives = new List<GameDataArchiveFact>();
		var streamLayouts = new List<GameDataStreamLayoutFact>();
		var issues = new List<PatchAnalysisIssue>();
		var archiveCurrent = 0;
		foreach (var packageName in packageNames)
		{
			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report(new GameDataArchiveIndexProgress("扫描 Archive TOC", ++archiveCurrent, packageNames.Length, packageName));
			var metadata = input.MetadataByPackageName is not null && input.MetadataByPackageName.TryGetValue(packageName, out var value) ? value : null;
			try
			{
				var packageToc = await resolver.GetPackageTocAsync(packageName, cancellationToken).ConfigureAwait(false);
				if (packageToc is null)
				{
					// Installed GameData enumeration also sees non-TOC data libraries
					// such as generated_customization_armor_sets.  They are inputs for
					// specialized readers, never standalone asset archives.  Preserve a
					// missing-entry diagnostic only for a caller's explicit archive list.
					if (!packageNamesWereExplicit)
					{
						continue;
					}
					var issue = new PatchAnalysisIssue("MissingArchiveToc", $"Game Data archive TOC was not found: {packageName}", packageName);
					archives.Add(new GameDataArchiveFact(packageName, metadata?.ArchiveHex, metadata?.DisplayName, metadata?.Category, false, Array.Empty<GameDataArchiveEntryFact>(), new[] { issue }));
					issues.Add(issue);
					continue;
				}

				var entries = new PatchTocScanner().ScanEntries(packageToc.Data, packageName, packageToc.UsesSlimEntryOffset)
					.Select(entry => new GameDataArchiveEntryFact(entry.AssetKey, packageName, entry.EntryIndex, entry.TocDataOffset, entry.StreamOffset, entry.GpuResourceOffset,
						entry.TocDataSize, entry.StreamSize, entry.GpuResourceSize, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4)).ToArray();
				archives.Add(new GameDataArchiveFact(packageName, metadata?.ArchiveHex, metadata?.DisplayName, metadata?.Category, packageToc.UsesSlimEntryOffset, entries, Array.Empty<PatchAnalysisIssue>()));
			}
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException or OverflowException or UnauthorizedAccessException)
			{
				var issue = new PatchAnalysisIssue("InvalidArchiveToc", exception.Message, packageName);
				archives.Add(new GameDataArchiveFact(packageName, metadata?.ArchiveHex, metadata?.DisplayName, metadata?.Category, false, Array.Empty<GameDataArchiveEntryFact>(), new[] { issue }));
				issues.Add(issue);
			}
		}
		if (!input.IncludeStreamLayouts)
		{
			return new GameDataArchiveIndex(input with { GameDataDirectory = directory }, archives, Array.Empty<GameDataStreamLayoutFact>(), issues, DateTimeOffset.UtcNow, SchemaVersion, ParserVersion);
		}

		var entriesByPackage = archives.Where(archive => archive.IsIndexed)
			.ToDictionary(archive => archive.PackageName, archive => (IReadOnlyList<PatchTocEntry>)archive.Entries.Select(entry => new PatchTocEntry(
				entry.AssetKey, archive.PackageName, archive.PackageName, entry.TocDataOffset, entry.StreamOffset, entry.GpuResourceOffset,
				entry.Unknown1, entry.Unknown2, entry.TocDataSize, entry.StreamSize, entry.GpuResourceSize, entry.Unknown3, entry.Unknown4, entry.EntryIndex)).ToArray(), StringComparer.OrdinalIgnoreCase);
		var layoutWork = archives.Where(archive => archive.IsIndexed)
			.SelectMany(archive => archive.Entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).Select(entry => (archive.PackageName, entry.AssetKey)))
			.ToArray();
		var layoutProgress = 0;
		var layoutResults = new System.Collections.Concurrent.ConcurrentBag<GameDataStreamLayoutFact>();
		var packageScope = entriesByPackage.Keys.ToArray();
		var maxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
		await Parallel.ForEachAsync(layoutWork, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = maxParallelism }, async (item, token) =>
		{
			var workerResolver = resolverFactory(directory);
			var reader = new GameDataUnitMeshReader(workerResolver);
			reader.PrimeEntries(entriesByPackage);
			try
			{
				var unit = await reader.ReadAsync(item.PackageName, item.AssetKey, packageScope, allowGlobalDependencySearch: false, token).ConfigureAwait(false);
				foreach (var stream in unit.Model.Streams)
				{
					layoutResults.Add(new GameDataStreamLayoutFact(item.PackageName, item.AssetKey, stream.Index, stream.ComponentInfoId, unit.Model.Version, stream.VertexStride, stream.Components.Select(component => new GameDataStreamComponentFact(component.Type, component.Format, component.Index, component.Unknown, component.Size)).ToArray()));
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
			{
				// Keep the archive-level index even when a legacy Unit cannot provide ABI facts.
			}
			var current = Interlocked.Increment(ref layoutProgress);
			progress?.Report(new GameDataArchiveIndexProgress("解析 Unit Stream ABI", current, layoutWork.Length, item.PackageName));
		}).ConfigureAwait(false);
		streamLayouts.AddRange(layoutResults.OrderBy(layout => layout.PackageName, StringComparer.OrdinalIgnoreCase).ThenBy(layout => layout.UnitAssetKey.FileId).ThenBy(layout => layout.StreamIndex));

		return new GameDataArchiveIndex(input with { GameDataDirectory = directory }, archives, streamLayouts, issues, DateTimeOffset.UtcNow, SchemaVersion, ParserVersion);
	}
}
