using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Decoration;
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
/// <para>
/// <b>"有效"= 名字像 patch 且文件头是 patch TOC 魔数。</b>只看名字是不够的：
/// 一个半截文件、一次中断留下的残留，甚至用户手工塞进去的同名文件，都会让整个 Mod
/// 变成"只部署它"，而基线层里那些材质与模型全部不部署 —— 表现是"模型还在、材质没了"。
/// 这条判据必须和 <see cref="ModNodePayloadLayout.EnumerateEffectiveFilesByName"/> 对齐，
/// 否则部署看到的和详情页/资产事实看到的不是同一批文件。
/// </para>
/// </summary>
public sealed class ModDeployableFilesTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";

	/// <summary>HD2 补丁 TOC 的魔数（<c>0xF0000011</c>）。</summary>
	private const uint PatchMagic = 0xF0000011;

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

	/// <summary>非 patch 的文件（结构文件等），原样写文本。</summary>
	private static void WriteText(string directory, string name, string content = "{}")
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name), content);
	}

	/// <summary>一份"能读的 patch"：4 字节魔数 + 一段标记文本（断言时按标记比对）。</summary>
	private static void WritePatch(string directory, string name, string marker = "payload")
	{
		Directory.CreateDirectory(directory);
		var body = Encoding.UTF8.GetBytes(marker);
		var bytes = new byte[sizeof(uint) + body.Length];
		BitConverter.GetBytes(PatchMagic).CopyTo(bytes, 0);
		body.CopyTo(bytes, sizeof(uint));
		File.WriteAllBytes(Path.Combine(directory, name), bytes);
	}

	/// <summary>一份"名字像 patch 但读不出来"的文件——半截、残留、手工塞进去的都会长这样。</summary>
	private static void WriteCorruptPatch(string directory, string name, string content = "not-a-toc")
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name), content);
	}

	/// <summary>跳过魔数读回标记文本。</summary>
	private static string Marker(string path)
		=> Encoding.UTF8.GetString(File.ReadAllBytes(path), sizeof(uint), (int)new FileInfo(path).Length - sizeof(uint));

	/// <summary>把 <c>Overwrite/</c> 标成"管理器物化过的"——只有这种才允许独占整层。</summary>
	private static void MarkManaged(string overwriteDirectory)
	{
		Directory.CreateDirectory(overwriteDirectory);
		File.WriteAllText(Path.Combine(overwriteDirectory, ManagedOverwriteMarkers.VpkRekey), string.Empty);
	}

	private static string Patch(int index) => $"{Archive}.patch_{index}";

	[Fact]
	public void ManagedOverwriteWithAValidPatch_Wins_ForASectionalMod()
	{
		var mod = NewMod();
		WritePatch(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(0), "baseline");
		WritePatch(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(5), "unselected");
		WritePatch(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(0), "selected");
		MarkManaged(ModNodePayloadLayout.GetOverwriteDirectory(mod));

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("selected", Marker(result.FilesByName[Patch(0)]));
		Assert.False(result.FilesByName.ContainsKey(Patch(5)));
	}

	// legacy 模组同样可能有 Overwrite。给它加"仅 sectional 生效"的检查会把生效层整个忽略掉。
	[Fact]
	public void ManagedOverwriteWithAValidPatch_Wins_ForALegacyModToo()
	{
		var mod = NewMod();
		WritePatch(mod, Patch(0), "root-patch");
		WritePatch(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(0), "overwrite-patch");
		MarkManaged(ModNodePayloadLayout.GetOverwriteDirectory(mod));

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("overwrite-patch", Marker(result.FilesByName[Patch(0)]));
	}

	/// <summary>
	/// <b>真实样本（2026-11-07，用户 reimu）</b>：基底有 unit + material 两层，
	/// <c>Overwrite/</c> 是 P3-1 之前那次换甲写的、**只含 unit 层且没有归属标记**。
	/// <para>
	/// 判定必须按名顶替，而不是"只部署 Overwrite"：材质层（16.7 MB 贴图）要留在部署集合里。
	/// 升级到 1.5.0 之后这里只部署 Overwrite，模型在、材质没了；重构建不碰 Overwrite，
	/// 所以怎么重转都好不了，只有删掉重加才行 —— 社区反馈的正是这个。
	/// </para>
	/// </summary>
	[Fact]
	public void UnmanagedOverwriteWithOnlyTheUnitLayer_KeepsTheBaseMaterialPatch()
	{
		var mod = NewMod();
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		WritePatch(payload, Patch(0), "unit-before-rekey");
		WritePatch(payload, Patch(1), "material-with-textures");

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(mod);
		WritePatch(overwrite, Patch(0), "unit-after-rekey");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		// unit 换成换甲后的那份…
		Assert.Equal("unit-after-rekey", Marker(result.FilesByName[Patch(0)]));
		// …材质层原样保留。
		Assert.Equal("material-with-textures", Marker(result.FilesByName[Patch(1)]));
	}

	[Fact]
	public void OverwriteWithoutAnyValidPatch_DoesNotCount()
	{
		// 只有结构文件、没有真 patch 的 Overwrite 不算"有生效层"。
		var mod = NewMod();
		WritePatch(mod, Patch(0), "root-patch");
		WriteText(ModNodePayloadLayout.GetOverwriteDirectory(mod), "sections.json");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Equal("root-patch", Marker(result.FilesByName[Patch(0)]));
	}

	/// <summary>
	/// <b>名字像 patch、内容不是 patch 的文件不许接管整个 Mod。</b>
	/// <para>
	/// 这是 1.5.0 之后必须钉住的一条：判定当时只解析文件名，于是 Overwrite 里任何一份
	/// 残留（中断留下的半截文件、用户手工塞的同名文件）都会让这个 Mod 变成"只部署它"，
	/// 基线层里的模型与材质一个都不部署 —— 用户看到的是"模型在、材质没了"，
	/// 而重新转换不会动 Overwrite，所以怎么重构建都好不了，只有删掉重加才行。
	/// </para>
	/// </summary>
	[Fact]
	public void OverwriteWithACorruptPatchFile_DoesNotTakeOver()
	{
		var mod = NewMod();
		WritePatch(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(0), "baseline");
		WritePatch(ModNodePayloadLayout.GetVpkPatchDirectory(mod), Patch(1), "material");
		WriteCorruptPatch(ModNodePayloadLayout.GetOverwriteDirectory(mod), Patch(0));

		var result = ModDeployableFiles.Resolve(mod, _parser);

		// 退回"部署基线层"：两份都进游戏，而不是只部署那份读不出来的残留。
		Assert.Null(result.SkipReason);
		Assert.Equal("baseline", Marker(result.FilesByName[Patch(0)]));
		Assert.Equal("material", Marker(result.FilesByName[Patch(1)]));
	}

	// 判定**不写盘**：deploy plan 用的就是它，而计划是只读的。
	// 实测踩过：跑一次计划就在 40 多个 Mod 里凭空出现 Overwrite/，用户完全没预期。
	[Fact]
	public void SectionalWithoutOverwrite_DeploysThePayloadWithoutTouchingTheDisk()
	{
		var mod = NewMod();
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		WritePatch(payload, Patch(0), "unit");
		WritePatch(payload, Patch(1), "material");

		var result = ModDeployableFiles.Resolve(mod, _parser);

		Assert.Null(result.SkipReason);
		Assert.Equal("unit", Marker(result.FilesByName[Patch(0)]));
		Assert.Equal("material", Marker(result.FilesByName[Patch(1)]));
		Assert.False(Directory.Exists(ModNodePayloadLayout.GetOverwriteDirectory(mod)));
	}

	[Fact]
	public void LegacyWithoutOverwrite_DeploysItsRootPatches()
	{
		var mod = NewMod();
		WritePatch(mod, Patch(0), "root-patch");

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
