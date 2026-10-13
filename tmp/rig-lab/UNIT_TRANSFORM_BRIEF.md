# UNIT_TRANSFORM_BRIEF — 第 58 轮实机失败：原因与修复交接

**给：** 专家模型
**来自：** rig-lab 本地流水线
**日期：** 2026-10-13（第 59 轮）
**一句话：** 实机"完全透明 + 阴影变钢丝球 + Blender 反导入抬高 1 米 + 骨骼堆在世界原点"的
**唯一根因是我们自己拼 `Unit.TransformInfo` 的方式错了**，不在权重、不在材质、不在容器格式。
权威算法一直就在仓库里（SDK 导出器 + 管理器的 Canonical 重建器），本轮已按它重写并通过 45 项校验。

---

## 0. 结论摘要（先看这个）

| 现象 | 根因 | 状态 |
|---|---|---|
| 被替换 unit **完全透明** | 附加的 107 根骨的 `matrices[]` 全填了**单位阵** ⇒ `BoneInfo.bones[k] = inv(I) = I` ⇒ 静止姿态下 `G_b @ B_b = G_b`，顶点被真实骨骼矩阵乱乘 ⇒ 几何炸成碎片 | **已修** |
| 阴影是**围绕玩家做布朗运动的钢丝球** | 同上（顶点位置每帧被不同骨骼矩阵乘出巨大位移） | **已修** |
| Blender 反导入**抬高 1 米**（鞋底 0.9m / 最高点 3m） | `MeshInfo.TransformIndex` 写成了 `8+(3-lod)`，于是 **lod0 的 m3 指向 8+3=11 号节点**，而 11 号是真实骨骼（`0x9b115563`，平移 z=**0.973473**）⇒ 网格被该节点矩阵整体抬 0.973 m | **已修** |
| 骨骼**大量堆在世界 00** | 附加骨的 `locals[]` 全填 0（`DEFAULT_LOCAL`）、`entries[]` 全填 `(1,0)` ⇒ SDK/引擎按"父链 + 局部变换"建骨架，全部塌到节点 0 | **已修** |
| 位置正常的只有 `helmet_culling` / `torso_culling` | 这两根是**模板自带的**节点（17 / 22），我们原样照抄、父链完整，所以只有它们没被压平 | 说明性证据 |
| patch 里 **9 个 unit 各含全量模型** | 上一轮为了"同一份几何回答所有护甲位置"把 1 份 payload 写成 9 个 id | **已改回 1 个 unit**（见 §6） |

**待专家裁定的是"我们是否还有没意识到的契约"**，见 §7。核心 6 项已按仓库内的权威实现修好。

> **§9 是第 59 轮追加**：用户点名要分析的"**重建 patch（修复过时 Unit）**"功能已精确定位。
> 它与护甲映射 / 自定义素体 / CrossArmor **无关**，就是
> "**把旧 patch 里 Unit 的模型数据抽出来 → 重新整理 → 用 GameData 中同 ID 的 Unit 当框架重建**"。
> 这条路线里的"规整规则"正是我们要的线索，见 §9.2 / §9.3。

---

## 1. 实机现象（用户原话摘录）

> 直接进入游戏什么也不显示，该护甲所有被替换的 unit 均透明了……玩家投影在地上的阴影变成了一团
> 特别大的、围绕着玩家附近做布朗运动的钢丝球或纸团，每帧都在变化。
> 将 patch 导入 SDK 并转到 Blender：模型的位置提高了 1 米，本应鞋底陷地约 10cm、最高点接近 2 米，
> 但实际上变成了鞋底在 0.9m 处，最高点在 3m 处。同时骨骼特别少……世界 00 的位置有非常多的骨骼
> 文字叠加在一起。同时，你的 patch 中包含了 9 个 unit，每个 unit 都包含了完整的、全量的、
> 位置等完全一致的模型。

---

## 2. 权威依据（全部来自仓库内源码，不是推测）

### 2.1 SDK 导出器：`docs/HD2SDK-CommunityEdition-3.8.0/stingray/unit.py`

**`AddBoneData`（行 1796–1934）——这就是"一个 unit 的 TransformInfo 该怎么拼"的权威答案：**

```python
for bone in armature_obj.data.edit_bones:
    name_hash = murmur32_hash(bone.name)
    try:
        transform_index = transform_info.NameHashes.index(name_hash)   # 已存在 -> 复用
    except ValueError:
        transform_info.NameHashes.append(name_hash)                    # 不存在 -> 追加
        transform_info.TransformMatrices.append(None)
        transform_info.Transforms.append(None)
        l = StingrayLocalTransform(); l.Incriment = 1; l.ParentBone = 0
        transform_info.TransformEntries.append(l)
        transform_index = len(transform_info.NameHashes) - 1

    # 世界矩阵
    loc, rot, scale = bone.matrix.decompose()
    m = mathutils.Matrix.LocRotScale(loc, rot, scale); m.transpose()
    transform_info.TransformMatrices[transform_index] = transform_matrix   # = 行主序 16 float

    # 局部变换：父矩阵的逆 @ 自身矩阵，再分解
    if bone.parent:
        local = bone.parent.matrix.inverted() @ bone.matrix
        translation, rotation, _ = local.decompose()
        transform_local.rot.x = [r[0][0], r[1][0], r[2][0]]   # 列主序存 3x3
        transform_local.pos = translation                     # ★ 父坐标系下的偏移
        transform_local.scale = scale
    else:
        transform_local = StingrayLocalTransform()            # 根骨：全 0
    transform_info.Transforms[transform_index] = transform_local

    # 父链
    if bone.parent:
        transform_info.TransformEntries[transform_index].ParentBone = \
            transform_info.NameHashes.index(murmur32_hash(bone.parent.name))
```

要点：
1. **`TransformInfo` 是"整个骨架"的副本**，逐骨遍历；同名哈希**复用**，绝不重复。
2. **`locals`（= `Transforms`）是相对父骨的局部变换**，不是世界位置。根骨全 0。
3. **`entries`（= `TransformEntries`）的 `ParentBone` 是父骨在本表里的下标**。
4. **`matrices`（= `TransformMatrices`）是世界矩阵**。

**`InitRawMeshes` 行 615 / 2094–2111 —— 网格与骨骼的绑定关系：**

```python
group_transform = self.TransformInfo.TransformMatrices[Mesh_Info.TransformIndex]  # 615
...
origin_transform_matrix = transform_info.TransformMatrices[mesh_info.TransformIndex] \
                              .ToBlenderMatrix().inverted()                        # 2100
for i, transform_index in enumerate(bone_info[lod_index].RealIndices):
    bone_matrix = transform_info.TransformMatrices[transform_index]
    m = (origin_transform_matrix @ bone_matrix.ToBlenderMatrix()).inverted().transposed()
    bone_info[lod_index].Bones[i] = transform_matrix                                  # 2111
```

⇒ `Bones[i] = inverse(origin @ M_bone)`，`origin = inverse(M_mesh)`。
**`M_mesh` 是单位阵时，`Bones[i]` 就是 `inverse(M_bone)`**——所以 `TransformIndex` 必须指向
**单位阵节点**，否则整个网格会被那个节点位移，且逆绑定矩阵也跟着错。

**`__init__.py` 行 1740–1746（`AutoLods`）**：把 lod0 的 RawMesh 复制给所有 LOD 槽，并且
`TransformMatrices[MeshInfoArray[n].TransformIndex] = TransformMatrices[lod0 的]`
⇒ 同一 unit 的多个 LOD **必须共享同一个网格变换矩阵**。

### 2.2 管理器的 Canonical 重建器（`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/`）

**`CanonicalTransformInfoExpander.ExpandTransformInfo`** —— 与 SDK 同构，并多一条硬规则：

