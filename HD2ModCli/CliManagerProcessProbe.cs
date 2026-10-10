using System.Diagnostics;

namespace HD2ModCli;

// 作用：唯一回答"本机是否有桌面端 HD2ModManager 在运行"的地方，并作为可替换的探测缝隙。
// Purpose: The single source of truth for "is a desktop HD2ModManager running on this machine?",
// exposed as a replaceable probe.
//
// 为什么必须存在这个检查：桌面端把 Mod 库快照持有在内存里，并按自己的节奏写回 library.json。
// CLI 若在此时写入库文件，桌面端下一次保存会静默覆盖 CLI 的结果。
//
// 为什么它不是冗余的：跨进程租约（.hd2mod-cli-write.lock）只覆盖 CLI 与 CLI 之间——桌面端
// 从不获取该租约。因此这个进程检查是唯一挡住"CLI 与桌面端并发写库"的屏障，不能删除。
//
// Why this check exists: the desktop app holds the library snapshot in memory and writes
// library.json on its own schedule, so a concurrent CLI write can be silently overwritten.
// Why it is not redundant: the cross-process lease only covers CLI-vs-CLI (the desktop app never
// takes it), which makes this process check the only barrier against CLI-vs-desktop races.
//
// 为什么可替换："本机是否开着桌面端"是环境状态，不是被测行为。若测试沿用真实探测，整个 CLI
// 套件的结果就取决于开发者桌面上是否开着管理器，无法在 CI 中稳定运行。
// 探测失败时返回哨兵值 [-1]，调用方据此判定为"无法确认"，绝不能当作安全写入窗口。
//
// Why it is replaceable: whether the GUI is open is ambient environment state, not behaviour under
// test; depending on it would make the suite non-hermetic and unusable in CI. A failed enumeration
// returns the [-1] sentinel so callers treat it as "cannot confirm", never as a safe write window.
internal static class CliManagerProcessProbe
{
	private static Func<IReadOnlyList<int>> _probe = DefaultProbe;

	internal static Func<IReadOnlyList<int>> Probe
	{
		get => _probe;
		set => _probe = value ?? throw new ArgumentNullException(nameof(value));
	}

	internal static IReadOnlyList<int> FindRunningManagerProcessIds() => _probe();

	internal static void ResetToDefault() => _probe = DefaultProbe;

	private static IReadOnlyList<int> DefaultProbe()
	{
		try
		{
			return Process.GetProcessesByName("HD2ModManager")
				.Select(process =>
				{
					try { return process.Id; }
					finally { process.Dispose(); }
				})
				.OrderBy(id => id)
				.ToArray();
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			// An uncertain process list must never be treated as a safe write window.
			return [-1];
		}
	}
}
