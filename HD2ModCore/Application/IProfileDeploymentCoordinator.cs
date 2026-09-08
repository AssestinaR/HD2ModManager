using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Serializes buffered profile deployment, immediate flush and deactivation.
public interface IProfileDeploymentCoordinator : IAsyncDisposable
{
	ProfileDeploymentStatus Status { get; }
	event EventHandler<ProfileDeploymentStatus>? StatusChanged;
	void NotifyActiveProfileChanged();
	Task<ProfileDeploymentStatus> FlushAsync(CancellationToken cancellationToken = default, LuaDeploymentPermission? luaPermission = null);
	Task DeactivateAsync(CancellationToken cancellationToken = default);
}
