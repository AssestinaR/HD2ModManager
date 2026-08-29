using HD2ModCore.Application;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Keeps unit tests on the normal information-center fallback path unless a
// test explicitly supplies persisted profile facts.
internal sealed class EmptyReferenceGraphQueryIndex : IReferenceGraphQueryIndex
{
    public ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(
        AdaptationAssetKey targetAssetKey,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ModAssetConsumerFact>>(Array.Empty<ModAssetConsumerFact>());
}
