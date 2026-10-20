namespace HD2ModCore.Domain;

// 作用：标识 Mod 信息产品等级，避免使用含义不明确的“完整分析”请求。
// Purpose: Identifies a Mod information product without ambiguous full-analysis requests.
public enum ModInformationKind
{
	FileFacts,
	AssetInventory,
	ReferenceGraph,
	UnitVersion,
	AdvancedUnitAnalysis,
	Thumbnail,
	MaintenanceAnalysis,
	// 无节点事实（设计文档 §22）：身份是"游戏目录 + 来源指纹"，不属于任何一个 Mod 节点。
	// 追加在末尾以保持既有取值的数值不变。
	// Node-less facts: their identity is the game directory plus a source fingerprint, not a Mod node.
	// Appended at the end so existing numeric values are preserved.
	GameDataIndex,
	GameDataMapping,
	DeployedGraph,
	// 护甲覆盖视图（设计文档 §4 #23）：由上面三条游戏数据事实组合出的聚合视图，
	// 同样没有节点——身份是"要的是哪一批护甲归档"。
	// 追加在末尾以保持既有取值的数值不变。
	// The armor coverage view: an aggregate of the three game-data facts above, equally node-less.
	// Appended at the end so existing numeric values are preserved.
	ArmorCoverage,
	// 一批 unit 的 section 结构（衣柜编辑器与 section 映射表的共同来源）。
	// 同样无节点：section 结构属于资产，可能来自某个 Mod 的 patch，也可能只存在于原版 archive。
	// 追加在末尾以保持既有取值的数值不变。
	// Section structure for a batch of units; node-less for the same reason as ArmorCoverage.
	UnitSectionStructure,
}