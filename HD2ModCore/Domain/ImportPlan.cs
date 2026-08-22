namespace HD2ModCore.Domain;

public enum ImportPlanMode
{
    ManifestStrict,
    HeuristicFolder,
    ArchiveNormalized,
    NestedContainer,
}

public enum ImportPlanItemRole
{
    Host,
    Option,
    Decoration,
    PlaceholderHost,
    Ignored,
}

public sealed record ImportPlanDiagnostic(
    string Code,
    string Message,
    string? SourcePath = null,
    bool IsBlocking = false);

public sealed record ImportPlanItem(
    string SourcePath,
    string DisplayName,
    ImportPlanItemRole Role,
    string? RelativePath,
    IReadOnlyList<PatchGroupKey> PatchGroups,
    bool IsTemporarySource = false,
    IReadOnlyList<string>? PlannedFiles = null,
    IReadOnlyList<ImportPlanDiagnostic>? Diagnostics = null);

public sealed record ImportPlan(
    string SourcePath,
    string DisplayName,
    ImportPlanMode Mode,
    IReadOnlyList<ImportPlanItem> Items,
    IReadOnlyList<ImportPlanDiagnostic> Diagnostics,
    bool RequiresDerivedInformation = true);
