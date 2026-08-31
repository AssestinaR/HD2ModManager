using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public sealed record CliModExportPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliModExportRequest Request,
	CliModExportTarget Target,
	string LibraryJsonSha256,
	IReadOnlyList<CliModExportNode> Nodes,
	IReadOnlyList<CliModExportDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.mod-export-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> Nodes.Count != 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliModExportRequest(string NodeId);

public sealed record CliModExportTarget(
	string ManagerRoot,
	string LibraryRoot);

public sealed record CliModExportNode(
	string NodeId,
	string RelativePath,
	string Name,
	string? Notes,
	IReadOnlyList<string> ChildNodeIds,
	IReadOnlyList<CliModExportFileSnapshot> Files,
	string PackagePath);

public sealed record CliModExportFileSnapshot(
	string RelativePath,
	string FileName,
	bool Exists,
	long Length,
	string? Sha256,
	string ArchiveRelativePath);

public sealed record CliModExportDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliModExportBuildResult(
	CliModExportPlanDocument Document,
	CliLibraryState State,
	ModNode Root,
	StoragePaths Paths);

internal sealed record CliModExportValidationResult(
	CliModExportPlanDocument Document,
	CliModExportBuildResult? Current,
	IReadOnlyList<CliModExportDiagnostic> Diagnostics)
{
	public bool IsValid
		=> Current is not null
			&& Current.Document.IsReady
			&& !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// Freezes one library node and its recursive Option tree, including every
/// payload file that will be written to the portable archive. The package root
/// is the selected host; each Option receives its own stable directory.
/// </summary>
internal static class CliModExportPlanArtifacts
{
	public static async ValueTask<CliModExportBuildResult> BuildAsync(
		CliWorkspace workspace,
		string rootSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(rootSelector))
			throw new CliUserException("ModExportRootRequired", "mod export plan 需要一个根 Mod GUID 或唯一 GUID 前缀。 ");
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("ModExportManagerRootRequired", "mod export plan 需要 --manager-root <管理器目录>。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "mod export plan 需要明确的 Mod 库。 ");
		if (!Directory.Exists(workspace.LibraryRoot))
			throw new CliUserException("LibraryDirectoryNotFound", $"Mod 库目录不存在：{workspace.LibraryRoot}");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var diagnostics = new List<CliModExportDiagnostic>();
		var root = CliProjections.ResolveMod(state.Snapshot.Nodes, rootSelector);
		var tree = CollectSubtree(state.Snapshot, root.Id, diagnostics);
		ValidateExternalParents(state.Snapshot, tree, diagnostics);

		var libraryHash = await ComputeFileSha256Async(paths.LibraryPath, cancellationToken).ConfigureAwait(false);
		var beforeHash = libraryHash;
		var projectedNodes = new List<CliModExportNode>();
		var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var seenSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var seenArchiveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var node in tree.Nodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizeSafeRelativePath(node.RelativePath, $"节点 {CliProjections.FormatId(node.Id)}", diagnostics, node);
			var packagePath = GetPackagePath(root.Id, node.Id);
			var directory = ResolveNodeDirectory(paths.ModsDirectory, node, relativePath, diagnostics);
			var files = directory is null
				? Array.Empty<CliModExportFileSnapshot>()
				: await CaptureFilesAsync(
					paths.ModsDirectory,
					directory,
					packagePath,
					node,
					seenSourceFiles,
					seenArchiveFiles,
					diagnostics,
					cancellationToken).ConfigureAwait(false);

			if (!seenRelativePaths.Add(relativePath))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportRelativePathConflict", "error", true,
					$"导出树中有多个 Mod 使用相同的相对路径：{relativePath}。",
					NodeId: CliProjections.FormatId(node.Id)));
			}

			projectedNodes.Add(new CliModExportNode(
				CliProjections.FormatId(node.Id),
				relativePath,
				node.Metadata.Name,
				node.Metadata.Notes,
				tree.ChildrenByParent.TryGetValue(node.Id, out var children)
					? children.Select(CliProjections.FormatId).ToArray()
					: Array.Empty<string>(),
				files,
				packagePath));
		}

		var afterHash = await ComputeFileSha256Async(paths.LibraryPath, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliModExportDiagnostic(
				"ModExportLibraryChangedDuringInspection", "error", true,
				"library.json 在导出计划扫描期间发生变化；请等待其它操作完成后重新生成计划。",
				paths.LibraryPath));
			libraryHash = afterHash;
		}

		var request = new CliModExportRequest(CliProjections.FormatId(root.Id));
		var target = new CliModExportTarget(Path.GetFullPath(workspace.ManagerRoot), Path.GetFullPath(workspace.LibraryRoot));
		var orderedNodes = projectedNodes
			.OrderBy(node => node.PackagePath.Length == 0 ? 0 : 1)
			.ThenBy(node => node.PackagePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var normalizedDiagnostics = DistinctDiagnostics(diagnostics);
		var document = new CliModExportPlanDocument(
			CliModExportPlanDocument.CurrentSchema,
			CliModExportPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeFingerprint(request, target, libraryHash, orderedNodes, normalizedDiagnostics),
			request,
			target,
			libraryHash,
			orderedNodes,
			normalizedDiagnostics);
		return new CliModExportBuildResult(document, state, root, paths);
	}

	public static async ValueTask<CliModExportPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("ModExportPlanRequired", "请通过 --plan <Mod导出计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "ModExportPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("ModExportPlanNotFound", $"Mod 导出计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliModExportPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null
				|| !string.Equals(document.Schema, CliModExportPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliModExportPlanDocument.CurrentVersion
				|| string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.NodeId)
				|| document.Target is null
				|| string.IsNullOrWhiteSpace(document.Target.ManagerRoot)
				|| string.IsNullOrWhiteSpace(document.Target.LibraryRoot)
				|| string.IsNullOrWhiteSpace(document.LibraryJsonSha256)
				|| document.Nodes is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划缺少目标、根节点、库快照或文件清单。 ");
			}
			if (!Guid.TryParse(document.Request.NodeId, out _))
				throw new CliUserException("ModExportPlanUnreadable", $"Mod 导出计划包含无效根节点 ID：{document.Request.NodeId}");

			var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var rootCount = 0;
			foreach (var node in document.Nodes)
			{
				if (node is null || !Guid.TryParse(node.NodeId, out _)
					|| node.Files is null || node.ChildNodeIds is null
					|| !nodeIds.Add(node.NodeId))
					throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划包含无效或重复节点。 ");
				var packagePath = ValidateSafeRelativePath(node.PackagePath, allowEmpty: true, "节点 PackagePath");
				if (!packagePaths.Add(packagePath))
					throw new CliUserException("ModExportPlanUnreadable", $"Mod 导出计划包含重复 PackagePath：{packagePath}");
				if (packagePath.Length == 0) rootCount++;
				_ = ValidateSafeRelativePath(node.RelativePath, allowEmpty: true, "节点 RelativePath");
				foreach (var childId in node.ChildNodeIds)
					if (!Guid.TryParse(childId, out _))
						throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划包含无效子节点 ID。 ");
				foreach (var file in node.Files)
				{
					if (file is null || !file.Exists || file.Length < 0
						|| string.IsNullOrWhiteSpace(file.FileName)
						|| string.IsNullOrWhiteSpace(file.Sha256)
						|| !IsSha256(file.Sha256)
						|| !file.FileName.Equals(Path.GetFileName(file.RelativePath), StringComparison.Ordinal))
						throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划包含无效文件快照。 ");
					var sourcePath = ValidateSafeRelativePath(file.RelativePath, allowEmpty: false, "文件 RelativePath");
					var archivePath = ValidateSafeRelativePath(file.ArchiveRelativePath, allowEmpty: false, "文件 ArchiveRelativePath");
					if (!filePaths.Add(sourcePath) || !archivePaths.Add(archivePath))
						throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划包含重复文件条目。 ");
				}
			}
			if (rootCount != 1 || !nodeIds.Contains(document.Request.NodeId))
				throw new CliUserException("ModExportPlanUnreadable", "Mod 导出计划必须包含且只能包含一个根节点。 ");
			foreach (var node in document.Nodes)
				foreach (var childId in node.ChildNodeIds)
					if (!nodeIds.Contains(childId))
						throw new CliUserException("ModExportPlanUnreadable", $"Mod 导出计划引用了不存在的子节点：{childId}");

			var declared = ComputeFingerprint(
				document.Request,
				document.Target,
				document.LibraryJsonSha256,
				document.Nodes,
				document.Diagnostics);
			if (!string.Equals(declared, document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException("ModExportPlanIntegrityMismatch", "Mod 导出计划在写出后被修改；请重新生成计划。 ");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("ModExportPlanUnreadable", $"无法读取 Mod 导出计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliModExportValidationResult> ValidateAsync(
		CliModExportPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliModExportDiagnostic>();
		CliModExportBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Request.NodeId, cancellationToken).ConfigureAwait(false);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportTargetChanged", "error", true,
					"当前管理器或 Mod 库目标与导出计划不同；请重新生成计划。"));
			}
			if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportInputChanged", "error", true,
					"根 Mod、选项树、library.json 或源文件内容已变化；请重新生成导出计划并重新确认。"));
			}
			diagnostics.AddRange(current.Document.Diagnostics);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliModExportDiagnostic(exception.Code, "error", true, exception.Message));
		}
		return new CliModExportValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static CliModExportTree CollectSubtree(
		LibrarySnapshot snapshot,
		ModNodeId rootId,
		ICollection<CliModExportDiagnostic> diagnostics)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		var result = new List<ModNode>();
		var childrenByParent = new Dictionary<ModNodeId, IReadOnlyList<ModNodeId>>();
		var visited = new HashSet<ModNodeId>();
		var active = new HashSet<ModNodeId>();

		Visit(rootId);
		return new CliModExportTree(result, result.Select(node => node.Id).ToHashSet(), childrenByParent);

		void Visit(ModNodeId id)
		{
			if (!snapshot.Nodes.TryGetValue(id, out var node))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportChildMissing", "error", true,
					$"导出树引用了不存在的子 Mod：{CliProjections.FormatId(id)}。",
					NodeId: CliProjections.FormatId(id)));
				return;
			}
			if (!active.Add(id))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportTreeCycle", "error", true,
					$"导出树包含循环引用，节点 {CliProjections.FormatId(id)} 被重复访问。",
					NodeId: CliProjections.FormatId(id)));
				return;
			}
			if (!visited.Add(id))
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportSharedOption", "error", true,
					$"选项 {CliProjections.FormatId(id)} 同时属于导出树中的多个父节点，无法无损表示为单一递归选项树。",
					NodeId: CliProjections.FormatId(id)));
				active.Remove(id);
				return;
			}

			result.Add(node);
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
			// Very old snapshots stored Option relationships in Children. Keep that
			// compatibility path, but never treat an ordinary child as an Option.
			foreach (var childId in node.Children)
			{
				if (!snapshot.Nodes.TryGetValue(childId, out var child) || child.Metadata.Kind != ModNodeKind.Option)
					continue;
				if (seen.Add(child.Id)) result.Add(child);
			}
			return result;
		}
	}

	private static void ValidateExternalParents(
		LibrarySnapshot snapshot,
		CliModExportTree tree,
		ICollection<CliModExportDiagnostic> diagnostics)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		foreach (var node in tree.Nodes.Where(node => node.Metadata.Kind == ModNodeKind.Option))
		{
			var outsideParents = graph.GetParents(node.Id)
				.Where(parentId => !tree.NodeIds.Contains(parentId))
				.ToArray();
			if (outsideParents.Length == 0) continue;
			diagnostics.Add(new CliModExportDiagnostic(
				"ModExportOptionHasExternalParent", "error", true,
				$"选项“{node.Metadata.Name}”还属于导出范围之外的主体，导出后无法保留这条共享关系：{string.Join(", ", outsideParents.Select(CliProjections.FormatId))}。",
				NodeId: CliProjections.FormatId(node.Id)));
		}
	}

	private static string? ResolveNodeDirectory(
		string modsRoot,
		ModNode node,
		string relativePath,
		ICollection<CliModExportDiagnostic> diagnostics)
	{
		try
		{
			var root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.IsNullOrEmpty(relativePath))
				throw new InvalidDataException("节点没有物理存储相对路径。");
			var directory = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (!IsWithin(root, directory))
				throw new InvalidDataException($"节点相对路径越出 Mod 库：{node.RelativePath}");
			if (HasReparsePointInPath(root, directory))
				throw new IOException($"节点物理路径包含链接或其它 reparse point：{directory}");
			if (!Directory.Exists(directory))
				throw new DirectoryNotFoundException(directory);
			return directory;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			diagnostics.Add(new CliModExportDiagnostic(
				exception is DirectoryNotFoundException ? "ModExportStorageMissing" : "ModExportStorageUnreadable",
				"error", true,
				$"无法安全读取节点“{node.Metadata.Name}”的物理目录：{exception.Message}",
				Path.Combine(modsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
				CliProjections.FormatId(node.Id),
				exception.ToString()));
			return null;
		}
	}

	private static async ValueTask<IReadOnlyList<CliModExportFileSnapshot>> CaptureFilesAsync(
		string modsRoot,
		string directory,
		string packagePath,
		ModNode node,
		ISet<string> seenSourceFiles,
		ISet<string> seenArchiveFiles,
		ICollection<CliModExportDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var result = new List<CliModExportFileSnapshot>();
		var pending = new Stack<(string FullPath, string RelativePath)>();
		pending.Push((directory, string.Empty));
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (currentDirectory, currentRelativePath) = pending.Pop();
			IEnumerable<string> files;
			IEnumerable<string> directories;
			try
			{
				files = Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly)
					.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
				directories = Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly)
					.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				diagnostics.Add(new CliModExportDiagnostic(
					"ModExportDirectoryUnreadable", "error", true,
					$"无法读取节点“{node.Metadata.Name}”的目录：{exception.Message}",
					currentDirectory,
					CliProjections.FormatId(node.Id),
					exception.ToString()));
				continue;
			}

			foreach (var file in files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (IsReparsePoint(file))
						throw new IOException("文件是链接或其它 reparse point。");
					var fileName = Path.GetFileName(file);
					if (currentRelativePath.Length == 0
						&& string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase)
						&& string.IsNullOrEmpty(packagePath))
						continue;
					var relative = string.IsNullOrEmpty(currentRelativePath)
						? fileName
						: currentRelativePath + "/" + fileName;
					var safeRelative = NormalizeSafeRelativePath(relative, file, diagnostics, node);
					var sourceRelative = NormalizeSafeRelativePath(Path.GetRelativePath(modsRoot, file), file, diagnostics, node);
					var archiveRelative = CombineZipPath(packagePath, safeRelative);
					if (!seenSourceFiles.Add(sourceRelative))
					{
						diagnostics.Add(new CliModExportDiagnostic(
							"ModExportDuplicateSourceFile", "error", true,
							$"导出树重复包含源文件：{sourceRelative}。",
							file,
							CliProjections.FormatId(node.Id)));
						continue;
					}
					if (!seenArchiveFiles.Add(archiveRelative))
					{
						diagnostics.Add(new CliModExportDiagnostic(
							"ModExportDuplicateArchiveFile", "error", true,
							$"导出内容产生重复的 zip 条目：{archiveRelative}。",
							file,
							CliProjections.FormatId(node.Id)));
						continue;
					}
					var info = new FileInfo(file);
					var hash = await ComputeFileSha256Async(file, cancellationToken).ConfigureAwait(false);
					if (string.IsNullOrEmpty(hash)) throw new IOException("无法读取文件内容。");
					result.Add(new CliModExportFileSnapshot(sourceRelative, fileName, true, info.Length, hash, archiveRelative));
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
				{
					diagnostics.Add(new CliModExportDiagnostic(
						"ModExportFileUnreadable", "error", true,
						$"无法读取节点“{node.Metadata.Name}”中的文件：{exception.Message}",
						file,
						CliProjections.FormatId(node.Id),
						exception.ToString()));
				}
			}

			foreach (var child in directories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(child);
				if (string.Equals(name, "Overwrite", StringComparison.OrdinalIgnoreCase)) continue;
				if (IsReparsePoint(child))
				{
					diagnostics.Add(new CliModExportDiagnostic(
						"ModExportDirectoryReparsePoint", "error", true,
						$"节点“{node.Metadata.Name}”包含链接或其它 reparse point 目录，已拒绝导出。",
						child,
						CliProjections.FormatId(node.Id)));
					continue;
				}
				var relative = string.IsNullOrEmpty(currentRelativePath) ? name : currentRelativePath + "/" + name;
				var safeRelative = NormalizeSafeRelativePath(relative, child, diagnostics, node);
				pending.Push((child, safeRelative));
			}
		}

		return result
			.OrderBy(file => file.ArchiveRelativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string GetPackagePath(ModNodeId rootId, ModNodeId nodeId)
		=> rootId == nodeId ? string.Empty : "options/" + nodeId.Value.ToString("N");

	private static string CombineZipPath(string directory, string file)
		=> string.IsNullOrEmpty(directory) ? file : directory + "/" + file;

	private static string NormalizeSafeRelativePath(
		string? path,
		string subject,
		ICollection<CliModExportDiagnostic> diagnostics,
		ModNode node)
	{
		try { return ValidateSafeRelativePath(path, allowEmpty: true, subject); }
		catch (InvalidDataException exception)
		{
			diagnostics.Add(new CliModExportDiagnostic(
				"ModExportUnsafeRelativePath", "error", true,
				exception.Message,
				subject,
				CliProjections.FormatId(node.Id)));
			return string.Empty;
		}
	}

	private static string ValidateSafeRelativePath(string? path, bool allowEmpty, string subject)
	{
		var raw = (path ?? string.Empty).Replace('\\', '/');
		if (raw.StartsWith("/", StringComparison.Ordinal) || raw.Contains(':'))
			throw new InvalidDataException($"{subject} 不是安全的相对路径：{path}");
		var normalized = raw.Trim('/');
		if (string.IsNullOrEmpty(normalized))
		{
			if (allowEmpty) return string.Empty;
			throw new InvalidDataException($"{subject} 不能为空。");
		}
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(segment => segment is "." or ".."))
			throw new InvalidDataException($"{subject} 包含 . 或 .. 路径段：{path}");
		return string.Join('/', segments);
	}

	private static bool HasReparsePointInPath(string root, string candidate)
	{
		if (IsReparsePoint(root)) return true;
		var relative = Path.GetRelativePath(root, candidate);
		var current = root;
		foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			if (Directory.Exists(current) && IsReparsePoint(current)) return true;
		}
		return false;
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static async ValueTask<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return string.Empty;
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
	}

	private static string ComputeFingerprint(
		CliModExportRequest request,
		CliModExportTarget target,
		string libraryJsonSha256,
		IReadOnlyList<CliModExportNode> nodes,
		IReadOnlyList<CliModExportDiagnostic> diagnostics)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(new
		{
			request,
			target,
			libraryJsonSha256,
			nodes = nodes.OrderBy(node => node.PackagePath.Length == 0 ? 0 : 1).ThenBy(node => node.PackagePath, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase).ToArray(),
			diagnostics = diagnostics.OrderBy(item => item.Blocking ? 0 : 1).ThenBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
		}))));

	private static IReadOnlyList<CliModExportDiagnostic> DistinctDiagnostics(IEnumerable<CliModExportDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Severity}\u001f{item.Message}\u001f{item.Path}\u001f{item.NodeId}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static bool IsSha256(string value)
		=> value.Length == 64 && value.All(Uri.IsHexDigit);

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
			return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
		try
		{
			return string.Equals(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool IsWithin(string root, string candidate)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private sealed record CliModExportTree(
		IReadOnlyList<ModNode> Nodes,
		IReadOnlySet<ModNodeId> NodeIds,
		IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNodeId>> ChildrenByParent);
}
