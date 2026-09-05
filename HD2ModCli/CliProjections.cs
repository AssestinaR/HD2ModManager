using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public static class CliProjections
{
	public const string ProtocolVersion = "1.26";

	public static object CreateCapabilities()
		=> new
		{
			protocolVersion = ProtocolVersion,
			command = "hd2mod",
			mode = "plan-gated-write",
			supports = new[]
			{
				new { name = "version", safety = "read-only", description = "读取 CLI 与协议版本" },
				new { name = "capabilities", safety = "read-only", description = "读取当前可用命令与安全边界" },
				new { name = "status", safety = "read-only", description = "读取管理器、Mod 库与 GameData 路径状态" },
				new { name = "assets metadata inspect", safety = "read-only", description = "读取设置页使用的在线资产元数据缓存、manifest 与有效远端来源" },
				new { name = "assets metadata plan", safety = "explicit-export-network", description = "下载并验证在线资产元数据到计划旁暂存区，导出固定内容哈希的提交计划" },
				new { name = "assets metadata validate", safety = "read-only", description = "验证管理器设置、本地缓存与计划旁暂存的远端内容均未漂移；不会联网" },
				new { name = "assets metadata apply", safety = "explicit-confirm", description = "仅将验证通过的已暂存元数据提交到 Manager resources，并原子记录最后检查时间" },
				new { name = "gamedata status", safety = "read-only", description = "读取指定 GameData 目录的可读性与原版 Archive 索引状态；不会重建索引" },
				new { name = "gamedata archive list", safety = "read-only", description = "分页读取持久化原版 Archive 索引；AssetKey 映射仅在 index.mappingTrusted=true 时可信" },
				new { name = "gamedata archive show", safety = "read-only", description = "分页读取一个 Archive 的安全十六进制 AssetKey；可用 --with-effective 附加当前 Data-Patch 胜者" },
				new { name = "gamedata asset find", safety = "read-only", description = "用明确的十六进制 TypeID/FileID 查询原版 Archive 映射；不会扫描 Mod 库或部署" },
				new { name = "gamedata override status", safety = "read-only", description = "只读取当前 Data 顶层 Patch 与 activation-state 的实际覆盖图；独立于资产索引" },
				new { name = "gamedata override list", safety = "read-only", description = "分页读取实际 Data-Patch AssetKey 覆盖链，可仅查看竞争链" },
				new { name = "gamedata override show", safety = "read-only", description = "读取一个明确 AssetKey 的实际 Data-Patch 覆盖链与赢家，不输出 64 位 JSON 数字" },
				new { name = "gamedata browser", safety = "read-only-expensive", description = "显式构建 UI 对照的 Archive + 库 + 活动配置 + 实际赢家全景；直读 Patch，不写派生缓存" },
				new { name = "gamedata index inspect", safety = "read-only", description = "检查基础资产索引、archivehashes 与 Armor / Helmet Unit 部位事实的当前状态" },
				new { name = "gamedata index plan", safety = "explicit-export", description = "导出 rebuild、units-all 或 units-missing 的 GameData 索引维护计划" },
				new { name = "gamedata index validate", safety = "read-only", description = "重新核验 GameData、可选 archivehashes 和现有索引状态，拒绝任何漂移" },
				new { name = "gamedata index apply", safety = "explicit-confirm", description = "仅重建管理器拥有的基础索引或 Armor / Helmet Unit 部位事实；不写 GameData 或 Mod 库" },
				new { name = "library sync inspect", safety = "read-only", description = "检查已登记 Mod 与库目录实际 Patch 的差异，不写入任何文件" },
				new { name = "library sync plan", safety = "explicit-export", description = "导出固定新增 Mod ID 与输入指纹的库对账计划" },
				new { name = "library sync validate", safety = "read-only", description = "重扫库目录并拒绝计划、Patch 或待删除状态漂移" },
				new { name = "library sync apply", safety = "explicit-confirm", description = "仅按验证通过的计划更新 library.json 的节点 Patch 记录；不导入、删除或部署" },
				new { name = "decoration inspect source", safety = "read-only", description = "读取一个普通/选项 Mod 的可选装饰 Unit、默认选择和建议附着策略；不写缓存或包" },
				new { name = "decoration show", safety = "read-only", description = "读取一个已导入装饰包的来源、附着策略、声明主体与当前启用状态" },
				new { name = "decoration package plan", safety = "explicit-export", description = "导出固定来源/目标、Unit 选择和 Patch 家族指纹的外部装饰包计划" },
				new { name = "decoration package validate", safety = "read-only", description = "重建来源候选并拒绝库、Patch、sidecar、目标或附件策略漂移" },
				new { name = "decoration package apply", safety = "explicit-confirm", description = "仅发布新的库外且游戏目录外装饰包；不导入、启用、部署或重建 Overwrite" },
				new { name = "decoration edit export", safety = "explicit-export", description = "从已导入的 PatchSnapshot 装饰包导出可编辑 Unit 选择与附件策略规格" },
				new { name = "decoration edit inspect", safety = "read-only", description = "从编辑规格重建候选、目标主体、启用主体和回建影响；不写入任何文件" },
				new { name = "decoration edit plan", safety = "explicit-export", description = "导出冻结 decoration.json、来源快照、启用状态、主体 Overwrite 与 GameData 索引输入的编辑计划" },
				new { name = "decoration edit validate", safety = "read-only", description = "重建装饰编辑计划并拒绝来源、附件、启用状态、Overwrite 或 GameData 漂移" },
				new { name = "decoration edit apply", safety = "explicit-confirm", description = "原子更新已导入装饰定义、回建所有已启用主体，并仅清理因移除目标而失效的启用关联" },
				new { name = "mod export plan", safety = "explicit-export", description = "冻结一个 Mod 及其递归选项树、库记录和源文件，导出可审阅的压缩包计划" },
				new { name = "mod export validate", safety = "read-only", description = "重新核验 Mod 树、library.json 和源文件内容是否仍与导出计划一致" },
				new { name = "mod export apply", safety = "explicit-confirm", description = "将验证通过的 Mod 树输出为带 manifest.json 的压缩包，不修改库或部署" },
				new { name = "mod list", safety = "read-only", description = "列出已登记 Mod" },
				new { name = "mod show", safety = "read-only", description = "读取指定 Mod、选项关系和激活状态" },
				new { name = "mod analyze", safety = "read-only-expensive", description = "直接使用 PatchGroupAnalyzer 深度读取一个 Mod 的 Patch 组、条目 AssetKey、引用、Unit/几何摘要和解析诊断；不写缓存或 SQLite" },
				new { name = "mod external inspect", safety = "read-only", description = "直接检查一个 Mod 当前可安全部署的 ReShade add-on/预设、未识别候选和同 Mod 目标冲突；不写缓存或 SQLite" },
				new { name = "mod image show", safety = "read-only", description = "读取一个 Mod 目录中按 icon.* 约定解析的图像状态" },
				new { name = "mod image set", safety = "explicit-confirm", description = "以来源图像原子替换一个 Mod 的规范 icon.* 文件；不修改 Patch、配置或部署" },
				new { name = "mod image clear", safety = "explicit-confirm", description = "只移除一个 Mod 的规范 icon.* 文件，不删除作者使用的其他图片资源" },
				new { name = "mod create", safety = "explicit-confirm", description = "创建一个无 Patch 的逻辑 Mod 容器，可加入 Profile 并作为 Option 主体" },
				new { name = "mod rename", safety = "explicit-confirm", description = "在持有库写租约且桌面端关闭时重命名一个 Mod" },
				new { name = "mod notes set", safety = "explicit-confirm", description = "在持有库写租约且桌面端关闭时设置一个 Mod 的备注" },
				new { name = "mod notes clear", safety = "explicit-confirm", description = "在持有库写租约且桌面端关闭时清空一个 Mod 的备注" },
				new { name = "mod convert-to-option", safety = "explicit-confirm", description = "将一个普通或共享 Mod 添加为明确主体的 Option；会从全部 Profile 释放该 Mod" },
				new { name = "mod delete preview", safety = "read-only", description = "兼容快捷预览：读取关系感知的安全删除后果与阻断项" },
				new { name = "mod delete plan", safety = "explicit-export", description = "导出冻结库、激活状态与待删目录内容指纹的可审计删除计划" },
				new { name = "mod delete validate", safety = "read-only", description = "重建删除关系和存储指纹，拒绝任何目标或内容漂移" },
				new { name = "mod delete apply", safety = "explicit-confirm", description = "仅在计划复核与库租约内执行关系感知删除；永不自动部署" },
				new { name = "mod delete", safety = "explicit-confirm", description = "兼容直接删除入口；Agent 应优先使用 plan/validate/apply" },
				new { name = "option enable", safety = "explicit-confirm", description = "为明确指定的主体启用一个直属 Option" },
				new { name = "option disable", safety = "explicit-confirm", description = "为明确指定的主体禁用一个直属 Option" },
				new { name = "option show", safety = "read-only", description = "检查一个 Option 的所有声明主体、激活状态与阻断原因" },
				new { name = "option enable-all", safety = "explicit-confirm", description = "仅在全部声明主体均为有效直属关系时，为一个 Option 启用所有主体范围" },
				new { name = "option disable-all", safety = "explicit-confirm", description = "仅在全部声明主体均为有效直属关系时，为一个 Option 禁用所有主体范围" },
				new { name = "option move", safety = "explicit-confirm", description = "在明确主体的直属 Option 范围内重排一个或多个 Option" },
				new { name = "option insert", safety = "explicit-confirm", description = "option move 的语法别名；不会把 Option 挂到新主体" },
				new { name = "option parent add", safety = "explicit-confirm", description = "把一个或多个普通/Option Mod 添加到明确主体下；支持一个 Option 拥有多个父级" },
				new { name = "option parent remove", safety = "explicit-confirm", description = "从明确主体移除一个或多个直属 Option；最后一个父级移除后恢复为普通 Mod" },
				new { name = "profile list", safety = "read-only", description = "列出配置文件" },
				new { name = "profile show", safety = "read-only", description = "读取逻辑顺序与实际部署展开顺序" },
				new { name = "profile create", safety = "explicit-confirm", description = "创建空配置文件，可选择设为活动配置" },
				new { name = "profile delete", safety = "explicit-confirm", description = "删除一个配置文件；不会自动修改 GameData" },
				new { name = "profile rename", safety = "explicit-confirm", description = "重命名一个配置文件" },
				new { name = "profile set-active", safety = "explicit-confirm", description = "设置活动配置；不会自动部署" },
				new { name = "profile clear-active", safety = "explicit-confirm", description = "清除活动配置；不会自动部署" },
				new { name = "profile add", safety = "explicit-confirm", description = "将可部署 Standard 或 ReShade Mod 追加到一个明确配置" },
				new { name = "profile remove", safety = "explicit-confirm", description = "从一个明确配置移除 Mod 条目" },
				new { name = "profile insert", safety = "explicit-confirm", description = "按明确逻辑槽位插入新的配置条目" },
				new { name = "profile move", safety = "explicit-confirm", description = "按明确逻辑槽位稳定移动已有配置条目" },
				new { name = "profile reorder", safety = "explicit-confirm", description = "以完整明确列表重排一个配置的逻辑条目" },
				new { name = "profile align export", safety = "explicit-export", description = "从一个现有 Profile 导出可编辑的完整逻辑成员与活动状态规格" },
				new { name = "profile align inspect", safety = "read-only", description = "以 Core 的完整成员替换规则检查 Profile 对齐规格，不写入任何状态" },
				new { name = "profile align plan", safety = "explicit-export", description = "导出冻结目标 Profile 版本、成员顺序、活动状态与参与 Mod 身份的对齐计划" },
				new { name = "profile align validate", safety = "read-only", description = "重新核验 Profile 对齐计划，区分可写、已对齐和漂移状态" },
				new { name = "profile align apply", safety = "explicit-confirm", description = "在库写租约内二次验证后仅更新 profiles.json 的完整成员顺序和可审阅活动状态" },
				new { name = "deploy plan", safety = "explicit-export", description = "只读重建配置、选项、Patch、Lua 和外部文件映射，并导出可审计部署计划" },
				new { name = "deploy validate", safety = "read-only", description = "重建部署计划并检查目标、归属、来源漂移和游戏进程，不写入任何文件" },
				new { name = "deploy apply", safety = "explicit-confirm", description = "在库与 GameData 双租约内重验计划、编译但不执行 Lua、精确替换已证明归属的 Patch" },
				new { name = "deploy deactivate plan", safety = "explicit-export", description = "导出仅清理当前 activation-state 完整证明归属的 Patch 和外部文件的停用计划" },
				new { name = "deploy deactivate validate", safety = "read-only", description = "重新验证停用计划、文件归属和游戏进程，不写入任何文件" },
				new { name = "deploy deactivate apply", safety = "explicit-confirm", description = "在双租约内精确停用已管理部署；不会改动活动配置或未登记 Patch" },
				new { name = "patch repackage inspect", safety = "read-only", description = "读取指定 Mod 的 Patch 组与整条 AssetKey Payload 候选，不写入来源或 GameData" },
				new { name = "patch repackage plan", safety = "explicit-export", description = "导出可审阅的 AssetKey 资源重打包计划；默认仅选择无冲突候选" },
				new { name = "patch repackage validate", safety = "read-only", description = "重新扫描来源 Patch、验证选择、Payload 与 TOC 指纹，不写入文件" },
				new { name = "patch repackage apply", safety = "explicit-confirm", description = "仅将经验证的完整 AssetKey 资源写入新的独立输出目录；不修改 Mod 库或 GameData" },
				new { name = "equipment helmet-merge inspect", safety = "read-only", description = "检查一个或多个来源 Mod 中完整的 Armor/Any Helmet 集合与共享 Unit 影响；不写入来源、缓存或 GameData" },
				new { name = "equipment helmet-merge plan", safety = "explicit-export", description = "导出固定来源 Patch、Unit/Mesh、共享影响与 GameData 索引指纹的头盔合并计划" },
				new { name = "equipment helmet-merge validate", safety = "read-only", description = "重建来源与候选，拒绝漂移、冲突或胸部/待隐藏头盔 Unit 重叠；不写入文件" },
				new { name = "equipment helmet-merge apply", safety = "explicit-confirm", description = "仅将验证通过的头盔追加到胸部并生成隐藏头盔 Unit，发布到新的库外且游戏目录外输出；不修改来源或部署" },
				new { name = "equipment cross-armor inspect", safety = "read-only", description = "读取一个来源 Mod 的装备 Unit 事实、唯一 Patch 资格与全部目标装备目录；不写入缓存或 GameData" },
				new { name = "equipment cross-armor plan", safety = "explicit-export", description = "导出可审阅的跨护甲意图、来源/目标 Unit 事实、映射、隐藏项与 GameData 索引指纹" },
				new { name = "equipment cross-armor validate", safety = "read-only", description = "从 request 重建 Core 跨护甲计划，拒绝来源、目标、映射或 GameData 漂移；不写入文件" },
				new { name = "equipment cross-armor apply", safety = "explicit-confirm", description = "仅将验证通过的 Direct-source Unit 跨护甲候选写入新的库外且游戏目录外目录；不修改来源、配置或部署" },
				new { name = "host-derived inspect", safety = "read-only", description = "检查一个或多个主体当前装饰、临时头盔合并和 Overwrite 所有权；不写入任何文件" },
				new { name = "host-derived plan decoration", safety = "explicit-export", description = "导出冻结装饰启停、主体、Patch、Overwrite 与 GameData 索引输入的主体派生计划" },
				new { name = "host-derived plan helmet", safety = "explicit-export", description = "导出冻结临时头盔合并状态、主体、Patch、Overwrite 与 GameData 索引输入的主体派生计划" },
				new { name = "host-derived plan rebuild", safety = "explicit-export", description = "导出只重建当前派生 Overwrite、不会改变任何状态文件的计划" },
				new { name = "host-derived validate", safety = "read-only", description = "重建主体派生计划并拒绝状态、Patch、Overwrite 或 GameData 索引漂移" },
				new { name = "host-derived apply", safety = "explicit-confirm", description = "在库写租约内先重建全部主体 Overwrite，再提交装饰或头盔状态；失败时回建已完成主体" },
				new { name = "patch repair batch inspect", safety = "read-only-expensive", description = "扫描可选范围或整个库的 Unit 版本、来源 Patch 指纹与 Canonical 修复资格；不写入来源、缓存或 GameData" },
				new { name = "patch repair batch plan", safety = "explicit-export", description = "导出冻结 Mod 范围、来源 Patch、Unit 版本与 GameData 索引指纹的批量原地修复计划；仅 selected 可编辑" },
				new { name = "patch repair batch validate", safety = "read-only", description = "重新检查批量修复计划的库、Patch、Unit 版本和 GameData 索引漂移；不写入文件" },
				new { name = "patch repair batch apply", safety = "explicit-confirm", description = "在库写租约内备份并原地替换计划中已选择的过时 Mod Patch，随后刷新对应库指纹并回建活动管理器 Overwrite" },
				new { name = "patch repair inspect", safety = "read-only", description = "检查一个明确来源 Patch 的 Same-key Canonical 重构资格、Unit 目标和 GameData 索引，不写入来源或缓存" },
				new { name = "patch repair plan", safety = "explicit-export", description = "导出固定 Mod、来源 Patch 与 GameData 指纹的 Same-key 重构计划" },
				new { name = "patch repair validate", safety = "read-only", description = "重新读取来源、GameData 指纹与 Canonical 重构资格，不写入文件" },
				new { name = "patch repair apply", safety = "explicit-confirm", description = "只将已验证的 Same-key Canonical 候选输出到新的库外目录；不替换、导入或部署" },
				new { name = "import batch inspect", safety = "read-only", description = "依次扫描多个导入来源并输出不会写库的批量导入预览" },
				new { name = "import batch plan", safety = "explicit-export", description = "导出逐来源冻结指纹的串行批量导入计划；可固定导入后一次加入 Profile" },
				new { name = "import batch validate", safety = "read-only", description = "重新验证每个批量来源、目标库与可选 Profile 目标" },
				new { name = "import batch apply", safety = "explicit-confirm", description = "在一个库租约下串行执行已验证来源；已完成来源不会被伪装为自动回滚" },
				new { name = "import inspect", safety = "read-only", description = "扫描待导入文件夹或压缩包，并输出导入前规划，不写入 Mod 库" },
				new { name = "import plan", safety = "explicit-export", description = "仅在给定 --output 后写出带来源/导入树指纹的可审计导入计划；可显式冻结导入后加入的 Profile" },
				new { name = "import validate", safety = "read-only", description = "复扫来源并检查目标库、计划指纹和管理器进程；不写入 Mod 库" },
				new { name = "import apply", safety = "explicit-confirm", description = "仅在 validate 通过且传入 --confirm 后导入 Mod 库；只会执行计划中明确冻结的 Profile 追加，永不部署" },
				new { name = "context export", safety = "explicit-export", description = "仅在给定 --output 后写出 AI 上下文 JSON" },
				new { name = "agent skill list", safety = "read-only", description = "兼容别名：列出可按需导出的能力 Skill" },
				new { name = "agent skill export", safety = "explicit-export", description = "兼容单文件导出：仅在给定 --output 后导出指定能力 Skill" },
				new { name = "agent skills list", safety = "read-only", description = "列出可安装的能力 Skill 目录" },
				new { name = "agent skills export", safety = "explicit-export", description = "仅在给定 --output 后导出一个或全部独立能力 Skill 目录与 SHA-256 清单" },
				new { name = "agent skill", safety = "explicit-export", description = "兼容旧命令：导出轻量 Agent Skill 路由器" },
			},
			notSupportedYet = new[]
			{
				"基于桌面设置的隐式自动导入，以及任何自定义 Patch 字节编辑工作流。",
			},
			workflow = new[] { "inspect", "plan", "validate", "apply" },
			safetyRules = new[]
			{
				"import apply 只会在计划复核成功、目标库可信、管理器未运行且显式 --confirm 时写入 Mod 库。",
				"import apply 不自动部署、不写 Patch 字节，也不会启动昂贵的派生信息分析；仅当 import plan 明确带有 --add-to-profile 时，才会在 Profile 身份与版本未漂移的前提下追加可部署条目。",
				"mod analyze 直接调用 Adaptation 的 PatchGroupAnalyzer，不使用 ModInformationCenter、持久化 Mod 信息缓存或 SQLite；它会返回结构化部分结果与解析诊断，但绝不写入状态。",
				"mod external inspect 直接扫描当前 Mod 目录中受支持的 ReShade add-on/预设映射；未识别外部文件永不自动部署，跨 Mod 冲突必须由 deploy plan 审阅。",
				"Mod 元数据与 Option 写入同样要求 --confirm、已有 library.json、所有桌面端实例关闭，以及库根目录的跨进程写租约。",
				"Option 单主体启停和排序必须显式指定 --host；option enable-all/disable-all 只会在所有声明主体均存在、类型正确且为直属关系时整批执行，绝不猜测或部分写入。",
				"library sync 只对账已存在的库目录与 library.json：它不会解析导入包树、不移动或删除文件、不改 Profile/激活状态，也会在已登记物理目录缺失时阻断。",
				"decoration package 只会从已验证的来源 Patch 家族复制选中 Unit 所在的完整 Patch 文件与 sidecar，并发布一个新的外部可导入包；它不会写 Mod 库、激活状态、Overwrite 或 GameData。",
				"decoration edit 只支持管理器当前 PatchSnapshot 装饰格式：apply 在库写租约内先更新 decoration.json、回建每个当前启用主体，再清理被移出目标列表的对应启用关联；任何一步失败都会恢复原定义和已完成主体。",
				"mod export 只从已登记 Mod 库读取一个根节点及其递归子树；apply 先在库租约内冻结并复核源文件，写入临时压缩包后再发布到库外目录，不会导出或覆盖库内内容。",
				"Profile 写入要求 --confirm，且只更新 Mod 库中的配置状态；永不隐式部署或触碰 GameData。profile align 额外冻结当前/目标语义指纹，在租约内二次验证，并且仅会写入 profiles.json。",
				"deploy plan / validate 完全只读；deploy apply 要求 --confirm、桌面端和游戏均已退出、库与 GameData 双租约，以及 activation-state.json 对现有 Patch 的完整归属证明。",
				"CLI 不会接管、清空或覆盖未登记 Patch；部署失败时仅恢复本次事务移动的已证明管理器 Patch。",
				"CLI 物化 Lua 时仅做编译验证，不在本地 Lua 运行时执行 Mod Lua。",
				"patch repackage 只允许在计划中选择已有完整 AssetKey Payload；apply 只写入新的 Mod 库外目录，绝不修改来源、自动导入或部署。",
				"equipment helmet-merge 只允许从已检查的完整 Armor/Any Helmet 集合中选择；apply 会重新验证来源、GameData 索引、共享影响和 Unit 冲突，并且只发布新的库外、游戏根外目录。",
				"equipment cross-armor 固定使用桌面版的 Direct-source Unit 生产路径；一个来源 Mod 必须恰有一个 base Patch，Agent 只能在计划 request 中选择已列出的 ArchiveId/AssetKey，apply 会双重验证并发布到新的外部目录。",
				"host-derived apply 只写入已验证主体库目录下的管理器 Overwrite 与对应的一个状态文件；它不触碰 GameData，且绝不覆盖或删除没有管理器标记的 Overwrite。",
				"patch repair batch 只会处理计划冻结范围内仍确认过时、且 selected=true 的 Mod；apply 在库写租约内再次验证所有来源与 GameData 索引，每个 Mod 先备份、候选失败自动恢复，成功后只刷新实际修复节点的 library.json 指纹并回建活动管理器 Overwrite。",
				"gamedata 的检查命令全部只读：绝不调用索引重建、绝不写 Mod facts 或 GameData；Archive 映射的可信度与 Data-Patch 覆盖图的可信度分别报告，不能互相替代。",
				"assets metadata plan 是唯一允许联网的计划操作：它把已验证的 hashlist 固定到计划同级暂存目录；validate/apply 永不重新下载，apply 要求 Manager 已关闭、Manager data 独占锁和 --confirm，失败会恢复本地 metadata 与 settings.json。",
				"gamedata index 是唯一的例外：它必须使用 plan → validate → apply --confirm，要求 Manager 已关闭并在 Manager data 目录持有独占锁；它只写管理器拥有的索引/隐藏 Unit 缓存，绝不修改 GameData 或 Mod 库。",
				"patch repair 只允许 Core 的固定 Same-key Canonical 重构配方；CLI 必须固定一个来源 Patch 并重验来源/当前 GameData 指纹，绝不替换库内文件。",
				"删除计划会冻结关系、Profile/激活状态和待删目录内容；validate/apply 重新确认这些输入，遇到活跃装饰、临时头盔合并或任何漂移都会拒绝猜测性删除。",
			},
		};

	public static object CreateStatus(CliWorkspace workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var libraryPath = string.IsNullOrWhiteSpace(workspace.LibraryRoot)
			? null
			: Path.Combine(workspace.LibraryRoot, "library.json");
		return new
		{
			protocolVersion = ProtocolVersion,
			mode = "read-only",
			manager = new
			{
				root = workspace.ManagerRoot,
				settingsPath = workspace.SettingsPath,
				settingsExists = workspace.SettingsPath is not null && File.Exists(workspace.SettingsPath),
				selectedProfileKey = workspace.Settings?.SelectedProfileKey,
			},
			library = new
			{
				root = workspace.LibraryRoot,
				libraryPath,
				directoryExists = workspace.LibraryRoot is not null && Directory.Exists(workspace.LibraryRoot),
				librarySnapshotExists = libraryPath is not null && File.Exists(libraryPath),
			},
			gameData = new
			{
				root = workspace.GameDataRoot,
				directoryExists = workspace.GameDataRoot is not null && Directory.Exists(workspace.GameDataRoot),
			},
			issues = workspace.Issues,
		};
	}

	public static object CreateImportPlanProjection(ImportPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return new
		{
			sourcePath = plan.SourcePath,
			displayName = plan.DisplayName,
			mode = plan.Mode.ToString().ToLowerInvariant(),
			requiresDerivedInformation = plan.RequiresDerivedInformation,
			items = plan.Items.Select(item => new
			{
				sourcePath = item.SourcePath,
				displayName = item.DisplayName,
				role = item.Role.ToString().ToLowerInvariant(),
				relativePath = item.RelativePath,
				patchGroups = item.PatchGroups.Select(group => new
				{
					archiveHex16 = group.ArchiveHex16,
					patchIndex = group.PatchIndex,
				}).ToArray(),
				isTemporarySource = item.IsTemporarySource,
				plannedFiles = item.PlannedFiles ?? Array.Empty<string>(),
				diagnostics = ProjectImportDiagnostics(item.Diagnostics),
				planNodeKey = item.PlanNodeKey,
				parentPlanNodeKeys = item.ParentPlanNodeKeys ?? Array.Empty<string>(),
				optionOrder = item.OptionOrder,
				isLogicalNode = item.IsLogicalNode,
			}).ToArray(),
			diagnostics = ProjectImportDiagnostics(plan.Diagnostics),
		};
	}

	public static object CreateModProjection(ModNode node, CliWorkspace workspace, CliLibraryState state, OptionRelationshipGraph graph)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(graph);
		var id = FormatId(node.Id);
		var optionStates = ReadOptionStates(node.Id, state.OptionActivations);
		var decorationHosts = ReadDecorationHosts(node.Id, state.DecorationActivations);
		var hostKey = node.Id.Value.ToString("N");
		var directOptions = graph.GetDirectOptions(node.Id)
			.Select(option => new
			{
				id = FormatId(option.Id),
				name = option.Metadata.Name,
				patchGroupCount = option.PatchGroups.Count,
				enabled = state.OptionActivations.IsEnabled(option.Id.Value.ToString("N"), hostKey),
				effectiveOrder = state.OptionActivations.GetEffectiveOrder(option.Id.Value.ToString("N"), hostKey),
				portableOrder = option.Metadata.OptionOrder,
			})
			.OrderBy(option => option.effectiveOrder ?? int.MaxValue)
			.ThenBy(option => option.portableOrder ?? int.MaxValue)
			.ThenBy(option => option.name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(option => option.id, StringComparer.Ordinal)
			.ToArray();
		return new
		{
			id,
			kind = node.Metadata.Kind.ToString().ToLowerInvariant(),
			name = node.Metadata.Name,
			notes = node.Metadata.Notes,
			relativePath = node.RelativePath,
			fullPath = ResolveNodePath(workspace.LibraryRoot, node.RelativePath),
			patchGroups = node.PatchGroups.Select(group => new
			{
				archiveHex16 = group.ArchiveHex16,
				patchIndex = group.PatchIndex,
			}).ToArray(),
			patchGroupCount = node.PatchGroups.Count,
			isDeployable = node.PatchGroups.Count > 0,
			isLogicalContainer = node.Metadata.IsLogicalContainer,
			optionOrder = node.Metadata.OptionOrder,
			hostModIds = NormalizeIds(node.Metadata.HostModGuids),
			directOptionIds = graph.GetDirectOptions(node.Id).Select(option => FormatId(option.Id)).ToArray(),
			directOptions,
			parentOptionIds = graph.GetParents(node.Id).Select(FormatId).ToArray(),
			declaredChildIds = node.Children.Select(FormatId).ToArray(),
			optionActivation = node.Metadata.Kind == ModNodeKind.Option ? optionStates : Array.Empty<object>(),
			decorationEnabledHostIds = node.Metadata.Kind == ModNodeKind.Decoration ? decorationHosts : Array.Empty<string>(),
			sourcePackage = new
			{
				guid = node.Metadata.SourcePackageGuid,
				path = node.Metadata.SourcePackagePath,
			},
			helmetsMergedIntoArmor = node.Metadata.HelmetToArmorMerged,
			contentFingerprint = node.ContentFingerprint,
			createdUtc = node.Metadata.CreatedUtc,
			modifiedUtc = node.Metadata.ModifiedUtc,
		};
	}

	public static object CreateProfileProjection(Profile profile, CliWorkspace workspace, CliLibraryState state)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		var effective = ProfileEffectiveProfileResolver.Resolve(profile, state.Snapshot, state.OptionActivations);
		return new
		{
			id = FormatId(profile.Id),
			name = profile.Name,
			active = state.Snapshot.ActiveProfileId == profile.Id,
			selectedInManager = IsSelectedByManager(profile, workspace.Settings?.SelectedProfileKey),
			createdUtc = profile.CreatedUtc,
			modifiedUtc = profile.ModifiedUtc,
			revision = profile.Revision,
			logicalEntries = profile.Entries
				.OrderBy(entry => entry.LoadOrder)
				.ThenBy(entry => entry.AddedUtc)
				.ThenBy(entry => entry.NodeId.Value)
				.Select((entry, index) => CreateProfileEntryProjection(entry, index, state.Snapshot))
				.ToArray(),
			effectiveDeploymentEntries = effective.Entries
				.Select((entry, index) => CreateProfileEntryProjection(entry, index, state.Snapshot))
				.ToArray(),
		};
	}

	public static object CreateContext(CliWorkspace workspace, CliLibraryState state)
	{
		var graph = OptionRelationshipGraph.Create(state.Snapshot);
		var mods = state.Snapshot.Nodes.Values
			.OrderBy(node => node.Metadata.Kind)
			.ThenBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.Select(node => CreateModProjection(node, workspace, state, graph))
			.ToArray();
		var profiles = state.Snapshot.Profiles
			.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(profile => profile.Id.Value)
			.Select(profile => CreateProfileProjection(profile, workspace, state))
			.ToArray();
		return new
		{
			protocolVersion = ProtocolVersion,
			exportedUtc = DateTimeOffset.UtcNow,
			mode = "read-only",
			paths = new
			{
				managerRoot = workspace.ManagerRoot,
				settingsPath = workspace.SettingsPath,
				libraryRoot = workspace.LibraryRoot,
				libraryPath = workspace.LibraryRoot is null ? null : Path.Combine(workspace.LibraryRoot, "library.json"),
				gameDataRoot = workspace.GameDataRoot,
			},
			manager = new
			{
				selectedProfileKey = workspace.Settings?.SelectedProfileKey,
			},
			library = new
			{
				version = state.Snapshot.Version,
				savedUtc = state.Snapshot.SavedUtc,
				modCount = state.Snapshot.Nodes.Count,
				profileCount = state.Snapshot.Profiles.Count,
				activeProfileId = state.Snapshot.ActiveProfileId is { } active ? FormatId(active) : null,
			},
			capabilities = CreateCapabilities(),
			mods,
			profiles,
			orphanedOptionActivationIds = GetOrphanedActivationIds(state.OptionActivations.Options.Keys, state.Snapshot.Nodes),
			orphanedDecorationActivationIds = GetOrphanedActivationIds(state.DecorationActivations.Keys, state.Snapshot.Nodes),
			issues = state.ReadIssues,
		};
	}

	public static ModNode ResolveMod(IReadOnlyDictionary<ModNodeId, ModNode> nodes, string selector)
	{
		if (string.IsNullOrWhiteSpace(selector))
			throw new CliUserException("ModSelectorRequired", "请提供 Mod 的完整 GUID 或唯一 GUID 前缀。");
		var normalized = NormalizeGuidPrefix(selector);
		if (normalized is null)
			throw new CliUserException("ModSelectorInvalid", $"Mod 标识必须是 GUID 或 GUID 前缀：{selector}");
		var matches = nodes.Values
			.Where(node => node.Id.Value.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
			.OrderBy(node => node.Id.Value)
			.ToArray();
		return matches.Length switch
		{
			0 => throw new CliUserException("ModNotFound", $"未找到 Mod：{selector}"),
			1 => matches[0],
			_ => throw new CliUserException("ModSelectorAmbiguous", $"Mod GUID 前缀不唯一：{selector}。候选：{string.Join(", ", matches.Select(match => FormatId(match.Id)))}"),
		};
	}

	public static Profile ResolveProfile(IReadOnlyList<Profile> profiles, ProfileId? activeProfileId, string? selector)
	{
		if (string.IsNullOrWhiteSpace(selector))
		{
			if (activeProfileId is { } active && profiles.FirstOrDefault(profile => profile.Id == active) is { } activeProfile)
				return activeProfile;
			if (profiles.Count == 1) return profiles[0];
			throw new CliUserException("ProfileSelectorRequired", "当前没有可唯一推断的配置文件；请提供名称、完整 GUID 或唯一 GUID 前缀。");
		}

		var exactNames = profiles.Where(profile => string.Equals(profile.Name, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
		if (exactNames.Length == 1) return exactNames[0];
		if (exactNames.Length > 1)
			throw new CliUserException("ProfileSelectorAmbiguous", $"存在多个同名配置文件：{selector}");

		var normalized = NormalizeGuidPrefix(selector);
		if (normalized is null)
			throw new CliUserException("ProfileNotFound", $"未找到配置文件：{selector}");
		var matches = profiles.Where(profile => profile.Id.Value.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
		return matches.Length switch
		{
			0 => throw new CliUserException("ProfileNotFound", $"未找到配置文件：{selector}"),
			1 => matches[0],
			_ => throw new CliUserException("ProfileSelectorAmbiguous", $"配置文件 GUID 前缀不唯一：{selector}。"),
		};
	}

	public static string FormatId(ModNodeId id) => id.Value.ToString("D");
	public static string FormatId(ProfileId id) => id.Value.ToString("D");

	private static object CreateProfileEntryProjection(ProfileEntry entry, int effectivePosition, LibrarySnapshot snapshot)
	{
		snapshot.Nodes.TryGetValue(entry.NodeId, out var node);
		return new
		{
			effectivePosition,
			loadOrder = entry.LoadOrder,
			addedUtc = entry.AddedUtc,
			nodeId = FormatId(entry.NodeId),
			nodeName = node?.Metadata.Name,
			nodeKind = node?.Metadata.Kind.ToString().ToLowerInvariant(),
			patchGroupCount = node?.PatchGroups.Count ?? 0,
			isDeployable = node?.PatchGroups.Count > 0,
		};
	}

	private static object[] ReadOptionStates(ModNodeId optionId, OptionActivationSnapshot states)
	{
		var key = optionId.Value.ToString("N");
		if (!states.Options.TryGetValue(key, out var hosts)) return Array.Empty<object>();
		return hosts
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(pair => (object)new
			{
				hostModId = FormatRawGuid(pair.Key),
				enabled = pair.Value.Enabled,
				effectiveOrder = pair.Value.EffectiveOrder,
			})
			.ToArray();
	}

	private static string[] ReadDecorationHosts(ModNodeId decorationId, IReadOnlyDictionary<string, IReadOnlySet<string>> states)
	{
		var key = decorationId.Value.ToString("N");
		return states.TryGetValue(key, out var hosts)
			? hosts.OrderBy(host => host, StringComparer.OrdinalIgnoreCase).Select(FormatRawGuid).ToArray()
			: Array.Empty<string>();
	}

	private static string[] NormalizeIds(IEnumerable<string>? values)
		=> (values ?? Array.Empty<string>())
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(FormatRawGuid)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static string[] GetOrphanedActivationIds(IEnumerable<string> activationIds, IReadOnlyDictionary<ModNodeId, ModNode> nodes)
	{
		var known = nodes.Keys.Select(id => id.Value.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return activationIds
			.Where(id => !known.Contains(NormalizeGuidPrefix(id) ?? id))
			.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
			.Select(FormatRawGuid)
			.ToArray();
	}

	private static bool IsSelectedByManager(Profile profile, string? selectedKey)
		=> !string.IsNullOrWhiteSpace(selectedKey)
			&& (string.Equals(profile.Name, selectedKey, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(FormatId(profile.Id), selectedKey, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(profile.Id.Value.ToString("N"), NormalizeGuidPrefix(selectedKey), StringComparison.OrdinalIgnoreCase));

	private static string? NormalizeGuidPrefix(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		var normalized = raw.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
		return normalized.Length is > 0 and <= 32 && normalized.All(Uri.IsHexDigit) ? normalized : null;
	}

	private static string FormatRawGuid(string raw)
		=> Guid.TryParse(raw, out var value) ? value.ToString("D") : raw;

	private static string? ResolveNodePath(string? libraryRoot, string relativePath)
	{
		if (string.IsNullOrWhiteSpace(libraryRoot)) return null;
		try { return Path.GetFullPath(Path.Combine(libraryRoot, relativePath)); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
	}

	private static object[] ProjectImportDiagnostics(IReadOnlyList<ImportPlanDiagnostic>? diagnostics)
		=> (diagnostics ?? Array.Empty<ImportPlanDiagnostic>())
			.Select(diagnostic => (object)new
			{
				code = diagnostic.Code,
				message = diagnostic.Message,
				sourcePath = diagnostic.SourcePath,
				isBlocking = diagnostic.IsBlocking,
			})
			.ToArray();
}
