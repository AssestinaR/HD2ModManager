using System.Reflection;
using System.Text;

namespace HD2ModCore.Infrastructure;

internal static class RuntimeArmorRegistryLuaSource
{
	public static string Load()
	{
		var assembly = typeof(RuntimeArmorRegistryLuaSource).Assembly;
		var protocol = Read(assembly, "armor_registry_protocol.lua");
		var probeCore = Read(assembly, "armor_metadata_probe_core.lua");
		var runtime = Read(assembly, "armor_registry_runtime.lua");
		var probe = Read(assembly, "armor_metadata_probe.lua");
		return $"local ARMOR_REGISTRY_PROTOCOL=(function()\n{protocol}\nend)()\n"
			+ $"local ARMOR_METADATA_PROBE_CORE=(function()\n{probeCore}\nend)()\n"
			+ $"local __hd2_runtime=(function()\n{runtime}\nend)()\n{probe}";
	}

	private static string Read(Assembly assembly, string suffix)
	{
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
		if (name is null)
			throw new InvalidOperationException($"内置护甲注册运行时资源缺失：{suffix}");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置护甲注册运行时资源：{suffix}");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
