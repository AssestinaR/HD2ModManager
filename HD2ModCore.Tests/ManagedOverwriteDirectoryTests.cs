using HD2ModCore.Decoration;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住托管 <c>Overwrite/</c> 的归属与事务语义。
/// <para>
/// Overwrite 是"派生出来、真正会进游戏的那一层"。它的安全性全押在一条规矩上：
/// **只覆盖/删除自己生成的东西**。这些用例把那条规矩的每个分支钉住——
/// 放宽任何一条，用户手工放进来的补丁都会在下一次派生里被静默删掉。
/// </para>
/// </summary>
public sealed class ManagedOverwriteDirectoryTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-managed-overwrite-tests", Guid.NewGuid().ToString("N"));

	public ManagedOverwriteDirectoryTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewDirectory(string name = "overwrite")
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	[Fact]
	public void IsManaged_IsFalse_ForAPlainDirectory()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, "user-file.txt"), "hand made");

		Assert.False(ManagedOverwriteDirectory.IsManaged(dir));
	}

	[Theory]
	[InlineData(ManagedOverwriteMarkers.HelmetMerge)]
	[InlineData(ManagedOverwriteMarkers.VpkRekey)]
	[InlineData(ManagedOverwriteMarkers.Decoration)]
	public void IsManaged_IsTrue_ForAnyMarker(string marker)
	{
		var dir = NewDirectory("marked-" + marker.Replace('.', '_'));
		File.WriteAllText(Path.Combine(dir, marker), "generated");

		Assert.True(ManagedOverwriteDirectory.IsManaged(dir));
	}

	[Fact]
	public void Prepare_RefusesAForeignDirectoryWithContent()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, "user-file.txt"), "hand made");

		Assert.Throws<InvalidOperationException>(() => ManagedOverwriteDirectory.Prepare(dir));
		// 拒绝之后文件必须原样还在——这条是整件事的意义所在。
		Assert.True(File.Exists(Path.Combine(dir, "user-file.txt")));
	}

	[Fact]
	public void Prepare_TakesOverAnEmptyDirectory()
	{
		var dir = NewDirectory();

		var backup = ManagedOverwriteDirectory.Prepare(dir);

		Assert.Null(backup);
		Assert.True(Directory.Exists(dir));
		Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
	}

	[Fact]
	public void Prepare_CreatesMissingDirectory()
	{
		var dir = Path.Combine(_root, "fresh");

		var backup = ManagedOverwriteDirectory.Prepare(dir);

		Assert.Null(backup);
		Assert.True(Directory.Exists(dir));
	}

	[Fact]
	public void Prepare_MovesManagedContentToBackup()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, ManagedOverwriteMarkers.VpkRekey), "generated");
		File.WriteAllText(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), "old");

		var backup = ManagedOverwriteDirectory.Prepare(dir);

		Assert.NotNull(backup);
		Assert.True(Directory.Exists(backup!));
		Assert.True(File.Exists(Path.Combine(backup!, "9ba626afa44a3aa3.patch_0")));
		Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
	}

	[Fact]
	public void Commit_WritesMarker_SoLaterOperationsRecogniseIt()
	{
		var dir = NewDirectory();
		ManagedOverwriteDirectory.Commit(dir, ManagedOverwriteMarkers.VpkRekey);

		Assert.True(ManagedOverwriteDirectory.IsManaged(dir));
	}

	[Fact]
	public void Rollback_RestoresTheBackup()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, ManagedOverwriteMarkers.HelmetMerge), "generated");
		File.WriteAllText(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), "previous");
		var backup = ManagedOverwriteDirectory.Prepare(dir);
		File.WriteAllText(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), "half-written");

		ManagedOverwriteDirectory.Rollback(dir, backup);

		Assert.Equal("previous", File.ReadAllText(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
	}

	[Fact]
	public void RemoveIfManaged_LeavesForeignContentAlone()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, "user-file.txt"), "hand made");

		Assert.False(ManagedOverwriteDirectory.RemoveIfManaged(dir));
		Assert.True(File.Exists(Path.Combine(dir, "user-file.txt")));
	}

	[Fact]
	public void RemoveIfManaged_DeletesManagedContent()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, ManagedOverwriteMarkers.HelmetMerge), "generated");

		Assert.True(ManagedOverwriteDirectory.RemoveIfManaged(dir));
		Assert.False(Directory.Exists(dir));
	}

	[Fact]
	public void RemoveManaged_ThrowsForForeignDirectory()
	{
		var dir = NewDirectory();
		File.WriteAllText(Path.Combine(dir, "user-file.txt"), "hand made");

		Assert.Throws<InvalidOperationException>(() => ManagedOverwriteDirectory.RemoveManaged(dir));
		Assert.True(File.Exists(Path.Combine(dir, "user-file.txt")));
	}

	[Fact]
	public void DiscardBackup_ToleratesMissingBackup()
	{
		ManagedOverwriteDirectory.DiscardBackup(null);
		ManagedOverwriteDirectory.DiscardBackup(Path.Combine(_root, "does-not-exist"));
	}
}
