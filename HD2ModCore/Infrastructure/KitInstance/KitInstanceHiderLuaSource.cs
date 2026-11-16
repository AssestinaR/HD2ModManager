using System.Reflection;
using System.Text;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>内嵌的 Hider 脚本（<c>HD2RuntimeArmorOverride/src/kit_instance_hider.lua</c>）。</summary>
internal static class KitInstanceHiderLuaSource
{
	/// <summary>独立 addon 补丁里 Lua 资源的名字（它的哈希就是资源 ID），也是首行 <c>-- HD2-Addon:</c> 标记的值。</summary>
	public const string ResourceName = "mods/hd2modmanager/kit_instance_hider";

	public static string Load()
	{
		var assembly = typeof(KitInstanceHiderLuaSource).Assembly;
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith("kit_instance_hider.lua", StringComparison.Ordinal))
			?? throw new InvalidOperationException("内置按实例隐藏运行时资源缺失：kit_instance_hider.lua");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException("无法读取内置按实例隐藏运行时资源：kit_instance_hider.lua");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
