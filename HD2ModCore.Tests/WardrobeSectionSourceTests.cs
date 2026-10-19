using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 编辑器用的 section 数据来源：从**当前部署的 patch** 现算。
///
/// 一条纯逻辑用例（附属载荷过滤）+ 一条真机用例（读环境变量里指向的 GameData，
/// 没有就跳过）。后者是这条链唯一无法离线证明的部分，所以宁可留一条会跳过的真机用例。
/// </summary>
public sealed class WardrobeSectionSourceTests
{
	/// <summary>真机样本目录：设置这个环境变量才会跑真机用例。</summary>
	private const string GameDataVariable = "HD2MOD_GAME_DATA";

	[Fact]
	public void EnumerateDeployedPatches_SkipsSidecarPayloads()
	{
		var root = Directory.CreateTempSubdirectory("hd2mod-patches-").FullName;
		try
		{
			File.WriteAllBytes(Path.Combine(root, "abc.patch_0"), [1, 2, 3]);
			File.WriteAllBytes(Path.Combine(root, "abc.patch_0.gpu_resources"), [4]);
			File.WriteAllBytes(Path.Combine(root, "abc.patch_1"), [5]);
			File.WriteAllBytes(Path.Combine(root, "abc.patch_1.stream"), [6]);
			File.WriteAllBytes(Path.Combine(root, "abc.patch_10"), [7]);
			File.WriteAllBytes(Path.Combine(root, "unrelated.txt"), [8]);

			var patches = WardrobeSectionSource.EnumerateDeployedPatches(root);
			// 附属载荷不是独立 patch：漏掉这层过滤会把 .gpu_resources 当成 TOC 去解析。
			Assert.Equal(3, patches.Count);
			// patch_10 排在 patch_1 之后，按数字后缀而不是字符串。
			Assert.EndsWith("abc.patch_0", patches[0], StringComparison.OrdinalIgnoreCase);
			Assert.EndsWith("abc.patch_1", patches[1], StringComparison.OrdinalIgnoreCase);
			Assert.EndsWith("abc.patch_10", patches[2], StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void EnumerateDeployedPatches_MissingDirectoryIsEmpty()
	{
		var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		Assert.Empty(WardrobeSectionSource.EnumerateDeployedPatches(missing));
	}

	[Fact]
	public async Task ResolveAsync_IgnoresInvalidUnitIds()
	{
		var root = Directory.CreateTempSubdirectory("hd2mod-patches-").FullName;
		try
		{
			var resolution = await WardrobeSectionSource.ResolveAsync(root, ["not-a-unit", string.Empty]);
			Assert.Empty(resolution.Units);
			Assert.Empty(resolution.MissingUnitIds);
			Assert.Empty(resolution.ScannedPatches);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	/// <summary>
	/// 真机用例：从部署的 patch 里读出目标 unit 的 section 表。
	/// 没有设置 <c>HD2MOD_GAME_DATA</c> 时跳过——CI 上没有游戏，不该红。
	/// </summary>
	[Fact]
	public async Task ResolveAsync_ReadsTheHelmetUnitFromTheDeployedPatch()
	{
		var gameData = Environment.GetEnvironmentVariable(GameDataVariable);
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return;

		const string helmet = "af431352d9b95e83";
		var resolution = await WardrobeSectionSource.ResolveAsync(gameData, [helmet]);
		Assert.Empty(resolution.MissingUnitIds);
		var unit = Assert.Single(resolution.Units);
		Assert.Equal(helmet, unit.FileId);
		Assert.Equal("Helmet", unit.Slot);
		// 部署包上实测是 22 个 section、5 级 LOD 且节数一致。
		Assert.Equal(22, unit.SectionCount);
		Assert.True(unit.LodConsistent);
		Assert.Equal("a47787804c45e1a9-8852", unit.Sections[0].Fingerprint);
	}

	/// <summary>
	/// 没有来源 mod 时也要能识别：unit 只存在于游戏自己的 archive 里。
	/// 没有这一级，一件原版护甲打开编辑器就是空树。
	/// </summary>
	[Fact]
	public async Task ResolveAsync_FallsBackToTheVanillaArchives()
	{
		var gameData = Environment.GetEnvironmentVariable(GameDataVariable);
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return;

		// 一个只存在于原版数据包、不在任何部署 patch 里的 unit。
		const string vanillaUnit = "143a0d93d14d6587";
		var resolution = await WardrobeSectionSource.ResolveAsync(gameData, [vanillaUnit]);
		Assert.Empty(resolution.MissingUnitIds);
		var unit = Assert.Single(resolution.Units);
		Assert.Equal(vanillaUnit, unit.FileId);
		Assert.True(unit.SectionCount > 0);
		Assert.NotEmpty(unit.Sections[0].Fingerprint);
	}
}
