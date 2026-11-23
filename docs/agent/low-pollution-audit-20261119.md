# 低污染模式审计（2026-11-19）

调研对象：护甲映射的「低污染模式」及其周边的 Lua 运行时、软映射、自定义素体压缩。
方法：7 个子代理分区精读 + 主代理复核关键判定 + 真实索引库/真实运行时产物核对。
本文只记录**事实与问题**，不含方案；方案在后续独立文档里定。

---

## 0. 三句话

1. 这套功能想解决的问题是真的：`link=` 走 `App.set_resource_override('unit', 原ID, 新ID)`，**全局、且会话内不可撤销**（`armor_override_runtime.lua:169-193`），所以只要覆盖了「别的护甲也在用」的 unit id，就必然污染。
2. 它的做法在原理上成立：素体压成 2/3 个 unit，映射只碰「目标独有」的承载件，共用件交给运行时按实例隐藏。真实数据支持这个前提 —— 130 条有名护甲记录里 **106 条至少有一个严格独有的 unit**；2005 个 unit id 里有 **431 个被多于一套共用**（占 21.5%）。
3. 但它的**实现是五套并行机制 + 两套功能重叠的运行时**，而且失败路径大量静默。在你这台机器上它**从未真正跑过一次**（证据见 §4），所以上面所有问题都还没有被实机暴露过。

---

## 1. 机制全景

### 1.1 五条并行通道

| # | 名称 | 载体 | 识别"这具身体是目标套"的方式 | 可逆 | 写者 |
|---|---|---|---|---|---|
| A | 全量映射 | `wardrobe.links` 的 `link=` | 无（按 unit id 全局替换） | 会话内不可逆 | `WardrobeLinkCompiler` |
| B | 软映射承载件 | 同上，只发独有件 | 同上 | 不可逆 | 同上，`lowPollution` 分支 |
| C | **Hider** | `ArmorRegistry/kit-instance.rules`（部署写）+ `kit-instance.mapping.rules`（应用写） | 位置 ≤0.3 m + 资源 ID 簇，**无实例身份** | **不可逆**（只藏不显） | `KitInstanceRulesFile` / `LowPollutionHiderRules` |
| D | **PlayerArmorInstances** | `wardrobe.links` 的 `instance=` | avatar 资源 + 0.3 m 唯一命中 + 骨架节点数校验 | **可逆**（session，`release` 恢复可见性 + unlink + destroy） | `WardrobeLinkCompiler` 的 `PlayerLinks` |
| E | 专属记录（P2） | `kit-instance.rules` | 规则 `all`/`none` | 不可逆 | 部署时按启用节点编译 |

### 1.2 C 与 D 是同一想法的两次实现，D 严格更强

| | C `kit_instance_hider.lua` | D `player_armor_instances.lua` |
|---|---|---|
| 体量 | 1056 行 / 43 KB，独立 addon（`mods/hd2modmanager/kit_instance_hider`） | 213 行 / 7 KB，内联进护甲运行时（`mods/codex/runtime_armor_registry`） |
| 锚点 | 任意「签名 unit 实例」的位置 | `W.units_by_resource(AVATAR)` 的真玩家角色 |
| 歧义处理 | 无（半径内全藏） | **唯一性裁决**：两个 avatar 都命中则放弃 |
| 身份校验 | 无 | `num_scene_graph_items(avatar) == rule.avatarCount` |
| 替换方式 | 只隐藏 | `W.spawn_unit` 模型 + 逐骨 `W.link_unit` |
| 恢复 | **无**（全文件没有 `set_unit_visibility(u,true)`） | `release()` 恢复可见性 + unlink + destroy |
| 失败处理 | 连续 10 次失败才停手；其余静默 | 每规则缓存失败、逐轮重试 |
| 半径 | 0.3（`LowPollutionMapping.cs:933`） | 0.3（`player_armor_instances.lua:3`）——同一个数字，两个常量 |

