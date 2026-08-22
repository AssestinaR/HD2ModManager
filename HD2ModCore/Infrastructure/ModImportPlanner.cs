using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Import planning only. It deliberately does not copy files, mutate the library,
/// or calculate derived Mod properties.
/// </summary>
public sealed class ModImportPlanner : IModImportPlanner
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
    private readonly IObjectTreeImporter _treeImporter;
    private readonly IArchiveObjectTreeImporter _archiveImporter;

    public ModImportPlanner(IObjectTreeImporter treeImporter, IArchiveObjectTreeImporter archiveImporter)
    {
        _treeImporter = treeImporter ?? throw new ArgumentNullException(nameof(treeImporter));
        _archiveImporter = archiveImporter ?? throw new ArgumentNullException(nameof(archiveImporter));
    }

    public async ValueTask<ImportPlan> PlanFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(folderPath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var manifest = StandardModManifest.TryLoad(full);
        var tree = await _treeImporter.ImportFolderAsync(full, cancellationToken).ConfigureAwait(false);
        return manifest is not null
            ? BuildManifestPlan(full, tree, manifest)
            : BuildHeuristicPlan(full, tree, ImportPlanMode.HeuristicFolder);
    }

    public async ValueTask<ImportPlan> PlanArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Archive file not found.", full);
        var display = Path.GetFileNameWithoutExtension(full);
        var diagnostics = new List<ImportPlanDiagnostic>();
        if (!ArchiveExtensions.Contains(Path.GetExtension(full)))
            diagnostics.Add(new("UnexpectedExtension", "文件扩展名不是常见压缩格式，仍尝试按压缩包规划。", full));

        // Archive extraction ownership remains with the caller/executor. The archive
        // object-tree adapter provides a temporary view for planning.
        var tree = await _archiveImporter.ImportArchiveAsync(full, cancellationToken).ConfigureAwait(false);
        var plan = BuildHeuristicPlan(full, tree, ImportPlanMode.ArchiveNormalized);
        return plan with { DisplayName = display, Diagnostics = plan.Diagnostics.Concat(diagnostics).ToArray() };
    }

    private static ImportPlan BuildManifestPlan(string source, ImportedObjectTree tree, StandardModManifest manifest)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in manifest.Options)
        {
            foreach (var include in option.Include ?? []) entries.Add(Normalize(include));
            foreach (var sub in option.SubOptions ?? [])
                foreach (var include in sub.Include ?? []) entries.Add(Normalize(include));
        }

        var items = new List<ImportPlanItem>();
        foreach (var node in tree.Nodes.Values.OrderBy(n => Normalize(n.RelativePath).Length).ThenBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = Normalize(node.RelativePath);
            if (string.IsNullOrEmpty(path))
            {
                items.Add(ToItem(node, source, ImportPlanItemRole.Host, "manifest root"));
                continue;
            }
            var match = entries.FirstOrDefault(k => path.Equals(k, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(k + "/", StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                items.Add(ToItem(node, source, ImportPlanItemRole.Ignored, "manifest does not include this path"));
                continue;
            }
            items.Add(ToItem(node, source, ImportPlanItemRole.Option, match));
        }

        if (items.All(i => i.Role != ImportPlanItemRole.Host))
            items.Insert(0, new ImportPlanItem(source, manifest.Name ?? Path.GetFileName(source), ImportPlanItemRole.PlaceholderHost, null, [], false, null,
                [new("ManifestPlaceholder", "manifest 未发现主体 patch，创建空主体承载选项。", source)]));
        return new(source, manifest.Name ?? Path.GetFileName(source), ImportPlanMode.ManifestStrict, items,
            [new("ManifestStrict", "发现 manifest，按严格模式规划；未声明的子目录不会独立导入。", source)]);
    }

    private static ImportPlan BuildHeuristicPlan(string source, ImportedObjectTree tree, ImportPlanMode mode)
    {
        var payload = tree.Nodes.Values.Where(n => n.PatchGroups.Count > 0 || n.Metadata.Kind == ModNodeKind.Decoration)
            .OrderBy(n => Normalize(n.RelativePath).Length).ThenBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var diagnostics = new List<ImportPlanDiagnostic>();
        if (payload.Length == 0)
        {
            diagnostics.Add(new("NoImportablePayload", "未发现 patch 或装饰包。", source, true));
            return new(source, Path.GetFileName(source), mode, [], diagnostics);
        }

        var root = payload.FirstOrDefault(n => string.IsNullOrEmpty(Normalize(n.RelativePath)));
        var host = root ?? payload[0];
        var items = new List<ImportPlanItem>();
        if (root is null)
        {
            items.Add(new ImportPlanItem(source, Path.GetFileName(source), ImportPlanItemRole.PlaceholderHost, null, [], false, null,
                [new("HeuristicPlaceholder", "根目录没有 patch，创建空主体并将首个 patch 目录作为主体候选。", source)]));
        }
        foreach (var node in payload)
        {
            var isHost = node.Id.Equals(host.Id);
            var role = node.Metadata.Kind == ModNodeKind.Decoration
                ? ImportPlanItemRole.Decoration
                : isHost ? ImportPlanItemRole.Host : ImportPlanItemRole.Option;
            items.Add(ToItem(node, source, role, Normalize(node.RelativePath)));
        }
        if (payload.Length > 1)
            diagnostics.Add(new("HeuristicSplit", "未发现 manifest，已按最浅 patch 层级划分主体和选项。", source));
        return new(source, Path.GetFileName(source), mode, items, diagnostics);
    }

    private static ImportPlanItem ToItem(ModNode node, string source, ImportPlanItemRole role, string? relative)
        => new(source, node.Metadata.Name, role, string.IsNullOrEmpty(relative) ? null : relative,
            node.PatchGroups, false, null, null);

    private static string Normalize(string? path)
        => string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : path.Replace('\\', '/').Trim('/');

}
