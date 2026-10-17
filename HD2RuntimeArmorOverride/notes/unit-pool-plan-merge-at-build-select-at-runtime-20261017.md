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

## 用户的新提议：管理器外部合并 + Lua 接收器热替换

用户给的参照物是 `MDL - Mod Dynamic Loader 1.4.2`（GUID `761188b2-…`）。
实测拆包后它的能力边界写在自己的 README 里：

```
SCRIPT - LIVE                        script addons you can start and stop right here
SCRIPT / MIXED / RESOURCE - LAUNCH ONLY
                                     mods the game loads only when it starts
```

**所以"动态加载 mod"只对 Lua 脚本成立，资源（patch 归档）是 LAUNCH ONLY。**
它的 patch 里 Lua 是 LuaJIT 字节码，字符串能看到 `loadstring`、`lua resource lookup`、
`can_get`、`ffi` + `kernel32.CreateDirectoryA`——它是**接管游戏的 Lua 资源查找、
从内存喂字符串**，所以能热加载脚本。它没有任何挂载归档的能力。

### 因此用户提议的三段，两段成立、一段不成立

| 环节 | 判断 |
|---|---|
| 管理器配一个超快合并器，改外观后立刻重合并 | ✅ **已经存在**：`CanonicalDecorationUnitAppender` + `DecorationHostRebuildService` |
| 通知我们的 Lua 接收器（运行中） | ✅ 可行。MDL 已证明 Lua 能热加载/热重载，接收器甚至可以做成 MDL live mod 方便调试 |
| **Lua 指定读取外部文件去覆盖 patch 进去的外观** | ❌ **不行**。归档只在启动时挂载，Lua 没有挂载能力 |

### 但只需要换掉最后一段，整体架构就成立

不换归档，改**换内存里的 unit 描述表**：

1. 装 mod 时（一次性）随 patch 发一个 **union unit**：该槽位所有候选 mesh 都在里面，
   GPU 缓冲在启动时全部上传完。
2. 管理器合并器额外产出一张**开关表**：`mesh 序号 → NumSections 字段在 blob 里的字节偏移`。
   这张表很小（几十个条目），随 mod 一起发。
3. 运行期：管理器把玩家的选择写成一个文件；Lua 读它，按开关表把对应 mesh 的
   `NumSections` 写成 0 或原值。

这样"外部合并"确实在外部（用户担心的重启问题因此不存在——合并结果随 mod 一次装好，
之后切换只是内存里改 dword），而运行期不需要挂载任何东西。

### 为什么不能直接热替换整块 unit blob

unit 的 `StreamInfo` 用 `VertexBufferId` / `IndexBufferId` 引用外置 GPU 缓冲。
换一整块 blob 意味着新 mesh 的缓冲必须已经在显存里——只有 union unit 全量加载
才满足这个前提。所以"外部重合并整块推入"不如"预载全量 + 运行期切描述表"稳。

## 需要用户确认的一点

这个方案里**合并是离线的**（union unit 随 mod 一起装），**只有选择是运行期的**。
好处是切装饰不用重启；代价是玩家装 mod 时仍然要重启一次（任何 mod 都要）。
如果用户坚持"连合并都必须在运行期完成"，那就要在 Lua 里重写整套装配器，
我不建议——但这是用户的选择。
