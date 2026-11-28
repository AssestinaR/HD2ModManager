using System.Reflection;
using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 内嵌的"护甲身份"只读探针（<c>HD2RuntimeArmorOverride/src/armor_identity_probe.lua</c>）。
/// <para>
/// 它是**一次性诊断**，不进产品流程：只有在 <c>ArmorRegistry/identity-probe.on</c> 存在时，
/// 独占 boot 的启动文档里才会多出这一块。为什么必须走 boot：独占模式下没有第三方加载器，
/// addon 形态的 Lua mod 不会被任何东西加载（2026-11-24 实机确认）。
/// </para>
/// </summary>
internal static class IdentityProbeLuaSource
{
	/// <summary>boot 文档里这一块的 chunk 名（同时是 Lua 资源的名字）。</summary>
	public const string ResourceName = "mods/hd2modmanager/armor_identity_probe";

	/// <summary>开关文件：放在 <see cref="RuntimeArmorRegistryService"/> 的 RegistryRoot 下。</summary>
	public const string EnableFileName = "identity-probe.on";

	public static string Load()
	{
		var assembly = typeof(IdentityProbeLuaSource).Assembly;
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith("armor_identity_probe.lua", StringComparison.Ordinal))
			?? throw new InvalidOperationException("内置探针资源缺失：armor_identity_probe.lua");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException("无法读取内置探针资源：armor_identity_probe.lua");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
