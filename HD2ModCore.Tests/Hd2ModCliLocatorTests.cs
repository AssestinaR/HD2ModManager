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

	// ---- 管理器根目录的推导（1.5.5 整批导入失败的根因） --------------------------
	//
	// 发布包：<安装根>\HD2ModManager.exe + <安装根>\bin\hd2mod.exe。
	// 命令行程序自己的目录是 bin\，管理器根是它的上一级。取错这一级，CLI 会去找
	// bin\settings.json 与 bin\data\indexes\asset-index.sqlite —— 两个都不存在，
	// 于是每个 VPK 导入都在规划阶段以 PrerequisitesMissing 失败。

	[Fact]
	public void ResolveManagerRoot_KeepsTheDirectoryWhenItIsAManagerRoot()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), "{}");

			Assert.Equal(root, Hd2ModCliLocator.ResolveManagerRoot(root));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void ResolveManagerRoot_KeepsTheDirectoryWhenOnlyTheDataDirectoryExists()
	{
		// 刚装好、还没跑过一次管理器时只有 data\（发布包自带 data\resources\*），没有 settings.json。
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, Hd2ModCliLocator.DataDirectoryName));

			Assert.Equal(root, Hd2ModCliLocator.ResolveManagerRoot(root));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void ResolveManagerRoot_EscapesTheReleaseBinDirectoryToTheInstallRoot()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), "{}");
			Directory.CreateDirectory(Path.Combine(root, Hd2ModCliLocator.DataDirectoryName));
			var bin = Path.Combine(root, Hd2ModCliLocator.ReleaseSubdirectory);
			Directory.CreateDirectory(bin);
			File.WriteAllText(Path.Combine(bin, Hd2ModCliLocator.ExecutableFileName), string.Empty);

			// 发布布局：从 bin\ 出发必须落到安装根，而不是留在 bin\。
			Assert.Equal(root, Hd2ModCliLocator.ResolveManagerRoot(bin));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void ResolveManagerRoot_DoesNotEscapeFromABinDirectoryWhoseParentIsNotManagerRoot()
	{
		// 只有"上一级确实像管理器根"才往上走：否则会一路滑到某个无关目录上去。
		var root = CreateRoot();
		try
		{
			var bin = Path.Combine(root, Hd2ModCliLocator.ReleaseSubdirectory);
			Directory.CreateDirectory(bin);

			Assert.Equal(bin, Hd2ModCliLocator.ResolveManagerRoot(bin));
			Assert.Null(Hd2ModCliLocator.TryGetManagerRootParent(bin));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void ResolveManagerRoot_DoesNotEscapeFromDirectoriesThatAreNotNamedBin()
	{
		// 开发构建：程序在 <输出目录>\net10.0\ 里，就算上层有 settings.json 也不许往上跳 ——
		// 那会把开发环境里的设置与运行中的那个管理器混在一起。
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), "{}");
			var nested = Path.Combine(root, "bin", "Debug", "net10.0");
			Directory.CreateDirectory(nested);

			Assert.Equal(nested, Hd2ModCliLocator.ResolveManagerRoot(nested));
			Assert.Null(Hd2ModCliLocator.TryGetManagerRootParent(nested));
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public void ResolveManagerRoot_TreatsTrailingSeparatorAsSameRoot()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), "{}");
			var bin = Path.Combine(root, Hd2ModCliLocator.ReleaseSubdirectory);
			Directory.CreateDirectory(bin);

			// AppContext.BaseDirectory 总是带结尾分隔符；不剪掉的话 GetFileName 返回空串，
			// "这个目录是不是 bin" 就永远判不出来。
			Assert.Equal(root, Hd2ModCliLocator.ResolveManagerRoot(bin + Path.DirectorySeparatorChar));
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
