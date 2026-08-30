using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Creates and writes a non-destructive same-AssetKey current-version reconstruction for one flat library Mod.
public interface IModSameKeyReconstructionService
{
	ValueTask<ModSameKeyReconstructionState> InspectAsync(
		ModNode source,
		string modsRootDirectory,
		string gameDataDirectory,
		CancellationToken cancellationToken = default,
		IProgress<OperationProgressEvent>? progress = null,
		Guid? operationId = null,
		string? sourcePatchTocPath = null);

	ValueTask<SameKeyReconstructionOperationResult> GenerateCandidateAsync(
		ModNode source,
		string modsRootDirectory,
		string gameDataDirectory,
		string outputRootDirectory,
		CancellationToken cancellationToken = default,
		IProgress<OperationProgressEvent>? progress = null,
		Guid? operationId = null,
		bool useSharedHiddenUnitTemplate = true,
		string? sourcePatchTocPath = null);
}
