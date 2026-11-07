using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// <c>library family-merge</c>：把「材质包是独立 Mod」的旧形状，归拢成存储约定要求的那**一个**条目。
/// <para>
/// 只有两步：**把材质包的来源 VPK 搬进模型 Mod 的根** → **对模型跑一次重构建**。
/// 产物形状由重构建保证（一次转换、每个输入一个 patch 组、平铺进 <c>sectional/</c>），
/// 这里不另写"搬 patch + 重写结构文件"的迁移器——那种迁移器本身就是第二层错误的来源。
/// </para>
/// <para>
/// 搬走 VPK 之后留下的空壳不在这里删：<c>library sync apply</c>（库对账）与
/// <c>library cleanup</c>（残留清理）已经负责这件事，不必再写第三套删除逻辑。
/// </para>
/// </summary>
internal static class CliFamilyMerge
{
	internal static ModFamilyMergePlan BuildPlan(CliWorkspace workspace)
		=> new ModFamilyMerger(new PatchFileNameParser()).Plan(RequireLibrary(workspace));

	internal static async ValueTask<ModFamilyMergeOutcome> ApplyAsync(
		CliWorkspace workspace,
		CliLibraryState state,
		ModFamilyMergePlan plan,
		Action<string>? onStage,
		CancellationToken cancellationToken)
	{
		var libraryRoot = RequireLibrary(workspace);
		var managerRoot = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");
		var context = new VpkHostContext(workspace.GameDataRoot, new StoragePaths(managerRoot, libraryRoot));

		async ValueTask<string?> Rebuild(string directoryName, string directory, CancellationToken token)
		{
			// `vpk rekey` 认的是**库节点标识**：目录名不一定能唯一解析，优先用节点 id。
			var node = state.Snapshot.Nodes.Values.FirstOrDefault(candidate =>
				string.Equals(candidate.RelativePath, directoryName, StringComparison.OrdinalIgnoreCase));
			var selector = node is null ? directoryName : node.Id.Value.ToString("N");
			var outcome = await VpkRebuildService.RunAsync(
				selector,
				directory,
				context,
				// null = 按 wardrobe.json 记着的配置重放（换甲目标 / section 开关 / 头身合并）。
				targetArchiveIds: null,
				disabledSections: null,
				onStage,
				token).ConfigureAwait(false);
			return outcome.Ok ? null : outcome.Error ?? "原因未知";
		}

		return await new ModFamilyMerger(new PatchFileNameParser())
			.ApplyAsync(libraryRoot, plan, Rebuild, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>人话投影：先说"几个家族要并"，再说每个家族搬几个 VPK。</summary>
	internal static string FormatPlan(ModFamilyMergePlan plan)
	{
		var lines = new List<string>();
		if (plan.Families.Count == 0)
		{
			lines.Add("没有需要归拢的家族：库里每个模型条目都是自洽的（材质已在同一个 Mod 里）。");
		}
		else
		{
			lines.Add($"需要归拢 {plan.Families.Count} 个家族（把材质包的来源 VPK 搬进模型条目，再重构建一次）：");
			foreach (var family in plan.Families)
			{
				lines.Add($"  · {family.Model.DirectoryName}"
					+ $"（命名空间 {family.IdNamespace}）：并进 {family.Materials.Count} 个材质包、"
					+ $"{family.Materials.Sum(item => item.SourceVpks.Count)} 个 VPK");
				foreach (var material in family.Materials)
					lines.Add($"      - {material.DirectoryName}：{string.Join("、", material.SourceVpks.Select(Path.GetFileName))}");
			}
		}

		lines.AddRange(plan.Notes.Select(note => "  ! " + note));
		return string.Join(Environment.NewLine, lines);
	}

	internal static string FormatOutcome(ModFamilyMergeOutcome outcome, string libraryRoot)
	{
		var lines = new List<string>
		{
			outcome.MergedFamilies == 0
				? "没有家族被归拢。"
				: $"归拢完成：{outcome.MergedFamilies} 个家族、搬了 {outcome.MovedVpks} 个来源 VPK，"
					+ $"并重构建了：{string.Join("、", outcome.Rebuilt)}。",
		};
		lines.AddRange(outcome.Notes.Select(note => "  · " + note));
		lines.AddRange(outcome.Errors.Select(error => "  ✗ " + error));
		if (outcome.MergedFamilies != 0)
		{
			lines.Add(string.Empty);
			lines.Add("接下来（材质包原来的空壳还留在库里，交给既有工具收尾）：");
			lines.Add($"  hd2mod library sync apply --manager-root <管理器目录> --library \"{libraryRoot}\" --confirm --json");
			lines.Add($"  hd2mod library cleanup --manager-root <管理器目录> --library \"{libraryRoot}\" --confirm --json");
		}

		return string.Join(Environment.NewLine, lines);
	}

	private static string RequireLibrary(CliWorkspace workspace)
		=> workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "请用 --library 指定 Mod 库。");
}
