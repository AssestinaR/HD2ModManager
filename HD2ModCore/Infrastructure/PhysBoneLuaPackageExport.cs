namespace HD2ModCore.Infrastructure;

/// <summary>
/// 物理运行时外置包的便捷入口（等价于 <see cref="LuaAddonPackageExport.Export"/> 导出物理那一项）。
///
/// 保留这个类是因为"物理"是当前唯一发布的 Lua 功能，调用点读起来更直接；
/// 真正的通用实现在 <see cref="LuaAddonPackageExport"/>（映射走同一条路）。
/// </summary>
public static class PhysBoneLuaPackageExport
{
	/// <summary>lua 版文件名。</summary>
	public const string FileName = "hd2physbone.lua";

	/// <summary>包内说明文件名。</summary>
	public const string ReadmeFileName = LuaAddonPackageExport.ReadmeFileName;

	/// <summary>导出到指定目录（该目录就是物理功能的包目录），返回目录路径。</summary>
	public static string Export(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		var full = Path.GetFullPath(directory);
		var parent = Path.GetDirectoryName(full);
		var key = Path.GetFileName(full);
		if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(key))
			throw new ArgumentException("需要形如 <root>/<feature> 的目录。", nameof(directory));

		var feature = LuaAddonCatalog.Physics() with { Key = key };
		return LuaAddonPackageExport.Export(parent, feature);
	}
}
