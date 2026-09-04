using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Coordinates the two derived layers that intentionally share one host Overwrite:
// original host + enabled decorations, followed by the optional helmet merge.
// It owns no activation state and has no UI dependency, so desktop and future
// headless callers execute exactly the same ownership and rollback transaction.
public sealed class HostDerivedOverwriteService
{
    private readonly IDecorationHostRebuildService _decorationRebuild;
    private readonly IHelmetToArmorMergeService _helmetToArmorMerge;
    private readonly IPatchFileNameParser _patchFileNameParser;

    public HostDerivedOverwriteService(
        IDecorationHostRebuildService decorationRebuild,
        IHelmetToArmorMergeService helmetToArmorMerge,
        IPatchFileNameParser patchFileNameParser)
    {
        _decorationRebuild = decorationRebuild ?? throw new ArgumentNullException(nameof(decorationRebuild));
        _helmetToArmorMerge = helmetToArmorMerge ?? throw new ArgumentNullException(nameof(helmetToArmorMerge));
        _patchFileNameParser = patchFileNameParser ?? throw new ArgumentNullException(nameof(patchFileNameParser));
    }

    public async Task<HostDerivedOverwriteResult> RebuildAsync(
        HostDerivedOverwriteRequest request,
        CancellationToken cancellationToken = default,
        IProgress<DecorationOperationProgress>? decorationProgress = null,
        IProgress<HelmetToArmorMergeProgress>? helmetProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);
        ArgumentNullException.ThrowIfNull(request.EnabledDecorations);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModsRootDirectory);

        var output = ResolveOverwriteDirectory(request.Host, request.ModsRootDirectory);
        var decorations = request.EnabledDecorations.ToArray();
        if (!request.HelmetToArmorMerged)
        {
            if (decorations.Length != 0)
            {
                var decoration = await _decorationRebuild.RebuildHostAsync(
                    request.Host,
                    decorations,
                    request.ModsRootDirectory,
                    request.GameDataDirectory,
                    cancellationToken,
                    decorationProgress).ConfigureAwait(false);
                return decoration.Succeeded
                    ? HostDerivedOverwriteResult.Success(output, decorationRebuild: decoration, issues: decoration.Issues)
                    : HostDerivedOverwriteResult.Failure(output, decoration, null, decoration.Issues);
            }

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
            return HostDerivedOverwriteResult.Failure(output, null, null, issues);
        }

        if (decorations.Length == 0)
            return await BuildHelmetOverwriteFromSourceAsync(request, output, inspection.Candidates, cancellationToken, helmetProgress).ConfigureAwait(false);

        // The decoration rebuild writes only the Patch families it edits. Its output
        // must therefore be overlaid onto the original host before helmet planning,
        // otherwise a pair in an untouched Patch group would be invisible here.
        var decorationResult = await _decorationRebuild.RebuildHostAsync(
            request.Host,
            decorations,
            request.ModsRootDirectory,
            request.GameDataDirectory,
            cancellationToken,
            decorationProgress).ConfigureAwait(false);
        if (!decorationResult.Succeeded)
            return HostDerivedOverwriteResult.Failure(output, decorationResult, null, decorationResult.Issues);

        return await ApplyHelmetMergeToGeneratedOverwriteAsync(
            request,
            output,
            decorationResult,
            inspection.Candidates,
            cancellationToken,
            helmetProgress).ConfigureAwait(false);
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
                return HostDerivedOverwriteResult.Failure(overwrite, null, result, result.Issues);
            }

            await File.WriteAllTextAsync(
                Path.Combine(overwrite, DecorationHostRebuildService.HelmetMergeOverwriteMarkerName),
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

    private async Task<HostDerivedOverwriteResult> ApplyHelmetMergeToGeneratedOverwriteAsync(
        HostDerivedOverwriteRequest request,
        string overwrite,
        DecorationHostRebuildResult decorationResult,
        IReadOnlyCollection<HelmetToArmorMergeCandidate> candidates,
        CancellationToken cancellationToken,
        IProgress<HelmetToArmorMergeProgress>? helmetProgress)
    {
        var temporary = overwrite + ".helmet-merge-" + Guid.NewGuid().ToString("N");
        var effectiveSource = CreateEffectiveHelmetMergeSource(request.Host, request.ModsRootDirectory, overwrite, cancellationToken);
        DeleteDirectoryIfExists(temporary);
        try
        {
            var result = await _helmetToArmorMerge.ExecuteAsync(
                request.Host,
                effectiveSource,
                temporary,
                request.GameDataDirectory,
                candidates,
                cancellationToken,
                helmetProgress).ConfigureAwait(false);
            if (!result.IsSuccessful)
                return HostDerivedOverwriteResult.Failure(overwrite, decorationResult, result, result.Issues);

            await File.WriteAllTextAsync(
                Path.Combine(temporary, DecorationHostRebuildService.HelmetMergeOverwriteMarkerName),
                "generated",
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(temporary, DecorationHostRebuildService.DecorationOverwriteMarkerName),
                "generated",
                cancellationToken).ConfigureAwait(false);

            var backup = PrepareManagedOverwrite(overwrite);
            try
            {
                Directory.Move(temporary, overwrite);
                if (backup is not null) DeleteDirectoryIfExists(backup);
            }
            catch
            {
                RestoreManagedOverwrite(overwrite, backup);
                throw;
            }
            return HostDerivedOverwriteResult.Success(overwrite, decorationResult, result, issues: MergeIssues(decorationResult.Issues, result.Issues));
        }
        finally
        {
            DeleteDirectoryIfExists(temporary);
            DeleteDirectoryIfExists(effectiveSource);
        }
    }

    private string CreateEffectiveHelmetMergeSource(
        ModNode host,
        string modsRootDirectory,
        string decorationOverwrite,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = ResolveHostDirectory(host, modsRootDirectory);
        var staging = Path.Combine(Path.GetTempPath(), "HD2ModManager-HelmetMerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            CopyPatchFamilies(sourceDirectory, staging, cancellationToken);
            CopyPatchFamilies(decorationOverwrite, staging, cancellationToken);
            return staging;
        }
        catch
        {
            DeleteDirectoryIfExists(staging);
            throw;
        }
    }

    private void CopyPatchFamilies(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_patchFileNameParser.TryParse(Path.GetFileName(path), out var info)
                || info?.SidecarKind != PatchSidecarKind.Base)
                continue;

            CopyFile(path, destinationDirectory);
            CopyOptionalSidecar(path + ".stream", destinationDirectory);
            CopyOptionalSidecar(path + ".gpu_resources", destinationDirectory);
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

    private static bool IsManagedOverwrite(string directory)
        => File.Exists(Path.Combine(directory, DecorationHostRebuildService.HelmetMergeOverwriteMarkerName))
           || File.Exists(Path.Combine(directory, DecorationHostRebuildService.DecorationOverwriteMarkerName));

    private static void RestoreManagedOverwrite(string directory, string? backup)
    {
        DeleteDirectoryIfExists(directory);
        if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, directory);
    }

    private static void CopyOptionalSidecar(string path, string destinationDirectory)
    {
        if (File.Exists(path)) CopyFile(path, destinationDirectory);
    }

    private static void CopyFile(string sourcePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), overwrite: true);
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

    private static IReadOnlyList<CoreIssue> MergeIssues(
        IReadOnlyList<CoreIssue> left,
        IReadOnlyList<CoreIssue> right)
        => left.Concat(right).ToArray();

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed record OverwriteCleanupResult(bool Removed);
}
