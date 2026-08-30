using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// 作用：将导入源扫描为对象树后复制到程序目录 mods/，并合并进 LibrarySnapshot 再持久化保存。
// Purpose: Imports a source by scanning to an object tree, copying into app-local mods/, merging into LibrarySnapshot and persisting.
public sealed class ModLibraryImporter : IModLibraryImporter
{
	private const int SnapshotVersion = 1;
	private const string ImportTemporaryMarkerFileName = ".hd2modmanager-import-temp";
	private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

	private readonly StoragePaths _paths;
	private readonly IObjectTreeImporter _folderImporter;
	private readonly IArchiveObjectTreeImporter _archiveImporter;
	private readonly IModLibraryStore _store;
	private readonly IModInformationCenter? _informationCenter;
	private readonly IModInformationReader? _informationReader;
	private readonly IModDerivedDataCleanup? _legacyCleanup;
	private readonly PatchFileNormalizer _normalizer;
	private readonly SevenZipArchiveExtractor _archiveExtractor;
	private readonly ImportTemporaryDirectoryManager _temporaryDirectories;

	public ModLibraryImporter(
		StoragePaths paths,
		IObjectTreeImporter folderImporter,
		IArchiveObjectTreeImporter archiveImporter,
		IModLibraryStore store,
		IPatchGroupAnalysisProvider? patchFactsProvider = null,
		IModDerivedDataCleanup? derivedDataCleanup = null,
		IModInformationCenter? informationCenter = null,
		IModInformationReader? informationReader = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_folderImporter = folderImporter ?? throw new ArgumentNullException(nameof(folderImporter));
		_archiveImporter = archiveImporter ?? throw new ArgumentNullException(nameof(archiveImporter));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_informationCenter = informationCenter;
		_informationReader = informationReader;
		_legacyCleanup = derivedDataCleanup;
		_normalizer = new PatchFileNormalizer(new PatchFileNameParser());
		_archiveExtractor = new SevenZipArchiveExtractor();
		_temporaryDirectories = new ImportTemporaryDirectoryManager(_paths);
	}

