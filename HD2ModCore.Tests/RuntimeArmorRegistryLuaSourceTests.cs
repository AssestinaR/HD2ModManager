using System.Reflection;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorRegistryLuaSourceTests
{
	[Fact]
	public void EmbeddedRuntimeIncludesIncrementalModulesBeforeEntryPoint()
	{
		var assembly = typeof(RuntimeArmorRegistryService).Assembly;
		var type = assembly.GetType("HD2ModCore.Infrastructure.RuntimeArmorRegistryLuaSource", throwOnError: true)!;
		var source = (string)type.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
		Assert.Contains("version='0.2.0'", source);
		var transaction = source.IndexOf("local ARMOR_REGISTRY_TRANSACTION=", StringComparison.Ordinal);
		var composition = source.IndexOf("local ARMOR_REGISTRY_COMPOSITION=", StringComparison.Ordinal);
		var wardrobe = source.IndexOf("local ARMOR_WARDROBE_MASKS=", StringComparison.Ordinal);
		var layout = source.IndexOf("local ARMOR_SECTION_LAYOUT=", StringComparison.Ordinal);
		var apply = source.IndexOf("local ARMOR_SECTION_APPLY=", StringComparison.Ordinal);
		var locate = source.IndexOf("local ARMOR_SECTION_LOCATE=", StringComparison.Ordinal);
		var publish = source.IndexOf("local ARMOR_SECTION_PUBLISH=", StringComparison.Ordinal);
		var entry = source.IndexOf("local __hd2_runtime=", StringComparison.Ordinal);
		Assert.True(transaction >= 0 && composition > transaction && entry > composition);
		// 掩码解析器与 TOC 走查器都必须排在入口点之前：入口点的 tick 里要用它们，
		// 晚于入口点加载就是 nil，而且只在游戏里才会炸。
		Assert.True(wardrobe > composition && wardrobe < entry);
		Assert.True(layout > wardrobe && layout < entry);
		Assert.True(apply > layout && apply < entry);
		Assert.True(locate > apply && locate < entry);
		Assert.True(publish > locate && publish < entry);
		Assert.Contains("wardrobe.masks exceeds 1 MiB", source);
		Assert.Contains("function M.lookup(document,target)", source);
		Assert.Contains("function M.section_fingerprint", source);
		Assert.Contains("mesh_info_offset_invalid", source);
		Assert.Contains("precheck_failed", source);
		Assert.Contains("function M.pattern", source);
		// 掩码必须在 blob 提交之前落盘，失败要整体撤回：这两句就是那个顺序的锚点。
		Assert.Contains("prepare_section_masks", source);
		Assert.Contains("section mask failed", source);
		Assert.Contains("restore_section_masks", source);
		Assert.Contains("rolled back", source);
		Assert.Contains("published buffer limit reached", source);
		Assert.Contains("local function publish_document", source);
		Assert.Contains("incremental applied: built=", source);
		Assert.Contains("metadata-probe.request", source);
		Assert.Contains("local ARMOR_REFRESH_PROBE=", source);
		Assert.Contains("local ARMOR_REFRESH_WORLD_PROBE=", source);
		Assert.Contains("refresh-probe.request", source);
		Assert.Contains("probe_version=10", source);
		Assert.Contains("local ARMOR_REFRESH_CODE_PROBE=", source);
		Assert.Contains("local ARMOR_REFRESH_ROUTE_PROBE=", source);
		Assert.Contains("local ARMOR_REFRESH_HOTKEY=", source);
		Assert.Contains("refresh-hotkey.request", source);
		Assert.Contains("hotkey_version=4", source);
		Assert.Contains("local ARMOR_REFRESH_MULTISITE=", source);
		Assert.Contains("local ARMOR_REFRESH_COMPONENT=", source);
		Assert.Contains("local ARMOR_REFRESH_RESOURCES=", source);
		Assert.Contains("native_owner_trace", source);
		Assert.Contains("World.link_unit", source);
		Assert.DoesNotContain("local function apply_document(document)", source);
	}
}
