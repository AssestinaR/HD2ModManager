# 另一线程工作流 → CLI 基层能力覆盖分析

> 日期：2026-10-14 · **修订版**（原版把任务理解成"帮另一线程做 L4D2 转换"，已按用户澄清重写）
> 本轮**只做分析**，未修改任何产品代码。
>
> **任务边界（用户澄清）**：本线程只负责 **mod 相关开发的基层能力**，把它做成方便 CLI 调用的形态。
> 不替另一线程做 L4D2→HD2 转换。另一线程后续会把 py 临时脚本里的能力**提取到 C# 类库**。
> 所以本轮要回答的是：**因为 CLI 有局限，另一线程被迫手写了哪些"本该由 CLI 提供"的基层能力？**

---

## 0. 核心结论

**不是"他们缺功能"，而是我们有一整套 C# 能力没有暴露成命令。**

C# 里**已经存在**一个完整的 **"Unit 数据视图"层**（网格 / 骨骼 / 顶点 / 流 / 几何事实 / 材质引用），
但 CLI 对它们的引用数几乎全是 **0**。于是另一线程只能在 Python 里**重新实现一遍**：

| 他们在 Python 里写的 | C# 里**早就有** | CLI 暴露 |
|---|---|---|
| `hd2_unit.py`（读写 Unit TocData+GpuData） | `PatchUnitMeshReader` → `UnitMeshModel` | **无** |
| `hd2_vertex.py`（顶点 ABI 解析） | `UnitVertexComponentValue` + `UnitStreamAbiProfiles` | **无** |
| `weight_half4.py`（权重编码/归一化） | `CanonicalMeshPreparation` 的权重归一化 | **无** |
| `_probe_unitdiag.py` / `_verify_patch.py`（unit 结构诊断） | `UnitGeometryFactsBuilder.Analyze` | **无** |
| `_probe_unitbones.py` / `_probe_boneinfo.py`（骨骼表） | `UnitBoneInfo` + `UnitBoneNamesReader` | **无** |
| `_probe_weightdiff.py`（逐字节差异对照） | `PatchWorkspace` + `PatchWorkspaceEntryChange` | **无** |

⇒ **这是"CLI 局限性导致手动实现基层能力"的直接证据**，也是本轮唯一值得动手的方向。

---

## 1. 边界判定标准

### 1.1 什么算"mod 开发的基层能力"（= 我们做）

**判定问题**：这个能力，是不是**任何一个做 HD2 mod 的人**都会需要，而与"源模型来自哪里"无关？

- ✅ **算**：读/写 patch 容器、读 unit 网格、看顶点/骨骼/权重、几何事实、patch 差异与健全性、材质/贴图引用与冲突。
- ❌ **不算**：解析 Source/L4D2 格式（VPK/MDL/VVD/VTX/VTF）、把 L4D2 模型拟合成 HD2 骨架、
  bodygroup 映射、转换算法的任何一环。

### 1.2 三分法

对每一项能力，判断它落在哪一类：

| 类别 | 含义 | 谁做 |
|---|---|---|
| **A. C# 已有 + CLI 未暴露** | 底座已就绪，只差命令层 | **我们（本轮重点）** |
| **B. C# 没有，需先把 py 能力搬进类库** | 算法/指标设计在另一线程 | 他们搬 → 我们再加命令 |
| **C. 与 HD2 mod 管理无关** | 源侧解析、转换算法 | 他们，不进 CLI |

---

## 2. 核心发现：C# 已存在、CLI 未暴露的基层能力（类别 A）

以下全部经过代码核实（`HD2ModCli/` 内引用数）：

| C# 能力 | 位置 | 提供什么 | CLI 引用数 |
|---|---|---|---|
| `PatchUnitMeshReader` / `UnitMeshReader` | `PatchReconstruction/UnitMesh/` | 解析 Unit 的 mesh/stream/material 结构 | 1（仅 `patch hide-unit` 内部用） |
| **`UnitMeshModel`** | `UnitMesh/UnitMeshModel.cs` | **完整网格模型**：骨骼表、流、mesh、截面、材质绑定、**逐顶点分量** | **0** |
| `UnitBoneInfo` / `UnitBoneRemap` | 同上 | 骨骼：真实索引、重映射、骨骼矩阵 | **0** |
| `UnitStreamInfo` / `UnitStreamComponentInfo` | 同上 | 流：顶点数、stride、索引数、分量表（Type/Format 名） | **0** |
| `UnitMeshInfo` / `UnitMeshSectionInfo` | 同上 | mesh：LOD、材质槽、截面、剔除包围盒、语义信息 | **0** |
| **`UnitGeometryFactsBuilder`** | `UnitMesh/UnitGeometryFacts.cs` | **几何事实**：每 mesh 顶点数/三角形数/可见性/质量分类（Placeholder…RenderableLod0） | **0** |
| `UnitBoneNamesReader` / `UnitBoneNames` | `UnitMesh/UnitBoneNames*.cs` | **骨骼 hash + 名字表**（`l_elbow` / `l_hand` 这类） | **0** |
| `UnitSourceVisibilityClassifier` | `UnitMesh/` | 区分"故意极小化的 unit" vs "含可见几何" | **0** |
| `UnitMaterialReferenceReader` | `UnitMesh/` | Unit 的材质引用 | **0** |
| `UnitStreamAbiProfiles` | `UnitMesh/` | 顶点流 ABI 配置表 | **0** |
| **`PatchWorkspace` + `PatchWorkspaceReader`** | `PatchReconstruction/PatchWorkspace/` | **patch 的完整内存模型**（每条目 Metadata + 三段字节） | **0** |
| `PatchWorkspaceEntryChange` | 同上 | 条目变更集：`Keep` / `Replace` / `Add` / `Remove` | **0** |
| `PatchOperationWorkspace` | 同上 | 操作 staging 工作区（`Stage` / `Reference`） | **0** |
| `CanonicalPositionDiagnostics` | `UnitMesh/Canonical/` | 位置诊断（stage 级、opt-in） | **0** |
| `HiddenUnitGenerator` | `UnitMesh/` | 隐藏 unit 生成（3 点极小化） | 1（`patch hide-unit`） |

