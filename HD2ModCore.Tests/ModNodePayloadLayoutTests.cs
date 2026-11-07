using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住"基底目录改名"的读兼容。
/// <para>
/// 2026-11 把 <c>vpk2patch/</c> 改名为 <c>sectional/</c>。老库里的目录不会自动改名，
/// 而读路径一旦只认新名，那批 Mod 的 patch 组会全部变空：对账把它们清成"没有 patch 的残留
/// 目录"，部署计划随后静默跳过 —— 表现是"部署成功、游戏里什么都没变"，没有任何报错。
/// 三处"跨程序集字面量"（物理骨骼 sidecar / 嵌入器 / 载体）尤其危险：改常量时全解决方案
/// 编译通过、单测全绿，但物理参数会静默消失。所以这里逐条钉住读路径。
/// </para>
/// </summary>
public sealed class ModNodePayloadLayoutTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-payload-layout-tests", Guid.NewGuid().ToString("N"));

	public ModNodePayloadLayoutTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Theory]
	[InlineData("sectional")]
	[InlineData("vpk2patch")]
	[InlineData("Sectional")]
	public void IsVpkPatchDirectoryName_AcceptsBothGenerations(string name)
		=> Assert.True(ModNodePayloadLayout.IsVpkPatchDirectoryName(name));

	[Fact]
	public void IsVpkPatchDirectoryName_RejectsOthers()
	{
		Assert.False(ModNodePayloadLayout.IsVpkPatchDirectoryName("Overwrite"));
		Assert.False(ModNodePayloadLayout.IsVpkPatchDirectoryName(null));
		Assert.False(ModNodePayloadLayout.IsVpkPatchDirectoryName(string.Empty));
	}

	[Fact]
	public void TryGetExistingVpkPatchDirectory_PrefersNewName()
	{
		var node = Path.Combine(_root, "both");
		Directory.CreateDirectory(Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName));
		Directory.CreateDirectory(Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName));

		var found = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(node);

		Assert.NotNull(found);
		Assert.Equal(ModNodePayloadLayout.VpkPatchDirectoryName, Path.GetFileName(found));
	}

	[Fact]
	public void TryGetExistingVpkPatchDirectory_FallsBackToLegacyName()
	{
		// 改名当天，库里的每个 VPK 类 Mod 都是这个形状。
		var node = Path.Combine(_root, "legacy-only");
		Directory.CreateDirectory(Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName));

		var found = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(node);

		Assert.NotNull(found);
		Assert.Equal(ModNodePayloadLayout.LegacyVpkPatchDirectoryName, Path.GetFileName(found));
	}

	[Fact]
	public void TryGetExistingVpkPatchDirectory_ReturnsNullWhenAbsent()
	{
		var node = Path.Combine(_root, "plain");
		Directory.CreateDirectory(node);

		Assert.Null(ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(node));
	}

	[Fact]
	public void EnumeratePayloadDirectories_IncludesLegacyLayer_AndNewNameWins()
	{
		var node = Path.Combine(_root, "layered");
		Directory.CreateDirectory(node);
		Directory.CreateDirectory(Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName));
		Directory.CreateDirectory(Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName));
		// 两层同名文件：后出现者赢，而"后"的顺序是 根 → 旧名 → 新名，所以新名必须最后。
		File.WriteAllText(Path.Combine(node, "9ba626afa44a3aa3.patch_0"), "root");
		File.WriteAllText(
			Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName, "9ba626afa44a3aa3.patch_0"),
			"legacy");
		File.WriteAllText(
			Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName, "9ba626afa44a3aa3.patch_0"),
			"current");

		var directories = ModNodePayloadLayout.EnumeratePayloadDirectories(node);
		var resolved = ModNodePayloadLayout.EnumeratePayloadFilesByName(node);

		Assert.Equal(3, directories.Count);
		Assert.Equal(node, directories[0]);
		Assert.Equal("current", File.ReadAllText(resolved["9ba626afa44a3aa3.patch_0"]));
	}

	[Fact]
	public void EnumeratePayloadDirectories_WithoutSubdirectories_IsJustTheNode()
	{
		var node = Path.Combine(_root, "legacy-mod");
		Directory.CreateDirectory(node);

		Assert.Equal([node], ModNodePayloadLayout.EnumeratePayloadDirectories(node));
	}

	[Fact]
	public void ModContentLayerNames_MatchTheLayoutConstants()
	{
		// 三处跨程序集字面量现在共享 Adaptation 里的常量；这里钉住 Core 与它没有漂。
		Assert.Equal(HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.Sectional, ModNodePayloadLayout.VpkPatchDirectoryName);
		Assert.Equal(HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.LegacySectional, ModNodePayloadLayout.LegacyVpkPatchDirectoryName);
		Assert.Equal(HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.Overwrite, ModNodePayloadLayout.OverwriteDirectoryName);
	}
}
