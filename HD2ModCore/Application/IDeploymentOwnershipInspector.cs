using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Pure read-only inspection of whether top-level GameData patches are provably managed by this manager.
public interface IDeploymentOwnershipInspector
{
	ValueTask<DeploymentOwnershipInspection> InspectAsync(
		string? gameDataDirectory,
		CancellationToken cancellationToken = default);
}
