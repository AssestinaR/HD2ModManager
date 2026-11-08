using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 面向 agent 的可用性：这几条钉的是"别让人得出错误结论"的静默行为。
/// <para>
/// 背景（另一个会话的实机反馈）：三条静默行为各自烧掉了半小时到两轮全量转换 ——
/// 缺索引却报成"找不到 Mod"、token 被二次哈希、部分失败仍报成功。
/// </para>
/// </summary>
public sealed class CliAgentFriendlinessTests
{
	// ---- ① --id-namespace 的形状守卫 --------------------------------------------

	[Fact]
	public async Task IdNamespace_RejectsAValueThatIsAlreadyAToken()
	{
		// 传进来的是**已经派生好的 token**：再算一遍 Hd2AssetId.NamespaceFor 会把所有材质/贴图 id 换成另一套。
		// 表面完全正常（退出码 0），只是产物与上一轮零重叠。
		var run = await CliTestConsole.RunCapturedAsync(
			"vpk2patch", "not-a-real.vpk", "--output", Path.GetTempPath(),
			"--id-namespace", "vpk-e688da0055f0a55f", "--json");

		Assert.Equal(2, run.ExitCode);
		using var json = run.ParseJson();
		Assert.Equal("IdNamespaceLooksLikeToken", json.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.Contains("--id-namespace-token", json.RootElement.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task IdNamespace_AcceptsAPlainSourceName()
	{
		// 普通"来源标识"必须照旧可用：报的不能是形状错误（这里会走到"文件不存在"）。
		var run = await CliTestConsole.RunCapturedAsync(
			"vpk2patch", "not-a-real.vpk", "--output", Path.GetTempPath(),
			"--id-namespace", "某来源包名", "--json");

		using var json = run.ParseJson();
		Assert.NotEqual("IdNamespaceLooksLikeToken", json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	// ---- ② 未知命令给最接近的那个 -------------------------------------------------

	[Fact]
	public async Task UnknownCommand_SuggestsTheClosestCommand()
	{
		var run = await CliTestConsole.RunCapturedAsync("mods", "list", "--json");

		Assert.Equal(2, run.ExitCode);
		using var json = run.ParseJson();
		Assert.Equal("UnknownCommand", json.RootElement.GetProperty("error").GetProperty("code").GetString());
		// "mods" 距离 "mod" 只有 1：必须点名，而不是只说"不支持"。
		Assert.Contains("最接近的是 'mod'", json.RootElement.GetProperty("error").GetProperty("message").GetString());
	}

	// ---- ③ 库快照修复：默认预演，--confirm 才写 ----------------------------------

	[Fact]
	public async Task RepairLibrarySnapshot_DefaultsToDryRunAndWritesNothing()
	{
		var (root, library, manager) = CreateEmptyLibrary();
		try
		{
			var libraryJson = Path.Combine(library, "library.json");
			var run = await CliTestConsole.RunCapturedAsync(
				"repair", "library-snapshot",
				"--library", library, "--manager-root", manager, "--json");
			Assert.True(run.ExitCode == 0, run.StandardError + run.StandardOutput);

			using var json = run.ParseJson();
			var repair = json.RootElement.GetProperty("repair");
			Assert.False(repair.GetProperty("written").GetBoolean());
			// **没有写盘**：这才是这条用例的重点。以前它默认就写。
			Assert.False(File.Exists(libraryJson));
			Assert.Contains("预演", run.StandardOutput);

			var confirmed = await CliTestConsole.RunCapturedAsync(
				"repair", "library-snapshot",
				"--library", library, "--manager-root", manager, "--confirm", "--json");
			Assert.True(confirmed.ExitCode == 0, confirmed.StandardError);
			using var confirmedJson = confirmed.ParseJson();
			Assert.True(confirmedJson.RootElement.GetProperty("repair").GetProperty("written").GetBoolean());
			Assert.True(File.Exists(libraryJson));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// ---- ④ 选择器统一：名字 / GUID / 前缀 ----------------------------------------

	[Fact]
	public void ResolveMod_AcceptsTheNodeName()
	{
		var id = ModNodeId.New();
		var nodes = new Dictionary<ModNodeId, ModNode> { [id] = Node(id, "MourningVeil_Bill", "mv") };

		Assert.Equal(id, CliProjections.ResolveMod(nodes, "MourningVeil_Bill").Id);
		// GUID 与前缀照旧。
		Assert.Equal(id, CliProjections.ResolveMod(nodes, id.Value.ToString("D")).Id);
		Assert.Equal(id, CliProjections.ResolveMod(nodes, id.Value.ToString("N")[..8]).Id);
	}

	[Fact]
	public void ResolveMod_ReportsAmbiguousNamesAndMissingSelectors()
	{
		var first = ModNodeId.New();
		var second = ModNodeId.New();
		var nodes = new Dictionary<ModNodeId, ModNode>
		{
			[first] = Node(first, "同名", "a"),
			[second] = Node(second, "同名", "b"),
		};
		Assert.Equal("ModSelectorAmbiguous",
			Assert.Throws<CliUserException>(() => CliProjections.ResolveMod(nodes, "同名")).Code);
		// 用不存在的名字：必须说清"既不是名字也不是 GUID"。
		var missing = Assert.Throws<CliUserException>(() => CliProjections.ResolveMod(nodes, "no-such-mod"));
		Assert.Equal("ModSelectorInvalid", missing.Code);
		Assert.Contains("名字", missing.Message);
	}

	// ---- ⑤ 管理器根目录被指到 bin\ 时，直说上一级才是根 --------------------------
	//
	// 发布包：<安装根>\HD2ModManager.exe + <安装根>\bin\hd2mod.exe。每次发版都有人
	// （或 agent）把 --manager-root 指到 bin\，因为"hd2mod.exe 就在这儿"。
	// 1.5.5 里这条误会直接让**所有 VPK 导入**在规划阶段失败，而报错只说"让 settings.json 指对"。

	[Fact]
	public async Task Status_PointingManagerRootAtBin_NamesTheInstallRoot()
	{
		var root = CreateInstallRoot();
		try
		{
			var bin = Directory.CreateDirectory(Path.Combine(root, Hd2ModCliLocator.ReleaseSubdirectory)).FullName;

			var run = await CliTestConsole.RunCapturedAsync("status", "--manager-root", bin, "--json");
			Assert.True(run.ExitCode == 0, run.StandardError + run.StandardOutput);

			using var json = run.ParseJson();
			var issues = string.Join("|", json.RootElement.GetProperty("issues")
				.EnumerateArray().Select(issue => issue.GetString()));
			// 必须点名"上一级才是管理器根"，而不是只留一句"未找到管理器设置"。
			Assert.Contains("管理器根目录是它的上一级", issues);
			Assert.Contains(root, issues);
		}
		finally { DeleteRoot(root); }
	}

	[Fact]
	public async Task Vpk2Patch_SaysWhichSettingsFileItLookedFor()
	{
		var root = CreateInstallRoot();
		try
		{
			// 设置文件在根目录（发布布局），但里面没有 GameDataFolder。
			File.WriteAllText(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), "{}");
			var vpk = Path.Combine(root, "dummy.vpk");
			File.WriteAllBytes(vpk, [0x00, 0x01, 0x02]);

			var run = await CliTestConsole.RunCapturedAsync(
				"vpk2patch", vpk, "--output", Path.Combine(root, "out"),
				"--manager-root", root, "--dry-run", "--json");

			Assert.Equal(2, run.ExitCode);
			using var json = run.ParseJson();
			Assert.Equal("PrerequisitesMissing", json.RootElement.GetProperty("error").GetProperty("code").GetString());
			// 报错必须写出**实际去读的那一份** settings.json —— 否则路径被推导错时完全看不见。
			var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
			Assert.Contains(Path.Combine(root, Hd2ModCliLocator.SettingsFileName), message);
		}
		finally { DeleteRoot(root); }
	}

	private static string CreateInstallRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-cli-layout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, Hd2ModCliLocator.DataDirectoryName, "resources"));
		return root;
	}

	private static void DeleteRoot(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch (IOException) { }
	}

	private static ModNode Node(ModNodeId id, string name, string relativePath)
		=> new(id, relativePath, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null),
			Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());

	private static (string Root, string Library, string Manager) CreateEmptyLibrary()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-cli-agent-" + Guid.NewGuid().ToString("N"));
		var library = Path.Combine(root, "library");
		var manager = Path.Combine(root, "manager");
		Directory.CreateDirectory(library);
		Directory.CreateDirectory(manager);
		return (root, library, manager);
	}
}
