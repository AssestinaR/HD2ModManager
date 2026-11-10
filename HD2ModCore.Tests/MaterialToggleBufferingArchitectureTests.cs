namespace HD2ModCore.Tests;

// 作用：钉住"材质开关批量操作必须排队、不许丢弃"这条正确性要求。
// Purpose: Pins that batch material-option toggles are queued instead of dropped.
//
// 病根是一个很容易写对的错觉："忙就 return，用户再点一次就行"。
// 但开关是**乐观更新**的——点下去行上的状态就已经翻了，丢弃等于文件没写、
// 随后那次 RefreshMaterialOptions 又把界面推回旧状态，用户看到的是"改一下又被恢复"。
// 批量开关时每一行都会撞一次，所以这条不能只靠人记得。
public sealed class MaterialToggleBufferingArchitectureTests
{
	private const string MaterialsFile = "ModDetailsPageViewModel.Materials.cs";

	[Fact]
	public void MaterialOperationsAreQueuedInsteadOfDroppedWhenBusy()
	{
		var page = ReadMaterialsViewModel();

		// 旧的"忙就丢弃"必须彻底消失：字段、提示文案、以及那条 return 都不许回来。
		Assert.DoesNotContain("_materialOperationInFlight", page);
		// 只看代码调用，不看注释——注释里引用旧文案是应该保留的上下文。
		Assert.DoesNotContain("Show(\"上一项改动还在处理", page);

		// 现在是串行闸门 + 合并标记。
		Assert.Contains("_materialOperationGate.WaitAsync()", page);
		Assert.Contains("_materialOperationGate.Release()", page);
		Assert.Contains("_materialSyncPending", page);
	}

	[Fact]
	public void MaterialToggleJoinsAPendingSyncInsteadOfStartingAnother()
	{
		var page = ReadMaterialsViewModel();

		// 开关操作读的是行上的最新状态，所以排队期间可以合并；
		// 顺序变更与改名带着各自的快照/取值，不能合并——只有开关这一处传 true。
		Assert.Equal(1, CountOccurrences(page, "coalescibleSync: true"));
		Assert.Contains("if (_materialSyncPending) return;", page);
		// 开始读状态之前必须清掉标记，否则同步期间到达的点击会被永久漏掉。
		Assert.Contains("if (coalescibleSync) _materialSyncPending = false;", page);
	}

	[Fact]
	public void MaterialRefreshIsDeferredWhileANewerSyncIsPending()
	{
		var page = ReadMaterialsViewModel();

		// 刷新会用磁盘状态覆盖行上的乐观改动。还有更新的同步在排队时刷早了，
		// 等于把用户刚点的那几下丢掉——所以必须等最后一次同步跑完再刷。
		Assert.Contains("if (!_materialSyncPending) RefreshMaterialOptions();", page);
	}

	// 第二个漏点：重建材质列表不只在写操作末尾发生。改动成功后会通报"内容变了"，
	// 库随即发 SnapshotChanged，详情页的 Refresh() 又回到 RefreshMaterialOptions()。
	// 那条路径如果照样读盘，会把用户刚点、但还没落盘的那几下覆盖掉。
	[Fact]
	public void MaterialCatalogRebuildYieldsToAPendingSync()
	{
		var page = ReadMaterialsViewModel();

		var refresh = ExtractMember(page, "internal void RefreshMaterialOptions()");
		Assert.True(refresh.Length > 0, "无法定位 RefreshMaterialOptions。");
		Assert.Contains("if (_materialSyncPending) return;", refresh);
	}

	private static string ExtractMember(string source, string signatureStart)
	{
		var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
		if (start < 0) return string.Empty;
		var end = source.IndexOf("\n\tinternal ", start + signatureStart.Length, StringComparison.Ordinal);
		if (end < 0) end = source.IndexOf("\n\tprivate ", start + signatureStart.Length, StringComparison.Ordinal);
		return end > start ? source[start..end] : source[start..];
	}

	private static string ReadMaterialsViewModel()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "HD2ModManager", "ViewModels", MaterialsFile);
		Assert.True(File.Exists(path), $"找不到 {MaterialsFile}：{path}");
		return File.ReadAllText(path);
	}

	private static int CountOccurrences(string source, string value)
	{
		var count = 0;
		var index = 0;
		while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += value.Length;
		}
		return count;
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
