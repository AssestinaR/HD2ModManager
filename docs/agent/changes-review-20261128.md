# 变更整理：`wip/repair-rig-experiment` vs `master`（2026-11-28）

> 用途：给"打勾 / 合并 / 封存"做决定用。**这一轮只整理，不动代码。**
> 结论先说：15 个提交、29 个文件、+5715/−20。里面只有三块东西，**能分开**：
> ① 只读探针 + 文档（无运行时影响）② 民主官当映射目标（小、干净、不依赖修补表）
> ③ 修补表 / 克隆 / NPC 会话（≈1500 行 C# + 860 行 Lua，就是"不好用"的那块）。

## 0. 分支现状

| 项 | 值 |
|---|---|
| 当前分支 | `wip/repair-rig-experiment` |
| 基点 | `3259659`（= `master` 的 HEAD，也是 `merge-base`） |
| master 与 origin/master | master 领先 origin **3 个提交**（preview P0/P1 + 文档），**与本次实验无关** |
| 本分支 | 领先 master **15 个提交**，全部本地，未推送 |
| 净改动 | 29 个文件，+5715 / −20 |
| 关键：`WardrobeLinkCompiler.cs` / `RuntimeArmorRegistry.cs` | **净改动为零**（af56199 加过、6bbdcde 又撤了）⇒ 映射行生成本身没被改过 |

## 1. 三组的分界（最重要的判断）

| 组 | 内容 | 运行时影响 | 与用户诉求的关系 |
|---|---|---|---|
| **A 探针 + 文档** | `armor_identity_probe.lua` v1→v8、`IdentityProbeLuaSource`、`LuaDeploymentService` 的探针投放、全部 `docs/agent/*` | **无**（`identity-probe.on` 不在就完全不参与） | 认知工具，本轮所有"引擎到底能问出什么"的结论都靠它 |
| **B 民主官当映射目标** | `KitNonPlayerArmors.WithOfficerHead` 把 `387f271c7f49614f` 补成他那套的 slot0 件 + `ArmorAuthorityIndexReader` 一行调用 + `OfficerHeadMappingTests` | 只改**协议里的联行**（`link=头,模型头` / `link=头,隐藏`），不需要任何克隆 | 正好是用户要的"正确地隐藏头 / 附加模型"，**且不依赖修补表** |
| **C 修补表 / 克隆 / NPC 会话** | `RepairTableBuilder`、`RepairRigReader`、`RuntimeArmorRegistryService` 接线、`armor_override_runtime.lua`（+860 行：认甲 / 按 avatar 会话 / 克隆 + 绑骨 / maintain / teardown / npc 宿主 / 宽松隐藏）、`armor_registry_protocol.lua` 的 `kit_piece=`/`repair=`、对应测试与 `tools/ArmorRepairPlanPreview` | **大**：每拍扫描、克隆、按实例隐藏 | 用户判定"不好用、引出更多问题"的那块 |

## 2. 逐提交清单

| # | commit | 一句话 | 组 | 建议 |
|---|---|---|---|---|
| 1 | `af56199` | 备份：按 rig 的逐实例修补实验（随后被 6bbdcde 撤回） | — | 历史，无净改动（除文档/工具） |
| 2 | `6bbdcde` | 污染修复：认甲锚点 + 按槽位/按 avatar 会话换件 | C | 📦 封存 |
| 3 | `4234f90` | 民主官整身替换成视觉来源（含头部） | C | 📦 封存 |
| 4 | `1bad2b5` | 会话失效只看认甲结果；NPC 兜底定位标记 | C | 📦 封存 |
| 5 | `f3ce12e` | 认甲按视觉来源分组；同槽位"多余件"也出修补行 | C | 📦 封存 |
| 6 | `f2c26b0` | 修补行不得把"该显示的件"写进要藏的列表 | C | 📦 封存 |
| 7 | `3829f91` | 同位素标记可行性：隐藏件 patch 可复制成任意新 id | **D** | ✅ 保留（纯测试） |
| 8 | `67624ee` | 只读探针 v1/v2 | A | ✅ 保留 |
| 9 | `41facc6` | 探针可独占一次 boot（+csproj 内嵌） | A | ✅ 保留（见 §4 风险） |
| 10 | `465cdb0` | 探针 v6 两遍聚类 + 交接文档 | A | ✅ 保留 |
| 11 | `fb71706` | 探针 v7 + 离线测试 + Test.ps1 | A | ✅ 保留 |
| 12 | `16f307c` | 探针 v8 + NPC 标记换根 unit + `NpcHost` | **A+C 混合** | 🔀 拆：v8 进 A，`npc=`/`NpcHost` 进 C |
| 13 | `9fa74af` | 文档：头 = `387f271c7f49614f`（实机确认） | A | ✅ 保留 |
| 14 | `9f23561` | 头补成 slot0 件（`WithOfficerHead`） | B | ✅ 保留 |
| 15 | `d22949c` | 运行时 1.10：宽松隐藏不吃别的套的 NPC | C | 📦 封存（判据本身留着，见 §5） |