	public async ValueTask<ImportResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default)
	{
		var full = Path.GetFullPath(folderPath);
		if (!Directory.Exists(full))
		{
			throw new DirectoryNotFoundException(full);
		}

		var units = await DiscoverFolderUnitsAsync(full, cancellationToken).ConfigureAwait(false);
		if (units.Count == 0)
			throw new InvalidDataException("No patch or decoration package was found in the selected folder.");
		return await ImportUnitsAsync(units, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<ImportResult> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default)
	{
		var full = Path.GetFullPath(archiveFilePath);
		if (!File.Exists(full))
		{
			throw new FileNotFoundException("Archive file not found.", full);
		}

		var extractRoot = _temporaryDirectories.Create();
		try
		{
			await _archiveExtractor.ExtractAsync(full, extractRoot, cancellationToken).ConfigureAwait(false);
			// Use the same package discovery path as a dropped folder. This matters for
			// compound distributions such as a root .addon64 next to an inner Lua-patch
			// archive: both components must survive one user import.
			var units = await DiscoverFolderUnitsAsync(
				extractRoot,
				cancellationToken,
				Path.GetFileNameWithoutExtension(full)).ConfigureAwait(false);
			return await ImportUnitsAsync(units, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			try { _temporaryDirectories.Delete(extractRoot); } catch { }
		}
	}

	private async ValueTask<ImportResult> ImportUnitsAsync(IReadOnlyList<ImportSourceUnit> units, CancellationToken cancellationToken)
	{
		ImportedObjectTree? firstStoredTree = null;
		LibrarySnapshot? latestSnapshot = null;
		var optionPathConflicts = new List<OptionPathConflict>();
		var importedNodeIds = new List<ModNodeId>();
		var cacheInvalidationFailedNodeIds = new List<ModNodeId>();
		foreach (var unit in units)
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				var rawTree = await _folderImporter.ImportFolderAsync(unit.SourceRoot, cancellationToken).ConfigureAwait(false);
				var planned = unit.Manifest is null
					? PlanHeuristicImport(rawTree, unit.SourceRoot)
					: PlanManifestImport(rawTree, unit.Manifest);
				if (planned.Tree.Nodes.Count == 0) continue;
				planned = await RestoreExportedNodeIdsAsync(planned, unit.SourceRoot, cancellationToken).ConfigureAwait(false);
				optionPathConflicts.AddRange(planned.OptionPathConflicts);
				var (storedTree, snapshot, invalidationFailures) = await CommitImportAsync(
					planned,
					unit.SourceRoot,
					unit.SourceDisplayName,
					unit.PreferHardLinks,
					cancellationToken,
					unit.Manifest).ConfigureAwait(false);
				SetStoredTreeReadOnly(storedTree);
				firstStoredTree ??= storedTree;
				latestSnapshot = snapshot;
				importedNodeIds.AddRange(storedTree.Nodes.Keys);
				cacheInvalidationFailedNodeIds.AddRange(invalidationFailures);
			}
			finally
			{
				if (unit.TemporaryRoot is not null)
				{
					try { _temporaryDirectories.Delete(unit.TemporaryRoot); } catch { }
				}
			}
		}

		if (firstStoredTree is null || latestSnapshot is null)
			throw new InvalidDataException("No importable patch or decoration package was found.");
		var distinctImportedNodeIds = importedNodeIds.Distinct().ToArray();
		var distinctCacheFailures = cacheInvalidationFailedNodeIds.Distinct().ToArray();
		var cacheInvalidationStatus = distinctCacheFailures.Length == 0
			? ImportCacheInvalidationStatus.Completed
			: distinctCacheFailures.Length >= distinctImportedNodeIds.Length
				? ImportCacheInvalidationStatus.Failed
				: ImportCacheInvalidationStatus.Partial;
		return new ImportResult(
			latestSnapshot,
			firstStoredTree.RootId,
			firstStoredTree.SourceDisplayName,
			optionPathConflicts.Count > 0)
		{
			OptionPathConflicts = optionPathConflicts
				.DistinctBy(conflict => (conflict.SourceRelativePath, string.Join("|", conflict.AffectedParentOptionIds.OrderBy(id => id.Value))))
				.ToArray(),
			CacheInvalidationStatus = cacheInvalidationStatus,
			CacheInvalidationFailedNodeIds = distinctCacheFailures,
		};
	}

	internal static bool ContainsImportablePayload(ImportedObjectTree tree, string sourceRootDirectory)
		=> tree.Nodes.Values.Any(node => node.PatchGroups.Count > 0 || node.Metadata.Kind == ModNodeKind.Decoration)
			|| ExternalInstallFileFactsService.HasRecognizedFiles(sourceRootDirectory);

	internal static PackageRoot ResolvePackageRoot(string directory)
	{
		var manifest = StandardModManifest.TryLoad(directory);
		if (manifest is not null) return new PackageRoot(directory, manifest);

		try
		{
			var directories = Directory.EnumerateDirectories(directory).ToArray();
			var files = Directory.EnumerateFiles(directory).ToArray();
			if (directories.Length == 1 && files.Length == 0)
			{
				var wrapped = directories[0];
				manifest = StandardModManifest.TryLoad(wrapped);
				if (manifest is not null) return new PackageRoot(wrapped, manifest);
			}
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }

		return new PackageRoot(directory, null);
	}

	private async ValueTask<IReadOnlyList<ImportSourceUnit>> DiscoverFolderUnitsAsync(
		string root,
		CancellationToken cancellationToken,
		string? rootDisplayName = null)
	{
		var packageRoot = ResolvePackageRoot(root);
		// A manifest owns its complete package tree. Nested archives are then just
		// package files, not separate user-selected imports.
		if (packageRoot.Manifest is not null)
		{
			return [new ImportSourceUnit(packageRoot.Directory, rootDisplayName ?? new DirectoryInfo(root).Name, false, packageRoot.Manifest)];
		}

		var units = new List<ImportSourceUnit>();
		var directTree = await _folderImporter.ImportFolderAsync(root, cancellationToken).ConfigureAwait(false);
		if (ContainsImportablePayload(directTree, root))
		{
			units.Add(new ImportSourceUnit(root, rootDisplayName ?? new DirectoryInfo(root).Name, false, null));
		}

		foreach (var archive in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => ArchiveExtensions.Contains(Path.GetExtension(path))))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var extracted = _temporaryDirectories.Create();
			try
			{
				await _archiveExtractor.ExtractAsync(archive, extracted, cancellationToken).ConfigureAwait(false);
				var archiveRoot = ResolvePackageRoot(extracted);
				var archiveTree = await _folderImporter.ImportFolderAsync(archiveRoot.Directory, cancellationToken).ConfigureAwait(false);
				if (!ContainsImportablePayload(archiveTree, archiveRoot.Directory))
				{
					_temporaryDirectories.Delete(extracted);
					continue;
				}
				units.Add(new ImportSourceUnit(archiveRoot.Directory, Path.GetFileNameWithoutExtension(archive), true, archiveRoot.Manifest, extracted));
			}
			catch
			{
				try { _temporaryDirectories.Delete(extracted); } catch { }
				throw;
			}
		}
		return units;
	}

	private async ValueTask<(ImportedObjectTree Tree, LibrarySnapshot Snapshot, IReadOnlyList<ModNodeId> CacheInvalidationFailedNodeIds)> CommitImportAsync(PlannedImportTree planned, string sourceRoot, string sourceName, bool preferHardLinks, CancellationToken cancellationToken, StandardModManifest? manifest = null)
	{
		ImportedObjectTree? storedTree = null;
		try
		{
			storedTree = PersistFlattenedTree(planned, sourceRoot, sourceName, preferHardLinks, cancellationToken, manifest);
			var snapshot = await MergeIntoSnapshotAsync(storedTree, cancellationToken).ConfigureAwait(false);
			await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
			var invalidationFailures = await InvalidateImportedNodesAsync(storedTree.Nodes.Keys).ConfigureAwait(false);
			return (storedTree, snapshot, invalidationFailures);
		}
		catch
		{
			if (storedTree is not null) await RollbackStoredTreeAsync(storedTree).ConfigureAwait(false);
			throw;
		}
	}

	private async ValueTask RollbackStoredTreeAsync(ImportedObjectTree storedTree)
	{
		foreach (var node in storedTree.Nodes.Values)
		{
			try
			{
				_informationReader?.InvalidateNode(node.Id);
				if (_informationCenter is not null)
					await _informationCenter.InvalidateNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(false);
				else if (_legacyCleanup is not null)
					await _legacyCleanup.DeleteAsync(node.Id).ConfigureAwait(false);
				var directory = Path.Combine(_paths.ModsDirectory, node.RelativePath);
				if (Directory.Exists(directory))
				{
					SetReadOnlyRecursive(directory, readOnly: false);
					Directory.Delete(directory, recursive: true);
				}
			}
			catch { }
		}
	}

	private void NormalizePatchDirectories(string rootDirectory, CancellationToken cancellationToken)
	{
		foreach (var dir in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories).Prepend(rootDirectory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			_normalizer.NormalizeDirectory(dir, cancellationToken);
		}
	}

	private ImportedObjectTree PersistFlattenedTree(PlannedImportTree planned, string sourceRoot, string sourceDisplayName, bool preferHardLinks, CancellationToken cancellationToken, StandardModManifest? manifest)
	{
		var imported = planned.Tree;
		Directory.CreateDirectory(_paths.ModsDirectory);
		var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var nodes = new Dictionary<ModNodeId, ModNode>();

		foreach (var kvp in imported.Nodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = kvp.Value;
			var sourceRelativePaths = planned.SourceRelativePaths.TryGetValue(node.Id, out var plannedPaths)
				? plannedPaths
				: new[] { NormalizeRelativePath(node.RelativePath) };
			var directoryDisplayName = sourceRelativePaths.Count == 0 && node.Metadata.Kind == ModNodeKind.Option
				? $"{sourceDisplayName}-{node.Metadata.Name}"
				: BuildDisplayName(sourceDisplayName, node.RelativePath);
			var flatDirName = CreateUniqueDirectoryName(_paths.ModsDirectory, directoryDisplayName, usedNames);
			var destDir = Path.Combine(_paths.ModsDirectory, flatDirName);
			Directory.CreateDirectory(destDir);
			foreach (var sourceRelativePath in sourceRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var sourceNodeDir = string.IsNullOrWhiteSpace(sourceRelativePath)
					? sourceRoot
					: Path.Combine(sourceRoot, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
				if (!Directory.Exists(sourceNodeDir)) continue;
				CopyTopLevelFiles(sourceNodeDir, destDir, preferHardLinks, cancellationToken);
				CopyExternalInstallFiles(sourceNodeDir, destDir, preferHardLinks, cancellationToken);
			}
			CopyResolvedIcon(sourceRoot, destDir, node, manifest, cancellationToken);
			WriteRelationMetadata(destDir, node.Metadata, cancellationToken);
			NormalizePatchDirectories(destDir, cancellationToken);

			nodes[kvp.Key] = node with
			{
				RelativePath = flatDirName,
				Metadata = node.Metadata with { Name = ResolveStoredNodeDisplayName(node, directoryDisplayName, manifest) },
				Children = Array.Empty<ModNodeId>(),
			};
		}

		return imported with
		{
			Nodes = nodes,
			SourceDisplayName = sourceDisplayName,
		};
	}

	private static string ResolveStoredNodeDisplayName(ModNode node, string fallback, StandardModManifest? manifest)
	{
		if (manifest is null) return fallback;
		var manifestNodeName = manifest.Nodes?
			.FirstOrDefault(item => string.Equals(NormalizeRelativePath(item.RelativePath), node.Metadata.SourcePackagePath, StringComparison.OrdinalIgnoreCase))?
			.Name;
		if (!string.IsNullOrWhiteSpace(manifestNodeName)) return manifestNodeName;
		var entries = BuildManifestEntries(manifest);
		var sourcePath = !string.IsNullOrWhiteSpace(node.Metadata.SourcePackagePath)
			? node.Metadata.SourcePackagePath!
			: NormalizeRelativePath(node.RelativePath);
		var entry = FindManifestEntryByKey(entries, sourcePath)
			?? FindUniqueManifestEntryForSourcePath(entries, sourcePath);
		return string.IsNullOrWhiteSpace(entry?.Name) ? fallback : entry.Name!;
	}

	private async ValueTask<LibrarySnapshot> MergeIntoSnapshotAsync(ImportedObjectTree imported, CancellationToken cancellationToken)
	{
		var current = await _store.TryLoadAsync(cancellationToken).ConfigureAwait(false);
		var nodes = current?.Nodes is not null
			? new Dictionary<ModNodeId, ModNode>(current.Nodes)
			: new Dictionary<ModNodeId, ModNode>();

		foreach (var kvp in imported.Nodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			nodes[kvp.Key] = kvp.Value;
		}

		var profiles = current?.Profiles?.ToList() ?? new List<Profile>();

		return new LibrarySnapshot(
			Version: current?.Version ?? SnapshotVersion,
			SavedUtc: DateTimeOffset.UtcNow,
			Nodes: nodes,
			Profiles: profiles,
			ActiveProfileId: current?.ActiveProfileId);
	}

	// Only the manager's exported Nodes[].Guid format participates in identity restore.
	// Community manifests with a root Guid and Options remain ordinary package metadata.
	private static async ValueTask<PlannedImportTree> RestoreExportedNodeIdsAsync(
		PlannedImportTree planned,
		string sourceRoot,
		CancellationToken cancellationToken)
	{
		var tree = planned.Tree;
		var manifestPath = Path.Combine(sourceRoot, "manifest.json");
		if (!File.Exists(manifestPath)) return planned;

		NodeGuidManifest? manifest;
		try
		{
			await using var stream = File.OpenRead(manifestPath);
			manifest = await JsonSerializer.DeserializeAsync<NodeGuidManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
			{
				PropertyNameCaseInsensitive = true,
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (JsonException)
		{
			return planned;
		}

		if (manifest?.Nodes is not { Count: > 0 }) return planned;
		var idsByPath = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in manifest.Nodes)
		{
			if (entry is null || !Guid.TryParse(entry.Guid, out var id)) continue;
			if (!idsByPath.TryAdd(NormalizeRelativePath(entry.RelativePath), new ModNodeId(id))) return planned;
		}
		if (idsByPath.Count == 0) return planned;

		var replacements = tree.Nodes.Values
			.Where(node => idsByPath.TryGetValue(NormalizeRelativePath(node.RelativePath), out _))
			.ToDictionary(node => node.Id, node => idsByPath[NormalizeRelativePath(node.RelativePath)]);
		if (replacements.Values.Distinct().Count() != replacements.Count || replacements.Count == 0) return planned;

		var nodes = new Dictionary<ModNodeId, ModNode>();
		foreach (var node in tree.Nodes.Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var nodeId = replacements.GetValueOrDefault(node.Id, node.Id);
			var children = node.Children.Select(child => replacements.GetValueOrDefault(child, child)).ToArray();
			nodes.Add(nodeId, node with { Id = nodeId, Children = children });
		}
		var sourcePaths = planned.SourceRelativePaths.ToDictionary(
			pair => replacements.GetValueOrDefault(pair.Key, pair.Key),
			pair => pair.Value);
		var optionPathConflicts = planned.OptionPathConflicts
			.Select(conflict => conflict with
			{
				AffectedParentOptionIds = conflict.AffectedParentOptionIds
					.Select(id => replacements.GetValueOrDefault(id, id))
					.Distinct()
					.ToArray(),
			})
			.ToArray();
		return planned with
		{
			Tree = tree with { RootId = replacements.GetValueOrDefault(tree.RootId, tree.RootId), Nodes = nodes },
			SourceRelativePaths = sourcePaths,
			OptionPathConflicts = optionPathConflicts,
		};
	}

	private async ValueTask<IReadOnlyList<ModNodeId>> InvalidateImportedNodesAsync(IEnumerable<ModNodeId> nodeIds)
	{
		var failures = new List<ModNodeId>();
		foreach (var nodeId in nodeIds.Distinct())
		{
			try
			{
				// Import may restore a manager-owned node ID.  Its old directory can have
				// entirely different Patch/sidecar content, so revision-keyed caches alone
				// are not the ownership boundary here.
				_informationReader?.InvalidateNode(nodeId);
				if (_informationCenter is not null)
					await _informationCenter.InvalidateNodeAsync(nodeId, CancellationToken.None).ConfigureAwait(false);
				else if (_legacyCleanup is not null)
					await _legacyCleanup.DeleteAsync(nodeId).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				// Continue through the remaining Nodes. One broken durable-cache entry
				// must not keep unrelated imported content from crossing this boundary.
				failures.Add(nodeId);
				System.Diagnostics.Debug.WriteLine($"[ModLibraryImporter] cache invalidation failed for {nodeId}: {exception}");
			}
		}
		return failures;
	}

	internal static PlannedImportTree PlanTreeForImport(ImportedObjectTree tree, string sourceRootDirectory, StandardModManifest? manifest)
		=> manifest is null
			? PlanHeuristicImport(tree, sourceRootDirectory)
			: PlanManifestImport(tree, manifest);

	internal static PlannedImportTree PlanManifestImport(ImportedObjectTree tree, StandardModManifest manifest)
	{
		var entries = BuildManifestEntries(manifest);
		var rootEntry = entries[0];
		var rootCandidate = tree.Nodes.Values.FirstOrDefault(node => string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath)));
		// A community root Guid is package metadata, not a manager identity, when
		// the source already has a concrete root node. This preserves prior export
		// behavior while still giving a no-patch manifest root a stable identity.
		var rootId = rootCandidate?.Id
			?? (Guid.TryParse(manifest.Guid, out var parsedGuid) ? new ModNodeId(parsedGuid) : ModNodeId.New());
		var nodes = new Dictionary<ModNodeId, ModNode>();
		var sourcePaths = new Dictionary<ModNodeId, IReadOnlyList<string>>();

		if (rootCandidate is not null)
		{
			var rootKind = rootCandidate.Metadata.Kind == ModNodeKind.ReShade
				&& rootCandidate.PatchGroups.Count == 0
				&& entries.Count == 1
				? ModNodeKind.ReShade
				: ModNodeKind.Standard;
			nodes[rootId] = rootCandidate with
			{
				Id = rootId,
				Metadata = rootCandidate.Metadata with
				{
					Name = string.IsNullOrWhiteSpace(rootEntry.Name) ? rootCandidate.Metadata.Name : rootEntry.Name!,
					Notes = rootEntry.Notes ?? manifest.Description,
					Kind = rootKind,
					HostModGuids = null,
					SourcePackageGuid = manifest.Guid,
					SourcePackagePath = null,
				},
			};
		}
		else
		{
			nodes[rootId] = new ModNode(
				rootId,
				string.Empty,
				new ModNodeMetadata(
					string.IsNullOrWhiteSpace(rootEntry.Name) ? tree.SourceDisplayName : rootEntry.Name!,
					rootEntry.Notes ?? manifest.Description,
					DateTimeOffset.UtcNow,
					null,
					ModNodeKind.Standard,
					null,
					manifest.Guid),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
		}
		sourcePaths[rootId] = [string.Empty];

		var entryIds = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase) { [rootEntry.Key] = rootId };
		foreach (var entry in entries.Where(entry => entry.Kind == ModNodeKind.Option))
		{
			var explicitGuid = FindNodeGuid(manifest, entry.IncludePaths);
			entryIds[entry.Key] = Guid.TryParse(explicitGuid, out var parsedOptionId)
				? new ModNodeId(parsedOptionId)
				: new ModNodeId(CreateStableNodeGuid(manifest.Guid ?? rootId.Value.ToString("N"), entry.Key));
		}
		if (entryIds.Values.Distinct().Count() != entryIds.Count)
			throw new InvalidDataException("manifest 为多个逻辑选项指定了相同的 Mod ID。");

		var optionEntries = entries.Where(entry => entry.Kind == ModNodeKind.Option).ToArray();
		var ownedPayloadNodes = optionEntries.ToDictionary(
			entry => entry.Key,
			_ => new List<ModNode>(),
			StringComparer.OrdinalIgnoreCase);
		var conflictingPayloadNodes = new List<ManifestPayloadConflict>();
		foreach (var sourceNode in tree.Nodes.Values
			.Where(node => node.Metadata.Kind != ModNodeKind.Decoration)
			.Where(node => node.PatchGroups.Count > 0)
			.OrderBy(node => NormalizeRelativePath(node.RelativePath), StringComparer.OrdinalIgnoreCase))
		{
			var sourcePath = NormalizeRelativePath(sourceNode.RelativePath);
			var owners = FindManifestEntriesForSourcePath(entries, sourcePath);
			if (owners.Count == 1 && owners[0].Kind == ModNodeKind.Option)
			{
				ownedPayloadNodes[owners[0].Key].Add(sourceNode);
				continue;
			}
			if (owners.Count > 1)
				conflictingPayloadNodes.Add(new ManifestPayloadConflict(sourceNode, sourcePath, owners));
		}

		var conflictParentIds = conflictingPayloadNodes
			.SelectMany(conflict => conflict.Owners)
			.Select(entry => entryIds[entry.Key])
			.ToHashSet();
		foreach (var entry in optionEntries)
		{
			var optionId = entryIds[entry.Key];
			var parentId = entry.ParentKey is not null && entryIds.TryGetValue(entry.ParentKey, out var resolvedParent)
				? resolvedParent
				: rootId;
			var matchedNodes = ownedPayloadNodes[entry.Key]
				.OrderBy(node => NormalizeRelativePath(node.RelativePath), StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var paths = matchedNodes
				.Select(node => NormalizeRelativePath(node.RelativePath))
				.Where(path => !string.IsNullOrEmpty(path))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var sourceNode = matchedNodes.FirstOrDefault();
			var fallbackName = sourceNode?.Metadata.Name ?? $"选项 {entry.OptionOrder.GetValueOrDefault() + 1}";
			nodes[optionId] = new ModNode(
				optionId,
				paths.FirstOrDefault() ?? entry.IncludePaths.FirstOrDefault() ?? $"__logical_option__/{entry.Key}",
				new ModNodeMetadata(
					string.IsNullOrWhiteSpace(entry.Name) ? fallbackName : entry.Name!,
					entry.Notes,
					sourceNode?.Metadata.CreatedUtc ?? DateTimeOffset.UtcNow,
					sourceNode?.Metadata.ModifiedUtc,
					ModNodeKind.Option,
					[parentId.Value.ToString("N")],
					manifest.Guid,
					entry.Key,
					entry.OptionOrder,
					SuppressDefaultDirectOptionActivation: conflictParentIds.Contains(optionId)),
				matchedNodes
					.SelectMany(node => node.PatchGroups)
					.Distinct()
					.OrderBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
					.ThenBy(group => group.PatchIndex)
					.ToArray(),
				Array.Empty<ModNodeId>());
			sourcePaths[optionId] = paths;
		}

		for (var conflictIndex = 0; conflictIndex < conflictingPayloadNodes.Count; conflictIndex++)
		{
			var conflict = conflictingPayloadNodes[conflictIndex];
			var physicalId = new ModNodeId(CreateStableNodeGuid(manifest.Guid ?? rootId.Value.ToString("N"), $"conflict:{conflict.SourceRelativePath}"));
			var hostIds = conflict.Owners
				.Select(entry => entryIds[entry.Key].Value.ToString("N"))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var manifestNode = manifest.Nodes?.FirstOrDefault(node => string.Equals(
				NormalizeRelativePath(node.RelativePath),
				conflict.SourceRelativePath,
				StringComparison.OrdinalIgnoreCase));
			var leafName = manifestNode?.Name;
			if (string.IsNullOrWhiteSpace(leafName))
				leafName = Path.GetFileName(conflict.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
			if (string.IsNullOrWhiteSpace(leafName)) leafName = "共享选项内容";
			nodes[physicalId] = new ModNode(
				physicalId,
				conflict.SourceRelativePath,
				new ModNodeMetadata(leafName, manifestNode?.Notes, conflict.SourceNode.Metadata.CreatedUtc, conflict.SourceNode.Metadata.ModifiedUtc,
					ModNodeKind.Option, hostIds, manifest.Guid, conflict.SourceRelativePath, int.MinValue + conflictIndex),
				conflict.SourceNode.PatchGroups,
				Array.Empty<ModNodeId>());
			sourcePaths[physicalId] = [conflict.SourceRelativePath];
		}

		foreach (var decoration in tree.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Decoration))
		{
			if (nodes.ContainsKey(decoration.Id)) continue;
			nodes[decoration.Id] = decoration;
			sourcePaths[decoration.Id] = [NormalizeRelativePath(decoration.RelativePath)];
		}

		return new PlannedImportTree(tree with { RootId = rootId, Nodes = nodes }, sourcePaths)
		{
			OptionPathConflicts = conflictingPayloadNodes
				.Select(conflict => new OptionPathConflict(
					conflict.SourceRelativePath,
					conflict.Owners.Select(entry => entryIds[entry.Key]).Distinct().ToArray()))
				.ToArray(),
		};
	}

	private static IReadOnlyList<ManifestEntry> FindManifestEntriesForSourcePath(IReadOnlyList<ManifestEntry> entries, string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			var root = FindManifestEntryByKey(entries, string.Empty);
			return root is null ? Array.Empty<ManifestEntry>() : [root];
		}
		var matches = entries
			.Where(entry => entry.Kind == ModNodeKind.Option)
			.SelectMany(entry => entry.IncludePaths.Select(include => (Entry: entry, Include: include)))
			.Where(candidate => SourcePathMatchesInclude(path, candidate.Include))
			.OrderByDescending(candidate => candidate.Include.Length)
			.ToArray();
		if (matches.Length == 0) return Array.Empty<ManifestEntry>();
		var longest = matches[0].Include.Length;
		return matches.Where(candidate => candidate.Include.Length == longest).Select(candidate => candidate.Entry).Distinct().ToArray();
	}

	private static ManifestEntry? FindUniqueManifestEntryForSourcePath(IReadOnlyList<ManifestEntry> entries, string path)
	{
		var matches = FindManifestEntriesForSourcePath(entries, path);
		return matches.Count == 1 ? matches[0] : null;
	}

	private static bool SourcePathMatchesInclude(string sourcePath, string include)
		=> string.Equals(sourcePath, include, StringComparison.OrdinalIgnoreCase)
			|| sourcePath.StartsWith(include + '/', StringComparison.OrdinalIgnoreCase)
			|| sourcePath.EndsWith('/' + include, StringComparison.OrdinalIgnoreCase)
			|| sourcePath.Contains('/' + include + '/', StringComparison.OrdinalIgnoreCase);

	private static ManifestEntry? FindManifestEntryByKey(IReadOnlyList<ManifestEntry> entries, string key)
		=> entries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

	internal static PlannedImportTree PlanHeuristicImport(ImportedObjectTree tree, string sourceRootDirectory)
	{
		var payloadNodes = tree.Nodes.Values
			.Where(node => node.PatchGroups.Count > 0
				|| node.Metadata.Kind == ModNodeKind.Decoration
				|| (string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath))
					&& ExternalInstallFileFactsService.HasRecognizedFiles(sourceRootDirectory)))
			.OrderBy(node => NormalizeRelativePath(node.RelativePath), StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (payloadNodes.Length == 0)
			return new PlannedImportTree(tree with { Nodes = new Dictionary<ModNodeId, ModNode>() }, new Dictionary<ModNodeId, IReadOnlyList<string>>());
		if (payloadNodes.Length == 1)
		{
			var only = payloadNodes[0];
			var kind = GetHeuristicStandaloneKind(only, sourceRootDirectory);
			var standalone = only with { Metadata = only.Metadata with { Kind = kind, HostModGuids = null, SourcePackagePath = null } };
			return new PlannedImportTree(
				tree with { RootId = only.Id, Nodes = new Dictionary<ModNodeId, ModNode> { [only.Id] = standalone } },
				new Dictionary<ModNodeId, IReadOnlyList<string>> { [only.Id] = [NormalizeRelativePath(only.RelativePath)] });
		}

		var patchNodes = payloadNodes
			.Where(node => node.PatchGroups.Count > 0 && node.Metadata.Kind is not (ModNodeKind.Decoration or ModNodeKind.ReShade))
			.ToArray();
		if (patchNodes.Length == 0)
		{
			var sourcePaths = payloadNodes.ToDictionary(
				node => node.Id,
				node => (IReadOnlyList<string>)[NormalizeRelativePath(node.RelativePath)]);
			return new PlannedImportTree(tree with { RootId = payloadNodes[0].Id, Nodes = payloadNodes.ToDictionary(node => node.Id) }, sourcePaths);
		}

		var rootCandidate = patchNodes.FirstOrDefault(node => string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath)));
		var rootId = rootCandidate?.Id ?? ModNodeId.New();
		var nodes = new Dictionary<ModNodeId, ModNode>();
		var sourcePathsByNode = new Dictionary<ModNodeId, IReadOnlyList<string>>();
		if (rootCandidate is not null)
		{
			nodes[rootId] = rootCandidate with
			{
				Id = rootId,
				Metadata = rootCandidate.Metadata with { Kind = ModNodeKind.Standard, HostModGuids = null, SourcePackagePath = null },
			};
			sourcePathsByNode[rootId] = [string.Empty];
		}
		else
		{
			nodes[rootId] = new ModNode(
				rootId,
				string.Empty,
				new ModNodeMetadata(tree.SourceDisplayName, null, DateTimeOffset.UtcNow, null, ModNodeKind.Standard),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
			sourcePathsByNode[rootId] = Array.Empty<string>();
		}

		foreach (var auxiliary in payloadNodes.Where(node => node.Id != rootCandidate?.Id && (node.Metadata.Kind is ModNodeKind.Decoration or ModNodeKind.ReShade)))
		{
			nodes[auxiliary.Id] = auxiliary;
			sourcePathsByNode[auxiliary.Id] = [NormalizeRelativePath(auxiliary.RelativePath)];
		}

		var patchByPath = patchNodes
			.Where(node => !string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath)))
			.ToDictionary(node => NormalizeRelativePath(node.RelativePath), StringComparer.OrdinalIgnoreCase);
		var logicalPaths = patchByPath.Keys
			.SelectMany(GetPathAndAncestors)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => PathDepth(path))
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var siblingOrders = logicalPaths
			.GroupBy(GetParentPath, StringComparer.OrdinalIgnoreCase)
			.SelectMany(group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select((path, order) => (Path: path, Order: order)))
			.ToDictionary(pair => pair.Path, pair => pair.Order, StringComparer.OrdinalIgnoreCase);
		var idsByPath = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = rootId };
		foreach (var path in logicalPaths)
		{
			var parentPath = GetParentPath(path);
			if (!idsByPath.TryGetValue(parentPath, out var parentId))
				throw new InvalidDataException($"无法为启发式选项目录“{path}”定位父节点。");
			patchByPath.TryGetValue(path, out var physicalNode);
			var optionId = physicalNode?.Id ?? ModNodeId.New();
			idsByPath[path] = optionId;
			var fallbackName = string.Join('-', new[] { tree.SourceDisplayName }.Concat(path.Split('/')));
			nodes[optionId] = (physicalNode ?? new ModNode(
				optionId,
				path,
				new ModNodeMetadata(fallbackName, null, DateTimeOffset.UtcNow, null, ModNodeKind.Option),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>())) with
			{
				Id = optionId,
				RelativePath = path,
				Metadata = (physicalNode?.Metadata ?? new ModNodeMetadata(fallbackName, null, DateTimeOffset.UtcNow, null, ModNodeKind.Option)) with
				{
					Kind = ModNodeKind.Option,
					HostModGuids = [parentId.Value.ToString("N")],
					SourcePackagePath = path,
					OptionOrder = siblingOrders[path],
				},
				Children = Array.Empty<ModNodeId>(),
			};
			sourcePathsByNode[optionId] = physicalNode is null ? Array.Empty<string>() : [path];
		}

		return new PlannedImportTree(tree with { RootId = rootId, Nodes = nodes }, sourcePathsByNode);
	}

	private static ModNodeKind GetHeuristicStandaloneKind(ModNode node, string sourceRootDirectory)
		=> node.Metadata.Kind == ModNodeKind.Decoration
			? ModNodeKind.Decoration
			: string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath))
				&& node.PatchGroups.Count == 0
				&& ExternalInstallFileFactsService.HasRecognizedFiles(sourceRootDirectory)
				? ModNodeKind.ReShade
				: ModNodeKind.Standard;

	private static List<ManifestEntry> BuildManifestEntries(StandardModManifest manifest)
	{
		var result = new List<ManifestEntry>
		{
			new(string.Empty, null, manifest.Name, manifest.Description, manifest.IconPath, Array.Empty<string>(), ModNodeKind.Standard, null),
		};
		for (var index = 0; index < manifest.Options.Count; index++)
			AddOption(manifest.Options[index], string.Empty, $"option:{index}", index);
		return result;

		void AddOption(StandardModManifestOption option, string parentKey, string key, int siblingOrder)
		{
			var includePaths = (option.Include ?? [])
				.Select(NormalizeRelativePath)
				.Where(path => !string.IsNullOrEmpty(path))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			result.Add(new ManifestEntry(key, parentKey, option.Name, option.Description, option.Image, includePaths, ModNodeKind.Option, siblingOrder));
			for (var childIndex = 0; childIndex < (option.SubOptions?.Count ?? 0); childIndex++)
				AddOption(option.SubOptions![childIndex], key, $"{key}/option:{childIndex}", childIndex);
		}
	}

	private static string? FindNodeGuid(StandardModManifest manifest, IReadOnlyList<string> paths)
	{
		foreach (var path in paths)
		{
			var guid = manifest.Nodes?.FirstOrDefault(item => string.Equals(NormalizeRelativePath(item.RelativePath), path, StringComparison.OrdinalIgnoreCase))?.Guid;
			if (!string.IsNullOrWhiteSpace(guid)) return guid;
		}
		return null;
	}

	private static void CopyResolvedIcon(string sourceRoot, string destinationDirectory, ModNode node, StandardModManifest? manifest, CancellationToken cancellationToken)
	{
		if (manifest is null) return;
		var entries = BuildManifestEntries(manifest);
		var sourcePath = !string.IsNullOrWhiteSpace(node.Metadata.SourcePackagePath)
			? node.Metadata.SourcePackagePath!
			: NormalizeRelativePath(node.RelativePath);
		var entry = FindManifestEntryByKey(entries, sourcePath)
			?? FindUniqueManifestEntryForSourcePath(entries, sourcePath);
		var image = entry?.Image ?? manifest.IconPath;
		if (string.IsNullOrWhiteSpace(image)) return;
		var source = Path.GetFullPath(Path.Combine(sourceRoot, image.Replace('/', Path.DirectorySeparatorChar)));
		if (!source.StartsWith(Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) return;
		cancellationToken.ThrowIfCancellationRequested();
		var extension = Path.GetExtension(source).ToLowerInvariant();
		if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp" and not ".webp") return;
		// Archive imports may already have copied this file as a hard link. Replacing
		// it would target the same underlying file and can fail while the source
		// archive stream or thumbnail reader still has it open.
		var destination = Path.Combine(destinationDirectory, "icon" + extension);
		if (File.Exists(destination)) return;
		File.Copy(source, Path.Combine(destinationDirectory, "icon" + extension), overwrite: true);
	}

	private sealed record ManifestEntry(
		string Key,
		string? ParentKey,
		string? Name,
		string? Notes,
		string? Image,
		IReadOnlyList<string> IncludePaths,
		ModNodeKind Kind,
		int? OptionOrder);

	private sealed record ManifestPayloadConflict(
		ModNode SourceNode,
		string SourceRelativePath,
		IReadOnlyList<ManifestEntry> Owners);

	internal sealed record PlannedImportTree(
		ImportedObjectTree Tree,
		IReadOnlyDictionary<ModNodeId, IReadOnlyList<string>> SourceRelativePaths)
	{
		public IReadOnlyList<OptionPathConflict> OptionPathConflicts { get; init; } = Array.Empty<OptionPathConflict>();
		public bool HasOptionPathConflicts => OptionPathConflicts.Count > 0;
	}

	internal sealed record PackageRoot(string Directory, StandardModManifest? Manifest);

	private sealed record ImportSourceUnit(
		string SourceRoot,
		string SourceDisplayName,
		bool PreferHardLinks,
		StandardModManifest? Manifest,
		string? TemporaryRoot = null);

	private static Guid CreateStableNodeGuid(string? packageGuid, string path)
	{
		var input = $"HD2ModManager:manifest-node:{packageGuid ?? "unknown"}:{NormalizeRelativePath(path).ToLowerInvariant()}";
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
		var guidBytes = bytes[..16];
		guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
		guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
		return new Guid(guidBytes);
	}

	private static void WriteRelationMetadata(string destinationDirectory, ModNodeMetadata metadata, CancellationToken cancellationToken)
	{
		if (metadata.Kind != ModNodeKind.Option || metadata.HostModGuids is not { Count: > 0 }) return;
		cancellationToken.ThrowIfCancellationRequested();
		var relation = new OptionRelationDocument
		{
			Version = 1,
			Kind = "Option",
			HostModGuids = metadata.HostModGuids.ToList(),
			SourcePackageGuid = metadata.SourcePackageGuid,
			SourcePath = metadata.SourcePackagePath,
			OptionOrder = metadata.OptionOrder,
			SuppressDefaultDirectOptionActivation = metadata.SuppressDefaultDirectOptionActivation,
		};
		File.WriteAllText(
			Path.Combine(destinationDirectory, "option.json"),
			JsonSerializer.Serialize(relation, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
	}


	private static string NormalizeRelativePath(string? path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace('\\', '/').Trim('/');

	private static IEnumerable<string> GetPathAndAncestors(string path)
	{
		var parts = NormalizeRelativePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (var index = 1; index <= parts.Length; index++)
			yield return string.Join('/', parts.Take(index));
	}

	private static string GetParentPath(string path)
	{
		var normalized = NormalizeRelativePath(path);
		var separator = normalized.LastIndexOf('/');
		return separator < 0 ? string.Empty : normalized[..separator];
	}

	private static int PathDepth(string path)
		=> string.IsNullOrEmpty(NormalizeRelativePath(path)) ? 0 : NormalizeRelativePath(path).Count(ch => ch == '/') + 1;

	private sealed class NodeGuidManifest
	{
		public List<NodeGuidManifestEntry>? Nodes { get; set; }
	}

	private sealed class NodeGuidManifestEntry
	{
		public string? RelativePath { get; set; }
		public string? Guid { get; set; }
	}

	private void SetStoredTreeReadOnly(ImportedObjectTree storedTree)
	{
		foreach (var node in storedTree.Nodes.Values)
		{
			SetReadOnlyRecursive(Path.Combine(_paths.ModsDirectory, node.RelativePath));
		}
	}

	private static void CopyTopLevelFiles(string sourceDir, string destDir, bool preferHardLinks, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destDir);
		foreach (var file in Directory.EnumerateFiles(sourceDir))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.Equals(Path.GetFileName(file), ImportTemporaryMarkerFileName, StringComparison.OrdinalIgnoreCase))
				continue;
			var destination = Path.Combine(destDir, Path.GetFileName(file));
			if (File.Exists(destination))
			{
				if (FilesAreByteIdentical(file, destination)) continue;
				throw new InvalidDataException($"同一个逻辑选项的多个 Include 包含同名但内容不同的文件：{Path.GetFileName(file)}。");
			}
			if (preferHardLinks)
			{
				try
				{
					if (!CreateHardLink(destination, file, IntPtr.Zero)) throw new IOException("Unable to create hard link.");
					continue;
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
			File.Copy(file, destination, overwrite: false);
		}
	}

	private static bool FilesAreByteIdentical(string leftPath, string rightPath)
	{
		var left = new FileInfo(leftPath);
		var right = new FileInfo(rightPath);
		if (left.Length != right.Length) return false;
		using var leftStream = File.OpenRead(leftPath);
		using var rightStream = File.OpenRead(rightPath);
		var leftBuffer = new byte[64 * 1024];
		var rightBuffer = new byte[leftBuffer.Length];
		while (true)
		{
			var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
			var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
			if (leftRead != rightRead) return false;
			if (leftRead == 0) return true;
			if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
		}
	}

	private static void CopyExternalInstallFiles(string sourceDir, string destDir, bool preferHardLinks, CancellationToken cancellationToken)
	{
		foreach (var sourcePath in EnumerateExternalFiles(sourceDir))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceDir, sourcePath);
			if (!ExternalInstallFileFactsService.TryMapRecognizedFile(
				sourcePath,
				relative,
				out _,
				out var normalizedSource,
				out _,
				out _)) continue;
			var destination = Path.Combine(destDir, normalizedSource.Replace('/', Path.DirectorySeparatorChar));
			var destinationDirectory = Path.GetDirectoryName(destination);
			if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
			if (File.Exists(destination)) continue;
			if (preferHardLinks)
			{
				try
				{
					if (CreateHardLink(destination, sourcePath, IntPtr.Zero)) continue;
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
			File.Copy(sourcePath, destination, overwrite: false);
		}
	}

	private static IEnumerable<string> EnumerateExternalFiles(string sourceDir)
	{
		IEnumerable<string> rootFiles;
		try { rootFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly); }
		catch (IOException) { rootFiles = Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { rootFiles = Array.Empty<string>(); }
		foreach (var file in rootFiles) yield return file;

		var bin = Path.Combine(sourceDir, "bin");
		if (!Directory.Exists(bin) || IsReparsePoint(bin)) yield break;
		var pending = new Stack<string>();
		pending.Push(bin);
		while (pending.Count > 0)
		{
			var directory = pending.Pop();
			IEnumerable<string> files;
			try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly); }
			catch (IOException) { continue; }
			catch (UnauthorizedAccessException) { continue; }
			foreach (var file in files) yield return file;

			IEnumerable<string> children;
			try { children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly); }
			catch (IOException) { continue; }
			catch (UnauthorizedAccessException) { continue; }
			foreach (var child in children)
				if (!IsReparsePoint(child)) pending.Push(child);
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

	private static string BuildDisplayName(string sourceDisplayName, string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
		{
			return sourceDisplayName;
		}

		var parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
		return string.Join('-', new[] { sourceDisplayName }.Concat(parts));
	}

	private static string CreateUniqueDirectoryName(string parent, string displayName, HashSet<string>? reservedNames = null)
	{
		var baseName = SanitizeFileName(displayName);
		var candidate = baseName;
		var index = 2;
		while ((reservedNames is not null && !reservedNames.Add(candidate)) || Directory.Exists(Path.Combine(parent, candidate)))
		{
			candidate = $"{baseName}_{index}";
			index++;
		}

		return candidate;
	}

	private static string SanitizeFileName(string value)
	{
		var sanitized = string.IsNullOrWhiteSpace(value) ? "ImportedMod" : value.Trim();
		foreach (var ch in Path.GetInvalidFileNameChars())
		{
			sanitized = sanitized.Replace(ch, '_');
		}

		return string.IsNullOrWhiteSpace(sanitized) ? "ImportedMod" : sanitized;
	}

	private static void SetReadOnlyRecursive(string directory, bool readOnly = true)
	{
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			var attributes = File.GetAttributes(path);
			File.SetAttributes(path, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
		}
	}
}