D 现在的触发条件极窄：`LowPollutionMapping.cs:253-294` ——**只有「2 槽头身合并素体」且「目标护甲含那两个硬编码肩件」**（`0x293d8135eb95873b` / `0x29b877b122acc704`，`:130-132`）时才走 D，其余全部走 C。也就是说最强的那套实现只服务一个特殊 case。

### 1.3 为什么「污染」必须躲，而不能撤

- `set_resource_override` 只进不出：`enqueue` 只是把「不再需要的键」**计数**（`armor_override_runtime.lua:161-165` 的 `removals`），`drain` 从不撤销。
- 所以一旦覆盖错，那一局的角色外观就回不去了；低污染的价值就是**少覆盖**。
- 同理 C 的隐藏也不恢复，于是「关掉低污染 / 撤销映射」不会让部件回来，只能重启游戏。

---

## 2. 端到端数据流

```
① 素体压缩（编译期，仅当目标是自己的素体）
   SectionalCompiler.CompileAsync(..., lowPollution)
     └ SectionalCompiler.LowPollution.cs:24  CompileLowPollutionOwnModelAsync
        ├ SectionalCompressionPlanner.Plan         分组（SectionalCompressionPlan.cs:106）
        ├ SectionalUnitMerger.MergeAsync           几何合并（按玩家骨架骨名对齐）
        ├ SectionalUnitPatchWriter.WriteAsync      删掉来源全部 unit，只补压缩 unit
        └ 写 Overwrite/low-pollution-layout.json  （schema /1 或 /2 + fingerprint）
   产物：compressed units + custom-model.json + sections.json + layout

② 素体部署（用户点部署，或在映射页应用里顺带一次）
   压缩 unit 进游戏 data → 之后可从 index/patches 读出 deployedUnits

③ 应用映射（ArmorRegistryPageViewModel.ApplyAsync:2314）
   ├ PrepareSourceModelsAsync:2182      按指纹判断要不要重编（可能弹 128 MiB 对话框）
   ├ FlushAsync:2354                    先部署一次模型包（游戏未运行时）
   └ service.ApplyAsync(..., lowPollutionMode, skipModelKeys):2358
        ├ 写 armor-registry.protocol                       :551-570
        ├ LowPollutionMappingService.Plan                   :596（需要 layout + deployedUnits）
        ├ LowPollutionHiderRules.Build → WriteMappingAsync  :602-608 → kit-instance.mapping.rules
        └ WardrobeLinkCompiler.Compile                      :610-627 → wardrobe.links
             ├ link=承载件,压缩unit（Mapped 的目标）
             └ instance=承载件,模型,骨架数,节点数,骨链,隐藏集  （D 通道）
       ← links 编译异常一律吞成 LinksError，只保留上一版文件

④ Hider 投放（部署期，不是应用期）
   LuaDeploymentService.PrepareKitInstanceRulesAsync:876
     ├ 编译 workspace → kit-instance.rules                :904-931
     ├ kitRuleCount = 主规则数 + CountMappingRules(mapping) :934
     └ > 0 才投放 Hider：外置路径做独立 addon，独占路径并进 boot 最后一块 :545-577 / :612-621

⑤ 游戏内
   armor_override_runtime.lua   读 link=/map=/instance=，set_resource_override + 转交 D
   player_armor_instances.lua   按 avatar 挂模型、按 session 隐藏与恢复
   kit_instance_hider.lua       读两个规则文件（取并集），按锚点半径隐藏
```

---

## 3. 代码地图

