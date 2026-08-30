namespace HD2ModCore.Application;

/// <summary>
/// Cross-process lease for one already-validated Helldivers 2 data directory.
/// Callers take the library lease first, then this lease, so CLI and desktop
/// deployment paths have one stable lock order.
/// </summary>
public interface IGameDataDeploymentLease : IAsyncDisposable
{
	string GameDataDirectory { get; }
}

public interface IGameDataDeploymentLeaseProvider
{
	ValueTask<IGameDataDeploymentLease> AcquireAsync(string gameDataDirectory, CancellationToken cancellationToken = default);
}

public sealed class GameDataDeploymentLeaseUnavailableException : IOException
{
	public GameDataDeploymentLeaseUnavailableException(string gameDataDirectory, Exception innerException)
		: base($"Another HD2 Mod deployment is already active for GameData: {gameDataDirectory}", innerException)
	{
		GameDataDirectory = gameDataDirectory;
	}

	public string GameDataDirectory { get; }
}
