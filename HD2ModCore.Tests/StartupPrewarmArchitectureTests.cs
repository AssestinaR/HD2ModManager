namespace HD2ModCore.Tests;

// 作用：把"启动预热"的正确性前提固定下来（设计文档 §24）。
// Purpose: Pins the correctness preconditions of the startup prewarm (design doc §24).
//
// 预热本身是纯收益，但它有一个**很容易踩、踩了还看不出来**的坑：
// 预热会在库快照加载完、启动部署落定之前就把页面建出来并触发取数。
// 那时算出来的显示事实是错的（图标查不到、覆盖算在旧部署上），
// 而它一旦算完就会被标成"已加载"——用户之后打开页面看到的就是这份错数据，且不会自愈。
// 所以"取数前先等闸门"不是优化，是正确性要求；这几条用例把它钉住。
public sealed class StartupPrewarmArchitectureTests
{
	[Fact]
	public void ArmorPageWaitsForItsDisplayFactsGateBeforeComputingFacts()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));

		// 显示事实（图标、"已被覆盖"标记）必须在闸门放行之后才算：闸门没放行时算出来的
		// 都是错的，而且一旦算完就被标成"已加载"，用户之后看到的就是这份错数据。
		var facts = ExtractMember(page, "private async Task<IReadOnlyDictionary<string, ArmorDisplayFacts>> ReadArmorDisplayFactsAsync(");
		Assert.True(facts.Length > 0, "无法定位护甲页的显示事实读取方法（ReadArmorDisplayFactsAsync）。");
		Assert.Contains("await displayFactsReady", facts);

		// 列表本身**不能**等闸门：等它就又变成"打开页面先空一会儿"——而那正是用户报的
		// "护甲库总是不显示"。目录与快照只依赖数据库，与库快照/部署状态无关。
		var core = ExtractMember(page, "private async Task<(RuntimeArmorRegistrySnapshot Snapshot");
		Assert.True(core.Length > 0, "无法定位护甲页的目录读取方法（LoadCatalogCoreAsync）。");
		Assert.DoesNotContain("displayFactsReady", core);
		Assert.Contains("service.ReadArmorCatalogAsync", core);
	}

	// 预热与首开必须共用同一次加载。少了这条，会出现两种坏结果：
	// 各读一遍（预热白做一半），或者预热还在跑时用户点开页面、RefreshAsync 因 IsBusy 直接返回，
	// 页面停在空状态等预热跑完。
	[Fact]
	public void ArmorPagePrewarmAndFirstOpenShareOneLoad()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));

		Assert.Contains("public Task WarmUpAsync()", page);
		Assert.Contains("EnsureCatalogLoadedAsync(", page);
		// 单飞账本：同一个 Task 只发一次。
		Assert.Contains("catalogLoadTask ??=", page);
		// 预热失败只记日志，不给"从没打开过这个页面"的用户弹错误。
		Assert.Contains("预热运行时护甲注册失败", page);
	}

	[Fact]
	public void StartupPrewarmWarmsTheArmorPageViewModelAndItsFacts()
	{
		var root = FindRepositoryRoot();
		var shell = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ShellViewModel.cs"));

		// 1) 页面 VM 在空闲时建好——它的构造会把三个 CollectionView 挂到 UI 线程的 Dispatcher 上，
		//    不能等预热数据时再顺手 new。
		Assert.Contains("_ = CreateArmorRegistryPage();", shell);
		// 2) 数据预热在启动维护之后触发，闸门在同一处放行。
		Assert.Contains("QueueArmorRegistryPrewarm();", shell);
		Assert.Contains("_armorDisplayFactsGate.TrySetResult();", shell);
		Assert.Contains(".WarmUpAsync()", shell);
		// 3) 闸门放行必须写在 finally 里：启动维护失败或被取消时卡住它，
		//    等于让用户打开护甲页后永远停在"尚未读取"。
		var finallyBlock = shell.IndexOf("_armorDisplayFactsGate.TrySetResult();", StringComparison.Ordinal);
		var precedingFinally = shell.LastIndexOf("finally", finallyBlock, StringComparison.Ordinal);
		Assert.True(precedingFinally > 0, "闸门放行必须写在 finally 里。");
	}

	[Fact]
	public void VisualPrewarmChainEndsWithTheArmorPage()
	{
		var root = FindRepositoryRoot();
		var controller = File.ReadAllText(Path.Combine(
			root, "HD2ModManager", "Views", "WorkspacePagePresentationController.cs"));

		// 三张页面各占一个空闲回合：profile → library → armor。
		Assert.Contains("WorkspaceVisualPrewarm.Profile", controller);
		Assert.Contains("WorkspaceVisualPrewarm.Library", controller);
		Assert.Contains("WorkspaceVisualPrewarm.Armor", controller);
		Assert.Contains("ContinueWorkspaceVisualPrewarmArmor", controller);

		// 宿主也得有：没有宿主就没有 measure/arrange，等于白建。
		var hosts = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Views", "WorkspacePagePresentationController.cs"));
		Assert.Contains("ContentControl PrewarmArmor", hosts);
		var window = File.ReadAllText(Path.Combine(root, "HD2ModManager", "MainWindow.xaml"));
		Assert.Contains("PrewarmArmorPageHost", window);
		var windowCode = File.ReadAllText(Path.Combine(root, "HD2ModManager", "MainWindow.xaml.cs"));
		Assert.Contains("PrewarmArmorPageHost", windowCode);
	}

	private static string ExtractMember(string source, string signatureStart)
	{
		var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
		if (start < 0) return string.Empty;
		var end = source.IndexOf("\n\tprivate ", start + signatureStart.Length, StringComparison.Ordinal);
		return end > start ? source[start..end] : source[start..];
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
