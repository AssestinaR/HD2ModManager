using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Reads explicitly declared equipment relationships from the installed game, not from community name lists.
public interface IGameDataAuthorityProvider
{
	ValueTask<GameDataAuthoritySnapshot> LoadAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default,
		bool refreshPlaintextCache = false);
}
