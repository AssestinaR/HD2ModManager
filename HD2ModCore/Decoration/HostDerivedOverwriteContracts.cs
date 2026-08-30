using HD2ModCore.Domain;

namespace HD2ModCore.Decoration;

// Complete, UI-neutral input for deriving a host Mod's managed Overwrite. The
// caller owns activation persistence; this service only materializes the result.
public sealed record HostDerivedOverwriteRequest(
    ModNode Host,
    IReadOnlyCollection<DecorationHostRebuildAttachment> EnabledDecorations,
    bool HelmetToArmorMerged,
    string ModsRootDirectory,
    string GameDataDirectory,
    HelmetToArmorMergeInspection? PreparedHelmetInspection = null);

// Decoration and helmet merge deliberately remain visible as separate results:
// callers can preserve their existing progress/error presentation while sharing
// the single ownership, staging and rollback transaction.
public sealed record HostDerivedOverwriteResult(
    bool Succeeded,
    string OverwriteDirectory,
    DecorationHostRebuildResult? DecorationRebuild,
    HelmetToArmorMergeResult? HelmetMerge,
    bool ClearedManagedOverwrite,
    IReadOnlyList<CoreIssue> Issues)
{
    public static HostDerivedOverwriteResult Success(
        string overwriteDirectory,
        DecorationHostRebuildResult? decorationRebuild = null,
        HelmetToArmorMergeResult? helmetMerge = null,
        bool clearedManagedOverwrite = false,
        IReadOnlyList<CoreIssue>? issues = null)
        => new(true, overwriteDirectory, decorationRebuild, helmetMerge, clearedManagedOverwrite, issues ?? []);

    public static HostDerivedOverwriteResult Failure(
        string overwriteDirectory,
        DecorationHostRebuildResult? decorationRebuild,
        HelmetToArmorMergeResult? helmetMerge,
        IReadOnlyList<CoreIssue> issues)
        => new(false, overwriteDirectory, decorationRebuild, helmetMerge, false, issues);
}
