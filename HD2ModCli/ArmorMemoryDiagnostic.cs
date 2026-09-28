using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>One-launch memory comparison; leaves the registry and deployed assets untouched.</summary>
public static class ArmorMemoryDiagnostic
{
    public static async Task<int> RunAsync(CliGlobalOptions options)
    {
        if (!OperatingSystem.IsWindows() || options.Json || Console.IsInputRedirected)
            throw new CliUserException("InteractiveRequired", "需要 Windows 交互窗口。");
        var manager = ResolveManager(options.ManagerRoot);
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try
            {
                var launch = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas"
                };
                foreach (var argument in new[] { "diagnostics", "armor", "memory", "--manager-root", manager })
                    launch.ArgumentList.Add(argument);
                if (options.RegistryRoot is { } root)
                {
                    launch.ArgumentList.Add("--registry-root");
                    launch.ArgumentList.Add(root);
                }
                using var elevated = Process.Start(launch)
                    ?? throw new InvalidOperationException("管理员进程未启动。");
                await elevated.WaitForExitAsync();
                return elevated.ExitCode;
            }
            catch (Win32Exception)
            {
                throw new CliUserException("AdministratorRequired", "未获得管理员权限，未执行清理。");
            }
        }
        var workspace = CliWorkspace.Open(options with { ManagerRoot = manager });
        if (workspace.LibraryRoot is null || workspace.GameDataRoot is null)
            throw new CliUserException("SetupRequired", "请先在管理器中配置并应用 Mod 库和游戏目录。");
        var validation = new GameDataTargetGuard().Validate(workspace.GameDataRoot);
        if (!validation.IsValid) throw new CliUserException("InvalidGameData", "游戏目录校验失败。");
        EnsureStopped();

        var registry = RuntimeArmorRegistryService.ResolveRegistryRoot(options.RegistryRoot);
        var protocol = Path.Combine(registry, RuntimeArmorRegistryService.ProtocolFileName);
        var original = ArmorDiagnosticSession.ReadProtocol(protocol);
        var document = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        if (!document.Enabled || document.Mappings.Count == 0)
            throw new CliUserException("NoMapping", "请先在管理器中应用至少一条启用的护甲映射。");
        var directory = Path.Combine(manager, ArmorDiagnosticSession.RelativeDirectory);
        Directory.CreateDirectory(directory);
        using var toolLease = new FileStream(Path.Combine(directory, ".wizard.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var run = Path.Combine(directory, "memory-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
            + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);

        Process? game = null;
        int? pid = null;
        DateTime? gameStart = null;
        var reason = "session_timeout";
        var cleaned = false;
        var start = DateTimeOffset.UtcNow;
        var expectedExe = Path.Combine(Path.GetDirectoryName(workspace.GameDataRoot)!,
            "bin", "helldivers2.exe");
        using var stop = new CancellationTokenSource();
        Task? sampler = null;
        try
        {
            ArmorDiagnosticEvidence.Save(run, "environment.json", new
            {
                schema = 1, cliVersion = typeof(ArmorMemoryDiagnostic).Assembly.GetName().Version?.ToString(),
                osVersion = Environment.OSVersion.Version.ToString(),
                gameVersion = FileVersionInfo.GetVersionInfo(expectedExe).FileVersion,
                originalProtocolSha256 = ArmorDiagnosticSession.Hash(original),
                document.Revision, document.Mappings.Count,
                protocolModified = false, gameMemoryRead = false
            });
            ArmorDiagnosticEvidence.Save(run, "protocol-summary.json",
                ArmorDiagnosticEvidence.ProtocolSummary(document));
            sampler = ArmorMemoryMetrics.SampleAsync(run, stop.Token, () => game, () => pid);
            using var metrics = new ArmorMemoryMetrics();
            using var keys = new ArmorDiagnosticHotkeys(includeCrash: true);
            Console.WriteLine("正在通过 Steam 启动游戏。进入舰船后按 F8（只清备用内存）或 F10（激进清理）。");
            Console.WriteLine("每次启动只能清理一次。按键后尽快打开军械库，观察是否崩溃；F9 结束采样。");
            Console.WriteLine("激进清理可能短暂卡顿；若游戏崩溃而进程残留，请用 Steam 停止，再按 F9。");
            ArmorDiagnosticEvidence.Mark(run, "memory", "launch_requested", "pending");
            Process.Start(new ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true })
                ?.Dispose();
            var launchDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            var readyAnnounced = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (game is null)
                {
                    var candidates = Process.GetProcessesByName("helldivers2");
                    try
                    {
                        if (candidates.Length > 1) throw new InvalidOperationException("多个游戏进程，无法关联。");
                        if (candidates.Length == 1)
                        {
                            game = Process.GetProcessById(candidates[0].Id);
                            pid = game.Id;
                            gameStart = game.StartTime.ToUniversalTime();
                            ArmorDiagnosticEvidence.Mark(run, "memory", "game_started", "observed", pid);
                        }
                        else if (DateTimeOffset.UtcNow >= launchDeadline)
                        {
                            reason = "launch_timeout";
                            break;
                        }
                    }
                    finally { foreach (var candidate in candidates) candidate.Dispose(); }
                }
                else
                {
                    game.Refresh();
                    if (game.HasExited)
                    {
                        reason = "game_exited";
                        break;
                    }
                    if (game.StartTime.ToUniversalTime() != gameStart)
                        throw new InvalidOperationException("游戏进程身份变化。");
                    IReadOnlyDictionary<string, string> status;
                    try { status = ArmorDiagnosticEvidence.ReadStatus(registry); }
                    catch (IOException) { status = new Dictionary<string, string>(); }
                    var ready = ArmorDiagnosticQuick.IsReady(status, game.Id, document.Revision)
                        && File.GetLastWriteTimeUtc(Path.Combine(registry, "runtime.status")) >= gameStart;
                    if (ready && !readyAnnounced)
                    {
                        readyAnnounced = true;
                        Console.WriteLine("Lua 已就绪。保持游戏在前台，进入舰船后选择 F8 或 F10，然后尽快打开军械库。");
                    }
                    var action = keys.Poll();
                    if (action == ArmorDiagnosticHotkeys.HotkeyAction.Finish)
                    {
                        reason = "user_finished";
                        break;
                    }
                    if (action is ArmorDiagnosticHotkeys.HotkeyAction.Marker or ArmorDiagnosticHotkeys.HotkeyAction.Crash)
                    {
                        var aggressive = action == ArmorDiagnosticHotkeys.HotkeyAction.Crash;
                        if (cleaned)
                        {
                            Console.WriteLine("本次已清理过。对照另一模式请重启工具和游戏。");
                        }
                        else if (!ArmorDiagnosticQuick.AcceptMarker(
                            ArmorDiagnosticHotkeys.ForegroundPid(), game.Id, ready))
                        {
                            Console.WriteLine("未清理：请等待 Lua 就绪，让游戏保持前台并进入舰船。");
                        }
                        else
                        {
                            cleaned = true;
                            var mode = aggressive ? "aggressive" : "standby";
                            ArmorDiagnosticEvidence.Mark(run, "memory", "clean_" + mode, "started", pid);
                            var before = metrics.Capture(game, pid);
                            var result = ArmorMemoryMetrics.Clean(aggressive);
                            Thread.Sleep(1000); // Keep the registered hotkeys on this thread.
                            var after = metrics.Capture(game, pid);
                            ArmorDiagnosticEvidence.Save(run, "memory-clean.json",
                                new { schema = 1, mode, before, result, after });
                            ArmorDiagnosticEvidence.Mark(run, "memory", "clean_" + mode, "finished", pid);
                            Console.WriteLine("清理请求已记录；现在尽快打开军械库。完成或崩溃后按 F9。");
                        }
                    }
                }
                Thread.Sleep(50); // Thread-level hotkeys are pumped on this thread.
            }
        }
        catch (Exception e)
        {
            reason = "capture_error";
            Console.WriteLine("采样中止：" + e.Message);
            ArmorDiagnosticEvidence.Mark(run, "memory", "capture_error", "stopped", pid);
        }
        finally
        {
            stop.Cancel();
            if (sampler is not null) try { await sampler; } catch { }
            if (game is not null && gameStart is { } started && reason == "user_finished")
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    game.Refresh();
                    if (game.HasExited) break;
                    await ArmorDiagnosticEvidence.CollectEventsAsync(run, "memory", start);
                    var outcome = ArmorDiagnosticCrashCleanup.TryCleanup(game, started, expectedExe,
                        File.ReadAllText(Path.Combine(run, "events-memory.json")));
                    ArmorDiagnosticEvidence.Mark(run, "memory", "crash_cleanup", outcome, pid);
                    if (outcome != "no_matching_crash_event") break;
                    await Task.Delay(3000);
                }
            }
            if (CliDeploymentProcessGuard.Inspect().IsGameRunning)
                Console.WriteLine("游戏仍在运行；若已崩溃而残留，请在 Steam 中停止。");
            await ArmorDiagnosticEvidence.CollectEventsAsync(run, "memory", start);
            ArmorDiagnosticEvidence.Save(run, "summary.json",
                new { schema = 1, mode = "memory", reason, cleaned, pid, gameStartUtc = gameStart,
                    armoryOpenAutomaticallyDetected = false, originalProtocolSha256 = ArmorDiagnosticSession.Hash(original),
                    protocolModified = false, gameMemoryRead = false });
            game?.Dispose();
            Console.WriteLine("请仅发送此报告 ZIP：");
            Console.WriteLine(ArmorDiagnosticEvidence.Export(run));
            Console.WriteLine("按回车关闭此窗口。");
            Console.ReadLine();
        }
        return 0;
    }

    private static string ResolveManager(string? provided)
    {
        if (provided is not null) return Path.GetFullPath(provided);
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "settings.json"))) return parent;
        throw new CliUserException("ManagerNotFound", "请把整个诊断文件夹放进已配置的管理器目录。");
    }

    private static void EnsureStopped()
    {
        CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
        var state = CliDeploymentProcessGuard.Inspect();
        if (!state.IsReliable || state.IsGameRunning)
            throw new CliUserException("GameStillRunning", "请先通过 Steam 完全退出游戏，并关闭管理器。");
    }
}
