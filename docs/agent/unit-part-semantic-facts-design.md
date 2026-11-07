# HD2 Unit 部件语义稳定事实设计

**日期：** 2026-07-15  
**状态：** Game Data 索引期稳定事实已接入；Mod 通过完全相同的 Unit `AssetKey` 只读映射。

## 1. 目标

将 Unit 内 Mesh 识别为胸口、胯部、左右臂腿、肩甲、头部或附件，并区分内部、护甲、隐藏壳与静态网格。该事实用于高级诊断，以及后续旧 Mod 修复和跨护甲模型转移。

部件语义属于 Unit 资产自身，不属于 Profile。Profile 只决定同 AssetKey 的最终提供者。

## 2. 唯一分析来源

`HD2ModAdaptation` 负责：

1. 读取 Unit、Composite Unit 与 Bones；
2. 复用 SDK `MeshID -> bone name` 命名；当前游戏 Armor Unit 常缺少 `CustomizationInfo`，因此索引使用同步的 SDK 全局 `bonehash.txt` 作为 `MeshID -> bone name` 字典；
3. 判定 LOD、culling body、static mesh；
4. 生成 `UnitMeshPartFact`，包含部位、层级、证据、置信度和原因。

`HD2ModCore` 只在索引重建的受限 Armor 阶段编排 Adaptation 的二进制读取，持久化结果并生成中文摘要投影；不重新实现命名规则。

`HD2ModManager` 不读取 Patch 或 Game Data payload，只显示 Core 投影。

## 3. 检测时机

### 3.1 Mod

导入事务只建立低成本 AssetKey 与引用事实，不读取完整 GPU mesh，也不保存 Mod 自己的 `unit_mesh_parts`。对于替换原版资产的 Mod，其 Unit `AssetKey` 与原版目标完全相同；高级详情因此按该 AssetKey 查询 Game Data 的稳定部件事实。

没有原版事实的旧版或非原版 Unit 显示为未知，但不会触发 Patch 读取、SQLite 写入或页面加载卡顿。

### 3.2 Game Data

Game Data 索引重建先建立低成本 archive 与 AssetKey 反向索引；随后只扫描 `archivehashes.json` 中 `Armor` 分类 archive 的 Unit，并写入 `game_data_unit_parts`。索引源指纹变化时这些事实会随索引清空，因此不会跨游戏版本误用。

该阶段使用已验证的 SDK `bonehash.txt` 全局映射恢复 `g_torso_*`、`g_legs_hips_*`、`g_torso_arm_*`、`g_leg_*` 与 `g_shoulder_*` 命名。详情窗口及 Mod 表只读 SQLite，绝不解析或写入 Unit 事实。

## 3.3 体型变体与层

SDK `CustomizationInfoOffset` 位于 Unit 头部 `0x4C`；此前错误读取 `0x50`（`UnkHeaderOffset1`）会丢失体型和层信息。经新版 Patch 独立探针验证，`CustomizationInfo` 可稳定提供：

- `BodyType`：`Slim`（纤细）、`Stocky`（健壮）、`Any`（通用）；
- `PieceType`：至少包括 `Undergarment`（内部）与 `Armor`（护甲）。

这些均为 **Mesh 级** 事实：同一 Unit 可同时包含 Slim 与 Stocky 胸部，也可包含 Any 的左右腿。表格摘要显示“部位－层－体型”，如“胸口－内部－纤细”。后续转移中 `Any` 可兼容 Slim 与 Stocky；Slim 与 Stocky 的直接互配不是精确匹配。

## 4. 展示与后续使用

- Mod 高级详情的 Unit 行显示部件摘要；
- Game Data archive 二级表的 Unit 行显示部件摘要；
- 默认摘要只包含可见、非 LOD、已识别 Mesh；
- culling/static/LOD 仍保存在稳定事实中，供后续转移规划判定。

后续自动匹配至少要求：同部位、同侧、同层级、可见、非 LOD、非 static。未知和附件竞争必须进入人工确认，不得仅凭几何大小自动落位。

## 5. 启动与稳定事实

启动不得重新扫描整个 Mod 库的 Patch、stream 或 gpu_resources，也不得以“内存中的 UI 投影为空”判定 Mod 事实过期。导入已经在发布 LibrarySnapshot 前写入稳定事实。

启动时仅构建当前活动 Profile 所需的运行时 winner、材质诊断与部署状态；没有活动 Profile 时不读取任何 Mod 内容事实。普通库列表和 Mod 详情按需从 SQLite 投影，导入时只刷新新导入节点。

旧版全库 SHA-256 Patch 指纹刷新已移除：它会读取所有大型 sidecar 两次，并把每次重启都错误地判定为全库脏数据。外部修改库中只读 Mod 不再作为启动热路径支持；需要时应通过重新导入或后续明确的维护操作处理。