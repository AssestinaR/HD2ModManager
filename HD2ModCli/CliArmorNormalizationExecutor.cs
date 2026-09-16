using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliArmorNormalizationExecution(
    bool Success,
    string? OutputDirectory,
    int ProcessedPatches,
    int RebuiltUnits,
    int HiddenUnits,
    IReadOnlyList<CliArmorNormalizationDiagnostic> Diagnostics);

internal static class CliArmorNormalizationExecutor
{
    public static async ValueTask<CliArmorNormalizationExecution> ApplyAsync(
        CliArmorNormalizationPlanDocument document,
        CliWorkspace workspace,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var validation = await CliArmorNormalizationPlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.Current is null)
            return new(false, null, 0, 0, 0, validation.Diagnostics);

        var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot, document.GameDataDirectory);
        var parent = Path.GetDirectoryName(target) ?? throw new CliUserException("ArmorNormalizeOutputInvalid", "无法解析输出目录父路径。 ");
        Directory.CreateDirectory(parent);
        var staged = Path.Combine(parent, ".hd2mod-cli-armor-normalize-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = (await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false)).Snapshot.Nodes.Values
                .FirstOrDefault(n => string.Equals(CliProjections.FormatId(n.Id), document.SourceNodeId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
                return new(false, null, 0, 0, 0, [new("ArmorNormalizeSourceNotFound", "error", true, "计划来源 Mod 当前不存在。 ")]);
            var paths = new StoragePaths(workspace.ManagerRoot ?? Directory.GetCurrentDirectory(), workspace.LibraryRoot!);
            var sourceDirectory = Path.GetFullPath(Path.Combine(paths.ModsDirectory, source.RelativePath));
            await using var reader = CoreServices.CreateModInformationReader();
            var service = CoreServices.CreateDirectArmorNormalizationService(paths, reader);
            var result = await service.ExecuteAsync(source, sourceDirectory, staged, document.GameDataDirectory, cancellationToken).ConfigureAwait(false);
            var diagnostics = result.Issues.Select(ToDiagnostic).ToArray();
            if (!result.IsSuccessful || diagnostics.Any(d => d.Blocking)) return new(false, null, result.ProcessedPatches, result.RebuiltUnits, result.HiddenUnits, diagnostics);
            if (Directory.Exists(target) || File.Exists(target)) throw new CliUserException("ArmorNormalizeOutputAlreadyExists", $"输出目录已存在：{target}。 ");
            Directory.Move(staged, target);
            return new(true, target, result.ProcessedPatches, result.RebuiltUnits, result.HiddenUnits, diagnostics);
        }
        finally
        {
            if (Directory.Exists(staged))
            {
                try { Directory.Delete(staged, true); } catch { }
            }
        }
    }

    private static string PrepareOutputDirectory(string outputDirectory, string? libraryRoot, string gameDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new CliUserException("ArmorNormalizeOutputRequired", "必须提供库外的新输出目录。 ");
        var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "ArmorNormalizeOutputInvalid");
        if (File.Exists(target) || Directory.Exists(target)) throw new CliUserException("ArmorNormalizeOutputAlreadyExists", $"输出目录已存在：{target}。 ");
        if (!string.IsNullOrWhiteSpace(libraryRoot) && IsChild(target, libraryRoot)) throw new CliUserException("ArmorNormalizeOutputInsideLibrary", "输出目录不能位于 Mod 库内。 ");
        if (IsChild(target, gameDataDirectory)) throw new CliUserException("ArmorNormalizeOutputInsideGameData", "输出目录不能位于 GameData 内。 ");
        var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName;
        if (!string.IsNullOrWhiteSpace(gameRoot) && IsChild(target, gameRoot)) throw new CliUserException("ArmorNormalizeOutputInsideGameRoot", "输出目录不能位于游戏根目录内。 ");
        return target;
    }
    private static bool IsChild(string candidate, string root)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(c, r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static CliArmorNormalizationDiagnostic ToDiagnostic(CoreIssue issue) => new(issue.Code, issue.Severity == CoreIssueSeverity.Error ? "error" : "warning", issue.Severity == CoreIssueSeverity.Error, issue.Message, issue.FilePath);
}
