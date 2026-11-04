# C-placement：位置与遮挡由什么决定（只读核查，2026-11-04）

## 1 一句话答案

**位置不会变 —— 但只有走"带变换补偿的追加链路"时才不会变。** 顶点位置 = 顶点 position 分量（mesh-local）× 该 mesh 自己的 `TransformInfo` 矩阵，再按**骨骼名哈希**蒙皮；`(slot, pieceType, layer)` 完全不出现在这条算式里。真正的风险不在位置，而在**两处几何范围语义**：`CullingBounds` 不重算、`lod=-1` 深度代理被原样保留（§7）。

---

## 2 问题 1：渲染位置由什么决定

### 2.1 三段式：顶点 → mesh 变换 → 骨骼调色板

| 环节 | 谁决定 | 依据 |
|---|---|---|
| 顶点坐标 | 顶点 `Type=0` 分量，**mesh-local** | `CanonicalPositionDiagnostics.cs:44`（按 `Type==0 && Index==0` 取位置） |
| mesh-local → 模型空间 | 该 `MeshInfo` 的 `TransformIndex` 指向的 `TransformInfo` 矩阵，**每个 mesh 一个** | `UnitMeshModel.cs:116`；`CanonicalTransformResolver.cs:6-8`（"SDK GetMeshData uses the mesh TransformInfo matrix as the object origin"）、`:38`（`sourceMatrix * inverse(targetMatrix)`） |
| 模型空间 → 屏幕 | `BoneInfo` 调色板：`RealIndices` 指向 `TransformNameHashes`；`BoneMatrices` = `inverse(meshMatrix) * boneMatrix` 的逆关节矩阵 | `CanonicalBoneRebuilder.cs:74-89`、`:104-113`、`:183-209`（`:211-215` 注释写清 SDK 的 `origin = inverse(mesh)`） |

骨骼用**名字哈希**对齐，不用下标、不用槽位：`CanonicalBoneRebuilder.cs:77-86` 遍历来源用到的骨哈希，在**目标 unit 的** `TransformNameHashes` 里查；查不到抛 `MissingTargetBone` 硬失败（`:80-84`），不会静默错位。重写顶点同样按哈希走（`:271-287`）。

### 2.2 追加链路已经做了变换补偿

`CanonicalDecorationUnitAppender.cs:100-108` 对每个来源算 `SourceToTargetLocal`，交给 `CanonicalAppendMeshAssembler.TryAppendMany`；后者在 `:61-73` 真的把顶点过一遍矩阵（`TransformVertex` `:99-111`：`Type=0` 走 `Vector3.Transform`，`Type=1/2/3` 用法线矩阵）。**所以"把 LeftShoulder 的几何并进 Torso unit"，只要走这条路，视觉位置不动。**

### 2.3 什么情况下会动

只有一种：**绕过 `CanonicalAppendMeshAssembler`，直接把顶点缓冲区拷进目标 unit**。位移量正好等于两个 mesh `TransformInfo` 矩阵之差。除此之外没有第二种。

### 2.4 已有在役证据：跨部位族合并早就跑通了

`CanonicalCrossArmorOrchestrator.cs:472-553` 是"多来源 → 同一输出 unit"的生产分支（目标格子比来源少时触发），`host = sources[0]`，其余 append。`:502` 注释直接写明：**"按 LOD 序号取，不校验部位族——不同部位的 SemanticInfo 本来就不同。"** 这是发版行为，说明"一个 unit 里装多个部位的几何"引擎侧没有任何问题。

**结论**：位置由骨骼 + mesh `TransformInfo` 决定，槽位不参与。LeftShoulder 的几何并进 Torso unit 后视觉位置不变，前提两条：① 走 `CanonicalDecorationUnitAppender` / `CanonicalAppendMeshAssembler`；② 目标 unit 的 `TransformInfo` 含 donor 用到的骨哈希（`CanonicalTransformInfoExpander.Expand(..., includeAvatarSkeleton: true)`，见 `SameKeyCanonicalUnitRebuilder.cs:83-88`、`CanonicalDecorationUnitAppender.cs:72-75`）。

---

## 3 问题 2：unit 槽位到底影响什么

