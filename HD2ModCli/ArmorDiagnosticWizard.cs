using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>Interactive, original-configuration-first diagnostic. Never deploys or packages a loader.</summary>
public static class ArmorDiagnosticWizard
{
    public static async Task<int> RunAsync(CliGlobalOptions options, string mode)
    {
        if (mode == "quick") return await ArmorDiagnosticQuick.RunAsync(options);
        if (!OperatingSystem.IsWindows()) throw new CliUserException("WindowsRequired", "诊断向导仅支持 Windows。");
        if (options.Json || Console.IsInputRedirected)
            throw new CliUserException("InteractiveRequired", "请双击诊断入口，在交互窗口中运行。");
        if (options.Output is not null || options.Plan is not null || options.Host is not null
            || options.AddToProfile is not null || options.Overwrite
            || options.Position is not null || options.Limit is not null || options.Cursor is not null
            || options.SetActive || options.WithUnsharedOptions || options.WithEffective || options.OnlyCompetition)
            throw new CliUserException("UnsupportedOption", "诊断向导不接受计划、导出路径或不安全 Lua 参数。");
        var manager = options.ManagerRoot ?? FindManager();
        var workspace = CliWorkspace.Open(options with { ManagerRoot = manager });
        if (workspace.LibraryRoot is null || workspace.GameDataRoot is null)
            throw new CliUserException("SetupRequired", "请先在管理器中设置 Mod 库和游戏目录。");
        var registry = RuntimeArmorRegistryService.ResolveRegistryRoot(options.RegistryRoot);
        var protocol = Path.Combine(registry, RuntimeArmorRegistryService.ProtocolFileName);
        var directory = Path.Combine(manager, ArmorDiagnosticSession.RelativeDirectory);
        Directory.CreateDirectory(directory);
        using var toolLease = new FileStream(Path.Combine(directory, ".wizard.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        ArmorDiagnosticSession? session = null;
        string? inventoryFingerprint = null;
        var selectedTarget = 0U;
        var outcomes = new List<object>();
        var stage = "original";
        Console.WriteLine("""
            护甲映射故障诊断（基础版）
            本工具不安装/分发 Lua 注入器，不附加调试器，不运行内存探针或强制刷新。
            保留当前全部模型包和映射作为原始条件；对照组仅临时关闭所有护甲映射。
            不改源 JSON、配置文件、模型包或游戏装备；请保持同一身形、头盔和测试护甲。
            请先关闭游戏和管理器。测试中不要打开管理器或自行重启游戏接着上一轮。
            """);
        if (File.Exists(Path.Combine(directory, "active.json")))
        {
            session = ArmorDiagnosticSession.Open(directory, protocol, EnsureStopped);
            if (!session.Journal.Completed)
            {
                Console.WriteLine("检测到未完成诊断。原备份保存在本机；优先恢复，不开始新测试。");
                if (Choose("输入 R 恢复并导出已有结果，或 Q 退出", "r", "q") == "r")
                {
                    EnsureStopped();
                    session.Restore();
                    ArmorDiagnosticEvidence.Save(session.RunDirectory, "recovery.json",
                        new { restored = true, utc = DateTimeOffset.UtcNow, byteExact = true });
                    Console.WriteLine("原始协议已逐字节恢复。");
                    PrintExport(session);
                }
                return 0;
            }
        }
        if (mode == "recover")
        {
            Console.WriteLine("没有待恢复的诊断。");
            if (session is not null) PrintExport(session);
            return 0;
        }
        session = null; // A new failed preflight must not modify the previous report.
        EnsureStopped();
        var validation = new GameDataTargetGuard().Validate(workspace.GameDataRoot);
        if (!validation.IsValid) throw new CliUserException("InvalidGameData", "游戏目录校验失败，请在管理器中重新确认。");
        // Hold normal cross-process library/deployment leases. No deployment writes.
        await using var libraryLease = await CoreServices.CreateLibraryWriteLeaseProvider().AcquireAsync(workspace.LibraryRoot);
        await using var gameLease = await CoreServices.CreateGameDataDeploymentLeaseProvider().AcquireAsync(workspace.GameDataRoot);
        var originalBytes = ArmorDiagnosticSession.ReadProtocol(protocol);
        var document = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(originalBytes).TrimStart('\uFEFF'));
        if (!document.Enabled || document.Mappings.Count == 0)
            throw new CliUserException("NoMapping", "当前实际协议没有启用映射，请先应用需要诊断的配置。");
        var service = new RuntimeArmorRegistryService(new StoragePaths(manager, workspace.LibraryRoot), registry);
        IReadOnlyDictionary<uint, string> names;
        try { names = (await service.ReadArmorCatalogAsync()).ToDictionary(x => x.ItemId, x => x.DisplayName); }
        catch { names = new Dictionary<uint, string>(); }
        Console.WriteLine("请选择发生问题的目标（仅用于标记报告，不会缩减你原有的其他映射）：");
        for (var i = 0; i < document.Mappings.Count; i++)
        {
            var mapping = document.Mappings[i];
            Console.WriteLine($"{i + 1}. {names.GetValueOrDefault(mapping.TargetItemId, "未收录名称")}  Item ID {mapping.TargetItemId}");
        }
        selectedTarget = document.Mappings[ReadIndex(document.Mappings.Count)].TargetItemId;
        Console.WriteLine("""
            导出内容：版本、数字 Item/Unit ID、部件结构、部署文件哈希、步骤结果、脱敏事件。
            不导出：模型包、完整日志、内存转储、源 JSON、恢复备份、用户名、绝对路径或模型名称。
            本机恢复记录仍包含完整原协议；不要把整个 diagnostics 文件夹发给别人。
            """);
        if (Choose("输入 Y 同意并开始，或 Q 退出", "y", "q") != "y") return 0;
        try
        {
            EnsureStopped();
            using (var protocolLease = new FileStream(Path.Combine(registry, ".manager.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (ArmorDiagnosticSession.Hash(ArmorDiagnosticSession.ReadProtocol(protocol)) != ArmorDiagnosticSession.Hash(originalBytes))
                    throw new InvalidOperationException("选择期间协议已改变，请重新开始。");
                session = ArmorDiagnosticSession.Create(directory, protocol, EnsureStopped);
            }
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "protocol-summary.json", ArmorDiagnosticEvidence.ProtocolSummary(document));
            var relevantItems = document.Mappings.SelectMany(m => m.SourceItemId is uint source
                    ? new[] { m.TargetItemId, source } : new[] { m.TargetItemId })
                .Concat((document.Models ?? []).Where(m => m.SourceItemId.HasValue).Select(m => m.SourceItemId!.Value))
                .Distinct().Take(1024).ToArray();
            var nativeDefinitions = new List<object>();
            foreach (var item in relevantItems)
            {
                try { nativeDefinitions.Add(new { itemId = item, pieces = await service.ReadArmorPiecesAsync(item) }); }
                catch { nativeDefinitions.Add(new { itemId = item, state = "metadata_unavailable" }); }
            }
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "catalog-summary.json", new
            {
                definitions = nativeDefinitions, metadataIsRuntimeObject = false
            });
            var inventory = await ArmorDiagnosticEvidence.InventoryAsync(workspace.GameDataRoot);
            inventoryFingerprint = Fingerprint(inventory);
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "inventory.json", inventory);
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "environment.json", new
            {
                schema = 1, cliVersion = typeof(ArmorDiagnosticWizard).Assembly.GetName().Version?.ToString(),
                osVersion = Environment.OSVersion.Version.ToString(), processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                gameVersion = FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(workspace.GameDataRoot)!, "bin", "helldivers2.exe")).FileVersion,
                loaderVersionVerified = false, loaderNotRedistributed = true
            });
            Console.WriteLine("稍后按提示从 Steam 启动游戏。若尚未穿上目标，先装备该护甲；设置过程故障也请记录。");
            Console.WriteLine($"目标 Item ID：{selectedTarget}。本次保留原始映射，不部署或换包。");
            foreach (var currentStage in new[] { "original", "disabled" })
            {
                stage = currentStage;
                if (stage == "disabled")
                {
                    Console.WriteLine("下一组仅关闭护甲映射，所有模型包保持不变。");
                    if (Choose("完全退出游戏后输入 Y 继续对照，或 Q 停止并恢复", "y", "q") == "q") break;
                    EnsureStopped();
                    await VerifyInventory();
                    session.Disable();
                }
                else if (Choose("输入 Y 准备开始原始条件测试，或 Q 取消", "y", "q") == "q") break;
                var since = DateTimeOffset.UtcNow;
                using var samplerStop = new CancellationTokenSource();
                var sampler = ArmorDiagnosticEvidence.SampleStatusAsync(session.RunDirectory, stage, registry, samplerStop.Token);
                try
                {
                    Console.WriteLine($"当前组：{(stage == "original" ? "原始映射" : "关闭映射")}。请从 Steam 启动游戏。");
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "launch_prompt", "pending");
                    var answer = Choose("进入舰船输入 Y；此前崩溃输入 C，卡死 H，取消 Q", "y", "c", "h", "q");
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "ship_entry", answer);
                    if (answer == "q") break;
                    if (answer != "y")
                    {
                        outcomes.Add(new { stage, operation = "before_armory", outcome = answer, verified = false });
                        continue;
                    }
                    using var game = GetOnlyGame();
                    var start = game.StartTime.ToUniversalTime();
                    if (start < since.UtcDateTime)
                        throw new InvalidOperationException("游戏早于本组启动提示，不能作为独立启动对照；请退出后重新测试。");
                    await VerifyInventory();
                    var expected = stage == "original" ? document :
                        CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(Convert.FromBase64String(session.Journal.DisabledBase64)));
                    // A ship-ready confirmation may precede a delayed initial
                    // runtime acknowledgement. Bounded wait, never accept a
                    // previous PID's status or silently switch to a new game.
                    var acknowledgementDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    while (true)
                    {
                        game.Refresh();
                        if (game.HasExited) throw new InvalidOperationException("游戏在运行时确认前已退出。");
                        var status = ArmorDiagnosticEvidence.ReadStatus(registry);
                        if (status.GetValueOrDefault("pid") == game.Id.ToString()
                            && status.GetValueOrDefault("state") is "apply_error" or "protocol_error")
                            throw new InvalidOperationException("Lua 报告应用/协议错误，停止本组；错误状态已由采样保存。");
                        if (status.GetValueOrDefault("pid") == game.Id.ToString()
                            && status.GetValueOrDefault("state") == "ready"
                            && status.GetValueOrDefault("revision") == expected.Revision.ToString()) break;
                        if (DateTimeOffset.UtcNow >= acknowledgementDeadline)
                            throw new InvalidOperationException("等待本次游戏的 Lua 运行时确认超时；本组未验证，不计为对照结果。");
                        await Task.Delay(1000);
                    }
                    VerifyRuntime(game, start, expected);
                    RecordState(game);
                    answer = Choose("确认一直使用所选目标、同一身形/头盔后输入 Y；设置时崩溃 C，卡死 H，误操作 X", "y", "c", "h", "x");
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "target_setup", answer, game.Id);
                    if (answer != "y") { outcomes.Add(new { stage, operation = "setup", outcome = answer }); continue; }
                    Console.WriteLine("先在舰船静置 30 秒，不打开军械库。");
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "idle_prompt", "pending", game.Id);
                    for (var second = 0; second < 30; second++)
                    {
                        await Task.Delay(1000);
                        VerifyRuntime(game, start, expected);
                    }
                    answer = Choose("静置正常回车；崩溃 C，卡死 H，误操作 X", "", "c", "h", "x");
                    outcomes.Add(new { stage, operation = "idle", outcome = answer == "" ? "success" : answer });
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "idle_result", answer == "" ? "success" : answer, game.Id);
                    if (answer != "") continue;
                    for (var cycle = 1; cycle <= 3; cycle++)
                    {
                        VerifyRuntime(game, start, expected);
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, $"cycle_{cycle}_prompt", "pending", game.Id);
                        Console.WriteLine($"第 {cycle}/3 次：打开军械库，不选择/预览其他护甲，直接关闭，等 5 秒后返回本窗口。");
                        answer = Choose("正常回车；打开时崩溃 O，关闭时崩溃 C，其他时刻崩溃 B，卡死 H，误操作 X", "", "o", "c", "b", "h", "x");
                        var outcome = answer == "" ? "success" : answer;
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, $"cycle_{cycle}_result", outcome, game.Id);
                        outcomes.Add(new { stage, operation = $"cycle_{cycle}", outcome });
                        RecordState(game);
                        if (answer != "") break;
                        VerifyRuntime(game, start, expected);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("本会话已停止：" + e.Message);
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "session_guard", "stopped");
                    outcomes.Add(new { stage, operation = "guard", outcome = "incomplete" });
                }
                finally
                {
                    samplerStop.Cancel();
                    try { await sampler; } catch { Console.WriteLine("状态采样未完整结束，已保留已写入记录。"); }
                    await ArmorDiagnosticEvidence.CollectEventsAsync(session.RunDirectory, stage, since);
                    try { await VerifyInventory(); }
                    catch { outcomes.Add(new { stage, operation = "deployment_check", outcome = "changed_or_unreadable" }); }
                    SaveSummary();
                }
                Console.WriteLine("请正常退出游戏；若崩溃后仍有进程，请用 Steam 停止。不要自行重启接着本组。");
            }

            async Task VerifyInventory()
            {
                if (Fingerprint(await ArmorDiagnosticEvidence.InventoryAsync(workspace.GameDataRoot)) != inventoryFingerprint)
                    throw new InvalidOperationException("实际部署内容已改变，停止对照。");
            }
            void VerifyRuntime(Process game, DateTime start, CliArmorRegistryDocument expected)
            {
                CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
                game.Refresh();
                if (game.HasExited || game.StartTime.ToUniversalTime() != start) throw new InvalidOperationException("游戏会话已结束或重启，请勿混用结果。");
                var s = ArmorDiagnosticEvidence.ReadStatus(registry);
                if (s.GetValueOrDefault("pid") != game.Id.ToString() || s.GetValueOrDefault("revision") != expected.Revision.ToString()
                    || s.GetValueOrDefault("state") != "ready"
                    || s.GetValueOrDefault("active_mappings") != (expected.Enabled ? expected.Mappings.Count : 0).ToString()
                    || File.GetLastWriteTimeUtc(Path.Combine(registry, "runtime.status")) < start)
                    throw new InvalidOperationException("本进程未确认加载预期 Lua 协议。请确认前置与托管运行时已部署；本工具不会安装它们。");
                session.EnsureCurrent(stage == "original" ? session.Journal.OriginalHash : session.Journal.DisabledHash);
            }
            void RecordState(Process game)
            {
                var s = ArmorDiagnosticEvidence.SafeStatus(ArmorDiagnosticEvidence.ReadStatus(registry));
                // SafeStatus returns only typed/allowlisted values. Append as JSON, not raw runtime log.
                ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "runtime_state", JsonSerializer.Serialize(s), game.Id);
            }
        }
        finally
        {
            if (session is not null)
            {
                Console.WriteLine("请完全退出游戏及管理器，然后恢复。原始协议备份不会丢失。");
                if (Choose("退出后输入 R 恢复；如暂时不能退出输入 Q，下次运行入口优先恢复", "r", "q") == "r")
                {
                    try
                    {
                        session.Restore();
                        ArmorDiagnosticEvidence.Save(session.RunDirectory, "recovery.json",
                            new { restored = true, utc = DateTimeOffset.UtcNow, byteExact = true });
                        Console.WriteLine("已恢复原始协议，源 JSON 和所有模型包未改变。");
                    }
                    catch (Exception e) { Console.WriteLine("未恢复：" + e.Message); }
                }
                SaveSummary();
                PrintExport(session);
            }
        }
        return 0;

        void SaveSummary()
        {
            if (session is null) return;
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "summary.json", new
            {
                schema = 1, session.Journal.Id, session.Journal.StartedUtc, selectedTarget,
                restored = session.Journal.Completed, outcomes,
                originalProtocolSha256 = session.Journal.OriginalHash,
                scope = "original configuration versus all mappings disabled; patches unchanged",
                localPlayerIdentityVerified = false, nativeProbeEnabled = false
            });
        }
    }

    private static string Fingerprint(object value) => ArmorDiagnosticSession.Hash(JsonSerializer.SerializeToUtf8Bytes(value));
    private static string FindManager()
    {
        var root = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(root, "settings.json"))) return root;
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(root))?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "settings.json"))) return parent;
        throw new CliUserException("ManagerNotFound", "请将诊断入口放在已配置好的管理器目录内运行。");
    }
    private static void EnsureStopped()
    {
        CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
        var game = CliDeploymentProcessGuard.Inspect();
        if (!game.IsReliable || game.IsGameRunning) throw new InvalidOperationException("请先完全退出游戏及管理器，不能在运行中切换或恢复协议。");
    }
    private static Process GetOnlyGame()
    {
        var games = Process.GetProcessesByName("helldivers2");
        if (games.Length == 1) return games[0];
        foreach (var game in games) game.Dispose();
        throw new InvalidOperationException("必须有且只有一个游戏进程。");
    }
    private static string Choose(string prompt, params string[] choices)
    {
        while (true)
        {
            Console.WriteLine(prompt);
            var value = Console.ReadLine();
            if (value is null) throw new OperationCanceledException();
            value = value.Trim().ToLowerInvariant();
            if (choices.Contains(value)) return value;
            Console.WriteLine("输入未识别，请按提示选择；无需输入路径或说明文字。");
        }
    }
    private static int ReadIndex(int count)
    {
        while (true)
        {
            Console.Write("输入编号：");
            var line = Console.ReadLine() ?? throw new OperationCanceledException();
            if (int.TryParse(line, out var index) && index >= 1 && index <= count) return index - 1;
        }
    }
    private static void PrintExport(ArmorDiagnosticSession session)
    {
        Console.WriteLine("报告仅包含前述脱敏诊断字段；可在发送前打开 ZIP 检查。");
        Console.WriteLine("发送此 ZIP，不要发送本机恢复目录：\n" + ArmorDiagnosticEvidence.Export(session.RunDirectory));
    }
}
