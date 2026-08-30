using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class LibraryWriteLeaseProviderTests
{
	[Fact]
	public async Task AcquireAsync_UsesAnExclusiveLeaseUntilItIsReleased()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var firstProvider = new LibraryWriteLeaseProvider();
			var secondProvider = new LibraryWriteLeaseProvider();
			var first = await firstProvider.AcquireAsync(root);
			try
			{
				var exception = await Assert.ThrowsAsync<LibraryWriteLeaseUnavailableException>(async () =>
				{
					await secondProvider.AcquireAsync(root);
				});
				Assert.Equal(Path.GetFullPath(root), exception.LibraryRoot);
			}
			finally
			{
				await first.DisposeAsync();
			}

			await using var replacement = await secondProvider.AcquireAsync(root);
			Assert.Equal(Path.GetFullPath(root), replacement.LibraryRoot);
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