### 3.1 真依赖槽位（引擎按槽位找 unit）

| # | 行为 | 依据 |
|---|---|---|
| 1 | **寻址**：护甲件的视觉内容由四元组 `(itemId, body, slot, pieceType)` 唯一确定到一个 unit，少一维不唯一 | `ArmorStructure.cs:10-16`、`:139` |
| 2 | **槽位数上限**：每部位最多 3 个 unit 槽，`pieceType` 就是槽序号（0 内层 / 1 护甲 / 2 装饰） | `ArmorStructure.cs:54-62`、`:151-154` |
| 3 | **哪一层有几何**：某部位只有护甲层没有内衣层时，护甲层是该部位唯一几何（AF-52「禁锢」右臂实测） | `CliVpkRekey.cs:619-624`、`VpkRekeyPlanTests.cs:26-42` |
| 4 | **隐藏一个槽位的代价**：该 unit 的可见 LOD 全最小化，但 `lod=-1` 深度代理**保留** | `CliPatchPrimitives.cs:524-530`、`SameKeyCanonicalUnitRebuilder.cs:112-117` |
| 5 | **隐藏 unit 的 id 必须真实存在**：指向不存在的 id 会让引擎记住加载失败，军械库整页空白并崩溃 | `WardrobeHiddenUnitTemplate.cs:11-14` |

### 3.2 只是我们自己的分类习惯（引擎不读）

| 行为 | 为什么不算引擎语义 | 依据 |
|---|---|---|
| `UnitMeshPartKind` / `UnitMeshPartLayer` 枚举本身 | 自认"展示用枚举"，注释明说数值序列化进 SQLite、与游戏编号要靠显式映射对齐 | `UnitMeshPartContracts.cs:5-11,30-38`；`ArmorStructure.cs:51-52` |
| 承载层选择（`carrierLayerByKind`，外层优先） | 纯管理器策略：为绕开"外层护甲件的深度代理挡住内层几何"这条**几何**事实，不是引擎规则 | `CliVpkRekey.cs:631-641`、`:844-852` |
| `layer` 由文本启发式判定 | 从 mesh 名 / `CustomizationInfo` 字符串 grep 关键词 | `UnitMeshPartClassifier.cs:51-57` |
| `MeshInfoIndex` 当身份 | 注释明确"只是 unit 布局里的一个位置"，稳定锚是 `MeshId` + 语义 | `DecorationHostTargetBinder.cs:9-11,45-47` |
| `IsCullingBody` 判定 | 结构判定：该 mesh 的 section `MaterialSlotId` **都不在**材质表 → 无材质绑定 → 只写深度 | `UnitMeshReader.cs:883-886` |
| `AutoHideUnmappedTargetUnits` | 决定"未命中的目标 unit 写成空壳，还是干脆不写进 patch" | `CanonicalCrossArmorOrchestrator.cs:352-354` |
| 两份"层"排序方向相反 | VPK `Undergarment 0 < Armor 1 < Accessory 2`；常规线 `Armor 3 > Undergarment 2 > Accessory 1` | `CliVpkRekey.cs:844-852` vs `HelmetToArmorMergeService.cs:706-712` |

**关键区分**：引擎只按槽位**找到** unit；找到之后 unit 里的几何一律照画，不按槽位做空间过滤。"把护肩几何塞进 Torso unit"在引擎侧合法，代价只是它不再拥有护肩槽位（设计稿 §4.5 已承认这个取舍）。

---

## 4 问题 3：`lod = -1` 的 mesh 是什么

**它是 depth-only 遮挡壳 —— 不是深度预通道的可见几何，也不是纯 culling proxy。**

