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
		var transaction = Read(assembly, "armor_registry_transaction.lua");
		var composition = Read(assembly, "armor_registry_composition.lua");
		var refreshProbe = Read(assembly, "armor_refresh_probe.lua");
		var worldProbe = Read(assembly, "armor_refresh_world_probe.lua");
		var codeProbe = Read(assembly, "armor_refresh_code_probe.lua");
		var routeProbe = Read(assembly, "armor_refresh_route_probe.lua");
		var hotkeyProbe = Read(assembly, "armor_refresh_hotkey.lua");
		var resourceProbe = Read(assembly, "armor_refresh_resources.lua");
		var componentProbe = Read(assembly, "armor_refresh_component.lua");
		var multisiteProbe = Read(assembly, "armor_refresh_multisite.lua");
		var bankPatch = Read(assembly, "bank_patch.lua");
		return $"local ARMOR_REGISTRY_PROTOCOL=(function()\n{protocol}\nend)()\n"
			+ $"local ARMOR_METADATA_PROBE_CORE=(function()\n{probeCore}\nend)()\n"
			+ $"local ARMOR_REGISTRY_TRANSACTION=(function()\n{transaction}\nend)()\n"
			+ $"local ARMOR_REGISTRY_COMPOSITION=(function()\n{composition}\nend)()\n"
			+ $"local ARMOR_REFRESH_PROBE=(function()\n{refreshProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_WORLD_PROBE=(function()\n{worldProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_CODE_PROBE=(function()\n{codeProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_ROUTE_PROBE=(function()\n{routeProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_HOTKEY=(function()\n{hotkeyProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_RESOURCES=(function()\n{resourceProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_COMPONENT=(function()\n{componentProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_MULTISITE=(function()\n{multisiteProbe}\nend)()\n"
			// bank 扩容补丁必须排在 __hd2_runtime 之前，运行时闭包才能把它当 upvalue。
			+ $"local ARMOR_BANK_PATCH=(function()\n{bankPatch}\nend)()\n"
			+ $"local __hd2_runtime=(function()\n{runtime}\nend)()\n"
			// 安装补丁。pcall 包住：失败只记录，绝不让整个插件加载失败，
			// 否则玩家连原版护甲都没有。
			+ "local __hd2_bank_patch_state=nil\n"
			+ "do\n"
			+ "  local ok,state_or_err=pcall(ARMOR_BANK_PATCH.install)\n"
			+ "  if ok and type(state_or_err)=='table' then\n"
			+ "    __hd2_bank_patch_state=state_or_err\n"
			+ "  else\n"
			+ "    __hd2_bank_patch_error=tostring(state_or_err)\n"
			+ "  end\n"
			+ "end\n"
			+ probe;
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
