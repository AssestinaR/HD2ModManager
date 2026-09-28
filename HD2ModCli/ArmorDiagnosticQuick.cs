using System.Diagnostics;
using System.Text;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>Single-launch, hotkey-marked crash capture; original mappings stay intact.</summary>
public static class ArmorDiagnosticQuick
{
    public static async Task<int> RunAsync(CliGlobalOptions options)
    {
        if (!OperatingSystem.IsWindows() || options.Json || Console.IsInputRedirected)
            throw new CliUserException("InteractiveRequired", "需要 Windows 交互窗口。");
        var manager = ResolveManager(options.ManagerRoot);
        var workspace = CliWorkspace.Open(options with { ManagerRoot = manager });
        if (workspace.LibraryRoot is null || workspace.GameDataRoot is null)
            throw new CliUserException("SetupRequired", "请先在管理器中设置并应用 Mod 库和游戏目录。");
        var registry = RuntimeArmorRegistryService.ResolveRegistryRoot(options.RegistryRoot);
        var protocol = Path.Combine(registry, RuntimeArmorRegistryService.ProtocolFileName);
        var directory = Path.Combine(manager, ArmorDiagnosticSession.RelativeDirectory);
        Directory.CreateDirectory(directory);
        using var toolLease = new FileStream(Path.Combine(directory, ".wizard.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(Path.Combine(directory, "active.json")))
        {
            var previous = ArmorDiagnosticSession.Open(directory, protocol, EnsureStopped);
            if (!previous.Journal.Completed)
            {
                Console.WriteLine("发现上次未恢复的诊断。请先彻底结束游戏，再自动恢复。");
                EnsureStopped();
                previous.Restore();
                ArmorDiagnosticEvidence.Save(previous.RunDirectory, "recovery.json",
                    new { restored = true, utc = DateTimeOffset.UtcNow, byteExact = true });
                Console.WriteLine("原协议已恢复。请重新双击启动本工具开始新采样。");
                return 0;
            }
        }
        EnsureStopped();
        var validation = new GameDataTargetGuard().Validate(workspace.GameDataRoot);
        if (!validation.IsValid)
            throw new CliUserException("InvalidGameData", "游戏目录校验失败。");
        var original = ArmorDiagnosticSession.ReadProtocol(protocol);
        var document = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        if (!document.Enabled || document.Mappings.Count == 0)
            throw new CliUserException("NoMapping", "没有已应用且启用的护甲映射。");

        // Gathering patch hashes can take time; do it before launching Steam.
        object inventory;
        await using (var libraryLease = await CoreServices.CreateLibraryWriteLeaseProvider().AcquireAsync(workspace.LibraryRoot))
        await using (var gameLease = await CoreServices.CreateGameDataDeploymentLeaseProvider().AcquireAsync(workspace.GameDataRoot))
            inventory = await ArmorDiagnosticEvidence.InventoryAsync(workspace.GameDataRoot);

        ArmorDiagnosticSession? session = null;
        Process? game = null;
        DateTime? gameStartForCleanup = null;
        using var samplerStop = new CancellationTokenSource();
        Task? sampler = null;
        var startCapture = DateTimeOffset.UtcNow;
        var markers = 0;
        var observedGame = false;
        var reason = "not_started";
        var version = FileVersionInfo.GetVersionInfo(
            Path.Combine(Path.GetDirectoryName(workspace.GameDataRoot)!, "bin", "helldivers2.exe")).FileVersion;
        try
        {
            EnsureStopped();
            using (var protocolLease = new FileStream(Path.Combine(registry, ".manager.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (ArmorDiagnosticSession.Hash(ArmorDiagnosticSession.ReadProtocol(protocol))
                    != ArmorDiagnosticSession.Hash(original))
                    throw new InvalidOperationException("准备期间协议已更改。");
                session = ArmorDiagnosticSession.Create(directory, protocol, EnsureStopped);
            }
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "protocol-summary.json",
                ArmorDiagnosticEvidence.ProtocolSummary(document));
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "inventory.json", inventory);
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "environment.json", new
            {
                schema = 2, cliVersion = typeof(ArmorDiagnosticQuick).Assembly.GetName().Version?.ToString(),
                osVersion = Environment.OSVersion.Version.ToString(), gameVersion = version,
                loaderVersionVerified = false, loaderNotRedistributed = true
            });
            ArmorDiagnosticEvidence.Save(session.RunDirectory, "capture-config.json", new
            {
                schema = 1, mode = "quick", marker = "F8", finish = "F9",
                sampleIntervalMs = 100, maxMinutes = 12, expectedRevision = document.Revision,
                protocolModified = false, patchModified = false
            }); // Private tool config, not shipped in the report.
            sampler = ArmorDiagnosticEvidence.SampleStatusAsync(session.RunDirectory, "quick", registry,
                samplerStop.Token, 100, 7200);
            using var keys = new ArmorDiagnosticHotkeys();
            Console.WriteLine("已保存恢复记录和诊断配置；保持当前映射和模型包不变。");
            Console.WriteLine("正在通过 Steam 启动游戏。进入舰船后，在即将打开或关闭军械库前按 F8，随即操作。");
            Console.WriteLine("可多次按 F8。崩溃/卡死后如有残留，请先通过 Steam 停止，再按 F9 完成采集。");
            Console.WriteLine("正常结束也按 F9。快捷键只记录时间，不会触发换装或注入游戏。");
            ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "launch_requested", "pending");
            Process.Start(new ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true })
                ?.Dispose();

