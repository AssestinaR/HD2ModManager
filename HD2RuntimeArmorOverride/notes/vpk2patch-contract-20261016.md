# `vpk2patch` 契约（2026-10-16 确认）

> 本文是**已确认的设计**，不是提案。落地前若与此处冲突，以本文为准。

## 1. 核心语义：一个 VPK = 一个 patch，只含它自己的资源

这是整个功能的根：**转换的单位是 VPK，不是"模型 + 一堆材质来源"。**

| 输入 VPK 的内容 | 输出 patch |
|---|---|
| 含 `.mdl`（模型 VPK） | **只有 unit**。零材质、零贴图资产 |
| 只有 `.vmt` / `.vtf`（材质 VPK） | **只有这个 VPK 定义的材质、以及它自带的贴图** |

管理器**不判断**"哪些 VPK 该一起用"，也不合并材质。它只忠实地把 VPK 逐个翻译成 patch；
用户怎么装那堆原始 VPK，就怎么装这堆 patch。

反例（当前 `source convert` 的行为，**必须改掉**）：拿一个模型 VPK，扫描整个 addons 目录，
最后产出一个合并材质包 —— 这就是"多带"。

## 2. 批处理队列：材质 VPK 从队列里找模型

`vpk2patch` 接受**一批** VPK：

```
hd2mod vpk2patch <vpk1> [<vpk2> ...] --output <目录> [--avatar-toc ...] [--game-data ...] --json
```

- 整批构成**解析集合**：材质转换时可以跨 VPK 解析贴图（一个材质的 VMT 和它引用的贴图
  可以来自不同 VPK —— 实测 A1 的 `white.vmt` 指向 D1 的 `white.vtf`）。
- **材质 VPK 可以从队列里找到模型 VPK**，因此 matcap 的**面积加权平均色**仍然算得出来
  （`ComputeMatCapMeans` 需要三角形）。模型 VPK 只作为几何来源参与计算，
  **不输出它的任何资产**到材质 patch 里。
- 每个 VPK 独立产出一个 patch；失败互不影响（一个 VPK 失败不拖垮整批）。

## 3. 一条命令，自动识别

**只有一个命令**，用户/agent 不需要思考用哪个：

- 队列里某个 VPK 含 `.mdl` → 按**模型**转换（出 unit patch）
- 只含 `.vmt`/`.vtf` → 按**材质**转换（出 material patch）

这正是"管理器导入流程对每个 VPK 调一次"所需要的形状，不需要判断类型。

## 4. 让 patch 之间对上的不变量：id 只由名字决定

```
材质 id = hash($basetexture 全路径 [+ "|emission"] [+ "|alphaclip"])
贴图 id = hash(VTF basename)
```

与"哪个 VPK 提供"无关。所以：

- **模型转换**只需要**解析出**每个材质槽名对应的 `$basetexture` 来**算 id**，
  不需要、也不应该把材质写进 patch；
- **材质转换**产出的正是这些 id。

因此 N 个 patch 能拼起来，而任何一份都不需要知道另一份的存在。
`--material-source` 的角色随之降级为**只用于解析名字**；输出由 scope 决定。

## 5. 要改的 C#（三处）

1. **`SourceMaterialSource`**：暴露每个 VMT / VTF 来自哪个 VPK
   （现在私有字段里已有 `(VpkPath, EntryPath)`，加 `TryGetVmtSource` / `TryGetVtfSource`）。
2. **`VpkPatchAssembler.AssembleAsync`**：加 `materialScope` —— 只**输出**源 VPK 命中 scope
   的材质与贴图；并允许输出 0 个材质资产（unit patch 依然合法）。
3. **CLI**：新增 `vpk2patch` 命令（批处理 + 自动识别），内部按第 1 节分派；
   保留 `source convert` 作为"模型专用、带合并材质"的旧路径（供 M1/M2 对拍与调试用），
   但管理器导入流程走 `vpk2patch`。

## 6. 验收标准

用 `【SHOP】Lunalice八人包101.7z`（已解压到 `E:\tmp\lunalice-shop`）做端到端：

- 队列 = `Lunalice_Zoey.vpk` + A1 + B1 + B2 + C1 + C2 + D1..D4 + E1..E3 + F1 + F2。
- 期望产出：1 个 unit patch（0 个材质、0 个贴图）+ 14 个材质 patch，
  每个材质 patch 的资源清单**恰好**等于该 VPK 里 VMT/VTF 的映射结果，逐一核对不多不少。
- 材质 patch 的材质 id 必须与 unit patch 里引用的 id 对得上（同一个 `$basetexture` → 同一个 id）。
- 单独转换任一材质 VPK（不带模型）时 matcap 平均色如何处理，见第 2 节：模型在队列里就算得出来。

## 7. 目标目录形状

```
Lunalice-RE2310-Release/
  9ba626afa44a3aa3.patch_0 (+gpu)   ← 纯 unit
  A1/   patch_1                     ← 只有 A1 的 69 个资源
  B1/   patch_2    B2/   patch_2    ← 同编号 = 二选一（大编号覆盖小编号）
  C1/   patch_3    C2/   patch_3
  D1/ … D4/        patch_4
  E1/ … E3/        patch_5
  F1/   patch_6    F2/   patch_6
```

用户必须同时启用：**unit + A1 + B 各一 + C 各一 + D 各一 + E 各一 + F 各一**。
（A1 与 B 不是二选一：它们的 VMT 集合不相交，各自负责一半材质槽。）
