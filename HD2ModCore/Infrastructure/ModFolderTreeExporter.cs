using System.IO.Compression;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把主体 + 选项树导出成"文件夹结构"的 zip。
/// <para>
/// <b>与 <see cref="ModExporter"/> 的分工。</b>那个导出的是**可重新导入**的包（含
/// <c>manifest.json</c>、选项放在 <c>options/&lt;guid&gt;</c>），导入器认它。
/// 这个导出的是**给人看 / 给别的工具吃**的包：<b>不含任何 manifest</b>，
/// 选项就是同名文件夹，选项的选项就是子文件夹——把库里的嵌套结构一对一摊开。
/// </para>
/// <para>
/// <b>为什么 VPK 版节点要特殊处理。</b>VPK 版 Mod 的节点目录里同时有：来源 <c>.vpk</c> 原件、
/// <c>vpk2patch/</c>（转换产物）、<c>Overwrite/</c>（换甲后的变体）。导出要的是**能用的
/// patch**，不是来源包——把 15 MB 的 .vpk 塞进 zip 既没用又误导。所以：
/// </para>
/// <list type="bullet">
/// <item>VPK 版节点 → 只写 <c>vpk2patch/</c> 里的 patch（<c>*.patch_*</c> 与其 sidecar）。</item>
/// <item>勾了"导出 Overwrite 版"且有 <c>Overwrite/</c> → <b>只写 Overwrite 的那份 patch</b>。</item>
/// </list>
/// <para>
/// <b>不写管理器自己的元数据文件</b>（<c>option.json</c> / <c>wardrobe.json</c> /
/// <c>vpk2patch.json</c>）：它们是管理器内部记账，进了导出包只会让人以为要照着改。
/// 备注会写成 <c>Description.txt</c>。
/// </para>
/// </summary>
public sealed class ModFolderTreeExporter : IModFolderTreeExporter
{
	/// <summary>库根里这些文件名属于管理器内部记账，不进导出包。</summary>
	private static readonly HashSet<string> ManagerMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
	{
		"option.json",
		"wardrobe.json",
		"vpk2patch.json",
		"manifest.json",
		"custom-model.json",
		"sections.json",
	};

	/// <summary>备注写进这个文件名。</summary>
	public const string DescriptionFileName = "Description.txt";

	private readonly StoragePaths _paths;

