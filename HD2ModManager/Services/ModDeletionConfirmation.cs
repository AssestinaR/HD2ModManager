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

        var confirm = System.Windows.MessageBox.Show(
            primaryPrompt,
            primaryTitle,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return null;

        var selected = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attachedOptions = targets
            .Select(library.Get)
            .Where(mod => mod is { IsDecoration: false, IsOption: false, IsReShade: false })
            .SelectMany(host => library.GetOptionsForHost(host!.Guid))
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
            targets.AddRange(attachedOptions.Select(option => option.Guid));
        return targets;
    }
}
