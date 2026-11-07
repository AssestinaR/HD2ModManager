using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Infrastructure;

// 作用：按 Mod 库顶层目录与有效 patch 文件对账，发现外部新增和缺失节点。
// Purpose: Reconciles top-level library directories and valid patch files.
public sealed class ModLibrarySynchronizer : IModLibrarySynchronizer
{
	private readonly IPatchFileNameParser _fileNameParser;

	public ModLibrarySynchronizer(IPatchFileNameParser fileNameParser)
	{
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
	}

	public ValueTask<ModLibrarySynchronizationResult> SynchronizeAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		Directory.CreateDirectory(Path.GetFullPath(modsRootDirectory));
		return SynchronizeCoreAsync(snapshot, modsRootDirectory, ignoredRelativePaths, cancellationToken);
	}

	public async ValueTask<ModLibrarySynchronizationInspection> InspectAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		var root = Path.GetFullPath(modsRootDirectory);
		if (!Directory.Exists(root))
			throw new DirectoryNotFoundException($"Mod library directory does not exist: {root}");

		var ignoredPaths = NormalizeIgnoredPaths(ignoredRelativePaths);
		var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var directories = new List<ModLibrarySynchronizationDirectory>();
		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizePath(Path.GetRelativePath(root, directory));
			if (string.Equals(relativePath, ".hd2modmanager-trash", StringComparison.OrdinalIgnoreCase)
				|| ignoredPaths.Contains(relativePath))
				continue;
			seenPaths.Add(relativePath);
			var patchGroups = DiscoverPatchGroups(directory, cancellationToken);
			var logicalNodeId = ModNodeStorageProtocol.TryReadLogicalNodeId(directory, out var parsedLogicalNodeId)
				? parsedLogicalNodeId
				: (ModNodeId?)null;
			// A stale logical marker must never suppress real Patch deployment.
			// Treat the directory as payload-bearing as soon as it contains a base
			// Patch; the marker remains discoverable for repair but no longer changes
			// the node's deployment semantics.
			var isLogicalContainer = logicalNodeId.HasValue && patchGroups.Count == 0;
			if (patchGroups.Count == 0 && !isLogicalContainer) continue;
			// 文件夹里的设置文件是**权威**：名字、备注、持久 id 都以它为准。
			// library.json 只是索引——把它当权威的话，把文件夹搬到另一个库就会丢名字。
			var settings = ModSettingsFile.TryRead(directory);
			directories.Add(new ModLibrarySynchronizationDirectory(
				relativePath,
				string.IsNullOrWhiteSpace(settings?.Name) ? Path.GetFileName(directory) : settings!.Name!,
				patchGroups,
				ComputeFingerprint(directory, cancellationToken),
				isLogicalContainer,
				logicalNodeId,
				settings?.Notes,
				TryParseNodeId(settings?.Id),
				HasSettings: settings is not null));
		}
		return new ModLibrarySynchronizationInspection(
			directories.OrderBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
			seenPaths,
			ignoredPaths);
	}

	public ModLibrarySynchronizationResult ApplyInspection(
		LibrarySnapshot snapshot,
		ModLibrarySynchronizationInspection inspection,
		IReadOnlyDictionary<string, ModNodeId>? addedNodeIdsByRelativePath = null,
		DateTimeOffset? timestampUtc = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(inspection);
		var timestamp = timestampUtc ?? DateTimeOffset.UtcNow;
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes);
		var existingByPath = nodes.Values.ToDictionary(node => NormalizePath(node.RelativePath), StringComparer.OrdinalIgnoreCase);
		var directoriesByPath = inspection.Directories.ToDictionary(directory => NormalizePath(directory.RelativePath), StringComparer.OrdinalIgnoreCase);
		var plannedIds = NormalizeAddedNodeIds(addedNodeIdsByRelativePath, snapshot.Nodes);
		var added = new HashSet<ModNodeId>();
		var changed = new HashSet<ModNodeId>();
		var missing = new HashSet<ModNodeId>();

		foreach (var directory in inspection.Directories)
		{
			var relativePath = NormalizePath(directory.RelativePath);
			if (existingByPath.TryGetValue(relativePath, out var existing))
			{
				if (!string.Equals(existing.ContentFingerprint, directory.ContentFingerprint, StringComparison.Ordinal)
					|| !PatchGroupsEqual(existing.PatchGroups, directory.PatchGroups)
					|| existing.Metadata.IsLogicalContainer != directory.IsLogicalContainer
					// 只有在文件夹**确实有设置文件**时才比名字/备注：否则"没有设置文件"会被读成
					// "备注是 null"，下一次扫描就把用户的备注抹掉了。
					|| (directory.HasSettings
						&& (!string.Equals(existing.Metadata.Name, directory.Name, StringComparison.Ordinal)
							|| !string.Equals(existing.Metadata.Notes, directory.Notes, StringComparison.Ordinal))))
				{
					nodes[existing.Id] = existing with
					{
						PatchGroups = directory.PatchGroups,
						ContentFingerprint = directory.ContentFingerprint,
						Metadata = existing.Metadata with
						{
							ModifiedUtc = timestamp,
							IsLogicalContainer = directory.IsLogicalContainer,
							// 名字/备注以文件夹为准（用户可能直接在别的库里改过、又把文件夹搬回来）。
							Name = directory.HasSettings ? directory.Name : existing.Metadata.Name,
							Notes = directory.HasSettings ? directory.Notes : existing.Metadata.Notes,
						}
					};
					changed.Add(existing.Id);
				}
				continue;
			}

			var id = directory.SettingsNodeId
				?? (plannedIds.TryGetValue(relativePath, out var plannedId)
					? plannedId
					: directory.LogicalNodeId ?? ModNodeId.New());
			if (nodes.ContainsKey(id))
			{
				// A stale logical marker can survive a deleted/re-imported Mod.  The
				// directory is still valid storage and must be recoverable on reload;
				// allocate a fresh library identity instead of aborting the whole scan.
				// The physical marker is intentionally left untouched here because this
				// reconciliation method is also used as a pure inspection operation.
				id = ModNodeId.New();
				while (nodes.ContainsKey(id)) id = ModNodeId.New();
			}
			nodes[id] = new ModNode(
				id,
				relativePath,
				new ModNodeMetadata(directory.Name, directory.Notes, timestamp, null, ModNodeKind.Standard,
					IsLogicalContainer: directory.IsLogicalContainer),
				directory.PatchGroups,
				Array.Empty<ModNodeId>(),
				directory.ContentFingerprint);
			added.Add(id);
		}

		foreach (var node in snapshot.Nodes.Values)
		{
			var relativePath = NormalizePath(node.RelativePath);
			if (inspection.IgnoredRelativePaths.Contains(relativePath)) continue;
			if (!inspection.SeenRelativePaths.Contains(relativePath))
			{
				missing.Add(node.Id);
				continue;
			}
			if (!directoriesByPath.ContainsKey(relativePath)
				&& (node.PatchGroups.Count != 0 || node.Metadata.IsLogicalContainer))
			{
				nodes[node.Id] = node with
				{
					PatchGroups = Array.Empty<PatchGroupKey>(),
					ContentFingerprint = ComputeEmptyFingerprint(),
					Metadata = node.Metadata with
					{
						ModifiedUtc = timestamp,
						IsLogicalContainer = false,
					}
				};
				changed.Add(node.Id);
			}
		}

		var hasSnapshotChanges = added.Count != 0 || changed.Count != 0;
		var next = hasSnapshotChanges
			? snapshot with { Nodes = nodes, SavedUtc = timestamp }
			: snapshot;
		return new ModLibrarySynchronizationResult(next, added, changed, missing, hasSnapshotChanges || missing.Count != 0)
		{
			HasSnapshotChanges = hasSnapshotChanges,
		};
	}

	private async ValueTask<ModLibrarySynchronizationResult> SynchronizeCoreAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? ignoredRelativePaths,
		CancellationToken cancellationToken)
	{
		var inspection = await InspectAsync(snapshot, modsRootDirectory, ignoredRelativePaths, cancellationToken).ConfigureAwait(false);
		return ApplyInspection(snapshot, inspection);
	}

	// 载荷目录包含 `vpk2patch/`（见 ModNodePayloadLayout）。少了它，一个"patch 全在
	// vpk2patch 里"的 Mod 在重新载入库时会被当成空目录而丢失。
	private IReadOnlyList<PatchGroupKey> DiscoverPatchGroups(string directory, CancellationToken cancellationToken)
	{
		var groups = new HashSet<PatchGroupKey>();
		foreach (var path in ModNodePayloadLayout.EnumeratePayloadFiles(directory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_fileNameParser.TryParse(Path.GetFileName(path), out var info)
				&& info?.SidecarKind == PatchSidecarKind.Base)
				groups.Add(new PatchGroupKey(info.ArchiveHex16, info.PatchIndex));
		}
		return groups.OrderBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.PatchIndex).ToArray();
	}

	private string ComputeFingerprint(string directory, CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		foreach (var path in ModNodePayloadLayout.EnumeratePayloadFiles(directory)
			.Where(path => _fileNameParser.TryParse(Path.GetFileName(path), out _))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new FileInfo(path);
			var relative = NormalizeLayerName(Path.GetRelativePath(directory, path).Replace('\\', '/'));
			builder.Append(relative.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	/// <summary>
	/// 把基底目录的**旧名**归一化成新名，再进指纹。
	/// <para>
	/// 目录从 <c>vpk2patch/</c> 改名成 <c>sectional/</c> 不代表内容变了，但相对路径进过指纹：
	/// 不改归一化的话，一次性改名会把用户刚生成、刚审阅过的库对账计划当场作废
	/// （实测报 <c>LibrarySyncInputChanged</c>，而改名其实已经发生了）。
	/// </para>
	/// <para>
	/// 只映射旧名 → 新名，所以已经在用新名的 Mod 指纹一个字都不变，不会造成额外 churn。
	/// </para>
	/// </summary>
	private static string NormalizeLayerName(string relative)
	{
		var legacy = ModNodePayloadLayout.LegacyVpkPatchDirectoryName + "/";
		var current = ModNodePayloadLayout.VpkPatchDirectoryName + "/";
		return relative.StartsWith(legacy, StringComparison.OrdinalIgnoreCase)
			? current + relative[legacy.Length..]
			: relative;
	}

	private static string NormalizePath(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace(Path.DirectorySeparatorChar, '/').Trim('/');

	private static IReadOnlySet<string> NormalizeIgnoredPaths(IReadOnlySet<string>? ignoredRelativePaths)
		=> ignoredRelativePaths is null
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(ignoredRelativePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyDictionary<string, ModNodeId> NormalizeAddedNodeIds(
		IReadOnlyDictionary<string, ModNodeId>? addedNodeIdsByRelativePath,
		IReadOnlyDictionary<ModNodeId, ModNode> existingNodes)
	{
		if (addedNodeIdsByRelativePath is null || addedNodeIdsByRelativePath.Count == 0)
			return new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		var result = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		var ids = new HashSet<ModNodeId>();
		foreach (var (rawPath, id) in addedNodeIdsByRelativePath)
		{
			var path = NormalizePath(rawPath);
			if (string.IsNullOrWhiteSpace(path))
				throw new InvalidOperationException("A planned synchronization node path cannot be empty.");
			if (!result.TryAdd(path, id))
				throw new InvalidOperationException($"A planned synchronization path was provided more than once: {path}");
			if (!ids.Add(id))
				throw new InvalidOperationException($"A planned synchronization node ID was reused: {id.Value:N}");
			if (existingNodes.ContainsKey(id))
				throw new InvalidOperationException($"A planned synchronization node ID already exists: {id.Value:N}");
		}
		return result;
	}

	private static bool PatchGroupsEqual(IReadOnlyList<PatchGroupKey> left, IReadOnlyList<PatchGroupKey> right)
		=> left.Count == right.Count
			&& left.Zip(right).All(pair => string.Equals(pair.First.ArchiveHex16, pair.Second.ArchiveHex16, StringComparison.OrdinalIgnoreCase)
				&& pair.First.PatchIndex == pair.Second.PatchIndex);

	private static string ComputeEmptyFingerprint()
		=> Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

	private static ModNodeId? TryParseNodeId(string? value)
		=> !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "N", out var parsed)
			? new ModNodeId(parsed)
			: null;
}
