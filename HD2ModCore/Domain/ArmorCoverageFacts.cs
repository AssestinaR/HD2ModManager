namespace HD2ModCore.Domain;

// 作用：一件护甲的覆盖结论——它是否被某个 Mod 的 Unit 覆盖，以及是谁覆盖了它。
// Purpose: The coverage verdict for one armor: whether a Mod's Unit overrides it, and by whom.
//
// 两个赢家字段不是重复：
//   · UnitWinnerNodeId 只统计 Unit 类资产，用来回答"这件护甲的**模型**被换了吗"；
//   · CoverWinnerNodeId 在没有 Unit 赢家时退回到"任意资产的赢家"，用来回答"这件护甲的**外观**被谁改了"。
//   护甲页拿 CoverWinnerNodeId 去查覆盖者的图标，拿 HasEffectiveUnitOverride 去做筛选——
//   这与改造前逐条读浏览器覆盖层的判定完全一致（设计文档 §4 #23）。
public sealed record ArmorCoverageEntry(
	bool HasEffectiveUnitOverride,
	ModNodeId? UnitWinnerNodeId,
	ModNodeId? CoverWinnerNodeId);

// 作用：一批护甲的覆盖事实（设计文档 §4 #23 的 ArmorCoverage 视图）。
// Purpose: Coverage facts for a batch of armor archives (the ArmorCoverage view of design section 4 #23).
//
// 为什么它只带"被问到的那些归档"：调用方问的是护甲目录里的一批归档 id，
// 事实就只对这批 id 作答——问什么答什么，不让调用方从一大包里自己挑。
//
// 为什么它**不含**图标：图标是对赢家节点的库内查找（内存字典 + 父链），代价可忽略；
// 而把它塞进事实会让"换个图标"变成"覆盖事实重算"，与设计文档 §7 护栏 3
// （改图标 → ArmorCoverage 不重算）正好相反。图标由页面自己解析。
public sealed record ArmorCoverageFacts(
	string CoverageGeneration,
	IReadOnlyDictionary<string, ArmorCoverageEntry> Archives,
	IReadOnlyList<CoreIssue> Issues);
