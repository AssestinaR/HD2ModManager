using System.IO;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

/// <summary>
/// 把物理参数（<c>HD2PBX01</c> 容器）写进某个 Mod 的 <c>vpk2patch/</c>。
/// <para>
/// <b>为什么单独抽出来。</b>重构建、以及"同一来源再次导入"这两条路都要做这件事，
/// 而它们都是"产物换完之后"的最后一步。规则只有一条：<b>失败不算失败</b>——
/// 参数可以以后补，产物本身是好的；但必须返回一句人能看懂的话，否则"这套护甲不会晃"
/// 在报告里连一个字都没有。
/// </para>
/// </summary>
public static class VpkPhysBoneEmbedder
{
	/// <summary>
	/// 在 <paramref name="modDirectory"/> 上跑一次 <c>physbone embed-mod</c>，返回一条给用户看的结论。
	/// </summary>
	public static async Task<string> RunAsync(
		string executable,
		string modDirectory,
		CancellationToken cancellationToken)
	{
		try
		{
			var result = await Hd2ModProcess.RunAsync(
				executable, ["physbone", "embed-mod", modDirectory, "--json"], cancellationToken).ConfigureAwait(false);
			if (result.ExitCode != 0) return "物理参数嵌入失败：" + Hd2ModProcess.Explain(result, "写入物理参数");
			if (string.IsNullOrWhiteSpace(result.Output)) return "物理参数嵌入没有输出，已跳过。";

			using var document = JsonDocument.Parse(result.Output);
			var root = document.RootElement;
			var embedded = root.TryGetProperty("embedded", out var flag) && flag.ValueKind == JsonValueKind.True;
			if (embedded)
			{
				var bones = root.TryGetProperty("bones", out var bonesValue) && bonesValue.TryGetInt32(out var parsed) ? parsed : 0;
				var patches = root.TryGetProperty("patches", out var patchesValue) && patchesValue.TryGetInt32(out var parsedPatches) ? parsedPatches : 0;
				return $"已写入物理参数（{patches} 个 patch、{bones} 根骨）。";
			}

			var reason = root.TryGetProperty("reason", out var reasonValue) && reasonValue.ValueKind == JsonValueKind.String
				? reasonValue.GetString()
				: null;
			return "没有物理参数（" + (string.IsNullOrWhiteSpace(reason) ? "原因未知" : reason) + "）——这套护甲不会有头发/裙摆摆动。";
		}
		catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
		{
			return "物理参数嵌入跳过（" + exception.Message + "）";
		}
	}

	/// <summary>
	/// 用当前安装里的 <c>hd2mod.exe</c> 跑一次；找不到转换器时返回 <c>null</c>（调用方自己决定怎么说）。
	/// </summary>
	public static async Task<string?> TryRunAsync(string modDirectory, CancellationToken cancellationToken)
	{
		var executable = Hd2ModCliLocator.TryResolve();
		if (executable is null)
			return "物理参数没有写入：找不到 hd2mod.exe（发布包自带）。";
		return await RunAsync(executable, modDirectory, cancellationToken).ConfigureAwait(false);
	}
}
