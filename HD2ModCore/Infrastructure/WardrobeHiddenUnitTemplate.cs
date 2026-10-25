using System.Reflection;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 管理器的运行时补丁里那个"什么都不画"的 unit。
///
/// 运行时把"来源套件里不存在的槽位"（例如 RE-2310 只有裆部 1 号层，目标却有 0/2 号层）
/// 指向它，这样目标件永远有对应物，不会留下原样显示的残留件。
///
/// 它必须是一个**真实存在**的 unit 资源：实测指向不存在的 id 会让引擎记住加载失败，
/// 之后军械库整页空白并崩溃。载荷来自 2026-09-28 "隐藏全部原版护甲 unit" 实验里
/// 引擎已经接受过的隐藏占位（取其中最小的一个，4240 + 418 字节），
/// 以自己的键 <see cref="Key"/> 重新发布。
///
/// 部署形态是**一个补丁两个条目**：该 unit + Lua 运行时，不额外占用 patch 序号。
/// </summary>
internal static class WardrobeHiddenUnitTemplate
{
	/// <summary>unit 的键：<c>stingray_hash("mods/codex/wardrobe_hidden_unit")</c>。</summary>
	public const string Key = "def7dc54e86d6054";

	private const string PatchSuffix = "wardrobe_hidden_unit.patch";
	private const string GpuSuffix = "wardrobe_hidden_unit.patch.gpu_resources";

	/// <summary>一份只含该 unit 的补丁，作为双条目构建的模板。</summary>
	public static byte[] ReadPatch() => Read(PatchSuffix);

	/// <summary>模板对应的 GPU 侧车（418 字节）。</summary>
	public static byte[] ReadGpu() => Read(GpuSuffix);

	private static byte[] Read(string suffix)
	{
		var assembly = typeof(WardrobeHiddenUnitTemplate).Assembly;
		var name = assembly.GetManifestResourceNames()
			.FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal))
			?? throw new InvalidOperationException($"内置隐藏 unit 资源缺失：{suffix}");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置隐藏 unit 资源：{suffix}");
		using var buffer = new MemoryStream();
		stream.CopyTo(buffer);
		return buffer.ToArray();
	}
}
