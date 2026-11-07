# HD2 模型替换 Patch 人工流程还原与 Core 移植记录

> 目的：固化当前已确认的视频/人工操作流程、对应插件源码位置、Patch 文件层实际变化，避免后续上下文丢失；同时作为后续把 HD2SDK 读写逻辑移植到 `HD2ModCore` 的设计依据。

## 1. 背景结论

当前确认：视频中的“从一个 Patch 提取模型并制作成另一个护甲/头盔替换 Mod”的流程，**不是简单修改 Patch TOC 或 Unit header**。

它的本质是：

1. 从源 Patch 解析 Unit 的 mesh 数据。
2. 从原版游戏资源/目标 Archive 解析目标护甲或头盔 Unit。
3. 在 mesh slot 层面把源模型几何映射/合并到目标 Unit 的对应 mesh。
4. 把不需要显示的目标原版 mesh 极小化。
5. 重新序列化目标 Unit 的 `TocData`。
6. 重新生成目标 Unit 的 `.gpu_resources` vertex/index buffer。
7. 重建 Patch TOC entry 的 offsets/sizes，并写出 `.patch_*`、`.gpu_resources`、`.stream`。

所以，稳定修复“游戏不崩但模型不可见”的关键，不是继续扩大 `ModUnitRepairService` 的 header/layout 修补，而是让 Core 能真正读写 Unit mesh 和 GPU sidecar。

### 1.1 源/目标/验证样本术语校正

后续自动化流程必须严格区分三类输入：

1. **source / old mod**：提供作者自定义的 mesh、material、texture 等内容；它是要被迁移/适配的内容来源。
2. **original game archive target**：提供当前游戏版本中目标护甲/头盔的 Unit 模板，包括 skeleton、mesh slots、stream layout、LOD、material slot 等结构；它是自动修复/适配的目标结构来源。
3. **new working mod sample**：只用于验证、对照和回归分析；生产流程不应依赖“从新版 mod 借 mesh”。

因此，忠实还原人工流程的无 Blender 自动化不是：

```text
旧版 mod + 新版 mod -> 修复旧版 mod
```

而是：

```text
source/old mod 自定义内容 + 原版游戏目标 Unit 模板 -> 新的目标适配 patch
```

新版可用 mod 可以帮助判断结果是否接近人工产物，但不应成为必需输入。

## 2. 相关插件与源码位置

### 2.1 JJK/VMT 自动工具

路径：

- `docs/autofix Tools/__init__.py`

关键代码位置：

| 功能 | 类/函数 | 位置 |
|---|---|---|
| 一键修复 3.0 Mod | `VMT_OT_fix_mod30` | `docs/autofix Tools/__init__.py` 约 235 行起 |
| 设置 SDK 导入选项 | `VMT_OT_fix_mod30.execute` | 约 260-275 行 |
| 扫描 Patch 中 Unit entries | `VMT_OT_fix_mod30.execute` | 约 290-315 行 |
| 加载 Unit 到 Blender 场景 | `entry.Load(Reload=False, MakeBlendObject=True, ...)` | 约 325-360 行 |
| 删除 Patch 中旧 Unit | `Global_TocManager.RemoveEntryFromPatch(entry.FileID, UnitID)` | 约 400-410 行 |
| 从原版 Archive 加载模板 Unit | `Global_TocManager.GetEntryByLoadArchive(unit_file_id, UnitID)` | 约 440-485 行 |
| 把目标/非修复 mesh 转为极小面 | `bpy.ops.mesh.merge(type='CENTER')` | 约 525-570 行 |
| 修复 mesh 与极小 mesh 合并 | `bpy.ops.object.join()` | 约 585-655 行 |
| 设置骨骼修改器目标 | 遍历 `obj.modifiers`，查找 `ARMATURE` | 约 760-850 行 |
| 保存网格回 Patch | `bpy.ops.helldiver2.archive_unit_batchsave()` | 约 900-950 行 |
| 保存选中集合到 Patch | `VMT_OT_save_selected_collection_to_patch` | 约 1160 行起 |
| 保存选中集合时极小化 Unit | 修改 `entry.LoadedData.RawMeshes` | 约 1230-1300 行 |
| 转换选中集合网格为极小面 | `VMT_OT_merge_vertices_in_collection` | 约 1330 行起 |

### 2.2 HD2SDK Community Edition

主要参考路径：

- `tools/ref/HD2SDK-CommunityEdition/__init__.py`
- `tools/ref/HD2SDK-CommunityEdition/stingray/unit.py`

用户提供目录另有：

- `docs/HD2SDK-CommunityEdition-main/`

但当前已读、已确认的参考副本主要在 `tools/ref/HD2SDK-CommunityEdition/`。

关键代码位置：

| 功能 | 类/函数 | 位置 |
|---|---|---|
| Unit 加载入口 | `LoadStingrayUnit` | `tools/ref/HD2SDK-CommunityEdition/__init__.py` 约 1670 行 |
| Unit 保存入口 | `SaveStingrayUnit` | 同文件约 1690 行 |
| Patch entry 数据读写 | `TocEntry.SerializeData` | 同文件约 529 行 |
| Patch TOC 文件读写 | `StreamToc.Serialize` | 同文件约 726 行 |
| 写 `.patch` / `.gpu_resources` / `.stream` | `StreamToc.ToFile` | 同文件约 800 行附近 |
| 加载 Archive/Patch | `TocManager.LoadArchive` | 同文件约 859 行 |
| 从 Archive 找目标 Entry | `TocManager.GetEntryByLoadArchive` | 同文件约 945 行 |
| 从 Patch 删除 Entry | `TocManager.RemoveEntryFromPatch` | 同文件约 1113 行 |
| 默认 Archive 加载按钮 | `DefaultLoadArchiveOperator` | 同文件约 1920 行 |
| 导出 Patch Zip | `ExportPatchAsZipOperator` | 同文件约 2188 行 |
| 单 Unit 保存 Operator | `SaveStingrayUnitOperator` | 同文件约 2717 行 |
| 批量 Unit 保存 Operator | `BatchSaveStingrayUnitOperator` | 同文件约 2832 行 |
| Unit mesh parser/writer | `StingrayMeshFile` | `stingray/unit.py` 文件开头 |
| GPU buffer 读写 | `StingrayMeshFile.SerializeGpuData` | `stingray/unit.py` 约 220 行 |
| index buffer 读写 | `SerializeIndexBuffer` | `stingray/unit.py` 约 235 行 |
| vertex buffer 读写 | `SerializeVertexBuffer` | `stingray/unit.py` 约 300 行 |
| RawMesh 初始化 | `InitRawMeshes` | `stingray/unit.py` 约 350 行 |
| 写出前建立 vertex components | `SetupRawMeshComponents` | `stingray/unit.py` 约 390 行 |
| Blender mesh 转 RawMesh | `GetMeshData` | `stingray/unit.py` 约 1150 行起 |
| 选中对象批量转 RawMesh | `GetObjectsMeshData` | `stingray/unit.py` 约 1970 行附近 |
| RawMesh 转 Blender object | `CreateModel` | `stingray/unit.py` 约 2000 行起 |

### 2.3 SDK Unit / Composite 读取链路校准

已按 `docs/HD2SDK-CommunityEdition-main` 重新核对真实加载链路：

1. `__init__.py` 中 `TocEntry.SerializeData` 根据 TOC entry 读取 `TocData`、`.gpu_resources`、`.stream` payload。
2. `LoadStingrayUnit` 调用 `StingrayMeshFile.Serialize(toc, gpu, Global_TocManager)`。
3. `stingray/unit.py` 中 `StingrayMeshFile.Serialize` 读取 Unit header；若 `StreamInfoOffset == 0 && CompositeRef == 0`，SDK 直接抛出 `Unsupported Mesh Format (No buffer stream)`，Blender 工具只有在 skip import errors 时跳过。
4. 若 `CompositeRef != 0`，SDK 通过 `Global_TocManager.GetEntry(self.CompositeRef, CompositeUnitID)` 读取同一资源集合中的 Composite Unit，并用 `stingray/composite_unit.py` 的 `StingrayCompositeMesh.Serialize` 提供 stream info 和 composite gpu buffer。
5. Core 对应实现应保持：Patch/Archive reader 负责按 `CompositeRef` 找到 Composite Unit payload，再把 Unit 和 Composite payload 一起交给 `UnitMeshReader`；`UnitMeshReader` 只负责二进制解析。

当前 Core 已对齐的关键点：

- `PatchUnitMeshReader` 支持从已扫描 patch entries 中按 `CompositeRef` 查找 Composite Unit payload。
- `ArchiveUnitMeshReader` 已补上同 archive Composite Unit payload 查找，与 SDK 的 `GetEntry(CompositeRef, CompositeUnitID)` 语义一致。
- `PatchUnitMeshSourceCatalogBuilder` 会把 SDK 明确不支持的 no-buffer Unit 标记为 `IsUnsupportedUnitMeshFormat`，避免把非 mesh/不可导入 Unit 当成可自动修复对象。

### 2.4 SDK 3.8.0 Unit header 偏移修正

用户实际使用的发行版源码位于：

- `docs/HD2SDK-CommunityEdition-3.8.0/stingray/unit.py`

该版本 `StingrayMeshFile.Serialize` 的 header 字段顺序为：

| 字段 | Core 读取偏移 |
|---|---:|
| `CompositeRef` | `0x10` |
| `Version` | `0x2c` |
| `BoneInfoOffset` | `0x58` |
| `StreamInfoOffset` | `0x5c` |
| `EndingOffset` | `0x60` |
| `MeshInfoOffset` | `0x64` |
| `MaterialsOffset` | `0x70` |

旧版 Core 曾把 `BoneInfoOffset` 到 `MaterialsOffset` 整体提前 12 字节读取，即 `0x4c/0x50/0x54/0x58/0x64`。这会把真实可导入 Unit 误判为 `StreamInfoOffset == 0`，进而得到大量 `Unsupported Mesh Format (No buffer stream)`。

已修正位置：

- `HD2ModCore/Infrastructure/UnitMeshReader.cs`
- `_diagnostics/AdaptationSmoke/Program.cs`
- `HD2ModCore.Tests` 中的 synthetic Unit header fixtures

验证结果：

- `dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj`：109 passed。
- `_diagnostics/AdaptationSmoke` 真实旧版样本：从 `entries=0 failures=1727` 改善为 `entries=1691 failures=68`。
- 剩余失败主要为异常大的 patch payload offset，例如 `16915718763308572383`。`PatchUnitMeshSourceCatalogBuilder` 现在会先验证 TOC、`.gpu_resources`、`.stream` payload 范围，把这类 entry 统一记录为 `Unit entry payload range is outside its patch payload files.` 且标记为 unsupported，避免进入 mesh reader 后产生噪声异常。后续若要追这些 68 项，应单独排查 TOC entry/payload offset 解析或该批 patch 的特殊封装，而不是 Unit mesh header offset。

