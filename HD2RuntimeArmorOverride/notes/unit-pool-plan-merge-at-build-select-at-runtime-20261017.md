# 单元池方案：把"合并"从运行期挪到装配期，只把"选择"留在运行期（2026-10-17）

## 用户的提议

运行中由 Lua 管理一个 unit 池：默认用原版缓存，检测到玩家用某套护甲时，
异步把玩家需要的网格重新合并进标准 7 槽，覆盖游戏注册的 unit。
接受"游戏内合并"，不能接受"外部合并"，因为外部合并要重启游戏。

**这个方向是对的，而且它是目前唯一能同时满足全部约束的路。**
但要把"合并"和"选择"拆开看，否则会在 Lua 里重写一整套二进制装配器。

## 关键事实：unit 本身就是"多网格容器"

`UnitMeshModel`（`HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshModel.cs`）说明：

* 一个 unit = `Streams[]`（顶点/索引缓冲描述，缓冲本体按 ID 外置在 GPU 资源里）
  + `Meshes[]`（`UnitMeshInfo`：`MeshId / LodIndex / TransformIndex / StreamIndex /
  NumSections / SectionsOffset`）
  + `Materials[]` + `BoneInfos[]` + `TransformInfo`（骨骼节点表）
* **每个 mesh 自带 `UnitMeshSemanticInfo`：`Slot / PieceType / BodyType / Weight`**
  —— 也就是说一个 unit 里的多个 mesh 可以分别属于不同槽位/层。

所以"把 43 件合进 7 个槽"在数据格式上是**天然支持**的，不是 hack。

## 而且装配器已经写好了

`CanonicalDecorationUnitAppender` 的注释原话：
"Unit-level Blender Join pipeline. All sources are appended before material layout
and palette compilation, so multiple decorations share one authoritative final Unit
representation."

API 就是我们要的：

```csharp
TryAppendMany(PatchUnitMesh targetUnit, int targetMeshInfoIndex,
              IReadOnlyList<CanonicalDecorationAppendInput> inputs,
              UnitTransformInfo avatarTransformInfo)
TryAppendLodFamily(...)   // 整族 LOD 一次重建，避免近 LOD 合了、远 LOD 没合
```

**离线的"多件合并进一个 unit"能力不需要新写。**

而且它已经接进服务层了：

* `HD2ModCore/Infrastructure/DecorationHostRebuildService.cs:208`
  —— `TryAppendLodFamily(unit, inputsByTargetMesh, avatar, replaceTargetGeometry)`，
  `inputsByTargetMesh` 本身就是"一次追加多件"的形状。
* `HD2ModCore/Infrastructure/ArmorNormalizationService.cs:145` 同款调用。
* `HD2ModCore/Infrastructure/HelmetToArmorMergeService.cs:313` 同款调用。

所以第 1 步不是"写合并器"，而是"把全部候选件喂进已有的 host 重建服务"。

## 但不要在 Lua 里重写合并

`Canonical/` 下有 20 多个文件：骨骼重定位、逆关节矩阵、LOD 骨骼调色板、材质绑定解析、
stream ABI 契约、section 布局…… 在 LuaJIT 里重写等于把几千行精密二进制代码再实现一遍，
风险远高于收益。

## 正确的拆分

| 阶段 | 在哪做 | 频率 | 需要重启吗 |
|---|---|---|---|
| **合并**：把某个槽位的全部候选网格装进一个 unit | 离线，装配期 | 装 mod 时一次 | 装 mod 时一次（正常） |
| **选择**：决定这个槽位这次画哪几件 | 运行期，Lua 改内存 | 玩家每次切换 | **不需要** |

玩家真正恨的是"改个装饰要重启"。把合并固化成"该槽位所有候选件都在一个 unit 里"，
那么"选哪几件"就退化成**每件一个开关位**——这才是运行期该做的事。

## 容量问题因此消失

7 个槽位 = 7 个键。当前上限是 3 bank × 10 slot = 30 个唯一 `(type, slot)` 键，
身体段 18。**7 ≪ 18**，根本不需要扩容，也不需要碰代码段。

## 剩下的唯一未知：运行期怎么"关掉"一个 mesh

`UnitMeshInfo` 里没有 visible 标志。可试的候选（都在内存里改一个 dword）：

1. **`NumSections = 0`** —— 渲染器按 section 循环，0 个 section 应该什么都不画。
   最优先，因为它是 mesh 级的，不共享。
2. `NumMaterials = 0` / `MaterialOffset` 指向空表。
3. `TransformIndex` 指到 scale=0 的节点 —— **不推荐**：
   `CanonicalTransformInfoExpander` 说明 TransformInfo 是按**骨骼名哈希**建的，
   同一根骨骼上的多件装饰会共享 transform，关一个会连带关掉别的。

所以优先验证 `NumSections`。

## 落地步骤

1. **离线**：用 `TryAppendMany` 给每个身体槽位造一个"全候选 union unit"，
   再跑一遍读回校验（mesh 数、semantic 的 slot/pieceType、section 数）。
   这一步不碰游戏，可完整验证。
2. **探针**：确认 union unit 的明文块在内存里的位置（和 `armor_sets` 一样是
   `LDLD` 平铺块、惰性读取），并定位每个 mesh 的 `NumSections` 字段偏移。
3. **实测**：改一个 mesh 的 `NumSections` 为 0 → 截图看那一件是否消失；
   改回 → 是否恢复。**只写数据，不写代码。**
4. 成立后：注册表把 7 个槽位指向 7 个 union unit，Lua 负责按玩家开关改
   `NumSections`。

## 需要用户确认的一点

这个方案里**合并是离线的**（union unit 随 mod 一起装），**只有选择是运行期的**。
好处是切装饰不用重启；代价是玩家装 mod 时仍然要重启一次（任何 mod 都要）。
如果用户坚持"连合并都必须在运行期完成"，那就要在 Lua 里重写整套装配器，
我不建议——但这是用户的选择。
