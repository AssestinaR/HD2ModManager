# HD2 内嵌材质拆分与材质合并写出 Smoke 计划

**日期：** 2026-07-14  
**状态：** 写出前可行性验证计划；未进入正式产品功能  

> ⛔ 状态（2026-11-08）：上面那句「未进入正式产品功能」只描述 2026-07-14 当时的阶段。
> 本文 P0–P3 已经落地（`HD2ModAdaptation/PatchReconstruction/PatchSubsetWriter.cs`、`MaterialPackagingService.cs`），
> 拆分/合并写出也经高级详情「材质打包」入口进入产品（`MaterialPackagingApplicationService`、
> `MaterialPackagingPageViewModel`），见 [`material-texture-reference-design.md`](material-texture-reference-design.md) §9。
> §12 的接入顺序里，1–3（只读可行性审计 / 输出到指定文件夹 / 导入为普通 Mod）同样已完成；
> 4–5（候选材质推荐、一键打包与作者工作流）仍未做。本文其余 P0–P4 规则仍是这套写出器的验收依据，可照用。

**前置：** [material-texture-reference-design.md](material-texture-reference-design.md)  
**适用项目：** `HD2ModAdaptation`，隔离验证目录 `_diagnostics/MaterialPackagingSmoke/`

---

## 1. 目的

验证以下两个操作是否能在**不修改任何资源引用或资源 Payload**的前提下安全完成：

1. 将内嵌 Patch 中的 Material 与其 Texture 闭包拆到独立材质 Mod；
2. 将模型 Mod 与选定材质 Mod 的 Material/Texture 闭包合并成一个内嵌 Mod。

本计划只验证“资源容器级重组”，不验证材质编辑、Texture 重新压缩、随机 ID 生成或 Unit binding 改写。

核心不变式：

$$
\operatorname{Payload}_{\mathrm{output}}(A)
=
\operatorname{Payload}_{\mathrm{input}}(A)
$$

对每个被搬运的 AssetKey $A$，`TocData`、`StreamData`、`GpuResourceData` 必须逐字节不变；允许变化的仅是新 Patch 容器内的 TOC/sidecar offset、entry index 与类型计数。

---

## 2. 重要结论与部署假设

拆分结果是两个普通平铺 Mod 文件夹，例如：

```text
Cyber Armor-模型/
  9ba626afa44a3aa3.patch_0
  9ba626afa44a3aa3.patch_0.stream
  9ba626afa44a3aa3.patch_0.gpu_resources

Cyber Armor-材质/
  9ba626afa44a3aa3.patch_0
  9ba626afa44a3aa3.patch_0.stream
  9ba626afa44a3aa3.patch_0.gpu_resources
```

它们导入 Manager 后是两个普通 Mod。Manager 现有部署链按 Profile 顺序为文件组分配实际目标 Patch 序号；HD2 的资源引用不要求提供 Material 的 Patch 一定位于引用 Unit 的 Patch 之前。

因此，源 Patch 文件名相同不是拆分阻塞项。必须验证的是：

```text
模型 Mod + 材质 Mod 同时在 Profile 部署
```

后，最终有效资源图与原内嵌 Mod 相同。

---

## 3. 第一版绝对边界

### 3.1 允许

- 原样复制完整 entry 的 `TocData`、`StreamData`、`GpuResourceData`；
- 原样保留 AssetKey；
- 原样保留 entry 的 `Unknown1..4`；
- 重建 TOC type table、entry table、offset、size、entry index；
- 将 Unit、Material、Texture 按已确认的引用闭包分配到新 Patch；
- 使用当前 Patch entry 读取器、扫描器和轻量引用读取器审计结果。

### 3.2 禁止

