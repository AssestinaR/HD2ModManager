using System.Reflection;
using System.Text;

namespace HD2ModCore.Infrastructure;

internal static class RuntimeArmorRegistryLuaSource
{
	/// <summary>
	/// 返回要部署进游戏的 Lua 载荷。
	///
	/// 2026-10-23 起只保留<b>资源覆盖运行时</b>。此前的链路是 ffi 内存修补
	/// （<c>armor_registry_runtime</c> + transaction + composition + metadata probe），
	/// 它在 26088801 上第一件事就是 <c>require('ffi')</c>，而该模块在这版引擎里不存在，
	/// 于是整条链在加载时就退出——那段代码已经不可能生效，留着只会误导后来人。
	///
	/// section 关闭功能（<c>wardrobe.masks</c> 及其 apply/layout/locate/publish）同样依赖 ffi
	/// 写 unit TOC，本版一并摘除；等它的替代实现就绪后再接回来。
	///
	/// The ffi memory-patching chain and the section-mask feature are both gone: on 26088801
	/// `require('ffi')` fails, so neither can run. Only the resource-override runtime remains.
	/// </summary>
	public static string Load()
	{
		var assembly = typeof(RuntimeArmorRegistryLuaSource).Assembly;
		return Read(assembly, "armor_override_runtime.lua");
	}

	private static string Read(Assembly assembly, string suffix)
	{
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
		if (name is null)
			throw new InvalidOperationException($"内置护甲运行时资源缺失：{suffix}");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置护甲运行时资源：{suffix}");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
