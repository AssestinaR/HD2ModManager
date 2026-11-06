using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 锁住 `library cleanup` 的两条边界。
/// <para>
/// 都是真机上撞出来的：一个是"清不掉的 78 MB 垃圾文件"，一个是"差点把用户映射的回滚点删了"。
/// </para>
/// </summary>
public sealed class LibraryCleanupScopeTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-cleanup-scope-" + Guid.NewGuid().ToString("N"));

	public LibraryCleanupScopeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void Scan_CoversJunkFilesAndNotOnlyDirectories()
	{
		// 退役文件夹里曾经躺着一个 78 MB 的普通文件 `vpk2patch.bak-*`。
		// 只扫目录的话它永远清不掉，还会因为扩展名"不无害"把整个文件夹卡成不可删。
		var mod = Path.Combine(_root, "mod");
		Directory.CreateDirectory(mod);
		File.WriteAllText(Path.Combine(mod, "vpk2patch.bak-20260101-000000"), "junk");
		Directory.CreateDirectory(Path.Combine(mod, "Overwrite.bak-20260101-000000"));

		var entries = CliLibraryCleanup.Scan(_root);

		Assert.Contains(entries, entry => entry.RelativePath.EndsWith("vpk2patch.bak-20260101-000000", StringComparison.Ordinal));
		Assert.Contains(entries, entry => entry.RelativePath.EndsWith("Overwrite.bak-20260101-000000", StringComparison.Ordinal));
	}

	[Fact]
	public void Scan_LeavesManagerOwnedDirectoriesAlone()
	{
		// `.hd2modmanager\armor-registry.workspace.json.bak-*` 是用户护甲映射的回滚点。
		// 把它当垃圾删掉等于抹掉用户的映射历史。
		var manager = Path.Combine(_root, CustomModelCatalog.ManagerDirectoryPrefix);
		Directory.CreateDirectory(manager);
		File.WriteAllText(Path.Combine(manager, "armor-registry.workspace.json.bak-20261017-111110"), "{}");
		var mod = Path.Combine(_root, "mod");
		Directory.CreateDirectory(mod);
		File.WriteAllText(Path.Combine(mod, "vpk2patch.bak-20260101-000000"), "junk");

		var entries = CliLibraryCleanup.Scan(_root);

		Assert.DoesNotContain(entries, entry => entry.RelativePath.Contains("armor-registry", StringComparison.Ordinal));
		Assert.Contains(entries, entry => entry.RelativePath.EndsWith("vpk2patch.bak-20260101-000000", StringComparison.Ordinal));
	}
}