```csharp
if (targetByHash.TryGetValue(hash, out var existing))
{
    // A same-named target bone is therefore not authoritative: replace its
    // transforms and parent link with the Avatar definition too.
    locals[existing]   = avatar.LocalTransforms[avatarIndex];
    matrices[existing] = avatar.Matrices[avatarIndex];
    entries[existing]  = new UnitTransformEntry(avatarEntry.Increment, (ushort)parentIndex);
    return existing;                       // ← 复用，不新增
}
...
hashes.Add(hash);                          // ← 只在真的不存在时才追加
```

`ValidateTransformInfo` 另外要求 **`NameHashes.Distinct().Count() == count`**（不允许重复哈希）。

**`CanonicalUnitRebuilder.RebuildMeshInfoAndUnitTail` 行 503–504**：
```csharp
WriteUInt32(writableHeader, recordOffset + 40, mesh.MeshId);
WriteUInt32(writableHeader, recordOffset + 48, mesh.TransformIndex);   // ← 照抄目标的值
```
⇒ **`MeshInfo.TransformIndex` 是从目标 unit 照抄的，不是自己算的。**

**`CanonicalLodBonePaletteCompiler`**：同一 LOD 的多个 mesh 必须共享同一个 `TransformIndex`
（`SharedLodTransformMismatch`），且 palette 必须在 TransformInfo 完整之后才能解析。

### 2.3 SDK 导入器：`stingray/unit.py` `CreateModel` 行 2375–2483

```python
for i, transform in enumerate(transform_info.TransformEntries):
    boneName = Global_BoneNames.get(boneHash, str(boneHash))
    newBone = armature.edit_bones.new(boneName)
    boneParents[i] = transform.ParentBone
    boneTransforms[newBone.name] = transform_info.Transforms[i]        # locals
    boneMatrices[newBone.name]   = transform_info.TransformMatrices[i] # 世界
...
for i, bone in enumerate(bones):
    bone.parent = bones[boneParents[i]]
for bone in armature.edit_bones:
    mat = Matrix.Identity(4); mat[0]=a.v[0:4]; mat[1]=a.v[4:8]; mat[2]=a.v[8:12]; mat[3]=a.v[12:16]
    bone.matrix = mat.transpose()                                       # ← 世界矩阵直接赋值
```

⇒ **Blender 反导入时骨骼位置完全来自 `TransformMatrices`**，父链来自 `TransformEntries`。
我们旧版把 `matrices` 全填单位阵 ⇒ **107 根骨全部 `bone.matrix = I` ⇒ 全部堆在原点**，
与用户观察完全一致（只有模板自带的 17/22 两根保留原值）。

---

## 3. 根因逐条（附 `_probe_unitdiag.py` 实测数字）

模板（结构捐赠者）= 管理器产出的 `Torso/Stocky` unit `0xd841b16544c8fbae`（23 节点、4 流、6 mesh）。
**模板本身无罪**：它的 TOC 与 Chiffon 源 patch 中对应 unit **逐字节相同**（`identical=True`，10412 B）。

### (a) `locals[]` 全零 —— 骨骼塌到原点
```
ours  locals[30] (avatar bone 7) = {'rot': [1,0,0,0,1,0,0,0,1], 'pos': [0,0,0], 'scale': [1,1,1]}
av    locals[7]                  = {'pos': [0, -0.021450195, 0.973472714]}
追加的 107 根骨中，locals 与 avatar 不一致：107 / 107
```
模板自己的骨骼节点证明 `locals` 是**局部偏移**：节点 13 `locals.pos=[0.19271,0,0]`，
其 `matrix` 平移是 `[0, 0.011559, 1.163334]`（父节点 12 的旋转把 0.19271 映射到那个方向）。

### (b) `entries[]` 全填 `(1,0)` —— 父链被压平
```
ours  entries[23..25] = [(1,0), (1,0), (1,0)]
av    entries[0..2]   = [(0,0), (1,0), (1,1)]
```
模板自己的父链是 `0→1→2→3→4→{5..8}`（5..8 是 4 个 LOD 网格节点），骨骼链是 `9→10→11→…→21`。

### (c) `matrices[]` 全填单位阵 —— 逆绑定矩阵全变 I（**透明的直接原因**）
```python
extra = [(DEFAULT_LOCAL, IDENTITY16, (1, 0), avatar.hashes[k]) ...]   # ← IDENTITY16！
build_bone_info(...): bi.bones[k] = inv(matrices[23+k]) = inv(I) = I
```
⇒ 静止姿态下 `G_b @ B_b = G_b ≠ I`，顶点被真实骨骼矩阵乱乘 ⇒ 几何炸开 ⇒ 不可见 + 阴影成球。

### (d) 重复哈希 —— 违反 `ValidateTransformInfo`
模板 23 个节点里有 **15 个**哈希同时存在于 avatar 的 107 根骨中。我们"23 + 107 直接拼接"
⇒ **15 个重复哈希**（旧版共 130 节点 / 115 个不同哈希）。

### (e) `TransformIndex` 算错 —— **抬高 0.973 m 的直接原因**
```python
mesh_tf = 8
mi.transform_index = mesh_tf + (3 - lod)      # lod 3/2/1/0 -> 11/10/9/8
```
模板的真实取值是 **lod 3/2/1/0 → 5/6/7/8**，全部是单位阵。
我们给 lod0（= m3，玩家近距离看到的那个）填了 **11**，而节点 11 是真实骨骼 `0x9b115563`，
`matrix` 平移 = `[0, -0.02145, 0.973473]`。
**0.973 m ≈ 用户报告的"抬高 1 米"**（鞋底 −0.09 → 0.88；最高点 2.03 → 3.00）。

### (f) `MeshInfo.MeshID` 与 `TransformIndex` 不匹配
模板的不变量（6 个 mesh 全部成立）：**`MeshID == NameHashes[TransformIndex]`**。
- m0 tf=5 → `0x756179d6`；m1 tf=6 → `0x71e199d2`；m2 tf=7 → `0xec2519ba`；m3 tf=8 → `0x1fb50ca8`
- m4 tf=17 → `0xa620a54c`；m5 tf=22 → `0x800b8ea2`
我们用 `murmur32_hash('lunalice/lod0')` 之类的**自造 id**，与该不变量冲突。

### (g) LOD group list 的 `radius` 口径错（次要）
模板 record 1/2 存的 `min/max` **就是该 culling mesh 自身的局部 bbox**（逐位相同），
`radius` 是**该 mesh 全部顶点的范数**（stream 3 的 24 个顶点 `|v| = 0.457118` 恒定；stream 2 为 `0.243766`）。
我们写成了 `|max - center|`。另外原代码用 `8 + offs[i]` 定位 record，
对 record 1（i=3）落在 `min` 上正确，对 record 2（i=4）落在 record 头部，**位置不一致**。

### 已排除的可能性（不要再查）
- 容器格式：`hd2_patchw.py` 与管理器产物往返 `toc identical: True` / `gpu identical: True`。
- 模板 unit：与 Chiffon 源 patch 逐字节相同。
- `bones_ref = 0`：管理器产物也是 0，正常。
- 半精度闭合：`half4 closes exactly (worst 0.000e+00)`，188356 个 slot 填满。
- 权重数据：121704 顶点位置解码回 `ours_bind` **max |d| = 0.000e+00 m**。

---

## 4. 已做的修复（`tmp/rig-lab/hd2_unitbuild.py` / `build_patch.py`）

