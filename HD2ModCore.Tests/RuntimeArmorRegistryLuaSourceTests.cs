using System.Reflection;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住部署进游戏的 Lua 载荷的**契约**，而不是它的内部结构。
///
/// 2026-10-23 起载荷只剩资源覆盖运行时：旧的 ffi 内存修补链与 section 掩码都摘掉了，
/// 因为 26088801 上 <c>require('ffi')</c> 不存在，它们加载即退出。这里守三件事：
/// 载荷能被引擎当引导脚本用（四个全局钩子）、只走公开 API、且**从不释放覆盖表项**
/// （释放会让已实例化的护甲悬空，几秒后崩溃）。
/// </summary>
public sealed class RuntimeArmorRegistryLuaSourceTests
{
	private static string Load()
	{
		var assembly = typeof(RuntimeArmorRegistryService).Assembly;
		var type = assembly.GetType("HD2ModCore.Infrastructure.RuntimeArmorRegistryLuaSource", throwOnError: true)!;
		return (string)type.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
	}

	[Fact]
	public void EmbeddedPayloadIsTheResourceOverrideRuntime()
	{
		var source = Load();
		Assert.Contains("wardrobe.links", source);
		Assert.Contains("set_resource_override", source);
		Assert.Contains("queue drained", source);
	}

	[Fact]
	public void EmbeddedPayloadDefinesTheEngineHooks()
	{
		var source = Load();
		// 引擎不 pcall 这些钩子：任何一个缺失或抛错都会直接结束进程。
		Assert.Contains("init = noop", source);
		Assert.Contains("shutdown = noop", source);
		Assert.Contains("render = noop", source);
		Assert.Contains("update = function()", source);
	}

	[Fact]
	public void EmbeddedPayloadNeverReleasesAnOverride()
	{
		var source = Load();
		// 释放表项会让通过覆盖创建的实例悬空，军械库重建时崩溃。契约上永不调用。
		Assert.DoesNotContain("clear_resource_override", source);
		Assert.Contains("stay active until the game restarts", source);
	}

	[Fact]
	public void EmbeddedPayloadHasNoFfiOrSectionMachinery()
	{
		var source = Load();
		Assert.DoesNotContain("require('ffi')", source);
		Assert.DoesNotContain("ARMOR_WARDROBE_MASKS", source);
		Assert.DoesNotContain("ARMOR_SECTION_APPLY", source);
		Assert.DoesNotContain("ARMOR_REGISTRY_COMPOSITION", source);
		Assert.DoesNotContain("armor-registry.protocol", source);
	}

	[Fact]
	public void EmbeddedPayloadStartsWithACommentAndHasNoBom()
	{
		var source = Load();
		// Lua 5.1 不跳过 BOM：带 BOM 的引导脚本会编译失败，而引擎不 pcall 它，进程直接退出。
		Assert.StartsWith("--", source);
		Assert.False(source.Length > 0 && source[0] == '\uFEFF');
	}
}
