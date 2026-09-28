using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>Cold-start and live-protocol timing controls for armory crashes.</summary>
public static class ArmorDiagnosticMatrix
{
    private const string Stage = "matrix";

    public static async Task<int> RunAsync(CliGlobalOptions options, bool composition = false)
    {
        var reportStage = composition ? "composition" : Stage;
        if (!OperatingSystem.IsWindows() || options.Json || Console.IsInputRedirected)
            throw new CliUserException("InteractiveRequired", "需要 Windows 交互窗口。");
        var manager = ResolveManager(options.ManagerRoot);
        var workspace = CliWorkspace.Open(options with { ManagerRoot = manager });
        if (workspace.LibraryRoot is null || workspace.GameDataRoot is null)
            throw new CliUserException("SetupRequired", "请先配置并部署 Mod 库和游戏目录。");
        var registry = RuntimeArmorRegistryService.ResolveRegistryRoot(options.RegistryRoot);
        var protocol = Path.Combine(registry, RuntimeArmorRegistryService.ProtocolFileName);
        var directory = Path.Combine(manager, ArmorDiagnosticSession.RelativeDirectory);
        Directory.CreateDirectory(directory);
        using var toolLease = new FileStream(Path.Combine(directory, ".wizard.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(Path.Combine(directory, "active.json")))
        {
            var old = ArmorDiagnosticSession.Open(directory, protocol, EnsureStopped);
            if (!old.Journal.Completed)
            {
                Console.WriteLine("发现未完成的诊断。请通过 Steam 完全结束游戏；工具检测到结束后自动恢复原协议。");
                WaitStopped(composition);
                old.Restore();
                ArmorDiagnosticEvidence.Save(old.RunDirectory, "recovery.json",
                    new { restored = true, utc = DateTimeOffset.UtcNow, byteExact = true });
                Console.WriteLine("已恢复；重新运行本工具开始新一轮。");
                return 0;
            }
        }
        EnsureStopped();
        var validation = new GameDataTargetGuard().Validate(workspace.GameDataRoot);
        if (!validation.IsValid) throw new CliUserException("InvalidGameData", "游戏目录校验失败。");
        var original = ArmorDiagnosticSession.ReadProtocol(protocol);
        var enabled = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        if (!enabled.Enabled || enabled.Mappings.Count == 0)
            throw new CliUserException("NoMapping", "请先在管理器中应用一份已启用的映射配置，然后关闭管理器。");
        if (composition && enabled.Revision > int.MaxValue - 2)
            throw new CliUserException("RevisionExhausted", "协议版本空间不足，无法进行两组对照。");
        var inventory = await ArmorDiagnosticEvidence.InventoryAsync(workspace.GameDataRoot);
        var inventoryHash = HashInventory(inventory);
        ArmorDiagnosticSession? session = null;
        Process? game = null;
        CancellationTokenSource? samplerStop = null;
        Task? sampler = null;
        var outcomes = new List<object>();
        var captureStart = DateTimeOffset.UtcNow;
        var result = "incomplete";
        try
        {
            using (var protocolLease = new FileStream(Path.Combine(registry, ".manager.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (ArmorDiagnosticSession.Hash(ArmorDiagnosticSession.ReadProtocol(protocol))
                    != ArmorDiagnosticSession.Hash(original))
                    throw new InvalidOperationException("准备期间协议已变更。");
                session = composition
                    ? ArmorDiagnosticSession.CreateComposition(directory, protocol, EnsureStopped)
                    : ArmorDiagnosticSession.Create(directory, protocol, EnsureStopped);
            }
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "protocol-summary.json",
                ArmorDiagnosticEvidence.ProtocolSummary(enabled));
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "inventory.json", inventory);
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "environment.json", new
            {
                schema = 1, mode = composition ? "composition" : Stage,
                gameVersion = FileVersionInfo.GetVersionInfo(
                    Path.Combine(Path.GetDirectoryName(workspace.GameDataRoot)!, "bin", "helldivers2.exe")).FileVersion,
                loaderNotRedistributed = true
            });
            samplerStop = new CancellationTokenSource();
            sampler = ArmorDiagnosticEvidence.SampleStatusAsync(session.RunDirectory, reportStage, registry,
                samplerStop.Token, 200, 4500);

            if (composition) Console.WriteLine($"""
                护甲槽位结构对照：当前 {enabled.Mappings.Count} 条映射。
                工具会自动启动两次游戏，分别测试“完整槽位”和“只替换原有槽位”。
                两组使用完全相同的模型包和映射，只改变 Lua 组合方式。
                这仅用于查找崩溃原因，不会修复问题。
                不要打开管理器，也不要更改映射、装备、身形、头盔或 Mod。
                每组在军械库外按 F8，打开军械库再直接关闭。
                正常关闭后按 F9；崩溃或卡死按 F10。
                每组结束请正常退出游戏；崩溃后在 Steam 点击“停止”。
                工具确认游戏彻底结束后自动进入下一组，无需回窗口按回车。
                两组做完会自动恢复你的原始设置并生成一个可发送的 ZIP。
                """);
            else Console.WriteLine($"""
                军械库映射对照：当前 {enabled.Mappings.Count} 条映射。
                这不是修复工具，只做六组对照测试。工具只临时开关运行时协议，
                不改模型包、源定义、部署文件或管理器设置。
                测试期间不要打开管理器，不要换身形、头盔、装备或 Mod。
                每一组都必须按屏幕顺序完成；工具不会把未完成的组算作成功。
                游戏崩溃后必须用 Steam 点击“停止”，确认游戏不再运行后才能继续。
                任何时候输入 Q，或等待 F8 时按 F9，可以结束整轮并恢复原协议。
                """);
            var disabled = CliArmorRegistryProtocol.ParseProtocol(
                Encoding.UTF8.GetString(Convert.FromBase64String(session.Journal.DisabledBase64)));
            var comparison = composition
                ? CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(
                    Convert.FromBase64String(session.Journal.ComparisonBase64!)))
                : null;
            var stages = composition
                ? new[]
                {
                    new TestStage("full", true, Change.None, false),
                    new TestStage("existing_only", false, Change.None, false)
                }
                : new[]
            {
                new TestStage("cold_disabled", false, Change.None, false),
                new TestStage("cold_enabled", true, Change.None, false),
                new TestStage("enable_closed", false, Change.Enable, false),
                new TestStage("disable_closed", true, Change.Disable, false),
                new TestStage("enable_open", false, Change.Enable, true),
                new TestStage("disable_open", true, Change.Disable, true)
            };
            for (var index = 0; index < stages.Length; index++)
            {
                var test = stages[index];
                Console.WriteLine();
                Console.WriteLine($"========== 第 {index + 1}/{stages.Length} 组 ==========");
                Console.WriteLine(composition
                    ? test.Name == "full"
                        ? "完整槽位：使用来源的全部可见部件，可能改变目标的槽位数量。"
                        : "原有槽位：只替换目标已有槽位的 Unit，多出的来源部件会跳过。"
                    : StageDescription(test));
                if (!composition) Console.WriteLine("只按屏幕提示操作；做完当前动作后再按回车。");
                if (composition) session.SetCompositionStopped(test.Name == "existing_only");
                else session.SetStopped(test.ColdEnabled);
                ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "cold_protocol", "written");
                var compositionLogOffset = composition ? ArmorDiagnosticEvidence.CompositionLogOffset(registry) : 0;
                var launchRequested = DateTimeOffset.UtcNow;
                Console.WriteLine(composition
                    ? "正在通过 Steam 启动游戏。进入舰船并站在军械库外，等待游戏加载完成。"
                    : "正在通过 Steam 启动游戏。进入舰船后回到此窗口按回车；若已崩溃输入 C。");
                try
                {
                    using var compositionKeys = composition ? new ArmorDiagnosticHotkeys(includeCrash: true) : null;
                    Process.Start(new ProcessStartInfo("steam://rungameid/553850")
                        { UseShellExecute = true })?.Dispose();
                    game = WaitForGame(launchRequested);
                    var started = game.StartTime.ToUniversalTime();
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "game_started", "observed", game.Id);
                    if (!composition)
                        ReadPrompt("进入舰船，确认军械库关闭，然后按回车继续；崩溃 C，卡死 H，退出 Q。",
                            session, test.Name + "_ship", game.Id);
                    var current = composition
                        ? test.Name == "full" ? disabled : comparison!
                        : test.ColdEnabled ? enabled : disabled;
                    WaitReady(game, started, registry, current,
                        composition
                            ? test.Name == "full" ? session.Journal.DisabledHash : session.Journal.ComparisonHash!
                            : test.ColdEnabled ? session.Journal.OriginalHash : session.Journal.DisabledHash,
                        launchRequested, session);
                    if (composition)
                    {
                        var outcome = CompositionCycle(test.Name, game, started, registry, current,
                            session, compositionKeys!);
                        outcomes.Add(new { stage = test.Name, cycle = 1, outcome });
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "cycle_1_result", outcome, game.Id);
                        if (outcome != "success")
                            throw new StageFaultException("本组未完成：" + outcome);
                        outcomes.Add(new { stage = test.Name, outcome = "success" });
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "stage_complete", "success", game.Id);
                    }
                    else
                    {
                        if (test.Change != Change.None)
                        {
                            if (test.OpenDuringChange)
                                ReadPrompt("现在打开军械库并保持打开，不要换装；完成后按回车。崩溃 C，卡死 H，退出 Q。",
                                    session, test.Name + "_opened", game.Id);
                            Console.WriteLine(test.OpenDuringChange
                                ? "请把游戏窗口置于前台，在军械库保持打开时按一次 F8。"
                                : "请把游戏窗口置于前台，在军械库保持关闭时按一次 F8。");
                            WaitHotkey(game, started);
                            var requested = DateTimeOffset.UtcNow;
                            ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "switch_requested",
                                test.Change == Change.Enable ? "enable" : "disable", game.Id);
                            if (test.Change == Change.Enable) session.EnableLive();
                            else session.DisableLive();
                            current = test.Change == Change.Enable ? enabled : disabled;
                            WaitReady(game, started, registry, current,
                                test.Change == Change.Enable ? session.Journal.OriginalHash : session.Journal.DisabledHash,
                                requested, session);
                            if (test.OpenDuringChange)
                                ReadPrompt("F8 已完成。现在关闭军械库，完成后按回车；崩溃 C，卡死 H，退出 Q。",
                                    session, test.Name + "_closed", game.Id);
                        }
                        Cycles(test.Name, game, started, registry, current, session, outcomes, 3);
                        ReadPrompt("本组的 3 次军械库测试已完成。按回车进入下一组；崩溃 C，卡死 H，退出 Q。",
                            session, test.Name + "_confirmed", game.Id);
                        outcomes.Add(new { stage = test.Name, outcome = "success" });
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "stage_complete", "success", game.Id);
                    }
                }
                catch (Exception e) when (e is StageFaultException
                    or InvalidOperationException or TimeoutException)
                {
                    outcomes.Add(new { stage = test.Name, outcome = "failed", reason = e.GetType().Name });
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, test.Name, "stage_complete", "failed",
                        game?.Id);
                    Console.WriteLine($"本组发生故障：{e.Message}。本组不再重复；请按提示用 Steam 完全结束游戏，然后继续下一组。");
                }
                finally
                {
                    Console.WriteLine("请正常退出本组游戏；若有残留，请在 Steam 点击“停止”。工具会自动检测并继续。");
                    WaitStopped(composition);
                    if (composition)
                        ArmorDiagnosticEvidence.CaptureCompositionLog(
                            session.RunDirectory, test.Name, registry, compositionLogOffset);
                    game?.Dispose();
                    game = null;
                }
            }
            result = "all_stages_attempted";
        }
        catch (Exception e)
        {
            result = "stopped";
            Console.WriteLine("实验停止：" + e.Message);
            if (session is not null)
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, reportStage, "stop", "incomplete",
                    game?.Id);
        }
        finally
        {
            if (samplerStop is not null) samplerStop.Cancel();
            if (sampler is not null) try { await sampler; } catch { }
            if (session is not null)
            {
                Console.WriteLine("请完全退出游戏。若崩溃后仍有残留进程，请在 Steam 中点击停止；工具不会跳过进程检查。");
                WaitStopped(composition);
                var restored = false;
                try
                {
                    session.Restore();
                    restored = true;
                    ArmorDiagnosticEvidence.Save(session.RunDirectory, "recovery.json",
                        new { restored, utc = DateTimeOffset.UtcNow, byteExact = true });
                }
                catch (Exception e) { Console.WriteLine("原协议尚未恢复：" + e.Message + "；请保留本机恢复目录并重启工具。"); }
                var deploymentUnchanged = false;
                try { deploymentUnchanged = HashInventory(
                    await ArmorDiagnosticEvidence.InventoryAsync(workspace.GameDataRoot)) == inventoryHash; }
                catch { }
                await ArmorDiagnosticEvidence.CollectEventsAsync(session.RunDirectory, reportStage, captureStart);
                ArmorDiagnosticEvidence.Save(session.RunDirectory, "summary.json", new
                {
                    schema = 1, mode = composition ? "composition" : Stage, session.Journal.Id, result, restored, deploymentUnchanged,
                    mappingCount = enabled.Mappings.Count, outcomes,
                    originalProtocolSha256 = session.Journal.OriginalHash,
                    scope = composition ? "full versus existing-only composition; no source, patch, or deployment changes"
                        : "runtime protocol timing; no source, patch, or deployment changes"
                });
                Console.WriteLine("请发送此报告 ZIP，不要发送本机私有恢复目录：");
                Console.WriteLine(ArmorDiagnosticEvidence.Export(session.RunDirectory));
            }
            game?.Dispose();
            samplerStop?.Dispose();
        }
        return result == "all_stages_attempted" ? 0 : 1;
    }

    private enum Change { None, Enable, Disable }
    private sealed record TestStage(string Name, bool ColdEnabled, Change Change, bool OpenDuringChange);
    private sealed class StageFaultException(string message) : Exception(message);

    private static string StageDescription(TestStage stage)
        => stage.Name switch
        {
            "cold_disabled" => "从启动起关闭映射，只检查军械库是否正常。",
            "cold_enabled" => "从启动起启用映射，检查军械库。",
            "enable_closed" => "先关闭映射启动，军械库关闭时按 F8 启用，再检查。",
            "disable_closed" => "先启用映射启动，军械库关闭时按 F8 禁用，再检查。",
            "enable_open" => "先关闭映射启动，打开军械库后按 F8 启用，再关闭并重开。",
            _ => "先启用映射启动，打开军械库后按 F8 禁用，再关闭并重开。"
        };

    private static string CompositionCycle(string stage, Process game, DateTime started, string registry,
        CliArmorRegistryDocument expected, ArmorDiagnosticSession session, ArmorDiagnosticHotkeys keys)
    {
        Console.WriteLine("进入舰船后，让游戏保持前台。在打开军械库前按 F8；打开后直接关闭，不要换装。");
        Console.WriteLine("成功关闭后按 F9；崩溃或卡死按 F10。无需切回工具窗口。");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(6);
        var marked = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            game.Refresh();
            if (game.HasExited) return marked ? "game_exited_after_marker" : "game_exited_before_marker";
            switch (keys.Poll())
            {
                case ArmorDiagnosticHotkeys.HotkeyAction.Marker when !marked:
                    if (ArmorDiagnosticHotkeys.ForegroundPid() != (uint)game.Id)
                    {
                        Console.WriteLine("F8 未记录：请保持游戏窗口在前台，再按一次 F8。");
                        break;
                    }
                    CheckReady(game, started, registry, expected);
                    marked = true;
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "hotkey_F8", "start", game.Id);
                    Console.WriteLine("F8 已记录。现在打开军械库并直接关闭；成功按 F9，崩溃或卡死按 F10。");
                    break;
                case ArmorDiagnosticHotkeys.HotkeyAction.Finish when marked:
                    if (ArmorDiagnosticHotkeys.ForegroundPid() != (uint)game.Id)
                    {
                        Console.WriteLine("F9 未记录：仅在游戏窗口正常显示且位于前台时按 F9；崩溃请按 F10。");
                        break;
                    }
                    CheckReady(game, started, registry, expected);
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "hotkey_F9", "success", game.Id);
                    return "success";
                case ArmorDiagnosticHotkeys.HotkeyAction.Crash:
                    ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, "hotkey_F10", "reported_crash_or_hang", game.Id);
                    return marked ? "reported_crash_or_hang" : "reported_before_marker";
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException(marked ? "等待 F9/F10 超时，不能判定结果。" : "等待 F8 超时，未开始本组测试。");
    }

    private static void Cycles(string stage, Process game, DateTime started, string registry,
        CliArmorRegistryDocument expected, ArmorDiagnosticSession session, List<object> outcomes, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            CheckReady(game, started, registry, expected);
            Console.WriteLine($"第 {i}/{count} 次：打开军械库，不换装，直接关闭，等待 5 秒。");
            Console.WriteLine("完成后回到这里：正常回车；打开时崩溃 O；关闭时崩溃 C；其他时刻崩溃 B；卡死 H；停止整轮 Q。");
            ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, $"cycle_{i}_prompt", "pending", game.Id);
            var outcome = ReadOutcome();
            Record(session, outcomes, stage, i, outcome, game.Id);
            if (outcome is "crash_open" or "crash_close" or "crash_other" or "hang")
                throw new StageFaultException($"第 {i} 次军械库操作 {outcome}");
            if (outcome != "success") throw new OperationCanceledException("用户主动结束整轮。");
            CheckGame(game, started);
        }
    }

    private static void Record(ArmorDiagnosticSession session, List<object> outcomes,
        string stage, int cycle, string outcome, int pid)
    {
        outcomes.Add(new { stage, cycle, outcome });
        ArmorDiagnosticEvidence.Mark(session.RunDirectory, stage, $"cycle_{cycle}_result", outcome, pid);
    }

    private static void WaitReady(Process game, DateTime started, string registry,
        CliArmorRegistryDocument expected, string expectedHash, DateTimeOffset requested,
        ArmorDiagnosticSession session)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var active = expected.Enabled ? expected.Mappings.Count : 0;
        Console.WriteLine($"等待 Lua 实际发布 revision={expected.Revision}, active_mappings={active}（最多 60 秒）。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            CheckGame(game, started);
            session.EnsureCurrent(expectedHash);
            var status = ArmorDiagnosticEvidence.ReadStatus(registry);
            var statusPath = Path.Combine(registry, "runtime.status");
            if (status.GetValueOrDefault("pid") == game.Id.ToString()
                && status.GetValueOrDefault("state") is "protocol_error" or "apply_error" or "runtime_error")
                throw new InvalidOperationException("Lua 报告协议或应用错误。");
            if (status.GetValueOrDefault("pid") == game.Id.ToString()
                && File.GetLastWriteTimeUtc(statusPath) >= requested.UtcDateTime
                && Ready(status, game.Id, expected.Revision, active))
            {
                ArmorDiagnosticEvidence.Mark(session.RunDirectory, Stage, "lua_ready",
                    JsonSerializer.Serialize(ArmorDiagnosticEvidence.SafeStatus(status)), game.Id);
                Console.WriteLine("Lua 已确认，才开始下一步操作。");
                return;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("Lua 未确认本次协议，停止实验。");
    }

    public static bool Ready(IReadOnlyDictionary<string, string> status, int pid, int revision, int active)
        => status.GetValueOrDefault("pid") == pid.ToString()
            && status.GetValueOrDefault("revision") == revision.ToString()
            && status.GetValueOrDefault("active_mappings") == active.ToString()
            && status.GetValueOrDefault("state") == "ready";

    private static void CheckReady(Process game, DateTime started, string registry, CliArmorRegistryDocument expected)
    {
        CheckGame(game, started);
        var status = ArmorDiagnosticEvidence.ReadStatus(registry);
        if (!Ready(status, game.Id, expected.Revision, expected.Enabled ? expected.Mappings.Count : 0))
            throw new InvalidOperationException("运行时状态已变化，停止实验。");
    }

    private static void WaitHotkey(Process game, DateTime started)
    {
        using var keys = new ArmorDiagnosticHotkeys();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            CheckGame(game, started);
            switch (keys.Poll())
            {
                case ArmorDiagnosticHotkeys.HotkeyAction.Marker:
                    if (ArmorDiagnosticHotkeys.ForegroundPid() == (uint)game.Id) return;
                    Console.WriteLine("F8 未接受：请将游戏窗口置于前台后重试。");
                    break;
                case ArmorDiagnosticHotkeys.HotkeyAction.Finish:
                    throw new OperationCanceledException("用户按 F9 结束实验。");
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("等待 F8 超时。");
    }

    private static Process WaitForGame(DateTimeOffset requested)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var games = Process.GetProcessesByName("helldivers2");
            if (games.Length > 1)
            {
                foreach (var item in games) item.Dispose();
                throw new InvalidOperationException("多个游戏进程，无法建立独立对照。");
            }
            if (games.Length == 1)
            {
                if (games[0].StartTime.ToUniversalTime() >= requested.UtcDateTime.AddSeconds(-2))
                    return games[0];
                games[0].Dispose();
                throw new InvalidOperationException("发现先于诊断启动的游戏进程。");
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException("Steam 启动游戏超时。");
    }

    private static void CheckGame(Process game, DateTime started)
    {
        CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
        game.Refresh();
        if (game.HasExited || game.StartTime.ToUniversalTime() != started)
            throw new InvalidOperationException("游戏会话已结束或发生更换。");
        var games = Process.GetProcessesByName("helldivers2");
        try
        {
            if (games.Length != 1 || games[0].Id != game.Id)
                throw new InvalidOperationException("游戏进程数或身份变化，停止实验。");
        }
        finally { foreach (var item in games) item.Dispose(); }
    }

    private static string ReadOutcome()
    {
        while (true)
        {
            var input = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            var outcome = input switch
            {
                "" => "success",
                "o" => "crash_open",
                "c" => "crash_close",
                "b" => "crash_other",
                "h" => "hang",
                "q" => "quit",
                _ => "invalid"
            };
            if (outcome != "invalid") return outcome;
            Console.WriteLine("输入未识别。正常直接回车；崩溃 O/C/B；卡死 H；结束整轮 Q。");
        }
    }

    private static void ReadPrompt(string prompt, ArmorDiagnosticSession session, string action, int pid)
    {
        ArmorDiagnosticEvidence.Mark(session.RunDirectory, Stage, action + "_prompt", "pending", pid);
        Console.WriteLine(prompt);
        string answer;
        while (true)
        {
            answer = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            if (answer is "" or "c" or "h" or "q") break;
            Console.WriteLine("输入未识别。正常直接回车；崩溃 C；卡死 H；结束整轮 Q。");
        }
        ArmorDiagnosticEvidence.Mark(session.RunDirectory, Stage, action + "_result",
            answer == "" ? "success" : answer, pid);
        if (answer is "c" or "h") throw new StageFaultException($"{action} 时报告 {answer}");
        if (answer == "q") throw new OperationCanceledException("用户主动结束整轮。");
    }

    private static void WaitStopped(bool automatic = false)
    {
        var announced = false;
        while (true)
        {
            try { EnsureStopped(); return; }
            catch (Exception e)
            {
                if (!automatic)
                {
                    Console.WriteLine(e.Message + " 请使用 Steam 完全结束残留进程，再按回车重新检查。");
                    Console.ReadLine();
                }
                else
                {
                    if (!announced)
                        Console.WriteLine("仍检测到游戏或管理器进程。请关闭管理器；若游戏已崩溃，请在 Steam 点击“停止”。工具会自动继续。");
                    announced = true;
                    Thread.Sleep(1000);
                }
            }
        }
    }

    private static void EnsureStopped()
    {
        CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
        var state = CliDeploymentProcessGuard.Inspect();
        if (!state.IsReliable || state.IsGameRunning)
            throw new InvalidOperationException("游戏或管理器仍在运行。");
    }

    private static string HashInventory(object value)
        => ArmorDiagnosticSession.Hash(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string ResolveManager(string? provided)
    {
        if (provided is not null) return Path.GetFullPath(provided);
        var root = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(root, "settings.json"))) return root;
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(root))?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "settings.json"))) return parent;
        throw new CliUserException("ManagerNotFound", "请把整个诊断附件放在已配置的管理器目录。");
    }
}