### 2.5 Archive sidecar 读取修正

`docs/HD2SDK-CommunityEdition-3.8.0/utils/slim.py` 中 `load_package` 会把一个 archive 拆成三条数据流：

- `package` -> TOC/TocData
- `package + ".gpu_resources"` -> GPU resource data
- `package + ".stream"` -> stream data

bundled archive 也遵循同样命名规则，只是通过 `reconstruct_package_from_bundles` 从 `bundles.*.nxa` 重建对应虚拟 package。`TocEntry.SerializeData` 随后分别从 `TocFile`、`GpuFile`、`StreamFile` 按 entry offsets 读取 payload。

Core 曾在 `ArchiveUnitMeshReader.ReadPayloadAsync` 中把 TOC、GPU、stream 都从同一个 `entry.ArchiveName` package 读取，导致真实 game data 下读取原版 Unit 模板时出现 optional payload 越界，例如：

```text
Could not read optional payload at offset 702528 size 582984 from archive '7cfc63f636fa5132'.
```

已修正为：

- TOC 使用 `entry.ArchiveName`
- stream 使用 `entry.ArchiveName + ".stream"`
- GPU 使用 `entry.ArchiveName + ".gpu_resources"`

修正位置：

- `HD2ModCore/Infrastructure/ArchiveUnitMeshReader.cs`
- `HD2ModCore.Tests/ArchiveUnitMeshReaderTests.cs`

验证结果：

- `dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj`：109 passed。
- `_diagnostics/AdaptationSmoke` 使用 `D:\SteamLibrary\steamapps\common\Helldivers 2\data`：不再出现 archive optional payload `EndOfStreamException`，并能进入 adaptation planner；当前结果为 `canWrite=False`，原因是 `No compatible source/target RawMesh candidate was found.`，这是后续 source/target mesh 匹配策略问题。

### 2.6 第一阶段实验性 source -> target 适配

为测试阶段暴露更多真实问题，`CoreServices.CreateUnitMeshAdaptationPlanner()` 已对 adaptation planner 单独开启实验性匹配与 retarget：

- `UnitMeshReplacementStrategy(allowExperimentalFallback: true)`
- `UnitMeshRetargeter(allowExperimentalLayoutFallback: true)`

默认 factory 与默认构造函数仍保持严格行为，既有 patch-level editor/planner/tests 不会自动进入跨 layout 替换。

实验性匹配允许 source/target stream component layout 不一致时产生 `ExperimentalFallback` candidate。当前评分仍优先考虑：

- source/target `MeshInfoIndex` 相同；
- LOD 相同；
- vertex stride 相同或接近；
- section / vertex / triangle 数量接近。

实验性 retarget 当前只做测试阶段可写性处理，不等价于最终正确模型转换：

- vertex raw data 会按 target stream stride 截断或补零；
- 16-bit target index buffer 会把 source vertices 裁剪到 `65536` 以内，并丢弃越界 triangle；
- source section 数大于 target MeshInfo section capacity 时，只保留 target 能描述的 section；
- section material index / material slot 仍来自 target slot，避免 source material slot 直接污染目标 Unit。

这两个 writer blocker 已在真实 smoke 中出现并处理：

1. `A 16-bit Unit index buffer cannot contain an index greater than 65535.`
2. `RawMeshData contains more sections than the target Unit MeshInfo can describe.`

最新真实 game data smoke：

```text
dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data"

sourceCatalog patches=142 entries=1691 failures=68
first source archive match=7cfc63f636fa5132
target model streams=2 meshes=5 rawMeshes=5
adaptation canWrite=True candidates=30 replacements=5 minified=5
reason=Dry-run adaptation produced 5 replacement(s) and 5 minified target slot(s).
write tocBytes=5520 gpuBytes=11501208
```

这说明第一阶段已经能从旧 source patch 读取自定义 mesh、从原版 archive 读取 target Unit template、进行激进 candidate selection、极小化 target slots，并 dry-run 序列化出新的 Unit `TocData` 与 `.gpu_resources`。后续风险主要在模型语义正确性：component layout 补零/截断、骨骼 remap、材质依赖复制、真实 patch 重组与 zip/export 仍未完成。

用户指定样例已跑通真实 patch 写出链路：

```text
dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-batch2"

sourceCatalog patches=1 entries=32 failures=0
batch edits=21 skipped=0 failed=11
output toc=...\Chiffon-Lala-Crash-Casual-CW22-CW36-batch2\9ba626afa44a3aa3.patch_0 bytes=252460
output stream=...\Chiffon-Lala-Crash-Casual-CW22-CW36-batch2\9ba626afa44a3aa3.patch_0.stream bytes=1049044
output gpu=...\Chiffon-Lala-Crash-Casual-CW22-CW36-batch2\9ba626afa44a3aa3.patch_0.gpu_resources bytes=128346068
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

该诊断已从单 entry 扩展为批量 entry：同一 source AssetKey 若命中多个原版 archive，会逐个尝试 target template，直到找到可读且可写的 archive。这样把本样例可写数量从 18 提升到 21。剩余 11 个失败仍是 `No readable target archive...`，底层多为原版 target TOC payload offset/size 读取不到完整数据，属于后续 archive/bundle 解析覆盖范围问题，不影响本次已写出的 21 个 Unit 的 patch 回读验证。

### 2.7 `0x44415441` 过时资源排查

`docs/hd2-repatcher-main/update_unit_mods.py` 的核心修复逻辑是：从当前原版 Unit 读取 `0x2c` 版本号和 `0x30..0x34` LOD group，并写回旧 mod Unit。这说明游戏会按当前原版 Unit 的 freshness 字段拒绝旧 Unit。

新自动化 retarget 本身以原版 target Unit payload 作为写出模板，成功适配的 21 个 Unit 已天然带当前版本/LOD group；但第一次 batch 输出仍保留了 11 个未成功适配的旧 Unit entry，这些旧 Unit 仍携带过时版本，足以触发游戏报 `0x44415441`。

当前修复：`PatchArchiveDryWriter` 支持传入 `removedEntries`，重建时会压缩 entry table、重写 header `numFiles`、重写 type counts、重新分配 entry index，并保留 entry unknown metadata 字段。`_diagnostics/AdaptationSmoke` 当前只保留成功适配的 Unit entry，删除未适配旧 Unit entry；非 Unit 依赖仍保留。

新验证输出：`_diagnostics/AdaptationSmoke/out/Chiffon-Lala-Crash-Casual-CW22-CW36-pruned-stale-units`，结果为 21 个 Unit 写出、11 个旧 Unit 移除、Core 回读验证 21/21；TOC header 校验为 `numFiles=45`，Unit type count `21`，entry index 连续。

### 2.8 游戏内视觉反馈后的几何/材质排查

用户验证 `pruned-stale-units` 后确认：游戏可以进入，头盔模型已经可见，但贴图错乱；身体盔甲存在顶点漂移、拉伸扭曲和贴图错乱，且仍有部分原版护甲部件残留。

对照人工流程后，当前判断如下：

- body 顶点漂移/拉伸的首要风险不是 target minify，而是实验性 retarget 早期按 raw vertex byte 截断/补零。source/target 的 stream component layout 不一致时，这会把 position、normal、uv、bone index/weight 等语义写到错误偏移。
- `UnitMeshMinifier.MinifyAll()` 已在 planner 写出前遍历 target template 的全部 raw mesh slot 并替换为极小三角面；因此“原版部件残留”更可能来自未成功适配后被移除的 11 个 Unit、Composite/子部件引用、目标 Unit 选择不完整，或部分可见部件不在当前 Unit raw mesh 列表中。
- 直接把 source Unit material binding table 写入 target Unit 不可行：真实样例中 source material binding 数量超过 target Unit `MaterialsOffset..EndingOffset` 可容纳空间，dry-run 会变成 0 个可写 Unit。已回退该尝试，避免破坏当前可进入游戏的链路。

已完成的几何修复：`UnitMeshRetargeter` 在 experimental layout fallback 下改为按 target stream component semantic 重新编码 vertex data。当前支持把 source component 按 `(Type, Index)` 匹配到 target component，并按 target format 写出；缺失 component 使用 0 或合适默认值。这样至少避免 position/uv/权重因为 stride/layout 不同而整体错位。

验证输出：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 110, 失败: 0, 成功: 110, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-component-remap"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

新可测目录：`_diagnostics/AdaptationSmoke/out/Chiffon-Lala-Crash-Casual-CW22-CW36-component-remap`。

后续根据用户第二轮游戏反馈确认：`component-remap` 已解决身体顶点乱飞/漂移，但可见部件和贴图仍明显不对。继续对照 HD2SDK 后发现，Core 必须区分 Unit 版本对应的 vertex component format table。旧 Unit version `10800437` 中 SDK 会对 `format > 16` 执行 `format -= 4`，因此旧表与当前表存在数字重叠：

- old `24` = `vec4_uint8`，current `24` = `vec4_uint32`。
- old `29` = `vec2_half`，current `29` = `vec4_1010102`。
- current target 常见格式包括 `30:unk_normal`、`33:vec2_half`、`28:vec4_uint8`、`24:vec4_uint32`、`29:vec4_1010102`、`35:vec4_half`。

已修复位置：

- `UnitMeshReader.ReadStreams(...)` 传入 Unit version，并按版本解析 format name/size。
- `UnitMeshReader.DecodeVertexComponent(...)` 改为按 `FormatName` 解码，避免 current `29` 被误读为 half UV。
- `UnitMeshRetargeter` 和 `UnitMeshMinifier` 的 component 写出改为按 `FormatName` 分支，避免 current `24` 被当成颜色/uint8，old/current `29` 被混淆。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 110, 失败: 0, 成功: 110, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-format-name-remap"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

新可测目录：`_diagnostics/AdaptationSmoke/out/Chiffon-Lala-Crash-Casual-CW22-CW36-format-name-remap`。

下一步材质方向不能继续简单“扩容 target Unit material table”。更合理的方向是先解析 source material/texture 依赖和 target material binding 的实际语义：判断 source mesh sections 引用的是 material index 还是 section id 映射，再决定是复用 target slot、重映射到已有容量内，还是在 patch 层补齐 material/texture 资源引用链。

### 2.9 SDK 风格语义 mesh 匹配

HD2SDK 3.8.0 的语义线索来自 `docs/HD2SDK-CommunityEdition-3.8.0/stingray/unit.py`：

- `CustomizationInfo.Serialize` 读取 `BodyType`、`Slot`、`Weight`、`PieceType`，读取模式是先跳过 24 字节，再按 length-prefixed string 读取字段，并在字段之间跳过 12 字节。
- `NameFromMesh` 在 `CustomizationInfo.BodyType` 非空时去掉 `HelldiverCustomizationBodyType_`、`HelldiverCustomizationSlot_`、`HelldiverCustomizationWeight_`、`HelldiverCustomizationPieceType_` 前缀，并生成 `Slot_PieceType_BodyType_lodN` 或 `Slot_PieceType_BodyType_meshN` 名称。
- `CreateModel` 会把这些字段写入 Blender object 的 `Z_CustomizationBodyType`、`Z_CustomizationSlot`、`Z_CustomizationWeight`、`Z_CustomizationPieceType` 属性；人工流程中的对象名和属性都依赖这组信息。

Core 当前已移植这部分只读语义信息：

- `UnitMeshReader` 从 Unit header `0x50` 读取 `CustomizationInfoOffset`，并解析 `UnitCustomizationInfo`。
- `UnitMeshInfo` 带有 `UnitMeshSemanticInfo`，用于保存 SDK 风格名称、slot、piece type、body type、weight、LOD 与 mesh info index。
- `UnitMeshReplacementStrategy` 在 source/target 都有真实 customization 语义时要求 slot 与 piece type 一致；body type 完全一致优先，`Any` 可以作为 fallback，非空且不兼容的 body type 会拒绝候选。
- source mesh 若有真实语义且 `vertices <= 10` 或没有 triangle，会按 autofix 的 `vertices > 10` 逻辑跳过，避免把极小化占位 mesh 当作可迁移部件。
- fallback 名称如 `3145176363_mesh0`、`3844486962_lod3` 仅作为诊断标签，不参与真实语义判定，也不会触发 tiny 语义过滤。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 112, 失败: 0, 成功: 112, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-semantic-remap"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

本次真实样本的 source/target dump 中 `customization=///`，说明这些 Unit 没有可用的 customization 字段，候选仍退回到 mesh id/lod/结构评分。该实现已经为带 customization metadata 的 Unit 建好语义匹配路径，但当前样本的 body/arms/legs 视觉错配不能只靠 `CustomizationInfo` 解决。后续需要继续从 SDK 的 `CreateModel` / `GetObjectsMeshData` 中移植 object-level 属性、bone info / culling body / material slot 语义，或引入手动/半自动 part mapping 表。

