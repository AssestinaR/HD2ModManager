using System.Text.RegularExpressions;

namespace HD2ModCore.Tests;

// 作用：约束 HD2ModManager 不绕过共享信息中心创建稳定事实基础设施。
// Purpose: Prevent HD2ModManager from bypassing the shared information center.
public sealed class ModInformationCenterArchitectureTests
{
	[Fact]
	public void ManagerProductionCodeDoesNotCreateInformationInfrastructureOrUseObsoleteCenterlessFactories()
	{
		var managerRoot = FindRepositoryRoot();
		var sourceFiles = Directory.EnumerateFiles(Path.Combine(managerRoot, "HD2ModManager"), "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
		var forbidden = new Regex(
			@"(?:CoreServices\.)?CreateModDataIndex\s*\(|new\s+(?:ModDataIndex|JsonModInformationCache|SqliteModFactsStore)\s*\(|new\s+(?:ModFileFactsProducer|ModContentFactsService|ReferenceGraphProducer|MaintenanceAnalysisProducer|UnitVersionInformationProducer|AdvancedUnitAnalysisProducer|ModThumbnailProducer)\s*\(",
			RegexOptions.Compiled);
		var violations = sourceFiles
			.SelectMany(path => File.ReadLines(path).Select((line, number) => (path, line, number: number + 1)))
			.Where(item => forbidden.IsMatch(item.line))
			.Select(item => $"{Path.GetRelativePath(managerRoot, item.path)}:{item.number}: {item.line.Trim()}")
			.ToArray();

		Assert.Empty(violations);
	}

	// 作用：§7 护栏测试 1 的 Manager 半边——Manager 不得绕过信息中心直接消费 game-data 旁路服务。
	// Purpose: The Manager half of guardrail test 1 in §7 — the Manager must not consume the game-data
	//          bypass services directly.
	//
	// 现状（2026-10-11 实测）：Manager 对这三个服务**零直调**，且完全不引用后两个接口。
	// 本用例把这条边界固定下来——它是"护甲页/浏览器页最终改为向中心请求"（§6 阶段 2）之前必须守住的底线：
	// 一旦 Manager 为了图快又贴一张膏药，这里立刻失败。
	[Fact]
	public void ManagerDoesNotConsumeTheGameDataBypassServicesDirectly()
	{
		var root = FindRepositoryRoot();
		var managerFiles = EnumerateProductionSources(Path.Combine(root, "HD2ModManager"));

		// 1) 唯一名为 GetArchiveSummariesAsync 的成员只存在于 IAssetArchiveIndexService，因此这个方法是精确判据。
		var methodCallViolations = managerFiles
			.Where(path => File.ReadAllText(path).Contains("GetArchiveSummariesAsync", StringComparison.Ordinal))
			.Select(path => Path.GetRelativePath(root, path))
			.ToArray();
		Assert.Empty(methodCallViolations);

		// 2) 后两个服务没有同名成员可依赖，改用类型引用判定——Manager 连类型都不该看到。
		foreach (var forbiddenType in new[] { "IDeployedOverrideGraphService", "IGameDataMappingFactsService" })
		{
			var typeViolations = managerFiles
				.Where(path => File.ReadAllText(path).Contains(forbiddenType, StringComparison.Ordinal))
				.Select(path => Path.GetRelativePath(root, path))
				.ToArray();
			Assert.Empty(typeViolations);
		}
	}

	// 作用：§7 护栏 3 的落地——护甲页不得为了"是否被覆盖 + 覆盖者图标"整包构建 GameData 归档浏览器。
	// Purpose: The armor page must not build the whole GameData archive browser just for coverage plus an icon.
	//
	// 改造前它每次打开都调 GameDataArchiveBrowserService.BuildAsync，而那一步会为**每一个** Mod
	// 请求一次资产清单（逐包解析 patch）——护甲页首开卡顿的根因，也是当时唯一把这件事做全的调用点。
	// 现在它只请求 ArmorCoverage（由已部署资产投影得出，与 Mod 库无关）。
	// 这条断言把"不再回头"固定下来：一旦有人为了省一次请求又贴回 BuildAsync，这里立刻失败。
	[Fact]
	public void ArmorPageProjectsCoverageInsteadOfBuildingTheGameDataArchiveBrowser()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ArmorRegistryPageViewModel.cs"));

		Assert.DoesNotContain("CreateGameDataArchiveBrowserService", source);
		Assert.DoesNotContain("BuildAsync", source);
		Assert.Contains("RequestArmorCoverageAsync", source);
	}