## 3. 打勾（建议合并到 master 的净改动）

**A 组（13 个文件）**
- `HD2RuntimeArmorOverride/src/armor_identity_probe.lua`（v8 全量）
- `HD2RuntimeArmorOverride/tests/test_armor_identity_probe.lua`
- `HD2ModCore/Infrastructure/IdentityProbeLuaSource.cs`、`HD2ModCore.csproj`（内嵌资源）
- `HD2ModCore/Infrastructure/LuaDeploymentService.cs`（探针投放三处）
- `HD2RuntimeArmorOverride/scripts/Test.ps1`（登记探针测试）
- `docs/agent/*` 全部 6 篇

**B 组（4 个文件）**
- `HD2ModCore/Infrastructure/KitInstance/KitNonPlayerArmors.cs`：`DemocracyOfficerHeadUnitId`（值换成实测 id）、`DemocracyOfficerRootUnitId`、`DemocracyOfficerShoulderUnitId`、`WithOfficerHead`、`ExtraHideUnits` 指向新头
- `HD2ModCore/Infrastructure/ArmorAuthorityIndexReader.cs`：读完快照后 `WithOfficerHead` 一行
- `HD2ModCore.Tests/OfficerHeadMappingTests.cs`（3 个用例：补件幂等 / 有头模型换头 / 没头模型藏头）
- `HD2ModCore.Tests/LowPollutionMappingTests.cs`：`face`→`head` 改名（跟随常量）

**D 组（1 个文件）**
- `HD2ModAdaptation.Tests/MarkerHiddenUnitPatchTests.cs`（`PatchArchiveWriter.additionalEntries` 在 master 上已存在 ⇒ 独立可合并）

## 4. 打勾部分的三个风险点（合并前要过一眼）

1. **`LuaDeploymentService` 动了 boot 组装**。逻辑全部由 `ArmorRegistry/identity-probe.on` 门控，文件不在就 `probeRequested=false`，两条分支都不进。CLI 部署测试（197 条）在分支上是绿的，但这是唯一一处"会影响用户部署结果"的 A 组改动。
2. **`IdentityProbeBootOnly` 会借 `physicsBootDocument` 这个变量**（`if (probeOnlyBoot is not null) physicsBootDocument = probeOnlyBoot;`）。名字对不上语义，但只探针模式（两模式都关）才走；合并时把变量名改成 `bootDocumentOverride` 之类更清楚。
3. **`schema=4` 与 `armor_registry_protocol.lua` 的 `schema ≤ 3` 断言不一致**。`RepairTableBuilder.Append`
   会把 `wardrobe.links` 提到 `schema=4`，而 `armor_registry_protocol.lua` 只接受 1/2/3。今天没炸是因为
   这个模块的消费者是 `armor_registry_runtime`（读的是 `armor-registry.protocol`，另一份文件），
   不是 `wardrobe.links`。**属于埋着的坑**：封存 C 组后 `schema` 回到 3，坑自动消失。

## 5. 封存（不合并）

**要一起回滚的东西（少一样都会留下半截代码）**

