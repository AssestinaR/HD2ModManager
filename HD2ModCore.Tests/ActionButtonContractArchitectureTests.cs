namespace HD2ModCore.Tests;

// 作用：把"按钮契约重构"这批改动的**护栏**先钉下来（规划见
// docs/agent/action-button-contract-plan-20261110.md §2.5）。
//
// 为什么这些用例长成这样：
//   主工程是 net10.0-windows + UseWPF，HD2ModCore.Tests 是 net8.0 —— UI 层**无法在此编译**，
//   所以护栏只能落在两处：①对**产品代码文本**做结构化断言；②对**纯函数**做语义断言（后续阶段加）。
//
// 关于文本断言的失败模式（评审指出）：现有 ExtractMember 用**硬编码制表符/空格**当成员边界，
// 一旦缩进被统一，抽取结果会**静默变空**，断言随之真空通过。本文件因此刻意：
//   1) 先断言语料本身非空且含已知锚点——抽取坏了就**红**，不会静默通过；
//   2) 不依赖缩进做语义判断，只把缩进当作"成员从哪儿开始"的粗边界。
public sealed class ActionButtonContractArchitectureTests
{
	// 基线导出的字段集合就是"渲染层会读什么"的契约。
	// 少一个字段 = 某个维度的行为变化抓不到；多一个字段而无来源 = 基线不稳定。
	[Fact]
	public void BaselineCapturesEveryDimensionTheRendererReads()
	{
		var baseline = ReadManagerFile(Path.Combine("Services", "ActionBarBaseline.cs"));

		foreach (var dimension in new[] { "visible=", "label=", "order=", "canExecute=" })
			Assert.Contains(dimension, baseline);

		// 渲染层按 Order 升序铺（FloatingActionCluster._view.SortDescriptions）。
		// 基线若不同样排序，"顺序变了"就漏检。
		Assert.Contains("OrderBy(action => action.Order)", baseline);
	}

	// 可见性判据在 P2 合并进声明表之前，是**两处**：产品代码的 SyncActionVisibility 与基线的固件。
	// 两处一旦漂移，基线就不再反映真实行为 —— diff 为空会变成"基线自己骗自己"。
	// 这里不自动比对两者（P2 会消灭重复），只钉住"两处都还在、且都覆盖同一个 Kind 全集"。
	[Fact]
	public void VisibilityTableAndBaselineFirmwareCoverTheSameKinds()
	{
		var actions = ReadManagerFile(Path.Combine("ViewModels", "ModDetailsPageViewModel.Actions.cs"));
		var sync = ExtractMember(actions, "private void SyncActionVisibility(");
		Assert.True(sync.Length > 0, "无法定位 SyncActionVisibility。");

		var baseline = ReadManagerFile(Path.Combine("Services", "ActionBarBaseline.cs"));
		var firmware = ExtractMember(baseline, "private static Func<PageActionKind, bool> ActionVisibleInModDetails(");
		Assert.True(firmware.Length > 0, "无法定位 ActionVisibleInModDetails。");

		// 逐个 Kind：两处都必须提到它，否则基线与产品代码在"这个动作归哪条判据"上已经分家。
		foreach (var kind in new[]
		{
			"PageActionKind.Table", "PageActionKind.RebuildVpk", "PageActionKind.Fix",
			"PageActionKind.CrossArmor", "PageActionKind.Compact", "PageActionKind.Package",
			"PageActionKind.Helmet", "PageActionKind.Merge", "PageActionKind.CustomModel",
			"PageActionKind.PatchRebuild", "PageActionKind.CloneVariant",
		})
		{
			Assert.Contains(kind, sync);
			// 基线固件里前六个 Kind 合并写在同一行（Fix or CrossArmor or …），
			// 所以这里只断言"该 Kind 在固件里出现过"，不要求独立成行。
			Assert.Contains(kind, firmware);
		}
	}

	// 死动作的证据要一直可见：PatchRebuild 的可见性判据是 IsDecoration，而它恒为 false
	// （ModLibraryService.cs:1390-1392：装饰包已不再支持，属性保留只是为了不动十几处守卫）。
	// 这条用例的作用是：一旦有人把 IsDecoration 恢复成真值，这里会先红——提醒他先更新规划文档。
	[Fact]
	public void DecorationFlagIsStillHardwiredFalseSoPatchRebuildCannotShow()
	{
		var library = ReadManagerFile(Path.Combine("Services", "ModLibraryService.cs"));
		Assert.Contains("IsDecoration = false", library);

		var actions = ReadManagerFile(Path.Combine("ViewModels", "ModDetailsPageViewModel.Actions.cs"));
		var sync = ExtractMember(actions, "private void SyncActionVisibility(");
		Assert.Contains("PageActionKind.PatchRebuild => IsDecoration", sync);
	}

	// 评审给出的硬禁令（§2.1 B8）：MainWindow.xaml:282/283 两个浮动簇绑**同一个** PageActions，
	// 增删集合在双簇场景下行为不可预测。所以可见性/可点性变化只许改 Filter，不许 Add/Remove。
	// 现在这个约束靠约定；这条用例把它变成"改了就红"。
	[Fact]
	public void VisibilityChangesOnlyTouchTheFilterNotTheCollection()
	{
		var sync = ExtractMember(
			ReadManagerFile(Path.Combine("ViewModels", "ModDetailsPageViewModel.Actions.cs")),
			"private void SyncActionVisibility(");
		Assert.True(sync.Length > 0, "无法定位 SyncActionVisibility。");

		Assert.DoesNotContain("PageActions.Add(", sync);
		Assert.DoesNotContain("PageActions.Remove", sync);
		Assert.Contains("action.IsVisible =", sync);
	}

	private static string ReadManagerFile(string relativePath)
		=> File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", relativePath));

	// 取一个成员从签名开始、到下一个同级成员（或类结尾）为止的源码。
	// 注意：边界里同时认空格与制表符缩进（仓库两种混用），但**调用方必须先断言结果非空**——
	// 否则缩进一改就会静默拿到空串，断言随之真空通过（评审指出的既有缺陷）。
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