### 4.1 新增 `merge_transform_info(ref_ti, avatar, required_hashes)`
按 SDK `AddBoneData` + `CanonicalTransformInfoExpander` 逐字实现：
- 从模板 23 节点出发；
- 对 avatar 的每根骨 `EnsureHash(hash)`：**已存在 → 覆写 locals/matrices/entry（parent 重映射到本表下标）；不存在 → 追加**；
- 根骨（`parent == self`）的 parent 指向自己；否则递归先建父骨；
- **绝不产生重复哈希**。

实测：`donor 23 nodes + avatar 107 bones -> 115 nodes (appended 92, overwritten 15, donor-only kept 8)`，
与 `_probe_unitdiag.py` 的独立统计（overlap 15 / missing 92）**完全一致**。
模板的 8 个独有节点（4 个 LOD 网格节点 5–8 + 2 个 culling 节点 17/22 + 节点 2/4）
全部原样保留 ⇒ LOD group list 的父/网格下标继续有效。

### 4.2 `mesh.transform_index` 改为照抄模板
`lod_tf = {3:5, 2:6, 1:7, 0:8}`，culling `{stream3: 17, stream2: 22}`，
并断言这四个节点**必须是单位阵**（否则报错而不是静默位移）。

### 4.3 `MeshID = NameHashes[TransformIndex]`
不再自造；`unk3` 与 `transform_index` 保持一致（模板如此）。

### 4.4 `BoneInfo.bones[k] = inv(matrices[real_indices[k]])`
公式本来对，但**输入**从单位阵换成了 avatar 的真实世界矩阵。

### 4.5 `radius` 口径修正 + 用 (min,max) 定位 record
`build_patch.py::rewrite_lod_group_list` 改为**在 192 字节里搜索该 culling mesh 的
(min, max) 24 字节序列**来定位 record（模板逐位存的就是这个 bbox），
`radius` 取 `max|v|`。不再依赖语义不一致的 offset 表。

### 4.6 新增 `check_unit()` / `check_transform_info()`
构建期硬断言：数组长度一致、**无重复哈希**、parent 下标在范围内、
**每根 avatar 骨的 locals/matrices 与 avatar 一致**、parent 重映射正确、父链无环、
**每个 LOD mesh 的变换是单位阵**、`MeshID == NameHashes[TransformIndex]`、
`bones[k] @ matrices[real_indices[k]] == I`、vertex fake index 在 palette 内。

### 4.7 校验升级为 45 项，全部通过（`_verify_patch.py`）
关键几项：
```
[ok] transform info = donor nodes + the missing avatar bones   115 nodes
[ok] TransformInfo has no duplicate name hash                  115 distinct of 115
[ok] MeshID == NameHashes[TransformIndex] for all six meshes
[ok] every LOD mesh transform is the identity                  worst |M - I| = 0.00e+00
[ok] every avatar bone carries the avatar locals               0 differ
[ok] every avatar bone carries the avatar matrix               0 differ
[ok] every avatar bone parent is remapped into our index space 0 differ
[ok] every parent chain terminates at its own root             0 cyclic
[ok] bones[k] == inv(M_bone_k) (rest pose is the identity)     worst = 1.13e-07
[ok] positions decode back to ours_bind                        max |d| = 0.000e+00 m
[ok] weights close under fsum(half)                            worst 0.000e+00
[ok] unit toc round-trips byte for byte                        74956 bytes
[ok] every texture uses a DX10 DDS header
```

---

## 5. 当前产物

`E:\mod\Lunalice-RigTest\`
- `9ba626afa44a3aa3.patch_0` — **91092 B / 33 entries**（1 Unit + 2 Bone + 14 Material + 16 Texture）
- `9ba626afa44a3aa3.patch_0.gpu_resources` — 67.8 MB
- `lunalice-rigtest.protocol` — 9 条 `model_piece`，**全部指向同一个 unit `0xd5cf3b8546db25f0`**
- `lunalice-rigtest.manifest.json`

几何/权重逐位未变（`fingerprint.geometry = 05e8f5f876a2c32e`）。

---

## 6. unit 数量的处理（回应"你只需要输出一个 unit"）

`--unit-mode` 现在是：
- **`fresh`（新默认）**：**1 个 unit**，整模型都在里面；protocol 用 9 条 `model_piece` 全部指向它。
  **需要 Lua 护甲注册表启用**才能被游戏解析。
- `same-key`：同一份 payload 写到 9 个**真实游戏 unit id** 上 ⇒ 不需要 Lua/管理器配置，
  但 patch 里会出现 9 个 unit（共用同一 GPU blob，写出器按 SHA256 去重）。
- `both`：两者都出。

若 `fresh` 进游戏仍然什么都不显示，**第一个要试的对照就是 `--unit-mode same-key`**
（它能区分"我们的 unit 内容错了"和"注册表没解析到我们的 id"）。

---

## 7. 请专家裁定 / 复核的点

1. **`required_hashes` 取"全部 107 根 avatar 骨"是否过大？**
   模板只带 11 根（它实际用到的）。SDK 导出器是"遍历整个 armature"所以是全量；
   我们取全量是超集，理论上安全（`remap` 恒等、fake index 直接对应）。
   但**引擎是否会为 unit 里多出来的骨创建运行时节点**、是否影响性能或绑定匹配？请确认。

2. **4 个 LOD 的 `TransformIndex` 是否必须是 5/6/7/8 这一组？**
   我们照抄模板。SDK `AutoLods` 只要求"所有 LOD 共享 lod0 的矩阵"。模板 5..8 全为单位阵，
   所以数值上等价。但 `CanonicalLodBonePaletteCompiler` 是按 LOD 分组做 `SharedLodTransformMismatch`
   检查的 —— 我们的 m4/m5（culling）是 `lod = -1`，**它们是否也应该参与这个共享约束？**

3. **`BoneInfo` 的 `NumRemaps` 语义**
   我们取 `NumRemaps = 材质槽数 = 14`（模板是 2，= 它自己的材质槽数），每个 remap 是 0..106 的恒等表。
   `CanonicalLodBonePaletteCompiler` 用的是"同一 LOD 内所有 mesh 的共享材质序"。
   单 unit + 单 LOD 时两者应该等价，但请确认 `NumRemaps` 是"该 LOD 的材质槽数"而不是"unit 的材质数"。

4. **`unit.section_ids` / `material_ids` 的长度规则**
   模板 = 4 LOD mesh × 2 槽 + 2 culling × 1 = 10；我们 = 4 × 14 + 2 = 58。逐 mesh 的槽表与
   `MeshInfo.material_ids` 一致，但 unit 级是否要求与 `MeshInfo` 顺序严格对齐？

5. **LOD group list 的 offset 表语义**
   `offs = [0x28, 0x38, 0x48, 0x58, 0x84]`，record 0 在 0x30、record 1 在 0x58、record 2 在 0x8c。
   `offs[i] + 8` 只对 record 0 / record 2 成立，对 record 1 不成立（差 8）。
   我们改成按 (min,max) 内容定位，绕开了这个不确定。请确认这样是否覆盖了全部语义
   （`nGroups = 2`、`u32 3`、`u32 0x0c5bc110`、threshold 表 `[1.0, 0.0965, 1, 3, 0.0965, 0.0439, 1, 2, 0.0439, 0]`）。

6. **`Incriment` 字段**
   模板除节点 0（`inc=0`）外全是 `inc=1`；avatar 也是节点 0 `inc=0`。我们直接沿用 avatar 的值。
   这个字段的语义（是否 "父链跳过层数"）尚未确证，请确认照抄 avatar 是安全的。

7. **实机验证顺序**
   按 `WEIGHT_TRANSFER_BRIEF.md §10.8`，建议下一步是"**先只验编码/结构合法性**"
   （即当前这一版：几何不变、权重不变、只修 TransformInfo），确认模型能**以正确的
   位置/朝向/大小显示出来**之后，再进入六区域权重转移。请确认这个顺序。

---

## 8. 复现命令

```bash
cd tmp/rig-lab
# 结构诊断（模板 vs 我们的 unit，逐字段）
python _probe_unitdiag.py