| 文件 | 行数 | 职责 |
|---|---|---|
| `HD2ModCore/Infrastructure/Sectional/LowPollutionMapping.cs` | 1145 | 布局 DTO + 布局读取器 + 映射规划器 + Hider 规则编译 + 编排服务（**五件事一个文件**） |
| `…/Sectional/SectionalCompiler.LowPollution.cs` | 353 | 压缩编译分支（含 128 MiB 溢出重试循环） |
| `…/Sectional/SectionalCompressionPlan.cs` | 275 | 分组规划（头/躯干/手臂 + 分体型 + body-arms 派生）+ 布局文件写出 |
| `…/Sectional/SectionalUnitMerger.cs` | 212 | 几何合并 |
| `…/Sectional/SectionalMergeOverflowModels.cs` | 518 | 128 MiB 溢出归因与交互（文件名里的 Models 指"对话框模型"） |
| `…/Sectional/LowPollutionFreshness.cs` | 266 | 指纹、是否要重编、不可压缩标记 |
| `…/Sectional/LowPollutionRecompiler.cs` | 67 | 逐个重编编排 |
| `…/Sectional/PlayerArmorInstanceRigReader.cs` | 114 | 应用期扫全部 `*.patch_*` 算骨架重定向（D 通道） |
| `…/Sectional/SeafSoldierUnitBuilder.cs` | 100 | SEAF 士兵用原版根 unit 重建网格（**零测试**） |
| `…/KitInstance/KitInstanceRulesProtocol.cs` | 290 | 规则文本协议（与 Lua 双实现） |
| `…/KitInstance/KitInstanceRulesCompiler.cs` | 179 | 规则编译 + 两个规则文件的读写 |
| `…/KitInstance/KitInstanceHiderLuaSource.cs` | 23 | 从嵌入资源读 lua |
| `…/WardrobeLinkCompiler.cs` | 402 | 老路径与新路径汇合点 |
| `HD2RuntimeArmorOverride/src/kit_instance_hider.lua` | 1056 | C 运行时 |
| `HD2RuntimeArmorOverride/src/player_armor_instances.lua` | 213 | D 运行时 |
| 测试 | ≈ 3200 | `LowPollutionMappingTests` 1603 / `SectionalLowPollutionTests` 780 / lua 785 |

另：`LowPollution*` / `kit-instance` 相关命中 **937 行**，分布在 Core（289）、Core.Tests（384）、Manager（81）、Lua（28）、docs（131）。

---

## 4. 真机现状（这台机器上的证据）

| 事实 | 证据 |
|---|---|
| 低污染**从未跑过** | `ArmorRegistry/kit-instance.rules` 只有 95 B、0 条规则；`kit-instance.mapping.rules` **不存在** |
| Hider **从未投放** | `Logs/` 下没有 `KitInstanceHider.log`；`BingusSharedLoader.log` 的 discovery 列表里没有 `mods/hd2modmanager/kit_instance_hider` |
| 但链路其余部分是活的 | 同一份日志里 `mods/codex/runtime_armor_registry: loaded`（D 通道在跑）、`mods/mdl/runtime: loaded`（MDL3 加载器在位） |
| 现在跑的是全量映射 | `wardrobe.links` 2002 条 `link=` / 2000 个不同键，`map=` 0 条、`instance=` 0 条 → 268 条映射全是素体映射 |
| 覆盖表没爆 | `ArmorOverrideRuntime.log:1037` `queue drained (1056 sets, 0 rejected), total overrides=2000` |

含义：**所有"实机未验证"的项仍然未验证**；同时这台机器的库里躺着 2000 个全局覆盖，正是低污染要消掉的那 2000 个。

---

## 5. 独立核算的真实数据

用 `HD2ModManager/bin/Debug/.../asset-index.sqlite`（schema 12，2026-11-17，源自真实游戏 data）只读统计：

- 422 条 kit 记录 = 138 护甲 + 161 头盔 + 123 披风。
- 有名（非 `0x…`）护甲记录 130 条 → 去重后 **116 个名字**；头盔 141 条 → **118 个名字**。
- **106 / 130** 条有名护甲至少有一个「严格独有」unit（拥有者集合 = {自己}）。
  - 有独有躯干（slot 2）：97；有躯干以外的独有件：104。
  - 独有件槽位分布：肩 88/83、躯干 97、胯 69、腿 67/64、臂 63/62。
- unit id 共 2005 个，其中 **431 个被多于 1 套共用**（21.5%）—— 这就是污染面积。

与文档 §11.10 的「100 套可映射 / 30 套不可」同一量级（口径更宽时会略多），可以认为作者的数字是可信的。

