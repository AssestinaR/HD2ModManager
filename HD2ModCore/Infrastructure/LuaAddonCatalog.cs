using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 内置 Lua 功能的清单：哪些功能能导出成外置包、用什么 addon id、落在哪个子目录。
///
/// 集中在这里是为了让"设置页选外置"和"部署器需要合并进 boot"两处读**同一份**定义，
/// 不会出现"导出的 addon id 和部署时 require 的名字对不上"这种静默失效。
/// </summary>
public static class LuaAddonCatalog
{
	/// <summary>addon 资源名的统一前缀：<c>mods/&lt;作者&gt;/&lt;名字&gt;</c>，三家加载器都认这个形状。</summary>
	public const string Author = "hd2modmanager";

	/// <summary>物理骨骼。</summary>
	public const string PhysicsAddonId = "mods/" + Author + "/physbone";

	/// <summary>护甲外观映射。</summary>
	public const string WardrobeAddonId = "mods/" + Author + "/wardrobe";

	/// <summary>物理外置包的 mod GUID（固定，重复导出不会被当成新 mod）。</summary>
	public const string PhysicsModGuid = "7c1e2b90-5f3a-4d68-9a11-2b7c4e5f6a01";

	/// <summary>映射外置包的 mod GUID。</summary>
	public const string WardrobeModGuid = "3f9d5a12-8c47-4b93-a6e0-1d2f3a4b5c02";

	/// <summary>物理功能的外置包描述。</summary>
	public static LuaAddonPackageExport.Feature Physics() => new(
		Key: "physbone",
		Title: "HD2 物理骨骼运行时",
		AddonId: PhysicsAddonId,
		ModGuid: PhysicsModGuid,
		LuaFileName: "hd2physbone.lua",
		Source: PhysBoneLuaSource.ComposeStandalonePackage(PhysicsAddonId));

	/// <summary>
	/// 映射功能的外置包描述。
	///
	/// 映射运行时靠"接管 <c>update</c>"安装自己（见 armor_override_runtime.lua 尾部），
	/// 三家加载器都在引擎 <c>init</c> 之后才跑 mod、且此时 <c>update</c> 已存在，
	/// 所以 addon 形态可用。首行补上 HD2-Addon 标记，否则加载器扫不到它。
	/// </summary>
	public static LuaAddonPackageExport.Feature Wardrobe() => new(
		Key: "wardrobe",
		Title: "HD2 护甲外观映射运行时",
		AddonId: WardrobeAddonId,
		ModGuid: WardrobeModGuid,
		LuaFileName: "hd2wardrobe.lua",
		Source: ComposeWardrobeSource());

	private static string ComposeWardrobeSource()
	{
		var body = ArmorRuntimeScriptExport.LoadSource();
		var builder = new StringBuilder(body.Length + 256);
		// 首行必须是标记：Boot Mod Loader 按它识别 addon。
		builder.Append("-- HD2-Addon: ").Append(WardrobeAddonId).Append('\n');
		builder.Append(body);
		return builder.ToString();
	}
}
