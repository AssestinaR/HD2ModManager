using System.IO;
using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;

namespace HD2ModManager.Services;

// Desktop compatibility adapter. The Canonical rebuild implementation lives in
// Core so CLI and WPF execute the exact same source/overwrite transaction.
public sealed class DecorationAttachmentService
{
    private readonly DecorationHostRebuildService _core;

    public DecorationAttachmentService(StoragePaths paths, IModInformationReader informationReader)
    {
        _core = CoreServices.CreateDecorationHostRebuildService(paths, informationReader);
    }

    public async Task RebuildHostAsync(
        ModNode host,
        IEnumerable<(ModEntity Mod, DecorationPlanDocument Plan)> enabled,
        string modsRoot,
        string gameDataDirectory,
        CancellationToken cancellationToken = default,
        IProgress<DecorationOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(enabled);

        var attachments = enabled.Select(item => new DecorationHostRebuildAttachment(
            item.Mod.Guid,
            item.Mod.SourcePath ?? string.Empty,
            item.Plan)).ToArray();
        var result = await _core.RebuildHostAsync(
            host,
            attachments,
            modsRoot,
            gameDataDirectory,
            cancellationToken,
            progress).ConfigureAwait(false);
        if (result.Succeeded) return;

        var error = result.Issues.FirstOrDefault(item => item.Severity == CoreIssueSeverity.Error)
            ?? result.Issues.FirstOrDefault();
        var message = error?.Message ?? "装饰主体重建失败。";
        if (string.Equals(error?.Code, "DecorationGameDataMissing", StringComparison.Ordinal))
            throw new InvalidOperationException(message);
        throw new InvalidDataException(message);
    }
}