### 5.1 顺带结案：`BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts` 为什么红

- 该用例期望 **120 / 116 / 118**，我按代码口径复算 **Debug 索引**（138/161/123 行）得到：护甲名组 **116**、头盔名组 **118**、+4 条 NPC = **120** —— **完全吻合，断言本身是对的**。
- 它默认去找 **`bin/Release/.../data/indexes/asset-index.sqlite`**（`ArmorMappingPickerPlannerTests.cs:348-372`），而那份索引是 **2026-09-22 / schema 10**（132/155/115），复算得 114/110/112 → 必然红。
- 也就是说：这不是"期望过时"，是**默认数据源指向了两个月前的 Release 索引**，且 `ArmorAuthorityIndexReader` 不校验 schema 版本。上一轮把它当成"环境相关的既有失败"搁置，结论下早了。

---

## 6. 问题清单

分级：**P0 = 会让用户看到错误结果或静默失效**；P1 = 架构/判定分裂，改动会互相打脸；P2 = 可维护性；P3 = 文档。

### P0 正确性 / 静默失效

| # | 问题 | 证据 |
|---|---|---|
| P0-1 | **素体目录不可用或素体不在目录里 → 静默退回全量映射**，正是低污染要消灭的污染。用户只看到"协议已保存"。 | `LowPollutionFreshness.cs:259`、`LowPollutionMapping.cs:1085`、`WardrobeLinkCompiler.cs:88-108` |
| P0-2 | **被跳过的素体（重编失败/游戏在跑/Blocked）的目标行没有任何标注**，与"已应用"长得一样，且已被算进 baseline。 | `ArmorRegistryPageViewModel.cs:2150-2162`、`:2367`、`:2425` |
| P0-3 | **Hider 的隐藏不可逆**：撤映射 / 关开关 / 清规则都不恢复，只能重启游戏。测试把这条当预期锁住。 | `kit_instance_hider.lua:432-444`（全文件无 `true`）、`test_kit_instance_hider.lua:267-277` |
| P0-4 | **首次启用必须再部署一次**：应用里那次 `FlushAsync` 发生在 `ApplyAsync` 写映射规则**之前**，所以 Hider 那一次没被投放；界面只能在汇总句尾说一句"首次启用需要再部署一次"，而游戏开着时没有任何"再部署"入口。 | `ArmorRegistryPageViewModel.cs:2352` / `:2402`；`LuaDeploymentService.cs:934` |
| P0-5 | **CLI 会静默把低污染关掉**：`armor-registry apply` 不传 `lowPollutionMode`（默认 false）→ 清空 `kit-instance.mapping.rules` 并把 `wardrobe.links` 重写成全量。CLI 构造 `VpkHostContext` 也不设低污染，所以命令行永不压缩。 | `CliArmorRegistryProtocol.cs:513-521`、`RuntimeArmorRegistryService.cs:497`、`CliModRebuild.cs:43` |
| P0-6 | **写盘无回滚且顺序反了**：先写隐藏规则、后编 links；links 抛异常被吞成 `LinksError`，规则已经生效 → "藏了部件没有模型"的中间态。 | `RuntimeArmorRegistryService.cs:600-608` vs `:610-648` |
| P0-7 | **承载件冲突 / 低污染跳过 / 规则丢弃只计数不呈现**：`LowPollutionLinkCount/Skipped/Conflicts` 全仓只被测试读过，界面拿的是 `plan.Status`，所以会显示"已映射"而文件里根本没这行。 | `WardrobeLinkCompiler.cs:46-52,260-265`；`RuntimeArmorRegistryService.cs:610-627` |
| P0-8 | **写映射规则前不校验、不查重名**：`WriteMappingAsync` 只做 `FitToLimits`。Lua 侧遇重名/非法是**整份文件无效**（只留一行日志）→ 所有映射失效，而 UI 报"Hider 规则 N 条"。 | `KitInstanceRulesCompiler.cs:129-143`、`kit_instance_hider.lua:182-212,391-396` |
| P0-9 | **`instance=` 在 C# 侧零校验**：Lua 要求 hide 非空且 ≤128、单条 `instance=` 解析失败会让**整份 `wardrobe.links` 无效**（保留上一版）→ 一条坏行冻结全部映射更新。 | `player_armor_instances.lua:17-41`、`armor_override_runtime.lua:123-126,202-207`；C# 无界：`LowPollutionMapping.cs:281-289` |
| P0-10 | **两套规则解析器口径不一致**：radius 下限 C# 要 `≥1e-6` 而 Lua 只要 `>0`；文件大小 C# 按 UTF-8 字节、Lua 按字符。与"两侧必须一致"的注释直接矛盾。 | `KitInstanceRulesProtocol.cs:45,68,157` vs `kit_instance_hider.lua:168,184` |
| P0-11 | **`plans.Add(null!)` 占位 + 未捕获异常**：`Plan` 里 `armorPlansByModel[...]` 可能抛 `KeyNotFoundException`，而 `ApplyAsync` 的 catch 不含它 → 协议已写、links 未写、异常直抛 UI。 | `LowPollutionMapping.cs:1122-1123,1132-1142`；`RuntimeArmorRegistryService.cs:641-644` |
| P0-12 | **§18「全量映射后军械库缺件」未修，根因 B 仍在代码里**：每个目标同时写「承载件原 ID」和「压缩 unit ID」两套签名、hide 集相同，于是所有已映射角色身上都会触发各目标的隐藏集。文档仍标"未修"，且它指向的 `armory-twin-pollution-handoff-20261114.md` **从未存在**。 | `LowPollutionMapping.cs:948-966`（双签名）、`:914-924`（并集安全性论证）；`kit-instance-mapping-deploy-plan-20261114.md:865-867` |

