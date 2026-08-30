using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ModLibrarySynchronizerTests
{
	[Fact]
	public async Task SynchronizeAsync_PendingDeletionPath_IsNotReintroducedAsExternalMod()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var mods = Path.Combine(root, "mods");
			var stale = Path.Combine(mods, "stale");
			Directory.CreateDirectory(stale);
			await File.WriteAllTextAsync(Path.Combine(stale, "9ba626afa44a3aa3.patch_0"), "test");
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>());
			var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());

			var result = await synchronizer.SynchronizeAsync(
				snapshot,
				mods,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stale" });

			Assert.Empty(result.AddedNodeIds);
			Assert.Empty(result.Snapshot.Nodes);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
