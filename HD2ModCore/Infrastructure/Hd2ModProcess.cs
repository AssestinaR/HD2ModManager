using System.Diagnostics;
using System.IO;
using System.Text;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 跑一次 <c>hd2mod.exe</c> 并收回它的三段输出。
/// <para>
/// <b>为什么集中一个。</b>管理器里已经有三个调用方各自实现了同一段进程样板
/// （导入、换甲、重构建），三份的差异全在"怎么读 stderr"上，而那种差异的表现是
/// "某个入口永远看不到进度"或者"失败原因里塞满进度行"。这里只做一件事：
/// 稳定地给出退出码 + 完整 stdout + 去掉进度行的 stderr。
/// </para>
/// <para>
/// <b>为什么必须逐行读 stderr。</b>CLI 把进度写在 stderr 上（<c>#progress</c> 行），
/// 整段 <c>ReadToEnd</c> 会把几十行进度攒到进程结束才回来——那正是"进度条一直停在解压中"
/// 的成因。逐行读还能把它们从"错误输出"里摘出去：错误输出只在失败时拼进给用户的消息，
/// 混进进度就等于把真正的原因埋掉。
/// </para>
/// </summary>
public static class Hd2ModProcess
{
	/// <summary>一次运行的结果。<see cref="Error"/> 已剔除进度行。</summary>
	public readonly record struct Result(int ExitCode, string Output, string Error);

	/// <param name="onProgress">
	/// 收到 <c>#progress</c> 行时回调（阶段键 / 已完成 / 总数 / 文案）。给 <c>null</c> 就只丢弃。
	/// </param>
	public static async Task<Result> RunAsync(
		string executable,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default,
		Action<string, int, int, string>? onProgress = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);
		ArgumentNullException.ThrowIfNull(arguments);

		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				// 结论里全是中文。CLI 自己把 Console 设成 UTF-8（Program.cs），这里必须对齐。
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				WorkingDirectory = Path.GetDirectoryName(executable)!,
			},
		};
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

		if (!process.Start()) throw new InvalidOperationException("无法启动 hd2mod.exe。");
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = ReadErrorLinesAsync(process.StandardError, onProgress, cancellationToken);
		using var registration = cancellationToken.Register(() =>
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
		});
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return new Result(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
	}

	private static async Task<string> ReadErrorLinesAsync(
		StreamReader reader,
		Action<string, int, int, string>? onProgress,
		CancellationToken cancellationToken)
	{
		var buffer = new StringBuilder();
		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			if (VpkProgressLine.TryParse(line, out var phase, out var done, out var total, out var text))
			{
				onProgress?.Invoke(phase, done, total, text);
				continue;
			}

			buffer.AppendLine(line);
		}

		return buffer.ToString();
	}

	/// <summary>失败时给一句话：优先 stderr（真正的原因），退回 stdout（CLI 的 JSON 错误体）。</summary>
	public static string Explain(Result result, string what)
	{
		var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
		return $"{what}失败（hd2mod 退出码 {result.ExitCode}）：{detail.Trim()}";
	}
}