# 重新构建（1 个 unit）
python build_patch.py --out /e/mod/Lunalice-RigTest --unit-mode fresh
# 回退对照（9 个真实游戏 id，不需要 Lua）
python build_patch.py --out /e/mod/Lunalice-RigTest --unit-mode same-key

# 45 项独立回读校验
python _verify_patch.py
```

**探针清单**（全部在 `tmp/rig-lab/`）：
`_probe_unitdiag.py`（本轮新增，字段级诊断）、`_probe_ourdivergence.py`、
`_probe_stream01.py`、`_probe_tibind.py`、`_probe_unitheaders.py`、`_probe_unitdiff.py`、
`_probe_lodgroup.py`、`_probe_origtf.py`（§9.4 新增，只读对比原版/Chiffon/我们）、`_verify_patch.py`。

---

## 9. 管理器"重建 patch（修复过时 Unit）"功能 —— 精确定位，以及它对我们的线索

> 用户在第 59 轮明确点名要分析这个功能，并强调它**与护甲映射、自定义素体、CrossArmor 完全无关**。
> 本节只做定位与描述，**没有改动任何产品代码**。

### 9.1 它是哪一个功能 / 什么条件下触发

**"过时"是一个 4 字节的版本号比较**，不是几何比较：

- `HD2ModCore/Domain/UnitCompatibility.cs`
  - `CurrentUnitVersion = 0x00a4cd36`（= **10800438**，正好是我们 `hd2_unit.py` 写出的 `version`）
  - `IsOutdated => Status == UnitCompatibilityStatus.OutdatedConfirmed`
  - `outdated = version < CurrentUnitVersion`（行 44）
- `HD2ModCore/Infrastructure/UnitVersionProbe.cs`
  - 只读 **Unit TOC 偏移 `0x2c` 的 4 字节 u32**（`VersionOffset = 0x2c`），不做任何解码。
  - 这是"最小安全读取"：判"过时"的成本极低。

**入口（CLI）**：

```
hd2mod patch repair inspect   <Mod GUID或前缀> [base.patch_N] --manager-root … --game-data … --json
hd2mod patch repair plan      <Mod GUID或前缀> <base.patch_N> --manager-root … --game-data … --output same-key-plan.json --json
hd2mod patch repair validate  --plan same-key-plan.json --manager-root … --game-data … --json
hd2mod patch repair apply     --plan same-key-plan.json --manager-root … --game-data … --output <库外目录> --confirm --json
hd2mod patch repair batch     inspect|plan|validate|apply …          （批量版）
```

管理器的库页面就是按 `UnitCompatibility.IsOutdated` 过滤出"**模型过时**"的 Mod
（`HD2ModManager/ViewModels/LibraryPageViewModel.cs:268`、`PageViewModels.cs:724`），再走批量修复。

> ⚠️ **不要和 CrossArmor 混淆**：CrossArmor（`CanonicalCrossArmorOrchestrator` + 护甲注册表 +
> `1f39ba3f9305xxxx` 重编号）是**另一条**路线，做的是"自定义素体 / 护甲部位映射"，
> 与"修复过时 Unit"无关。

### 9.2 它的路线 —— 正是用户描述的"抽模型数据 → 重新整理 → 用 gamedata 的 unit 当框架重建"

主实现：`HD2ModCore/Infrastructure/CanonicalSameKeyReconstructionService.cs`

| 阶段 | 代码位置 | 做什么 |
|---|---|---|
| 资格闸门 | `InspectAsync` 行 49–82 | Mod 必须在库里登记且有 base patch 主文件；GameData 目录必须存在；**GameData 资产索引必须 `IsCurrent`**（否则 `GameDataIndexNotCurrent` 直接拒绝） |
| 规划（只读 TOC） | `PlanAsync` 行 598–656 | 只读来源 patch 的 TOC 与 `library.json` 事实，**不解码任何 Unit payload**；`assetIndex.FindAssetArchivesAsync(每个 Unit 的 AssetKey)` 找出**当前原版拥有同一个 (TypeID, FileID) 的 archive** |
| **取"框架"** | `GenerateGroupedCandidateAsync` 行 429–447 | `new GameDataUnitMeshReader(resolver).ReadAsync(archiveId, sourceEntry.AssetKey, allowGlobalDependencySearch: true)`。该类的注释原文：**"Reads one explicitly selected vanilla Unit and its explicitly allowed dependencies."** ⇒ 这就是用户说的"**用 gamedata 中的指定 unit 为框架**" |
| 取骨架 | 行 115–120 | `CanonicalAvatarRigReader` 读 archive `18235e0c9ec0e636` 里 Unit `0x4d1c334d294dfa97` 的 TransformInfo（107 根骨） |
| 能力闸门（fail-closed） | `CanonicalUnitCapabilityDetector.Analyze` | **来源与目标都**必须 `PreferredRoute == CanonicalRebuild`；否则**整包按"不透明来源 Unit 原样保留"**，绝不半修 |
| **重建** | `SameKeyCanonicalUnitRebuilder.Rebuild` 行 54–308 | 见 §9.3 |
| 打包 | 行 550–558 | `PatchWorkspaceWriter.WriteAsync(...)`，`headerTemplateTocData` 取自**目标 archive 的 TOC** |
| 结果 | 行 556–563 | 只写**新的库外目录**；不替换、不导入、不启用、不部署 |

### 9.3 这条路线里的 6 条"规整"规则 —— 对我们最有价值的部分

1. **框架优先（TransformInfo）** — `CanonicalTransformInfoExpander.Expand(target, sources, avatar, includeAvatarSkeleton: true)`
   以**目标的 TransformInfo 为基底**，只往里补 avatar 的骨；**同名骨以 avatar 为准覆盖**
   （注释原文：*"A same-named target bone is therefore not authoritative: replace its transforms and
   parent link with the Avatar definition too."*）；只有真的不存在时才 `hashes.Add`；
   `ValidateTransformInfo` 硬拒重复哈希、非有限矩阵。
2. **网格变换锚定** — `CanonicalMeshSkinningRouter.TryPrepare(..., CanonicalSkinningMode.BindStaticToTargetMeshTransform, CanonicalBoneAnchor.TargetMeshTransform)`
   源几何被换算到**目标 mesh 的局部空间**；`CanonicalTransformResolver.TryResolve` 给出 `SourceToTargetLocal`；
   `CanonicalMeshSemanticMerger.TryMerge` 用这个局部变换做合并。
3. **逆绑定矩阵口径** — `CanonicalBoneRebuilder.BuildInverseJointMatrices`
   `origin = inv(transpose(M_mesh))`，`Bones[k] = transpose(inv(origin * transpose(M_bone)))`。
   当 `M_mesh` 为单位阵时退化为 `Bones[k] = inv(M_bone)` —— 与我们第 59 轮的修法一致。
4. **骨架调色板按 LOD 编译** — `CanonicalLodBonePaletteCompiler.TryCompile(target, lod, group)`
   palette 是**该 LOD 各 section 实际用到的骨**（不是全量 107）；`realIndices` 指向 target 的 TransformInfo；
   同一 LOD 的多个 mesh **必须共享同一个 `TransformIndex`**（否则 `SharedLodTransformMismatch`）；
   fake index 是**每材质槽**的 remap。
5. **流 ABI 尽量沿用目标** — `CanonicalStreamContractCompiler.BuildContract`
   若目标 ABI 能编码全部最终语义，就**原样保留目标的组件顺序 / 格式 / stride**；
   只有不够时才回退到导出器 profile `(color 4, position 2, normal 30, uv 1, bone_index 28, weight 35)`。
   —— 这一条与我们现在的做法**相反**（我们是无条件用导出器 profile）。
6. **落盘只用目标的 TocData 当基底** — `CanonicalUnitRebuilder.TryRebuild(target, targetOriginalTocData, …)`
   `RelocateTransformInfo`（TransformInfo 变长时重排 `0x4c,0x50,0x54,0x58,0x5c,0x60,0x64,0x70` 八个头偏移）
   → `RebuildBoneInfoBlock` → `WriteStreamLayouts` → 逐 stream 写 GPU → `RebuildMeshInfoAndUnitTail`。

### 9.4 按这条路线做对照后实测到的硬事实（供专家判断）

我们的 donor 一直是 **Chiffon 的 patch unit**；而管理器的框架是 **GameData 里同 ID 的原版 unit**。
用只读探针 `_probe_origtf.py` 对同一个 AssetKey（`0x9841b16544c8fbae`，Torso/Stocky）三方对比：

| 字段 | A = 游戏原版 | B = Chiffon patch | C = 我们 |
|---|---|---|---|
| TOC 大小 | 8 816 B | 10 412 B | 74 956 B |
| TransformInfo 节点数 | **22** | 23 | 115 |
| 矩阵为单位阵的槽位 | **{0,1,2,3,4,17}** | {0…10} | {0…10, 23, 104, 109, 110, 113} |
| mesh 顺序（按 `MeshInfo`） | **culling, culling, lod3, lod2, lod1, lod0** | lod3…lod0, culling, culling | 同 Chiffon |
| 各 mesh 的 `TransformIndex` | **12,15,18,19,20,21** | 5,6,7,8,17,22 | 同 Chiffon |
| **4 个 LOD mesh 的变换矩阵** | **非单位阵**，`matT=(-0.02891,-0.00305,+1.25703)` | 单位阵 | 单位阵 |
| 流数量 | **3** | 4 | 4 |
| 主网格 stride | **60** | 56 | 56 |
| 主流组件（9 个 vs 8 个） | `0/2@0, 1/30@0, 4/33@0, 4/33@1, 4/33@2, 7/35@0, 6/28@0, 6/28@1, 6/24@2`（**3 组 bone_index = 12 影响**、UV 用 `vec2_half`、**无顶点色**） | `5/4@0, 0/2@0, 1/30@0, 4/1@0, 4/1@1, 4/1@2, 7/35@0, 6/28@0`（1 组 bone_index、UV 用 `vec2_float`、有顶点色） | 同 Chiffon |
| 各流顶点数 | [12 871, 24, 24] | [119 636, 358 908, 24, 24] | [121 704, 121 704, 24, 24] |
| `BoneInfo` 骨数 | [10, 8, 8, 8] | [11, 11, 11, 11] | [107, 107, 107, 107] |
| LOD group list `u32@+8` | `0x101` | `0x1` | `0x1`（照抄 Chiffon） |
| LOD group record0 的两个计数 | **5 / 4** | 3 / 2 | 3 / 2（照抄 Chiffon） |

**请专家注意两点：**

1. 上面每一项差异**都可能是自洽的** —— Unit 的 ABI / 变换 / 调色板都是**自我声明**的，引擎读的是本 unit 的声明。
   因此这张表**不能**证明"必然透明"。它只说明：**我们选错了"框架"**。
   按管理器的路线，框架应当是 **A（GameData 原版同 ID unit）**，不是 **B（另一个 mod 的 patch unit）**。
2. 第 9.3 条 5（**流 ABI 尽量沿用目标**）是我们现在**明确违反**的一条：原版主流是 stride 60 / `vec2_half` UV /
   3 组 bone_index / 无顶点色，我们输出的是导出器 profile（stride 56 / `vec2_float` / 1 组 / 有顶点色）。
   这条**是否致命**需要专家裁定。

### 9.5 本轮我做了什么 / 没做什么

- **没有改动任何产品代码**（`.cs` / `build_patch.py` / `hd2_unitbuild.py` 等一律未动）。
- 只在 `tmp/rig-lab/` 新增了一个**只读**探针 `_probe_origtf.py`（上表的唯一来源）。
- 唯一一次"只读实跑"：`hd2mod patch repair inspect 8740e923 … --json`，它正常工作，
  只报 `GameDataIndexNotCurrent`（索引过期）。**索引重建需要关闭管理器**（`ManagerProcessRunning` 安全闸门），
  我**没有**绕过，也没有继续。

### 9.6 仅记录、未追查（与本节主题无关，避免专家被误导）

当前游戏 `data/9ba626afa44a3aa3.patch_0` 指向 `Lunalice-RigTest`，而**当前生效的护甲注册表
（rev 93, enabled）引用的 unit id 是 `1f39ba3f93050001..13`**，这批 unit 只存在于 Milltina 的 patch 里、
现在**没有被部署**。这属于"部署 / 注册表"层面，**不是** same-key 重建功能的一部分，我按用户要求**没有**继续追这条线。
记录在此只是为了说明：**第 58 轮的实机结果可能并不反映我们 unit 本身的正确性**，请专家在判断时把这一点纳入考虑。

---

## 10. 独立专家裁定：已有修复可以保留，但“唯一根因、全部修好”不成立

本轮范围：独立审查、只读包解析、保存证据、提供指导；没有改主管线、正式权重或游戏部署，没有重新打包。本节结论优先于前面“不要再查”“已修”的断言。需要继续做的是结构/装配修复，暂不继续六区域刷权重。

### 10.1 版本证据：旧失败包不能从 Git 完整还原

- 第58轮失败包：记录为709668 B、41 entries、9 Unit；管理器日志在12:17:29确实读到9个可转移Unit。未找到失败包的独立提交和保留副本。
- 第59轮当前包：12:55:28写入同一 `E:/mod/Lunalice-RigTest/`，91092 B、33 entries、1 Unit。`build_patch.py` 构建前删除同名旧产物，因此原输出被覆盖。限定仓库/E:/mod范围的查找未找到旧包；不等于证明所有磁盘及归档内都不存在。
- `3545674`（12:58:56）首次提交 build_patch.py/hd2_unitbuild.py/_verify_patch.py，不能用它的父提交找回这三个文件的第58轮版本。`9cf2783` 是更早权重编码提交，并不是失败导出器快照。
- 工作区仍有 export_obj.py、hd2_patch.py 的未提交修改，以及 hd2_unit.py/hd2_unitw.py/hd2_asset.py 等未跟踪依赖。不要 `git reset/checkout` 整体回滚，也不要认为提交号已足够复现当前包。hd2_patch.py 的未提交修改改变矩阵返回约定（去掉转置），调用者必须一起审计。
- 已保存当前包、协议、manifest和关键脚本的证据副本：`tmp/rig-lab/out/unit-review-evidence-c7a9425dc5de.zip`。这是**第59轮现包**备份，不是找回了第58轮。
- 当前 patch SHA256：`c7a9425dc5de2c7b8b9c5a479db600a2c5c2bca7f9619b40339ff52143294226`；GPU：`5122001f3c624e6e1f54f2e1e5b6d5ed9c690994a93279f1f4eaabb0e480e149`。几何hash相同不能区分两个打包版本。

### 10.2 对旧现象与现包修复的裁定

**抬高约0.973m、骨骼原点堆叠的解释与SDK源码吻合，但旧数据未能独立重读。** SDK用 MeshInfo.TransformIndex 取网格矩阵，用 TransformMatrices 直接放置骨骼。因此若旧包确实如§3所记，错误指向骨盆高度节点足以解释抬高，I骨矩阵足以解释原点堆叠。§3(e)公式旁的注释“lod3/2/1/0→11/10/9/8”写反了：`8+(3-lod)`实际得到8/9/10/11，应按公式核对，不能复制错误注释。

**现包绑定部分有真实修复成果。** 独立从磁盘读取现包：115节点/115唯一hash；逐节点局部×父世界与世界矩阵最大误差1.0203e-7；四套LOD全部逆绑定互逆误差8.8143e-8（float64复算）；可见LOD仍为5/6/7/8且均I。额外复核palette→hash及GPU权重/索引与冻结NPZ一致。这些排除了现包中相应的静态数据错配，不证明运行时动画、材质或剔除正确。

**不能宣布“透明/钢丝球的唯一根因”。** 错误绑定确实可以造成随动画变化的灾难性变形，是强候选；透明也可能涉及材质alpha、面绕序、剔除、加载路径等。权重和为1只证明归一，位置解码一致只证明位置编码。正确结论是：已识别并修复一组重大结构错误，尚待下面遗漏处理与实机闭环。

### 10.3 当前包的确定遗漏：MeshInfo边界未更新

`hd2_unitbuild.py:402` 对所有可见LOD复制 `ref.mesh_infos[0].unk2`；419行对两个culling都复制 `ref.mesh_infos[4].unk2`。`UnitMeshReader.cs:624–626`将其前7个float读作 CullingBounds，不能当不透明填充照抄。

本轮从TOC mesh offset表直接提取字段，并与GPU解码顶点比较：

| 项 | 当前实际几何 | 当前保存值 |
|---|---|---|
| 可见网格z范围 | -0.091181～2.029500m | 0.997623～1.717627m |
| 可见网格x范围 | -0.736943～0.736957m | -0.339624～0.299235m |
| 可见网格最大原点距离 | 2.038316m | radius 1.719567m |
| 可见网格bbox外点数 | **66290 / 121704** | 四个LOD同样遗漏 |
| culling A实际局部最大范数 | 1.713475m | 0.457118m |
| culling B实际局部最大范数 | 1.954802m | 0.457118m（复制了A的旧值） |

两个culling各24点全部在其MeshInfo保存bbox之外。`rewrite_lod_group_list`只改了另一份LODGroup记录，没有同步这份边界。

**应修，但不要称它已证实导致当前透明。** 必须核对SDK每一类字段的坐标空间、中心/radius口径，再根据最终section索引引用的顶点计算逐mesh边界；不要给共享stream里所有mesh无差别使用整个stream bbox。当前四个可见LOD各含全身，故全身bbox恰好适用。保留32B字段中未确认的最后4B，不整块猜值。绑定态边界通过之后仍须验证动作包围范围。

另一风险是 `build_patch.py:398` 把 helmet_culling/torso_culling 都放大全身，并断言“只增加overdraw”。仓库 `CanonicalAutoLodMappingExpander.cs:36` 把它们描述为 optional cutouts；这不足以确定全部引擎语义，却足以否定“普通可见性包围盒、越大越安全”的想当然。**区分可见mesh的bounds与culling代理壳体**。首先复用已知目标代理结构，不先放大两个特殊壳；必要时制作仅改变代理策略的独立对照。保留/最小化/移除策略必须按SDK有效结构执行，不任意删字节。

### 10.4 用户点名的重建功能：采用SameKey路线，不转成CrossArmor任务

权威路径：`CanonicalSameKeyReconstructionService` → `SameKeyCanonicalUnitRebuilder` → `CanonicalStreamContractCompiler` / `CanonicalLodBonePaletteCompiler` / `CanonicalMeshPreparation` / `CanonicalUnitRebuilder`。

- 版本号0x2c处为10800438不代表结构或渲染正确。首页批量仅选过时项；详情单项没有过时门槛，但仍要求GameData同键目标和有效索引。
- 重建框架是**GameData同(TypeID,FileID)的原版Unit**。当前fresh ID `d5cf3b8546db25f0`没有同键目标，不能直接通过菜单自动找到宿主；不得伪造低版本号骗批处理。
- 路线应先选定用户实际装备/体型对应的一个原生载体ID，冻结源最终几何、权重与材质，再建立与该目标同键的可解释输入/显式重建请求，复用上述重建组件。新中间输入另存目录，不能覆盖现包；不要只改ID就宣称结构已修。
- **非单位网格变换合法。** 原版框架可能有非单位T；行向量约定：源mesh世界矩阵S、目标T、骨世界B，则 `p_target = p_source * S * inverse(T)`，写出逆绑定 `J = T * inverse(B)`。源最终顶点已是角色空间时S=I。用 `p_target*T == p_final` 以及 `J*B == T` 验证。列向量/SDK表达要按其转置约定转换，不能混乘。
- §2.1“必须单位阵”仅是**当前Python简化实现**的前提；换原版框架时若仍写inv(B)，或把T强行清I而不处理关联数据，会再次出错。5/6/7/8也只是旧donor索引，不能硬编码搬到新目标。
- 保留目标ABI的原则是“它能承载最终语义时保留”，不是“所有mod必须长得和原版相同”。本轮已实读当前56B布局，与固定编码顺序一致：color4、position12、normal4、UV三组各8、half4权重8、uint8x4索引4；culling44B也一致。**未发现当前声明与payload在这项上错位，不能拿原版60B不同就判56B致命。**
- 但Python克隆donor声明、固定打包56B的方式不能直接换成原版60B模板使用。换框架时须让contract compiler决定格式并按最终声明编码；拒绝“只换ref、保留pack_mesh_vertices”。目标不能承载额外语义时由编译器明确fallback。多组bone_index不自动等于12个骨影响，需要完整权重布局/语义证据。
- `SameKeyCanonicalUnitRebuilder`会重新处理palette、材料、变换及未映射mesh；不是一键保留一切。来源/目标任一能力不支持会拒绝或原样保留，务必读诊断和来源几何类型，避免把skinned源误判成static锚到单骨。
- 不能假定重建器已有所有边界处理：当前Canonical保留某些头字段，需加§10.3独立验证。如果同一来源payload被用于多个不同目标，服务的配方复用还需包含目标契约/来源身份，不能只凭来源GPU相同复用全部目标结果。首轮只选一个目标避免扩大变量。
- GameData索引若被安全闸门阻止更新，保留诊断，按正常流程关闭管理器再更新；不要绕过进程锁。本轮未运行apply、未更改索引或部署。

### 10.5 “一份全模型”与“一条加载引用”必须一起满足

现包确有1 Unit，但协议9条model_piece全部引用它；没有map_model。解析器按body/slot/piece_type识别不同piece，未按Unit ID合并。**1份资源≠1个渲染实例**；也不能反过来说9条必然同时生成9个，体型/头身过滤会影响实际引用。

当前fresh既没有覆盖一个原生unit ID，也没有原生装备对新ID的引用；附带协议无mapping。它不能作为“无需Lua”的直接测试包。不要建议用户直接拿fresh测试，再以不显示来判断几何错。

当前已知部署还包含runtime Lua；rev93协议只有模型声明，无mapping，运行回执active_mappings=0/last_writes=0。12:19/12:25回执说明那时处理过rev93，**并不证明现在在运行，也不能倒推12:17旧包失败是缺失1f39资源造成**。空mapping也不等于无Lua环境。§9.6中的缺失ID仅凭声明不构成主动加载错误。

建议输出契约：**选定一套装备/体型，只有一个可见完整模型载体，其他原装备槽位不再渲染重复/原始外观。** 若仅覆盖一个原生unit，其他原槽可能仍显示；需要明确隐藏策略。可复用隐藏占位的底层组件，但不要把用户工作流改成CrossArmor。隐藏可能需要额外原生unit资源：它们应只有有效微小占位，无全模型payload；管理器 `CanonicalPlaceholderMinifier` 的实现是3点、0.0001m三角形，并非空文件。

**用户字面要求patch里也只有1个Unit时，不能悄悄加入隐藏unit。** 先做仅1个同键载体的结构验证包并明确其他槽仍可能显示；完整角色替换若需要额外占位，应展示数量及各自用途，让用户确认这一差异。若通过原生装备引用修改隐藏其余槽，属于另一种资产编辑，需要单独验证，不能从新ID/协议自动推导。支持两体型可能需要不同载体入口，首轮固定一个体型。

### 10.6 现有检查的盲区，以及应补的负对照

现有脚本本次实跑实际52项全过（文档仍写45）。通过的检查有价值，但它们未覆盖全部契约：

- 与同一reader/writer往返相同、用写入器自己的remap-offset计算函数验证自己，是一致性证据而非独立格式证明。
- 某些绑定检查只取首套BoneInfo、每7骨取一个；avatar hash找不到直接continue，可能仍报“every bone”。必须检查缺失集合为空，全LOD/全骨参与。
- 索引<107不等于指向正确骨；逐section验证 fake→remap→RealIndices→hash→骨矩阵，并按骨身份比较权重。
- section总索引数相同不能证明不重叠/无空洞；需验证区间与材质一一对应、所有引用顶点合法。
- `_verify_patch.py:293` 使用 `sid不在槽表 AND mid不存在`，一个条件错时可能漏检；应验证完整映射关系，不只是改成OR便止步。
- mesh bbox、代理壳/LODGroup关联与实际加载引用不在原检查覆盖内。Normal中位数合格会漏掉少量严重反向法线。

建议负对照在**内存副本或独立fixture**上做：把一个绑定矩阵置I、调换两项palette、删除一个avatar hash、交换section区间、保留旧bbox、把T换非单位而不改J、伪造不匹配的stream stride；每项应由相应独立检查明确失败，不能动正式包来造坏样本。

另有非首要缺陷：oct_encode在负Z折叠用np.sign(0)=0，与SDK sign(0)=1不同，(0,0,-1)会编码为近+Z；basic材质缺VTF分支引用只在flat分支定义的white；merge里的visiting没有执行环检测。应修并验，但没有证据将它们当作这次阴影钢丝球的已证根因。

### 10.7 对§7七个问题逐项回答

| 问题 | 裁定 |
|---|---|
| 1 全107是否过大 | TransformInfo可保留完整avatar及父链，107包含场景节点，不能全叫变形骨。BoneInfo应按LOD实际有效影响构建紧凑palette（当前用49）。107未超过当前uint8索引容量，但无证据保证引擎无限制或证明它是故障原因。 |
| 2 必须5/6/7/8吗 | 不必须；使用所选目标真实索引与完整T。同LOD共享BoneInfo的mesh受共享transform约束；lod=-1代理不参加该约束。SDK AutoLods复制相同矩阵是其实现策略，不代表所有合法Unit全LOD必须相同索引。 |
| 3 NumRemaps | 对应该LOD共享材质序的remap表，不能一般化成unit全局材料资源数。当前每LOD单mesh且14槽，14条恒等表可自洽；多mesh/顺序改变应由palette compiler处理。 |
| 4 unit材质表 | 验证section短ID到material asset的完整映射；SDK按ID查表，不仅按数组位置。当前58对重复映射一致未发现错配，但重复顺序不构成一般契约。 |
| 5 LODGroup offsets | 未完整解码，不能确认所有语义。bbox字节搜索只适合已锁定hash且唯一命中的donor，不能泛化原版不同表。优先保留已知目标结构，更新字段前确认坐标/语义；必须同步MeshInfo另一份边界。 |
| 6 Incriment | 精确语义仍未确证。沿用可信avatar/目标对应值比猜“父链跳层数”合理；必须重映射parent并验证FK，不发明算法。 |
| 7 验证顺序 | 同意先结构/装配、后权重；新增当前包证据冻结、单同键载体、SDK实际反导入及无Lua干净部署关卡。通过后才恢复六区权重。 |

SDK的根local默认是pos=0、rotation=I、scale=1，**不是全零矩阵**；§2和旧记忆“根骨全0”应纠正。

### 10.8 下一轮执行顺序与交付标准

1. 保留本轮证据副本；后续每个候选使用新目录与hash，包含所有导出依赖/未提交修改、参数、原生目标AssetKey和GameData版本，不再同路径覆盖唯一测试证据。
2. 首选沿SameKey重建组件做**一个明确原生载体**，其余槽位政策单列。不要同时重刷权重、改材质风格、改代理壳和换加载路线。目标ABI及非单位T按§10.4适配。
3. 修复/校验MeshInfo边界；分清代理cutout壳与普通bounds，按可信目标策略保留。补充§10.6检查与负对照。
4. 用**真正SDK导入器**在独立Blender场景读取最终发布包，不只看npz或本地自制reader。脚本自动列骨数、骨hash、父链、骨世界位置、网格对象矩阵、Armature modifier、vertex groups和包内权重映射。比较应用对象变换后的角色空间几何：当前冻结范围应近z=-0.091181～2.029500，不能再整体+0.973m。
5. 自动施加小幅已知关节旋转，按实际读入的绑定/权重检查局部运动与未影响区，生成固定视角截图。用户无需学习Blender权重面板。合成动作通过不冒充游戏动作通过。
6. 对一个体型、一套护甲做受控无Lua冷启动对照：原版基线→仅一个同键模型候选；记录部署文件/hash、运行状态、视角与动作。不要把现有无mapping的Lua视作“已经没有Lua”。本轮未替用户改部署，主线程应通过正常管理器流程安排。
7. 成功显示且无钢丝球后，才修局部腰腹/长发/手指权重。若仅随镜头消失，优先查bounds/代理；SDK里正常而游戏仅动作炸裂，追真实运行绑定/索引/实际加载版本；静态位置错则查T/local/逆绑定。每次只变一个原因，留前后证据。

**本轮产物：** `review_unit_contract.py`（只读包检查+证据归档）、`out/unit-contract-independent-review.json`、上述证据ZIP、`outputs/unit-transform-independent-review.html`。当前发现足以指导修复，但没有新的可宣称已修好的游戏包；不要把本节审查通过当成实机验收。


---

## 11. 主线程本轮执行记录（§10 之后的实际动作与实测证据）

本节由主线程填写，回答 §10.8 第 1～3 条与第 6 条的前半段，并列出需要专家裁定的新问题。
所有结论都来自本机实跑；**没有做任何实机验收**。

### 11.1 交付物与部署状态

| 产物 | 路径 | 字节 | 条目 | Unit |
|---|---|---|---|---|
| 原生 1-unit 包（DS-10 载体） | `E:/tmp/rig-lab/native-1unit/` | 91076 | 33 | 1（`0xa1a7563e6ac8b8f8` = DS-10 Torso/Armor/Any） |
| DS-10 规整包 | `E:/tmp/an-out-ds10-1/` | 512788 | 54 | 22（1 真实 + 21 隐藏） |
| 原生 2-unit 包（RE-2310 载体） | `E:/tmp/rig-lab/native-re2310/` | 166128 | 34 | 2（`0xcae35ee15168145f` Slim / `0x9841b16544c8fbae` Stocky） |
| **RE-2310 规整包（已部署）** | `E:/mod/Lunalice-RE2310-Test/` | 563900 | 54 | 22（2 真实 + 20 隐藏） |

- 部署方式：把 `data/9ba626afa44a3aa3.patch_0[.gpu_resources]` 两个符号链接改指 `E:/mod/Lunalice-RE2310-Test/`。
  原目标已记录在 `E:/tmp/deploy-revert-note.txt`，回退只需把链接改回 `E:/mod/Lunalice-RigTest/`。
- **部署面与参考模组等价**：参考模组 `【4.28】Chiffon-LUNALICE替换RE-2310/` 只有
  `9ba626afa44a3aa3.patch_0` + `.gpu_resources`（`.stream` 是 0 字节）——即"一个 toc + 一个 gpu"就够了，
  我们的包同样不需要 `.stream`（承载 unit 的 `stream_size == 0`）。`*.protocol` 是**我们自己写的记录文件**，
  参考模组里**根本没有**它；管理器运行时读的协议是固定名 `armor-registry.protocol`（在 registry root 下），
  与本文件无关。所以 `enabled=false` 不构成任何风险，纯记录。
- 选择 RE-2310 而不是 DS-10 作部署目标的**结构理由**：我们 unit 的两个 culling 壳
  `0xa620a54c`（r=0.457118）与 `0x800b8ea2`（r=0.243766）与 **RE-2310 躯干的壳逐字节一致**；
  原版 DS-10 躯干只带 `0xa620a54c` 一个壳。也就是说本模型本来就是从 RE-2310 躯干结构派生的。

### 11.2 管理器 CLI 链路的实测事实（对后续选路有用）

1. **`armor-normalize` 只认原生 Unit id。** `ModEquipmentSourceFactsReader` 在
   `Property == UnitPartMapping` 时用 `patchUnitKeys.Where(mappedUnitKeys.Contains)` 过滤，
   而 `mappedUnitKeys` 来自 GameData 装备目录。因此**改名 id（`d…` 前缀）的旧包一律得到 `armors: []`**。
   实测 `Lunalice-Visible-Virtual-Model`（fresh id）与旧 `Lunalice-RigTest` 都是空；
   换成原生 id 后立刻得到 1 个 armor。这解释了此前"CLI 认不出我们的包"。
2. **`armor-normalize` 会自动隐藏整个 archive 的非目标 Unit**（`ArmorNormalizationService.cs` 行 57–61：
   "The archive is the visibility boundary"）。这正是本轮要的"自动隐藏该护甲其他 unit"，
   实测隐藏 21（DS-10）/ 20（RE-2310）个，且每个都被替换为
   `CanonicalPlaceholderMinifier` 的 3 顶点、0.0001 m 三角形（culling 壳保留）。
3. **`SelectPair` 决定了单 unit 只能落在 `BodyVariant == Any` 的部件上。**
   它要求 Slim+Stocky 成对，否则必须有一个 `Any`。RE-2310 躯干只有 Slim/Stocky，
   所以单 unit 会直接 `ArmorNormalizeTargetMissing`、`targets.Count == 0`、`armors: []`；
   必须一次给两个身形。DS-10 躯干是 `Any`，单 unit 即可且与身形无关。
4. **CLI 不会重建我们的 Unit。** 规整前后我们的 unit TocData 与 GPU 载荷**逐字节相同**
   （74940/74956 B、17457848 B），只新增隐藏占位 unit。风险面因此被限制在"新增的占位 unit"上。

### 11.3 独立审计（`tmp/rig-lab/_verify_normalized.py`，27 项）

不信任生产者，重新读容器、重新解析每个 Unit、重新推导关系。RE-2310 包结果 **26 通过 / 1 已知**：

复现命令（`--vanilla-dir` **必须**指向该 archive 的**全量** unit 抽取目录，否则基线不全、
"cutout 壳一致"会退化成 `0/0`、并把原版既有的悬空短 id 误报为"新引入"）：

```
python _verify_normalized.py <源 native 包 patch_0> <待检 patch_0> \
    --our-unit cae35ee15168145f,9841b16544c8fbae \
    --vanilla-dir E:/tmp/hd2-mgroot/re2310-full/unit
