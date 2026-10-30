namespace HD2ModCore.Domain;

// 组件开关（`components.json`）的领域模型。
//
// 它回答一个问题：**这个 Mod 有哪些"关掉某一块"的开关，每个开关关掉的是哪几块几何。**
//
// 为什么是独立文件而不是塞进 sections.json：
//   · sections.json 描述"这个 patch 里有什么"（构建产物，偏移与指纹），
//   · components.json 描述"用户能关什么"（源侧意图，跨 patch 汇总）。
// 两者的失效条件不同：重建 patch 会让前者全变、后者大多不变。混在一起会让"只想改开关名字"
// 变成"重写整份偏移表"。
//
// 为什么掩码存 (unit, section 下标) 而不是 section 身份：
//   这份文件是**刚转换完就写**的，那时 sections.json 就在旁边，身份与下标一一对应；
//   而运行时/烘焙只需要下标（numIndices 的落点由 sections.json 提供）。存身份要多一次对齐，
//   换来的能力（跨重建复用）本来也不成立——重建后几何全变，开关必须重转。
//
// 名字是源侧给的中文标注（`猫耳`、`披风`），它只用于显示；匹配与去重全部按掩码。

/// <summary>
/// 一个开关关掉的 section 集合。<c>Unit</c> 是 16 位小写 hex（与 <c>sections.json</c> 同口径）。
/// </summary>
public sealed record VpkComponentSwitchTarget(string Unit, IReadOnlyList<int> Sections);

/// <summary>
/// 一个开关：名字 + 它隐藏的源材质 + 落到哪些 section。
/// <para>
/// <see cref="Materials"/> 是源侧材质裸名，保留它是为了"这个开关凭什么关掉这块"可回答——
/// 用户报"关了猫耳结果耳朵还在"时，要先看材质有没有对上。
/// </para>
/// </summary>
public sealed record VpkComponentSwitchEntry(
	string Name,
	string SourceVpk,
	IReadOnlyList<string> Materials,
	IReadOnlyList<VpkComponentSwitchTarget> Disabled)
{
	/// <summary>这个开关实际关掉多少块几何。0 表示没匹配上，界面不该把它画成可用开关。</summary>
	public int SectionCount => Disabled.Sum(target => target.Sections.Count);
}

/// <summary>
/// 识别到、但没能落成开关的包（材质在 patch 里找不到）。
/// <para>
/// <b>必须报出来。</b>用户装了"猫耳关"却看不到开关，只会以为功能没做；
/// 说清"它藏的是 <c>xxx</c>，这个 patch 里没有这个材质"才是可照做的信息。
/// </para>
/// </summary>
public sealed record VpkComponentSwitchUnmatched(string Name, string SourceVpk, string Reason);

/// <summary><c>components.json</c> 的内存表示。<see cref="Schema"/> 是版本闸门。</summary>
public sealed record VpkComponentSwitchDocument(
	string Schema,
	IReadOnlyList<VpkComponentSwitchEntry> Switches,
	IReadOnlyList<VpkComponentSwitchUnmatched> Unmatched)
{
	/// <summary>当前格式标识。格式变了就换字符串，让旧文件显式失效而不是被误读成"零个开关"。</summary>
	public const string CurrentSchema = "hd2mod.vpk-components/1";

	public static VpkComponentSwitchDocument Empty { get; } = new(CurrentSchema, [], []);
}