### 2.10 SDK 风格 mesh role 分类

由于真实样本没有 `CustomizationInfo`，下一层先移植 HD2SDK 的 object-level mesh role 判断，而不是继续放宽 index/LOD 匹配。

HD2SDK 参考点：

- `RawMeshClass.IsCullingBody()`：当 mesh 的所有 material 都仍是 `StingrayDefaultMaterial` 时视为 culling body。SDK 的 `SerializeIndexBuffer` 只有在 section id 能映射到 Unit material binding 时才把默认 material 替换成真实 material。
- `RawMeshClass.IsLod()`：`LodIndex` 不是 `0` 或 `-1`，且不是 culling body。
- `RawMeshClass.IsStaticMesh()`：所有 vertex weights 都是 `[0,0,0,0]`。
- `NameFromMesh` 除了 customization 命名外，还会在 culling body 上使用 `_culling{MeshInfoIndex}` 后缀，并在有 bone names 时用 `murmur32(boneName) == MeshID` 把名称替换为 bone name。
- `CreateModel` 导入阶段可按 `ImportLods`、`ImportCulling`、`ImportStatic` 跳过对应对象；这说明这些 role 是人工 Blender 流程里实际存在的对象边界。

Core 当前实现：

- `UnitMeshSemanticInfo` 增加 `IsCullingBody`、`IsStaticMesh`、`IsLod`，以及 `IsVisualMesh` 派生属性。
- `UnitMeshReader` 在 raw mesh 解码后按 Unit material binding 的 section id 判断 culling body：mesh section 的 material slot 全部不在 material binding section id 集合中时，近似 SDK 默认 material 行为。
- `UnitMeshReader` 按 bone weight component 判断 static mesh：存在 `bone_weight` component，且所有 float/uint 权重都为零。
- `UnitMeshReplacementStrategy` 不再把 culling/static source 或 target 槽纳入视觉替换候选，避免把碰撞/剔除/静态辅助对象当作 body/armor 几何迁移。
- smoke dump 已打印 `role=visual|lod|culling|static`，方便对照 SDK/Blender 对象边界。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 115, 失败: 0, 成功: 115, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-visual-role-only"
batch edits=12 skipped=9 failed=11
removed stale unit entries=20
verify group file=9ba626afa44a3aa3.patch_0 verified=12/12
```

这次结果比纯 customization/结构匹配更保守：首个可写样本候选从 `20` 降到 `16`，因为 culling target/source 不再参与视觉替换；整批从 `21` 个 edit 降到 `12` 个 edit，新增 `9` 个 skipped。这个变化符合当前阶段目标：先减少明显错误的 object role 匹配，再继续补 bone-name、material/texture dependency 和更细的 part mapping。注意输出仍会 minify culling target 槽，这是 planner 层当前“先 minify all target meshes”的旧策略，后续应改成按 role/policy minify。

游戏内测试 `Chiffon-Lala-Crash-Casual-CW22-CW36-visual-role-only`：小甲片重新出现，上一轮没有小甲片，说明 culling target 槽被 minify 后可能反而破坏了原版的隐藏/剔除辅助对象。胸部部分大致对应，手臂为空，胯部较正常，腿部仍有单腿嵌合/错配，贴图仍大面积错乱。结论是 culling/static 不应参与视觉替换，但也不应被 planner 的全量 minify 清空。

后续修正：`UnitMeshAdaptationPlanner` 改为只 minify `SemanticInfo.IsVisualMesh` 的 target mesh，保留 culling/static target mesh 原样。验证目录：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 116, 失败: 0, 成功: 116, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-preserve-culling"
batch edits=12 skipped=9 failed=11
verify group file=9ba626afa44a3aa3.patch_0 verified=12/12
```

该版本首个输出模型中 culling mesh 保持 `tiny=False vertices=439 indices=2280`，visual/lod mesh 仍由 source 替换。游戏内复测后确认该版本与上一轮表现完全一致，小甲片仍未隐藏，因此“保留 culling/static target mesh”是假设错误。

人工流程修正：目标 Unit 的非骨骼模型在合并前应全部隐藏成小三角体；role 分类只用于理解/命名/匹配源目标关系，不应用来决定目标 mesh 是否保留。真正需要判断的是源模型包是否为空、源模型应注入哪个目标壳；未被源模型替换的目标 mesh 也应保持小三角隐藏。

后续修正：`UnitMeshAdaptationPlanner` 改为 minify 所有 target RawMesh，再把选中的 source mesh 注入对应 target slot。验证目录：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 116, 失败: 0, 成功: 116, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-minify-all-targets"
batch edits=12 skipped=9 failed=11
verify group file=9ba626afa44a3aa3.patch_0 verified=12/12
```

该版本首个输出模型中 culling mesh 已变为 `tiny=True vertices=3 indices=3`，visual/lod mesh 仍由 source 替换。下一轮游戏测试应优先确认小甲片是否隐藏；如果仍未隐藏，问题就不在单个 target mesh 的 minify 策略，而更可能在目标 Unit 覆盖集合不完整、旧 patch entry 删除/写回范围、或源到目标壳匹配关系。

游戏内测试 `Chiffon-Lala-Crash-Casual-CW22-CW36-minify-all-targets` 后确认：小甲片仍未隐藏，胸部大致对应，手臂为空，胯部较正常，双腿异常，贴图大面积错乱，脚部/胯部骨骼动画异常。由此确认“已编辑 entry 内的 target mesh 是否全量 minify”不是唯一问题；剩余重点转为 target Unit 覆盖集合、source-target slot 匹配、材质依赖和骨骼/skin 绑定。

随后修正 no-candidate 行为：当 source/target 没有可替换候选时，planner 不再返回 non-writable，而是仍按人工流程把 target Unit 的所有 RawMesh 写成小三角并输出 minify-only Unit。这样可避免这些目标 Unit 被直接跳过或只作为 stale Unit 删除。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 116, 失败: 0, 成功: 116, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-minify-only-skips"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

本轮输出目录：`_diagnostics/AdaptationSmoke/out/Chiffon-Lala-Crash-Casual-CW22-CW36-minify-only-skips`。其中 9 个原本 `candidates=0` 的 Unit 已变为 `canWrite=True` 的 minify-only target Unit；仍有 11 个失败为 `No readable target archive among ... candidate(s).`，说明下一步应优先增强 archive/bundle 读取诊断与目标完整集合覆盖，而不是继续改单个 mesh 的 minify 策略。贴图错乱和脚部/胯部动画异常仍指向当前 `UnitMeshRetargeter` 只复制 RawMesh、保留 target material slot，且没有 source skeleton/bone index 到 target skeleton 的完整 remap。

### 2.11 SDK 风格 BoneInfo remap

手臂 Unit 仍不可见后继续对照 SDK 保存链路，确认 C# 与 SDK 还有一个关键差异：SDK 在 `GetMeshData`/保存 Unit 时按每个 vertex 所属 polygon/material index 调用 `BoneInfo.GetRemappedIndex(real_index, material_idx)`；Core 旧实现只取 source/target raw mesh 的第一个 section/material 建一个 mesh-wide bone map。

真实手臂日志里该差异很明显：target `mat0` remap 往往只有 1-2 个骨骼，而 source 或 target 的 `mat1` 才包含完整手臂骨骼集合。只使用第一个 section 会把第二材质 section 的顶点也按 `mat0` remap，容易把骨骼索引写错或退回未映射索引。

当前修复：

- `UnitMeshRetargeter` 改为按 replacement section 建 target remap 列表，按 source section 建 source remap 列表。
- 根据 source section triangles 建 vertex -> section/material map。
- 写 `bone_index` component 时按当前 vertex 所属 material 选择对应 remap，近似 SDK 的 per-polygon/material 行为。
- 保持全量 minify target 基线，不恢复“保留未匹配 target mesh”的已否定策略。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 128, 失败: 0, 成功: 128, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data"
batch edits=32 skipped=0 failed=0
verify group file=9ba626afa44a3aa3.patch_0 verified=32/32
```

新可测目录：`_diagnostics/AdaptationSmoke/out/20260709-165806`。

游戏内测试 `Chiffon-Lala-Crash-Casual-CW22-CW36-minify-only-skips` 后确认：多余小甲片已消失，说明 target 全量 minify 与 no-candidate minify-only 是正确方向；剩余问题集中在手臂空白、腿部错配/嵌合、贴图几乎全错、脚部粘地以及胯部不随走路动画运动。

为定位这些问题，`_diagnostics/AdaptationSmoke` 已增加 replacement 级诊断，输出每个 selected source->target slot 的 mesh id、LOD、vertex/triangle/section 数、source/target material slot 与 material binding、bone index/weight 统计和 position bounds。最新诊断输出：

