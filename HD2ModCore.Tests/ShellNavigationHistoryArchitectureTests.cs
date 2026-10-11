namespace HD2ModCore.Tests;

// 作用：把"按 Esc 逐层返回上一次的页面组合"这条不变式固定下来。
// Purpose: Pins the invariant behind "Esc walks back through previous page combinations".
//
// 背景：用户要求在**长嵌套**的 Mod 逻辑选项里上下级切换，但不要多窗口、不要面包屑。
// 做法是记录每次导航**之前**的页面组合，按 Esc 出栈一层。
// 这个功能的爆炸半径很小（不落盘、不碰 Core、不碰页面内部逻辑），
// 但有两个容易悄悄坏掉的地方，正是下面这些用例要钉的：
//   1) 记录点必须覆盖**所有**导航路径，否则某些路走进去就再也退不回来；
//   2) 回放本身绝不能再入栈，否则 Esc 会在两页之间来回弹。
public sealed class ShellNavigationHistoryArchitectureTests
{
	// Esc 的前两级是"取消编辑 / 清除选中"，手感不能动；第三级才是返回队列。
	// 队列空了必须退回原来的行为（回首页），否则首页按 Esc 会变成死键。
	[Fact]
	public void EscapeFallsBackToTheReturnQueueBeforeGoingHome()
	{
		var window = ReadManagerFile("MainWindow.xaml.cs");
		var handler = ExtractMember(window, "private void MainWindow_PreviewKeyDown(");

		Assert.True(handler.Length > 0, "无法定位 MainWindow_PreviewKeyDown。");
		Assert.Contains("shell.BottomBar.HasTemporaryEditor", handler);
		Assert.Contains("shell.CancelBottomBarEdit()", handler);
		Assert.Contains("shell.HasSelection", handler);
		Assert.Contains("shell.ClearTransientSelection()", handler);
		Assert.Contains("shell.TryGoBack()", handler);

		var back = handler.IndexOf("shell.TryGoBack()", StringComparison.Ordinal);
		var home = handler.IndexOf("shell.Navigate(WorkspaceMode.Home)", StringComparison.Ordinal);
		Assert.True(home > back, "回首页必须发生在返回队列为空之后（TryGoBack 返回 false 的分支里）。");
	}

	// 记录点只有两个：CommitNavigation（Navigate 的唯一出口）与 UpdateModeFromSlots。
	// 后者是其余全部导航路径的共同收尾——下面逐个确认，少一个就会出现"某条路退不回来"。
	[Fact]
	public void EveryNavigationPathRecordsThePreviousCombination()
	{
		var shell = ReadManagerFile(Path.Combine("ViewModels", "ShellViewModel.cs"));

		Assert.Contains("RecordWorkspaceEntry();", ExtractMember(shell, "private void CommitNavigation("));
		Assert.Contains("RecordWorkspaceEntry();", ExtractMember(shell, "private void UpdateModeFromSlots("));

		foreach (var caller in new[]
		{
			"public void OpenSinglePage(",
			"public void OpenPage(",
			"public void OpenGameDataBrowser()",
			"public void OpenCrossArmorPlan(",
			"private void OpenSecondaryPage(",
			"private void CloseDeletedModDetails()",
		})
		{
			var body = ExtractMember(shell, caller);
			Assert.True(body.Length > 0, $"无法定位导航入口 {caller}。");
			Assert.Contains("UpdateModeFromSlots();", body);
		}
	}

	// 队列必须有上限，且丢的是**最旧**的一条（不是最新的，也不是清空）。
	// 另外连续相同的组合不入栈：否则在同一个页面上按 Esc 会先"空跳"一格，用户会以为坏了。
	[Fact]
	public void TheReturnQueueIsCappedAtTwentyAndDropsTheOldest()
	{
		var shell = ReadManagerFile(Path.Combine("ViewModels", "ShellViewModel.cs"));

		Assert.Contains("private const int NavigationHistoryLimit = 20;", shell);
		var record = ExtractMember(shell, "private void RecordWorkspaceEntry(");

		Assert.True(record.Length > 0, "无法定位 RecordWorkspaceEntry。");
		Assert.Contains("_navigationHistory.Count >= NavigationHistoryLimit", record);
		Assert.Contains("_navigationHistory.RemoveAt(0)", record);
		Assert.Contains("_navigationHistory.Add(previous.Value)", record);
		Assert.Contains("previous.Value == current", record);
		// 首次导航（构造函数里的 Navigate(Home)）只能播种，不能把 null 压进队列。
		Assert.Contains("previous is null", record);
	}