### P1 架构 / 判定分裂

| # | 问题 | 证据 |
|---|---|---|
| P1-1 | **「独有」有 5 套判据**：严格单拥有者 / 同名组 `IsGroupUnique` / `HelmetUnique`（只收头盔、合并那一遍要求严格唯一）/ `HelmetOwners`（收所有类别、有 SetId 守卫）/ `IsOwnerCheckedForConsensus`（整类丢弃未命名记录）。注释却写"口径共用，不许各写一套"。 | `SectionalExclusivePlanner.cs:155-165,497-499`；`LowPollutionMapping.cs:135-141,218-245,364-370` |
| P1-2 | **2 槽 / 3 槽有 5 套判定且不等价**：`IsTwoSlotCompressedModDirectory` 在**没有布局文件**时也能返回 true（回退读 wardrobe.json），而 `IsThreeSlotCompressedCustomModel` 要求文件存在 → 同一份 Mod 可能"选品器认为 2 槽、映射器认为没压缩"。 | `ArmorMappingPickerPlanner.cs:353-418` |
| P1-3 | **槽位兜底三处不一致**：`LowPollutionMapping.SlotOf` 把任意未知部位兜成**头盔槽 0**，另两处兜 0/1/2。 | `LowPollutionMapping.cs:183`；`SectionalCompilePlan.cs:121-123`；`SectionalExclusivePlanner.cs:247-249` |
| P1-4 | **布局 role 名写侧兜底危险**：任何新 role 都被静默写成 `head-body`。 | `SectionalCompressionPlan.cs:267-274` |
| P1-5 | **`HelmetKitsOf` 缺 `SetId == 0` 守卫**，而 `HelmetOwners` 有 → 同一概念两处口径不同。 | `SectionalExclusivePlanner.cs:168-174` vs `LowPollutionMapping.cs:135-141` |
| P1-6 | **Hider 里约 250 行是一次性排查代码，且生产常开**：安装时传 `diag_interval = DIAG_INTERVAL(15)`，于是 `officer_near`/`female_heads`/`female_near`/`player_near` 在热循环里一直跑；压测脚本反而没开诊断，所以「每帧开销与规则数无关」的结论没覆盖真实形态。 | `kit_instance_hider.lua:34,1028`；`stress_kit_instance_hider.lua` 未传该参数 |
| P1-7 | **`world_position` 是会闪退的调用，靠 5 处手写 gate 维持**，没有集中封装 —— 漏一处就是闪退。 | `kit_instance_hider.lua:582,608,618,639,662`（注释在 `:659`） |
| P1-8 | **资源名有 3 份副本**：lua 首行标记、`KitInstanceHiderLuaSource.ResourceName`、`DiagnosticLogParser`；改首行时 `BuildLuaResource` 不会重写前缀 → 引擎资源 ID 变、日志归因静默失效。 | `kit_instance_hider.lua:1`；`KitInstanceHiderLuaSource.cs:10`；`DiagnosticLogParser.cs:46` |
| P1-9 | **`update` 接力脆弱**：`armor_override_runtime.lua` 只在"前一个 update 恰好是 Hider 的 hook"时保留它；任何第三方在中间换掉 `update` 就静默顶掉 Hider。 | `armor_override_runtime.lua:230-236` |
| P1-10 | **映射模式＝「关闭」时低污染仍全套执行**：页面和服务都不读 `ArmorMappingMode`，素体被改形状、toast 说"已映射"，游戏里毫无变化（部署侧才看）。 | `LuaDeploymentService.cs:378`；全仓 `ArmorRegistryPageViewModel`/`RuntimeArmorRegistryService` 无 `ArmorMappingMode` |
| P1-11 | **指纹不含 patch 内容、不含压缩器版本**：换几何但 `sections.json` 逐字节相同时会被判"已是最新"，映射继续指向旧压缩 unit。 | `LowPollutionFreshness.cs:41-61` |
| P1-12 | **2 槽 → 3 槽没有自动退化**：headMerge 是纯用户开关，合并失败即整份编译硬失败，界面没有"关掉头身合并"的提示。 | `LowPollution.LowPollution.cs:239-242`；`SectionalUnitMerger.cs:131` |