```text
dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-diagnostic-parts"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

诊断中的代表性结论：

- 可见 body replacement 多数是 `ExperimentalFallback`，source mesh 通常为 `115396` vertices / `155232` triangles，被写入 target LOD slot；这能解释胸部已有几何，但不能保证手臂/腿的 part mapping 正确。
- 替换后的 section 仍使用 target material slot，例如 target binding `3761205758:35ba55486617431f`，source binding 则是 `1701023029:1d0ad4bd575099f2` 或 `1552097879:bfde44ecdd177145`。这直接解释了 source UV/geometry 搭配 target material 导致的贴图错乱。
- source/target bone 数据只按 raw component 复制或按 target component 重新编码，没有通过 SDK 的 `BoneInfo.GetRealIndex/GetRemappedIndex` 按 material remap 到 target skeleton。HD2SDK 在导入/导出时会用 `bone_info[mesh.LodIndex].GetRealIndex(bone_index, material_index)` 与 `GetRemappedIndex(real_index, material_idx)` 进行转换，Core 目前完全缺失这层，所以脚部粘地和胯部动画异常很可能来自 bone remap/LOD bone info 不一致。

因此下一步不应继续调整 minify；优先级应改为：解析/写回 Unit material binding 与 patch material/texture 依赖；解析 `BoneInfo` 的 real/fake/remap tables，并在 retarget 时按 source material/target material 建立 bone index remap；随后再用 bounds/bone/material 特征改进没有 customization metadata 时的手臂/腿 part mapping。

### 2.11 BoneInfo remap 初版修复

HD2SDK 的 `BoneInfo.GetRealIndex/GetRemappedIndex` 证明 vertex `bone_index` 不是全局骨骼 index，而是按 material remap table 编码的局部 remapped index。旧 Core 直接复制 source `bone_index`，在 target 的 remap table 下会被解释成另一个 real bone，导致脚部粘地、胯部动画不同步等 skinning 问题。

当前 Core 已完成第一版修复：

- `UnitMeshReader` 已读取 `UnitBoneInfo.RealIndices` 与每个 material 的 `UnitBoneRemap.FakeIndices`。
- `UnitMeshRetargeter` 在替换 RawMesh 时建立 `source remapped index -> source real bone -> target remapped index` 映射。
- 对 `bone_index` vertex component 按 target stream format 重新写出，支持 `vec4_uint8` 与 `vec4_uint32`。
- 当前实现先使用 mesh 第一组 section 的 material index 作为 source/target remap 上下文；多 section、共享 vertex 的 per-section material remap 仍需后续细化。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj --filter Retarget_BoneInfoRemapsBoneIndexComponents
测试摘要: 总计: 1, 失败: 0, 成功: 1, 已跳过: 0

dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 118, 失败: 0, 成功: 118, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" "E:\Data\source\repos\WpfApp1\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" "E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-bone-remap"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

新可测目录：`_diagnostics/AdaptationSmoke/out/Chiffon-Lala-Crash-Casual-CW22-CW36-bone-remap`。

这次修复只处理 skinning index 解释错误，不会修正贴图错乱。贴图问题仍然来自 source geometry/UV 绑定 target material slot；下一步应解析并写回 material binding 与 patch material/texture dependency chain，或实现受 target Unit 容量约束的 material remap 策略。

### 2.12 SDK bone-name mesh naming fallback

继续核对 HD2SDK 3.8.0 后确认，`NameFromMesh(mesh, id, customization_info, bone_names)` 的最终命名还包含一条比 `CustomizationInfo` 更靠后的 fallback：如果 Unit 的 `BonesRef` 能加载到 Bones 资源，SDK 会遍历 bone names，并在 `murmur32_hash(bone_name.encode()) == mesh.MeshID` 时把 mesh 名称覆盖为该 bone name。例如插件/Blender 中常见的 `RightArm_Undergarment_Slim_lod0` 就来自这条路径，而不是 Unit `CustomizationInfo`。

当前 Core 已补齐对应只读路径：

- `UnitBoneNamesReader` 解析 Bones 资源：`NumNames`、`NumLODLevels`、`UnkArray1`、`BoneHashes`、`LODLevels` 和后续 UTF-8 null-terminated names。
- `MurmurHash` 移植 HD2SDK `utils/hashing.py` 的 Murmur64/Murmur32 算法；测试用 Python SDK 对照值验证 `RightArm_Undergarment_Slim_lod0 -> 0x2ed924fa`、`RightLeg_Undergarment_Any_lod0 -> 0x4a24d412`。
- `UnitMeshReader` 在读取 mesh 时接收 `UnitBoneNames`，命中 `murmur32(name) == MeshID` 后用 bone name 覆盖诊断名，并从 `Slot_PieceType_BodyType_lodN` 推断 `Slot`、`PieceType`、`BodyType`，让既有 `UnitMeshReplacementStrategy` 可以按左右手/腿等语义拒绝错误候选。
- `PatchUnitMeshReader` 会按 Unit header `BonesRef` 在同 patch entry context 中查找 Bones entry；`PatchUnitMeshSourceCatalogBuilder` 和 smoke 也会传递完整 scanned entries，避免后续重读 source Unit 时丢失同 patch Bones/Composite 上下文。
- `ArchiveUnitMeshReader` 已接入同 archive Bones 查找；若后续 target 需要 SDK `SearchAll=True` 语义，还需要补 cross-archive Bones lookup。

验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 124, 失败: 0, 成功: 124, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data" ".\_diagnostics\旧版\【2.3】Chiffon-Lala+Crash+Casual替换CW-22&CW-36" ".\_diagnostics\AdaptationSmoke\out\Chiffon-Lala-Crash-Casual-CW22-CW36-sdk-bone-name-context"
batch edits=21 skipped=0 failed=11
removed stale unit entries=11
verify group file=9ba626afa44a3aa3.patch_0 verified=21/21
```

本轮 smoke 仍显示该真实样本的首个 source/target dump 为数字 fallback 名称，如 `3844486962_lod3`，说明这些 Unit 的 mesh id 没有命中 patch/archive 中可直接读取的 bone names。该输出仍是当前可进游戏测试的候选版本，因为它保留了上一版最好 baseline 的 material 策略、BoneInfo remap、target 全量 minify 与 stale Unit 删除；但它不应被视为已解决手臂/右腿语义错配。下一步应优先定位 SDK/Blender 能拿到的全局 bone/name 来源，或在 Archive reader 中补齐 cross-archive/global bone names 后再重新评估 `RightArm/RightLeg` 命中率。

### 2.13 Source material binding propagation

模型和 BoneInfo remap 经游戏内验证基本正常后，剩余贴图问题定位到 Unit section/material binding：Core 已经复制 source geometry/UV，但 section material slot 与 Unit material binding 仍来自 target Unit，导致 source UV 采样原版 material/texture。

第一次修复：

- `CoreServices.CreateUnitMeshAdaptationPlanner()` 对真实 adaptation planner 启用 `UnitMeshRetargeter(..., propagateSourceMaterials: true)`。
- `UnitMeshRetargeter` 在 target material table 容量内，把 selected target mesh 的 material slot/binding 替换为 source slot 与 source material id。
- 当 source slot 会碰撞未替换的 target binding、source material binding 重复、target replacement slot 不在 binding table，或其他会导致 writer 不可安全改写的情况发生时，retargeter 回退到 target binding，只保留 mesh/bone 替换，确保完整 Unit 覆盖不回退。
- `PatchArchiveDryWriter` 继续保留 source patch 中已有的 material/texture entries；但后来实测证明，本样本 Unit 引用的 source material id 并不等于 patch 中闭合存在的 material entries，不能直接认为 Unit binding propagation 足以安全引用 source material。

初版无条件传播曾导致真实 smoke 回退为 `batch edits=18 skipped=0 failed=14`、`removed stale unit entries=14`。根因是部分 source material binding 表存在重复 section id 或与 target 未替换 binding 冲突，触发写入路径异常。加入安全回退后验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj --filter "FullyQualifiedName~UnitMeshReaderTests|FullyQualifiedName~UnitMeshAdaptationPlannerTests"
测试摘要: 总计: 39, 失败: 0, 成功: 39, 已跳过: 0

dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 132, 失败: 0, 成功: 132, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data"
batch edits=32 skipped=0 failed=0
output toc=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-001141\9ba626afa44a3aa3.patch_0 bytes=245728
output stream=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-001141\9ba626afa44a3aa3.patch_0.stream bytes=1049044
output gpu=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-001141\9ba626afa44a3aa3.patch_0.gpu_resources bytes=134764820
removed stale unit entries=0
verify group file=9ba626afa44a3aa3.patch_0 verified=32/32
```

`_diagnostics/AdaptationSmoke/out/20260710-001141` 已被游戏内测试否定：进入游戏不会立即崩溃，但镜头接近、即将渲染玩家模型时崩溃。该输出不应再作为推荐测试包。

按 SDK 思路，材质替换必须同时保证两层闭合：Unit section/material binding 自洽，以及 material payload 引用的 texture entries 在 patch/archive 中可解析。当前安全修复新增 `MaterialDependencyValidator` 和 `StingrayMaterialReferenceReader`：先从 source Unit material binding 收集 material id，再验证 material entry 和 texture entry 是否在 patch 中闭合；未通过验证的 source material 不传播，保留 target material binding。

新验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 134, 失败: 0, 成功: 134, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data"
outputDir=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-005339
material dependency valid=0/12 rejected=12
batch edits=32 skipped=0 failed=0
output toc=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-005339\9ba626afa44a3aa3.patch_0 bytes=245728
removed stale unit entries=0
verify group file=9ba626afa44a3aa3.patch_0 verified=32/32
```

当前新可测目录：`_diagnostics/AdaptationSmoke/out/20260710-005339`。这版会牺牲 source material 贴图传播，优先避免引用不闭合 material 导致渲染阶段崩溃。后续若要恢复正确贴图，需要扩展 `PatchArchiveDryWriter`，像 SDK `SaveStingrayMaterial()` 一样复制/生成 material 与 texture dependency entries 后再放开 propagation。

### 2.14 Material dependency type id 修正与局部传播

用户测试 `20260710-005339` 后确认：mesh/skeleton 行为基本正常，但贴图仍使用原版贴图。继续排查发现先前 material/texture type id 常量不匹配当前真实 patch，导致依赖校验把所有 source material 都判为缺失。当前真实样本中的相关 type id 为：

- Unit: `0xe0a48d0be9a7453f`
- Composite Unit: `0xc4f0f4be7fb0c8d6`
- Material: `0xeac0b497876adedf`
- Texture: `0xcd4238c6a0c69e32`

修正后，`_diagnostics/AdaptationSmoke` 能在 source patch 中识别出 2 个 material entry，另外 10 个 Unit material id 在 source patch 中没有对应 entry：

```text
material dependency valid=2/12 rejected=10
material rejected ...: Source material entry is missing from patch.
```

