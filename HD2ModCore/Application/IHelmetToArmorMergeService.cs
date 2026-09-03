using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：统一提供头盔合并到盔甲的候选识别和 Patch 输出能力。
// Purpose: Provides one read/plan/write boundary for helmet-to-armor merging.
public interface IHelmetToArmorMergeService
{
	ValueTask<HelmetToArmorMergeInspection> InspectAsync(
		ModNode source,
		string modsRootDirectory,
		string gameDataDirectory,
		CancellationToken cancellationToken = default);

	ValueTask<HelmetToArmorMergeResult> ExecuteAsync(
		ModNode source,
		string sourceDirectory,
		string outputDirectory,
		string gameDataDirectory,
		IReadOnlyCollection<HelmetToArmorMergeCandidate>? selectedCandidates = null,
		CancellationToken cancellationToken = default,
		IProgress<HelmetToArmorMergeProgress>? progress = null,
		HelmetToArmorMergeOutputMode outputMode = HelmetToArmorMergeOutputMode.FirstHelmetOnly);
}

public sealed record HelmetToArmorMergeProgress(
	string Stage,
	int Completed,
	int Total)
{
	public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}