### P2 可维护性

- `LowPollutionMapping.cs` 1145 行 / 5 职责；`CompileLowPollutionOwnModelAsync` 226 行（`while(true)` + 4 层嵌套）；`PlanArmorGroup` 105 行；`Service.Plan` 81 行且用 `plans.Add(null!)` 占位。
- 编译尾段（Build→ApplyUnitIdentities→Bake→Materialize→WriteStructure）在 4 处重复（`SectionalCompiler.cs:223-242`、`:388-407`、`Exclusive.cs:249-268`、`LowPollution.cs:138-157`），导致低污染分支与普通分支约 90% 同构。
- 128 MiB 溢出处置写了两份（`LowPollution.cs:185-238` 与 `SectionalCompiler.Exclusive.cs:130-200`）。
- unit id 归一化 6 份实现；`UnitTypeId = 0xe0a48d0be9a7453f` 抄 3 份；半径 0.3 两个独立常量。
- 布局文件一半字段只写不读：`mode`、`idFrom`、`parts[]`、`passthrough[]` 全仓无消费者（`layout.Mode` 零引用）；NPC 行的 `idFrom` 固定写 `torso`、`parts` 为空，不是事实。
- `SectionalCompiler.LowPollution.cs:141-143` 的第二次 `BakeAsync(..., EmptySections)` 是**空写**（`target == current` 全跳过），读起来像必要步骤。
- ViewModel 侧：`ApplyAsync` 230 行一条直线、12 个阶段一个 try；`GetEffectiveLowPollutionMode()` 一次应用里读 5 次；`"not-deployed"` / `"mapped"` / `"unavailable"` 是跨程序集魔法字符串。
- **测试卫生（最值得注意）**：真数据用例全部用 `if (…) return;` 跳过（4 个文件共 35 处），xUnit 记成**通过**；CI 永远绿但零覆盖。Lua 测试有入口 `scripts/Test.ps1` 但没有任何 workflow 引用；`stress_kit_instance_hider.lua` 连入口都没登记。
- 测试盲区（按危险排序）：`SeafSoldierUnitBuilder` 零测试；`WriteMappingAsync` 不校验（见 P0-8）；`AnyNpcVariantMismatch`/`LeftoverNpcModelKeys` 零测试但决定要不要重编；`IsTwoSlotCompressedCustomModel` 零测试；**没有任何测试把 C# 生成的真实规则文本喂给 Lua 解析器**（跨语言对偶未交叉验证）；`hideRulesDropped` 无人断言。
- 断言大量绑中文文案子串（改一句提示就红），部分断言绑内部列表顺序。