- 生成新 Material/Texture ID；
- 改写 Unit 的 Material binding；
- 改写 Material 的 Texture ID 表；
- 编辑 Parent Material、Shader variable 或 Texture payload；
- 静默丢弃未知类型 entry；
- 仅凭 Material/Texture 类型或数量移动资源；
- 覆盖源目录、源 Patch 或原 Mod；
- 在未经游戏 smoke 前向 Manager 用户公开正式拆分/合并入口。

---

## 4. 所需隔离工具

新建独立 Console 项目：

```text
_diagnostics/MaterialPackagingSmoke/
```

该工具不能引用 `HD2ModManager`；可以引用：

```text
HD2ModAdaptation
HD2ModCore（仅路径、导入/导出或部署验证适配需要时）
```

工具应只接受显式输入和输出目录，不读取或修改当前用户 Mod 库，不启动正式部署。

建议命令语义：

```text
inspect <input-patch-group> <report-directory>
plan-split <input-patch-group> <report-directory>
write-split <approved-plan> <output-directory>
plan-merge <model-patch-group> <material-patch-group> <report-directory>
write-merge <approved-plan> <output-directory>
verify <original-or-plan> <output-directory>
```

实际命令名称可以调整，但必须坚持：

```text
inspect / plan 默认只读
write 仅写入新输出目录
verify 不写输入
```

---

## 5. 阶段 P0：真实样本审计与计划生成

### 5.1 样本选择

选择一个满足下列条件的真实内嵌材质 Patch 文件组：

- 至少有一个可读取 Unit；
- Unit 能读取 `Unit → Material`；
- 至少一个被引用 Material 在同 Patch 内；
- Material 能读取 `Material → Texture`；
- 所引用 Texture 在同 Patch 内；
- 三段 Payload 均可读取；
- 不选择已知旧版本、不可被当前游戏接受的 Patch 作为游戏兼容性主样本。

原始样本必须只读保存，输出永远放在 `_diagnostics` 下的新目录。

### 5.2 `inspect` 输出

`inspect` 输出 `report.json` 和可读 `report.md`，至少包含：

```text
Patch group 输入文件及 fingerprint
TOC layout / entry 数量 / type 数量
每个 entry：AssetKey、类型、三段 payload 长度、Unknown1..4
每个 Unit：Material bindings 与 slot
每个 Material：Texture IDs
解析 issue
所有未支持/未知类型
```

报告必须保存每条边的证据：Payload relative offset、slot 或 texture index、来源 Patch group。

### 5.3 `plan-split` 的精确规则

输入内嵌 Patch group 后：

```text
Model subset
  所有 Unit
  为保留 Unit 必需、但不属于待移动 Material/Texture closure 的资源

Material subset
  所有从保留 Unit 直接可达且在当前 Patch 内提供的 Material
  上述 Material 直接可达且在当前 Patch 内提供的 Texture
```

第一版必须保守：

- 若 Material/Texture 同时被未知资源类型引用，阻断；
- 若被移动 Material 仍被保留但未选中的 Unit 引用，阻断或将其一并处理；
- 若移动 Texture 同时被保留 Material 或未知资源引用，阻断；
- 若 Material/Texture 引用的私有依赖不在闭包内且不在 Game Data，阻断；
- Parent Material 默认报告为外部 Game Data 依赖，不自动移动；若证据显示 Parent 仅存在于 Patch 内，阻断，等待专门支持。

计划必须显式列出：

```text
保留 AssetKey
移动 AssetKey
外部 Game Data 依赖
未知/阻塞依赖
每条移动决定的引用证据
```

若存在任意阻塞项，工具只能产出计划，不得启用 `write-split`。

---

## 6. 阶段 P1：实现 `PatchSubsetWriter`

不要直接把现有 `PatchArchiveWriter` 当作最终拆包写入器。

现有 writer 为 Unit 重建设计，可保留源 `.stream` 全文件。拆分/合并需要一个紧凑、严格的子集写入器：

