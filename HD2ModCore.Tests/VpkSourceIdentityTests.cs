using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// "这个 VPK 是不是库里某个 Mod 的来源"——重新导入同一个包时该**更新**还是**新建**。
/// <para>
/// 判错的方向有两种，代价差很远：判成"同源"但实际是别人的包 ⇒ 覆盖掉别人的产物；
/// 判成"新包"但其实是同一个 ⇒ 库里多一个一模一样的 Mod。
/// 所以判据刻意保守：文件名与字节数都要一样。
/// </para>
/// </summary>
public sealed class VpkSourceIdentityTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-vpk-source-" + Guid.NewGuid().ToString("N"));

	private string ModsRoot => Path.Combine(root, "mods");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	/// <summary>造一个"转换产物目录"：vpk2patch/ 存在，根上有来源 VPK 原件。</summary>
	private string CreateMod(string directoryName, string vpkFileName, string content)
	{
		var directory = Path.Combine(ModsRoot, directoryName);
		Directory.CreateDirectory(Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName));
		File.WriteAllText(Path.Combine(directory, vpkFileName), content);
		return directory;
	}

	private string CreateSource(string fileName, string content)
	{
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, fileName);
		File.WriteAllText(path, content);
		return path;
	}

	[Fact]
	public void TheSameFileAndSizeIsTheSameMod()
	{
		var directory = CreateMod("MourningVeil_Bill", "MourningVeil.vpk", "abc");
		var source = CreateSource("MourningVeil.vpk", "abc");

		var existing = VpkSourceIdentity.FindExistingMod(ModsRoot, source);

		Assert.NotNull(existing);
		Assert.Equal(Path.GetFullPath(directory), Path.GetFullPath(existing!.Directory));
		Assert.Equal("MourningVeil_Bill", existing.Name);
		Assert.Equal(1, existing.CandidateCount);
	}

	/// <summary>
	/// <b>同名但内容不同 ⇒ 不是同一个 Mod。</b>作者发了第二版、或两个人各自把包命名成
	/// <c>材质.vpk</c> 都很常见。按同源处理等于覆盖掉别人的产物。
	/// </summary>
	[Fact]
	public void TheSameNameWithDifferentBytesIsADifferentMod()
	{
		CreateMod("别人的包", "材质.vpk", "另一份内容更长");
		var source = CreateSource("材质.vpk", "短");

		Assert.Null(VpkSourceIdentity.FindExistingMod(ModsRoot, source));
	}

	/// <summary>没有 <c>vpk2patch/</c> 的目录不是这套流程的产物，不许被覆盖。</summary>
	[Fact]
	public void ADirectoryWithoutPayloadIsNotAMatch()
	{
		var directory = Path.Combine(ModsRoot, "手搓的Mod");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "X.vpk"), "abc");
		var source = CreateSource("X.vpk", "abc");

		Assert.Null(VpkSourceIdentity.FindExistingMod(ModsRoot, source));
	}

	/// <summary>
	/// 历史遗留的重复（同一个包导入过两次）⇒ 取排序后第一个，并把个数带出去让报告点名。
	/// 不在这里替用户删东西。
	/// </summary>
	[Fact]
	public void DuplicatesAreReportedButOnlyTheFirstIsUpdated()
	{
		CreateMod("X", "a.vpk", "abc");
		CreateMod("X (2)", "a.vpk", "abc");
		var source = CreateSource("a.vpk", "abc");

		var existing = VpkSourceIdentity.FindExistingMod(ModsRoot, source);

		Assert.NotNull(existing);
		Assert.Equal("X", existing!.Name);
		Assert.Equal(2, existing.CandidateCount);
	}

	/// <summary>库目录或来源文件不在 ⇒ 认不出来，按新 Mod 处理（多一个总比覆盖别人的安全）。</summary>
	[Fact]
	public void MissingInputsAreNotAnError()
	{
		Assert.Null(VpkSourceIdentity.FindExistingMod(ModsRoot, Path.Combine(root, "不存在.vpk")));
		Assert.Null(VpkSourceIdentity.FindExistingMod(string.Empty, "x.vpk"));
		Assert.Null(VpkSourceIdentity.FindExistingMod(null!, "x.vpk"));

		Directory.CreateDirectory(ModsRoot);
		var source = CreateSource("b.vpk", "abc");
		Assert.Null(VpkSourceIdentity.FindExistingMod(ModsRoot, source));
	}
}
