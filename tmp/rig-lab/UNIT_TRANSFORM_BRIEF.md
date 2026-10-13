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
`_probe_lodgroup.py`、`_verify_patch.py`。
