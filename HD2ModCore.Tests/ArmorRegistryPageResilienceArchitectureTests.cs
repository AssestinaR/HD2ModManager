namespace HD2ModCore.Tests;

// 作用：把"护甲页取数失败绝不会把整个管理器炸掉"这条不变式固定下来。
// Purpose: Pins the invariant that a failed armor-page data load must never take down the whole manager.
//
// 背景：护甲页是**启动即创建**的（视觉预热链路最后一环，见 StartupPrewarmArchitectureTests），
// 而它的取数入口 OnLoaded 是一个没有 try/catch 的 async void。这两件事叠在一起，
// 任何从 ApplySnapshot 逃出来的异常都会变成"管理器遇到未处理错误"的全局弹窗——
// 用户看到的是整个程序坏了，实际上只是页面有一行数据解析不出来。
//
// 20261011 就真的这么炸过一次：映射的目标 id 来自工作区/协议文件，
// 能不能解析取决于本机资产索引；模型映射（map_model）的 SourceItemId 恒为 null，
// 会被那条"补 Unknown"的循环跳过，于是它的目标从来没被兜底，直接用索引器就是 KeyNotFoundException。
// 下面两条用例分别钉住"不许再用裸索引器"和"异常必须就地转成页面状态"。
public sealed class ArmorRegistryPageResilienceArchitectureTests
{
	// 这条钉的是根因本身。判据是"读取"不是"写入"：
	// `Choices[...] = ...` 是补 Unknown / 建自定义模型，安全；
	// 出现在**取值位置**（当构造参数，例如 `Choices[mapping.TargetItemId],`）才是会抛 KeyNotFoundException 的那个。
	// 注意判据必须是"右括号后是不是 `=`"，不能是"行首是不是 Choices["——
	// 历史 bug 那一行恰好就是行首的 `Choices[mapping.TargetItemId],`，行首启发式会把它当成写入放过。
	[Fact]
	public void ApplySnapshotNeverReadsChoicesThroughTheRawIndexer()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var apply = ExtractMember(page, "private void ApplySnapshot(");

		Assert.True(apply.Length > 0, "无法定位护甲页的 ApplySnapshot 方法。");
		Assert.Contains("ResolveChoice(mapping.TargetItemId)", apply);
		var rawRead = FindChoicesRead(apply);
		Assert.True(rawRead is null, $"ApplySnapshot 里出现了对 Choices 的裸索引器**读取**：{rawRead}（会抛 KeyNotFoundException，应改走 ResolveChoice）。");
		// 兜底工厂必须在同一个文件里，否则上面的替换只是把崩溃挪了个地方。
		Assert.Contains("private RuntimeArmorChoiceViewModel ResolveChoice(uint itemId)", page);
	}

	// 兜底可以，静默不行。ResolveChoice 缺一次要留一条 WARN，
	// 否则"索引里少了一条护甲"这种事会被显示成"未知护甲"然后没人知道。
	[Fact]
	public void MissingArmorFallbackLogsInsteadOfSwallowing()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var resolve = ExtractMember(page, "private RuntimeArmorChoiceViewModel ResolveChoice(");

		Assert.True(resolve.Length > 0, "无法定位 ResolveChoice。");
		Assert.Contains("Choices.TryGetValue(itemId, out var choice)", resolve);
		Assert.Contains("RuntimeArmorChoiceViewModel.Unknown(itemId)", resolve);
		Assert.Contains("LogService.Warn(", resolve);
	}

	// 第二道防线：即使将来又有别的异常从 RefreshAsync 漏出来，
	// 也必须在这里被兜成页面状态，而不是从 async void 的 UI 事件逃到全局。
	[Fact]
	public void CatalogLoadFailureBecomesPageStateInsteadOfAGlobalCrash()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var load = ExtractMember(page, "private async Task LoadCatalogOnceAsync(");

		Assert.True(load.Length > 0, "无法定位 LoadCatalogOnceAsync。");
		Assert.Contains("catch (Exception exception)", load);
		Assert.Contains("LogService.Error(", load);
		Assert.Contains("StatusTitle = ", load);
		Assert.Contains("StatusDetail = ", load);
		// 失败后必须清掉单飞账本，让下一次打开页面还能重试；
		// 且 hasLoadedCatalog 仍为 false（catch 里不碰它）——所以只能靠 finally 复位。
		Assert.Contains("finally", load);
		Assert.Contains("catalogLoadTask = null", load);
		Assert.DoesNotContain("hasLoadedCatalog = true", load);
	}

	// 找出第一处"对 Choices 的裸索引器读取"，没有则返回 null。
	// 判据：`Choices[...]` 的右括号之后（跳过空格）如果不是单个 `=`，它就是一次读取。
	// 赋值（`Choices[x] = ...`）是允许的写入；读取一律要经过 ResolveChoice 的兜底。
	private static string? FindChoicesRead(string source)
	{
		const string marker = "Choices[";
		var index = 0;
		while ((index = source.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
		{
			var close = source.IndexOf(']', index + marker.Length);
			if (close < 0) return source[index..Math.Min(source.Length, index + 40)];
			var cursor = close + 1;
			while (cursor < source.Length && source[cursor] == ' ') cursor++;
			var isAssignment = cursor < source.Length && source[cursor] == '='
				&& (cursor + 1 >= source.Length || source[cursor + 1] != '=');
			if (!isAssignment)
			{
				var end = source.IndexOfAny(['\n', '\r'], index);
				return end < 0 ? source[index..] : source[index..end];
			}
			index = close + 1;
		}
		return null;
	}

	// 与 StartupPrewarmArchitectureTests 的同名辅助同构，但**同时**以 public 成员为右边界：
	// LoadCatalogOnceAsync 之后紧跟的就是 `public async Task FlushWorkspaceAsync()`，
	// 只认 `\n\tprivate ` 会把整个 RefreshAsync 也吞进来（那里有 hasLoadedCatalog = true，会误判）。
	// 第三道防线：把"页面为什么可能显示得比预期少"说出来。
	// 这两条都不是"要不要显示映射"的问题——映射永远照显示；
	// 它们回答的是"为什么名字变成了未知护甲"和"这份协议可能不是本库的"。
	// 少了它们，用户会把"索引没建"误当成"我的映射丢了"。
	[Fact]
	public void ThePageSaysWhyItMightBeShowingLessThanExpected()
	{
		var root = FindRepositoryRoot();
		var page = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var status = ExtractMember(page, "private void UpdateStatus(");

		Assert.True(status.Length > 0, "无法定位 UpdateStatus。");
		// 工作区缺失 → 退回协议副本时必须说清楚。
		Assert.Contains("RuntimeArmorRegistryDocumentSource.ProtocolFallback", status);
		Assert.Contains("请到设置页确认模组库地址", status);
		// 目录 0 条 → 名字解析不了，要指路而不是静默显示未知护甲。
		Assert.Contains("cachedCatalog.Count == 0", status);
		Assert.Contains("请到设置页建立资产索引", status);
		// 只追加、不覆盖：这些说明不能把运行时状态那段挤掉。
		Assert.Contains("StatusDetail", status);
	}

	private static string ExtractMember(string source, string signatureStart)
	{
		var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
		if (start < 0) return string.Empty;
		var searchFrom = start + signatureStart.Length;
		var privateEnd = source.IndexOf("\n\tprivate ", searchFrom, StringComparison.Ordinal);
		var publicEnd = source.IndexOf("\n\tpublic ", searchFrom, StringComparison.Ordinal);
		var end = privateEnd < 0 ? publicEnd
			: publicEnd < 0 ? privateEnd
			: Math.Min(privateEnd, publicEnd);
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
