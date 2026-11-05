using System.IO;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// <c>custom-model sectional plan|apply</c> 的实现。
/// <para>
/// <b>和 `custom-model convert` 的区别只有一件事：多了拆件。</b>
/// convert 产出的是"一具自定义素体"（每个 unit 一个部位），
/// 这里产出的是"一具**分件**的自定义素体"——换甲产物再按「7 标准槽 × 身形」重排成 section 组，
/// 每块几何都能在游戏里单独开关。
/// </para>
/// <para>
/// <b>两步都真的会生成产物。</b><c>plan</c> 不是"读一读不干活"：换甲产物必须先生成出来，
/// 拆件才有东西可规划。所以 plan 也走一遍生成，只是跑完把产物与素体登记一起撤掉，
/// 库和注册表都不留痕。
/// </para>
/// </summary>
internal static class CliCustomModelSectional
{
    public static async Task<int> RunAsync(
        CliInvocation invocation,
        bool apply,
        Action<object, Func<string>> emit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(emit);

        if (apply && !invocation.Options.Confirm)
        {
            throw new CliUserException(
                "ConfirmationRequired",
                "custom-model sectional apply 会把产物写入 Mod 库并登记素体；请人工确认后显式传入 --confirm。");
        }

        var workspace = CliWorkspace.Open(invocation.Options);
        if (workspace.ManagerRoot is null || workspace.LibraryRoot is null || workspace.GameDataRoot is null)
            throw new CliUserException("WorkspaceRequired", "请指定 --manager-root、--library 和 --game-data。");

        var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
        var source = CliProjections.ResolveMod(state.Snapshot.Nodes, invocation.Command[3]);
        var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);

        await using var reader = CoreServices.CreateModInformationReader();
        var facts = await CoreServices.CreateDirectModEquipmentSourceFactsReader(paths, reader).ReadAsync(
            source,
            workspace.LibraryRoot,
            new ModInformationRequest(
                ModInformationKind.AssetInventory,
                "CustomModelSectional",
                Context: ModInformationRequestContext.Create(ModInformationCacheScope.None, operationName: "CustomModelSectional"))
            { Property = ModInformationPropertyKind.UnitPartMapping },
            cancellationToken).ConfigureAwait(false);

        var converter = new CustomModelConversionService(paths, reader);
        var archives = invocation.Command[4]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var name = invocation.Command[5];

