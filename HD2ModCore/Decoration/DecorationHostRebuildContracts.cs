using HD2ModCore.Domain;

namespace HD2ModCore.Decoration;

// A UI-neutral description of one enabled decoration package. The package itself
// owns the portable plan; the relative path points to its library storage root.
public sealed record DecorationHostRebuildAttachment(
    string DecorationId,
    string RelativePath,
    DecorationPlanDocument Plan);

// Result of rebuilding the managed Overwrite layer for one host Mod. Operational
// failures are reported here so a CLI can render diagnostics without parsing UI
// exceptions; desktop compatibility adapters may still rethrow them for legacy UX.
public sealed record DecorationHostRebuildResult(
    bool Succeeded,
    string OutputDirectory,
    int RebuiltPatchCount,
    int EditedUnitCount,
    bool ClearedGeneratedOverwrite,
    IReadOnlyList<CoreIssue> Issues)
{
    public static DecorationHostRebuildResult Success(
        string outputDirectory,
        int rebuiltPatchCount,
        int editedUnitCount,
        bool clearedGeneratedOverwrite,
        IReadOnlyList<CoreIssue>? issues = null)
        => new(true, outputDirectory, rebuiltPatchCount, editedUnitCount, clearedGeneratedOverwrite, issues ?? []);

    public static DecorationHostRebuildResult Failure(
        string outputDirectory,
        IReadOnlyList<CoreIssue> issues)
        => new(false, outputDirectory, 0, 0, false, issues);
}

// Explicit outcome for the no-attachment cleanup path. Keeping this small helper
// public lets non-WPF callers retain the same ownership guard as the rebuild flow.
public sealed record DecorationGeneratedOverwriteCleanupResult(bool Removed, bool WasManaged);