- **长什么样**：整条前臂 / 小腿的形状，只写深度、不出颜色。判定条件是它的 section `MaterialSlotId` 全都不在材质绑定表里（`UnitMeshReader.cs:883-886`），没材质就画不出颜色。
- **起什么作用**：**遮挡**。实测结论（2026-11-01 进游戏对照）：护甲件 `mesh0` 是 `lod=-1` 的深度代理，把我们的几何挂在更内层时会被它按深度剔除，表现成"手套 / 丝袜整段不显示、也不是品红"（`CliVpkRekey.cs:556-560`、`:626-630`、`:779`；`VpkRekeyPlanTests.cs:14-17`）。
- **能不能搬到别的 unit**：**不能，链路会拒绝**。① `CanonicalDecorationUnitAppender.cs:58` 拒绝 `targetRaw.LodIndex < 0` 当宿主；② `CanonicalMeshSkinningRouter.cs:44,49-64` 把 `LodIndex == -1` 一律当 proxy 并剥掉 `Type=6/7` 蒙皮分量；③ `LodIndex=-1` 没有 `BoneInfo`（`HasBoneData` 要求 `LodIndex >= 0`，`CanonicalMeshSkinningRouter.cs:90-91`），既不能当 donor 也不能当宿主。
- **能不能丢掉**：**不能**。① 遮挡语义会变（原本被它挡住的内层几何会突然露出来）；② `CliPatchPrimitives.cs:524-530` 注释写明必须保留，否则该 unit 失去合法的 `LOD -1 contract`。全仓唯一"连代理一起最小化"的开关是 `MinifyUnmappedCullingMeshes`（`SameKeyCanonicalUnitRebuilder.cs:13,112`），**没有任何调用点置 true**（`CanonicalHiddenUnitOutputCache.cs:17,25` 默认 false）。

---

## 5 问题 4：`layer`（层）关系

- **同槽内多层的排序语义 = 没有显式排序。** 三层是三个**独立 unit**、各自独立 draw call，谁盖谁完全由**深度缓冲 + 各自几何位置**决定；代码里不存在 render order / 排序键。
- **层真正决定的是"谁承载我们的几何"**：`CliVpkRekey.cs:631-641` 按 `(PartKind, Layer)` 选承载层，其余层隐藏。而"隐藏"保留了 `lod=-1` 深度代理 ⇒ **层选择是间接的遮挡决定**：几何放在内层，外层的深度代理会把它挡掉（`CliVpkRekey.cs:626-630` 实测结论）。
- **跨槽合并后原层序还有没有意义**：只剩**分类意义**，没有渲染意义 —— 合并后所有几何在同一个 unit、同一批 section 里，层号不再对应任何独立绘制单元。
- **该怎么重排**：不要重排，层号不进产物。要保留的是 ① donor 的 `pieceType`（写进 `sections.json` provenance，供组表定位，设计稿 §6）；② **承载槽必须选"该部位最外层"**（`CliVpkRekey.cs:624`），因为只有外层能躲开更内层残留的深度代理。

---

## 6 问题 5：隐藏 unit 的代价

**本项目的"隐藏"= 写一个空壳，不是"不写它"。** 三种做法在游戏里完全不同：

| 做法 | 机制 | 游戏里发生什么 |
|---|---|---|
| **写空壳**（现行） | 可见 LOD 全部最小化成 1e-4 退化三角形，`lod=-1` 代理原样保留 | 看不见，但 unit 结构、材质槽、stream ABI、`BoneInfo`、`TransformInfo` 全保留 → 不缺件、不 T-pose、不掉材质 |
| **不写它** | patch 里没有这个 unit 条目 | 引擎回落**原版** game data 的 unit → 原版件继续显示（"残留件"），正是 `AutoHideUnmappedTargetUnits` 要解决的问题（`CanonicalCrossArmorOrchestrator.cs:352-354`） |
| **指向不存在的 id** | 件记录指向不存在的 unit | 引擎记住加载失败 → **军械库整页空白并崩溃**（`WardrobeHiddenUnitTemplate.cs:11-14`） |

空壳形态：`CanonicalPlaceholderMinifier.cs:15,30-46` —— 3 个顶点（原点 + 1e-4 偏移），**只有第 0 段**留一个三角形，其余段 `numIndices = 0`；section 数与 `MaterialSlotId` 全保留。`patch hide-unit` 走同一套（`CliPatchPrimitives.cs:508,532-538`）。"确实隐藏了"的可执行谓词：`UnitGeometryFacts.IsFullyHidden` = 所有视觉 mesh 的 `Quality == Placeholder`（`UnitGeometryFacts.cs:31,50-52`；`Placeholder` 定义 `:57-58`：顶点 ≤ 3 或三角形 ≤ 1）。

---

