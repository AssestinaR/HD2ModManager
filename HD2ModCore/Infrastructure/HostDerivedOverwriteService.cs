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
            var removed = false;
            if (Directory.Exists(output))
            {
                ManagedOverwriteDirectory.RemoveManaged(output);
                removed = true;
            }
            return HostDerivedOverwriteResult.Success(output, clearedManagedOverwrite: removed);
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
        // 归属、备份、回滚统一交给 ManagedOverwriteDirectory：这套规矩只能有一份实现，
        // 否则"算不算管理器的"会在不同派生来源之间给出不同答案。
        var backup = ManagedOverwriteDirectory.Prepare(overwrite);
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
                ManagedOverwriteDirectory.Rollback(overwrite, backup);
                return HostDerivedOverwriteResult.Failure(overwrite, result, result.Issues);
            }

            ManagedOverwriteDirectory.Commit(overwrite, ManagedOverwriteMarkers.HelmetMerge);
            ManagedOverwriteDirectory.DiscardBackup(backup);
            return HostDerivedOverwriteResult.Success(overwrite, helmetMerge: result, issues: result.Issues);
        }
        catch
        {
            ManagedOverwriteDirectory.Rollback(overwrite, backup);
            throw;
        }
    }

    private static string ResolveHostDirectory(ModNode host, string modsRootDirectory)
    {
        var root = Path.GetFullPath(modsRootDirectory);
        var hostDirectory = Path.GetFullPath(Path.Combine(root, host.RelativePath));
        var relative = Path.GetRelativePath(root, hostDirectory);
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidOperationException("主体 Mod 的库内相对路径无效，已拒绝重建 Overwrite。");
        return hostDirectory;
    }

    private static string ResolveOverwriteDirectory(ModNode host, string modsRootDirectory)
        => Path.Combine(ResolveHostDirectory(host, modsRootDirectory), "Overwrite");

}