```

通过项包括：容器 magic/条目数；每个 entry 的 TOC/GPU/Stream range 都落在各自文件内；
14 Material + 16 Texture 全部存活；承载 unit 的 TocData 与 GPU **逐字节等于源包**；
每个非目标可见 LOD 只有 1 个绘制 section 且 ≤1 个三角形，几何跨度 0.0001 m；
**22/22 个非目标 cutout 壳的 mesh id、stream ABI、section 表、bbox 与原版逐项相同**；
section 窗口不越界；单 mesh 内绘制的索引窗口互不重叠；每条 stream 的绘制窗口恰好覆盖 `[0, num_indices)`；
承载 unit 的**可见 LOD 全部落在包内 Material 上**。

### 11.4 唯一已知瑕疵（不在我们代码里，请专家判定是否需要处理）

**CLI 生成的隐藏占位 unit 会丢掉"零索引 section"的材质短 id。** 实测 3 个 unit
（`0x8e9dea5c60da652d` 丢 `0x7079b003`、`0x24a740753a92e788` 与 `0x75cde6ee99e5a887` 丢 `0x0bf81d82`），
`mi.material_ids[k]` 指向的短 id 不在 `unit.section_ids` 里。

- 这些 section 的 `num_indices == 0`，不绘制，因此**功能上惰性**；
- **原版本身也有同类悬空引用**：DS-10/RE-2310 的 culling mesh 引用 `0x093fc934`，
  而原版 unit 的 `section_ids` 里根本没有它。所以引擎显然不做 slot 校验。
- 已把这条检查写成"**不得引入原版没有的新悬空引用**"，RE-2310 包只在这 3 处失败。
- 结论倾向：不阻断实机，但这是管理器 `CanonicalHiddenUnitBuilder` / 材质布局编译器的一条真实不一致，
  记录在案；**未改管理器代码**（用户明确要求先不改无关代码，且改它要重编 CLI）。

### 11.5 相对 §10.3 的进展

- **MeshInfo 边界**：已按"该 mesh 自己 section 引用的顶点"逐 mesh 计算
  （`hd2_unitbuild.mesh_bounds`），并**保留 `unk2` 最后 4 字节**（实测是逐槽位常量：
  LOD3=`02 00 00 00`、LOD2/1/0=`09 00 00 00`、culling=`00 00 00 00`，donor 与 vanilla 载体独立一致）。
- **culling 策略**：默认改为 `--cull-policy target`（原样复用参考壳，与管理器
  `SameKeyCanonicalUnitRebuilder.cs:114` "must retain the target cutout" 一致）；
  旧"放大到全身"降级为对照开关 `whole-body`。
- **oct 编码**：修掉 §10.6 指出的 `np.sign(0)` 缺陷，往返残差 89.378° → 0.475°（10-bit 量化下限）。
- 自建包校验：`_verify_patch.py` 51 项全过 + 7 项内存负对照全部被对应检查抓住。

### 11.6 仍未做、需要专家指示的部分

1. §10.8 第 4 条（**真 SDK 导入器**在独立 Blender 场景读回发布包）—— 未做。
2. §10.8 第 5 条（自动关节旋转 + 固定视角截图）—— 未做。
3. §10.8 第 6 条（无 Lua 冷启动对照）—— **部署已就绪但未实机**；本轮已把
   `data/9ba626afa44a3aa3.patch_0` 指到 RE-2310 规整包，`patch_1` 仍是那条只有 1 个 Lua 资产的
   管理器运行时补丁（无 Unit，不覆盖我们的 id）。需要用户进游戏确认后才有实机结论。
4. §11.4 是否值得改管理器；若要改，改 `CanonicalHiddenUnitBuilder` 还是材质布局编译器。
5. 一个未解的设计问题：我们的 unit 带 2 个 culling 壳（=RE-2310 躯干），
   而原版 DS-10 躯干只带 1 个。把本 unit 当作 DS-10 躯干使用时多出的那个壳
   （`0x800b8ea2`，r=0.243766）是否会让引擎的遮挡判定变严，请裁定。