`UnitMeshRetargeter` 已改为按 slot 局部传播材质：通过依赖闭合校验的 source material 会写入对应 target material slot；未通过的 slot 保留 target material，避免重新触发此前 full source material propagation 的渲染崩溃。新验证结果：

```text
dotnet test .\HD2ModCore.Tests\HD2ModCore.Tests.csproj
测试摘要: 总计: 135, 失败: 0, 成功: 135, 已跳过: 0

dotnet run --project .\_diagnostics\AdaptationSmoke\AdaptationSmoke.csproj -- "D:\SteamLibrary\steamapps\common\Helldivers 2\data"
outputDir=E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationSmoke\out\20260710-011228
material dependency valid=2/12 rejected=10
batch edits=32 skipped=0 failed=0
verify group file=9ba626afa44a3aa3.patch_0 verified=32/32
```

由于缺失的 10 个 material id 在旧 patch 内没有 entry，当前安全策略无法单靠 Unit material binding 让所有贴图恢复。下一步需要按 SDK `SaveStingrayMaterial` 思路扩展 patch writer：把 material/texture 依赖资源作为新增 entry 复制进输出 patch，重算 type counts、entry indices、offsets 和 sidecars，再在依赖闭合后启用完整 source material propagation。

## 3. 人工流程到代码流程的映射

### 3.1 安装并启用插件

人工操作：

1. 安装 `autofix Tools`。
2. 安装 `HD2SDK-CommunityEdition-main`。

代码依赖：

- JJK/VMT 工具通过 `sys.modules.get('HD2SDK-CommunityEdition')` 获取 SDK 模块。
- 关键对象：
  - `Global_TocManager`
  - `UnitID`
  - `MaterialID`
  - `TexID`
  - `TocEntry`

### 3.2 配置原版资源路径并加载 Default Archive

人工操作：

1. 设置游戏数据目录，例如 `D:\SteamLibrary\steamapps\common\Helldivers 2\data`。
2. 点击 SDK 的 Default Archive 星形按钮。

代码含义：

- `DefaultLoadArchiveOperator` 加载基础 Archive。
- SDK 需要它来解析目标护甲/头盔 Unit 以及 bones/state machine/material 等依赖。

Core 对应能力：

- 已有 `GameDataPackageResolver` 可作为基础。
- 后续需要提供“按 Archive/FileID/TypeID 找 Entry 并加载 TocData/GpuData”的统一接口。

### 3.3 加载源 Patch 并列出 texture/unit/material

人工操作：

- 选择源 Patch 文件，插件列出三类资源下拉列表。

代码含义：

- `StreamToc.FromFile` / `TocManager.LoadArchive` 读取 Patch。
- Entry 以 `TypeID -> FileID -> TocEntry` 方式组织。

Core 对应能力：

- Patch TOC parser 已有基础。
- 后续应暴露语义对象：
  - Unit entries
  - Material entries
  - Texture entries
  - sidecar presence/offset/size

### 3.4 Import 源 Patch 中所有 Unit 到 Blender

人工操作：

- 选中源 Patch 中所有 Unit，Import 到 Blender。

代码含义：

- `TocEntry.Load(Reload=False, MakeBlendObject=True, LoadMaterialSlotNames=False)`
- Unit 加载路径：
  1. `LoadStingrayUnit(...)`
  2. `StingrayMeshFile.Serialize(toc, gpu, Global_TocManager)`
  3. `SerializeGpuData(...)`
  4. `InitRawMeshes(...)`
  5. `CreateModel(...)`

生成的 Blender object 会带：

- `Z_ObjectID`
- `Z_SwapID`
- `MeshInfoIndex`
- `BoneInfoIndex`
- material slots
- vertex groups
- armature modifier

Core 对应能力：

- 需要移植 Unit mesh parser：`TocData + GpuData -> UnitMeshModel/RawMeshes`。

### 3.5 删除空物体，只保留有效源模型部件

人工操作：

- 删除空对象或无用对象，只保留头、胸、手、胯等真实模型。

代码含义：

- 这一步主要是人工筛选，没有直接 Patch 写入。
- 被保留对象后续会由 `GetMeshData` 转换成 `RawMeshClass`。

Core 对应能力：

- 如果无 Blender，需要提供源 mesh 列表及特征，让 UI 或算法判断哪些 mesh 是有效模型。
- 可用特征：顶点数、三角面数、材质、骨骼权重、bounding box、CustomizationInfo、MeshInfoIndex、BoneInfoIndex。

### 3.6 搜索并导入目标护甲/头盔 Unit

人工操作：

- 使用 SDK Search Found Archives 找目标，例如 DS-42 护甲和头盔。
- 导入目标 Unit。

代码含义：

- `SearchByEntryID(...)`
- `Global_TocManager.GetEntryByLoadArchive(unit_file_id, UnitID)`
- `template_entry.Load(...)`

Core 对应能力：

- 需要基于 Archive index 找到目标 Unit。
- 后续可结合 ArchiveID -> 物品分类/名称映射，提供“选择目标护甲/头盔”的 UI 数据源。

### 3.7 把目标护甲 mesh 转换为极小面

人工操作：

- 选中目标护甲集合，使用“转换选中集合网格为极小面”。

代码位置：

- `VMT_OT_merge_vertices_in_collection`
- `VMT_OT_save_selected_collection_to_patch` 中也有 RawMesh 级极小化逻辑。

代码含义：

- Blender 层：清空 mesh，只创建极小三角形。
- RawMesh 层：把每个 `raw_mesh` 改成 3 个顶点、1 个三角形。

Core 对应能力：

- 可以直接实现，不需要 Blender。
- 建议服务名：`UnitMeshMinifier`。

### 3.8 源模型与目标物体一一对应、合并

人工操作：

- 将源模型物体与目标物体一一对应。
- 胖/瘦不匹配时复制源模型或选择其中一个。
- 把源模型合并到目标物体。

代码含义：

- 手动阶段本质是确定映射：

```text
source RawMesh -> target Unit RawMeshes[target MeshInfoIndex]
```

- Blender 的 `join` 只是让最终保存时 `GetObjectsMeshData` 从目标 object 上读到合并后的几何。
- 真正写回发生在 `BatchSaveStingrayUnitOperator`：

```text
Entry.LoadedData.RawMeshes[mesh_index] = mesh
Entry.Save(...)
```

Core 对应能力：

- 需要 `UnitMeshRetargeter`。
- 输入应是：
  - 源 Unit mesh 列表
  - 目标 Unit mesh 列表
  - 用户或算法给出的 mapping
- 输出是修改后的目标 `UnitMeshModel`。

### 3.9 为选中集合添加骨骼修改器

人工操作：

- 使用“为选中集合添加骨骼修改器”。

代码位置：

- `VMT_OT_add_armature_to_selected_collection`
- `GetMeshData(...)` 内部读取 armature modifier 和 vertex groups。

代码含义：

- Blender 中通过 Armature modifier 建立 mesh -> armature 的关系。
- 导出时把 vertex groups 转成 HD2 的：
  - `VertexWeights`
  - `VertexBoneIndices`
  - `BoneInfoArray.Remaps`
  - `TransformInfo.NameHashes`

Core 对应能力：

- 如果源模型已经来自 HD2 Unit，理论上可以直接保留其权重数据并映射到目标骨架。
- 如果引入外部模型格式，则需要完整骨骼/权重 remap。

### 3.10 删除源 Patch 中旧 Unit，只保留材质/贴图

人工操作：

- 制作干净新护甲适配时，保存前删除源 Patch 中旧 Unit。

代码位置：

- `Global_TocManager.RemoveEntryFromPatch(entry.FileID, UnitID)`

代码含义：

- 只删除 Unit entry。
- material/texture entries 保留，供新目标 Unit 的 mesh 材质引用使用。

Core 对应能力：

- Patch rewriter 应支持按 TypeID/FileID 删除 entry。

### 3.11 保存选中集合到 Patch

人工操作：

- 选中集合，点击“保存选中集合到Patch”。

代码位置：

- `VMT_OT_save_selected_collection_to_patch`
- `bpy.ops.helldiver2.archive_unit_batchsave()`
- SDK `BatchSaveStingrayUnitOperator`

代码含义：

1. 收集选中 mesh。
2. 顶点数小于等于 10 的 mesh 视为极小 mesh，直接 RawMesh 级极小化对应 Unit。
3. 正常 mesh 通过 SDK batchsave 写回。
4. `GetObjectsMeshData` 把 Blender object 变成 `RawMeshClass`。
5. 替换 `Entry.LoadedData.RawMeshes[MeshInfoIndex]`。
6. `Entry.Save(...)` 调用 `SaveStingrayUnit`。
7. `StingrayMeshFile.Serialize(...)` 重建 Unit TocData 和 GpuData。

Core 对应能力：

- 需要 Unit mesh writer。
- 需要 Patch sidecar writer。

## 4. Patch 文件层实际变化

### 4.1 TOC 层

会发生：

- 删除源 Patch 中旧 Unit entries。
- 添加/覆盖目标 Unit entries。
- 保留 material/texture entries。
- 重新计算每个 entry 的：
  - `TocDataOffset`
  - `StreamOffset`
  - `GpuResourceOffset`
  - `TocDataSize`
  - `StreamSize`
  - `GpuResourceSize`
  - `EntryIndex`

### 4.2 Unit TocData 层

会重写：

- Unit header offsets
- `BoneInfoOffset`
- `StreamInfoOffset`
- `MeshInfoOffset`
- `MaterialsOffset`
- `EndingOffset`
- `MeshInfo.Sections`
- `MeshInfo.NumMaterials`
- `MeshInfo.NumSections`
- `StreamInfo.Components`
- `StreamInfo.VertexStride`
- `StreamInfo.NumVertices`
- `StreamInfo.NumIndices`
- material section IDs
- material IDs

### 4.3 GPU sidecar 层

会重新生成：

- vertex buffer
- index buffer
- vertex positions
- normals
- UVs
- colors
- bone weights
- bone indices

并更新：

- `StreamInfo.VertexBufferOffset`
- `StreamInfo.VertexBufferSize`
- `StreamInfo.IndexBufferOffset`
- `StreamInfo.IndexBufferSize`

### 4.4 `.gpu_resources` 文件层

SDK 写入时：

- 每个 entry 的 GPU data 按 64 字节对齐。
- `GpuResourceOffset` 指向新 sidecar 中的位置。
- `GpuResourceSize` 为新序列化后的 GPU data 长度。

这解释了为什么新版可用 Mod 的 `.gpu_resources` 与旧版/py/C# 修复版显著不同。

## 5. 为什么 header 修复不足

已有 `ModUnitRepairService` / Python `hd2-repatcher` 风格修复主要处理：

- Unit version
- LOD group offset
- joint/list offset
- 旧版 layout 的局部插入/删除
- 无效 entry/range 清理

