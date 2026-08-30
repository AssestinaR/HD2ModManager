using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class GameDataDeploymentLeaseProviderTests
{
	[Fact]
	public async Task AcquireAsync_ExcludesConcurrentLease_AndDeletesLockOnDispose()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2-deployment-lease-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var provider = new GameDataDeploymentLeaseProvider();
			var lockPath = Path.Combine(directory, ".hd2mod-deployment.lock");
			{
				await using var first = await provider.AcquireAsync(directory);
				Assert.True(File.Exists(lockPath));
				await Assert.ThrowsAsync<GameDataDeploymentLeaseUnavailableException>(async () =>
				{
					await using var ignored = await provider.AcquireAsync(directory);
				});
			}
			Assert.False(File.Exists(lockPath));
		}
		finally
		{
			try { Directory.Delete(directory, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task AcquireAsync_RejectsMissingDirectory()
	{
		var missing = Path.Combine(Path.GetTempPath(), "hd2-deployment-lease-missing-" + Guid.NewGuid().ToString("N"));
		await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
		{
			await using var ignored = await new GameDataDeploymentLeaseProvider().AcquireAsync(missing);
		});
	}
}
