using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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

	/// <summary>
	/// 失败时给一句<b>能读</b>的话：先从 CLI 的 JSON 报告里取出真正的原因，取不到才退回原文。
	/// <para>
	/// <b>为什么不能直接用 <see cref="Explain"/>。</b>它按"stderr 比 stdout 更接近真相"排序，
	/// 而 CLI 的失败有两种写法：
	/// </para>
	/// <list type="bullet">
	/// <item>整体失败 —— 顶层 <c>{"ok":false,"error":{"code":…,"message":…}}</c>；</item>
	/// <item>单个 VPK 失败 —— 顶层 <c>failures:[{vpk,error}]</c>，退出码 2，而 stderr 上只有
	/// <c>#progress</c> 行（已被剔除）。</item>
	/// </list>
	/// <para>
	/// 第二种情况下 stderr 是空的，于是 <see cref="Explain"/> 会把<b>整份 JSON</b> 拼进消息：
	/// 2026-11-09 实测那一条是 1 046 个字符，用户看到的是
	/// <c>重新转换失败（hd2mod 退出码 2）：{ "ok": false, …</c>，真正的原因埋在几百字之后。
	/// </para>
	/// </summary>
	/// <param name="result">一次 CLI 运行的结果。</param>
	/// <param name="what">动作名（"重新转换""规划 VPK"…），拼在"失败："前面。</param>
	public static string DescribeFailure(Result result, string what)
	{
		if (TryReadJsonMessage(result.Output) is { Length: > 0 } json)
			return $"{what}失败：{json}";

		var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
		return $"{what}失败（hd2mod 退出码 {result.ExitCode}）：{Clip(detail.Trim())}";
	}

	/// <summary>从 CLI 的 JSON 里取"一句话原因"：顶层 error.message，其次 failures[].error。</summary>
	private static string? TryReadJsonMessage(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return null;

			if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
				&& error.TryGetProperty("message", out var message)
				&& message.ValueKind == JsonValueKind.String
				&& message.GetString() is { Length: > 0 } text)
			{
				return Clip(text);
			}

			// 单个 VPK 失败：逐个列出来（最多三条），末尾补一句"还有 N 个"。
			if (root.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array
				&& failures.GetArrayLength() != 0)
			{
				var messages = failures.EnumerateArray()
					.Select(item => item.ValueKind == JsonValueKind.Object
						&& item.TryGetProperty("error", out var why) && why.ValueKind == JsonValueKind.String
							? why.GetString()
							: null)
					.Where(item => !string.IsNullOrWhiteSpace(item))
					.Take(3)
					.ToArray();
				if (messages.Length != 0)
				{
					var count = failures.GetArrayLength();
					var more = count > messages.Length ? $"（共 {count} 个 VPK 失败，这里列出前 {messages.Length} 个）" : string.Empty;
					return Clip(string.Join("；", messages!)) + more;
				}
			}
		}
		catch (JsonException)
		{
			// 不是 JSON：交给调用方退回原文。
		}

		return null;
	}

	/// <summary>把一段可能很长的原始输出剪成人能看的长度。</summary>
	private static string Clip(string text)
		=> text.Length <= MaxDetailLength
			? text
			: text[..MaxDetailLength].TrimEnd() + $"…（原文共 {text.Length} 字，已截断）";

	/// <summary>失败文案里原文的上限：超过它一定是把 JSON 之类整段拼进来了。</summary>
	private const int MaxDetailLength = 400;
}
