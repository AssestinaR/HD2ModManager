using HD2ModCore.Decoration;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「哪些文件会进游戏」的判定。
/// <para>
/// 折并之后宿主里会有最多十几个材质 patch，只有一份该生效。规则必须是：
/// **管理器物化过的 Overwrite 完全接管**；没有归属标记的目录走原来的两层合并。
/// 放宽前半句会让十三份材质一起部署、互相覆盖；收紧后半句会让用户手工放的补丁凭空消失。
/// </para>
/// </summary>
public sealed class ModDeployableFilesTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-deployable-files-tests", Guid.NewGuid().ToString("N"));

	public ModDeployableFilesTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewMod()
	{
		var root = Path.Combine(_root, "mod");
		Directory.CreateDirectory(root);
		return root;
	}

	private static void Write(string directory, string name, string content = "payload")
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name), content);
	}

	[Fact]
	public void ManagedOverwrite_TakesOverCompletely()
	{
		var mod = NewMod();
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		Write(payload, $"{Archive}.patch_0", "baseline-unit");
		Write(payload, $"{Archive}.patch_5", "unselected-variant");
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(mod);
		Write(overwrite, $"{Archive}.patch_0", "rekeyed-unit");
		Write(overwrite, ManagedOverwriteMarkers.VpkRekey, "generated");

		var files = ModDeployableFiles.EnumerateByName(mod);

		Assert.Equal("rekeyed-unit", File.ReadAllText(files[$"{Archive}.patch_0"]));
		// 未被选中的变体**不能**跟着部署：它会和生效层里那份互相覆盖。
		Assert.False(files.ContainsKey($"{Archive}.patch_5"));
	}

	[Fact]
	public void NoOverwrite_FallsBackToTheTwoLayerMerge()
	{
		var mod = NewMod();
		Write(ModNodePayloadLayout.GetVpkPatchDirectory(mod), $"{Archive}.patch_0");
		Write(mod, $"{Archive}.patch_1", "root-patch");

		var files = ModDeployableFiles.EnumerateByName(mod);

		Assert.True(files.ContainsKey($"{Archive}.patch_0"));
		Assert.True(files.ContainsKey($"{Archive}.patch_1"));
	}

	[Fact]
	public void UnmanagedOverwrite_KeepsTheOldBehaviour()
	{
		// 没有归属标记的 Overwrite 可能是用户手工放的：仍按**旧的两层合并**处理
		// ——而且旧规则还要求它"本身是一份可读的 patch TOC"才顶替。
		// 这里的纯文本不满足那条，所以基底版本继续生效、其余基底 patch 照常部署。
		// 重点是不改成"只认 Overwrite"：那会让这些补丁凭空消失。
		var mod = NewMod();
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		Write(payload, $"{Archive}.patch_0", "baseline-unit");
		Write(payload, $"{Archive}.patch_3", "other-payload");
		Write(ModNodePayloadLayout.GetOverwriteDirectory(mod), $"{Archive}.patch_0", "hand-made");

		var files = ModDeployableFiles.EnumerateByName(mod);

		Assert.Equal("baseline-unit", File.ReadAllText(files[$"{Archive}.patch_0"]));
		Assert.True(files.ContainsKey($"{Archive}.patch_3"));
	}

	[Fact]
	public void EmptyMod_YieldsNothing()
	{
		var mod = NewMod();

		Assert.Empty(ModDeployableFiles.EnumerateByName(mod));
	}
}
