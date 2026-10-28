using HD2ModCore.Application;
using HD2ModManager.Models;

namespace HD2ModManager.Services;

// Centralizes the two-step system confirmation used by every library deletion entry point.
public static class ModDeletionConfirmation
{
    public static IReadOnlyList<string>? ConfirmTargets(
        ModLibraryService library,
        IEnumerable<string> requestedIds,
        string primaryPrompt,
        string primaryTitle = "删除 Mod")
    {
        ArgumentNullException.ThrowIfNull(library);
        var targets = requestedIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && library.Get(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0) return Array.Empty<string>();

        var selected = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var confirm = System.Windows.MessageBox.Show(
            primaryPrompt + BuildOptionReferenceNote(library, targets, selected),
            primaryTitle,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return null;

        var attachedOptions = targets
            .Select(library.Get)
            .Where(mod => mod is { IsDecoration: false, IsReShade: false })
            .SelectMany(host => library.GetOptionDescendantsForHost(host!.Guid))
            .Where(option => !selected.Contains(option.Guid))
            .DistinctBy(option => option.Guid, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (attachedOptions.Length == 0) return targets;

        var related = System.Windows.MessageBox.Show(
            $"所选主体关联了 {attachedOptions.Length} 个未选中的选项。\n\n是否同时删除这些选项？\n选择“否”将仅删除原先选中的 Mod。",
            "删除关联选项",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
            if (related == System.Windows.MessageBoxResult.Yes)
            {
                var plan = library.PlanDeletion(targets, SafeModDeletionScope.WithUnsharedOptions);
                targets = plan.LibraryNodeRemovals
                    .Select(removal => removal.NodeId.Value.ToString("N"))
                    .ToList();
            }
        return targets;
    }

    // 删一个选项是合法操作，但它可能仍被若干主体引用：删掉之后主体还在，只是少了一个选项。
    // 用户必须在确认框里看到这件事，否则"点了删除"和"什么都没发生"在体感上没有区别。
    // Deleting an Option is legal; the surviving hosts simply lose it. Say so up front.
    private static string BuildOptionReferenceNote(
        ModLibraryService library,
        IReadOnlyList<string> targets,
        IReadOnlySet<string> selected)
    {
        var notes = new List<string>();
        foreach (var id in targets)
        {
            var option = library.Get(id);
            if (option is not { IsOption: true }) continue;
            var hostNames = (option.HostModGuids ?? Array.Empty<string>())
                .Where(hostId => !selected.Contains(hostId))
                .Select(library.Get)
                .Where(host => host is not null)
                .Select(host => host!.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (hostNames.Length == 0) continue;
            var shown = string.Join("、", hostNames.Take(3));
            var rest = hostNames.Length > 3 ? $" 等 {hostNames.Length} 个主体" : string.Empty;
            notes.Add($"“{option.Name}”仍被 {shown}{rest} 引用，删除后它们会失去这个选项（主体本身保留）。");
        }

        return notes.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", notes);
    }
}
