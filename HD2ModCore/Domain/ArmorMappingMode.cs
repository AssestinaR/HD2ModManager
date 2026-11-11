namespace HD2ModCore.Domain;

/// <summary>
/// 护甲外观映射的落地方式。
///
/// <para>
/// 与 <see cref="PhysicsMode"/> **同一套三态**（关闭 / 独占 boot / 外置加载器兼容）。
/// 护甲映射曾经有第四档「真映射」（把件表直接写进游戏注册表数据），2026-11-11 停用：
/// 游戏改了注册表文件的校验方式，写过的文件一进游戏就闪退。详见
/// <c>docs/agent/dl-bin-write-freeze-20261111.md</c>。
/// </para>
///
/// <para>
/// <b>数值有意与 <see cref="PhysicsMode"/> 对齐</b>（0=关闭、1=独占、2=外置）：
/// 两个下拉在设置页里并排放，语义相同的档位应当有相同的编号，读日志和读设置文件时
/// 不用来回换算。旧版本存的 0/1 在新口径下分别变成「关闭」与「独占」——
/// 迁移在 <see cref="ArmorMappingModes.MigrateStoredValue"/> 里做，不靠猜。
/// </para>
/// </summary>
public enum ArmorMappingMode
{
	/// <summary>不启用外观映射。不投放任何护甲运行时，护甲保持原版外观。</summary>
	Off = 0,

	/// <summary>
	/// 独占 boot。管理器占用 boot 入口投放运行时，不依赖任何第三方加载器；支持热切换。
	/// 只有总开关「启用管理器内置 lua 类功能」打开时才可选。
	/// </summary>
	ExclusiveLuaEntry = 1,

	/// <summary>
	/// 外置加载器兼容。运行时作为补丁交给外置加载器加载，管理器不碰 boot；支持热切换。
	/// </summary>
	ExternalLuaEntry = 2,
}

public static class ArmorMappingModes
{
	/// <summary>
	/// 旧的持久化数值：真映射（已停用）。保留常量只为了在迁移时认得出来——
	/// 它不再是合法档位，读到时按 <see cref="Off"/> 处理。
	/// </summary>
	public const int LegacyTrueMappingValue = 3;

	/// <summary>
	/// 默认「关闭」。与物理一致：这两项都是"替用户改外观/加运行时"的功能，
	/// 默认不替用户开启，想用的人主动选。
	/// </summary>
	public const ArmorMappingMode Default = ArmorMappingMode.Off;

	/// <summary>该模式是否需要投放 Lua 运行时。</summary>
	public static bool UsesRuntimeLua(this ArmorMappingMode mode)
		=> mode is ArmorMappingMode.ExclusiveLuaEntry or ArmorMappingMode.ExternalLuaEntry;

	/// <summary>该模式是否需要**占用 boot**（只有独占入口需要）。</summary>
	public static bool OccupiesBoot(this ArmorMappingMode mode)
		=> mode is ArmorMappingMode.ExclusiveLuaEntry;

	/// <summary>该模式是否支持游戏运行中切换映射。</summary>
	public static bool AllowsHotSwitch(this ArmorMappingMode mode) => mode.UsesRuntimeLua();

	public static string DisplayName(this ArmorMappingMode mode) => mode switch
	{
		ArmorMappingMode.ExclusiveLuaEntry => "独占 boot",
		ArmorMappingMode.ExternalLuaEntry => "外置加载器兼容",
		ArmorMappingMode.Off => "关闭",
		_ => mode.ToString(),
	};

	public static string Description(this ArmorMappingMode mode) => mode switch
	{
		ArmorMappingMode.ExclusiveLuaEntry =>
			"外观映射的运行时合并进管理器自己的启动补丁，由游戏自身的 Lua VM 加载，不需要第三方加载器。改动在游戏运行中即可生效。",
		ArmorMappingMode.ExternalLuaEntry =>
			"外观映射的运行时作为普通补丁投放，交给外置 Lua 加载器加载，管理器不占用 boot 入口。改动在游戏运行中即可生效。",
		ArmorMappingMode.Off =>
			"不投放任何外观映射运行时，护甲保持原版外观。",
		_ => string.Empty,
	};

