using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 hd2mod.exe 的定位同时覆盖开发布局（管理器旁边）与发布布局（bin\ 下）。
// Purpose: Verifies CLI discovery covers both the development layout (beside the manager) and the
// release layout (under bin\).
public sealed class Hd2ModCliLocatorTests
{
	[Fact]
	public void GetCandidatePaths_PutsDevelopmentLayoutBeforeReleaseLayout()
	{
		var root = CreateRoot();
		try
		{
			var candidates = Hd2ModCliLocator.GetCandidatePaths(root);

			Assert.Equal(2, candidates.Count);
			Assert.Equal(Path.Combine(root, "hd2mod.exe"), candidates[0]);
			Assert.Equal(Path.Combine(root, "bin", "hd2mod.exe"), candidates[1]);
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void TryResolve_FindsExecutableBesideManager()
	{
		var root = CreateRoot();
		try
		{
			var expected = Path.Combine(root, "hd2mod.exe");
			File.WriteAllText(expected, string.Empty);

			Assert.Equal(expected, Hd2ModCliLocator.TryResolve(root));
		}
		finally { DeleteRoot(root); }
	}

	// 发布包的布局：根目录只有 HD2ModManager.exe，命令行程序在 bin\ 下。
	// Release layout: only HD2ModManager.exe at the root, the CLI under bin\.
	[Fact]
	public void TryResolve_FindsExecutableInReleaseBinDirectory()
	{
		var root = CreateRoot();
		try
		{
			var bin = Path.Combine(root, "bin");
			Directory.CreateDirectory(bin);
			var expected = Path.Combine(bin, "hd2mod.exe");
			File.WriteAllText(expected, string.Empty);
			File.WriteAllText(Path.Combine(root, "HD2ModManager.exe"), string.Empty);

			Assert.Equal(expected, Hd2ModCliLocator.TryResolve(root));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void TryResolve_PrefersExecutableBesideManagerWhenBothExist()
	{
		var root = CreateRoot();
		try
		{
			var beside = Path.Combine(root, "hd2mod.exe");
			File.WriteAllText(beside, string.Empty);
			Directory.CreateDirectory(Path.Combine(root, "bin"));
			File.WriteAllText(Path.Combine(root, "bin", "hd2mod.exe"), string.Empty);

			Assert.Equal(beside, Hd2ModCliLocator.TryResolve(root));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void TryResolve_ReturnsNullWhenNoCandidateExists()
	{
		var root = CreateRoot();
		try
		{
			Assert.Null(Hd2ModCliLocator.TryResolve(root));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void DescribeSearchedLocations_ListsBothCandidatePaths()
	{
		var root = CreateRoot();
		try
		{
			var description = Hd2ModCliLocator.DescribeSearchedLocations(root);

			Assert.Contains(Path.Combine(root, "hd2mod.exe"), description, StringComparison.Ordinal);
			Assert.Contains(Path.Combine(root, "bin", "hd2mod.exe"), description, StringComparison.Ordinal);
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void GetCandidatePaths_TreatsTrailingSeparatorAsSameRoot()
	{
		var root = CreateRoot();
		try
		{
			var withoutSeparator = string.Join('|', Hd2ModCliLocator.GetCandidatePaths(root));
			var withSeparator = string.Join('|', Hd2ModCliLocator.GetCandidatePaths(root + Path.DirectorySeparatorChar));

			Assert.Equal(withoutSeparator, withSeparator);
		}
		finally { DeleteRoot(root); }
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-cli-locator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteRoot(string root)
	{
		if (Directory.Exists(root)) Directory.Delete(root, true);
	}
}
