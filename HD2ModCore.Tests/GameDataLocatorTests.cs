using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证游戏 data 目录的自动查找规则。通过注入候选 Steam 库列表，使断言不依赖本机是否安装 Steam。
// Purpose: Verifies the game data directory auto-detection rules. The candidate Steam library list is injected so
//          assertions do not depend on whether Steam happens to be installed on the machine.
public sealed class GameDataLocatorTests
{
	[Fact]
	public void Locate_LibraryContainingGame_ReturnsGameDataDirectory()
	{
		var root = CreateRoot();
		try
		{
			var dataDirectory = CreateGame(root, "Helldivers 2");
			var location = new GameDataLocator(() => [root]).Locate();

			Assert.NotNull(location);
			Assert.Equal(dataDirectory, location!.GameDataDirectory);
			Assert.Equal(Path.GetDirectoryName(dataDirectory), location.GameDirectory);
			Assert.Equal(root, location.MatchedSteamLibrary);
			Assert.Equal([root], location.ExaminedSteamLibraries);
		}
		finally { TryDelete(root); }
	}

	[Theory]
	[InlineData("Helldivers 2")]
	[InlineData("HELLDIVERS 2")]
	public void Locate_KnownGameFolderNameVariants_AreMatched(string folderName)
	{
		var root = CreateRoot();
		try
		{
			CreateGame(root, folderName);
			var location = new GameDataLocator(() => [root]).Locate();

			Assert.NotNull(location);
			// Windows 文件系统大小写不敏感：磁盘上实际叫 "HELLDIVERS 2" 时，探测 "Helldivers 2" 也会命中同一目录，
			// 返回路径保留的是"被探测的拼写"而不是"磁盘上的拼写"。因此这里只断言命中且目录真实存在，
			// 不假定返回路径的字母大小写——否则测试会在大小写不敏感的文件系统上误报失败。
			// Windows filesystems are case-insensitive: when the folder on disk is "HELLDIVERS 2", probing for
			// "Helldivers 2" hits the same directory and the returned path keeps the probed spelling. So assert
			// that it was found and really exists, without assuming the casing of the returned path.
			Assert.True(Directory.Exists(location!.GameDataDirectory));
			Assert.Equal(folderName, Path.GetFileName(Path.GetDirectoryName(location.GameDataDirectory)), ignoreCase: true);
			Assert.Equal(root, location.MatchedSteamLibrary, ignoreCase: true);
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Locate_LibraryWithoutGame_ReturnsNull()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "steamapps", "common"));
			Assert.Null(new GameDataLocator(() => [root]).Locate());
		}
		finally { TryDelete(root); }
	}

	// data 目录必须真实存在：否则调用方会把一个不存在的路径当成检测结果保存下来。
	// The data directory must really exist, otherwise callers persist a non-existent path as a detection result.
	[Fact]
	public void Locate_GameFolderWithoutDataDirectory_ReturnsNull()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Helldivers 2"));
			Assert.Null(new GameDataLocator(() => [root]).Locate());
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Locate_MultipleLibraries_ReturnsFirstMatch()
	{
		var first = CreateRoot();
		var second = CreateRoot();
		try
		{
			CreateGame(first, "Helldivers 2");
			var secondData = CreateGame(second, "Helldivers 2");

			var location = new GameDataLocator(() => [first, second]).Locate();

			Assert.NotNull(location);
			Assert.Equal(first, location!.MatchedSteamLibrary);
			Assert.NotEqual(secondData, location.GameDataDirectory);
		}
		finally { TryDelete(first); TryDelete(second); }
	}

	[Fact]
	public void Locate_MissingLibraryBeforeMatch_IsSkippedAndNotExamined()
	{
		var root = CreateRoot();
		var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		try
		{
			CreateGame(root, "Helldivers 2");
			var location = new GameDataLocator(() => [missing, root]).Locate();

			Assert.NotNull(location);
			Assert.Equal(root, location!.MatchedSteamLibrary);
			// 不存在的候选库不应出现在"已检查"清单里，否则诊断输出会误导人。
			// A non-existent candidate must not appear in the examined list, otherwise diagnostics mislead.
			Assert.DoesNotContain(missing, location.ExaminedSteamLibraries);
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Locate_DuplicateCandidates_AreExaminedOnce()
	{
		var root = CreateRoot();
		try
		{
			CreateGame(root, "Helldivers 2");
			var location = new GameDataLocator(() => [root, root, root + Path.DirectorySeparatorChar]).Locate();

			Assert.NotNull(location);
			Assert.Single(location!.ExaminedSteamLibraries);
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Locate_CandidateEnumerationThrows_ReturnsNull()
	{
		Assert.Null(new GameDataLocator(() => throw new IOException("无法枚举磁盘。")).Locate());
	}

	// 冒烟测试：默认构造函数会真正探测本机（注册表、vdf、磁盘），不得抛出。
	// Smoke test: the default constructor probes the real machine (registry, vdf, drives) and must not throw.
	[Fact]
	public void Locate_DefaultConstructor_DoesNotThrow()
	{
		var location = new GameDataLocator().Locate();

		Assert.True(location is null || Directory.Exists(location.GameDataDirectory));
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static string CreateGame(string libraryRoot, string gameFolderName)
	{
		var dataDirectory = Path.Combine(libraryRoot, "steamapps", "common", gameFolderName, "data");
		Directory.CreateDirectory(dataDirectory);
		return dataDirectory;
	}

	private static void TryDelete(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}
}
