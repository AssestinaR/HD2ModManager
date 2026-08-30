using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// UI-neutral boundary for rebuilding the decoration-derived Overwrite layer.
// Keeping this contract separate from the concrete Canonical implementation lets
// the host coordinator be tested without performing Patch reconstruction.
public interface IDecorationHostRebuildService
{
    Task<DecorationHostRebuildResult> RebuildHostAsync(
        ModNode host,
        IEnumerable<DecorationHostRebuildAttachment> enabled,
        string modsRoot,
        string gameDataDirectory,
        CancellationToken cancellationToken = default,
        IProgress<DecorationOperationProgress>? progress = null);
}
