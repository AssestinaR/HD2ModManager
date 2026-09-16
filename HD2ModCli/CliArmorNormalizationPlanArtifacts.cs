using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public sealed record CliArmorNormalizationPlanDocument(
    string Schema,
    int Version,
    DateTimeOffset CreatedUtc,
    string InputFingerprint,
    string SourceNodeId,
    string GameDataDirectory,
    string SourceModName,
    IReadOnlyList<string> SourcePatchPaths,
    IReadOnlyList<ArmorNormalizationArmor> Armors,
    IReadOnlyList<CliArmorNormalizationDiagnostic> Diagnostics)
{
    public const string CurrentSchema = "hd2mod.armor-normalization-plan";
    public const int CurrentVersion = 1;
    public bool IsReady => Armors.Count != 0 && !Diagnostics.Any(d => d.Blocking);
}

public sealed record CliArmorNormalizationDiagnostic(string Code, string Severity, bool Blocking, string Message, string? Path = null);

internal sealed record CliArmorNormalizationBuildResult(CliArmorNormalizationPlanDocument Document, StoragePaths Paths, ModNode Source);
internal sealed record CliArmorNormalizationValidationResult(CliArmorNormalizationPlanDocument Document, CliArmorNormalizationPlanDocument? Current, IReadOnlyList<CliArmorNormalizationDiagnostic> Diagnostics)
{
    public bool IsValid => Current is not null && !Diagnostics.Any(d => d.Blocking);
}

internal static class CliArmorNormalizationPlanArtifacts
{
    public static async ValueTask<CliArmorNormalizationBuildResult> BuildAsync(CliWorkspace workspace, string selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace.ManagerRoot) || string.IsNullOrWhiteSpace(workspace.LibraryRoot) || string.IsNullOrWhiteSpace(workspace.GameDataRoot))
            throw new CliUserException("ArmorNormalizeWorkspaceRequired", "equipment armor-normalize 需要 --manager-root、--library 和 --game-data。 ");
        var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
        var source = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
        var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
        await using var reader = CoreServices.CreateModInformationReader();
        var service = CoreServices.CreateDirectArmorNormalizationService(paths, reader);
        var inspection = await service.InspectAsync(source, workspace.LibraryRoot, workspace.GameDataRoot, cancellationToken).ConfigureAwait(false);
        var diagnostics = inspection.Issues.Select(ToDiagnostic).ToArray();
        var fingerprint = ComputeFingerprint(source, inspection);
        return new CliArmorNormalizationBuildResult(new CliArmorNormalizationPlanDocument(CliArmorNormalizationPlanDocument.CurrentSchema, CliArmorNormalizationPlanDocument.CurrentVersion, DateTimeOffset.UtcNow, fingerprint, CliProjections.FormatId(source.Id), workspace.GameDataRoot, source.Metadata.Name, inspection.SourcePatchPaths, inspection.Armors, diagnostics), paths, source);
    }

    public static async ValueTask<CliArmorNormalizationPlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = CliOutput.NormalizeDirectoryPath(path, "ArmorNormalizePlanPathInvalid");
        if (!File.Exists(full)) throw new CliUserException("ArmorNormalizePlanNotFound", $"计划不存在：{full}");
        var document = JsonSerializer.Deserialize<CliArmorNormalizationPlanDocument>(await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false), CliOutput.JsonOptions);
        if (document is null || document.Schema != CliArmorNormalizationPlanDocument.CurrentSchema || document.Version != CliArmorNormalizationPlanDocument.CurrentVersion)
            throw new CliUserException("ArmorNormalizePlanUnreadable", "规整计划为空或版本不受支持。 ");
        return document;
    }

    public static async ValueTask<CliArmorNormalizationValidationResult> ValidateAsync(CliArmorNormalizationPlanDocument document, CliWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<CliArmorNormalizationDiagnostic>();
        if (!string.Equals(Path.GetFullPath(workspace.GameDataRoot ?? ""), Path.GetFullPath(document.GameDataDirectory), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("ArmorNormalizeGameDataChanged", "error", true, "当前 GameData 目录与计划不一致。"));
        CliArmorNormalizationPlanDocument? current = null;
        if (diagnostics.Count == 0)
        {
            var build = await BuildAsync(workspace, document.SourceNodeId, cancellationToken).ConfigureAwait(false);
            current = build.Document;
            if (!string.Equals(document.InputFingerprint, current.InputFingerprint, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new("ArmorNormalizeInputChanged", "error", true, "来源 Patch、装备事实或规整候选已变化，请重新生成计划。"));
        }
        return new(document, current, diagnostics);
    }

    private static string ComputeFingerprint(ModNode source, ArmorNormalizationInspection inspection)
    {
        var json = JsonSerializer.Serialize(new { source.Id, inspection.SourcePatchPaths, inspection.Armors }, CliOutput.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
    private static CliArmorNormalizationDiagnostic ToDiagnostic(CoreIssue issue) => new(issue.Code, issue.Severity == CoreIssueSeverity.Error ? "error" : "warning", issue.Severity == CoreIssueSeverity.Error, issue.Message, issue.FilePath);
}