	public ModFolderTreeExporter(StoragePaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<ModFolderTreeExportResult> ExportAsync(
		ModNodeId rootNodeId,
		LibrarySnapshot snapshot,
		string destinationZipPath,
		bool includeOverwrite,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

		if (!snapshot.Nodes.TryGetValue(rootNodeId, out var root))
			throw new KeyNotFoundException($"导出失败：库里找不到节点 {rootNodeId}。");

		var tree = CollectSubtree(snapshot, rootNodeId);
		var zipPath = Path.GetFullPath(destinationZipPath);
		var directory = Path.GetDirectoryName(zipPath);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

		// 先写临时文件再改名：中途失败不会留下半个 zip 覆盖掉用户已有的同名文件。
		var temporaryPath = zipPath + ".tmp";
		if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

		var warnings = new List<string>();
		var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fileCount = 0;
		var nodeCount = 0;
		var nodesWithoutOverwrite = 0;
		ZipArchive? zip = null;
		try
		{
			zip = ZipFile.Open(temporaryPath, ZipArchiveMode.Create);
			WriteNode(root, string.Empty, isRoot: true);
			zip.Dispose();
			zip = null;

			if (File.Exists(zipPath)) File.Delete(zipPath);
			File.Move(temporaryPath, zipPath);

			// 勾了 Overwrite 但有节点没有 Overwrite：汇总成一条，而不是每个节点刷一行。
			if (nodesWithoutOverwrite > 0)
				warnings.Insert(0, $"勾选了导出 Overwrite 版，但 {nodesWithoutOverwrite} 个节点没有 Overwrite，已导出它们的基础版。");
		}
		finally
		{
			zip?.Dispose();
			if (File.Exists(temporaryPath))
			{
				try { File.Delete(temporaryPath); } catch (IOException) { }
			}
		}

		return new ModFolderTreeExportResult(zipPath, nodeCount, fileCount, warnings);

		void WriteNode(ModNode node, string zipDirectory, bool isRoot)
		{
			cancellationToken.ThrowIfCancellationRequested();
			nodeCount++;

			var nodeDirectory = ResolveNodeDirectory(node);
			var files = SelectPayload(nodeDirectory, includeOverwrite, out var hasOverwrite);
			if (includeOverwrite && !hasOverwrite) nodesWithoutOverwrite++;
			if (files.Count == 0)
				warnings.Add($"“{node.Metadata.Name}”没有任何可导出的文件。");

			// 备注 → Description.txt。空备注不写空文件（那只会让人以为我们丢了他的描述）。
			var notes = node.Metadata.Notes;
			var hasNotes = !string.IsNullOrWhiteSpace(notes);
			if (hasNotes)
			{
				var entryPath = CombineZipPath(zipDirectory, DescriptionFileName);
				if (writtenEntries.Add(entryPath))
				{
					var entry = zip!.CreateEntry(entryPath, CompressionLevel.Optimal);
					using var stream = entry.Open();
					using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
					writer.Write(notes!.TrimEnd() + Environment.NewLine);
					fileCount++;
				}
			}

			foreach (var file in files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var entryPath = CombineZipPath(zipDirectory, file.EntryName);
				if (!writtenEntries.Add(entryPath))
				{
					warnings.Add($"导出时跳过重名文件：{entryPath}");
					continue;
				}

				zip!.CreateEntryFromFile(file.FullPath, entryPath, CompressionLevel.Optimal);
				fileCount++;
			}

			if (!tree.ChildrenByParent.TryGetValue(node.Id, out var childIds)) return;
			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var childId in childIds)
			{
				if (!snapshot.Nodes.TryGetValue(childId, out var child)) continue;
				var folder = MakeUniqueFolderName(SafeFolderName(child.Metadata.Name), usedNames);
				WriteNode(child, CombineZipPath(zipDirectory, folder), isRoot: false);
			}
		}
	}

	/// <summary>挑出这个节点要写进 zip 的文件。规则集中在注释里，不散落到各处。</summary>
	private static IReadOnlyList<PayloadFile> SelectPayload(
		string nodeDirectory,
		bool includeOverwrite,
		out bool hasOverwrite)
	{
		// VPK 版 Mod：真正的 patch 在 vpk2patch\ 下，根上的 .vpk 只是来源原件。
		var vpkPatchDirectory = ModNodePayloadLayout.GetVpkPatchDirectory(nodeDirectory);
		var isVpkNode = Directory.Exists(vpkPatchDirectory);
		var baseline = isVpkNode ? vpkPatchDirectory : nodeDirectory;

		var overwriteDirectory = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);
		hasOverwrite = Directory.Exists(overwriteDirectory);
		var useOverwrite = includeOverwrite && hasOverwrite;

		if (isVpkNode)
		{
			// VPK 版：只取 patch（含 sidecar）。勾了 Overwrite 就**只**取 Overwrite 那份。
			var source = useOverwrite ? overwriteDirectory : baseline;
			return EnumerateTopLevelFiles(source)
				.Where(file => IsPatchPayload(file.Name))
				.Select(file => new PayloadFile(file.FullPath, file.Name))
				.ToArray();
		}

		// 普通（patch）Mod：根上的文件即产物。Overwrite 语义是"同名顶替"，
		// 所以勾了就只把同名的那几个换掉，其余（图片等）保留。
		var payload = new List<PayloadFile>();
		foreach (var file in EnumerateTopLevelFiles(nodeDirectory))
		{
			if (ManagerMetadataFiles.Contains(file.Name)) continue;
			if (file.Name.Equals(DescriptionFileName, StringComparison.OrdinalIgnoreCase)) continue;
			payload.Add(new PayloadFile(file.FullPath, file.Name));
		}

		if (!useOverwrite) return payload;

