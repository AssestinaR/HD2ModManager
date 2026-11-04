using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Owns the single host Overwrite directory used for derived output.
//
// 装饰包已不再支持（2026-11-04：能力被分件模组取代），所以这个服务现在只剩
// **头盔合并**这一条派生来源。它仍然独立存在（而不是并入头盔合并服务），因为
// 它负责的是"Overwrite 目录的归属、备份与回滚"，那是比合并本身更外一层的事：
// 谁生成的、能不能覆盖、失败时回滚到哪一步。
public sealed class HostDerivedOverwriteService
{
    private readonly IHelmetToArmorMergeService _helmetToArmorMerge;

    public HostDerivedOverwriteService(IHelmetToArmorMergeService helmetToArmorMerge)
    {
        _helmetToArmorMerge = helmetToArmorMerge ?? throw new ArgumentNullException(nameof(helmetToArmorMerge));
    }

    public async Task<HostDerivedOverwriteResult> RebuildAsync(
        HostDerivedOverwriteRequest request,
        CancellationToken cancellationToken = default,
        IProgress<HelmetToArmorMergeProgress>? helmetProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModsRootDirectory);

        var output = ResolveOverwriteDirectory(request.Host, request.ModsRootDirectory);
        if (!request.HelmetToArmorMerged)
        {
            var cleanup = RemoveManagedOverwrite(output);
            return HostDerivedOverwriteResult.Success(output, clearedManagedOverwrite: cleanup.Removed);
        }

        var inspection = request.PreparedHelmetInspection ?? await _helmetToArmorMerge.InspectAsync(
            request.Host,
            request.ModsRootDirectory,
            request.GameDataDirectory,
            cancellationToken).ConfigureAwait(false);
        if (inspection.Candidates.Count == 0)
        {
            var issues = inspection.Issues.Count == 0
                ? [new CoreIssue(CoreIssueSeverity.Error, "HelmetArmorPairMissing", "当前 Mod 没有可合并的 Armor/Helmet 模型集合。", request.Host.RelativePath, request.Host.Id)]
                : inspection.Issues;
            return HostDerivedOverwriteResult.Failure(output, null, issues);
        }

        return await BuildHelmetOverwriteFromSourceAsync(request, output, inspection.Candidates, cancellationToken, helmetProgress).ConfigureAwait(false);
    }

    private async Task<HostDerivedOverwriteResult> BuildHelmetOverwriteFromSourceAsync(
        HostDerivedOverwriteRequest request,
        string overwrite,
        IReadOnlyCollection<HelmetToArmorMergeCandidate> candidates,
        CancellationToken cancellationToken,
        IProgress<HelmetToArmorMergeProgress>? helmetProgress)
    {
        var source = ResolveHostDirectory(request.Host, request.ModsRootDirectory);
        var backup = PrepareManagedOverwrite(overwrite);
        try
        {
            var result = await _helmetToArmorMerge.ExecuteAsync(
                request.Host,
                source,
                overwrite,
                request.GameDataDirectory,
                candidates,
                cancellationToken,
                helmetProgress).ConfigureAwait(false);
            if (!result.IsSuccessful)
            {
                RestoreManagedOverwrite(overwrite, backup);
                return HostDerivedOverwriteResult.Failure(overwrite, result, result.Issues);
            }

            await File.WriteAllTextAsync(
                Path.Combine(overwrite, ManagedOverwriteMarkers.HelmetMerge),
                "generated",
                cancellationToken).ConfigureAwait(false);
            if (backup is not null) DeleteDirectoryIfExists(backup);
            return HostDerivedOverwriteResult.Success(overwrite, helmetMerge: result, issues: result.Issues);
        }
        catch
        {
            RestoreManagedOverwrite(overwrite, backup);
            throw;
        }
    }

    private static OverwriteCleanupResult RemoveManagedOverwrite(string directory)
    {
        if (!Directory.Exists(directory)) return new OverwriteCleanupResult(false);
        if (!IsManagedOverwrite(directory))
            throw new InvalidOperationException("主体 Mod 的 Overwrite 目录不是管理器生成，已拒绝删除。");
        Directory.Delete(directory, recursive: true);
        return new OverwriteCleanupResult(true);
    }

    private static string? PrepareManagedOverwrite(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return null;
        }
        if (!IsManagedOverwrite(directory))
            throw new InvalidOperationException("主体 Mod 的 Overwrite 目录不是管理器生成，已拒绝覆盖。");
        var backup = directory + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.Move(directory, backup);
        Directory.CreateDirectory(directory);
        return backup;
    }

    // 两枚标记都要认：`.hd2-decoration-overwrite` 是装饰当年生成的产物，
    // 我们还必须能识别并清掉它（见 ManagedOverwriteMarkers.Decoration 的说明）。
    private static bool IsManagedOverwrite(string directory)
        => File.Exists(Path.Combine(directory, ManagedOverwriteMarkers.HelmetMerge))
           || File.Exists(Path.Combine(directory, ManagedOverwriteMarkers.Decoration));

    private static void RestoreManagedOverwrite(string directory, string? backup)
    {
        DeleteDirectoryIfExists(directory);
        if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, directory);
    }

    private static string ResolveHostDirectory(ModNode host, string modsRootDirectory)
    {
        var root = Path.GetFullPath(modsRootDirectory);
        var hostDirectory = Path.GetFullPath(Path.Combine(root, host.RelativePath));
        var relative = Path.GetRelativePath(root, hostDirectory);
        if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("主体 Mod 的库内相对路径无效，已拒绝重建 Overwrite。");
        return hostDirectory;
    }

    private static string ResolveOverwriteDirectory(ModNode host, string modsRootDirectory)
        => Path.Combine(ResolveHostDirectory(host, modsRootDirectory), "Overwrite");

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed record OverwriteCleanupResult(bool Removed);
}
