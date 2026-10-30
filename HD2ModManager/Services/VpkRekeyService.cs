using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

/// <summary>一次换甲的结果。</summary>
public sealed record VpkRekeyOutcome(
	bool Ok,
	string? Error,
	IReadOnlyList<string> Mappings,
	int ReplacedUnits,
	int HiddenMeshes,
	IReadOnlyList<string> TargetArchiveIds,
	IReadOnlyList<string> TargetNames)
{
	public static VpkRekeyOutcome Failed(string error) => new(false, error, [], 0, 0, [], []);

	/// <summary>
	/// 这次没有跨护甲搬运，而是把来源 patch 原样退回 <c>Overwrite/</c>
	/// （用户选的目标就是这个 Mod 自己的素体）。界面据此换一句话。
	/// </summary>
	public bool RestoredFromSource { get; init; }
}

/// <summary>
/// 调 <c>hd2mod vpk rekey</c> 把 VPK 模组的 7 个 Unit 换到目标护甲上。
/// <para>
/// <b>为什么走外部进程</b>：换甲要读装备表、读目标 Unit、生成隐藏 Unit，这一整套在 CLI 里
/// 已经跑通了。管理器再实现一遍就是两份会漂移的实现，而漂移的表现是"命令行换出来的能看、
/// 管理器换出来的不能看"。管理器只负责"选哪个目标、什么时候换、换完刷新"。
/// </para>
/// </summary>
public static class VpkRekeyService
{
	/// <summary>
	/// 执行一次换甲。<paramref name="modDirectory"/> 给出时，成功后会把"指向哪件护甲"
	/// 记进 <c>wardrobe.json</c>——组件树的根标题要用它，重开页面时也不必再猜。
	/// </summary>
	public static async Task<VpkRekeyOutcome> RunAsync(
		string modSelector,
		string? modDirectory,
		IReadOnlyList<string> targetArchiveIds,
		Action<string>? onStage,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		var gameData = SettingsService.GetGameDataFolder();
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return VpkRekeyOutcome.Failed("没有配置游戏 data 目录：换甲要读目标护甲与隐藏 Unit。");

		var paths = SettingsService.CreateStoragePaths();
		// 位置随布局而变（开发构建在旁边、发布包在 bin\ 下），见 Hd2ModCliLocator。
		var executable = Hd2ModCliLocator.TryResolve();
		if (executable is null)
			return VpkRekeyOutcome.Failed(
				"找不到 hd2mod.exe：换甲依赖它（发布包自带）。"
				+ $"已查找：{Hd2ModCliLocator.DescribeSearchedLocations()}");

		onStage?.Invoke("正在读取目标护甲的 Unit 表");
		var arguments = new List<string>
		{
			"vpk", "rekey", modSelector,
			"--manager-root", paths.AppRootDirectory,
			"--library", paths.ModsRootDirectory ?? paths.AppRootDirectory,
			"--game-data", gameData,
			"--json",
		};
		if (targetArchiveIds.Count != 0)
			arguments.AddRange(["--archive-id", string.Join(',', targetArchiveIds)]);

		var (exitCode, output, error) = await RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(output))
			return VpkRekeyOutcome.Failed($"换甲失败（hd2mod 退出码 {exitCode}）：{error.Trim()}");

		try
		{
			using var document = JsonDocument.Parse(output);
			var root = document.RootElement;
			if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
			{
				var message = errorElement.TryGetProperty("message", out var text) ? text.GetString() : null;
				return VpkRekeyOutcome.Failed(message ?? "换甲失败。");
			}

			var outcome = new VpkRekeyOutcome(
				root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
				null,
				ReadStrings(root, "mappings"),
				ReadInt(root, "replacedUnits"),
				ReadInt(root, "hiddenMeshes"),
				ReadStrings(root, "targets"),
				ReadStrings(root, "targetNames"))
			{
				RestoredFromSource = root.TryGetProperty("restoredFromSource", out var restored)
					&& restored.ValueKind == JsonValueKind.True,
			};

			// 记住这次指向了哪件护甲：组件树的根标题要用它，重开页面时也不必再猜。
			if (outcome.Ok && !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory))
			{
				var file = await VpkModWardrobeService.ReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
				await VpkModWardrobeService.WriteAsync(modDirectory, file with
				{
					TargetArmorArchiveId = outcome.TargetArchiveIds.FirstOrDefault(),
					TargetArmorName = outcome.TargetNames.FirstOrDefault(),
				}, cancellationToken).ConfigureAwait(false);
			}

			return outcome;
		}
		catch (JsonException exception)
		{
			return VpkRekeyOutcome.Failed($"换甲输出无法解析：{exception.Message}");
		}
	}

	private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
			? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(text => text.Length != 0).ToArray()
			: [];

	private static int ReadInt(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

	private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
		string executable,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
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
			}
		};
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

		if (!process.Start()) throw new InvalidOperationException("无法启动 hd2mod.exe。");
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		using var registration = cancellationToken.Register(() =>
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
		});
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
	}
}
