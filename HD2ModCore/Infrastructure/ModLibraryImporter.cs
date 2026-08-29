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
		foreach (var unit in units)
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				var rawTree = await _folderImporter.ImportFolderAsync(unit.SourceRoot, cancellationToken).ConfigureAwait(false);
				var plannedTree = unit.Manifest is null
					? PlanHeuristicImport(rawTree, unit.SourceRoot)
					: PlanManifestImport(rawTree, unit.Manifest);
				if (plannedTree.Nodes.Count == 0) continue;

				plannedTree = await RestoreExportedNodeIdsAsync(plannedTree, unit.SourceRoot, cancellationToken).ConfigureAwait(false);
				var (storedTree, snapshot) = await CommitImportAsync(
					plannedTree,
					unit.SourceRoot,
					unit.SourceDisplayName,
					unit.PreferHardLinks,
					cancellationToken,
					unit.Manifest).ConfigureAwait(false);
				SetStoredTreeReadOnly(storedTree);
				firstStoredTree ??= storedTree;
				latestSnapshot = snapshot;
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
		return new ImportResult(latestSnapshot, firstStoredTree.RootId, firstStoredTree.SourceDisplayName);
	}

	private static bool ContainsImportablePayload(ImportedObjectTree tree, string sourceRootDirectory)
		=> tree.Nodes.Values.Any(node => node.PatchGroups.Count > 0 || node.Metadata.Kind == ModNodeKind.Decoration)
			|| ExternalInstallFileFactsService.HasRecognizedFiles(sourceRootDirectory);

	private static PackageRoot ResolvePackageRoot(string directory)
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

	private async ValueTask<(ImportedObjectTree Tree, LibrarySnapshot Snapshot)> CommitImportAsync(ImportedObjectTree tree, string sourceRoot, string sourceName, bool preferHardLinks, CancellationToken cancellationToken, StandardModManifest? manifest = null)
	{
		ImportedObjectTree? storedTree = null;
		try
		{
			storedTree = PersistFlattenedTree(tree, sourceRoot, sourceName, preferHardLinks, cancellationToken, manifest);
			var snapshot = await MergeIntoSnapshotAsync(storedTree, cancellationToken).ConfigureAwait(false);
			await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
			try
			{
				await InvalidateImportedNodesAsync(storedTree.Nodes.Keys).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				// The library snapshot and files are already committed.  A cache backend
				// outage must not turn that successful import into a half-rolled-back
				// library entry; synchronization will retry the invalidation boundary.
				System.Diagnostics.Debug.WriteLine($"[ModLibraryImporter] cache invalidation failed after import: {exception}");
			}
			return (storedTree, snapshot);
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

	private ImportedObjectTree PersistFlattenedTree(ImportedObjectTree imported, string sourceRoot, string sourceDisplayName, bool preferHardLinks, CancellationToken cancellationToken, StandardModManifest? manifest)
	{
		Directory.CreateDirectory(_paths.ModsDirectory);
		var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var nodes = new Dictionary<ModNodeId, ModNode>();

		foreach (var kvp in imported.Nodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = kvp.Value;
			var sourceNodeDir = string.IsNullOrWhiteSpace(node.RelativePath)
				? sourceRoot
				: Path.Combine(sourceRoot, node.RelativePath);

			var displayName = BuildDisplayName(sourceDisplayName, node.RelativePath);
			var flatDirName = CreateUniqueDirectoryName(_paths.ModsDirectory, displayName, usedNames);
			var destDir = Path.Combine(_paths.ModsDirectory, flatDirName);
			CopyTopLevelFiles(sourceNodeDir, destDir, preferHardLinks, cancellationToken);
			CopyExternalInstallFiles(sourceNodeDir, destDir, preferHardLinks, cancellationToken);
			CopyResolvedIcon(sourceRoot, destDir, node.RelativePath, manifest, cancellationToken);
			WriteRelationMetadata(destDir, node.Metadata, cancellationToken);
			NormalizePatchDirectories(destDir, cancellationToken);

			nodes[kvp.Key] = node with
			{
				RelativePath = flatDirName,
				Metadata = node.Metadata with { Name = displayName },
				Children = Array.Empty<ModNodeId>(),
			};
		}

		return imported with
		{
			Nodes = nodes,
			SourceDisplayName = sourceDisplayName,
		};
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
	private static async ValueTask<ImportedObjectTree> RestoreExportedNodeIdsAsync(
		ImportedObjectTree tree,
		string sourceRoot,
		CancellationToken cancellationToken)
	{
		var manifestPath = Path.Combine(sourceRoot, "manifest.json");
		if (!File.Exists(manifestPath)) return tree;

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
			return tree;
		}

		if (manifest?.Nodes is not { Count: > 0 }) return tree;
		var idsByPath = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in manifest.Nodes)
		{
			if (entry is null || !Guid.TryParse(entry.Guid, out var id)) continue;
			if (!idsByPath.TryAdd(NormalizeRelativePath(entry.RelativePath), new ModNodeId(id))) return tree;
		}
		if (idsByPath.Count == 0) return tree;

		var replacements = tree.Nodes.Values
			.Where(node => idsByPath.TryGetValue(NormalizeRelativePath(node.RelativePath), out _))
			.ToDictionary(node => node.Id, node => idsByPath[NormalizeRelativePath(node.RelativePath)]);
		if (replacements.Values.Distinct().Count() != replacements.Count || replacements.Count == 0) return tree;

		var nodes = new Dictionary<ModNodeId, ModNode>();
		foreach (var node in tree.Nodes.Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var nodeId = replacements.GetValueOrDefault(node.Id, node.Id);
			var children = node.Children.Select(child => replacements.GetValueOrDefault(child, child)).ToArray();
			nodes.Add(nodeId, node with { Id = nodeId, Children = children });
		}
		return tree with { RootId = replacements.GetValueOrDefault(tree.RootId, tree.RootId), Nodes = nodes };
	}

	private async ValueTask InvalidateImportedNodesAsync(IEnumerable<ModNodeId> nodeIds)
	{
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
				System.Diagnostics.Debug.WriteLine($"[ModLibraryImporter] cache invalidation failed for {nodeId}: {exception}");
			}
		}
	}

	private static ImportedObjectTree PlanManifestImport(ImportedObjectTree tree, StandardModManifest manifest)
	{
		var entries = BuildManifestEntries(manifest);
		var rootCandidate = tree.Nodes.Values.FirstOrDefault(node => string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath)));
		var rootId = rootCandidate?.Id
			?? (Guid.TryParse(manifest.Guid, out var parsedGuid)
				? new ModNodeId(parsedGuid)
				: ModNodeId.New());
		var hostGuid = rootId.Value.ToString("N");
		var nodes = new Dictionary<ModNodeId, ModNode>();
		if (rootCandidate is not null)
		{
			var rootEntry = entries.FirstOrDefault(entry => string.IsNullOrEmpty(entry.Path));
			var rootKind = rootCandidate.Metadata.Kind == ModNodeKind.ReShade && rootCandidate.PatchGroups.Count == 0
				? ModNodeKind.ReShade
				: ModNodeKind.Standard;
			var rootGuid = rootEntry?.Guid is not null && Guid.TryParse(rootEntry.Guid, out var explicitRoot)
				? new ModNodeId(explicitRoot)
				: rootId;
			nodes[rootGuid] = rootCandidate with
			{
				Id = rootGuid,
				Metadata = rootCandidate.Metadata with
				{
					Name = string.IsNullOrWhiteSpace(rootEntry?.Name) ? rootCandidate.Metadata.Name : rootEntry.Name!,
					Notes = rootEntry?.Notes ?? manifest.Description,
					Kind = rootKind,
					HostModGuids = null,
					SourcePackageGuid = manifest.Guid,
					SourcePackagePath = null,
				},
			};
			rootId = rootGuid;
			hostGuid = rootId.Value.ToString("N");
		}
		else
		{
			var rootEntry = entries.FirstOrDefault(entry => string.IsNullOrEmpty(entry.Path));
			nodes[rootId] = new ModNode(
				rootId,
				string.Empty,
				new ModNodeMetadata(
					string.IsNullOrWhiteSpace(rootEntry?.Name) ? tree.SourceDisplayName : rootEntry.Name!,
					rootEntry?.Notes ?? manifest.Description,
					DateTimeOffset.UtcNow,
					null,
					ModNodeKind.Standard,
					null,
					manifest.Guid),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
		}

		foreach (var node in tree.Nodes.Values)
		{
			var path = NormalizeRelativePath(node.RelativePath);
			if (string.IsNullOrEmpty(path)) continue;
			if (node.Metadata.Kind == ModNodeKind.Decoration)
			{
				nodes[node.Id] = node;
				continue;
			}

			var entry = FindManifestEntry(entries, path);
			if (entry?.Kind != ModNodeKind.Option) continue;
			var id = Guid.TryParse(entry.Guid, out var restored)
				? new ModNodeId(restored)
				: CreateStableNodeGuid(manifest.Guid ?? hostGuid, path) is var generated
					? new ModNodeId(generated)
					: node.Id;
			nodes[id] = node with
			{
				Id = id,
				Metadata = node.Metadata with
				{
					Name = string.IsNullOrWhiteSpace(entry.Name) ? node.Metadata.Name : entry.Name!,
					Notes = entry.Notes,
					Kind = ModNodeKind.Option,
					HostModGuids = [hostGuid],
					SourcePackageGuid = manifest.Guid,
					SourcePackagePath = path,
					OptionOrder = entry.OptionOrder,
				},
			};
		}

		return tree with { RootId = rootId, Nodes = nodes };
	}

	private static ManifestEntry? FindManifestEntry(IReadOnlyList<ManifestEntry> entries, string path)
	{
		// Prefer the exact path. This is the normal community-manifest layout.
		var exact = entries.FirstOrDefault(candidate =>
			string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
		if (exact is not null) return exact;

		if (string.IsNullOrEmpty(path)) return entries.FirstOrDefault(candidate => string.IsNullOrEmpty(candidate.Path));

		// Archives are often wrapped in one extra directory even though the
		// manifest paths are relative to the package root. Match a manifest path
		// against a suffix only when it is a complete path segment.
		var suffixMatches = entries
			.Where(candidate => !string.IsNullOrEmpty(candidate.Path)
				&& path.EndsWith('/' + candidate.Path, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(candidate => candidate.Path.Length)
			.ToArray();
		if (suffixMatches.Length > 0) return suffixMatches[0];

		// Some manifests include an option directory while the actual patch is
		// stored in one of its child directories. In that case the child remains
		// part of the same option branch and must not silently become a normal Mod.
		return entries
			.Where(candidate => candidate.Kind == ModNodeKind.Option
				&& !string.IsNullOrEmpty(candidate.Path)
				&& (path.StartsWith(candidate.Path + '/', StringComparison.OrdinalIgnoreCase)
					|| path.Contains('/' + candidate.Path + '/', StringComparison.OrdinalIgnoreCase)
					|| path.EndsWith('/' + candidate.Path, StringComparison.OrdinalIgnoreCase)))
			.OrderByDescending(candidate => candidate.Path.Length)
			.FirstOrDefault();
	}

	private static ImportedObjectTree PlanHeuristicImport(ImportedObjectTree tree, string sourceRootDirectory)
	{
		var payloadNodes = tree.Nodes.Values
			.Where(node => node.PatchGroups.Count > 0
				|| node.Metadata.Kind == ModNodeKind.Decoration
				|| (string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath))
					&& ExternalInstallFileFactsService.HasRecognizedFiles(sourceRootDirectory)))
			.OrderBy(node => NormalizeRelativePath(node.RelativePath), StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (payloadNodes.Length == 0) return tree with { Nodes = new Dictionary<ModNodeId, ModNode>() };
		if (payloadNodes.Length == 1)
		{
			var only = payloadNodes[0];
			var kind = GetHeuristicStandaloneKind(only, sourceRootDirectory);
			return tree with { RootId = only.Id, Nodes = new Dictionary<ModNodeId, ModNode> { [only.Id] = only with { Metadata = only.Metadata with { Kind = kind } } } };
		}

		var existingRoot = payloadNodes.FirstOrDefault(node => string.IsNullOrEmpty(NormalizeRelativePath(node.RelativePath)));
		var rootId = existingRoot?.Id ?? ModNodeId.New();
		var hostGuid = rootId.Value.ToString("N");
		var nodes = new Dictionary<ModNodeId, ModNode>();
		if (existingRoot is not null)
		{
			var rootKind = GetHeuristicStandaloneKind(existingRoot, sourceRootDirectory);
			nodes[rootId] = existingRoot with
			{
				Metadata = existingRoot.Metadata with
				{
					Kind = rootKind,
					HostModGuids = null,
					SourcePackagePath = null,
				},
			};
		}
		else
		{
			nodes[rootId] = new ModNode(
				rootId,
				string.Empty,
				new ModNodeMetadata(tree.SourceDisplayName, null, DateTimeOffset.UtcNow, null, ModNodeKind.Standard),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
		}

		foreach (var node in payloadNodes)
		{
			var path = NormalizeRelativePath(node.RelativePath);
			if (string.IsNullOrEmpty(path)) continue;
			if (node.Metadata.Kind == ModNodeKind.Decoration)
			{
				nodes[node.Id] = node;
				continue;
			}
			nodes[node.Id] = node with
			{
				Metadata = node.Metadata with
				{
					Kind = ModNodeKind.Option,
					HostModGuids = [hostGuid],
					SourcePackagePath = path,
				},
			};
		}
		return tree with { RootId = rootId, Nodes = nodes };
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
			new(string.Empty, manifest.Name, manifest.Description, manifest.IconPath, manifest.Guid, ModNodeKind.Standard, null, null),
		};
		var optionOrder = 0;
		foreach (var option in manifest.Options)
		{
			foreach (var include in option.Include ?? [])
			{
				var path = NormalizeRelativePath(include);
				if (!string.IsNullOrEmpty(path))
					result.Add(CreateOptionEntry(manifest, path, option.Name, option.Description, option.Image, optionOrder++));
			}
			foreach (var sub in option.SubOptions ?? [])
			foreach (var include in sub.Include ?? [])
			{
				var path = NormalizeRelativePath(include);
				if (!string.IsNullOrEmpty(path))
					result.Add(CreateOptionEntry(manifest, path, sub.Name, sub.Description, sub.Image, optionOrder++));
			}
		}
		return result;
	}

	private static ManifestEntry CreateOptionEntry(StandardModManifest manifest, string path, string? name, string? notes, string? image, int optionOrder)
	{
		var explicitGuid = FindNodeGuid(manifest, path);
		var stableGuid = Guid.TryParse(explicitGuid, out _)
			? explicitGuid
			: CreateStableNodeGuid(manifest.Guid, path).ToString("D");
		var hostGuid = Guid.TryParse(manifest.Guid, out var parsedHost) ? new[] { parsedHost.ToString("N") } : Array.Empty<string>();
		return new(path, name, notes, image, stableGuid, ModNodeKind.Option, hostGuid, optionOrder);
	}

	private static string? FindNodeGuid(StandardModManifest manifest, string path)
		=> manifest.Nodes?.FirstOrDefault(item => string.Equals(NormalizeRelativePath(item.RelativePath), path, StringComparison.OrdinalIgnoreCase))?.Guid;

	private static void CopyResolvedIcon(string sourceRoot, string destinationDirectory, string relativePath, StandardModManifest? manifest, CancellationToken cancellationToken)
	{
		if (manifest is null) return;
		var entries = BuildManifestEntries(manifest);
		var path = NormalizeRelativePath(relativePath);
		var entry = entries.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
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
		string Path,
		string? Name,
		string? Notes,
		string? Image,
		string? Guid,
		ModNodeKind Kind,
		IReadOnlyList<string>? HostModGuids,
	int? OptionOrder);

	private sealed record PackageRoot(string Directory, StandardModManifest? Manifest);

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
		};
		File.WriteAllText(
			Path.Combine(destinationDirectory, "option.json"),
			JsonSerializer.Serialize(relation, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
	}


	private static string NormalizeRelativePath(string? path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace('\\', '/').Trim('/');

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
			File.Copy(file, destination, overwrite: true);
		}
	}

	private static void CopyExternalInstallFiles(string sourceDir, string destDir, bool preferHardLinks, CancellationToken cancellationToken)
	{
		foreach (var sourcePath in EnumerateExternalFiles(sourceDir))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceDir, sourcePath);
			if (!ExternalInstallPathMapper.TryMap(relative, out _, out var normalizedSource, out _)) continue;
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