它不能生成新的 vertex/index buffer，也不能把源 mesh 映射到目标 mesh slot。

因此它最多做到：

```text
不崩溃 / Unit 结构基本可读
```

不能保证：

```text
模型可见 / 权重正确 / 材质正确 / GPU sidecar 匹配
```

## 6. 无 Blender 实现边界

### 6.1 可以无 Blender 实现的部分

- Patch TOC 读取/重建。
- Entry 删除/添加/覆盖。
- Unit TocData 解析。
- GPU sidecar 解析。
- RawMesh 读取。
- RawMesh 极小化。
- RawMesh slot 替换。
- Unit TocData 重新序列化。
- GPU sidecar 重新生成。
- 生成新 Patch/Zip。

### 6.2 需要替代 Blender 的部分

Blender 当前承担：

- 可视化 mesh。
- 人工选择/删除无用 mesh。
- 人工建立源 mesh 到目标 mesh 的映射。
- mesh join。
- triangulate。
- UV seam split。
- sharp edge split。
- custom normal transfer。
- vertex group normalize/limit。
- armature modifier 提供骨骼语义。

无 Blender 时可以替代，但需要 Core/UI 分工：

- Core 负责数据模型与转换。
- Manager UI 负责展示 mesh slot 特征和让用户确认映射。
- 算法负责基础猜测和验证。

### 6.3 不能只靠二进制块复制的情况

如果源/目标存在以下差异，直接复制 bytes 很容易失败：

- stream component layout 不同；
- vertex stride 不同；
- index buffer 16/32 位不同；
- material section 数不同；
- bone remap 不同；
- LOD layout 不同；
- mesh transform 不同；
- target Unit 的 bones/state machine 不同。

正确方向是对象模型级别重写，不是 offset 级 patch。

## 7. 建议的 Core 移植阶段

### 阶段 1：只读模型

目标：Core 能读懂 Unit mesh。

建议新增：

- `UnitMeshModel`
- `RawMeshModel`
- `MeshInfoModel`
- `StreamInfoModel`
- `MeshSectionModel`
- `BoneInfoModel`
- `TransformInfoModel`
- `UnitMeshReader`

输入：

```text
TocData + GpuData
```

输出：

```text
UnitMeshModel.RawMeshes
```

### 阶段 2：写回同构 Unit

目标：读入 Unit 后，不修改或轻微修改，再写出，能保持游戏可读。

实现：

- vertex component codecs
- index buffer writer
- vertex buffer writer
- StreamInfo offset/size 更新
- MeshInfo sections 更新
- MaterialsOffset/EndingOffset 更新

### 阶段 3：极小化 mesh

目标：实现 JJK 工具的极小面逻辑。

API 示例：

```text
UnitMeshMinifier.MinifyAllRawMeshes(unit)
UnitMeshMinifier.MinifyRawMesh(rawMesh)
```

### 阶段 4：RawMesh slot 替换

目标：把源 mesh 复制到目标 Unit 指定 slot。

API 示例：

```text
UnitMeshRetargeter.ApplyMapping(sourceUnit, targetUnit, mapping)
```

其中 mapping：

```text
source Unit/FileID + source MeshInfoIndex -> target Unit/FileID + target MeshInfoIndex
```

### 阶段 5：Patch 级 Armor Adaptation

目标：从一个源 Mod 生成新的目标护甲适配 Mod。

建议服务：

- `IArmorAdaptationPlanner`
- `IArmorAdaptationService`
- `IPatchMeshRewriteService`

流程：

1. 导入源 Patch。
2. 提取源 Unit mesh 列表。
3. 选择目标护甲/头盔 Unit。
4. 生成映射建议。
5. 用户确认映射。
6. 删除源 Unit entries。
7. 写入目标 Unit entries。
8. 保留材质/贴图依赖。
9. 导出 zip。

## 8. 对现有 Core 的设计提醒

不要把这些逻辑写进 `HD2ModManager`。

应放在 `HD2ModCore`：

- Unit mesh parser/writer
- Patch mesh rewrite
- Armor adaptation planner
- Compatibility analyzer
- Repair classification

Manager 只负责：

- 展示资源列表；
- 展示 mesh mapping UI；
- 触发 Core 服务；
- 显示进度和结果。

## 9. 后续实现优先级

推荐优先级：

1. 移植 `StingrayMeshFile` 的只读解析。
2. 为 Unit entries 暴露 mesh summary：mesh count、vertex count、index count、materials、bone info、LOD。
3. 移植 GPU vertex/index buffer 读取。
4. 实现 RawMesh -> Unit 写回 roundtrip 测试。
5. 实现 RawMesh 极小化。
6. 实现 source->target mesh mapping 写回。
7. 最后再做自动匹配和 UI。

## 10. 当前 Core 移植进度

已在 `HD2ModCore` 落地第一批 Unit mesh 读写能力：

- `HD2ModCore/Application/IUnitMeshReader.cs`
- `HD2ModCore/Application/IUnitMeshWriter.cs`
- `HD2ModCore/Application/IUnitMeshMinifier.cs`
- `HD2ModCore/Application/IUnitMeshRetargeter.cs`
- `HD2ModCore/Application/IUnitMeshReplacementStrategy.cs`
- `HD2ModCore/Application/IPatchUnitMeshReplacementPlanner.cs`
- `HD2ModCore/Application/IPatchUnitMeshAutomationReporter.cs`
- `HD2ModCore/Application/IPatchUnitMeshFolderAutomationReporter.cs`
- `HD2ModCore/Application/IPatchUnitMeshSourceCatalogBuilder.cs`
- `HD2ModCore/Application/IGameDataPackageResolver.cs`
- `HD2ModCore/Application/IArchiveUnitMeshReader.cs`
- `HD2ModCore/Application/IUnitMeshAdaptationPlanner.cs`
- `HD2ModCore/Application/IPatchTocFileCollector.cs`
- `HD2ModCore/Domain/UnitMeshModel.cs`
- `HD2ModCore/Domain/UnitMeshWriteResult.cs`
- `HD2ModCore/Domain/UnitMeshReplacementCandidate.cs`
- `HD2ModCore/Domain/UnitMeshReplacementCandidateKind.cs`
- `HD2ModCore/Domain/UnitMeshReplacementComponentSignature.cs`
- `HD2ModCore/Domain/PatchUnitMeshReplacementCandidate.cs`
- `HD2ModCore/Domain/PatchUnitMeshReplacementPlan.cs`
- `HD2ModCore/Domain/PatchUnitMeshAutomationEntryReport.cs`
- `HD2ModCore/Domain/PatchUnitMeshAutomationReport.cs`
- `HD2ModCore/Domain/PatchUnitMeshSourceCatalog.cs`
- `HD2ModCore/Domain/PatchUnitMeshSourceCatalogEntry.cs`
- `HD2ModCore/Domain/PatchUnitMeshSourceMeshSummary.cs`
- `HD2ModCore/Domain/PatchUnitMeshSourceCatalogFailure.cs`
- `HD2ModCore/Domain/ArchiveTocEntry.cs`
- `HD2ModCore/Domain/ArchiveEntryPayload.cs`
- `HD2ModCore/Domain/ArchiveUnitMesh.cs`
- `HD2ModCore/Domain/UnitMeshAdaptationIntent.cs`
- `HD2ModCore/Domain/UnitMeshAdaptationPlan.cs`
- `HD2ModCore/Domain/UnitMeshAdaptationStep.cs`
- `HD2ModCore/Domain/UnitMeshAdaptationStepKind.cs`
- `HD2ModCore/Domain/PatchTocFileSet.cs`
- `HD2ModCore/Infrastructure/GameDataPackageResolver.cs`
- `HD2ModCore/Infrastructure/ArchiveUnitMeshReader.cs`
- `HD2ModCore/Infrastructure/UnitMeshReader.cs`
- `HD2ModCore/Infrastructure/UnitMeshWriter.cs`
- `HD2ModCore/Infrastructure/UnitMeshMinifier.cs`
- `HD2ModCore/Infrastructure/UnitMeshRetargeter.cs`
- `HD2ModCore/Infrastructure/UnitMeshReplacementStrategy.cs`
- `HD2ModCore/Infrastructure/PatchUnitMeshReplacementPlanner.cs`
- `HD2ModCore/Infrastructure/PatchUnitMeshAutomationReporter.cs`
- `HD2ModCore/Infrastructure/PatchUnitMeshFolderAutomationReporter.cs`
- `HD2ModCore/Infrastructure/PatchUnitMeshSourceCatalogBuilder.cs`
- `HD2ModCore/Infrastructure/PatchTocFileCollector.cs`
- `HD2ModCore/Infrastructure/UnitMeshAdaptationPlanner.cs`
- `HD2ModCore.Tests/UnitMeshReaderTests.cs`
- `HD2ModCore.Tests/UnitMeshReplacementStrategyTests.cs`
- `HD2ModCore.Tests/PatchUnitMeshReplacementPlannerTests.cs`
- `HD2ModCore.Tests/PatchUnitMeshAutomationReporterTests.cs`
- `HD2ModCore.Tests/PatchUnitMeshFolderAutomationReporterTests.cs`
- `HD2ModCore.Tests/PatchUnitMeshSourceCatalogBuilderTests.cs`
- `HD2ModCore.Tests/PatchTocFileCollectorTests.cs`
- `HD2ModCore.Tests/ArchiveUnitMeshReaderTests.cs`
- `HD2ModCore.Tests/UnitMeshAdaptationPlannerTests.cs`

当前 `UnitMeshReader` 已支持：

1. 读取 Unit header 中的关键 offset：
  - `BoneInfoOffset`
  - `StreamInfoOffset`
  - `MeshInfoOffset`
  - `MaterialsOffset`
  - `EndingOffset`
2. 读取 `StreamInfo`：
  - components
  - vertex/index count
  - stride
  - vertex/index buffer offset/size
  - index buffer 16/32 位类型
3. 读取 `MeshInfo`：
  - mesh id
  - LOD index
  - transform index
  - stream index
  - material slot ids
  - sections
4. 读取 Unit-level material bindings。
5. 从 `.gpu_resources` 读取 RawMesh 的：
  - triangle indices
  - raw vertex stride records
  - 基础 vertex component decode
6. 目前已解码的 component formats：
  - `float`
  - `vec2_float`
  - `vec3_float`
  - `rgba_r8g8b8a8`
  - `vec4_uint32`
  - `vec4_uint8`
  - `vec4_1010102`
  - `unk_normal` packed oct normal
  - `vec2_half`
  - `vec4_half`

当前 `UnitMeshWriter` 已支持：

1. 保守复制原始 Unit `TocData`，只 patch 已知 stream/section 字段。
2. 从 `RawMeshData` 重建 `.gpu_resources`：
  - 先写 vertex buffer。
  - 后写 index buffer。
  - 支持 16-bit / 32-bit index buffer。
