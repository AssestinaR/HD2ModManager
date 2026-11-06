using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「一个 Mod 里哪些文件会进游戏」。
/// <para>
/// 判定只看**有哪一层**，不逐个 patch 判断：
/// <list type="number">
/// <item>Overwrite 里有有效 patch ⇒ 只部署它，这条结束。</item>
/// <item>sectional / vpk2patch：有基底 ⇒ 硬链接成生效层并部署；没有 ⇒ 跳过并说明。</item>
/// <item>legacy / logical：根目录有 patch ⇒ 部署；没有 ⇒ 跳过并说明。</item>
/// </list>
/// </para>
/// <para>
/// 第 1 条**对所有类型都适用**——legacy 模组同样可能有 Overwrite。
/// 不要给它加"仅 sectional 生效"的检查：那会把 legacy 的生效层整个忽略掉。
/// </para>
/// </summary>
public sealed class ModDeployableFilesTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-deployable-files-tests", Guid.NewGuid().ToString("N"));
	private readonly IPatchFileNameParser _parser = new PatchFileNameParser();

	public ModDeployableFilesTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewMod(string name = "mod")
	{
		var root = Path.Combine(_root, name);
		Directory.CreateDirectory(root);
		return root;
	}

	private static void Write(string directory, string name, string content = "payload")
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name), content);
	}

	private static string Patch(int index) => $"{Archive}.patch_{index}";

	[Fact]
	public void OverwriteWithAValidPatch_Wins_ForASectionalMod()
	{
		var mod = NewMod();
		Write(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(0), "baseline");
		Write(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(5), "unselected");
		Write(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(0), "selected");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("selected", File.ReadAllText(result.FilesByName[Patch(0)]));
		Assert.False(result.FilesByName.ContainsKey(Patch(5)));
	}

	// legacy 模组同样可能有 Overwrite。给它加"仅 sectional 生效"的检查会把生效层整个忽略掉。
	[Fact]
	public void OverwriteWithAValidPatch_Wins_ForALegacyModToo()
	{
		var mod = NewMod();
		Write(mod, Patch(0), "root-patch");
		Write(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(0), "overwrite-patch");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("overwrite-patch", File.ReadAllText(result.FilesByName[Patch(0)]));
	}

	[Fact]
	public void OverwriteWithoutAnyValidPatch_DoesNotCount()
	{
		// 只有结构文件、没有真 patch 的 Overwrite 不算"有生效层"。
		var mod = NewMod();
		Write(mod, Patch(0), "root-patch");
		Write(ModNodePayloadLayout.GetOverwriteDirectory(mod), "sections.json", "{}");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Equal("root-patch", File.ReadAllText(result.FilesByName[Patch(0)]));
	}

	[Fact]
	public void SectionalWithoutOverwrite_MaterializesThePayload()
	{
		var mod = NewMod();
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		Write(payload, Patch(0), "unit");
		Write(payload, Patch(1), "material");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("unit", File.ReadAllText(result.FilesByName[Patch(0)]));
		Assert.Equal("material", File.ReadAllText(result.FilesByName[Patch(1)]));
		// 硬链接之后 Overwrite 就是生效层，下一次判定只需列举目录。
		Assert.True(File.Exists(Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(1))));
		Assert.Equal(
			result.FilesByName.Keys.OrderBy(name => name),
			ModDeployableFiles.Resolve(mod, _parser).FilesByName.Keys.OrderBy(name => name));
	}

	[Fact]
	public void LegacyWithoutOverwrite_DeploysItsRootPatches()
	{
		var mod = NewMod();
		Write(mod, Patch(0), "root-patch");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.True(result.FilesByName.ContainsKey(Patch(0)));
	}

	[Fact]
	public void SectionalWithoutPayload_IsSkippedWithAReason()
	{
		var mod = NewMod();

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Empty(result.FilesByName);
		Assert.NotNull(result.SkipReason);
		Assert.Contains("没有可部署", result.SkipReason, StringComparison.Ordinal);
	}

	[Fact]
	public void EmptyMod_IsSkippedWithAReason()
	{
		var result = ModDeployableFiles.Resolve(NewMod("empty"), _parser);

		Assert.Empty(result.FilesByName);
		Assert.NotNull(result.SkipReason);
	}
}
