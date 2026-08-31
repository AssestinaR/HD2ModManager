using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Domain.Manifest;

namespace HD2ModCore.Infrastructure;

// 作用：将库中指定主体及其递归选项树导出为可重新导入的 zip，并内置标准 manifest.json。
// Purpose: Exports a host and its recursive Option tree as a re-importable zip with a standard manifest.json.
public sealed class ModExporter : IModExporter
{
	private const int ManifestVersion = 1;
	private static readonly JsonSerializerOptions ManifestSerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		AllowTrailingCommas = false,
		PropertyNameCaseInsensitive = true,
		Converters =
		{
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
		},
	};

	private readonly StoragePaths _paths;

	public ModExporter(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<string> ExportToZipAsync(
		ModNodeId rootNodeId,
		LibrarySnapshot snapshot,
		string destinationDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(destinationDirectory));

		if (!snapshot.Nodes.TryGetValue(rootNodeId, out var root))
			throw new KeyNotFoundException($"Root node not found: {rootNodeId}");

		var exportTree = CollectSubtree(snapshot, rootNodeId);
		var packagePaths = exportTree.Nodes.ToDictionary(
			node => node.Id,
			node => GetPackagePath(rootNodeId, node.Id));
		var manifest = CreateManifest(root, exportTree, packagePaths);
		var manifestJson = JsonSerializer.Serialize(manifest, ManifestSerializerOptions);

		Directory.CreateDirectory(destinationDirectory);
		var zipName = SanitizeFileName(root.Metadata.Name);
		if (string.IsNullOrWhiteSpace(zipName)) zipName = "export";
		var zipPath = Path.Combine(destinationDirectory, zipName + ".zip");
		if (File.Exists(zipPath)) File.Delete(zipPath);

		using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
		{
			var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
			await using (var stream = manifestEntry.Open())
			await using (var writer = new StreamWriter(stream))
			{
				await writer.WriteAsync(manifestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
			}

			var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
			foreach (var node in exportTree.Nodes)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourceDirectory = ResolveNodeDirectory(node);
				var packageDirectory = packagePaths[node.Id];
				foreach (var payload in EnumeratePayloadFiles(sourceDirectory, cancellationToken))
				{
					var entryPath = CombineZipPath(packageDirectory, payload.RelativePath);
					if (!entries.Add(entryPath))
						throw new InvalidDataException($"导出内容产生重复的 zip 条目：{entryPath}");
					zip.CreateEntryFromFile(payload.FullPath, entryPath, CompressionLevel.Optimal);
				}
			}
		}

		return zipPath;
	}

	private ExportManifest CreateManifest(
		ModNode root,
		ExportTree tree,
		IReadOnlyDictionary<ModNodeId, string> packagePaths)
	{
		var nodeEntries = tree.Nodes
			.Select(node => new ExportManifestNode(
				RelativePath: packagePaths[node.Id],
				Name: node.Metadata.Name,
				Notes: node.Metadata.Notes,
				Guid: node.Id.Value.ToString("D")))
			.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var options = tree.ChildrenByParent.TryGetValue(root.Id, out var rootChildren)
			? rootChildren
				.Where(tree.NodeIds.Contains)
				.Select(optionId => tree.Nodes.First(option => option.Id == optionId))
				.Select(option => CreateManifestOption(option, tree, packagePaths))
			.ToArray()
			: Array.Empty<ExportManifestOption>();
		var rootIcon = FindIconPath(ResolveNodeDirectory(root));

		return new ExportManifest(
			Version: ManifestVersion,
			RootName: root.Metadata.Name,
			ExportedUtc: DateTimeOffset.UtcNow,
			Nodes: nodeEntries,
			Guid: root.Id.Value.ToString("D"),
			Name: root.Metadata.Name,
			Description: root.Metadata.Notes,
			IconPath: rootIcon is null ? null : CombineZipPath(packagePaths[root.Id], rootIcon),
			Options: options);
	}

	private ExportManifestOption CreateManifestOption(
		ModNode option,
		ExportTree tree,
		IReadOnlyDictionary<ModNodeId, string> packagePaths)
	{
		var icon = FindIconPath(ResolveNodeDirectory(option));
		var children = tree.ChildrenByParent.TryGetValue(option.Id, out var childIds)
			? childIds
				.Where(tree.NodeIds.Contains)
				.Select(childId => tree.Nodes.First(child => child.Id == childId))
				.Select(child => CreateManifestOption(child, tree, packagePaths))
				.ToArray()
			: Array.Empty<ExportManifestOption>();
		return new ExportManifestOption(
			Name: option.Metadata.Name,
			Description: option.Metadata.Notes,
			Image: icon is null ? null : CombineZipPath(packagePaths[option.Id], icon),
			Include: [packagePaths[option.Id]],
			SubOptions: children);
	}

	private ExportTree CollectSubtree(LibrarySnapshot snapshot, ModNodeId rootId)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		var nodes = new List<ModNode>();
		var childrenByParent = new Dictionary<ModNodeId, IReadOnlyList<ModNodeId>>();
		var visited = new HashSet<ModNodeId>();
		var active = new HashSet<ModNodeId>();

		Visit(rootId);
		var selectedIds = nodes.Select(node => node.Id).ToHashSet();
		foreach (var option in nodes.Where(node => node.Metadata.Kind == ModNodeKind.Option))
		{
			var outsideParents = graph.GetParents(option.Id).Where(parentId => !selectedIds.Contains(parentId)).ToArray();
			if (outsideParents.Length > 0)
				throw new InvalidDataException($"Exported Option {option.Id} has parents outside the selected tree.");
		}
		return new ExportTree(nodes, selectedIds, childrenByParent);

		void Visit(ModNodeId id)
		{
			if (!snapshot.Nodes.TryGetValue(id, out var node))
				throw new KeyNotFoundException($"Export tree references missing node: {id}");
			if (!active.Add(id))
				throw new InvalidDataException($"Export tree contains a cycle at node: {id}");
			if (!visited.Add(id))
				throw new InvalidDataException($"Export tree contains a shared Option that cannot be represented in one portable tree: {id}");

			nodes.Add(node);
			var children = GetOptionChildren(node);
			childrenByParent[node.Id] = children.Select(child => child.Id).ToArray();
			foreach (var child in children) Visit(child.Id);
			active.Remove(id);
		}

		IReadOnlyList<ModNode> GetOptionChildren(ModNode node)
		{
			var result = new List<ModNode>();
			var seen = new HashSet<ModNodeId>();
			foreach (var child in graph.GetDirectOptions(node.Id))
				if (seen.Add(child.Id)) result.Add(child);
			// Pre-graph snapshots stored some Option relationships in Children.
			foreach (var childId in node.Children)
			{
				if (!snapshot.Nodes.TryGetValue(childId, out var child) || child.Metadata.Kind != ModNodeKind.Option)
					continue;
				if (seen.Add(child.Id)) result.Add(child);
			}
			return result;
		}
	}

	private string ResolveNodeDirectory(ModNode node)
	{
		var libraryRoot = Path.GetFullPath(_paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var relative = NormalizeSafeRelativePath(node.RelativePath, $"node {node.Id}");
		if (string.IsNullOrEmpty(relative))
			throw new InvalidDataException($"Export node {node.Id} has no physical storage relative path.");
		var directory = Path.GetFullPath(Path.Combine(libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsWithin(libraryRoot, directory))
			throw new InvalidDataException($"Export node {node.Id} escapes the Mod library: {node.RelativePath}");
		EnsureNoReparsePointInPath(libraryRoot, directory);
		if (!Directory.Exists(directory))
			throw new DirectoryNotFoundException(directory);
		return directory;
	}

	private static IEnumerable<PayloadFile> EnumeratePayloadFiles(string root, CancellationToken cancellationToken)
	{
		var pending = new Stack<(string FullPath, string RelativePath)>();
		pending.Push((root, string.Empty));
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (directory, relativeDirectory) = pending.Pop();
			IEnumerable<string> files;
			IEnumerable<string> directories;
			try
			{
				files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
					.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
				directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
					.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new InvalidDataException($"Unable to enumerate export directory: {directory}", exception);
			}

			foreach (var file in files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				EnsureNotReparsePoint(file);
				var fileName = Path.GetFileName(file);
				// The generated manifest owns the package-root manifest.json name.
				if (relativeDirectory.Length == 0 && string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
					continue;
				var relative = string.IsNullOrEmpty(relativeDirectory)
					? fileName
					: relativeDirectory + "/" + fileName;
				yield return new PayloadFile(file, NormalizeSafeRelativePath(relative, file));
			}

			foreach (var child in directories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(child);
				if (string.Equals(name, "Overwrite", StringComparison.OrdinalIgnoreCase)) continue;
				EnsureNotReparsePoint(child);
				var relative = string.IsNullOrEmpty(relativeDirectory) ? name : relativeDirectory + "/" + name;
				pending.Push((child, NormalizeSafeRelativePath(relative, child)));
			}
		}
	}

	private static string? FindIconPath(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
				.Where(path => Path.GetFileName(path).StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
				.Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.Select(Path.GetFileName)
				.FirstOrDefault();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new InvalidDataException($"Unable to inspect icon files in export directory: {directory}", exception);
		}
	}

	private static string GetPackagePath(ModNodeId rootId, ModNodeId nodeId)
		=> rootId == nodeId ? string.Empty : "options/" + nodeId.Value.ToString("N");

	private static string CombineZipPath(string directory, string file)
		=> string.IsNullOrEmpty(directory) ? file : directory + "/" + file;

	private static string NormalizeSafeRelativePath(string? path, string subject)
	{
		var raw = (path ?? string.Empty).Replace('\\', '/');
		if (raw.StartsWith("/", StringComparison.Ordinal) || raw.Contains(':'))
			throw new InvalidDataException($"Unsafe relative path for {subject}: {path}");
		var normalized = raw.Trim('/');
		if (string.IsNullOrEmpty(normalized)) return string.Empty;
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(segment => segment is "." or ".."))
			throw new InvalidDataException($"Unsafe relative path for {subject}: {path}");
		return string.Join('/', segments);
	}

	private static void EnsureNoReparsePointInPath(string root, string candidate)
	{
		var relative = Path.GetRelativePath(root, candidate);
		var current = root;
		if (IsReparsePoint(current))
			throw new IOException($"Export path is a reparse point: {current}");
		foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			if (Directory.Exists(current) && IsReparsePoint(current))
				throw new IOException($"Export path contains a reparse point: {current}");
		}
	}

	private static void EnsureNotReparsePoint(string path)
	{
		if (IsReparsePoint(path)) throw new IOException($"Export payload is a reparse point: {path}");
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static bool IsWithin(string root, string candidate)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static string SanitizeFileName(string name)
	{
		foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
		return name.Trim();
	}

	private sealed record ExportTree(
		IReadOnlyList<ModNode> Nodes,
		IReadOnlySet<ModNodeId> NodeIds,
		IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNodeId>> ChildrenByParent);
	private sealed record PayloadFile(string FullPath, string RelativePath);
}