3. 更新 `StreamInfo`：
  - `NumVertices`
  - `NumIndices`
  - `VertexBufferOffset`
  - `VertexBufferSize`
  - `IndexBufferOffset`
  - `IndexBufferSize`
4. 更新 `MeshInfo.Sections`：
  - `VertexOffset`
  - `NumVertices`
  - `IndexOffset`
  - `NumIndices`
5. 多 section mesh 的 section-level index offset / index count roundtrip 已覆盖。
6. 当 `RawMeshData.Sections` 多于目标 `MeshInfo.Sections` 时会显式拒绝写回，避免生成 TocData 无法描述的孤立 index 数据。
7. 16-bit 与 32-bit index buffer 写回路径均已有 synthetic roundtrip 覆盖。
8. 写回时会拒绝超过 stream vertex stride 的 raw vertex data。
9. 写回 16-bit index buffer 时会拒绝大于 `65535` 的 index。
10. 写回 triangle index 前会先校验其引用的 vertex 是否存在，避免生成引用越界的 index buffer。

当前 `UnitMeshMinifier` 已支持：

1. `MinifyAll(...)`：把全部 RawMesh 替换为极小占位三角形。
2. `MinifyRawMesh(...)`：按 `MeshInfoIndex` 极小化单个 RawMesh。
3. 占位 mesh 会保留原 stream vertex stride / component layout，并写入 3 个很小的 position 顶点。
4. 极小化结果可直接交给 `UnitMeshWriter` 写回并再次由 `UnitMeshReader` 解析。

当前 `UnitMeshRetargeter` 已支持：

1. `ReplaceRawMesh(...)`：把 source Unit 的 RawMesh 数据替换到 target Unit 的指定 `MeshInfoIndex`。
2. 默认严格模式下，替换前检查 source/target stream layout：
  - `VertexStride`
  - component count
  - component `Type`
  - component `Format`
  - component `Index`
  - component `Size`
3. 实验模式下允许跨 layout fallback，并把 source vertex raw data normalize 到 target stride。
4. 替换时保留 target section 的 material index / material slot id，避免 source material slot 污染目标 Unit。
5. 替换结果可直接交给 `UnitMeshWriter` 写回并再次由 `UnitMeshReader` 解析。
6. vertex stride 不一致时会显式拒绝 retarget，避免跨布局盲写。
7. component count 或 component layout 不一致时会显式拒绝 retarget。
8. 实验模式下会裁剪 16-bit target index buffer 的 source vertices / triangles，并把 source sections 裁剪到 target MeshInfo 能描述的数量。

当前 `UnitMeshReplacementStrategy` 已支持：

1. 在不依赖 Blender 的前提下，按结构扫描 target/source Unit RawMesh 替换候选。
2. 默认严格模式下，候选必须满足 stream layout 兼容：
  - `VertexStride`
  - component count
  - component `Type`
  - component `Format`
  - component `Index`
  - component `Size`
3. 实验模式下允许 stream layout 不同的 `ExperimentalFallback` candidate，用于第一阶段 smoke 暴露真实 writer/format 问题。
4. 候选会记录：
  - target/source `MeshInfoIndex`
  - target/source `MeshId`
  - `LodIndex`
  - `StreamIndex`
  - vertex stride
  - component layout signature
  - match kind
  - score
  - reason
5. 当前 strict match kind 从强到弱为：
  - `SameMeshId`
  - `SameLodAndMaterialSlots`
  - `SameLod`
  - `LayoutOnly`
6. 实验 match kind 为 `ExperimentalFallback`，它不是安全性保证，只表示测试阶段允许进入 retarget/write dry-run。
7. 策略层目前只负责“发现可尝试的结构候选”，不直接写回 patch，也不替代 `UnitMeshRetargeter` 的最终兼容性校验。

当前 `PatchUnitMeshReplacementPlanner` 已支持：

1. 输入一组 `.patch_n` TOC 文件与一个 source Unit entry，构建 patch-level replacement dry-run plan。
2. 对每个扫描到的 target entry：
  - 跳过 source 自身。
  - 使用 `IPatchUnitMeshReader` 解析 target Unit。
  - 使用 `IUnitMeshReplacementStrategy` 选择最佳 RawMesh 候选。
  - 使用 `IPatchUnitMeshEditor.ReplaceRawMeshAsync(...)` 生成 dry-run edit。
  - 复用 `IPatchArchiveBatchPlanner` 汇总 edited / skipped / failed。
3. 支持可选 `sourceMeshInfoIndex` 过滤，允许后续 UI/CLI 指定 source Unit 中的某个 RawMesh 作为替换源。
4. 输出 `PatchUnitMeshReplacementPlan`，同时保留：
  - source entry
  - requested source mesh index
  - patch-level candidates
  - batch archive write plan
5. 当前仍是 dry-run 自动规划，不直接写文件；真正落盘仍交给 `PatchArchiveFileWriter`。

当前 `PatchUnitMeshAutomationReporter` 已支持：

1. 包装 `IPatchUnitMeshReplacementPlanner`，生成 UI/CLI 可消费的 entry 级 dry-run 报告。
2. 报告保留 replacement plan 本体，因此不会丢失 archive write plan 与候选详情。
3. 每个 entry report 记录：
  - entry identity
  - edited / skipped / failed status
  - reason
  - matched candidate（如果存在）
  - exception（如果失败）
4. edited entry 的 reason 会补充 candidate kind 与 strategy reason，便于后续解释“为什么这个 entry 被选中”。
5. 当前仍不执行真实写回；它只是把 dry-run planning 的结果结构化为可展示/可记录的报告。

当前文件夹级 dry-run 入口已支持：

1. `PatchTocFileCollector` 会从目录递归收集严格 `.patch_数字` TOC 文件。
2. 收集器会排除 `.patch_数字.gpu_resources`、`.patch_数字.stream` 等 sidecar，避免重复/误扫描。
3. `PatchUnitMeshFolderAutomationReporter` 接收 patch 文件夹路径、source entry 和可选 source mesh index。
4. 文件夹报告器先收集 TOC 文件，再委托 `IPatchUnitMeshAutomationReporter` 生成现有 entry 级报告。
5. 如果文件夹中没有任何严格 `.patch_数字` TOC，会抛出 `InvalidDataException`，避免生成空的误导性报告。
6. 该入口仍为 dry-run/report-only，不写入、不覆盖、不 apply。

当前 source Unit mesh catalog 地基已支持：

1. `PatchUnitMeshSourceCatalogBuilder` 接收 patch 文件夹路径，复用 `PatchTocFileCollector` 收集严格 `.patch_数字` TOC。
2. 对每个 TOC 通过 `IPatchTocScanner.ScanEntriesAsync(...)` 扫描 entry，并只尝试解析 Unit TypeID entry。
3. 对可解析 Unit entry，输出可供 UI/CLI 选择的 source catalog entry：
  - patch entry identity
  - Unit version
  - Unit name hash
  - 每个 RawMesh 的 mesh info index / mesh id / LOD / stream index
  - vertex/index/material/section counts
  - vertex stride
  - component layout signature
4. 对无法扫描或无法解析的 TOC/entry，记录 `PatchUnitMeshSourceCatalogFailure`，不让单个坏 entry 中断整个 catalog 构建。
5. 该层只负责“列出可选 source mesh 与失败原因”，不执行自动匹配、不写回、不 apply。

当前 original archive target Unit template reader 地基已支持：

1. `GameDataPackageResolver` 已抽象为 `IGameDataPackageResolver`，可从原版游戏 data 目录读取 archive TOC 与 resource payload。
2. `PatchTocScanner` 新增 in-memory `ScanEntries(...)`，可直接解析 `GameDataPackageToc.Data` 中的 entry offset/size 元数据，并支持 slim entry offset 布局。
3. `ArchiveUnitMeshReader` 可按 `gameDataDirectory + archiveName + AssetKey`：
  - 解析目标 archive TOC；
  - 定位 Unit entry；
  - 读取目标 Unit 的 `TocData` / `.stream` / `.gpu_resources` payload；
  - 调用 `UnitMeshReader` 产出 `UnitMeshModel`；
  - 返回 `ArchiveUnitMesh`，保留原始 payload 与 `ArchiveTocEntry` 元数据。
4. `CoreServices.CreateArchiveUnitMeshReader()` 已提供默认工厂，便于后续 adaptation planner 或 UI 复用。
5. 该层对应人工流程中的“Default Archive / Search Found Archives 导入目标护甲或头盔 Unit”，仍只读、不写入、不自动适配。

当前 source mod -> original archive target template 的 adaptation dry-run 地基已支持：

1. `UnitMeshAdaptationPlanner` 接收已解析的 source `PatchUnitMesh` 与 target `ArchiveUnitMesh`。
2. 它复用 `UnitMeshReplacementStrategy` 查找同 stream layout 的候选，并支持可选 `sourceMeshInfoIndex` 过滤。
3. 当前选择策略是保守的非冲突 greedy selection：一个 target mesh slot 只接收一个 source mesh，一个 source mesh 也只用于一个 selected replacement。
4. 写入前先对 target template 全部 RawMesh 执行极小化，再把选中的 source RawMesh retarget 到目标 slot。
5. 输出 `UnitMeshAdaptationPlan`，包含：
  - 是否可写 `CanWrite`；
  - candidate 列表；
  - minify / replace step 列表；
  - 编辑后的 `UnitMeshModel`；
  - dry-run `UnitMeshWriteResult`；
  - 不可写原因或 writer failure 原因。
6. 该层对应人工流程中的“目标原版 mesh 极小化 + 源 mesh 合并到目标 slot + 保存 Unit”，但仍只生成内存 dry-run payload，不删除源 Unit entries、不写 patch 文件、不导出 zip。
7. 默认实现仍只允许同 vertex stream layout 替换；`CoreServices.CreateUnitMeshAdaptationPlanner()` 目前单独启用实验性跨 layout fallback，用于第一阶段真实数据 smoke。

已验证：

