# CLI 分层边界评估 与 基层能力落位方案

> 日期：2026-10-14 · **只做评估，未改任何代码**
> 回答用户的问题：「core 是核心，负责统筹，与消费者 modmanager/modcli 交互，与生产者 adaptation、以及后续可能的 L4D2support 交互」——**这个架构行不行？**

---

## 0. 结论（先给判断）

**架构方向可行，而且现状已经基本符合你的设想。** 但有 **两处缺口**，如果不补，
新建 L4D2Support 项目也解决不了问题——**只会把"直连 SourceModel"变成"直连 L4D2Support"。**

一句话：**先定接口，再建项目。**

---

## 1. 实测依赖图（来自各 csproj 的 ProjectReference）

```
Decryptor            (叶子)
HD2GameItemAssets    (叶子)
HD2SourceModel       (叶子)          ← 源格式解析：VPK / MDL / VVD / VTX
HD2ModAdaptation     (叶子)          ← HD2 重建：Analysis / PatchReconstruction / Processing
        ↑
HD2ModCore   ──引用──> HD2ModAdaptation, HD2GameItemAssets, Decryptor
        ↑
HD2ModCli    ──引用──> HD2ModCore, HD2SourceModel        ← ⚠️ 直连 SourceModel
        ↑
HD2ModManager ──引用──> HD2ModCore, HD2ModCli

HD2SourceMigration ──引用──> HD2SourceModel, HD2ModAdaptation   ← ⚠️ 孤立，无人引用
```

**对照你的设想：**

| 你的设想 | 现状 | 判定 |
|---|---|---|
| Core 引用生产者（adaptation） | Core → Adaptation ✅ | **符合** |
| Core 与消费者（manager/cli）交互 | Manager/Cli → Core ✅ | **符合** |
| 生产者不反向依赖 Core | Adaptation 无任何引用 ✅ | **符合** |
| 消费者只通过 Core 访问能力 | **Cli 直连 Adaptation（6 个文件）+ SourceModel（1 个文件）** | **❌ 缺口** |

---

## 2. 两处缺口（这是本轮最重要的发现）

### 缺口 1：Core 接口层「重读轻写」

Core/Application 现有 **89 个接口**，其中 patch 相关的是：

- ✅ **读**：`IPatchTocScanner`、`IPatchEntryPayloadReader`、`IPatchStateScanner`、`IPatchFileIndexBuilder`
- ❌ **写**：**一个都没有**（`PatchArchiveWriter` / `PatchSubsetWriter` 没有对应接口）

**后果**：上一轮我加 `patch edit` / `patch compose` / `patch hide-unit` 时，
**找不到 Core 的写接口，只能让 CLI 直连 Adaptation 的具体类型**（`CliPatchPrimitives.cs`）。
这不是我图省事，是**接口层确实缺了这一块**。

### 缺口 2：Core 完全没有「源侧」接口

Core/Application 里**没有任何 Source 相关接口**。所以 CLI 的 `source model inspect` 命令
只能直接 `using HD2SourceModel` 调 `SourceModelInspector.Inspect(...)`。

**后果**：这正是"CLI 局限性"的架构根源。你要加 L4D2Support，如果 Core 还是不给接口，
CLI 依旧会直连它。

### 补充：为什么"分层"在编译层面拦不住

C# 的 `ProjectReference` 是**传递的**——Cli 引用了 Core，就能看到 Core 引用的 Adaptation 的
public 类型。所以"Cli 直连 Adaptation"**编译能过、也没有警告**。
⇒ **分层只能靠约定，不能靠编译器**。这一点决定了下面的建议形态。

---

## 3. 对「新增 L4D2Support 项目并由 Core 引用」的建议

### 3.1 现状：已经有两个 Source 项目了

| 项目 | 内容 | 定位 |
|---|---|---|
| `HD2SourceModel` | `VpkArchive`、`SourceModelReader`、`SourceVertexReader`、`SourceGeometryBuilder`、`SourceModelInspector`、`Mat4`/`Vec3` | **源格式解析**（读） |
| `HD2SourceMigration` | `Hd2Rig`、`Hd2RigReader`、`SourceToHd2RigMap` | **源→HD2 骨架映射**（转换） |

**`HD2SourceMigration` 目前是孤立的**——除了它自己的测试项目，没有任何项目引用它。

⇒ **建议：不要新建第三个项目。** 先判断这两者是不是你说的"L4D2 支持的部分"：
- 如果是 → 把它们**统一归到 L4D2Support 名下**（合并或改名），而不是再造一个。
- 如果不是（比如 SourceMigration 只是历史遗留）→ 明确它是要接入还是废弃。

**避免出现第三个 Source 项目**，否则"哪个才是 L4D2 的入口"会长期混乱。

### 3.2 关键前提：先定接口，再建项目

新建 L4D2Support 必须**同时**在 Core/Application 定义接口，例如：

```
ISourceModelInspector       ← 源模型检视（VPK 里的 MDL/VVD/VTX）
ISourceRigMapper            ← 源骨架 → HD2 骨架映射
```

然后 Core/Infrastructure 提供实现（包装 L4D2Support），CLI 只调接口。

