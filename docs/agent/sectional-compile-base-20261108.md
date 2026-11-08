# 编译基座：链路只有四段（2026-11-08）

> 这一篇定的是**物理链路**，不是代码结构。任何新功能都先问：它属于这四段的哪一段。

## 1. 四段链路

```
导入（vpk / patch 解压） → 转换（vpk2patch） → 编译（sectional + 条件） → 部署（Overwrite → 游戏）
```

### 1.1 四段与"谁可以写"

| 段 | 输入 | 输出 | 谁可以写 |
|---|---|---|---|
| **导入** | 压缩包 / 目录 | 根上平铺的**来源**（`*.vpk` / `*.patch_N`） | 只写根 |
| **转换** | 根上的来源 | `sectional/`：标准 patch 组（自定义素体 + 标准 7 槽位 + 材质 patch 组） | 只写 `sectional/` 与 `vpk2patch.json` |
| **编译** | `sectional/` + 条件 | `Overwrite/`：最终生效 patch 组 | 只写 `Overwrite/` |
| **部署** | `Overwrite/` | 游戏的 `data/*.patch_*` | 只写游戏侧 |

### 1.2 两条硬边界

1. `sectional/` 是 **patch 级权威事实**：只由转换链路产出，形状固定。**任何其它链路都不许碰它**（换甲、section 开关、头身合并、材质开关、批量修复……全都不行）。要改它，只能另立一次**明确授权的迁移**。
2. `Overwrite/` 是**唯一的生效层**：只由编译产出。调用方不许自己往里面写。

### 1.3 目录名与覆盖顺序

- 基底目录**现在叫 `sectional/`**。2026-11 从 `vpk2patch/` 改名：老库里该目录仍在，**读侧两个名字都认**
  （`ModContentLayerNames.Sectional` / `LegacySectional`），`ModContentLayoutMigration` 做一次性改名。
  老库文档里写的 `vpk2patch/` 当**当时的事实**读，不当"今天该往哪写"。
- 同名文件的覆盖顺序是 **根 → `sectional/` → `Overwrite/`**，最后一层赢（`ModNodePayloadLayout.EnumerateEffectiveFilesByName`）。
- `Overwrite/` 是**替换层**：只有同名载荷存在、且它自身是一份可读的 patch TOC 时才顶替；
  孤立残留不生效——否则 `Overwrite/` 里任何残留文件都会变成一次额外部署。

## 2. 编译基座（本轮落地）

`HD2ModCore/Infrastructure/ModCompileService.cs`：

```csharp
CompileAsync(modSelector, modDirectory, context,
             targetArchiveIds?,        // 条件①：目标护甲（null = 读 wardrobe.json）
             disabledSections?,        // 条件②：section 开关（null = 读 wardrobe.json）
             onStage, ct)
```

- **条件③**（材质开关）来自 Mod 自己的设置文件（`enabledMaterialPatches`），由物化器读取。
- 顺序固定：有目标 → 换甲（同时把 section 开关烘进去）；随后把启用材质装进 `Overwrite/`。
- **只读配置**：目标护甲 / 开关是用户意图，只能由"用户做了那个动作"落盘。编译不写 `wardrobe.json`、不写设置文件。
- **不做增量、不做缓存**：每次从 `sectional/` 完整算一遍。优化留到链路稳定之后，单独做。

## 3. 现在的调用关系

| 入口 | 现在 |
|---|---|
| 详情页「切换」/「应用」 | 改条件 → **调用编译基座** |
| 详情页「重构建」 | **转换（来源 → sectional）→ 编译**，两段分明 |
| `vpk rekey`（CLI） | 底层命令（编译基座里"条件①"的实现），保留 |
| `vpk2patch`（CLI） | 底层命令（转换段），保留 |
| 批量修复 / `HostDerivedOverwriteService` | **未动**，另行处理（见 §5） |

## 4. 头身合并：编译期的一个条件（2026-11-08 重做）

**它是什么**：让编译器在编译期把「原本属于头 unit 的注册件」**当作挂在胸部槽位第 1 层**。

**放在哪**：只在编译期，只改 `Overwrite/`。`sectional/` 里那份 `custom-model.json` 是权威留档，一个字节都不动。

**顺序**（用户给定，代码照此实现）：