	// 作用：§7 护栏 1 的**业务服务半边**——收口点不是"不许提到这些类型"，而是"整包读取只有一个正当来源"。
	//
	// 与文档原文的偏差（2026-10-11 实测，值得记下来）：
	// 文档写的是"业务服务不得直调 BuildAsync / MapAsync / GetArchiveSummariesAsync"。
	// 实测 `MapAsync` 在 Core 里另有 **7 个正当调用点**（分布在 6 个文件：资产角色、材质投递、
	// 覆盖图、材质诊断……），它们要的就是映射事实本身，禁掉等于禁掉真实业务逻辑——那不是护栏，是误伤。
	// 所以这条护栏按**方法**收口，并且只收口"整包事实、只应有一个正当来源"的那两个：
	//   * `IAssetArchiveIndexService.GetArchiveSummariesAsync` —— 归档摘要是"游戏数据侧"的整包事实
	//   * `IDeployedOverrideGraphService`（整个类型）—— 部署图是"部署侧"的整包事实
	// 映射（`MapAsync`）的收口方式是 `IGameDataFactSource` 那道投影缝，见下一条用例。
	[Fact]
	public void WholePackageGameDataReadsAreConfinedToTheCompositionRootAndTheFactSourceAdapters()
	{
		var root = FindRepositoryRoot();
		var coreRoot = Path.Combine(root, "HD2ModCore");
		var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Path.Combine("Application", "IAssetArchiveIndexService.cs"),
			Path.Combine("Application", "IDeployedOverrideGraphService.cs"),
			// 装配点：它必须认识这些类型才能把它们接起来。
			Path.Combine("Infrastructure", "CoreServices.cs"),
			// 中心自己：它把这三个 application 接口当作可选生产者直接持有（§22.5）。
			Path.Combine("Infrastructure", "ModInformationCenter.cs"),
			// 事实源的两个适配器：一个走中心，一个直问旁路服务。
			Path.Combine("Infrastructure", "CenterGameDataFactSource.cs"),
			Path.Combine("Infrastructure", "DirectGameDataFactSource.cs"),
			// 实现自己当然会出现自己的名字。
			Path.Combine("Infrastructure", "AssetArchiveIndexService.cs"),
			Path.Combine("Infrastructure", "DeployedOverrideGraphService.cs"),
		};

		var violations = new List<string>();
		foreach (var path in EnumerateProductionSources(coreRoot))
		{
			var relative = Path.GetRelativePath(coreRoot, path);
			if (allowed.Contains(relative)) continue;
			var text = File.ReadAllText(path);
			if (text.Contains("GetArchiveSummariesAsync", StringComparison.Ordinal))
				violations.Add($"{relative}: 调用了 GetArchiveSummariesAsync");
			if (text.Contains("IDeployedOverrideGraphService", StringComparison.Ordinal))
				violations.Add($"{relative}: 引用了 IDeployedOverrideGraphService");
		}

