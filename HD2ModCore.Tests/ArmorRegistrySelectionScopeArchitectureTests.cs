namespace HD2ModCore.Tests;

// 作用：把护甲映射页"多选该出现哪颗按钮"这条链路钉死。
// Purpose: Pins the chain that decides which bulk actions appear on the armour mapping page.
//
// 背景：底栏的按钮**完全由选择作用域推导**（BottomBarCoordinator.ShowX => Scope == "ArmorRegistry:X"）。
// 于是"多选里没有解除映射目标 / 注册为映射目标"这类报障，根因永远落在同一处：
// 那一刻的作用域不是 Targets / AvailableAsTarget。而作用域只有一个来源——页面把
// 三个列表的选中集合交给 SelectionCoordinator 时用的那个常量。
//
// 这条链路上任何一环被改掉（改常量、改作用域推导、按钮从 XAML 里删掉、快照刷新顺手
// selection.Clear()），用户看到的就是"按钮不见了"，而不会有任何报错。所以每一环都钉一条。
public sealed class ArmorRegistrySelectionScopeArchitectureTests
{
	// 左槽两张卡 = 两种语义：视觉来源列表里多选是"把护甲升为来源"，
	// 某个来源的目标列表里多选是"解除这些映射目标"。作用域必须跟着卡片走。
	[Fact]
	public void LeftCardDecidesTheSelectionScope()
	{
		var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));

		var detail = ExtractMember(page, "public void ApplyDetailSelection(");
		Assert.True(detail.Length > 0, "无法定位 ApplyDetailSelection。");
		Assert.Contains("ArmorRegistryScopes.Targets", detail);

		var available = ExtractMember(page, "public void ApplyAvailableSelection(");
		Assert.True(available.Length > 0, "无法定位 ApplyAvailableSelection。");
		Assert.Contains("ArmorRegistryScopes.AvailableAsTarget", available);
		Assert.Contains("ArmorRegistryScopes.Available", available);
		// 判据必须是"目标卡开着"这件事本身，不能是别的间接状态。
		Assert.Contains("IsProviderDetailOpen", available);

		var providers = ExtractMember(page, "public void ApplyProviderSelection(");
		Assert.True(providers.Length > 0, "无法定位 ApplyProviderSelection。");
		Assert.Contains("ArmorRegistryScopes.Providers", providers);
	}

	// 目标卡的标题必须同时说清"角色"和"是谁的目标"：左槽是两张卡在切换，
	// 标题是用户唯一能确认"我现在配的是哪个来源"的地方。
	[Fact]
	public void TargetCardTitleCarriesRoleAndSourceName()
	{
		var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var title = ExtractMember(page, "public string ProviderDetailHeaderTitle");
		Assert.True(title.Length > 0, "无法定位 ProviderDetailHeaderTitle。");
		Assert.Contains("映射目标", title);
		Assert.Contains("ProviderDetailTitle", title);
	}

	// 快照刷新不许把选择清掉：清了作用域，底栏那两颗按钮就消失，用户会报"多选里没有按钮"。
	// 允许的只有"按可见性修剪"。
	[Fact]
	public void SnapshotRefreshPrunesTheSelectionInsteadOfClearingIt()
	{
		var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var apply = ExtractMember(page, "private void ApplySnapshot(");

		Assert.True(apply.Length > 0, "无法定位 ApplySnapshot。");
		Assert.Contains("PruneSelectionAfterRebuild()", apply);
		Assert.DoesNotContain("selection.Clear();", apply);

		var prune = ExtractMember(page, "private void PruneSelectionAfterRebuild(");
		Assert.True(prune.Length > 0, "无法定位 PruneSelectionAfterRebuild。");
		Assert.Contains("selection.Replace(scope, survivors)", prune);
	}

	// 目标卡不能因为"这个来源还没有映射"就自己关掉——那样用户根本来不及在里面选目标。
	[Fact]
	public void TargetCardStaysOpenWhileTheSourceStillExists()
	{
		var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));
		var exists = ExtractMember(page, "private bool ProviderStillExists(");

		Assert.True(exists.Length > 0, "无法定位 ProviderStillExists。");
		Assert.Contains("pendingProviderIds.Contains", exists);
		Assert.Contains("Mappings.Any", exists);
		Assert.DoesNotContain("Count ==", exists);
	}

	// 四颗（现在是五颗）按钮必须同时在四处存在：协调器的可见性、演示层的转发、
	// XAML 的绑定、Shell 的命令。少一处就是"按钮不出现"或"点了没反应"。
	[Fact]
	public void EveryBulkArmorActionIsWiredThroughAllFourLayers()
	{
		var root = FindRepositoryRoot();
		var coordinator = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "BottomBarCoordinator.cs"));
		var presentation = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Views", "SelectionActionBarPresentation.cs"));
		var xaml = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Views", "SelectionActionBarContent.xaml"));
		var shell = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ShellViewModel.cs"));

		foreach (var name in new[]
		{
			"ShowPromoteArmorProviders",
			"ShowRegisterArmorTargets",
			"ShowReleaseArmorProviders",
			"ShowReleaseArmorTargets",
			"ShowClearArmorTargets",
		})
		{
			Assert.Contains(name, coordinator);
			Assert.Contains(name, presentation);
			// 可见性必须在 RefreshState 里被通知，否则按钮只会在页面重建时才对一次。
			Assert.Contains($"OnPropertyChanged(nameof({name}))", coordinator);
		}

		foreach (var command in new[]
		{
			"PromoteArmorProvidersCommand",
			"RegisterArmorTargetsCommand",
			"ReleaseArmorProvidersCommand",
			"ReleaseArmorTargetsCommand",
			"ClearArmorTargetsCommand",
		})
		{
			Assert.Contains(command, presentation);
			Assert.Contains(command, xaml);
			Assert.Contains(command, shell);
		}

		// 两颗"解除映射目标"分别属于来源卡与目标卡；标签相同是刻意的，作用域不同。
		Assert.Contains("Content=\"注册为映射目标\"", xaml);
		Assert.Equal(2, CountOccurrences(xaml, "Content=\"解除映射目标\""));
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
