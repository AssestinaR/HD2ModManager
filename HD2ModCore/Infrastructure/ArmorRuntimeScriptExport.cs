using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把护甲映射运行时脚本导出成文件。
///
/// 用途：用户关掉「独占映射 Lua」后，管理器不再投放自己的运行时补丁；脚本导出到文件夹，
/// 由用户交给 MDL / Bingus 这类第三方加载器加载。
///
/// 写文件时**不带 BOM**：Lua 5.1 不跳过 BOM，带 BOM 的脚本编译会直接失败，
/// 而加载器通常不 pcall 引导脚本，表现为游戏启动即退出——这类问题极难排查。
/// </summary>
public static class ArmorRuntimeScriptExport
{
	public const string FileName = "armor_override_runtime.lua";

	/// <summary>写出脚本并返回文件路径。</summary>
	public static string Export(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		var full = Path.GetFullPath(directory);
		Directory.CreateDirectory(full);
		var path = Path.Combine(full, FileName);
		File.WriteAllText(
			path,
			RuntimeArmorRegistryLuaSource.Load(),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return path;
	}
}
