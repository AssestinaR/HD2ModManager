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

		var root = Path.GetFullPath(modsRootDirectory);
		Directory.CreateDirectory(root);
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes);
		var existingByPath = nodes.Values.ToDictionary(node => NormalizePath(node.RelativePath), StringComparer.OrdinalIgnoreCase);
		var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var ignoredPaths = ignoredRelativePaths is null
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(ignoredRelativePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
		var added = new HashSet<ModNodeId>();
		var changed = new HashSet<ModNodeId>();
		var missing = new HashSet<ModNodeId>();

		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizePath(Path.GetRelativePath(root, directory));
			if (string.Equals(relativePath, ".hd2modmanager-trash", StringComparison.OrdinalIgnoreCase)
				|| ignoredPaths.Contains(relativePath))
			{
				// A crash between durable tombstone registration and snapshot commit must not
				// make an existing node look externally missing on the next startup.
				if (existingByPath.ContainsKey(relativePath)) seenPaths.Add(relativePath);
				continue;
			}
			seenPaths.Add(relativePath);
			var patchGroups = DiscoverPatchGroups(directory, cancellationToken);
			if (patchGroups.Count == 0) continue;
			var fingerprint = ComputeFingerprint(directory, cancellationToken);

			if (existingByPath.TryGetValue(relativePath, out var existing))
			{
				if (!string.Equals(existing.ContentFingerprint, fingerprint, StringComparison.Ordinal))
				{
					nodes[existing.Id] = existing with { PatchGroups = patchGroups, ContentFingerprint = fingerprint, Metadata = existing.Metadata with { ModifiedUtc = DateTimeOffset.UtcNow } };
					changed.Add(existing.Id);
				}
				continue;
			}

			var id = ModNodeId.New();
			nodes[id] = new ModNode(
				id,
				relativePath,
				new ModNodeMetadata(Path.GetFileName(directory), null, DateTimeOffset.UtcNow, null),
				patchGroups,
				Array.Empty<ModNodeId>(),
				fingerprint);
			added.Add(id);
		}

		foreach (var node in snapshot.Nodes.Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!seenPaths.Contains(NormalizePath(node.RelativePath)))
			{
				missing.Add(node.Id);
			}
		}

		var next = snapshot with { Nodes = nodes, SavedUtc = DateTimeOffset.UtcNow };
		return ValueTask.FromResult(new ModLibrarySynchronizationResult(next, added, changed, missing, added.Count > 0 || changed.Count > 0 || missing.Count > 0));
	}

	private IReadOnlyList<PatchGroupKey> DiscoverPatchGroups(string directory, CancellationToken cancellationToken)
	{
		var groups = new HashSet<PatchGroupKey>();
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_fileNameParser.TryParse(Path.GetFileName(path), out var info) && info is not null)
				groups.Add(new PatchGroupKey(info.ArchiveHex16, info.PatchIndex));
		}
		return groups.OrderBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.PatchIndex).ToArray();
	}

	private string ComputeFingerprint(string directory, CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => _fileNameParser.TryParse(Path.GetFileName(path), out _))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new FileInfo(path);
			builder.Append(info.Name.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	private static string NormalizePath(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
}
