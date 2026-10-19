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
		var wardrobe = Read(assembly, "armor_wardrobe_masks.lua");
		var sectionLayout = Read(assembly, "armor_section_layout.lua");
		var sectionApply = Read(assembly, "armor_section_apply.lua");
		var sectionLocate = Read(assembly, "armor_section_locate.lua");
		var sectionPublish = Read(assembly, "armor_section_publish.lua");
		var refreshProbe = Read(assembly, "armor_refresh_probe.lua");
		var worldProbe = Read(assembly, "armor_refresh_world_probe.lua");
		var codeProbe = Read(assembly, "armor_refresh_code_probe.lua");
		var routeProbe = Read(assembly, "armor_refresh_route_probe.lua");
		var hotkeyProbe = Read(assembly, "armor_refresh_hotkey.lua");
		var resourceProbe = Read(assembly, "armor_refresh_resources.lua");
		var componentProbe = Read(assembly, "armor_refresh_component.lua");
		var multisiteProbe = Read(assembly, "armor_refresh_multisite.lua");
		return $"local ARMOR_REGISTRY_PROTOCOL=(function()\n{protocol}\nend)()\n"
			+ $"local ARMOR_METADATA_PROBE_CORE=(function()\n{probeCore}\nend)()\n"
			+ $"local ARMOR_REGISTRY_TRANSACTION=(function()\n{transaction}\nend)()\n"
			+ $"local ARMOR_REGISTRY_COMPOSITION=(function()\n{composition}\nend)()\n"
			+ $"local ARMOR_WARDROBE_MASKS=(function()\n{wardrobe}\nend)()\n"
			+ $"local ARMOR_SECTION_LAYOUT=(function()\n{sectionLayout}\nend)()\n"
			+ $"local ARMOR_SECTION_APPLY=(function()\n{sectionApply}\nend)()\n"
			+ $"local ARMOR_SECTION_LOCATE=(function()\n{sectionLocate}\nend)()\n"
			+ $"local ARMOR_SECTION_PUBLISH=(function()\n{sectionPublish}\nend)()\n"
			+ $"local ARMOR_REFRESH_PROBE=(function()\n{refreshProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_WORLD_PROBE=(function()\n{worldProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_CODE_PROBE=(function()\n{codeProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_ROUTE_PROBE=(function()\n{routeProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_HOTKEY=(function()\n{hotkeyProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_RESOURCES=(function()\n{resourceProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_COMPONENT=(function()\n{componentProbe}\nend)()\n"
			+ $"local ARMOR_REFRESH_MULTISITE=(function()\n{multisiteProbe}\nend)()\n"
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