1. 按 section 开关决定烘哪些组件（烘的是 `Overwrite/` 里那份 unit）；
2. 看目标：
   - **自定义素体** ⇒ unit 键不变；头件在 `custom-model.json` 里从 `slot 0 / layer 0` 改到
     `slot 2 / layer 1`（unit id 不变，另留 `movedFrom: "slot0/layer0"`），
     写一份新的到 `Overwrite/custom-model.json`；
   - **原版护甲** ⇒ 这里只记下来，留到第 3 步；
3. 替换 unit 键：来源 unit ↔ 目标护甲 unit（目标就是自身素体时不变）；
4. 材质：装启用的材质 patch 层。

**生效优先级**（这一条以前是坏的）：`Overwrite/custom-model.json` 是**实际生效**的那份，
`sectional/` 的是权威留档。所以

- `CustomModelCatalog` 读文件时 **`Overwrite/` 排第一**（头身合并改的就是它）；
- `VpkCustomModelFile.TryReadKey/TryReadName`（身份与名字）**仍以来源产物为准**，`Overwrite/` 只作兜底
  ——派生文件不能定义"这具素体是谁"，否则改名会被下一次编译还原；
- 按 key 找持有者（改名 / 归属）时**跳过 `Overwrite/` 里的派生副本**。

**关掉合并时必须删掉派生那份**：否则生效层永远读上一次合并过的布局，
用户看到"开关翻回去了、游戏里却没变"。物化器重开目录本来会带走它，显式删除是兜底。

**`vpk2patch --head-merge`** 仍是 CLI 实验 / 救急，并且和上面这条**不是一回事**：
那条把合并**烘进 `sectional/` 本体**（协议外产物），两个 CLI 开关都会打警示。
它只用于救急，常规链路不走它。


## 5. 明确未做（避免顺手扩大范围）

| 项 | 为什么先不做 |
|---|---|
| `HostDerivedOverwriteService` / 批量头盔合并页 | 是第三处 head-merge 实现，牵到装饰链与批量修复；要单独定"并入编译还是删除" |
| `physbone embed-mod` | 把 `HD2PBX01` 容器分别写进**根 / `sectional/` / `Overwrite/`** 三处含 Unit 的 patch（`HD2SourceMigration/VpkConversion/PhysBoneContainerEmbedder.cs`）。它是 `sectional/` 与 `Overwrite/` 的**双重直接写者**，动机是"`Overwrite` 是在 `sectional` 上重建出来的，尾附容器不会自己跟过去"。编译侧其实已经能覆盖（`CanonicalPatchWriter` / `PatchArchiveWriter` 都走 `PhysBoneCarrier.CarryFromAnyAsync`），所以它多半该收敛成"物化时带走容器"，但要不要动是单独的事 |
| 编译层优化（增量 / 缓存 / 只重算变化部分） | 用户明确要求先跑通链路，优化单独做 |
| `Overwrite` 的多写者收敛 | 本轮只让 GUI 与重构建走编译基座；装饰/折并/批量修复/physbone 嵌入仍是直接写者 |

## 6. 导入链路（同日落地）

导入只剩一件事：**把来源放进条目**。转换与编译都由重构建完成。

```
解压 → 按 planner 分组（模型 + 它自己的材质包）→ 每组一个「只含 .vpk 的目录」
     → 导入成条目 → 对每个新条目跑一次标准重构建（并发 5）
```

- 重构建会用**默认编译参数**编出 `Overwrite/`（没有目标护甲 → 自定义素体形态；材质按设置文件），
  所以这条链不需要任何额外代码。
- 「同一来源再导入」= 把新 VPK 换进已有条目的根 → 重构建；不新建条目、不动节点身份。
- 删掉的东西：每 VPK 一个转换任务、`--treat-as-list`、材质源清单、produced package 分发、
  材质 Option 关系挂载。
- "根上只有 `.vpk` 也算合法条目"要三处同时认：`ContainsImportablePayload`、
  `ObjectTreeImporter` 的节点接受条件、`PlanHeuristicImport` 的节点筛选。

## 7. 还没做

见 §5 —— **"未做清单"只有一张表**，别在这里再抄一份（两份迟早会漂移，而且读者不知道信哪份）。
按优先级：先收 `Overwrite/` 的其余直接写者（装饰、折并、批量修复、`physbone embed-mod`），编译层增量/缓存排在链路稳定之后。

