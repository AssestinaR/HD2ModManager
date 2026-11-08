# Blender Join 语义在 C# 跨护甲重建中的复刻计划

> ⛔ 状态（2026-11-08）：本篇是**设计计划**，不是现有实现清单。文中出现的
> `SdkStyleTargetShellUnitReconstructor`、`SdkStyleMeshReencoder`、`SdkStyleVertexStreamPlanner`、
> `CrossArmorTransferCandidateService`、`CanonicalSectionPlanner` 在源码里 **0 命中**（均已删除）；
> 现行实现是 `HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/` 下的 `CanonicalUnitRebuilder` /
> `CanonicalMeshSemanticMerger` / `CanonicalMeshPreparation` / `CanonicalPatchSession` / `CanonicalReplacementPlan`。
> 计划本身要表达的设计约束（几何策略二选一、compatibility key 分桶、四层验证）仍然有效。

## 目标

将当前跨护甲/批量 Patch 重建从“按 source section 位置投影”升级为可解释的 Blender-like canonical mesh join/re-encode 流程，保留过去实际需要的多来源 section 合并能力，同时避免 `sourceIndex % targetSectionCount` 造成的无语义回绕。

本计划必须区分两种不同目标：Blender 的 `minimal + fixed` 是“保留 active target 几何并追加 source 几何”，而当前跨护甲 target-shell 路径通常是“保留当前目标 Unit 结构、用 source 几何替换映射 target mesh，并将未映射 mesh minify”。后续实现前必须为每个调用场景明确选择其中一种语义，不能将两者混称为同一个算法。

本计划不是复刻 Blender 全部 Mesh 编辑器，而是复刻 `autofix Tools` 中：

```text
current target/minimal mesh（active object）
        +
source fixed mesh
        ↓
Blender object.join()
        ↓
统一 mesh/material slot/vertex group 数据
        ↓
HD2SDK 按最终 polygon material slot 导出 Unit
```

## 已确认的事实

### 自动修复入口