```text
PatchSubsetWriter
输入：
  一个或多个源 Patch group
  经审计批准的 AssetKey → 完整原始 Payload 集合
  header template 来源

输出：
  一个仅含选中 entries 的完整 Patch 文件组
```

### 6.1 `PatchSubsetWriter` 必须保证

1. 对每个选中 entry 重读完整三段 Payload；
2. 新 `.patch` 仅写选中 `TocData`；
3. 新 `.stream` 仅写选中且非空的 `StreamData`；
4. 新 `.gpu_resources` 仅写选中且非空的 `GpuResourceData`；
5. 所有 Stream/GPU entry offset 保持 64-byte 对齐；
6. 重新生成 type record count、entry table、entry index；
7. 保留每个 entry 的 `Unknown1..4`；
8. 不保留未选中 entry 的无主 sidecar 字节；
9. 不写源目录；
10. 输出后能够被 `PatchTocScanner` 和 `PatchEntryPayloadReader` 完整回读。

### 6.2 可复用实现

可复用：

- `PatchTocScanner`；
- `PatchEntryPayloadReader`；
- `AssetKey`；
- `PatchArchiveAdditionalEntry`；
- TOC layout/type table/entry record 的解析知识；
- 64-byte sidecar 对齐规则。

不可直接继承的策略：

```text
preserveOriginalStream=true
```

Subset writer 必须紧凑写出选中 entry 的 Stream/GPU 数据。

---

## 7. 阶段 P2：自动验证

每次 `write-split` 或 `write-merge` 后，工具必须自动执行以下验证；任何失败均将输出标记为失败产物，不得进入游戏 smoke。

### 7.1 容器回读

对每个输出 Patch：

- `PatchTocScanner` 能扫描；
- 预期 AssetKey 全部存在；
- 不存在未在计划中的 AssetKey；
- 每个 entry 的三段 Payload 均可读；
- type table count 与实际 entry 分组一致；
- entry index、offset、size 合法；
- sidecar offset 按 64-byte 对齐。

### 7.2 Payload Hash 恒等

对每个从源搬运的 AssetKey：

```text
SHA-256(source TocData) == SHA-256(output TocData)
SHA-256(source StreamData) == SHA-256(output StreamData)
SHA-256(source GpuResourceData) == SHA-256(output GpuResourceData)
```

任何一个 hash 不同，均表示这不是“无改写重组”，必须失败。

### 7.3 图等价验证

读取：

```text
原始内嵌包
拆分后的模型包 + 材质包
重新合并包
```

比较 Unit → Material → Texture 边，要求：

$$
G_{\mathrm{original}}
=
G_{\mathrm{split\ combination}}
=
G_{\mathrm{merged}}
$$

图比较不比较 TOC offset 或 entry index；比较：

- AssetKey；
- 最终 Unit → Material 边；
- 最终 Material → Texture 边；
- 需要写入输出的 Material/Texture closure；
- 对搬运条目的三段 Payload hash。

### 7.4 分离后的单包诊断预期

工具还应验证理论诊断是否正确：

```text
模型包单独：预期 MissingMaterial / MissingTexture（依实际闭包）
材质包单独：预期 NoEffectiveUnitConsumer
模型包 + 材质包：预期闭包完整
```

这是逻辑验证，不替代游戏验证。

---

## 8. 阶段 P3：合并规则

### 8.1 输入

- 一个模型 Patch group；
- 一个用户明确选定的材质 Patch group；
- 可选：原版 Game Data 查询能力，用于验证外部依赖。

### 8.2 收集规则

合并只嵌入：

```text
模型最终有效 Unit 所引用的 Material
→ 这些 Material 最终有效引用的 Texture
```

不得把材质包所有 Material/Texture 无差别打包。

### 8.3 AssetKey 冲突

若模型包和材质包同 AssetKey：

| 情况 | 处理 |
| --- | --- |
| 三段 Payload hash 全相同 | 去重，仅写一次 |
| Payload 不同，且材质包是用户选定 winner | 写材质包版本，并在计划中明确记录 |
| Payload 不同，且没有明确 winner | 阻断，要求用户选择 |