### P3 文档

| # | 问题 |
|---|---|
| P3-1 | 主规划 §18 指向 `armory-twin-pollution-handoff-20261114.md`，**该文件从未进过 git**（`git log --all -- '*armory-twin*'` 零命中）→ 那条未修 bug 的完整推理链已丢失。 |
| P3-2 | `KitInstanceRulesProtocol.cs:121` 的 XML 注释仍写「超过 64 条或 64 KiB」，实际上限是 1024 条 / 2 MiB。 |
| P3-3 | 主规划结构坏了：两个「## 7」、§6.1/§6.2 排在 §7 之后、§3 已被 §7 整节取代、**没有 §0 术语表**（术语在 productization-plan §0）。`armor-twin-bundling-plan` 引用了主规划不存在的 §19。 |
| P3-4 | §11.2 内部自相矛盾：「每个目标一条规则」vs「每个目标两套签名」；代码是每目标 2–4 条。 |
| P3-5 | 份额口径不一致：recon「138/138 全可行」vs 主规划「100 可映射 / 30 不可」。 |
| P3-6 | 代码已远超文档：Hider 实际 0.4.6（含四大块诊断采集），文档只写到 0.4.3；SEAF/舰船 NPC 映射在 11-14 文档里完全没有。 |
| P3-7 | `HD2RuntimeArmorOverride/README.md` 的"正式文件"清单里没有 `kit_instance_hider.lua`，也没提两个规则文件协议；`dist/` 入口不含它。 |
| P3-8 | 文档引用的 7 个提交（18c1f6b/78e2b7b/1b300d4/…）**都不是 master 的祖先**：PR #28 的 22 个提交被 squash 成 `aaf9668`，真实演进史只在 `pr28`/`pr33`/`pr34` 引用里。`git blame` 在低污染全部核心文件上都指向 `aaf9668`。 |
| P3-9 | 两个 release 提交（`fa7e875`/`b78e63e`）**只改 csproj 版本号一行**；版本叙述只在 commit message 里，发布说明与内容没有可追溯映射。 |

---

## 7. 贡献者与历史（供判断"谁的活、能不能大改"）

- 功能主体来自外部贡献者 `泡沫君 <59364032+egoist74110@users.noreply.github.com>`（GitHub `egoist74110`），走 PR #26 #28 #30 #33 #34 进入 master；仓库主人 `AssestinaR` 负责审核与发版。
- 阶段划分（我的划法，与文档的 P/S 两套编号不同）：P0 手工探针（11-13）→ P1 产品化接线（Hider 0.2.0 + 规则协议）→ **P2 低污染本体**（11-14 上午：压缩 + 只写独有件 + 按需重编）→ P3 实机返工（11-14 下午–夜：分体压缩、Hider 0.4.1/0.4.2/0.4.3、body-arms）→ P4 补丁式收尾（11-15–16：孪生捆绑、测试隔离、squash）→ P5 外延（#33 SEAF/NPC、#34 128 MiB 与 ProgramData）。
- **代码里一个 `TODO`/`FIXME`/`临时`/`权宜` 都没有**（全量 grep 0 命中）。所有"认输"都写在 docs：§16「已知代价」、§17.1「未验证」、§18「未修」。代价是无法机械检索技术债。

---

## 8. 权宜之计清单（这些是"做法"里最该规整的部分）