        // 产物落在临时目录：库里的位置由导入决定，中间目录只在这条命令的生命周期内存在。
        var staging = Path.Combine(Path.GetTempPath(), "hd2modmanager-sectional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? modelKey = null;
        var imported = false;
        try
        {
            if (!apply)
            {
                // 预演：换甲产物必须真的生成一遍，拆件才有东西可规划。
                // 跑完把产物与素体登记一起撤掉，库和注册表都不留痕。
                CustomModelConversionResult probe;
                try
                {
                    probe = await converter
                        .GenerateAsync(facts, archives, name, staging, workspace.GameDataRoot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    throw DescribeSourceFailure(exception);
                }

                modelKey = probe.Model.Key;
                // 预演也要归位：布局是"能不能被识别成分件模组"的关键，
                // 不归位的话预演看的是根目录，而实际执行看的是内容层，结论会对不上。
                var payloadDirectory = SectionalModBuilder.RelocateToPayloadDirectory(probe.OutputDirectory);
                var (plan, planWarnings) = await SectionalSplitter
                    .PlanAsync(payloadDirectory, cancellationToken)
                    .ConfigureAwait(false);

                await RollBackRegistrationAsync(paths, modelKey).ConfigureAwait(false);
                modelKey = null;
                emit(
                    new
                    {
                        ok = true,
                        mode = "plan",
                        name,
                        sourceUnitCount = plan.SourceUnitCount,
                        targets = plan.Targets.Select(target => new
                        {
                            slot = target.Slot,
                            bodyType = target.BodyType,
                            hostUnitId = target.HostUnitId,
                            donorUnitIds = target.DonorUnitIds,
                            sourceCount = target.SourceCount,
                        }).ToArray(),
                        groups = plan.Groups.Select(group => new
                        {
                            name = group.Name,
                            exclusiveGroup = group.ExclusiveGroup,
                            memberUnitIds = group.MemberUnitIds,
                            enabledByDefault = group.EnabledByDefault,
                        }).ToArray(),
                        dropped = plan.Dropped.Select(dropped => new { unitId = dropped.UnitId, reason = dropped.Reason }).ToArray(),
                        mergedSlots = plan.Targets.Count(target => target.DonorUnitIds.Count != 0),
                        warnings = planWarnings,
                    },
                    () => FormatPlan(name, plan, planWarnings));
                return 0;
            }

            // 执行：生成 → 归位到 vpk2patch/ → 拆件 → 剪枝，整条管线在 Core 里
            // （管理器走的是同一份实现，见 SectionalModBuilder 的注释）。
            SectionalModArtifacts artifacts;
            try
            {
                artifacts = await SectionalModBuilder
                    .BuildAsync(
                        converter,
                        facts,
                        archives,
                        name,
                        staging,
                        workspace.GameDataRoot,
                        CoreServices.CreateRuntimeArmorRegistryService(paths),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (exception.Message.StartsWith("拆件失败", StringComparison.Ordinal))
            {
                throw new CliUserException("SectionalSplitFailed", "拆件失败，产物已丢弃、素体登记已撤销：" + exception.Message);
            }
            catch (InvalidDataException exception)
            {
                throw DescribeSourceFailure(exception);
            }

            modelKey = artifacts.Model.Key;
            var split = artifacts.Split;
            var compaction = artifacts.Compaction;

            // 导入完全复用 import plan/validate/apply 那条守护路径：先建计划，再在跨进程写租约
            // 持有期间重新校验一次，最后才写库。**不绕过去**——绕过就等于让这条路没有护栏。
            var inspection = await CliImportPlanArtifacts.InspectAsync(artifacts.ModDirectory, cancellationToken).ConfigureAwait(false);
            var document = CliImportPlanArtifacts.CreateDocument(inspection);
            await using var lease = await CoreServices.CreateLibraryWriteLeaseProvider()
                .AcquireAsync(paths.ModsDirectory)
                .ConfigureAwait(false);
            var validation = await CliImportValidator.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid || validation.CurrentInspection is null)
            {
                throw new CliUserException(
                    "SectionalImportInvalid",
                    "产物已生成但导入校验未通过，已撤销素体登记：" + DescribeValidation(validation));
            }

            var execution = CoreServices.CreateModImportExecutionService(paths);
            var result = await execution.ImportFolderAsync(validation.CurrentInspection.Source.EffectivePath).ConfigureAwait(false);
            imported = true;
            emit(
                new
                {
                    ok = true,
                    mode = "apply",
                    name,
                    modelKey,
                    payloadDirectory = artifacts.PayloadDirectory,
                    targets = split.Plan.Targets.Count,
                    groups = split.Groups.Groups.Count,
                    mergedSlots = split.Plan.Targets.Count(target => target.DonorUnitIds.Count != 0),
                    prunedPieceCount = compaction.RemovedPieceCount,
                    remainingPieceCount = compaction.RemainingPieceCount,
                    importedNodeCount = GetImportedNodeCount(result),
                    warnings = split.Warnings,
                },
                () => $"已生成并导入分件模组「{name}」：{split.Plan.Targets.Count} 个输出 unit · "
                    + $"{split.Groups.Groups.Count} 个 section 组 · {compaction.RemainingPieceCount} 个组件"
                    + (compaction.Changed ? $"（剪掉 {compaction.RemovedPieceCount} 个悬空组件）" : string.Empty)
                    + $" · 导入 {GetImportedNodeCount(result)} 个节点。");
            return 0;
        }
        finally
        {
            // 没导入成功 = 这次生成的素体没有归属（素体的归属就是 Mod 目录里的 custom-model.json），
            // 必须撤掉登记，否则护甲映射页会挂着一个选了什么都不反应的素体。
            if (!imported && modelKey is not null)
                await RollBackRegistrationAsync(paths, modelKey).ConfigureAwait(false);
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// 换甲来源为空是最常见的失败：只有**取代原版护甲/头盔**的 Mod 才有"仅属于某个 Archive
    /// 的 Unit"可当来源。已经是自定义素体的 Mod 换不出东西来 —— 那种输入不需要这一步。
    /// 这里把话说明白，别只丢一句"来源无效"。
    /// </summary>
    private static CliUserException DescribeSourceFailure(InvalidDataException exception)
        => new(
            "SectionalSourceInvalid",
            $"这个 Mod 没有可用于换甲的来源：{exception.Message}"
            + "（只有取代原版护甲/头盔的 Mod 才有独占 Unit 可当来源；已经是自定义素体的 Mod 没有可换的来源。）");

    private static int GetImportedNodeCount(ModImportExecutionResult result)
        => result.ImportedNodeIds?.Count ?? 0;

    private static string DescribeValidation(CliImportValidationResult validation)
    {
        var issues = validation.Issues;
        return issues.Count == 0 ? "（没有更多细节）" : string.Join("；", issues.Select(issue => issue.Message));
    }

    private static async Task RollBackRegistrationAsync(StoragePaths paths, string modelKey)
    {
        try
        {
            await new CustomModelOwnershipService(
                CoreServices.CreateRuntimeArmorRegistryService(paths)).RemoveAsync(modelKey).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            // 撤销失败没有更好的兜底办法，但也不能因此把成功的主流程判成失败 ——
            // 交给调用方在消息里体现即可（这里只保证不抛）。
            Console.Error.WriteLine($"撤销素体登记失败（{modelKey}）：{exception.Message}");
        }
    }

    private static string FormatPlan(string name, SectionalSplitPlan plan, IReadOnlyList<string> warnings)
    {
        var lines = new List<string>
        {
            $"「{name}」拆件预演：{plan.SourceUnitCount} 个来源 unit → {plan.Targets.Count} 个输出 unit · {plan.Groups.Count} 个 section 组",
        };
        foreach (var target in plan.Targets.OrderBy(item => item.Slot, StringComparer.Ordinal).ThenBy(item => item.BodyType, StringComparer.Ordinal))
        {
            lines.Add($"  {target.Slot}/{target.BodyType}  承载 {target.SourceCount} 个来源"
                + (target.DonorUnitIds.Count == 0 ? "（无需合并）" : $"（合并 {target.DonorUnitIds.Count} 个）"));
        }
        foreach (var dropped in plan.Dropped)
            lines.Add($"  丢弃 {dropped.UnitId}：{dropped.Reason}");
        foreach (var warning in warnings)
            lines.Add($"  注意：{warning}");
        return string.Join(Environment.NewLine, lines);
    }
}
