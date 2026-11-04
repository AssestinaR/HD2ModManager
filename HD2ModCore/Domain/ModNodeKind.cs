namespace HD2ModCore.Domain;

// Purpose: Distinguishes deployable Mods from package options and external ReShade components.
public enum ModNodeKind
{
	Standard = 0,
	// 装饰包（decoration）。**2026-11-04 起已废弃**：它的能力被「分件模组」取代
	// （把 legacy Mod 压成 7 槽 × 身形的 section 组），生成入口（页面 / CLI / 服务）全部移除，
	// 导入也被明确拒绝。
	//
	// **为什么这个值还留着**：`library.json` 的 kind 是**字符串**序列化的
	// （`JsonStringEnumConverter` + camelCase），删掉成员会让旧库里 `"kind": "decoration"`
	// 直接反序列化失败 —— 那是"整库打不开"，比放弃一个已废弃的节点严重得多。
	// 所以保留数值与名字，只保留"读取时认出它并丢弃该节点"这一处处理；
	// 其余所有按装饰分支的行为代码都已删除。
	[Obsolete("装饰包已不再支持：能力被分件模组取代。此值仅用于读取旧库并丢弃这类节点。")]
	Decoration = 1,
	// A package option/sub-option. It is stored in the library, but is not a
	// profile member by itself; it is enabled as an attachment to its host.
	Option = 2,
	// A standalone external ReShade component. Its files are deployed to the
	// game's bin directory rather than to GameData.
	ReShade = 3,
}
