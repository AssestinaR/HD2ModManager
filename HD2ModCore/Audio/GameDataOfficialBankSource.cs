using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Audio;

// Purpose: Locates and reads current official Wwise banks straight from the game's data directory.
// The bank-id -> package table is built once per instance by scanning package TOCs, so it can never go stale
// after a game update (the standalone tool relied on a hard-coded name table instead).
public sealed class GameDataOfficialBankSource : IOfficialBankSource
{
	private readonly GameDataPackageResolver _resolver;
	private readonly IPatchTocScanner _scanner;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private Dictionary<ulong, (string Package, ulong Offset, uint Size)>? _index;
	private readonly Dictionary<ulong, byte[]?> _cache = new();

	public GameDataOfficialBankSource(string gameDataDirectory, GameDataPackageResolver? resolver = null, IPatchTocScanner? scanner = null)
	{
		_resolver = resolver ?? new GameDataPackageResolver(gameDataDirectory);
		_scanner = scanner ?? new PatchTocScanner();
	}

	public async ValueTask<byte[]?> ReadBankAsync(ulong bankFileId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_cache.TryGetValue(bankFileId, out var cached)) return cached;
			_index ??= await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
			byte[]? bank = null;
			if (_index.TryGetValue(bankFileId, out var location))
			{
				var resource = await _resolver.GetPackageResourceAsync(location.Package, location.Offset, location.Size, cancellationToken).ConfigureAwait(false);
				if (resource is { Length: >= 16 }) bank = resource[16..];
			}
			_cache[bankFileId] = bank;
			return bank;
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<Dictionary<ulong, (string Package, ulong Offset, uint Size)>> BuildIndexAsync(CancellationToken cancellationToken)
	{
		var index = new Dictionary<ulong, (string, ulong, uint)>();
		foreach (var package in await _resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			GameDataPackageToc? toc;
			IReadOnlyList<PatchTocEntry> entries;
			try
			{
				toc = await _resolver.GetPackageTocAsync(package, cancellationToken).ConfigureAwait(false);
				if (toc is null) continue;
				entries = _scanner.ScanEntries(toc.Data, package, toc.UsesSlimEntryOffset);
			}
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
			{
				continue; // unreadable data libraries are not banks
			}
			foreach (var entry in entries)
			{
				if (entry.AssetKey.TypeId != PatchPackage.BankTypeId) continue;
				index.TryAdd(entry.AssetKey.FileId, (package, entry.TocDataOffset, entry.TocDataSize));
			}
		}
		return index;
	}
}
