using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Produces the same logical import tree that <see cref="ModLibraryImporter"/>
/// will persist, without copying a file or mutating a library snapshot.
/// </summary>
public sealed class ModImportPlanner : IModImportPlanner
{
	private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
	private readonly IObjectTreeImporter _treeImporter;
	private readonly SevenZipArchiveExtractor _archiveExtractor = new();

	public ModImportPlanner(IObjectTreeImporter treeImporter, IArchiveObjectTreeImporter archiveImporter)
	{
		_treeImporter = treeImporter ?? throw new ArgumentNullException(nameof(treeImporter));
		// Keep the public constructor contract intact. The current archive-tree
		// adapter intentionally hides its temporary extraction root, while planning
		// must inspect that root for manifest/wrapper semantics before projecting it.
		ArgumentNullException.ThrowIfNull(archiveImporter);
	}

	public async ValueTask<ImportPlan> PlanFolderAsync(string folderPath, CancellationToken cancellationToken = default)
	{
		var fullPath = Path.GetFullPath(folderPath);
		if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
		return await PlanFolderContainerAsync(
			fullPath,
			fullPath,
			new DirectoryInfo(fullPath).Name,
			isTemporarySource: false,
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<ImportPlan> PlanArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
	{
		var fullPath = Path.GetFullPath(archivePath);
		if (!File.Exists(fullPath)) throw new FileNotFoundException("Archive file not found.", fullPath);
		return await PlanArchiveCoreAsync(
			fullPath,
			fullPath,
			Path.GetFileNameWithoutExtension(fullPath),
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<ImportPlan> PlanFolderContainerAsync(
		string actualRoot,
		string presentationSourcePath,
		string displayName,
		bool isTemporarySource,
		CancellationToken cancellationToken)
	{
		var packageRoot = ModLibraryImporter.ResolvePackageRoot(actualRoot);
		var directPlan = await PlanSinglePackageAsync(
			packageRoot,
			presentationSourcePath,
			displayName,
			isTemporarySource,
			cancellationToken).ConfigureAwait(false);

		// A manifest owns the entire package. This is the same boundary used by
		// ModLibraryImporter: nested archives remain ordinary package files.
		if (packageRoot.Manifest is not null) return directPlan;

		var componentPlans = new List<ImportPlan>();
		if (directPlan.Items.Count > 0) componentPlans.Add(directPlan);
		var nestedArchiveCount = 0;
		foreach (var archivePath in EnumerateArchives(actualRoot))
		{
			cancellationToken.ThrowIfCancellationRequested();
			nestedArchiveCount++;
			var relativeArchivePath = Normalize(Path.GetRelativePath(actualRoot, archivePath));
			var nestedPresentationPath = presentationSourcePath + "!/" + relativeArchivePath;
			var nested = await PlanArchiveCoreAsync(
				archivePath,
				nestedPresentationPath,
				Path.GetFileNameWithoutExtension(archivePath),
				cancellationToken).ConfigureAwait(false);
			if (nested.Items.Count > 0) componentPlans.Add(nested);
			else if (nested.Diagnostics.Any(diagnostic => diagnostic.IsBlocking)) componentPlans.Add(nested);
		}

		if (nestedArchiveCount == 0) return directPlan;
		if (componentPlans.Count == 0) return directPlan;

		var diagnostics = componentPlans
			.SelectMany(plan => plan.Diagnostics)
			.Where(diagnostic => !string.Equals(diagnostic.Code, "NoImportablePayload", StringComparison.OrdinalIgnoreCase))
			.ToList();
		diagnostics.Insert(0, new ImportPlanDiagnostic(
			"NestedContainer",
			$"发现 {nestedArchiveCount} 个嵌套压缩包；规划已按与导入器相同的包发现规则展开。",
			presentationSourcePath));
		return new ImportPlan(
			presentationSourcePath,
			displayName,
			ImportPlanMode.NestedContainer,
			componentPlans.SelectMany(plan => plan.Items).ToArray(),
			diagnostics,
			RequiresDerivedInformation: true);
	}

	private async ValueTask<ImportPlan> PlanArchiveCoreAsync(
		string actualArchivePath,
		string presentationSourcePath,
		string displayName,
		CancellationToken cancellationToken)
	{
		var extractRoot = Path.Combine(Path.GetTempPath(), "HD2ModManager", "import-plan", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(extractRoot);
		try
		{
			await _archiveExtractor.ExtractAsync(actualArchivePath, extractRoot, cancellationToken).ConfigureAwait(false);
			var plan = await PlanFolderContainerAsync(
				extractRoot,
				presentationSourcePath,
				displayName,
				isTemporarySource: true,
				cancellationToken).ConfigureAwait(false);
			var diagnostics = plan.Diagnostics.ToList();
			diagnostics.Insert(0, new ImportPlanDiagnostic(
				"ArchiveExtracted",
				"已在临时工作区展开压缩包以识别 manifest、选项和嵌套包；不会写入 Mod 库。",
				presentationSourcePath));
			return plan with { Diagnostics = diagnostics };
		}
		finally
		{
			TryDeleteTemporaryDirectory(extractRoot);
		}
	}

	private async ValueTask<ImportPlan> PlanSinglePackageAsync(
		ModLibraryImporter.PackageRoot packageRoot,
		string presentationSourcePath,
		string displayName,
		bool isTemporarySource,
		CancellationToken cancellationToken)
	{
		var tree = await _treeImporter.ImportFolderAsync(packageRoot.Directory, cancellationToken).ConfigureAwait(false);
		var planned = ModLibraryImporter.PlanTreeForImport(tree, packageRoot.Directory, packageRoot.Manifest);
		var mode = packageRoot.Manifest is null ? ImportPlanMode.HeuristicFolder : ImportPlanMode.ManifestStrict;
		var diagnostics = new List<ImportPlanDiagnostic>();
		foreach (var conflict in planned.OptionPathConflicts)
		{
			diagnostics.Add(new ImportPlanDiagnostic(
				"OptionPathConflict",
				$"多个 manifest 选项以同等优先级归属实际 Patch 目录“{conflict.SourceRelativePath}”。导入会保留一份共享 payload；仅这些受影响父选项的直属共享内容不会默认启用，其他选项仍按正常规则初始化。",
				presentationSourcePath));
		}
		if (!PathsEqual(packageRoot.Directory, presentationSourcePath) && packageRoot.Manifest is not null)
		{
			diagnostics.Add(new ImportPlanDiagnostic(
				"ManifestWrapper",
				"发现单层包装目录中的 manifest，已按包装目录内的真实包根规划。",
				presentationSourcePath));
		}
		diagnostics.Add(packageRoot.Manifest is null
			? new ImportPlanDiagnostic("HeuristicTree", "未发现 manifest，已复用导入器的启发式树规划。", presentationSourcePath)
			: new ImportPlanDiagnostic("ManifestStrict", "发现 manifest，已复用导入器的严格选项树规划。", presentationSourcePath));
		return ProjectPlannedTree(
			presentationSourcePath,
			displayName,
			mode,
			planned,
			isTemporarySource,
			diagnostics);
	}

	private static ImportPlan ProjectPlannedTree(
		string sourcePath,
		string displayName,
		ImportPlanMode mode,
		ModLibraryImporter.PlannedImportTree planned,
		bool isTemporarySource,
		IReadOnlyList<ImportPlanDiagnostic> diagnostics)
	{
		if (planned.Tree.Nodes.Count == 0)
		{
			return new ImportPlan(
				sourcePath,
				displayName,
				mode,
				Array.Empty<ImportPlanItem>(),
				diagnostics.Concat([new ImportPlanDiagnostic("NoImportablePayload", "未发现 patch、装饰包、ReShade 文件或其他可导入内容。", sourcePath, true)]).ToArray(),
				RequiresDerivedInformation: false);
		}

		var orderedNodes = planned.Tree.Nodes.Values
			.OrderBy(node => node.Id == planned.Tree.RootId ? 0 : 1)
			.ThenBy(node => PathDepth(node.RelativePath))
			.ThenBy(node => Normalize(node.RelativePath), StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.ToArray();
		var keyByNodeId = BuildPlanKeys(orderedNodes, planned.Tree.RootId);
		var items = new List<ImportPlanItem>(orderedNodes.Length);
		foreach (var node in orderedNodes)
		{
			var sourcePaths = planned.SourceRelativePaths.TryGetValue(node.Id, out var rawPaths)
				? rawPaths.Select(Normalize).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
				: Array.Empty<string>();
			var parentKeys = (node.Metadata.HostModGuids ?? Array.Empty<string>())
				.Select(value => Guid.TryParse(value, out var guid) ? new ModNodeId(guid) : (ModNodeId?)null)
				.Where(id => id.HasValue && keyByNodeId.ContainsKey(id.Value))
				.Select(id => keyByNodeId[id!.Value])
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var isLogicalNode = node.Metadata.Kind == ModNodeKind.Option
				&& node.PatchGroups.Count == 0
				&& sourcePaths.Length == 0;
			var itemDiagnostics = new List<ImportPlanDiagnostic>();
			if (isLogicalNode)
				itemDiagnostics.Add(new ImportPlanDiagnostic("LogicalOption", "该条目只承载选项关系，不直接包含 patch。", sourcePath));
			foreach (var conflict in planned.OptionPathConflicts.Where(conflict => conflict.AffectedParentOptionIds.Contains(node.Id)))
			{
				itemDiagnostics.Add(new ImportPlanDiagnostic(
					"OptionPathConflict",
					$"该逻辑选项共享实际 Patch 目录“{conflict.SourceRelativePath}”；其直属共享 payload 默认不启用。",
					sourcePath));
			}
			foreach (var conflict in planned.OptionPathConflicts.Where(conflict => string.Equals(
				conflict.SourceRelativePath,
				node.Metadata.SourcePackagePath,
				StringComparison.OrdinalIgnoreCase)))
			{
				itemDiagnostics.Add(new ImportPlanDiagnostic(
					"SharedOptionPayload",
					$"该条目是由多个逻辑选项共享的实际 Patch payload：“{conflict.SourceRelativePath}”。",
					sourcePath));
			}
			items.Add(new ImportPlanItem(
				sourcePath,
				node.Metadata.Name,
				ToRole(node, planned.Tree.RootId),
				Normalize(node.RelativePath) is { Length: > 0 } relative ? relative : null,
				node.PatchGroups,
				isTemporarySource,
				sourcePaths,
				itemDiagnostics,
				keyByNodeId[node.Id],
				parentKeys,
				node.Metadata.OptionOrder,
				isLogicalNode));
		}
		return new ImportPlan(sourcePath, displayName, mode, items, diagnostics, RequiresDerivedInformation: true);
	}

	private static Dictionary<ModNodeId, string> BuildPlanKeys(IReadOnlyList<ModNode> nodes, ModNodeId rootId)
	{
		var result = new Dictionary<ModNodeId, string>();
		var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var node in nodes)
		{
			var baseKey = node.Id == rootId
				? "root"
				: !string.IsNullOrWhiteSpace(node.Metadata.SourcePackagePath)
					? node.Metadata.SourcePackagePath!
					: Normalize(node.RelativePath);
			if (string.IsNullOrWhiteSpace(baseKey)) baseKey = "node";
			var key = baseKey;
			var suffix = 2;
			while (!used.Add(key)) key = baseKey + "#" + suffix++;
			result[node.Id] = key;
		}
		return result;
	}

	private static ImportPlanItemRole ToRole(ModNode node, ModNodeId rootId)
		=> node.Metadata.Kind switch
		{
			ModNodeKind.Option => ImportPlanItemRole.Option,
			ModNodeKind.Decoration => ImportPlanItemRole.Decoration,
			ModNodeKind.ReShade => ImportPlanItemRole.ReShade,
			_ when node.Id == rootId && node.PatchGroups.Count == 0 => ImportPlanItemRole.PlaceholderHost,
			_ => ImportPlanItemRole.Host,
		};

	private static IEnumerable<string> EnumerateArchives(string root)
	{
		IEnumerable<string> files;
		try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
		catch (IOException) { files = Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
		return files.Where(path => ArchiveExtensions.Contains(Path.GetExtension(path))).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static void TryDeleteTemporaryDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
			var parent = Directory.GetParent(directory)?.FullName;
			if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
				Directory.Delete(parent, recursive: false);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static bool PathsEqual(string left, string right)
		=> string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

	private static string Normalize(string? value)
		=> string.IsNullOrWhiteSpace(value) || value == "." ? string.Empty : value.Replace('\\', '/').Trim('/');

	private static int PathDepth(string path)
		=> string.IsNullOrEmpty(Normalize(path)) ? 0 : Normalize(path).Count(character => character == '/') + 1;
}
