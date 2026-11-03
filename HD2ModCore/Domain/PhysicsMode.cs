namespace HD2ModCore.Domain;

/// <summary>
/// 物理骨骼（jigglebone 二次运动）的落地方式。
///
/// 和 <see cref="ArmorMappingMode"/> 是同一套三态语义，但物理**没有"真映射"这一档**——
/// 物理是纯运行时计算，不存在"把结果写进注册表数据"的形态。所以这里的三个值是
/// 关闭 / 独占入口 / 外置入口。
/// </summary>
public enum PhysicsMode
{
	/// <summary>不启用物理。不生成任何 Lua，也不占 boot。</summary>
	Off = 0,

	/// <summary>
	/// 独占 lua 入口。管理器占用 boot，把物理运行时合并进启动补丁。
	/// 只有总开关「启用管理器内置 lua 类功能」打开时才可选。
	/// </summary>
	ExclusiveLuaEntry = 1,

	/// <summary>
	/// 外置 lua 入口兼容。物理运行时打成一个独立的 Lua 补丁包，
	/// 由玩家自己加进 mod 库部署，交给外置加载器加载；管理器不碰 boot。
	/// </summary>
	ExternalLuaEntry = 2,
}

public static class PhysicsModes
{
	/// <summary>
	/// 默认关闭。物理是"额外效果"，默认不替用户开启——想用的人主动选，
	/// 这样发布初版时不会突然给所有人加一份运行时开销。
	/// （2026-11-03 试过改成「独占 boot」，当天就改回来了：物理要读游戏自己的
	/// boot 归档，默认开等于把一个必然失败的分支变成默认路径。）
	/// </summary>
	public const PhysicsMode Default = PhysicsMode.Off;

	/// <summary>该模式是否需要投放 Lua 运行时。</summary>
	public static bool UsesRuntimeLua(this PhysicsMode mode)
		=> mode is PhysicsMode.ExclusiveLuaEntry or PhysicsMode.ExternalLuaEntry;

	/// <summary>该模式是否需要**占用 boot**（只有独占入口需要）。</summary>
	public static bool OccupiesBoot(this PhysicsMode mode)
		=> mode is PhysicsMode.ExclusiveLuaEntry;

	public static string DisplayName(this PhysicsMode mode) => mode switch
	{
		PhysicsMode.ExclusiveLuaEntry => "独占 boot",
		PhysicsMode.ExternalLuaEntry => "外置加载器兼容",
		PhysicsMode.Off => "关闭",
		_ => mode.ToString(),
	};

	public static string Description(this PhysicsMode mode) => mode switch
	{
		PhysicsMode.ExclusiveLuaEntry =>
			"物理运行时合并进管理器自己的启动补丁，由游戏自身的 Lua VM 加载，不需要第三方加载器。",
		PhysicsMode.ExternalLuaEntry =>
			"物理运行时打成一个独立的 Lua 补丁包，点「应用」后会打开所在文件夹，由你手动加进 mod 库部署。管理器不占用 boot 入口。",
		PhysicsMode.Off =>
			"不投放任何物理 Lua，护甲的头发/裙摆保持静态。",
		_ => string.Empty,
	};

	/// <summary>
	/// 当前可选的档位。**总开关关闭时不提供「独占」**——选项本身就表达能力边界，
	/// 而不是让用户选完再发现无效。
	/// </summary>
	public static IReadOnlyList<PhysicsMode> AvailableFor(bool builtInLuaEnabled)
		=> builtInLuaEnabled
			? [PhysicsMode.ExclusiveLuaEntry, PhysicsMode.ExternalLuaEntry, PhysicsMode.Off]
			: [PhysicsMode.ExternalLuaEntry];

	/// <summary>
	/// 把持久化的整数值收敛到合法档位。
	/// 总开关关闭时，「独占」与「关闭」都收敛到「外置」——
	/// 前者不可选；后者在那套选项里不存在（不想用物理就不把包加进库）。
	/// </summary>
	public static PhysicsMode Normalize(int value, bool builtInLuaEnabled)
	{
		if (!builtInLuaEnabled) return PhysicsMode.ExternalLuaEntry;
		return Enum.IsDefined(typeof(PhysicsMode), value) ? (PhysicsMode)value : Default;
	}
}