## 7 如果"位置确实依赖槽位"—— 没有，但有 4 处方案要改

位置不依赖槽位。但查出 **4 处真问题**，其中 2 处会直接表现为"几何不见了"，必须写进方案：

1. **`CullingBounds` 必须重算（最高优先，且是现存缺陷）。** `MeshInfo` 里那 7 个 float（min xyz / max xyz / 半径）是引擎剔除用的盒子。`UnitMeshWriter.cs:46-56` 只把 model 里的值**原样写回**；`UnitAuthor.cs:275-282` 注释明说 **"`CanonicalUnitRebuilder` 不动这几个字段，所以不重算的话留下的是外壳的包围盒；顶点跑到包围盒外，游戏的剔除就会把网格丢掉"**，Python 契约检查断言 `vertices_outside == 0`。⇒ **现有 Canonical 追加链路（装饰 / 头盔合并 / 多源合并）全都没重算这个盒子。** 把护肩 / 披风这类外扩几何并进 Torso 很容易让顶点出盒 → 整块被剔除。方案要加：追加后按合并后的顶点重算 `CullingBounds`（照抄 `UnitAuthor.UpdateCullingBounds`）。
2. **隐藏 donor 槽位后它的 `lod=-1` 深度代理仍在原处，可能把搬过去的几何挡掉。** 这正是"手套 / 丝袜整段不显示"的同一条路径；`MinifyUnmappedCullingMeshes` 存在但无人开启。方案要加：隐藏壳提供"连深度代理一起最小化"选项，U0 里 A/B 对比两种隐藏壳。
3. **目标 unit 的 `TransformInfo` 必须覆盖 donor 用到的骨。** 缺骨是硬失败（`CanonicalBoneRebuilder.cs:80-84` 的 `MissingTargetBone`），不是静默错位。7 槽素体全是 `Undergarment/Any`、共用一副头像骨架，通常满足；仍要显式走 `CanonicalTransformInfoExpander`。
4. **追加只在同 LOD 内合法。** `CanonicalAppendMeshAssembler.cs:41-42` 的 `AppendLodMismatch`：donor 与宿主必须 LOD 序号一致，且要按 LOD 家族整体重建（`CanonicalDecorationUnitAppender.cs:35-37`：否则引擎会从"改过的近 LOD"切到"没改的远 LOD"）。设计稿 §3.1 没提这条，要补。

---

## 8 实机 A/B 方案

### 8.0 样本

**主样本 mod：`E:\mod\Cow_QiPao_Milltina_Bill`**（7 unit，48 节，单 unit 最多 12 节，全 `Undergarment/Any`）。理由：① 整套替换型、7 个标准槽占满 —— 正是方案目标形态（设计稿 §4.5）；② 单 unit 节数余量大（12），把 §3.2 的节数变量排除掉；③ 有袖旗袍，LeftArm 与 Torso 在肩部相接，位置偏差 1 cm 就能在截图上看出；④ 已有 `Overwrite/` 变体，回滚只改一个目录。

**搬哪一块**：`LeftArm` unit（slot 6）的**可见 LOD 家族全部** → `Torso` unit（slot 2）。搬 LeftArm 而非护肩，是因为 `E:\mod` 里 16 个 mod 全是 7 槽素体，**没有任何一个提供 slot 8/9 的 unit**；护肩那层要等第四步用原版甲补。位置问题与"从哪个槽搬"无关（§2），这一步足够判定。

### 8.1 第一步（离线，决定性判据 —— 先做，能省掉进游戏）