- `Read_MinimalUnit_ReturnsMeshSummary` synthetic Unit reader 通过。
- `Write_ReadRoundTrip_PreservesMinimalRawMesh` reader/writer roundtrip 通过。
- `Minify_WriteReadRoundTrip_ReplacesRawMeshWithPlaceholderTriangle` minifier/writer/reader 组合通过。
- `Retarget_WriteReadRoundTrip_ReplacesTargetRawMeshWithSourceRawMesh` retargeter/writer/reader 组合通过。
- `Write_ReadRoundTrip_PreservesMultiSectionRawMesh` 多 section writer roundtrip 通过。
- `Write_ReadRoundTrip_PreservesUInt32IndexBuffer` 32-bit index buffer writer roundtrip 通过。
- `Write_RawMeshWithTooManySections_Throws` writer section count safety 拒绝路径通过。
- `Write_VertexDataLargerThanStride_Throws` writer vertex stride safety 拒绝路径通过。
- `Write_UInt16IndexOverflow_Throws` writer 16-bit index overflow 拒绝路径通过。
- `Write_TriangleReferencesMissingVertex_Throws` writer triangle vertex reference 拒绝路径通过。
- `Retarget_DifferentVertexStride_Throws` retargeter layout mismatch 拒绝路径通过。
- `Retarget_DifferentComponentCount_Throws` retargeter component count mismatch 拒绝路径通过。
- `Retarget_DifferentComponentLayout_Throws` retargeter component layout mismatch 拒绝路径通过。
- `UnitMeshReplacementStrategyTests` 验证替换策略会返回同 mesh id / 同 LOD 与 material slot / layout-only 候选，拒绝 component layout 不同的候选，并按 score 排序。
- `PatchUnitMeshReplacementPlannerTests` 验证 patch-level 自动替换规划器会跳过 source 自身、为兼容 target 生成候选与 edit、支持 source mesh 过滤，并拒绝不存在的 source mesh index。
- `PatchUnitMeshAutomationReporterTests` 验证 dry-run 报告层会汇总 entry / candidate / edited / skipped / failed 统计，保留异常，并把输入参数转发给 replacement planner。
- `PatchTocFileCollectorTests` 验证文件夹收集器只返回严格 `.patch_数字` 文件，并排除 sidecar 与非数字 patch 后缀。
- `PatchUnitMeshFolderAutomationReporterTests` 验证文件夹级入口会转发收集到的 TOC、source entry 与 source mesh index，并在空文件夹场景抛错。
- `PatchUnitMeshSourceCatalogBuilderTests` 验证 source catalog builder 会从文件夹扫描 Unit entry、生成 RawMesh 摘要、跳过非 Unit entry，并把坏 Unit / 坏 TOC 记录为 failure。
- `ArchiveUnitMeshReaderTests` 验证 original archive target reader 能从 resolver/scanner 组合读取 Unit payload 与模型，拒绝非 Unit asset，报告缺失 TOC、缺失 asset、缺失必需 payload。
- `UnitMeshAdaptationPlannerTests` 验证 adaptation dry-run 会为兼容 source/target 生成可序列化 payload，支持 source mesh 过滤，拒绝不兼容 stream layout，并拒绝不存在的 source mesh index。
- `ScanEntriesAsync_StandardHeaderToc_ReturnsEntryOffsetsAndSizes` patch TOC entry offset/size 元数据解析通过。
- `PatchEntryPayloadReaderTests` 验证 patch entry payload reader 能按 offset/size 读取 TOC / `.stream` / `.gpu_resources`，并拒绝越界范围。
- `PatchUnitMeshReaderTests` 验证 patch-level Unit mesh reader 能从单个 Unit entry 提取 payload 并解析 `UnitMeshModel`，且拒绝非 Unit TypeID entry。
- `PatchUnitMeshEditorTests` 验证 patch-level dry-run editor 能生成 minify / same-layout retarget 后的待写回 `TocData` 与 `GpuResourceData`，且不会直接修改 patch 文件。
- `PatchArchiveDryWriterTests` 验证 archive-level dry-run writer 能重建 `.patch_n` bytes 与 `.gpu_resources` bytes，更新 entry offset/size 元数据，且不修改原始文件。
- `PatchArchiveFileWriterTests` 验证 archive write plan 只能安全写入指定输出目录，拒绝直接写回源目录，并默认拒绝覆盖既有输出文件。
- `PatchArchiveBatchPlannerTests` 验证批量计划层能按 entry 汇总 edited / skipped / failed，并只把成功 edit 交给 archive dry writer。
- `DiagnosticsPatchSampleTests` 使用 `_diagnostics/旧版` 与 `_diagnostics/新版` 中复制的真实样本，验证新旧样本都存在可定位的 Unit payload / `.gpu_resources` sidecar 范围。
- `ReadPayloadAsync_DiagnosticsSamples_CanParseSomeRealUnitMeshes` 验证真实样本中可定位的 Unit payload 能通过 `PatchUnitMeshReader` 提取并解析。
- `HD2ModCore.Tests` 全量通过。
- 最新 `HD2ModCore.Tests` 全量通过：109 个测试，0 失败，0 跳过。

当前 patch-level 地基新增进展：

1. `PatchTocEntry` 已携带后续重写 Unit entry 需要的 TOC 元数据：
  - `TocDataOffset`
  - `StreamOffset`
  - `GpuResourceOffset`
  - `TocDataSize`
  - `StreamSize`
  - `GpuResourceSize`
  - `EntryIndex`
2. `PatchTocScanner.ScanEntriesAsync(...)` 已解析标准 80-byte entry 中上述 offset/size 字段。
3. 这一步把 patch-level 自动化从“只能识别资产键”推进到“能定位 Unit payload 与 `.gpu_resources` sidecar”。
4. `PatchEntryPayloadReader` 已能读取单个 entry 的：
  - `TocData` from `.patch_n`
  - `StreamData` from `.patch_n.stream`
  - `GpuResourceData` from `.patch_n.gpu_resources`
5. payload reader 会在读取前验证范围；sidecar 使用 64 字节对齐后的可读长度，允许读取真实文件尾部对齐 padding 并补零。
6. `PatchUnitMeshReader` 已组合 `PatchEntryPayloadReader` 与 `UnitMeshReader`，把单个 Unit entry 直接提升为：
  - 原始 `PatchEntryPayload`
  - 解析后的 `UnitMeshModel`
  - 原始 `PatchTocEntry`
7. `PatchUnitMeshReader` 会拒绝非 Unit TypeID entry，避免后续策略层把 texture/material 等资源误交给 Unit reader。
8. `PatchUnitMeshEditor` 已作为 dry-run 编辑层落地：
  - `MinifyAllAsync(...)`
  - `MinifyRawMeshAsync(...)`
  - `ReplaceRawMeshAsync(...)`
  - 输出 `PatchUnitMeshEditResult`，包含原始 payload / 原始模型 / 编辑后模型 / 新 `TocData` / 新 `GpuResourceData` / size delta。
  - 当前只生成待写回 bytes，不修改 `.patch_n` 或 sidecar 文件。
9. `PatchArchiveDryWriter` 已作为 archive-level dry-run writer 落地：
  - 输入一个 `.patch_n` 与一组 `PatchUnitMeshEditResult`。
  - 输出 `PatchArchiveWritePlan`，包含新 `.patch_n` bytes、新 `.gpu_resources` bytes、保留的 `.stream` bytes、更新后的 entries、编辑 placement delta。
  - 会重建 TOC payload 区与 GPU sidecar，并把 entry 的 `TocDataOffset/TocDataSize/GpuResourceOffset/GpuResourceSize` 写回到内存中的 TOC entry table。
  - `.gpu_resources` entry payload 按 64 字节边界重排。
  - 当前仍是 dry-run，不落盘。
10. `PatchArchiveFileWriter` 已作为安全落盘层落地：
  - 输入 `PatchArchiveWritePlan` 与输出目录。
  - 只向指定输出目录写 `.patch_n` / `.stream` / `.gpu_resources`。
  - 拒绝输出目录等于源 patch 目录，避免直接覆盖原 mod。
  - 默认拒绝覆盖既有输出文件，只有显式 `overwriteExisting: true` 才允许覆盖。
11. `PatchArchiveBatchPlanner` 已作为批量 dry-run 编排层落地：
  - 输入多个 `.patch_n` 与一个 entry-level edit factory。
  - 对每个 entry 记录 `Edited` / `Skipped` / `Failed`。
  - 单个 entry 的异常会被收集为 failed result，不会中断同 patch 后续 entry 计划。
  - 只把成功生成且身份匹配的 `PatchUnitMeshEditResult` 交给 `PatchArchiveDryWriter`。
  - 输出 `PatchArchiveBatchPlan`，包含 patch-level write plans 与全局 entry results 统计。
12. 真实样本已复制到 `_diagnostics/旧版` 与 `_diagnostics/新版`，并加入 `.gitignore`，只作为本地诊断/回归输入。
13. 真实样本扫描确认大量 Unit entry 带 `.gpu_resources` sidecar；其中也存在部分历史/异常 patch entry range，因此自动化写回前必须保留 range validation 与跳过/报告机制。
14. 真实样本回归已经从“能定位 Unit payload / sidecar 范围”推进到“能提取部分真实 Unit payload 并解析为 `UnitMeshModel`”。
15. 临时 smoke 已把用户提供的 `E:\新建文件夹 (3)\旧版` / `E:\新建文件夹 (3)\新版` 复制到 `_diagnostics`；旧版样本 source catalog 扫描到 141 个严格 patch TOC，但当前 `UnitMeshReader.ReadMesh(...)` 对这些 Unit 全部失败，失败原因集中为 `OverflowException`，因此真实样本 adaptation 还不能进入 archive-template 写入 smoke。
16. 这个 smoke 结论说明 adaptation planner 的对象模型地基已可测，但真实旧版 mod 的 Unit mesh layout 覆盖仍不足；下一步应先扩展/校正 `UnitMeshReader` 对真实 Unit `MeshInfo` 的读取，再重新跑 source catalog smoke。

下一步应进入 source catalog + original archive target template 之上的 adaptation planning，而不是继续扩大旧 header repair，也不是依赖新版 mod 借 mesh：

1. 扩展/校正 `UnitMeshReader` 对真实旧版 Unit `MeshInfo` / RawMesh 的解析，避免 source catalog 全部落入 `OverflowException`。
2. 重新对真实 `.patch_*` 样本做 source catalog smoke，输出可读 source mesh 与失败原因分布。
3. 在拥有可读 source mesh 后，用 original archive target template 跑 `UnitMeshAdaptationPlanner` dry-run，记录 candidate / replacement / minified / payload size。
4. 后续做原版游戏资产 counterpart extraction / comparison，用于更可靠地判断旧版 mod 与当前目标模板结构差异。
5. 完成真实样本 dry-run 后即可把流程接入 Manager UI 或 CLI，但核心逻辑仍必须留在 `HD2ModCore`。

## 11. 当前已知验证依据

SailorMizu 新旧包对照显示：

- 旧版 Unit version 为 `0x00A4CD35`。
- 新版 Unit version 为 `0x00A4CD36`。
- 新旧 Unit FileID 集合相同。
- 新版 23 个 Unit 的 payload 全部变化。
- 新版 `.gpu_resources` 大小和 hash 均变化。
- 23 个 Unit 的 GPU offset/size 全部变化。
- C# advanced repair 与 Python repatcher 基本一致，但二者都保留旧 GPU sidecar，因此不能达到新版可见模型的效果。

这进一步证明：

```text
真正有效的新包不是单纯 Unit header 修复，而是完整 mesh/gpu 重导出。
```
