using HD2ModCore.Application;
using HD2ModCore.Domain;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// 作用：从文件夹递归扫描 patch 文件，并将每个真实含 patch 的目录拆成一个扁平 mod 节点。
// Purpose: Recursively scans deployable patch directories and portable decoration-package directories into flat library nodes.
public sealed class ObjectTreeImporter : IObjectTreeImporter
{
	private readonly IPatchFileNameParser _fileNameParser;

	public ObjectTreeImporter(IPatchFileNameParser fileNameParser)
	{
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
	}

	public ValueTask<ImportedObjectTree> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(folderPath))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(folderPath));
		}

		var rootDir = new DirectoryInfo(folderPath);
		if (!rootDir.Exists)
		{
			throw new DirectoryNotFoundException(folderPath);
		}

		var nodes = new Dictionary<ModNodeId, ModNode>();
		foreach (var dir in EnumerateDirectoriesDepthFirst(rootDir))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var patchGroups = CollectPatchGroups(dir);
			var isDecoration = IsDecorationPackageDirectory(dir);
			var option = !isDecoration ? TryReadOptionRelation(dir) : null;
			// Logical markers describe zero-payload structure only. If a user later
			// places a real Patch in the directory, preserve the Patch as deployable
			// instead of silently importing it as a skipped logical anchor.
			var isLogicalContainer = !isDecoration
				&& patchGroups.Count == 0
				&& ModNodeStorageProtocol.IsLogicalDirectory(dir.FullName);
			var hasExternalInstallFiles = dir.FullName.Equals(rootDir.FullName, StringComparison.OrdinalIgnoreCase)
				&& ExternalInstallFileFactsService.HasRecognizedFiles(dir.FullName);
			if (patchGroups.Count == 0 && !isDecoration && option is null && !hasExternalInstallFiles && !isLogicalContainer)
			{
				continue;
			}

			var rel = Path.GetRelativePath(rootDir.FullName, dir.FullName);
			if (string.IsNullOrEmpty(rel) || rel == ".")
			{
				rel = string.Empty;
			}

			var id = ModNodeId.New();
			var kind = isDecoration
				? ModNodeKind.Decoration
				: option is not null
					? ModNodeKind.Option
					: hasExternalInstallFiles && patchGroups.Count == 0
						? ModNodeKind.ReShade
						: ModNodeKind.Standard;
			var metadata = new ModNodeMetadata(
				Name: BuildFlatName(rootDir.Name, rel),
				Notes: null,
				CreatedUtc: DateTimeOffset.UtcNow,
				ModifiedUtc: null,
				Kind: kind,
				HostModGuids: option?.HostModGuids,
				SourcePackageGuid: option?.SourcePackageGuid,
				SourcePackagePath: option?.SourcePath,
				OptionOrder: option?.OptionOrder,
				SuppressDefaultDirectOptionActivation: option?.SuppressDefaultDirectOptionActivation ?? false,
				IsLogicalContainer: isLogicalContainer || option is not null && patchGroups.Count == 0);

			nodes[id] = new ModNode(
				Id: id,
				RelativePath: rel,
				Metadata: metadata,
				PatchGroups: patchGroups,
				Children: Array.Empty<ModNodeId>());
		}

		var rootId = nodes.Count > 0 ? nodes.Keys.First() : ModNodeId.New();

		var tree = new ImportedObjectTree(
			RootId: rootId,
			Nodes: nodes,
			SourceDisplayName: rootDir.Name);

		return ValueTask.FromResult(tree);
	}

    private static bool IsDecorationPackageDirectory(DirectoryInfo dir)
        => File.Exists(Path.Combine(dir.FullName, "decoration.json"));

	private static OptionRelationDocument? TryReadOptionRelation(DirectoryInfo dir)
	{
		var path = Path.Combine(dir.FullName, "option.json");
		if (!File.Exists(path)) return null;
		try
		{
			var relation = JsonSerializer.Deserialize<OptionRelationDocument>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
			{
				PropertyNameCaseInsensitive = true,
			});
			return relation is { HostModGuids.Count: > 0 } && string.Equals(relation.Kind, "Option", StringComparison.OrdinalIgnoreCase)
				? relation
				: null;
		}
		catch (JsonException) { return null; }
		catch (IOException) { return null; }
	}

	// 载荷目录 = 节点根 + （存在时的）`vpk2patch/`。后者是 vpk2patch 转换产物的家：
	// 那些 patch 本来就不在根上，所以必须算进这个节点的内容，否则「导入了但一个 patch 都没有」。
	private List<PatchGroupKey> CollectPatchGroups(DirectoryInfo dir)
	{
		var patchGroups = new HashSet<PatchGroupKey>();
		foreach (var file in ModNodePayloadLayout.EnumeratePayloadFiles(dir.FullName))
		{
			if (!_fileNameParser.TryParse(Path.GetFileName(file), out var info) || info is null)
			{
				continue;
			}

			patchGroups.Add(new PatchGroupKey(info.ArchiveHex16, info.PatchIndex));
		}
		return patchGroups.OrderBy(g => g.ArchiveHex16, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.PatchIndex).ToList();
	}

	private static IEnumerable<DirectoryInfo> EnumerateDirectoriesDepthFirst(DirectoryInfo root)
	{
		yield return root;
		foreach (var child in SafeEnumerateDirectories(root))
		{
			if (IsReparsePoint(child)) continue;
			// `vpk2patch/` 不是一个独立选项，它是父节点自己的载荷（见 ModNodePayloadLayout）。
			// 把它的 patch 再拆成一个节点，会让同一批 patch 被部署两次。
			if (ModNodePayloadLayout.IsVpkPatchDirectoryName(child.Name)) continue;
			foreach (var nested in EnumerateDirectoriesDepthFirst(child))
			{
				yield return nested;
			}
		}
	}

	private static string BuildFlatName(string sourceName, string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			return sourceName;
		}

		var parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
		return string.Join('-', new[] { sourceName }.Concat(parts));
	}

	private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo dir)
	{
		try
		{
			return dir.EnumerateDirectories();
		}
		catch
		{
			return Array.Empty<DirectoryInfo>();
		}
	}

	private static bool IsReparsePoint(FileSystemInfo entry)
	{
		try { return (entry.Attributes & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}
}