		var overwriteFiles = EnumerateTopLevelFiles(overwriteDirectory)
			.Where(file => !ManagerMetadataFiles.Contains(file.Name))
			.ToArray();
		var replaced = overwriteFiles
			.Select(file => file.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		payload.RemoveAll(file => replaced.Contains(file.EntryName));
		payload.AddRange(overwriteFiles.Select(file => new PayloadFile(file.FullPath, file.Name)));
		return payload;
	}

	/// <summary>"这个文件属于可部署的 patch 载荷"。patch 本体 <c>x.patch_0</c> 与它的 sidecar 都算。</summary>
	private static bool IsPatchPayload(string fileName)
		=> fileName.Contains(".patch_", StringComparison.OrdinalIgnoreCase);

	private static IReadOnlyList<(string FullPath, string Name)> EnumerateTopLevelFiles(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
				.Select(path => (FullPath: path, Name: Path.GetFileName(path)))
				.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new InvalidDataException($"无法枚举导出目录：{directory}", exception);
		}
	}

	private ExportTree CollectSubtree(LibrarySnapshot snapshot, ModNodeId rootId)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		var nodes = new List<ModNode>();
		var childrenByParent = new Dictionary<ModNodeId, IReadOnlyList<ModNodeId>>();
		var visited = new HashSet<ModNodeId>();
		var active = new HashSet<ModNodeId>();

		Visit(rootId);
		return new ExportTree(nodes, childrenByParent);

		void Visit(ModNodeId id)
		{
			if (!snapshot.Nodes.TryGetValue(id, out var node))
				throw new KeyNotFoundException($"导出树引用了一个不存在的节点：{id}");
			if (!active.Add(id))
				throw new InvalidDataException($"导出树在节点 {id} 处出现环，无法导出。");
			if (!visited.Add(id))
				throw new InvalidDataException($"导出树里有一个被多处共享的选项（{id}），无法表示成一棵目录树。");

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
		var libraryRoot = Path.GetFullPath(_paths.ModsDirectory)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var relative = NormalizeSafeRelativePath(node.RelativePath);
		if (string.IsNullOrEmpty(relative))
			throw new InvalidDataException($"节点 {node.Id} 没有物理存储路径，无法导出。");

		var directory = Path.GetFullPath(Path.Combine(libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsWithin(libraryRoot, directory))
			throw new InvalidDataException($"节点 {node.Id} 的路径逃出了 Mod 库：{node.RelativePath}");
		if (!Directory.Exists(directory))
			throw new DirectoryNotFoundException($"节点目录不存在：{directory}");

		return directory;
	}

	private static char[] InvalidFolderNameCharacters { get; } =
		Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

	/// <summary>选项名 → 文件夹名。空名回落到 <c>Option</c>，非法字符换成下划线。</summary>
	private static string SafeFolderName(string? name)
	{
		var value = (name ?? string.Empty).Trim();
		foreach (var character in InvalidFolderNameCharacters)
			value = value.Replace(character, '_');
		value = value.Trim().TrimEnd('.');
		return string.IsNullOrWhiteSpace(value) ? "Option" : value;
	}

	/// <summary>同级目录撞名时依次追加 <c> (2)</c>、<c> (3)</c>——宁可多一个后缀，也不合并两个选项。</summary>
	private static string MakeUniqueFolderName(string name, HashSet<string> used)
	{
		if (used.Add(name)) return name;
		for (var suffix = 2; ; suffix++)
		{
			var candidate = $"{name} ({suffix})";
			if (used.Add(candidate)) return candidate;
		}
	}

	private static string CombineZipPath(string directory, string name)
		=> string.IsNullOrEmpty(directory) ? name : directory + "/" + name;

	private static string NormalizeSafeRelativePath(string? path)
	{
		var raw = (path ?? string.Empty).Replace('\\', '/').Trim('/');
		if (raw.Length == 0) return string.Empty;
		var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(segment => segment is "." or ".."))
			throw new InvalidDataException($"不安全的相对路径：{path}");
		return string.Join('/', segments);
	}

	private static bool IsWithin(string root, string candidate)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private sealed record ExportTree(
		IReadOnlyList<ModNode> Nodes,
		IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNodeId>> ChildrenByParent);

	private sealed record PayloadFile(string FullPath, string EntryName);
}
