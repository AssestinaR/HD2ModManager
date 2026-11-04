using HD2ModCore.Domain;

namespace HD2ModCore.Decoration;

// Complete, UI-neutral input for deriving a host Mod's managed Overwrite. The
// caller owns activation persistence; this service only materializes the result.
//
// 装饰包已不再支持（2026-11-04）：这个请求原来还带一批装饰附件，
// 现在只剩「头盔合并」这一条派生来源，所以 `EnabledDecorations` 已删除。
public sealed record HostDerivedOverwriteRequest(
    ModNode Host,
    bool HelmetToArmorMerged,
    string ModsRootDirectory,
    string GameDataDirectory,
    HelmetToArmorMergeInspection? PreparedHelmetInspection = null);

// 结果里原来还有一份独立的装饰重建结果；装饰退场后只剩头盔合并。
public sealed record HostDerivedOverwriteResult(
    bool Succeeded,
    string OverwriteDirectory,
    HelmetToArmorMergeResult? HelmetMerge,
    bool ClearedManagedOverwrite,
    IReadOnlyList<CoreIssue> Issues)
{
    public static HostDerivedOverwriteResult Success(
        string overwriteDirectory,
        HelmetToArmorMergeResult? helmetMerge = null,
        bool clearedManagedOverwrite = false,
        IReadOnlyList<CoreIssue>? issues = null)
        => new(true, overwriteDirectory, helmetMerge, clearedManagedOverwrite, issues ?? []);

    public static HostDerivedOverwriteResult Failure(
        string overwriteDirectory,
        HelmetToArmorMergeResult? helmetMerge,
        IReadOnlyList<CoreIssue> issues)
        => new(false, overwriteDirectory, helmetMerge, false, issues);
}