**一句话**：`UnitMeshModel` 已经能给出**每个顶点的骨骼权重分量**（`UnitVertexComponentValue` 带
`Type` / `FormatName` / `FloatValues` / `UIntValues`）——**这正是另一线程手写 `hd2_vertex.py` + `weight_half4.py` 在做的事。**

---

## 3. 需求逐条重新裁定

把原报告的 R1~R7 按 §1.2 的三分法重新分类：

| # | 需求 | 裁定 | 理由 |
|---|---|---|---|
| **R2** | bundled 归档只写出 `.toc`，缺 `.gpu_resources`/`.stream` | **A（我们的 bug）** | 基层能力缺陷。已核实根因（见 §4.1） |
| **R3** | 隐藏占位 unit 丢"零索引 section"材质短 id | **A（我们的 bug）** | `CanonicalPlaceholderMinifier` / `HiddenUnitGenerator` 的不一致 |
| **R1** | 材质冲突检测命令 | **A** | 多包共存是 mod 管理通用问题；C# 已有材质/贴图 id 逻辑 |
| **R6** | 贴图 id 加包族命名空间 | **A** | HD2 材质/贴图 id 生成，属 mod 打包 |
| **R7** | 把权重判断搬进 C# 并自动断言 | **A + B 拆开** | 见下方拆解 |
| R4 | `opt\` 目录优先级 | **C** | 这是 **L4D2 源侧** VMT 搜索路径（`cdtextures`），不是 HD2 mod 管理 |
| R5 | bodygroup → 多 Unit | **C** | L4D2 `$BodyGroup` 专有；且卡在"游戏能扛多少 piece"未测区间 |

### 3.1 R7 的正确拆解（回应"我担心 R7 是另一个线程的工作"）

**你的担心是对的，但要拆成两半：**

| 部分 | 归属 | 说明 |
|---|---|---|
| **指标的"定义"与"阈值"** | ❌ **他们的工作** | "远端权重渗入量"、"30° 位移中位数"、"橡皮筋判据"—— 这是他们的领域判断，不该由 CLI 定义 |
| **读取原语**（顶点 / 权重分量 / 骨骼索引 / 三角形 / 位移计算所需的顶点坐标） | ✅ **我们的工作** | **C# 已经有了**（`UnitMeshModel` + `UnitVertexComponentValue`），只是没暴露 |
| 把指标算法搬进 C# 类库 | 他们搬 | 搬完后 CLI 再加 `unit weights profile` 这类命令 |

⇒ **本轮不该做 `unit weights profile`**（那要等他们的算法进 C#）。
**该做的是把 `UnitMeshModel` 暴露出来**——让他们能用 CLI 取到顶点/权重，而不必写 `hd2_unit.py`。

---

## 4. 建议（只保留"我们该做的"）

> 设计原则沿用已确立的"安全线"：**只读命令不取租约、不走四段式；产出成品只写 `--output` 新目录，绝不落库内。**

### P0 — 修 R2（1 处，收益最大）

`gamedata extract asset` 对 bundled 归档只写出 `.toc`。

**✅ 已核实根因**：`HD2ModCore/Infrastructure/GameDataAssetCatalogService.cs:157-160` 在 bundled 分支把
`asset.ArchiveId`（**基名**）原样传给 `GetPackageResourceAsync`；后者传到
`GameDataPackageResolver.cs:163` 的 `ReadBundledPackageRangeAsync`，在 **line 166**
直接 `_packages.TryGetValue(packageName, ...)` 查包表。
但 `bundles.nxa` 里 GPU/stream 分区是**带后缀的独立包条目**（`<id>.gpu_resources` / `<id>.stream`），
基名只命中 TOC 包；随后 line 170 的 `resourceOffset >= package.Size` 判定失败 → 返回 `null`。
症状与另一线程记录完全一致。

- **改法**：bundled 分支按分区拼后缀（TOC 用基名，GPU/Stream 加后缀）。
- **收益**：另一线程那段"自己实现 bundle 解析"的绕行代码可以删掉。

### P1 — `unit` 数据视图族（**本轮最大缺口，C# 已就绪**）

把 §2 表格里 CLI 引用为 0 的能力暴露成**只读**命令：

```
hd2mod unit show     <patch> --unit 0xID [--json]   # 结构总览：mesh/stream/骨骼/材质/自定义信息
hd2mod unit geometry <patch> --unit 0xID [--json]   # 几何事实：顶点数/三角形数/可见性/质量分类
hd2mod unit bones    <patch> --unit 0xID [--json]   # 骨骼表：名字 + hash + 层级 + 重映射
hd2mod unit vertices <patch> --unit 0xID --output <dir>  # 逐顶点分量导出（含骨骼权重分量）
```

- **输入**：`patch_0`（可选 `.gpu_resources`）+ 目标 unit key。
- **输出**：JSON + 人类可读双输出（沿用现有 `Emit` 风格）。
- **底座**：全部已存在，**无需新写解析代码**，只是包装。
- **安全线**：`show` / `geometry` / `bones` 纯只读；`vertices` 只写 `--output`。

### P2 — `patch` 差异与健全性族（对应他们的两个手写脚本）

```
hd2mod patch diff  <patchA> <patchB> [--json]   # 条目级 + 字节级差异（对应 _probe_weightdiff.py）
hd2mod patch audit <patch> [--json]             # 健全性不变量（对应 _verify_normalized.py 的一部分）
```

- **底座**：`PatchWorkspace` + `PatchWorkspaceReader` 已能读全量条目与三段字节，`diff` 是纯计算。
- **注意**：`_verify_normalized.py` 的 27 项里，**涉及原版基线对照**的部分依赖 GameData 抽取（先修 P0）；
  其余（容器 magic、range 越界、截面窗口重叠、材质存活）可以纯本地做。

### P3 — 材质层

```
hd2mod patch material-conflicts <patch...> [--json]   # R1：{ basename: [提供它的包], 是否同图, 建议 }
```

- **实现轻**：只需读各包 TOC 里 Texture 条目的 `file_id` 做交集，**无需解码像素**；"是否同图"用 sha256。
- **必须写进文档的坑**：材质 id 用 `$basetexture` **全路径**，贴图 id 用 **basename**，
  这个不对称正是"冲突"的物理基础（见 `MATERIAL_CONFLICT_ANALYSIS.md` §1）。

### P4 — R6 贴图 id 命名空间

无新命令，改 id 生成器；**必须可开关、不改既有 id**。

---

## 5. 明确不做（他们的工作 / 超出边界）

1. **R4 `opt\` 优先级** —— L4D2 源侧 VMT 搜索路径，不进 HD2 mod CLI。
2. **R5 bodygroup → 多 Unit** —— L4D2 专有，且卡在未测区间（20~32 piece）。
3. **R7 的指标定义与阈值** —— 他们的领域判断。
4. **`hd2_source.py` 那类源侧解析**（VPK/MDL/VVD/VTX/VTF）—— 源引擎格式，不进 CLI。
5. **不要把 289 个 Python 探针逐个 CLI 化** —— 约 230 个是一次性诊断，只固化 §2/§4 里的通用原语。
6. **不动 `tmp/rig-lab/`、`tmp/physbone-recon/`、`docs/l4d2-*.md`** —— 那是另一线程的现场。

---

## 6. 另一线程在做什么（背景，供参考）

把 L4D2 角色（Lunalice / Lilium / MourningVeil）转成 HD2 护甲，直接替换 RE-2310 躯干。
当前焦点是**第 72 轮"小臂橡皮筋"**：小臂是唯一"远端伙伴是整只手（17 根骨）"的关节，
而管线给"肘→腕"和"膝→踝"用了同一套平滑参数。量化证据：远端子树单独转 30°，
小臂表面位移中位数 **13.8~17.6 mm**，小腿 0.75~1.51、上臂 0.09~0.32。

**主管线 6 步里只有第 3 步 `equipment armor-normalize` 走了 CLI**，其余全是 Python（289 个脚本）。
这个比例本身就是"CLI 基层能力覆盖不足"的度量。

---

## 7. 证据索引

| 主题 | 位置 |
|---|---|
| C# Unit 数据模型（核心） | `HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshModel.cs` |
| 几何事实分析器 | `HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitGeometryFacts.cs` |
| patch 工作区模型 | `HD2ModAdaptation/PatchReconstruction/PatchWorkspace/`（9 个类） |
| R2 根因（已核实） | `HD2ModCore/Infrastructure/GameDataAssetCatalogService.cs:157`、`GameDataPackageResolver.cs:166` |
| R3 相关实现 | `UnitMesh/Canonical/CanonicalPlaceholderMinifier.cs`、`UnitMesh/HiddenUnitGenerator.cs` |
| 材质冲突判据（R1） | `tmp/rig-lab/MATERIAL_CONFLICT_ANALYSIS.md` |
| 小臂问题全貌 | `tmp/rig-lab/ROUND72_FOREARM_BRIEF.md` |
| CLI 现有能力 | `docs/cli/HD2ModCli-capability-map.md`（148 条） |