	// 回放期间必须屏蔽入栈，否则"回放失败 → 回首页"会把自己压回队列，Esc 变成来回弹。
	// 同时确认：整段回放都被这个标志罩住（try/finally），异常路径也不会漏出去。
	[Fact]
	public void ReplayingTheQueueNeverPushesBackOntoIt()
	{
		var shell = ReadManagerFile(Path.Combine("ViewModels", "ShellViewModel.cs"));
		var back = ExtractMember(shell, "public bool TryGoBack(");

		Assert.True(back.Length > 0, "无法定位 TryGoBack。");
		Assert.Contains("_isRestoringWorkspace = true;", back);
		Assert.Contains("_isRestoringWorkspace = false;", back);
		Assert.Contains("finally", back);
		// 只出栈，不再入栈——"不可反撤销"就落在这句上。
		Assert.Contains("_navigationHistory.RemoveAt(_navigationHistory.Count - 1)", back);
		Assert.DoesNotContain("_navigationHistory.Add(", back);

		Assert.Contains("_isRestoringWorkspace ||", ExtractMember(shell, "private void RecordWorkspaceEntry("));
	}

	// 有些页面**不能**从"页面类型 + Mod id"重建：
	//   - CrossArmorPlan / MaterialPackaging：CreatePage 直接抛异常，必须由调用方传活实例；
	//   - GameDataArchiveDetails：必须与左侧浏览器配对，CreatePage 造出来的是无源宿主页。
	// 对这些组合的正确处理是"拒绝重建并回首页"，而不是硬造一个坏页面。
	[Fact]
	public void PagesThatCannotBeRebuiltAreRefusedInsteadOfSilentlyReopened()
	{
		var shell = ReadManagerFile(Path.Combine("ViewModels", "ShellViewModel.cs"));

		var canReopen = ExtractMember(shell, "private static bool CanReopenPage(");
		Assert.True(canReopen.Length > 0, "无法定位 CanReopenPage。");
		Assert.Contains("WorkspacePageType.CrossArmorPlan => false", canReopen);
		Assert.Contains("WorkspacePageType.MaterialPackaging => false", canReopen);
		Assert.Contains("WorkspacePageType.GameDataArchiveDetails => false", canReopen);

		// 存档浏览器这一对必须整体走 OpenGameDataBrowser，否则右槽是空的。
		Assert.Contains("OpenGameDataBrowser()", ExtractMember(shell, "private bool TryRestoreWorkspace("));

		// 重建失败的兜底是回首页。
		Assert.Contains(
			"if (!TryRestoreWorkspace(entry)) Navigate(WorkspaceMode.Home);",
			ExtractMember(shell, "public bool TryGoBack("));
	}

	private static string ReadManagerFile(string relativePath)
		=> File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", relativePath));

	// 取一个成员从签名开始、到下一个同级成员（或类结尾）为止的源码。
	// 两个目标文件的缩进**混用**空格与制表符（ShellViewModel.cs 里两者都有），
	// 所以右边界要同时认 `\n        private ` 与 `\n\t\tprivate `，否则会把后面的成员吞进来。
	private static string ExtractMember(string source, string signatureStart)
	{
		var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
		if (start < 0) return string.Empty;
		var searchFrom = start + signatureStart.Length;
		var best = source.Length;
		foreach (var boundary in new[]
		{
			"\n        private ", "\n        public ", "\n        internal ",
			"\n\t\tprivate ", "\n\t\tpublic ", "\n\t\tinternal ",
			"\n    }\n",
		})
		{
			var index = source.IndexOf(boundary, searchFrom, StringComparison.Ordinal);
			if (index >= 0 && index < best) best = index;
		}
		return best <= start ? source[start..] : source[start..best];
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