[docs/autofix Tools/__init__.py](autofix%20Tools/__init__.py#L617-L632) 将 `minimal_mesh` 设为 active object，然后调用 `bpy.ops.object.join()`。因此目标壳是最终对象主体，source 几何被追加到目标对象，而不是直接覆盖源 Unit 的二进制结构。

该文件中的 `bpy.ops.mesh.merge(type='CENTER')` 仅用于把未修复目标压缩成极小网格，不是 source/target section 合并算法。

### Blender Join 的真实职责

当前源码的核心实现位于 [mesh_join.cc](blender-main/source/blender/editors/mesh/mesh_join.cc#L486-L850)：

- 按对象计算 vertex/edge/face/corner ranges，并通过 offset 拼接拓扑；
- 将 source 坐标变换到 active object 局部空间；
- 按 Blender `Material*` identity 合并材质 slot，并重写 face `material_index`；
- 按 vertex group 名称建立并集，重映射权重中的 group index；
- 拼接 normal、UV、color 和其他属性；
- 不进行空间焊接或自动去重顶点。

### SDK 导出职责

HD2SDK 的 [unit.py](HD2SDK-CommunityEdition-3.8.0/stingray/unit.py#L1603-L2150) 接收已经统一的 Blender Mesh，并在导出前继续执行 `PrepareMesh()` 的 seam/sharp-edge 拆分、三角化和权重规范化：

- `GetMeshData()` 按最终 polygon 的 `material_index` 归组；
- 每个 Blender material slot 生成一个 `RawMaterialClass`；
- `Serialize()` 根据 `RawMesh.Materials` 重建 Unit sections；
- 重新写出 section index range、vertex/index buffer offset 和 stream metadata。

SDK 不知道 source Unit 与 target Unit 之间的 section 对应关系，也不会直接操作 Unit 层的 source→target section mapping。

## 核心设计结论

### 不再使用无语义 modulo 映射

当前 [SdkStyleMeshReencoder.cs](../HD2ModAdaptation/PatchReconstruction/UnitMesh/SdkStyle/SdkStyleMeshReencoder.cs#L255-L269) 中的 `sourceIndex % targetSectionCount` 只能作为历史兼容痕迹，不能作为通用映射算法。

> ⛔ 已过时（2026-11-08）：`SdkStyle/SdkStyleMeshReencoder.cs` 已不存在（该路径与类名源码 0 命中）。
> 结论（modulo 不是通用映射）仍然成立；现行落点是 `UnitMesh/Canonical/`。

但不能简单删除“合并能力”。应将其替换为显式的 many-to-one `SectionMergePlan`，允许：

```text
source section 0 ─┐
                  ├─ output material slot/section A
source section 1 ─┘
```

### C# 的正确抽象

新增纯内存阶段 `CanonicalMeshJoiner`，输出 Blender-like canonical mesh，而不是直接把 source section 写入 target section：

```text
source/target RawMesh
        ↓
CanonicalMeshJoiner
        ↓
canonical vertices + triangles + material slots + bone groups
        ↓
output section plan
        ↓
SdkStyleVertexStreamPlanner / SdkStyleMeshReencoder
        ↓
UnitMeshWriter
```

> ⛔ 已过时（2026-11-08）：这段管道里的 `SdkStyleVertexStreamPlanner` / `SdkStyleMeshReencoder` 都已不存在
> （0 命中）；`CanonicalMeshJoiner` 是当时提议的名字。`UnitMeshWriter` 仍存在。
> 现行链路见 `HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/`。

建议区分以下概念，不得用一个 ID 混用：

- `BlenderMaterialIdentity`：对应 Blender `Material*` 的语义；
- `Hd2MaterialAssetId`：Material 资源条目 ID；
- `UnitMaterialSlotId`：Unit section/material slot 的短 ID；
- `OutputMaterialIndex`：最终 MeshInfo section 使用的索引；
- `BoneHash` / `CanonicalBoneHash`；
- `BoneInfoRemapIndex`：目标 Unit 中的 fake index 映射。

### RS-40 暴露的额外边界

RS-40 的失败输出为 source `18462` 个三角形，但 output sections 为
`[18462,0,3269,522]`，说明旧 target-shell 路线已经复制了 source 几何，
但没有从最终几何集合中排除被替换 target mesh 的残余 target triangles。这个问题
不是 section 数组上的单个特殊值，也不能通过恢复或调整 ordinal/modulo 映射解决。

因此 canonical IR 必须先确定几何策略，再进入 section lowering：

- `Append`：保留 active target 几何，并追加 source 几何，等价于 Blender active
        object + `object.join()` 的几何语义；
- `ReplaceMappedTarget`：保留 target Unit 的输出 ABI、LOD、TransformInfo 和未替换
        mesh，但对当前映射 mesh 的最终几何只保留 source canonical polygons；被替换的
        target polygons 不得继续进入任何 output bucket。

RS-40 属于 `ReplaceMappedTarget`，不是简单的 `Append`。两种策略必须共用 canonical
IR、材质/骨骼归一化、target encoder、writer 和四层验证，但不能共用“保留哪些
polygon”的隐式默认值。

当前 `CanonicalSectionPlanner` 的 source→target ordinal assignment 只能作为历史
诊断原型，不能作为通用 join 计划。跨护甲 transfer 中：

> ⛔ 已过时（2026-11-08）：`CanonicalSectionPlanner` 本身已不存在（源码 0 命中），
> “它只能算历史诊断原型”这句话今天没有对应物；下面的四条 transfer 事实约束仍然有效。

- source `MeshId`、source LOD 和 source stream 是 provenance/输入事实，不要求与 target
        identity 相等；
- source `GroupIndex` 不要求等于 target `GroupIndex`，最终 `GroupIndex` 必须由 target
        output contract 决定；
- source raw stream 可以不同，但 source attributes 必须能被明确重编码到 target ABI；
- source/target 的 identity、LOD 和 stream 仍必须记录，并在不可解释时给出结构化诊断，
        不能伪造相等值来通过 gate。

`OutputBucketPlanner` 的输入应是已经确定几何策略的 canonical polygons，输出 bucket
至少包含 target material binding、target `GroupIndex`/render state、target bone-remap
context、target ABI 和 target LOD。它不应再通过 source/target section ordinal 推断
输出语义。

## 数据模型建议

### `CanonicalMeshJoinInput`

包含：

- active target/minimal `UnitRawMeshData`；
- source fixed `UnitRawMeshData`；
- target/source `UnitMeshModel`；
- source→target mesh-space transform；
- source material bindings；
- source bone hash 解析结果；
- target stream ABI；
- target LOD、MeshInfo、BoneInfo identity。

### `CanonicalMaterialSlot`

包含：

- stable internal identity；
- HD2 material asset ID；
- Unit slot ID；
- target/source provenance；
- conflict state；
- whether source material propagation is allowed。

材质匹配必须明确记录。相同数字 ID 不自动等同于 Blender 相同 `Material*`；不同来源的同一 HD2 material asset 是否可复用，需要由 Unit/material closure 规则确认。

### `SectionMergePlan`

每个 output section 记录：

- output section index；
- output material index/slot ID；
- source section members；
- target section template；
- compatibility key：material binding、`GroupIndex`/render state、BoneInfo-remap context、stream ABI 和 LOD/mesh identity；
- final group index；
- active bone hash union；
- final BoneInfo remap index；
- material decision：source、target、merged、fallback 或 rejected；
- vertex split requirement；
- diagnostics/reason。

计划必须可序列化并同时传给：

- canonical join；
- vertex/bone encoder；
- material propagation；
- writer；
- staged verifier。

## Canonical Join 算法

### 阶段 A：确定 active target 语义

1. 以 current Game Data target Unit 为结构主体；
2. 保留 target MeshInfo、TransformInfo、stream、LOD 和 Unit 版本；
3. 将 target/minimal mesh 作为 active mesh；
4. 禁止直接保留旧 source Unit 或旧 target metadata。

### 阶段 B：转换并追加几何

对 source mesh 的每个 vertex：

```text
source local position
    → source object/mesh space
    → target active mesh space
```

使用与 Blender 等价的 transform：

$$
M_{source\to active}=T_{active}^{-1}T_{source}
$$

position 使用该矩阵；normal/tangent 使用正确的法线矩阵，不能简单套 position 矩阵。

Join 只追加索引并加 offset，不做空间焊接。是否后续进行顶点合并必须是独立、显式的优化，不得隐含在 join 中。

### 阶段 C：构建最终材质 slot

1. 收集 active target 与 source 的 material bindings；
2. 建立显式 source/target material-to-output-slot 映射；
3. 对同一 output slot 的所有 polygon 记录 provenance；
4. 材质冲突不得静默吞掉：
   - 明确选择 target material；
   - 明确允许 source material；
   - 或拒绝该合并。

### 阶段 D：按 polygon 重建 section

不要以原始 section 数组位置直接决定输出 section。

1. 将 source/target triangle 转成带 `OutputMaterialIndex` 的 canonical polygon；
2. 按最终 compatibility key 分桶，而不是仅按 material slot 分桶；同材质但不同 `GroupIndex`、BoneInfo-remap context、stream ABI 或 LOD/mesh identity 的面不能无条件折叠；
3. 对每个桶重新排列 triangles；
4. 生成 `Section.NumIndices` 和连续 `IndexOffset`；
5. 按当前 writer 的既定契约写出 mesh-global 或 section-local vertex range；在确认格式契约前，不承诺自动生成 section-local `VertexOffset`/`NumVertices`；
6. 保留明确要求保留的空 section，但不得残留旧的非零 metadata。

### 阶段 E：顶点拆分

顶点输出 key 至少应包含：

```text
source vertex
output material slot
output section
bone remap context
UV/normal/color seam context
```

同一个 source vertex 被不同 section 或不同 BoneInfo remap 使用时，必须允许复制成多个 output vertices。

### 阶段 F：骨骼合并

1. source vertex group/bone index 先解析为 source bone hash；
2. source bone hash 映射到 canonical target TransformInfo；
3. 按 output section 收集 active bone hash union；
4. 为每个 output material/remap context 建立 target BoneInfo remap；
5. 编码 vertex fake bone indices；
6. 验证每个 fake index 都能通过该 section 的 remap 解析。

不要直接合并 source/target 数字 bone index。Blender 的 join 是按 vertex group 名称合并，SDK 才把名称解析到 Unit TransformInfo/BoneInfo。

### 阶段 G：SDK 风格属性处理

第一阶段必须支持：

- position；
- normal；
- tangent/bitangent（若目标 stream 存在）；
- UV；
- color；
- bone weights/index；
- triangle topology。

Blender 的 shape key、multires、sculpt face set 等功能不属于第一阶段，除非 HD2 exporter 证明确实消费它们。

## 与现有 C# 组件的关系

### `CrossArmorTransferCandidateService`

> ⛔ 已过时（2026-11-08）：这个类名在源码里 0 命中（已删除）。现行编排入口是
> `HD2ModCore/Infrastructure/CanonicalCrossArmorOrchestrator.cs`，按 `UnitMesh/Canonical/` 的
> `CanonicalReplacementPlan` 走。下面几条职责要求本身仍然成立。

负责：

- source/target Unit 选择；
- LOD family identity；
- 生成并冻结 `MeshTransferPlan`；
- 记录 source/target archive 和 generation；
- 输出明确的 section/material/bone 诊断。

不得在验证阶段重新推导 source mesh 或 LOD mapping。

### `SdkStyleTargetShellUnitReconstructor`

> ⛔ 已过时（2026-11-08）：该类已删除（源码 0 命中）。它在这份计划里的角色现在由
> `UnitMesh/Canonical/CanonicalUnitRebuilder`（+ `CanonicalMeshPreparation` 做未替换 mesh 的
> minify）承担。下面几条职责要求仍然成立。

负责：

- target shell 初始化；
- 未替换 target mesh minify；
- 调用 canonical join/reencode；
- 保持全量 target mesh coverage；
- 将最终 plan 传给 writer 和 verifier。

### `SdkStyleMeshReencoder`

> ⛔ 已过时（2026-11-08）：该类已删除（源码 0 命中）。现在的编码/合并职责由
> `UnitMesh/Canonical/CanonicalMeshSemanticMerger` 与 `CanonicalPatchSession` 承担，
> 仍然写 `UnitMeshWriter`。下面几条要求本身仍然成立。

负责：

- 根据 canonical output polygon/section 编码目标 stream；
- 生成 section-specific vertex split；
- 重建 BoneInfo remap；
- 生成连续 triangles/index buffer。

应删除其对 modulo mapping 的依赖，改为消费 `SectionMergePlan`。

### `UnitMeshWriter`

负责：

- 根据最终 RawMesh sections 重建 MeshInfo section metadata；
- 重写 shared stream 的全局 vertex/index offsets；
- 写出 material slot、BoneInfo、GPU sidecar；
- 不自行猜测 section/material/bone mapping。

当前 writer 对同一 RawMesh 的 sections 可能使用 mesh-global vertex range，而不是为每个 section 生成独立的顶点子范围。因此“按实际使用顶点生成 section-local `VertexOffset`/`NumVertices`”必须先作为单独的 writer/readback 设计确认，不能在 canonical join 阶段假定已经支持。

## 验证与安全门

### Plan-level gate

写出前阻断：

- source triangle 未映射或被映射多次；
- source material 决策未定义；
- output section 使用不存在的 material slot；
- output section 骨骼无法映射到 target TransformInfo；
- fake index 超出对应 remap；
- target stream 不支持所需 component；
- index 类型容量不足；
- section/LOD identity 不可解释；
- source/target transform 缺失或不可逆。
- source/target transform 的 determinant、normal/tangent 变换、镜像和 winding 处理无法解释；
- 同一 output material slot 的 BoneInfo-remap context 或 `GroupIndex` 不兼容。

### 四层验证

1. **Canonical memory model**：polygon、section、vertex、material、bone provenance；
2. **TOC**：MeshInfo section、MaterialIndex、GroupIndex、offset/count；
3. **GPU sidecar**：vertex/index buffer range、最大 index、stride、index width；
4. **Readback**：重新解析输出，验证 section、material、bone remap 和可见几何。

任何一层失败都不得提交正式 Patch。

## 最小实验矩阵

### 实验 1：材质 slot

两个三角形对象：

- 相同 HD2 material asset；
- 不同 Blender-like identity；
- 不同 source section。

比较 Blender join 后 slot 数、polygon material index 和 SDK 导出 section 数。

### 实验 2：骨骼组

两个对象使用：

- 相同骨骼组名称但不同源 group index；
- 不同骨骼组名称；
- 不同 material section。

比较 join 后 group index/weight，以及 SDK 导出后的 BoneInfo remap。

### 实验 3：空间变换

给 source/target 不同 object transform，比较 Blender join 后 position/normal 与 C# canonical join 结果。

### 实验 4：真实 N→1

构造 source 两 section、target 一个 output material slot 的 case，验证：

- 两组 triangles 均保留且各输出一次；
- material slot 明确；
- 顶点按 section/remap 正确拆分；
- index 连续；
- writer/readback 一致。

### 实验 5：真实失败边界

覆盖：

- 材质冲突；
- source active bone 不在 target TransformInfo；
- target BoneInfo 容量不足；
- target stream 缺少 source 必需 component；
- shared stream 多 mesh offset。

每个失败必须给出结构化原因，不能静默回退成错误几何。

## 实施顺序

### P0：语义定界与只读证据

- 明确当前调用场景属于“active target + source append”还是“target-shell source replacement”；
- 对照 Blender 4.3.2 与当前 `docs/blender-main` 的 join 关键语义；
- 定义 `CanonicalMeshJoinInput`、`CanonicalMaterialSlot`、`SectionMergePlan`；
- 以 compatibility key 而非 material-only 设计 section 计划；
- 实现 material/polygon/bone provenance；
- 增加 transform、determinant、normal/tangent ABI、shared stream、BoneInfo 容量的 fail-fast 诊断；
- 删除实际生产路径中的 modulo 合并；
- 暂不写文件，只生成诊断和不变量结果。

P0 的完成条件增加以下硬门槛：

- 先用 RS-40 复现并证明 `ReplaceMappedTarget` 的 canonical polygon 集合不含旧
        target residual triangles；
- planner 诊断必须记录 source/target Unit、MeshInfo、MeshId、LOD、stream、ABI
        token/hash、section GroupIndex 和最终策略；
- 不得以删除 `MeshContextIncompatible` 或复制 target `GroupIndex` 作为修复；
- 在 P0/P1 的内存模型和不变量测试通过前，不得重新开启生产 canonical gate，也不得
        将旧路线失败静默回退为旧 modulo 路径。

### P1：纯内存 canonical IR/join

- 实现 transform、拓扑 append、material slot remap、vertex group/bone union、section regroup；
- 明确 active geometry 是保留还是替换；
- 增加真实 N→1 测试；
- 对照 Blender join 后再经过 SDK `PrepareMesh()`/`GetMeshData()` 的结果，而不是只对照 join 后 Blender Mesh。

### P2：接入 encoder/writer

- 让 `SdkStyleMeshReencoder` 消费 canonical plan；
- 让 `UnitMeshWriter` 只写最终计划；
- 先决定 section vertex range 是 mesh-global 还是 section-local；若要求 section-local，先扩展 writer/readback；
- 对 normal/tangent 无可靠变换的情况明确阻断，不能静默复制；
- 增加 parse→write→parse 验证。

> ⛔ 已过时（2026-11-08）：P2 里的 `SdkStyleMeshReencoder` 已不存在（源码 0 命中）；
> 这一步现在落在 `UnitMesh/Canonical/`（`CanonicalMeshSemanticMerger` + `CanonicalPatchSession`）。

### P3：真实样本验证

- same-key 样本；
- 跨护甲多 LOD；
- 多材质/空 section；
- shared stream；
- culling proxy；
- 游戏内军械库、任务、动作和远近 LOD 验证。

### P4：性能和批量化

- 分批释放 source/target canonical model；
- 不同时持有全部完整 GPU 数据；
- 复用 immutable source intermediate；
- 保留每个候选的 plan/report，便于失败复盘。

## 不应做的事情

- 不以某个失败样本的 section/triangle 数写特例；
- 不用 `sourceIndex % targetSectionCount` 伪装映射成功；
- 不因为 writer 能输出就放宽验证；
- 不将 Blender Material identity、HD2 Material ID、Unit slot ID 混为一谈；
- 不直接复制旧 Unit/Composite Unit；
- 不在页面层自行解析或缓存稳定的派生事实；
- 不在没有四层验证的情况下提交正式 Patch。

## 当前阶段的决策

### 路线策略

保留当前 `target-shell replacement` 作为默认生产路线，不因 Blender `object.join()` 的交互速度而全面替换。该路线对普通跨护甲替换具有更小的几何范围、较低的内存成本，并适合批量处理。

额外增加一条受控的 `Blender-like canonical append/join` 实验路线，仅用于验证当前窄路线无法解释或无法安全输出的情况，例如：

- source section 必须执行真实 many-to-one 合并；
- 需要保留 target 几何并追加 source 几何；
- source/target material slot、vertex group 或 BoneInfo remap 无法通过直接 replacement 表达；
- 需要复现 Blender `object.join()` 后再由 SDK 导出的结果。

这里的“canonical append/join”是 canonical IR/lowering 基础设施的名称，不表示所有
失败任务都必须采用 append 几何语义。RS-40 等“source 替换 mapped target mesh”的
场景必须显式使用 `ReplaceMappedTarget`，否则会把 Blender append 与 target-shell
replacement 混淆，重新产生旧 target residual geometry。

两条路线不维护两套独立的输出基础设施，而是共享：

- canonical provenance 与 `SectionMergePlan`；
- material identity/asset/Unit slot 的分离模型；
- bone hash、TransformInfo、BoneInfo remap 构建；
- target stream encoder；
- `UnitMeshWriter`；
- staged write、四层验证和失败诊断。

差异只保留在几何输入语义：

```text
target-shell replacement：source 替换指定 target mesh，其他 target mesh minify
canonical append/join：保留 active target 几何，并追加 source 几何
```

实验路线在没有通过 plan-level gate、TOC/GPU/readback 验证和真实游戏样本前，不得成为默认路线，也不得静默接管失败任务。性能比较必须使用相同输入、相同输出承诺和相同验证等级；不能把 Blender 单次 `join()` 的交互耗时与 C# 全流程比较。

### 当前不立即修改生产代码

先完成：

1. Blender 4.3.2 与当前 `docs/blender-main` 的 join 关键语义对照；
2. 最小 Blender/SDK N→1 实验；
3. C# 只读 `SectionMergePlan` 原型和测试；
4. 专家复核计划与测试边界；
5. 分别测量当前默认路线和实验路线的读取、规划、编码、写出、验证、峰值内存与最终耗时；
6. 再分小步将实验路线接入现有重建链路。

在完成上述设计和内存模型前，现有 `CanonicalSectionPlanner` 不得继续扩展为生产
准入 gate；它可以保留用于失败样本诊断和回归测试，但生产接入应等待 canonical
polygon/bucket plan 取代 ordinal section assignment。

> ⛔ 已过时（2026-11-08）：`CanonicalSectionPlanner` 已不存在（源码 0 命中），
> “保留它做诊断/回归”这条今天无从执行；现行生产路径是 `UnitMesh/Canonical/`。

### 性能决策门

- 若 canonical append 只增加少量成本且能覆盖更多真实合法输入，可扩大其适用范围，但仍保留 replacement fast path；
- 若 canonical append 明显增加内存或写出成本，仅在 replacement plan 被结构性 gate 阻断时启用；
- 若两条路线最终产生相同 canonical IR，则保留一条共享 lowering/writer 路线，避免维护两套编码和验证逻辑；
- 任何“更快但无法解释”的输出不得替代严格验证。