不得静默以文件枚举顺序选择 Payload。

### 8.4 合并成功条件

输出中：

- Unit → Material → Texture 闭包在输出或 Game Data 中完整；
- 选定材质变体的 Payload 原样嵌入；
- 有效资源图等价于“模型包 + 选定材质包”的组合；
- 未将其他颜色变体的 Texture 混入输出。

---

## 9. 阶段 P4：游戏内 Smoke

只有 P0-P3 全部通过，才允许做游戏内 smoke。

每次 smoke 只用一个干净 Profile，记录：输入、部署顺序、输出 fingerprint、游戏结果和截图/日志摘要。

最小矩阵：

| 编号 | Profile 内容 | 预期 |
| --- | --- | --- |
| 1 | 原内嵌 Mod | 原始基线效果 |
| 2 | 拆分模型包 + 拆分材质包 | 与基线视觉一致 |
| 3 | 仅拆分模型包 | 缺材质或预期降级；不得崩溃 |
| 4 | 仅拆分材质包 | 无可见模型效果；Manager 应报无有效 Unit 调用方 |
| 5 | 重新合并包 | 与基线视觉一致 |
| 6 | 模型 + 红材质 + 蓝材质 | 后序变体生效；前序变体报直接覆盖及连带不可达 Texture |
| 7 | 模型 + 蓝材质 + 红材质 | 红色反向成为 winner，状态相应翻转 |

游戏内结果高于离线回读结果；离线成功不等于游戏兼容。

---

## 10. 输出目录与安全要求

每次 smoke 输出到唯一目录：

```text
_diagnostics/MaterialPackagingSmoke/output/<timestamp>-<sample>/
  original-copy/
  model-only/
  material-only/
  merged/
  report.json
  report.md
  hashes.json
  verification.json
```

必须：

- 输入 Patch 只读；
- 输出目录不同于输入目录；
- 默认拒绝覆盖既有输出；
- 所有 write 操作记录完整计划、输入 fingerprint 与 tool/analyzer version；
- failed output 与 passed output 明确区分；
- 不自动导入 Mod 库；
- 不自动部署到游戏 Data。

---

## 11. 完成/阻断标准

### 可宣布“容器级拆分/合并可行”的最低标准

1. 至少一个真实完整内嵌样本通过 P0-P3；
2. 相关 entry 的三段 Payload hash 全部一致；
3. 原始、拆分组合、重新合并三者的 Unit → Material → Texture 图等价；
4. 拆分后模型+材质作为普通平铺 Mod 被 Manager 部署成功；
5. 游戏内 smoke 证明拆分组合和合并包均与原始基线一致；
6. 红/蓝变体排序实验与 winner/不可达诊断一致；
7. 所有未知依赖被明确报告，没有静默忽略。

### 必须阻断正式功能接入的情况

- 仅合成 fixture 通过，真实样本失败；
- 任一搬运 Payload hash 改变；
- 出现未知私有依赖且无法证明外部来源；
- 模型+材质组合的有效图不等于原内嵌包；
- 游戏加载失败、材质异常或模型异常；
- 输出需要改写 AssetKey、Unit binding 或 Material Texture ID 才能运行；
- 输出只能靠覆盖源文件或特殊手工部署才能运行。

---

## 12. 后续产品接入顺序

在上述 smoke 全部成功前：

```text
不添加 Manager UI 写入入口
不自动导入拆分结果
不自动替用户部署
```

成功后依次接入：

1. 高级详情中的只读“拆分/合并可行性审计”；
2. 用户确认后的输出到指定文件夹；
3. 输出目录导入为两个普通 Mod；
4. Profile 资源诊断与候选材质推荐；
5. 最后才考虑一键导入、打包或作者工作流整合。
