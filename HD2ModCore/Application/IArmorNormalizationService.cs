using HD2ModCore.Domain;

namespace HD2ModCore.Application;

public interface IArmorNormalizationService
{
    ValueTask<ArmorNormalizationInspection> InspectAsync(ModNode source, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default);
    ValueTask<ArmorNormalizationResult> ExecuteAsync(ModNode source, string sourceDirectory, string outputDirectory, string gameDataDirectory, CancellationToken cancellationToken = default, IProgress<ArmorNormalizationProgress>? progress = null);
}

public sealed record ArmorNormalizationProgress(string Stage, int Completed, int Total);