| 文件 | 处理 |
|---|---|
| `HD2ModCore/Infrastructure/RepairTableBuilder.cs`（657 行，新文件） | 删除 |
| `HD2ModCore/Infrastructure/Sectional/RepairRigReader.cs`（125 行，新文件） | 删除 |
| `HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs`（+43） | 回到 master：不再 `Build` / `BuildAnchors` / `BuildNpcMarkers` / `Append` / `RepairRigReader` |
| `HD2RuntimeArmorOverride/src/armor_override_runtime.lua`（259 → ~1119 行） | 回到 master（只有"读协议 → 写 `set_resource_override`"） |
| `HD2RuntimeArmorOverride/src/armor_registry_protocol.lua`（+38） | 回到 master（去掉 `kit_piece=`/`repair=`/`priority=` 与 schema 3） |
| `HD2ModCore.Tests/RepairTableBuilderNpcTests.cs`、`HD2RuntimeArmorOverride/tests/test_armor_override_repair_v4.lua`、`test_armor_registry_protocol_v3.lua`、`test_armor_override_runtime_update.lua` 的用例 F、`tools/ArmorRepairPlanPreview/` | 随 C 组一起封存 |

**注意**：`armor_override_runtime.lua` 里有几条**与补丁表无关、但很有用**的经验，回滚前先把它们抄进文档（已经在
`docs/agent/soft-mapping-repair-npc-20261123.md` 与 `handover-20261124-armor-identity.md` 里，但代码注释会随文件一起没）：

- `isLiveAvatar`：**尸体/展台没有动画状态机**，不过滤就会一直选中尸体；
- 会话是否成立**只看认甲结果**，不看"我们藏过的件还剩几件"（否则永远失效不了）；
- `ownedByOtherKitNpc`：宽松隐藏不能吃"另一个套的 NPC 宿主"身上的件（2026-11-25 民主官整套消失）；
- `BONE_NAMES` 里**没有 head/neck/spine** ⇒ 头和头盔只能靠 `link=` 换件，不能克隆。

**封存的物理形式**（不动提交历史）

```powershell
git tag archive/repair-clone-20261128 wip/repair-rig-experiment
git branch archive/repair-clone-20261128 wip/repair-rig-experiment   # 或不建分支，只留 tag
```

以后要回头看/复用哪一段，`git show archive/repair-clone-20261128:HD2ModCore/Infrastructure/RepairTableBuilder.cs` 就能取。

## 6. 这次失败的"值钱部分"（建议合并的文档里已经写全）

1. **`set_resource_override` 先写生效**，且实例登记在 donor 名下 —— 两套共用承载件时，**只有一套能是对的**。这是所有麻烦的源头。
2. **`World.units_by_resource` 的键必须是 `IdString64.from_hex`**；返回空 ≠ 没有实例（要全量扫 `World.units` + `debug_name`）。
3. **引擎没有任何可读的"当前装备"数据**（v1–v3 探针六种问法全部 false/nil）⇒ 认甲只能靠"场上能观测到的东西"。
4. **按实例克隆 + 绑骨在 NPC 身上不可靠**：头/头盔的骨不在 `BONE_NAMES` 里；NPC 骨架与玩家不同，rig 配对表（107 节点）在 NPC 身上不成立。
5. **宽松隐藏会连别人身上"正确"的那件一起藏**（民主官整套消失）。
6. **头可以走 `link=` 换件**（`WithOfficerHead`）：比"判断来源有没有头，再决定藏不藏"简单得多，且天然正确。

## 7. 需要你拍板的三件事

1. **C 组是全封存，还是留一点点？** 我的建议：全封存。唯一值得考虑的例外是"NPC 宿主那一套 hide-only 行"，
   但 `WithOfficerHead` 之后它已经**冗余**（未覆盖的槽位由 `link=…,隐藏` 处理），留着只会多一条会打架的通路。
2. **`DemocracyOfficerRootUnitId` / `ShoulderUnitId` 两个常量**只被 C 组的 `NpcHost` 用（`WithOfficerHead`
   只用到头那个）。建议留着当文档常量 —— 它们是这次唯一"量出来的"引擎事实，删了以后还要再探一遍。
   你如果偏好"合并的代码里不出现未使用成员"，就一起封存。
3. **接受"共享承载件只能有一套正确"的现状吗？** 封存后，如果你同时映射 FS-05（玩家，3970674111）和民主官（124623533），
   只有先写的那套生效，另一套会显示成对方。要么先别同时映射这两套，要么下一轮专门做"共享件让不可标记的一方赢"（原②）。