            var launchDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
            DateTime? gameStart = null;
            var readyAnnounced = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (game is null)
                {
                    using var candidates = Process.GetProcessesByName("helldivers2").AsDisposableArray();
                    if (candidates.Items.Length > 1) throw new InvalidOperationException("多个游戏进程，无法关联。");
                    if (candidates.Items.Length == 1)
                    {
                        game = Process.GetProcessById(candidates.Items[0].Id);
                        gameStart = game.StartTime.ToUniversalTime();
                        gameStartForCleanup = gameStart;
                        observedGame = true;
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "game_started", "observed", game.Id);
                    }
                    else if (DateTimeOffset.UtcNow >= launchDeadline)
                    {
                        reason = "launch_timeout";
                        break;
                    }
                }
                else
                {
                    game.Refresh();
                    if (game.HasExited)
                    {
                        reason = "game_exited";
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "game_exited", "observed", game.Id);
                        break;
                    }
                    if (game.StartTime.ToUniversalTime() != gameStart)
                        throw new InvalidOperationException("进程身份变化，停止采样。");
                    IReadOnlyDictionary<string, string> status;
                    try { status = ArmorDiagnosticEvidence.ReadStatus(registry); }
                    catch (IOException) { status = new Dictionary<string, string>(); }
                    var ready = IsReady(status, game.Id, document.Revision)
                        && File.GetLastWriteTimeUtc(Path.Combine(registry, "runtime.status")) >= gameStart;
                    if (ready && !readyAnnounced)
                    {
                        readyAnnounced = true;
                        Console.WriteLine("Lua 已确认本次协议；现在可按 F8 标记并复现。");
                    }
                    switch (keys.Poll())
                    {
                        case ArmorDiagnosticHotkeys.HotkeyAction.Marker:
                        {
                            var foreground = ArmorDiagnosticHotkeys.ForegroundPid();
                            if (!AcceptMarker(foreground, game.Id, ready))
                            {
                                Console.WriteLine("F8 未记录：请让游戏保持前台，并等待 Lua 就绪。");
                                break;
                            }
                            markers++;
                            ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "hotkey_F8", "accepted", game.Id);
                            Console.WriteLine($"标记 {markers} 已记录；现在操作军械库。");
                            break;
                        }
                        case ArmorDiagnosticHotkeys.HotkeyAction.Finish:
                            reason = "user_finished";
                            break;
                    }
                    if (reason == "user_finished") break;
                }
                Thread.Sleep(50); // Keep the thread-level hotkey message queue on this thread.
            }
            if (reason == "not_started") reason = "session_timeout";
        }
        catch (Exception e)
        {
            reason = "capture_error";
            Console.WriteLine("采集停止：" + e.Message);
            if (session is not null)
                ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "capture_error", "stopped");
        }
        finally
        {
            samplerStop.Cancel();
            if (sampler is not null) try { await sampler; } catch { }
            if (session is not null)
            {
                var expectedExe = Path.Combine(Path.GetDirectoryName(workspace.GameDataRoot)!, "bin", "helldivers2.exe");
                var eventsPath = Path.Combine(session.RunDirectory, "events-quick.json");
                if (game is not null && observedGame && gameStartForCleanup is { } started
                    && reason == "user_finished")
                {
                    // WER typically arrives after the crash, while a damaged
                    // game process can still linger. Never infer a crash from
                    // window disappearance or the user's F9 press alone.
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        game.Refresh();
                        if (game.HasExited) break;
                        await Task.Delay(3000);
                        await ArmorDiagnosticEvidence.CollectEventsAsync(session.RunDirectory, "quick", startCapture);
                        var result = ArmorDiagnosticCrashCleanup.TryCleanup(game, started, expectedExe,
                            File.ReadAllText(eventsPath));
                        ArmorDiagnosticEvidence.Mark(session.RunDirectory, "quick", "crash_cleanup", result, game.Id);
                        if (result != "no_matching_crash_event") break;
                    }
                }
                if (reason == "user_finished" && CliDeploymentProcessGuard.Inspect().IsGameRunning)
                    Console.WriteLine("未能确认可自动清理的崩溃；请在 Steam 中停止游戏以恢复原配置。");
                var restoreDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
                while (CliDeploymentProcessGuard.Inspect().IsGameRunning && DateTimeOffset.UtcNow < restoreDeadline)
                    await Task.Delay(1000);
                // WER can be committed only after Steam finishes clearing a
                // crashed, still-live process; collect after the stop window.
                if (observedGame) await Task.Delay(3000);
                await ArmorDiagnosticEvidence.CollectEventsAsync(session.RunDirectory, "quick", startCapture);
                var restored = false;
                try
                {
                    session.Restore();
                    restored = true;
                    ArmorDiagnosticEvidence.Save(session.RunDirectory, "recovery.json",
                        new { restored, utc = DateTimeOffset.UtcNow, byteExact = true });
                }
                catch (Exception e) { Console.WriteLine("暂未恢复：" + e.Message + "；结束游戏后重新启动本工具以恢复。"); }
                ArmorDiagnosticEvidence.Save(session.RunDirectory, "summary.json", new
                {
                    schema = 2, session.Journal.Id, session.Journal.StartedUtc,
                    mode = "quick", reason, markers, restored, observedGame,
                    originalProtocolSha256 = session.Journal.OriginalHash,
                    scope = "original mappings and patches unchanged; F8 global marker in game foreground",
                    localPlayerIdentityVerified = false, nativeProbeEnabled = false
                });
                Console.WriteLine("发送此报告 ZIP（不要发送私有恢复目录）：");
                Console.WriteLine(ArmorDiagnosticEvidence.Export(session.RunDirectory));
            }
            game?.Dispose();
        }
        return 0;
    }

    private static string ResolveManager(string? provided)
    {
        if (provided is not null) return Path.GetFullPath(provided);
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "settings.json"))) return parent;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "settings.json"))) return AppContext.BaseDirectory;
        throw new CliUserException("ManagerNotFound", "请把整个诊断附件解压到已配置的管理器目录。");
    }

    private static void EnsureStopped()
    {
        CliManagerProcessGuard.EnsureDesktopManagerIsClosed();
        var state = CliDeploymentProcessGuard.Inspect();
        if (!state.IsReliable || state.IsGameRunning)
            throw new InvalidOperationException("请先完全退出游戏和管理器。");
    }

    private sealed class DisposableArray<T>(T[] items) : IDisposable where T : IDisposable
    {
        public T[] Items { get; } = items;
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }

    private static DisposableArray<Process> AsDisposableArray(this Process[] processes) => new(processes);

    public static bool IsReady(IReadOnlyDictionary<string, string> status, int pid, int revision)
        => status.GetValueOrDefault("pid") == pid.ToString()
            && status.GetValueOrDefault("revision") == revision.ToString()
            && status.GetValueOrDefault("state") == "ready";

    public static bool AcceptMarker(uint foregroundPid, int gamePid, bool ready)
        => ready && foregroundPid == (uint)gamePid;
}
