namespace HD2ModCore.Domain;

/// <summary>
/// 护甲外观映射的落地方式。
///
/// 兼容模式走运行时覆盖表（<c>set_resource_override</c>），因此可以热切换；
/// 真映射模式把结果直接写进注册表数据，代价是每次改动都必须重启游戏。
/// </summary>
public enum ArmorMappingMode
{
	/// <summary>
	/// 防污染兼容模式（独占 lua 入口）。管理器自己占用 boot 入口投放运行时，
	/// 不依赖任何第三方加载器；支持热切换。
	/// </summary>
	ExclusiveLuaEntry = 0,

	/// <summary>
	/// 防污染兼容模式（外置 lua 入口兼容）。运行时作为补丁交给外置加载器加载，
	/// 管理器不碰 boot；支持热切换。
	/// </summary>
	ExternalLuaEntry = 1,

	/// <summary>
	/// 真映射模式。不投放任何 Lua 运行时，直接把件表写进注册表数据。
	/// 改动需要重启游戏才生效。
	/// </summary>
	TrueMapping = 2,
}

public static class ArmorMappingModes
{
	public const ArmorMappingMode Default = ArmorMappingMode.ExclusiveLuaEntry;

	/// <summary>该模式是否需要投放 Lua 运行时。</summary>
	public static bool UsesRuntimeLua(this ArmorMappingMode mode)
		=> mode is ArmorMappingMode.ExclusiveLuaEntry or ArmorMappingMode.ExternalLuaEntry;

	/// <summary>该模式是否支持游戏运行中切换映射。</summary>
	public static bool AllowsHotSwitch(this ArmorMappingMode mode) => mode.UsesRuntimeLua();

	public static string DisplayName(this ArmorMappingMode mode) => mode switch
	{
		ArmorMappingMode.ExclusiveLuaEntry => "防污染兼容模式（独占 lua 入口）（允许热切换）",
		ArmorMappingMode.ExternalLuaEntry => "防污染兼容模式（外置 lua 入口兼容）（允许热切换）",
		ArmorMappingMode.TrueMapping => "真映射模式（不允许热切换）",
		_ => mode.ToString(),
	};

	public static string Description(this ArmorMappingMode mode) => mode switch
	{
		ArmorMappingMode.ExclusiveLuaEntry =>
			"运行时以管理器自己的 patch 部署，由游戏自身的 Lua VM 加载，不需要第三方加载器。改动在游戏运行中即可生效。",
		ArmorMappingMode.ExternalLuaEntry =>
			"运行时作为普通补丁投放，交给外置 Lua 加载器加载，管理器不占用 boot 入口。改动在游戏运行中即可生效。",
		ArmorMappingMode.TrueMapping =>
			"直接把外观写进护甲注册表数据，不投放任何 Lua 运行时。显存占用最低，但每次改动映射都必须重启游戏。",
		_ => string.Empty,
	};

	/// <summary>把持久化的整数值收敛到合法模式；未知值回落到默认。</summary>
	public static ArmorMappingMode Normalize(int value)
		=> Enum.IsDefined(typeof(ArmorMappingMode), value) ? (ArmorMappingMode)value : Default;
}