| 东西 | 当初绕开什么 | 现状 |
|---|---|---|
| 双签名（承载件原 ID + 压缩 unit ID） | 不确定 `Unit.resource_name` 在覆盖后返回哪个 ID | **仍在**，成了 §18 的根因 B |
| 0.3 m 位置判定 | 拿不到引擎的护甲实例身份 | **仍在**，误藏是已知而非已消除 |
| 不恢复可见性 | 避免跨帧持有 Unit 句柄 | **仍在**（设计选择） |
| 换色孪生捆绑（`TwinBundlingEnabled`） | 同构不同色的护甲游戏里分不开 | **仍在**，注释写着"将来方案就绪后置 false" |
| §16 躯干无独有件 → 身体改挂到胯/腿/肩 | 用户拒绝"不可映射" | **仍在**，真数据只救回 3 套；注释自承第一人称可能露模型 |
| body-arms 合体 unit | 躯干外只剩一个独有件时手臂没处放 | **仍在**，是"2/3 个 unit"口径之外的第 3/4 类 |
| 口径二：未命名的 `0x…` 记录不参与承载件共识 | 否则大量护甲变"不可映射" | **仍在**，只对民主官特批参与 |
| `instance=` + 骨架重定向 | 2 槽头身合并后军械库缩略图缺件 | **仍在**，硬编码两个肩件 ID，应用期要扫全部 patch |
| SEAF 硬编码 `continue` | SEAF 必须保留原资源键否则闪退 | **仍在** |
| 128 MiB 溢出 → 关组件 / 退回非合并 | 大素体压不进 2/3 个 unit | **仍在**，代价是"退回非合并"等于放弃低污染 |
| `int.MaxValue-1/-2/-3` 落位序号 | 多个 addon 补丁不能撞序号 | **仍在**，靠约定+注释 |
| PR 22 提交 squash 成 1 个 | 一次性审完 | **永久债** |

---

## 9. 我尚未搞清楚的（不猜）

1. 游戏内「2/3 个 unit + 少量承载件」是否真的正确显示 —— 文档 §17.1 明说第一人称视野与合体 unit 在右肩的位置未验证。
2. 多个 addon 补丁写成同一个 `archiveHex16.patch_0` 是否会互相覆盖（Hider/护甲/物理三份都这么写，只靠 `PlacementOrder` 排队）—— 取决于 MDL3 的实现，本仓库看不出来。若真是整档覆盖，外置模式下护甲 addon 会压掉 Hider。
3. `Unit.set_unit_visibility` 的真实语义（是否可逆、重复调用有无成本/副作用）。
4. 0.3 m 在真实密集场景（4 人贴脸、NPC 群）的误藏率。
5. `hd2pbx_api.txt` 的真实内容（不在仓库里），无法核对 `num_scene_graph_items` / `units_by_resource` 是否在加载器白名单内。
6. `MAX_UNITS_PER_WORLD = 65536` 的实测依据只有 8192 的历史事故，65536 这个数没有记录。
7. 真实老库 `sections.json` 里 `BodyType` 是否会出现空值（决定 P1 类问题里"空 BodyType 让整个素体不可压缩"的实际触发率）。
8. §18 那个事故到底修没修完：能确认 11-15 加了孪生捆绑处理了根因 A，**根因 B 没有找到显式修复**。

---

## 10. 参考

- 主规划：`docs/agent/kit-instance-mapping-deploy-plan-20261114.md`（102 KB，§10 压缩 / §11 软映射 / §12 自动工作流 / §13 分体 / §14 实机修正 / §15 收尾 / §16–§18 遗留）
- 侦察：`sectional-3slot-softmapping-recon-20261114.md`、`soft-mapping-armory-crash-recon-20261112.md`
- 产品化：`kit-instance-isolation-plan-20261113.md`、`kit-instance-isolation-productization-plan-20261113.md`
- 后续：`officer-head-merge-plan-20261116.md`、`seaf-npc-mapping-research-20261117.md`、`d3d12-128mib-stream-guard-and-overflow-resolution-plan-20261118.md`