**否则**：Core 引用了 L4D2Support（编译依赖成立），但 CLI 仍然会直连 L4D2Support 的类型——
问题原封不动。

### 3.3 现成的正确模板

Core 里已经有一个**教科书式的正确做法**，可以照抄：

```
IAdvancedUnitAnalysisProducer          (Core/Application 定义接口)
        ↑ 实现
AdvancedUnitAnalysisProducer           (Core/Infrastructure，using HD2ModAdaptation.Analysis)
        ↑ 由工厂暴露
CoreServices.CreateAdvancedUnitAnalysisProducer()   (CoreServices.cs:100)
```

**Core 定义接口 → Infrastructure 实现并包装 Adaptation → CoreServices 工厂暴露 → CLI 调接口。**
新能力都该走这条链。

---

## 4. 基层能力落位表（P0~P4 在这个架构下应该改哪里）

| 项 | 改哪个项目 | 需要新接口？ | 说明 |
|---|---|---|---|
| **P0** 修 bundled 分区读取 | **HD2ModCore/Infrastructure** `GameDataPackageResolver.cs` | 否 | 纯 bug 修复 |
| **P1** `unit show/geometry/bones/vertices` | **Core/Application 加 `IUnitMeshInspector`** → Core/Infrastructure 实现（包装 Adaptation 的 `PatchUnitMeshReader`/`UnitMeshModel`/`UnitGeometryFactsBuilder`）→ CLI 调接口 | **是** | 见下方注意 |
| **P2** `patch diff` / `patch audit` | 同上，`IPatchWorkspaceInspector` | 是 | 包装 Adaptation 的 `PatchWorkspace` |
| **P3** `patch material-conflicts` | 同上，`IMaterialConflictInspector` | 是 | |
| **补丁** Core 补 patch 写器接口 | `IPatchArchiveWriter` / `IPatchSubsetWriter` | 是 | **收敛现有 CLI 直连 Adaptation 的 6 个文件** |

### ⚠️ P1 的一个关键注意点

`IAdvancedUnitAnalysisProducer` **已经存在**，但它的输入是 **`ModNode`（mod 库里的节点）**，
不是"任意 patch 文件路径"。而 P1 要的是**面向任意 patch 文件的检视**。

⇒ **不要硬塞进 `IAdvancedUnitAnalysisProducer`**（职责不同：一个是"库内节点分析"，一个是"任意文件检视"）。
**新增独立接口更干净。**

---

## 5. 建议遵守的三条规则

1. **依赖方向单向**：`Manager/Cli → Core → {Adaptation, L4D2Support, GameItemAssets, Decryptor}`。
   生产者**不得**反向依赖 Core；消费者**不得**直接依赖生产者。

2. **新能力必须走接口链**：Core/Application 定接口 → Core/Infrastructure 实现并包装生产者 →
   `CoreServices` 工厂暴露 → 消费者调接口。（照抄 `IAdvancedUnitAnalysisProducer` 的模板。）

3. **现有直连不专门重构，但遇到就顺手收敛**。
   现在有 6 处（Cli→Adaptation）+ 1 处（Cli→SourceModel），含我上轮加的 1 处。
   逐个专门重构成本高、风险大；建议**新代码一律走接口**，旧代码遇到再改。

---

## 6. 务实 vs 严格：需要你定一个调子

| 选项 | 做法 | 代价 | 收益 |
|---|---|---|---|
| **A. 务实** | 维持现状，CLI 可直连 Adaptation 的具体类型（已有 5 处先例） | 分层不纯，但改动小 | 快 |
| **B. 严格** | CLI 只通过 Core 接口；补 patch 写器 + Source 接口，并逐步收敛 7 处直连 | 改动大 | 架构干净，L4D2Support 才能真正"由 Core 引用" |

**我的建议：B 作为目标，A 作为过渡。** 具体落法：
- **新功能（P1/P2/P3）一律走 B**（Core 定接口）——反正它们是新代码，没有历史包袱。
- **旧代码走 A**（不专门重构，遇到再改）。
- **L4D2Support 立项时，同步把接口定好**（这是 B 的关键一步）。

---

## 7. 证据索引

| 主题 | 位置 |
|---|---|
| 依赖关系 | 各 `*/[Project].csproj` 的 `ProjectReference` |
| Core 接口层（89 个） | `HD2ModCore/Application/` |
| 正确模板 | `IAdvancedUnitAnalysisProducer.cs` + `Infrastructure/AdvancedUnitAnalysisProducer.cs` + `CoreServices.cs:100` |
| 组装工厂 | `HD2ModCore/Infrastructure/CoreServices.cs`（348 行，`CreateXxx()` 静态方法） |
| 缺口 1 实例 | `HD2ModCli/CliPatchPrimitives.cs`（直连 `PatchArchiveWriter` 等） |
| 缺口 2 实例 | `HD2ModCli/CliApplication.cs:1879`（`SourceModelInspector.Inspect`） |
| 孤立项目 | `HD2SourceMigration`（仅被自身测试引用） |
| 基层能力缺口清单 | `docs/cli/HD2ModCli-riglab-gap-analysis.md` |
