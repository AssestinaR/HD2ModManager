using System.Runtime.CompilerServices;

namespace HD2ModCli.Tests;

// 作用：把"本机是否开着桌面端 HD2ModManager"这一环境状态从整个 CLI 测试套件中隔离出去。
// Purpose: Removes the "is a desktop HD2ModManager open on this machine?" ambient state from the whole
// CLI test suite.
//
// 生产环境的 CliWriteTargetGuard 会在任何 HD2ModManager.exe 运行时拒绝全部 Mod 库写入。这是必要的
// 安全约束：桌面端把库快照持有在内存里并按自己的节奏写回 library.json，CLI 的并发写入会被静默覆盖；
// 而跨进程租约（.hd2mod-cli-write.lock）只覆盖 CLI 与 CLI，桌面端从不获取它，所以进程检查不是冗余。
//
// 但若测试沿用真实探测，整个套件的结果就取决于开发者桌面上是否开着管理器——写入类用例会成批失败，
// 套件无法在 CI 中运行。因此在程序集初始化时把探测置为"未运行"。
//
// Production refuses every Mod-library write while any HD2ModManager.exe runs. That constraint is
// necessary: the desktop app holds the library snapshot in memory and writes library.json on its own
// schedule, so a concurrent CLI write would be silently overwritten, and the cross-process lease only
// covers CLI-vs-CLI (the desktop app never takes it) — which makes the process check non-redundant.
// Reusing the real probe in tests would however make the suite depend on the developer's desktop, so the
// probe is neutralised at assembly initialisation.
//
// 探测替换必须成对使用 WithManagerProcessIdsAsync。所有会调用 CLI 的测试类都在 "cli-console" 集合内
// （DisableParallelization = true），CLI 调用天然串行，因此替换窗口内不会有其他调用观察该探测。
//
// Probe replacement must go through WithManagerProcessIdsAsync. Every test class that invokes the CLI
// belongs to the "cli-console" collection (DisableParallelization = true), so CLI invocations are
// already serialised and no other invocation can observe the replacement window.
internal static class CliTestEnvironment
{
	internal static readonly Func<IReadOnlyList<int>> NoManagerRunning = static () => Array.Empty<int>();

	[ModuleInitializer]
	internal static void Initialize() => CliManagerProcessProbe.Probe = NoManagerRunning;

	// 作用：在指定进程探测结果下执行一段 CLI 调用，结束后恢复默认探测。
	// Purpose: Runs a CLI invocation under a specific process-probe result and restores the default after.
	internal static async Task<T> WithManagerProcessIdsAsync<T>(
		IReadOnlyList<int> runningManagerProcessIds,
		Func<Task<T>> action)
	{
		ArgumentNullException.ThrowIfNull(runningManagerProcessIds);
		ArgumentNullException.ThrowIfNull(action);
		var previous = CliManagerProcessProbe.Probe;
		CliManagerProcessProbe.Probe = () => runningManagerProcessIds;
		try { return await action().ConfigureAwait(false); }
		finally { CliManagerProcessProbe.Probe = previous; }
	}
}