	/// <summary>
	/// 把旧版本存下的数值翻译成新口径。**升级时唯一需要小心的地方。**
	///
	/// <para>
	/// 旧口径：<c>0 = 独占 boot</c>、<c>1 = 外置加载器兼容</c>、<c>2 = 真映射</c>。
	/// 新口径：<c>0 = 关闭</c>、<c>1 = 独占 boot</c>、<c>2 = 外置加载器兼容</c>。
	/// 两者数值重叠但含义完全不同——直接把旧值当新值读，会把"开着独占"的用户
	/// 悄悄变成"关闭"（本机就踩过：<c>settings.json</c> 里存的 0 会被读成关闭）。
	/// </para>
	///
	/// <para>
	/// 判据用存档里同步维护的 <c>MappingModeSchema</c> 版本号，而不是猜数值：
	/// 写设置时一并写版本，读到没有版本号的（旧档）才走翻译。
	/// </para>
	/// </summary>
	public static ArmorMappingMode MigrateStoredValue(int stored, int schema)
	{
		if (schema >= CurrentSchema) return FromStoredValue(stored);

		// 旧口径。2 及以上的真映射一律落到「关闭」：那一档已经不可用，
		// 而"关闭"是它最接近的替代（都不投放运行时）。
		return stored switch
		{
			0 => ArmorMappingMode.ExclusiveLuaEntry,
			1 => ArmorMappingMode.ExternalLuaEntry,
			_ => ArmorMappingMode.Off,
		};
	}

	/// <summary>
	/// 当前设置的存储口径版本。改枚举数值时必须加一，否则旧档会被当新档读。
	/// </summary>
	public const int CurrentSchema = 2;

	/// <summary>
	/// 真映射（写 `generated_customization_armor_sets.dl_bin`）是否仍然允许。
	///
	/// <para>
	/// <b>2026-11-11 起为 <c>false</c>，且不再是可选项。</b>游戏在这一版改了该文件的校验方式：
	/// 文件仍可正常解密、格式未变、写进去也不报错，但**写过的文件一进游戏就闪退**。
	/// 换成"先校验再决定写不写"没有意义——校验能过，进游戏仍然会崩。
	/// 详见 <c>docs/agent/dl-bin-write-freeze-20261111.md</c>。
	/// </para>
	/// <para>
	/// 恢复条件：弄清游戏新的校验方式（或官方回退这次改动），使伪造方案重新成立。
	/// </para>
	/// </summary>
	public static readonly bool TrueMappingWritable = false;

	/// <summary>
	/// 仅供测试：临时放开写入，用来验证"写入被允许时"的编译逻辑仍然正确。
	/// <para>
	/// 理由：编译算法（件表改写、校验和伪造、对齐）有二十多个用例，必须走真实写盘路径
	/// 才能证明自己没坏；停写之后那条路径在正常配置下走不到。
	/// </para>
	/// <para><b>生产代码永远不要使用它。</b>用计数而非布尔：xUnit 会并行跑不同测试类。</para>
	/// </summary>
	internal static void PushAllowWriteForTests() => Interlocked.Increment(ref _allowWriteForTestsCount);

	internal static void PopAllowWriteForTests()
	{
		if (Interlocked.Decrement(ref _allowWriteForTestsCount) < 0)
			Interlocked.Exchange(ref _allowWriteForTestsCount, 0);
	}

	static int _allowWriteForTestsCount;

	/// <summary>这一层此刻是否允许写注册表数据（生产判定一律用它，别直接读字段）。</summary>
	internal static bool CanWriteRegistryData
		=> TrueMappingWritable || Volatile.Read(ref _allowWriteForTestsCount) > 0;

	/// <summary>把持久化的整数值收敛到合法档位；未知值回落到默认。</summary>
	public static ArmorMappingMode FromStoredValue(int value)
		=> Enum.IsDefined(typeof(ArmorMappingMode), value) ? (ArmorMappingMode)value : Default;

	/// <summary>
	/// 当前可选的档位。**总开关关闭时不提供「独占 boot」**——管理器不占 boot，
	/// 那一档在物理上就无法实现，所以直接从选项里去掉，而不是让用户选完发现无效。
	/// </summary>
	public static IReadOnlyList<ArmorMappingMode> AvailableFor(bool builtInLuaEnabled)
		=> builtInLuaEnabled
			? [ArmorMappingMode.ExclusiveLuaEntry, ArmorMappingMode.ExternalLuaEntry, ArmorMappingMode.Off]
			: [ArmorMappingMode.ExternalLuaEntry, ArmorMappingMode.Off];

	/// <summary>
	/// 把持久化的整数值收敛到合法档位，并考虑总开关。
	/// 总开关关闭时「独占」收敛到「外置」——保留"用运行时"这层意图，
	/// 而不是掉到「关闭」把功能悄悄关掉。
	/// </summary>
	public static ArmorMappingMode Normalize(int value, bool builtInLuaEnabled)
	{
		var mode = FromStoredValue(value);
		if (!builtInLuaEnabled && mode == ArmorMappingMode.ExclusiveLuaEntry)
			return ArmorMappingMode.ExternalLuaEntry;
		return mode;
	}
}