		Assert.Empty(violations);
	}

	// 作用：把"投影只认识事实源"这条缝钉死。
	//
	// 改造前 `GameDataArchiveBrowserService` 直调三个旁路服务；现在它只认识 `IGameDataFactSource`。
	// 这条断言的价值不在今天，而在明天：只要有人为了省一层又把它接回旁路服务，这里立刻失败。
	[Fact]
	public void GameDataArchiveBrowserProjectsThroughTheFactSourceOnly()
	{
		var root = FindRepositoryRoot();
		var browser = File.ReadAllText(Path.Combine(
			root, "HD2ModCore", "Infrastructure", "GameDataArchiveBrowserService.cs"));

		Assert.Contains("IGameDataFactSource", browser);
		Assert.DoesNotContain("IAssetArchiveIndexService", browser);
		Assert.DoesNotContain("IGameDataMappingFactsService", browser);
		Assert.DoesNotContain("IDeployedOverrideGraphService", browser);

		// 两个工厂也必须走事实源：中心版给 Manager，直读版给 CLI 只读检查。
		var services = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CoreServices.cs"));
		Assert.Contains("new CenterGameDataFactSource(", services);
		Assert.Contains("new DirectGameDataFactSource(", services);
	}

	private static string[] EnumerateProductionSources(string projectDirectory)
		=> Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.ToArray();

	[Fact]
	public void CanonicalToolOperationsUseTheSharedPatchOperationWorkspace()
	{
		var root = FindRepositoryRoot();
		var sameKey = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));
		var crossArmor = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalCrossArmorOrchestrator.cs"));
		var helmetMerge = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "HelmetToArmorMergeService.cs"));

		Assert.Contains("IPatchOperationWorkspaceFactory", sameKey);
		Assert.Contains("operationWorkspace.Stage(rebuilt.Job)", sameKey);
		Assert.Contains("IPatchOperationWorkspaceFactory", crossArmor);
		Assert.Contains("operationWorkspace.Stage(outputTargetEntry)", crossArmor);
		Assert.DoesNotContain("StagePayloads(", crossArmor);
		Assert.Contains("IPatchOperationWorkspaceFactory", helmetMerge);
		Assert.Contains("StageEdit(operationWorkspace", helmetMerge);
		Assert.Contains("CacheScope = ModInformationCacheScope.None", helmetMerge);
	}

	[Fact]
	public void SharedPatchWriterPreservesUntouchedPayloadsAsSourceRanges()
	{
		var root = FindRepositoryRoot();
		var writer = File.ReadAllText(Path.Combine(root, "HD2ModAdaptation", "PatchReconstruction", "PatchWorkspace", "PatchWorkspaceWriter.cs"));

		Assert.Contains("CanonicalPayloadSourceRange", writer);
		Assert.DoesNotContain("private static byte[] ReadPayload", writer);
	}

	[Fact]
	public void SameKeyPlanningUsesCachedFactsAndDoesNotDecodeUnitPayloads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));
		var start = source.IndexOf("private async ValueTask<SameKeyReconstructionPlan> PlanAsync", StringComparison.Ordinal);
		var end = source.IndexOf("private static IReadOnlyList<TargetShellMeshMapping> BuildMappings", StringComparison.Ordinal);

		Assert.True(start >= 0 && end > start, "Unable to isolate Same-key planning implementation.");
		var planning = source[start..end];
		Assert.Contains("index?.Entries", planning);
		Assert.DoesNotContain("sourceReader.ReadAsync", planning);
		Assert.DoesNotContain("targetReader.ReadAsync", planning);
	}

	[Fact]
	public void SameKeyReconstructionUsesUnifiedReaderForSourcePatchAndUnitReads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));

		Assert.Contains("IModInformationReader", source);
		Assert.Contains("ReadSourcePatchIndexAsync", source);
		Assert.Contains("ReadCanonicalSourceUnitAsync", source);
		Assert.DoesNotContain("workspaceReader.ReadIndexAsync", source);
		Assert.DoesNotContain("new PatchUnitMeshReader()", source);
	}

	[Fact]
	public void CrossArmorReconstructionUsesUnifiedReaderForSourceSideReads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalCrossArmorOrchestrator.cs"));

		Assert.Contains("IModInformationReader", source);
		Assert.Contains("ReadSourcePatchIndexAsync", source);
		Assert.Contains("ReadSourcePayloadAsync", source);
		Assert.Contains("ReadSourceUnitAsync", source);
		Assert.DoesNotContain("patchWorkspaceReader.ReadIndexAsync", source);
		Assert.DoesNotContain("sourcePayloadReader.ReadPayloadAsync", source);
		Assert.DoesNotContain("sourceReader.ReadAsync", source);
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
