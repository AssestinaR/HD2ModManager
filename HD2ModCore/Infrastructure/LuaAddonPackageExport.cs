using System.Text;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把内置 Lua 功能导出成**外置补丁包**，供玩家手动加进 mod 库。
///
/// <para>
/// 每个功能导出**两种形态**（2026-11-01 核对过三家加载器的一手资料）：
/// <list type="bullet">
/// <item><c>lua版/</c> —— 裸 <c>.lua</c> 文件。给"从文件夹读脚本"的加载器，也方便玩家自己看。</item>
/// <item><c>patch版/</c> —— 一个完整的 mod 目录（<c>manifest.json</c> + 补丁三件套）。
/// 资源名与首行标记走 <c>-- HD2-Addon:</c> 约定，Boot Mod Loader / Chef Shared Loader /
/// MDL3 全都认。**这是通用解**：拖进 mod 库、启用、部署即可。</item>
/// </list>
/// 两种都产出的原因：不同加载器的入口形态不一样，让玩家按自己装的加载器挑，
/// 比我们赌一个"最通用"的形态更可靠。
/// </para>
/// </summary>
public static class LuaAddonPackageExport
{
	/// <summary>包内说明文件名。</summary>
	public const string ReadmeFileName = "如何安装.txt";

	/// <summary>一个可导出的内置 Lua 功能。</summary>
	/// <param name="Key">输出目录名（如 <c>physbone</c>）。</param>
	/// <param name="Title">说明里用的中文名。</param>
	/// <param name="AddonId">addon 资源名，形如 <c>mods/hd2modmanager/physbone</c>。</param>
	/// <param name="ModGuid">patch 版 mod 目录的 manifest GUID（同一功能每次导出必须相同）。</param>
	/// <param name="LuaFileName">lua 版的文件名。</param>
	/// <param name="Source">单文件 Lua 源码（首行已带 HD2-Addon 标记）。</param>
	public sealed record Feature(
		string Key,
		string Title,
		string AddonId,
		string ModGuid,
		string LuaFileName,
		string Source);

	/// <summary>
	/// 导出一个功能到 <paramref name="rootDirectory"/>/<paramref name="feature"/>.Key。
	/// 返回该功能目录。
	/// </summary>
	public static string Export(string rootDirectory, Feature feature)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		ArgumentNullException.ThrowIfNull(feature);

		var root = Path.GetFullPath(rootDirectory);
		var folder = Path.Combine(root, feature.Key);
		// 每次导出整目录重建：避免上一次的残留文件被误当成"还要装的东西"。
		if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

		var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

		var luaDir = Path.Combine(folder, "lua版");
		Directory.CreateDirectory(luaDir);
		File.WriteAllText(Path.Combine(luaDir, feature.LuaFileName), feature.Source, utf8);

		var patchDir = Path.Combine(folder, "patch版");
		Directory.CreateDirectory(patchDir);
		LuaAddonPatchBuilder.Write(patchDir, feature.AddonId, feature.Source);
		File.WriteAllText(Path.Combine(patchDir, "manifest.json"), BuildManifest(feature), utf8);

		File.WriteAllText(Path.Combine(folder, ReadmeFileName), BuildReadme(feature), utf8);
		return folder;
	}

	private static string BuildManifest(Feature feature)
	{
		// 与其它 mod 的 manifest 同构：Version/Guid/Name/Description。
		// GUID 固定，重复导出不会被管理器当成另一个 mod。
		var manifest = new Dictionary<string, object>
		{
			["Version"] = 1,
			["Guid"] = feature.ModGuid,
			["Name"] = feature.Title,
			["Description"] = "HD2 Mod Manager 生成的内置 Lua 功能（外置补丁包）。",
		};
		return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
	}

	private static string BuildReadme(Feature feature)
	{
		var builder = new StringBuilder();
		builder.AppendLine($"{feature.Title} · 外置 lua 包");
		builder.AppendLine("================================");
		builder.AppendLine();
		builder.AppendLine("这里有两个版本，**按你装的加载器挑一个**，不要两个都装：");
		builder.AppendLine();
		builder.AppendLine("【patch版】推荐。适用于会扫描 Lua 补丁的加载器：");
		builder.AppendLine("  · Boot Mod Loader");
		builder.AppendLine("  · Chef Shared Loader");
		builder.AppendLine("  · MDL3 / HD2 Data Hub");
		builder.AppendLine("  · Bingus Shared Loader");
		builder.AppendLine("  用法：把整个 patch版 文件夹拖进管理器的 mod 库 → 启用 → 部署。");
		builder.AppendLine();
		builder.AppendLine("【lua版】适用于直接从文件夹读 .lua 的加载器，或者你想自己看/改脚本。");
		builder.AppendLine($"  文件：{feature.LuaFileName}");
		builder.AppendLine();
		builder.AppendLine("说明：");
		builder.AppendLine("· 这是「外置加载器兼容」模式：管理器不占用 boot，由你的加载器负责加载本脚本。");
		builder.AppendLine("· 物理参数跟着护甲 patch 走，不需要在这里再配一遍。");
		builder.AppendLine("· 换护甲、进军械库、切地图都会自动重新挂载，不需要重启游戏。");
		builder.AppendLine("· 如果没生效，先确认对应的护甲 mod 是用本管理器转换的。");
		return builder.ToString();
	}
}
