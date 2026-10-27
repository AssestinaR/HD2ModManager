using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证物理删除的两条不变量——成功时目录与回收区都不留痕；失败时不留空事务目录。
// Purpose: Verifies the two physical-deletion invariants: a successful delete leaves no directory and no
// trash, and a failed one never leaves an empty quarantine transaction directory behind.
public sealed class ModStorageDeletionServiceTests
{
	[Fact]
	public async Task DeleteAsync_RemovesDirectoryAndLeavesNoTrashOrPendingDocument()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var modDirectory = Path.Combine(paths.ModsDirectory, "mod");
			Directory.CreateDirectory(modDirectory);
			File.WriteAllText(Path.Combine(modDirectory, "9ba626afa44a3aa3.patch_0"), "x");

			var result = await new ModStorageDeletionService(paths).DeleteAsync("mod");

			Assert.Equal(ModStorageDeletionState.DeletedImmediately, result.State);
			Assert.True(result.CanRemoveLibraryNode);
			Assert.False(Directory.Exists(modDirectory));
			Assert.False(Directory.Exists(paths.DeletionTrashDirectory));
			Assert.False(File.Exists(paths.PendingDeletionPath));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task DeleteAsync_WhenDirectoryIsLocked_NeverLeavesEmptyQuarantineTransactionDirectory()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			var modDirectory = Path.Combine(paths.ModsDirectory, "locked-mod");
			Directory.CreateDirectory(modDirectory);
			var locked = Path.Combine(modDirectory, "9ba626afa44a3aa3.patch_0");
			File.WriteAllText(locked, "x");

			ModStorageDeletionResult result;
			using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
				result = await new ModStorageDeletionService(paths).DeleteAsync("locked-mod");

			// 未提权时 MoveFileEx 可能失败，所以既可能是 PendingRestart 也可能是 Failed；
			// 两种情况下目录都还在，且回收区里不允许留下空的 GUID 事务目录。
			// Without elevation MoveFileEx may fail, so either PendingRestart or Failed is valid; in both
			// cases the directory survives and the quarantine must not keep an empty GUID transaction dir.
			Assert.NotEqual(ModStorageDeletionState.DeletedImmediately, result.State);
			Assert.True(Directory.Exists(modDirectory));
			if (Directory.Exists(paths.DeletionTrashDirectory))
			{
				Assert.Empty(Directory.EnumerateDirectories(paths.DeletionTrashDirectory));
			}
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task DeleteAsync_MissingDirectory_IsReportedAsMissingAndRemovable()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.ModsDirectory);

			var result = await new ModStorageDeletionService(paths).DeleteAsync("never-existed");

			Assert.Equal(ModStorageDeletionState.Missing, result.State);
			Assert.True(result.CanRemoveLibraryNode);
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task DeleteAsync_RejectsPathOutsideModsRoot()
	{
		var root = CreateRoot();
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.ModsDirectory);

			var result = await new ModStorageDeletionService(paths).DeleteAsync("../outside");

			Assert.Equal(ModStorageDeletionState.Failed, result.State);
			Assert.False(result.CanRemoveLibraryNode);
		}
		finally { DeleteRoot(root); }
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-storage-deletion-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteRoot(string root)
	{
		try
		{
			foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
				File.SetAttributes(path, FileAttributes.Normal);
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
		catch { }
	}
}
