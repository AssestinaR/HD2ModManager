using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住"把一份 patch 装进 Overwrite"这个原语。
/// <para>
/// 它以前在换甲里有两条一模一样的实现。漂了的表现是 sidecar 与新 TOC 不配对——
/// 游戏里材质全黑或加载失败，而那种错在包里看不出来。所以这几条用例盯的是
/// **旧版本必须被清干净**，而不是"新文件写进去了"。
/// </para>
/// </summary>
public sealed class OverwritePatchInstallerTests : IDisposable
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-overwrite-installer-tests", Guid.NewGuid().ToString("N"));

	public OverwritePatchInstallerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				File.SetAttributes(path, System.IO.FileAttributes.Normal);
			Directory.Delete(_root, recursive: true);
		}
		catch { }
	}

	private string NewDir(string name)
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	private string NewPatch(string directory, string name = PatchName, bool withSidecars = true)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, name);
		File.WriteAllText(path, "toc");
		if (withSidecars)
		{
			File.WriteAllText(path + ".gpu_resources", "gpu");
			File.WriteAllText(path + ".stream", "stream");
		}
		return path;
	}

	[Fact]
	public void Install_CopiesPatchAndEveryExistingSidecar()
	{
		var source = NewPatch(NewDir("source"));
		var overwrite = NewDir("overwrite");

		var result = OverwritePatchInstaller.Install(overwrite, source);

		Assert.Equal(PatchName, Path.GetFileName(result.PatchPath));
		Assert.Equal(3, result.WrittenFiles.Count);
		Assert.True(File.Exists(Path.Combine(overwrite, PatchName)));
		Assert.True(File.Exists(Path.Combine(overwrite, PatchName + ".gpu_resources")));
		Assert.True(File.Exists(Path.Combine(overwrite, PatchName + ".stream")));
	}

	[Fact]
	public void Install_SkipsSidecarsTheSourceDoesNotHave()
	{
		var source = NewPatch(NewDir("source"), withSidecars: false);
		var overwrite = NewDir("overwrite");

		var result = OverwritePatchInstaller.Install(overwrite, source);

		Assert.Single(result.WrittenFiles);
		Assert.False(File.Exists(Path.Combine(overwrite, PatchName + ".gpu_resources")));
	}

	[Fact]
	public void Install_RemovesThePreviousVariantOfTheSamePatch()
	{
		var overwrite = NewDir("overwrite");
		// 旧变体：TOC 与 sidecar 都在。
		File.WriteAllText(Path.Combine(overwrite, PatchName), "old-toc");
		File.WriteAllText(Path.Combine(overwrite, PatchName + ".gpu_resources"), "old-gpu");
		// 新来源**没有** .stream：旧的那份必须消失，否则新旧 sidecar 会错配。
		File.WriteAllText(Path.Combine(overwrite, PatchName + ".stream"), "stale-stream");
		var source = NewPatch(NewDir("source"), withSidecars: false);

		OverwritePatchInstaller.Install(overwrite, source);

		Assert.Equal("toc", File.ReadAllText(Path.Combine(overwrite, PatchName)));
		Assert.False(File.Exists(Path.Combine(overwrite, PatchName + ".stream")));
	}

	[Fact]
	public void Install_LeavesOtherPatchesAndMetadataAlone()
	{
		var overwrite = NewDir("overwrite");
		File.WriteAllText(Path.Combine(overwrite, "9ba626afa44a3aa3.patch_1"), "other-variant");
		File.WriteAllText(Path.Combine(overwrite, "sections.json"), "{}");
		File.WriteAllText(Path.Combine(overwrite, "components.json"), "{}");
		var source = NewPatch(NewDir("source"));

		OverwritePatchInstaller.Install(overwrite, source);

		Assert.Equal("other-variant", File.ReadAllText(Path.Combine(overwrite, "9ba626afa44a3aa3.patch_1")));
		Assert.True(File.Exists(Path.Combine(overwrite, "sections.json")));
		Assert.True(File.Exists(Path.Combine(overwrite, "components.json")));
	}

	[Fact]
	public void Install_OverwritesReadOnlyFiles()
	{
		// 库里的产物是只读的：装新变体时必须先清属性，否则 File.Copy 会抛
		// "Access to the path ... is denied"——那句话一个字都没提只读。
		var overwrite = NewDir("overwrite");
		var existing = Path.Combine(overwrite, PatchName);
		File.WriteAllText(existing, "old");
		File.SetAttributes(existing, System.IO.FileAttributes.ReadOnly);
		var source = NewPatch(NewDir("source"));

		OverwritePatchInstaller.Install(overwrite, source);

		Assert.Equal("toc", File.ReadAllText(existing));
	}

	[Fact]
	public void Install_ThrowsWhenSourcePatchIsMissing()
	{
		var overwrite = NewDir("overwrite");

		Assert.Throws<FileNotFoundException>(() =>
			OverwritePatchInstaller.Install(overwrite, Path.Combine(_root, "nope", PatchName)));
	}

	[Fact]
	public void Remove_DeletesTheVariantAndItsSidecars_ButNotOthers()
	{
		var overwrite = NewDir("overwrite");
		File.WriteAllText(Path.Combine(overwrite, PatchName), "toc");
		File.WriteAllText(Path.Combine(overwrite, PatchName + ".gpu_resources"), "gpu");
		File.WriteAllText(Path.Combine(overwrite, "9ba626afa44a3aa3.patch_1"), "other");

		Assert.True(OverwritePatchInstaller.Remove(overwrite, PatchName));

		Assert.False(File.Exists(Path.Combine(overwrite, PatchName)));
		Assert.False(File.Exists(Path.Combine(overwrite, PatchName + ".gpu_resources")));
		Assert.True(File.Exists(Path.Combine(overwrite, "9ba626afa44a3aa3.patch_1")));
	}

	[Fact]
	public void Remove_OnMissingDirectory_ReturnsFalse()
		=> Assert.False(OverwritePatchInstaller.Remove(Path.Combine(_root, "nope"), PatchName));
}
