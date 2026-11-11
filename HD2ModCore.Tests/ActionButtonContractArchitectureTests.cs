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

	// 用户订的契约（§2.1 B9）：**动作按钮永不禁用**。缺什么由执行器当场说清，
	// 而不是灰一个按钮让用户猜。这条用例只扫底栏内容模板，因为浮动簇还有一处
	// 历史绑定（FloatingActionCluster.xaml:98 的 {Binding IsEnabled}，恒真），
	// 按规划 P1 一并删掉，届时把范围扩到全部 *Content.xaml。
	[Fact]
	public void BottomBarButtonsNeverBindIsEnabled()
	{
		var views = Path.Combine(FindRepositoryRoot(), "HD2ModManager", "Views");
		var files = Directory.GetFiles(views, "*BarContent.xaml");
		Assert.NotEmpty(files);

		var inspectedButtons = 0;
		foreach (var file in files)
		{
			// 先去掉 XML 注释：说明性文字里会提到 IsEnabled，不能算违规。
			var text = StripXmlComments(File.ReadAllText(file));
			foreach (var element in ElementsStartingWith(text, "<Button", "<ToggleButton", "<RepeatButton"))
			{
				inspectedButtons++;
				Assert.False(
					element.Contains("IsEnabled", StringComparison.Ordinal),
					$"{Path.GetFileName(file)} 里的按钮绑了 IsEnabled：按钮按契约永不禁用，"
					+ "缺什么应当由执行器在点击后说清（而不是把按钮灰掉）。");
			}
		}

		// 扫到 0 个按钮说明语料变了（文件名改了/模板搬走了），此时这条用例是空转，必须红。
		Assert.True(inspectedButtons > 0, "底栏模板里一个按钮都没扫到，用例失去意义。");
	}

	private static string StripXmlComments(string xaml)
	{
		var builder = new System.Text.StringBuilder(xaml.Length);
		var index = 0;
		while (index < xaml.Length)
		{
			var open = xaml.IndexOf("<!--", index, StringComparison.Ordinal);
			if (open < 0)
			{
				builder.Append(xaml, index, xaml.Length - index);
				break;
			}

			builder.Append(xaml, index, open - index);
			var close = xaml.IndexOf("-->", open, StringComparison.Ordinal);
			if (close < 0) break;
			index = close + 3;
		}

		return builder.ToString();
	}

	// 取每个标签从名字到它自己的 '>' 为止的文本（够判断属性了；底栏里的按钮都是自闭合或单行属性）。
	private static IEnumerable<string> ElementsStartingWith(string text, params string[] tags)
	{
		var found = new List<string>();
		foreach (var tag in tags)
		{
			var index = 0;
			while (true)
			{
				index = text.IndexOf(tag, index, StringComparison.Ordinal);
				if (index < 0) break;
				var end = text.IndexOf('>', index);
				found.Add(end < 0 ? text[index..] : text[index..end]);
				index += tag.Length;
			}
		}

		return found;
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