需要**临时工具**（约 60 行，照 `HD2RuntimeArmorOverride\tools\VanillaHideExperiment\Program.cs` 骨架）：读 mod 的 `9ba626afa44a3aa3.patch_0` → `PatchUnitMeshReader` 取 LeftArm 与 Torso → `CanonicalDecorationUnitAppender.TryAppendLodFamily(torsoUnit, inputsByLod, avatarTransforms)` → **按合并后顶点重算 `CullingBounds`**（§7.1）→ `CanonicalPatchSession` 写两个条目（Torso = 合并结果，LeftArm = `HiddenUnitGenerator` 空壳）→ `CanonicalPatchWriter` 输出到 `E:\tmp\investigate\sectional-u0\payload\`。

为什么必须写工具：`patch compose` 只做**条目级**合成（`CliProjections.cs:126`）；`hd2mod unit show/geometry/bones` 只读（`CliApplication.cs:1331-1353`）；`patch hide-unit` 只会**隐藏**不会**搬**（`CliProjections.cs:128`）。仓库里"把 A 的几何并进 B"只有 `CanonicalDecorationUnitAppender` 一条实现，没有任何 CLI 入口。

**判据（可证伪，无需进游戏）**：

```powershell
$env:HD2_CANONICAL_POSITION_DIAGNOSTICS = '1'   # CanonicalPositionDiagnostics.cs:9
# 跑工具，然后读 logs/canonical-position-diagnostics.log
```

- **位置没变**：日志里 donor LeftArm 的 `position=x[..] y[..] z[..]` 包围盒（`CanonicalPositionDiagnostics.cs:47-58`）与追加后 Torso mesh 里对应那批顶点的包围盒逐轴 `|Δ| ≤ 1e-4`（float32 精度内相等）→ 位置不依赖槽位，成立。
- **变了**：任一轴 `|Δ| > 1e-3` → 位置依赖 mesh 变换，方案必须强制走追加链路。
- **顺带必查**：`hd2mod unit geometry <新patch_0> --keys <torso key> --json`，确认 `TriangleCount` 增加量 ≈ donor 三角形数，且没有 `Placeholder`。

### 8.2 第二步（实机，判定遮挡）

同一件护甲、同一机位拍三张（进游戏后不要动鼠标视角）：

| 张 | 机位 | 看什么 |
|---|---|---|
| A0 | 军械库预览，正面，默认距离 | 基线：两臂完整、袖口贴合 |
| A1 | 侧面 90°（左臂朝镜头） | 肩部接缝、袖根有没有裂缝 |
| A2 | 跑动中（左臂抬起） | 蒙皮有没有被拉断 |

B 组同机位再拍三张（A0' / A1' / A2'）。**判据**：

- **位置没变**：A1' 与 A1 的左臂轮廓 **IoU ≥ 0.98**，轮廓质心偏移 **≤ 2 px**（1080p）。
- **变了**：质心偏移 **> 10 px**，或左臂**整段消失**（那是 §7.2 的深度代理），或袖根出现 **> 3 px 的裂缝 / 穿插**。
- **必须单独记录的失败态**：整块几何消失但军械库不崩 —— 这是 `CullingBounds` 没重算（§7.1）；军械库整页空白 —— 这是空壳 unit id 不存在（§6）。

### 8.3 第三步（对照，验证 §7.2）

同一个 B payload 做两个变体，各拍 A1 一张：**B1** LeftArm 空壳 = 现行 `patch hide-unit`（**保留** `lod=-1` 代理）；**B2** 空壳 = 可见 LOD + `lod=-1` 代理**一起**最小化（给工具加开关，即 `MinifyUnmappedCullingMeshes = true`）。判据：B1 被挡、B2 正常 ⇒ §7.2 成立，方案必须默认 B2；两者都正常 ⇒ 跨槽场景下代理不构成遮挡，§7.2 降级为可选。

### 8.4 第四步（补 slot 8/9，非 7 槽专项）

`E:\mod` 没有护肩 unit，必须用原版甲。做法同 §8.1，但 donor 从 game data 读：`GameDataUnitMeshReader.ReadAsync(archive, key)` 取 slot 8，host 取同一 archive 的 slot 2。挑甲命令（需索引库）：`hd2mod gamedata equipment list --kind armor --json`，过滤 pieces 含 slot 8 的 kit（`ArmorStructure.cs:44` 实测：138 套身体甲里 125 套有双肩）。判据同 §8.1 / §8.2，只多一条：合并后 Torso 的 `CullingBounds` 必须覆盖护肩顶点（§7.1 `vertices_outside == 0`）。

### 8.5 硬约束（已遵守）

未 build、未启动 / 关闭 `HD2ModManager.exe`、未改任何代码、`E:\mod` 与游戏目录只读。`E:\mod\.hd2modmanager` 下**没有** sqlite 索引库，所以 §8.4 的挑甲命令本次未执行。
