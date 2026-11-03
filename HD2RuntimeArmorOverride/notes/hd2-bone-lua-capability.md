# HD2 / Stingray 骨骼架构与 Lua 运行时能力调研

> 目的：判断"用 Lua 把 L4D2 的自定义骨骼链 + jigglebone 参数完整搬进 HD2"这条路的可行边界。
> 方法：**先读本仓库代码与探针结果（一手），再补外部资料**。所有能力结论都带 `文件:行号` + 代码片段。
> 拿不到证据的地方明确写「仓库里没有证据」，不猜 API 名。
>
> 调研日期：2026-11-03

---

## 0. 结论摘要（先看这张表）

| # | 能力 | 能否 | 关键证据 | 备注 |
|---|---|---|---|---|
| A1 | 读/写 unit 的骨骼层级（父索引、rest 局部位姿、世界 bind 矩阵） | ✅ 完全可读写 | `UnitMeshModel.cs:34-54`、`UnitMeshReader.cs:167-198` | 这是**离线资产层**，不是 Lua |
| A2 | HD2 动画是"烘焙的逐骨局部变换轨道" | ✅ 是 | `animation.py:250-300`、`animation.py:28-60` | 骨数写死在每个 `.animation` 头部（=90） |
| A3 | 顶点如何引用骨骼（几组权重） | **4 组**（4 索引 + 4 权重） | `UnitStreamAbiProfiles.cs:276-277` | 经 LOD 调色板两级间接寻址 |
| A3b | 追加骨骼后能否重分配顶点权重 | ✅ 是 | `Retargeter.JiggleBones.cs:44-61`、`UnitTransformInfoAppender.cs:34-104` | 已在生产链路里跑 |
| B1 | Lua 读骨骼局部/世界变换 | ✅ | `physbone_compat.lua:1632,1656,1216` | 但**句柄活不过一帧**，必须立刻转数字 |
| B2 | Lua 写骨骼局部变换（每帧） | ✅ 写入成功且**不被覆盖** | `hd2-physics-roadmap-and-handover.md:672,764`（+1.5 保持 450 帧） | ⚠️ **同名于 avatar 的骨"写了不参与渲染"**，见 §B2.3 |
| B3 | 物理查询 API（raycast / sphere_cast / PhysicsWorld / Actor） | ❌ **完全不存在** | `hd2-armor-physics-research.md:453-457`、`:587-591` | 决定"胶囊碰撞"只能自己近似 |
| B3b | HD2 自带的抖动骨 Lua 入口 | ⚠️ 存在但签名未知 | `hd2-armor-physics-research.md:464-467`、`:646` | 只能驱动状态机里已有的 6 根 |
| B4 | 运行时创建物体并挂到骨骼 | ✅ 部分（可 spawn 整个 unit 并 link 到骨节点） | `tmp/probe/link.lua:69-89` + `tmp/probe/v05/v06/v07.txt` | 不能凭空创建"骨节点" |
| B5 | 每帧回调 + dt | ✅ `_G.update(dt)` 与 `_G.render()` | `lua_surface_probe.lua:203-217`、`physbone_runtime.lua:1109-1124` | 回调约 **120 Hz**，与渲染帧率解耦 |
| B6 | 运行时克隆/替换 unit 的骨列表（加骨） | ❌ 运行时加不了；✅ **离线加骨 + 运行时写姿态** | `hd2-armor-physics-research.md:587`(B1)、`hd2-physics-roadmap-and-handover.md:960-981` | 这就是本项目现行走法 |
| C1 | 社区有自定义骨架/物理 mod | ✅ 有，且是两个成熟方案 | C-Rig Runtime、HD2 Integrated Armor System | 都**不是纯 Lua**：走 native addon |
| D1 | 完整搬 L4D2 骨骼链 + jigglebone | **部分可行** | 见 §D | 缺"改名/继承姿态"以外的三件事：碰撞、驱动信号、参数可调性 |

**一句话**：骨骼**拓扑**必须在离线资产里加好（我们已经会加）；Lua 只负责**每帧写那几根新骨的局部旋转**——这条路已经跑通。真正做不成的只有两件：**物理查询（碰撞体）** 和 **对 avatar 同名骨生效**。

---

## A. HD2 / Stingray 的骨骼与动画架构

### A1. `.bones` 资产 vs Unit 内的 `TransformInfo`（两个完全不同的东西）

**关键认知：HD2 的"骨架"其实在 Unit 资产里，`.bones` 资源只有名字表。**

#### (1) `.bones` 资产：只有名字 + LOD，没有父链、没有 rest pose

社区 SDK 的完整序列化器只有 5 张表：

`docs/HD2SDK-CommunityEdition-3.8.0/stingray/bones.py:9-30`
```python
def Serialize(self, f: MemoryStream):
    self.NumNames = f.uint32(self.NumNames)
    self.NumLODLevels   = f.uint32(self.NumLODLevels)
    ...
    self.BoneHashes = [f.uint32(value) for value in self.BoneHashes]
    ...
    self.LODLevels = [f.uint32(value) for value in self.LODLevels]
    if f.IsReading():
        Data = f.read().split(b"\x00")
        self.Names = [dat.decode() for dat in Data]
```

⇒ **没有 parent index、没有局部变换**。这一点在本仓也被独立确认：
`docs/hd2-native-bone-baseline-20261030.md:196`

> `bones.py` 只序列化 4 张表：`NumNames / NumLODLevels / BoneHashes / LODLevels / Names`
> —— **bones 资源里没有父子关系、没有物理参数**。父链在 Unit 的场景图里，物理参数在状态机里。

#### (2) Unit 内的 `TransformInfo`：这才是真正的骨架（父链 + rest + 世界 bind）

数据模型（C# 侧已完整解析）——`HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshModel.cs:34-54`
```csharp
public sealed record UnitTransformInfo(
	uint Reserved0, uint Reserved1, uint Reserved2,
	IReadOnlyList<UnitLocalTransform> LocalTransforms,   // 父空间局部变换
	IReadOnlyList<UnitTransformMatrix> Matrices,         // 世界 bind 4×4
	IReadOnlyList<UnitTransformEntry> Entries,           // ← 父索引在这里
	IReadOnlyList<uint> NameHashes);                     // 骨名哈希（有序）

public sealed record UnitLocalTransform(
	IReadOnlyList<float> Rotation,   // 3×3
	IReadOnlyList<float> Position,   // 3
	IReadOnlyList<float> Scale,      // 3
	float Padding);

public sealed record UnitTransformEntry(ushort Increment, ushort ParentIndex);
```

二进制布局（**这是"已能读写"的硬证据**）——`UnitMeshReader.cs:167-198`
```csharp
var count = checked((int)ReadUInt32(data, transformInfoOffset));
var reserved0 = ReadUInt32(data, transformInfoOffset + 4);
...
var localOffset    = checked((int)transformInfoOffset + 16);
var matricesOffset = checked(localOffset + count * 64);
var entriesOffset  = checked(matricesOffset + count * 64);
var hashesOffset   = checked(entriesOffset + count * 4);
...
locals[i]  = new UnitLocalTransform(
    ReadSingles(data, itemOffset, 9),        // rot 9 floats @+0
    ReadSingles(data, itemOffset + 36, 3),   // pos 3 floats @+36
    ReadSingles(data, itemOffset + 48, 3),   // scale 3 @+48
    ReadSingle(data, itemOffset + 60));      // pad @+60
matrices[i] = new UnitTransformMatrix(ReadSingles(data, checked(matricesOffset + i * 64), 16));
entries[i]  = new UnitTransformEntry(
    ReadUInt16(data, entriesOffset + i*4), ReadUInt16(data, entriesOffset + i*4 + 2));
hashes[i]   = ReadUInt32(data, hashesOffset + i * 4);
```

| 字段 | 含义 | 证据 |
|---|---|---|
| `LocalTransforms[i]` | **父空间**局部位姿（rot 3×3 + pos + scale），64 字节/节点 | 上面 |
| `Matrices[i]` | **世界 bind** 4×4，**行向量**约定（`world = local · parent`），平移在平坦下标 12/13/14 | `UnitTransformInfoAppender.cs:17,64-67` |
| `Entries[i].ParentIndex` | **父节点下标**（u16）— 所以**有 parent index 数组** | 上面 |
| `Entries[i].Increment` | 实测恒为 1 | `UnitTransformInfoAppender.cs:18` |
| `NameHashes[i]` | 骨名 32 位哈希，顺序即节点序 | 上面 |
| Unit TocData `+0x34` | `TransformInfoOffset`（u32） | `UnitMeshReader.cs:163` |
| Unit 头 `+0x44` | **指向矩阵数组内部**的绝对偏移；插一个节点必须 **+64** | `hd2-physics-roadmap-and-handover.md:960-966` |

**我们能写吗？能，而且是生产链路。** 追加新骨：
`HD2SourceMigration/VpkConversion/UnitTransformInfoAppender.cs:86-93`
```csharp
locals.Add(new UnitLocalTransform(IdentityRotation, [localX, localY, localZ], [1f,1f,1f], 0f));
matrices.Add(new UnitTransformMatrix(world));
entries.Add(new UnitTransformEntry(1, checked((ushort)extra.ParentIndex)));
hashes.Add(extra.NameHash);
```
并且自带"locals ↔ world 自洽"断言（`AssertLocalAndWorldAgree`，`UnitTransformInfoAppender.cs:114-144`），
调用点：`HD2SourceMigration/VpkConversion/VpkPatchAssembler.cs:458-467`
```csharp
// ①b 把 jiggle 骨的**合成节点**追加进 TransformInfo（必须在建网格之前）。
var transformInfo = UnitTransformInfoAppender.Append(avatarTransformInfo!, retarget?.ExtraBones);
if (transformInfo is not null && retarget?.ExtraBones is { Count: > 0 })
    UnitTransformInfoAppender.AssertLocalAndWorldAgree(transformInfo, avatarTransformInfo!.Matrices.Count);
```

另有一条"只导入 avatar 已有骨"的路径（用于把源模型的骨引进来）：
`HD2ModAdaptation/.../Canonical/CanonicalTransformInfoExpander.cs:40-78`（`EnsureHash` 会把 avatar 的
`LocalTransforms/Matrices/Entries` 整条父链拷进来；**要求哈希必须存在于 avatar**：
`:42` `throw new InvalidDataException(... does not contain required bone hash ...)`）。

### A2. 动画系统：`.animation` / `.state_machine` / `.flow`

#### 结论：HD2 角色 = Skeleton（TransformInfo）+ **AnimationStateMachine**；动画是**烘焙的逐骨局部变换轨道**

`stingray.Application` 暴露的命名空间里明确有 `AnimationStateMachineComponent`（`hd2-armor-physics-research.md:460`），
运行时的判据也是它：`HD2PhysBoneCompat/src/physbone_runtime.lua:224`
```lua
local animated = try(S_.Unit.has_animation_state_machine, unit) == true
```

**`.animation` 资产结构**（SDK 解析器，一手）——`docs/HD2SDK-CommunityEdition-3.8.0/stingray/animation.py:250-258`
```python
def load(self, tocFile):
    self.unk = tocFile.uint32(temp)
    self.bone_count = tocFile.uint32(temp)        # +0x04 = 骨数
    self.animation_length = tocFile.float32(temp) # +0x08 = 时长
    self.file_size = tocFile.uint32(temp)         # +0x0c = 数据长度
    self.hashes_count  = tocFile.uint32(temp)
    self.hashes2_count = tocFile.uint32(temp)
```
然后是**每根骨的初始局部状态**（`animation.py:283-300`）：
```python
for x in range(self.bone_count):
    bone_state = AnimationBoneInitialState()
    bone_state.compress_position = bit_array.get(x*3)
    bone_state.compress_rotation = bit_array.get(x*3+1)
    bone_state.compress_scale    = bit_array.get(x*3+2)
    ...
    bone_state.rotation = AnimationBoneInitialState.decompress_rotation(tocFile.uint32(temp))
```
再是**关键帧条目流**（每条 = 一根骨的一个通道）——`animation.py:28-60`
```python
type = (data[1] & 0xC0) >> 6
...
if type == 3:
    data2 = AnimationBoneInitialState.decompress_rotation(...)   # rotation data
elif type == 2:
    data2 = AnimationBoneInitialState.decompress_position(...)   # position data
elif type == 1:
    data2 = AnimationBoneInitialState.decompress_scale(...)      # scale data
```

⇒ **是的，就是烘焙的 per-bone local transform 序列**（rot/pos/scale 三种通道，按骨下标索引），
而且**骨数写死在资产头**：`docs/hd2-native-bone-baseline-20261030.md:718-730`
```
| +0x04 | **90** | **骨数**（正好等于玩家骨架原骨数） |
| +0x08 | 4.9837 | 时长（秒） |
```
玩家归档里有 **3522 份 `animation`**，全部按 90 根骨排布。这就是"给玩家骨架加骨必崩"的根因（附录 F）。

#### `.state_machine`：动画 + **每骨物理条目**

`docs/HD2SDK-CommunityEdition-3.8.0/stingray/state_machine.py:20-22,44-46`
```python
self.ragdolls = []
self.ragdoll_count = 0
self.ragdoll_offset = 0
...
self.ragdoll_count  = memory_stream.uint32(self.ragdoll_count)
self.ragdoll_offset = memory_stream.uint32(self.ragdoll_offset)
```
`state_machine.py:274-292`（`RagdollItem`，56 字节）
```python
class RagdollItem:
    def __init__(self):
        self.bone_index = 0
        self.params = [0] * 9
        self.unk_hash = 0
        self.unk_enum = 2
        self.unk = 0
    def load(self, stream):
        self.bone_index = stream.uint32(self.bone_index)
        self.params = [stream.float32(t) for t in range(9)]
        self.unk_hash = stream.uint64(self.unk_hash)
        self.unk_enum = stream.uint32(self.unk_enum)
        self.unk = stream.uint32(self.unk)
```
`bone_index` 指向 **`bones` 资源的下标**（`hd2-native-bone-baseline-20261030.md:743-744`：
"P3-1 已证：改 `bone_index=71` 的参数就改了 `support_mg` 的抖动"）。
9 个 float 的实测含义见 `hd2-native-bone-baseline-20261030.md:128-138`。

#### `.flow`
仓库里**没有** flow 资产的解析证据。唯一相关的事实是一条类型归类：
`HD2ModCore/Infrastructure/FileSystemAssetMetadataCatalogProvider.cs:182`
```csharp
"lua" or "flow" => AssetTypeCategory.Script,
```
⇒ Flow 被当作**脚本类**资产处理，我们没有它的节点图结构证据。**对本方案无影响**（不走 Flow）。

### A3. 网格 ↔ 骨骼的绑定关系

#### 顶点引用骨骼的方式：**每顶点 4 组（4 索引 + 4 权重）**

组件类型表 —— `HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshReader.cs:1143-1154`
```csharp
private static string GetComponentTypeName(uint type) => type switch
{
    0 => "position",
    1 => "normal",
    2 => "tangent",
    3 => "bitangent",
    4 => "uv",
    5 => "color",
    6 => "bone_index",
    7 => "bone_weight",
    _ => "unknown"
};
```
格式约束 —— `UnitStreamAbiProfiles.cs:269-279`
```csharp
6 => format.Name is "vec4_uint8" or "vec4_uint32",   // 4 个骨索引
7 => format.Name is "vec4_half" or "vec4_float",     // 4 个权重
```
⇒ **4-bone skinning**（`vec4`），没有第二组。

#### 寻址是**两级间接**：顶点索引 → LOD 调色板槽 → TransformInfo 节点

`UnitMeshModel.cs:67-83`
```csharp
public sealed record UnitBoneInfo(
	int Index, uint Offset, uint NumBones, uint MatrixOffset, uint RealIndicesOffset, uint RemapDataOffset,
	IReadOnlyList<uint> RealIndices,        // 调色板槽 → TransformInfo 下标
	IReadOnlyList<UnitBoneRemap> Remaps)    // 每材质槽：FakeIndices[]（顶点里的下标 → 调色板槽）
{
	public IReadOnlyList<byte[]> BoneMatrices { get; init; }   // 每槽 64 字节
}

public sealed record UnitBoneRemap(int MaterialIndex, uint Offset, IReadOnlyList<uint> FakeIndices);
```

三个不变式（实测，残差 0）——`docs/hd2-native-bone-baseline-20261030.md:551-562`
```
每个 BoneInfo（对应一个材质 / LOD 组）里有两张等长的表：
  RealIndices[i]  uint32     第 i 个调色板槽 → TransformInfo.NameHashes 的下标
  BoneMatrices[i] 64 字节    第 i 个调色板槽的 4x4 骨骼矩阵

实测不变式（残差 0，21 个 Unit × 全部槽验证）：
  BoneMatrices[i] · TransformInfo.Matrices[RealIndices[i]]  ==  X
  ⇒ BoneMatrices[i] = X · inverse(world_i)
蒙皮是 `v' = v · BoneMatrices[i] · world_bone(t)`
```
`BoneMatrices = mesh · inverse(bone)` 也在生产代码里：
`CanonicalInverseJointMatrixCompiler.Build(targetModel, targetMesh.TransformIndex, realIndices, errors)`
（`CanonicalAppendBoneCompiler.cs:113`）。

#### `CanonicalLodBonePaletteCompiler` 在做什么？——**是"调色板编译器"，不是"重分配权重"**

`HD2ModAdaptation/.../Canonical/CanonicalLodBonePaletteCompiler.cs:1-6, 40-60`
```csharp
// Purpose: Compiles all provisional skinned meshes of one target LOD into the SDK-style shared BoneInfo palette.
// SDK reference: BoneInfo.SetRemap() builds material-ordinal remaps after final object topology and vertex groups exist.
...
var palette = new List<uint>();
foreach (var input in inputs)
    foreach (var hash in ResolveActiveHashes(input.Mesh, input.ProvisionalBoneInfo, hashes, diagnostics))
        if (!palette.Contains(hash)) palette.Add(hash);
...
var realIndices = palette.Select(hash => IndexOf(hashes, hash)).ToArray();
var missingTargetHashes = palette.Where(hash => IndexOf(hashes, hash) < 0).Distinct().ToArray();
if (missingTargetHashes.Length != 0)
    return new(null, [], missingTargetHashes.Select(hash => new CanonicalPlanDiagnostic(
        "MissingTargetBone", $"Active final bone hash 0x{hash:x8} is absent from target TransformInfo.")).ToArray());
```

**它的职责**：把一个 LOD 下所有 mesh 用到的骨哈希**取并集**，编译成一份共享调色板（`RealIndices` + 每材质槽 `Remaps` + `BoneMatrices`）。
**它不做的事**：不认识新骨（`MissingTargetBone` 直接拒），不改顶点权重。

#### 真正"把顶点权重改挂到新骨"的是重定向层

`HD2SourceMigration/Retarget/Retargeter.JiggleBones.cs:44-61`
```csharp
private (List<RetargetExtraBone> Extras, Dictionary<int,int> WeightTargets) CollectJiggleBones(
    IReadOnlyDictionary<int,int> resolved)
{
    ...
    var extras   = PlanJiggleBones(_model.Bones, scratch, _rig.Bones.Count, AlignedSourceJoint);
    var targets  = new Dictionary<int,int>();
    foreach (var (sourceIndex, target) in scratch)
        if (target >= _rig.Bones.Count)      // 只有被改写到"原生骨之后"的那些才是合成骨
            targets[sourceIndex] = target;
    return (extras, targets);
}
```
配合 `RetargetExtraBone.AnchorIndex`（同一文件 `:17-24`）：**顶点烘焙时该用哪根原生骨**——
因为合成骨在 `_deform` 里没有条目（否则会"完全绕开锚点的姿态"，实测 24.3% 顶点位移、最大 1.42 m）。

⇒ **回答"网格顶点权重能否重新分配"：能。** 并且已在生产链路里：
`VpkPatchAssembler.cs:461`（追加骨）→ `RetargetedMeshBuilder.Build(..., transformInfo!, ...)`（`:477`，按 `WeightTargets` 写 Type=6）。

> ⚠️ 一个**已被推翻的旧结论**：`hd2-native-bone-baseline-20261030.md:651-654` 曾写
> 「加权到 avatar 骨架里没有对应名字的节点 = 顶点塌陷」。那是 L1-A 实验（2026-10-30）的观察。
> 2026-10-31 找到了真正根因：**Unit 头 `0x44` 必须随插入节点数 +64**（`hd2-physics-roadmap-and-handover.md:960-981`）。
> 修好之后「插节点 + 重指向 + `0x44 +64`」实测 **✅ 完全正常，与原版一致**。以新结论为准。

---

## B. HD2 Lua 运行时能对骨骼做什么

> 本节所有 API 名都来自**真实调用过的代码或探针结果**，不是上游 Stingray 文档的推断。
> 上游 Stingray 有 3098 个函数签名（`tmp/stingray-api/sigs.json`），HD2 **裁掉了其中一大半**，
> 照上游文档写会踩空。

### B1. 能否读骨骼的局部/世界变换？—— 能

| 函数 | 参数 | 返回 | 证据 |
|---|---|---|---|
| `Unit.local_pose(unit, node)` | unit, **节点下标（1 基）** | `Matrix4x4`（12 个有效数） | `physbone_compat.lua:1632` |
| `Unit.world_pose(unit, node)` | 同上 | `Matrix4x4` | `hd2-physics-roadmap-and-handover.md:641` |
| `Unit.local_rotation(unit, node)` | 同上 | `Quaternion` | `physbone_compat.lua:2262` |
| `Unit.local_position(unit, node)` | 同上 | `Vector3` | HD2IAS `runtime.lua`（`U.local_position(`） |
| `Unit.world_position(unit, node)` | 同上 | `Vector3` | `physbone_compat.lua:1656` |
| `Unit.world_rotation(unit, node)` | 同上 | `Quaternion` | `physbone_compat.lua:1216` |
| `Unit.node(unit, id32)` | unit, **骨名哈希（IdString32）** | 节点下标（**1 基**） | `physbone_compat.lua:1538` |
| `Unit.has_node(unit, id32)` | 同上 | boolean | `physbone_compat.lua:1536` |
| `Unit.num_scene_graph_items(unit)` | unit | 节点总数 | `physbone_compat.lua:1523` |
| `Unit.scene_graph_parent(unit, node)` | unit, node | 父节点下标 / nil | HD2IAS `runtime.lua:498,5631` |

代码原文（`HD2PhysBoneCompat/src/physbone_compat.lua:1630-1639`）
```lua
for index, bone in ipairs(chain) do
    springs[index] = M.new_spring(bone, options.segmentUnitScale)
    local ok, pose = pcall(function() return S.Unit.local_pose(unit, nodes[index]) end)
    -- 存**数字**，不存句柄：实测缓存句柄 0.1 秒就变 inf（一致性电池 E6）。
    local snap = (ok and pose ~= nil) and snapshot_pose(S, pose) or nil
    basePoses[index] = snap
```

**三条必须知道的坑（都是踩出来的）：**

1. **`Unit.local_pose` 返回的是临时对象，句柄连一帧都留不住**（`physbone_compat.lua:1119-1124`）：
   > 缓存句柄和"当场重读"在 **0.10 秒**后就分叉，而且差值是 `inf` …… 所以：任何句柄都必须**当场**转成数字。
2. **`Matrix4x4.to_elements` 只返回 12 个数（3×4，列主序）**，不是 16；旋转占 1..9，平移在 10..12
   （`physbone_compat.lua:1127-1130`、`hd2-physics-roadmap-and-handover.md:999-1000`）。
3. **`Unit.bones` 会 AV 崩溃，禁止批量调**（`hd2-physics-roadmap-and-handover.md:690`）：
   > `Unit.bones` | 遍历到第 ~42 个就 **AV 崩溃**（`0xC0000005`，两次复现） | ❌ **禁止批量调**
   探针脚本自己也标了：`tmp/probe/link.lua:2-3` — `-- ... no Unit.bones (it crashes).`
4. **`Unit.node` 对不存在的名字也返回一个数字** —— 必须先 `has_node == true` 再取
   （`physbone_compat.lua:1519-1522`）。

节点名的解析必须两步（`physbone_compat.lua:1530-1548`）
```lua
local function resolveNode(text)
    for _, candidate in ipairs(M.bone_name_aliases(text)) do
        local id = id32(candidate)                       -- Application.string_to_id32
        if id ~= nil then
            local okHas, has = pcall(function() return S.Unit.has_node(unit, id) end)
            if okHas and has == true then
                local okNode, node = pcall(function() return S.Unit.node(unit, id) end)
                -- Unit.node 是 1 基下标（§12.1 实测）：合法范围是 1..nodeCount。
                if okNode and type(node) == "number" and node >= 1 and node <= nodeCount then
                    return node
```

### B2. 能否写骨骼的局部/世界变换？写了会不会被动画覆盖？—— **能写、不被覆盖，但有两个致命前提**

#### B2.1 写入 API（快慢两条路径）

`HD2PhysBoneCompat/src/physbone_compat.lua:2216-2240`
```lua
--- 两条路径：
---   快路径（`M._fastWrite`）：四元数在 Lua 里算好，引擎只做 `from_elements` + `set_local_rotation`
---     = **每骨 2 次引擎调用**；
---   矩阵路径（默认/回退）：`Vector3` + `axis_angle` + `Vector3` + `from_quaternion_position` + `set_local_pose`
---     = 每骨 5 次。
--- 位置不需要写：我们从不改平移，`set_local_rotation` 会保留它。
local function write_pose(S, unit, node, rotation, base)
	if M._fastWrite == true then
		local qx, qy, qz, qw = q_axis_angle(rotation[1], rotation[2], rotation[3], rotation[4])
		local q = S.Quaternion.from_elements(qx, qy, qz, qw)
		if q == nil then return false end
		S.Unit.set_local_rotation(unit, node, q)
		return true
	end
	local q = S.Quaternion.axis_angle(S.Vector3(rotation[1], rotation[2], rotation[3]), rotation[4])
	if q == nil then return false end
	local pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(base.px, base.py, base.pz))
	if pose == nil then return false end
	S.Unit.set_local_pose(unit, node, pose)
	return true
end
```

#### B2.2 立即生效吗？—— **要等 `World.update_unit`**

三条铁律（`physbone_compat.lua:9-16`）
```lua
-- 三条铁律（都是实测踩出来的，见交接文档 §13.3 / §15）：
--   1. 必须缓存基准姿态，每帧从基准算。这类节点不会被引擎每帧重置，
--      写「当前值 × delta」会逐帧累加 —— 表现为疯狂旋转。
--   2. 只替换旋转，必须保留原来的平移。简单做 pose×delta 会把平移一起转走。
--   3. 写完调 World.update_unit，否则这一帧渲染看不到。
-- 引擎侧已证实（§15）：父子链会自动合成，所以我们只需写每根骨的 local pose。
```
调用点：`physbone_compat.lua:2148-2153`
```lua
-- 铁律 3：写完让引擎重算世界变换。
-- 代价说明：world 为 nil 时**必须不调**——update_unit(nil, unit) 是一次 AV 崩溃，
if written > 0 and session.world ~= nil then
    pcall(function() S.World.update_unit(session.world, unit) end)
end
```

#### B2.3 会被动画系统下一帧覆盖吗？—— **不会覆盖；但"同名骨"写了根本不参与渲染**

这是整个调研里**最关键的答案**，分两半：

**(a) 写进去的值会一直留着。** 实测（`docs/hd2-physics-roadmap-and-handover.md:672`）
```
| 一次性 `chest.local_position.z += 1.5`，之后每 30 帧回读 | **稳定保持 +1.5000 达 450 帧**，没有任何东西覆盖它 |
```
结论（同文件 `:764-766`）
```
**（b）停写 ≠ 复位。** `hold` 测试里 +1.5 稳定保持了 450 帧，没有任何东西覆盖它。
也就是说**最后写入的姿态会一直留着**。
⇒ 弹簧必须有**显式复位**：关掉功能时要主动写回 bind pose，否则角色会歪着停在那儿。
```
⇒ **所以"会不会被动画覆盖"的答案是：不会。** 反而要担心的是"写了之后引擎不还原"，
必须自己缓存 base pose、自己写回（`physbone_compat.lua:2157-2208` 的 `M.release` 就是干这个）。

**(b) 但对"avatar 骨架里同名"的节点，写入不生效于渲染。**（`hd2-physics-roadmap-and-handover.md:663-680`）
```
### 11.6 核心否定结论：护甲 unit 的**同名骨**写不进去
| `set_local_pose(chest, pose·R)` 后**同一帧**回读 `local_pose(chest)` | **等于我们写进去的值** |
| 同上，同一帧读子节点世界坐标 | **完全没变**（`moved=0.0000`） |
| 每帧持续转 90°，x/y/z 三轴各试一遍 | `head − chest` 偏移恒定 ≈ (0.02, 0.02, 0.345)，**完全不跟随** |
| 一次性 `chest.local_position.z += 1.5`，之后每 30 帧回读 | **稳定保持 +1.5000 达 450 帧** |
| 同上状态截图 | **人物完全正常**，看不出任何位移 |
⇒ **写进护甲 unit 的 local pose 会被保存下来，但对"avatar 骨架里同名"的骨不参与渲染。**
⇒ 那类骨的渲染变换是**按骨名从 avatar 重定向**过来的，与它自己的 local pose 无关。
```
**反过来，对"avatar 里没有这个名字"的新骨，写就生效** —— 这是 §12 的正面结论
（`hd2-physics-roadmap-and-handover.md:722-736`）：
```
## 12. M-E′：新骨**确实**能被 Lua 驱动（2026-10-31 收尾，本轮最重要的正面结论）
- `Unit.node(unit, string_to_id32("jig_breast"))` → **node 113**，即该 unit 的最后一个节点
- ⚠️ **`Unit.node` 返回 1 基下标**：`num_scene_graph_items=113` 时合法下标是 1..113。
```
以及最终形态（同文件 `:977-978`）：
```
| 插节点 + 重指向 + **0x44 +64** | 本轮修复 | ✅ **完全正常，与原版一致** |
| 同上 + Lua 每帧驱动 ±12° | 最终目标 | ✅ **乳房完整、无拉伸、被驱动抬起** |
```

**(c) 若哪天真要接管 avatar 同名骨，官方给了两个开关**（但**未实测调用过**，只确认在成员表里）：
`docs/hd2-armor-physics-research.md:607-614`
```
1. **`Unit.set_animation_bone_mode(unit, mode)`**
   mode ∈ `ignore` / `transform`（默认）/ `position` / `rotation` / `scale` / ...
   官方说明原文：`ignore` = *"Bone animations are ignored; they will not affect the unit's bone nodes."*
   → **可以把骨骼从动画驱动里"摘出来"**，交给我们的弹簧算。
2. **`Unit.set_bones_lod(unit, lod_level)`**
   官方说明：*"The LOD level determines which bones in the animation will be evaluated."*
```
⚠️ **诚实边界**：整个仓库（`src/`、`tmp/`）搜不到任何一次对这两个函数的**实际调用**。
清单只出现在 `hd2-physics-roadmap-and-handover.md:246` 与 `hd2-armor-physics-research.md:607-614` 的"可用"列表里。

#### B2.4 到底该 hook 在哪一步？—— 我们的生产实现挂在 `render` 之后

`HD2PhysBoneCompat/src/physbone_runtime.lua:1109-1124`
```lua
originalRender = rawget(_G, "render")
rawset(_G, "render", function(...)
    -- 先让原来的 render 跑完（别的 mod 可能也在包装），再写我们的姿态：
    -- 这样引擎已经算完动画、还没画，我们写进去的 local pose 会直接用于本帧蒙皮。
    local result
    if originalRender ~= nil then result = originalRender(...) end
    if driver.installed then
        driver.frame = driver.frame + 1
        local ok, err = pcall(drive, driver)
        ...
```
⇒ 这是**正确的时序答案**：动画求值发生在 `update` 里；`render` 之后写 pose，本帧蒙皮就用上了。

### B3. 有没有 World / 物理 API？—— **没有。整个物理层被裁掉**

#### B3.1 被裁掉的命名空间（游戏内探测坐实，不是推断）

`docs/hd2-armor-physics-research.md:453-457`
```
上游有、而 **HD2 里被整个裁掉**的命名空间（由我们自己的游戏内探测坐实，不是推断）：
`PhysicsWorld`（上游 19 个函数，含 `create_joint` / `spawn_box|sphere|plane`）、
`Actor`(43)、`Joint`(41)、`Mover`(16)、`Cloth`(3)、`HumanIK`(235)、`Vehicle`(46)、`Raycast`、`NavigationMesh`。
→ **HD2 的 Lua 里不存在任何物理世界、刚体、关节、布料或 IK 接口。**
`Unit.create_actor` / `create_custom_joint` / `create_cloth` / `destroy_cloth` / `find_cloth` 同样不存在。
```
同文件 `:602-603`
```
**但 `EntityManager.actor_component` 被砍了**（上游有 `actor_component(world) : stingray.ActorComponent`）。
→ **能造实体，造不出物理。**
```
另见 `docs/hd2-physics-roadmap-and-handover.md:239-243`（同一结论的表格版）。

#### B3.2 `Actor` 只剩一个计数器

`U.num_actors(unit)` 在 HD2IAS 里**唯一**的用法是断言"克隆体没有原生 actor"：
`tmp/hd2ias/extracted/RuntimePackage/runtime.lua:5651`
```lua
if type(get(U,'num_actors'))=='function' then assert(U.num_actors(u)==0,'copy has native actors')end
```
⇒ 没有 `Actor` 对象、没有 actor 的包围体/碰撞过滤查询。**仓库里没有任何 `Actor.*` 的方法调用证据。**

#### B3.3 bounding volume 查询：只有"节点世界坐标"，没有包围体

可用的只有 `Unit.world_position(unit, node)` / `world_pose`（逐节点）和网格级 culling bounds（**离线**读
`UnitMeshInfo.CullingBounds`，`UnitMeshModel.cs:126-129`，Lua 侧碰不到）。
**没有 `Unit.box_extents` / `bounding_volume` / `world_bounds` 之类的证据 —— 仓库里没有。**

#### B3.4 唯一与"物理"沾边的官方 Lua 入口：HD2 自建的抖动骨 API

`docs/hd2-armor-physics-research.md:464-467`
```
**HD2 自己新增了上游没有的抖动骨 API**（均为 C 函数，上游 Stingray 不存在）：
`Unit.update_jiggle_bone_data`、`Unit.update_jiggle_bone_orientation`、
`Unit.debug_draw_jiggle_bone`、`Unit.debug_draw_all_jiggle_bones`、`Unit.debug_draw_all_constraint_bones`。
这是**唯一现成的"抖动骨"运行时接口**。
```
但：**参数签名未知，从未真正调用过**（同文件 `:646`）
> `update_jiggle_bone_data` / `update_jiggle_bone_orientation` 的**参数签名** —— 上游没有这两个函数；
> 我们的探测脚本只调用了白名单（`invokes_discovered_functions: "allowlisted_id_conversion_and_can_get_only"`），
> **没真的调用过**。验证方式：游戏内用 `pcall` 试调，错误信息会暴露参数个数。

且它**只能驱动状态机里已经存在的那 6 根骨**（`:498-501`）：
> 这 6 根骨的物理参数是**烘焙在状态机资源里的**，Lua 只能"驱动"它们，
> **不能新建第 7 根，也不能改这 6 根的参数**。

#### B3.5 结论：胶囊/球碰撞体用引擎做不出来

- 没有 `Raycast` / `sphere_cast` / `line_object` / `collision_filter`。
- 没有 `PhysicsWorld` / `Actor` / `Joint`。
- 没有碰撞体创建。
⇒ **纯 Lua 只能自己写"骨骼间最小距离约束"来近似自穿插防护**（见 §D2）。

> 对照：社区/native 路线是**有的**。HD2IAS 的 add-on 暴露了完整的碰撞能力
> （`tmp/hd2ias/extracted/RuntimePackage/runtime.lua:87-100` 的 `hd2ir_physics_capabilities_v1`：
> `max_colliders / max_contacts / max_link_collisions / max_candidates`），实测上限在 `:706-709`：
> `max_chains=1024, max_joints=8192, max_colliders=1024, max_chain_links=1024, max_instances=1024,
> max_contacts=32768, max_link_collisions=8192, max_candidates=65536`，
> 并且能力位 `0x7df` 的注释写明是 *"current node-contact build: collision filters are advertised"*（`:700-703`）。
> **这是 native 插件（addon64 + LuaJIT FFI），不是纯 Lua。**

### B4. 有没有办法创建运行时物体并挂到骨骼上？—— **能 spawn 整个 unit 并 link 到骨节点；不能凭空造骨节点**

#### (1) 不能凭空创建节点

`docs/hd2-physics-roadmap-and-handover.md:251-252`
```
⚠️ **`SceneGraphComponent` 上游 26 个方法里没有任何"创建节点"** → **Lua 不能凭空加骨**，
骨必须离线写进资产，Lua 只负责每帧写姿态。这正是 AQ 版 `HD2IRG1` 的做法。
```
`docs/hd2-armor-physics-research.md:587`（含义 B1 新增骨骼）
```
| B1 | **新增骨骼**（改拓扑） | ❌ 不可能 | HD2 `Unit` 119 个成员**零 `create_*`**；上游 Stingray 也**没有** `create_bone`；`Mesh` 无相关接口 |
```

#### (2) 但可以 spawn unit 并挂到骨节点上 —— **实测成功**

探针：`tmp/probe/link.lua:66-97`
```lua
if phase == 'spawn' then
    local okRes, resource = pcall(stingray.Unit.resource_name, donor)
    mark('donor resource ok=' .. tostring(okRes) .. ' ' .. tostring(resource))
    local okSpawn, result = pcall(stingray.World.spawn_unit, stingray.Application.main_world(),
        resource, stingray.Vector3(0, 0, 0), stingray.Vector4(0, 0, 0, 1))
...
if phase == 'link' then
    local okA, errA = pcall(stingray.World.link_unit, stingray.Application.main_world(), spawned, helmet, 'head')
    mark('link(name) ok=' .. tostring(okA) .. ' ' .. tostring(errA))
    if not okA then
        local okNode, node = pcall(stingray.Unit.node, helmet, 'head')
        ...
        local okB, errB = pcall(stingray.World.link_unit, stingray.Application.main_world(), spawned, helmet, node)
```
**探针实际写出的结果文件**（`tmp/probe/`）：
```
v05.txt = spawn ok=true [Unit '#ID[ea846ef460cc5ba0]']
v06.txt = link(name) ok=true nil
v07.txt = spawned visible ok=true nil
v08.txt = unlink ok=true nil
v09.txt = destroy ok=true nil
```
⇒ **`World.spawn_unit(world, resource, Vector3, Vector4)` + `World.link_unit(world, child, parent, 'byname')` 全链路可用。**

另一种 5 参写法（节点下标）：`tmp/probe/wear.lua`
```lua
-- attach the spawned cape to bone 0 of the live body
local ok, err = pcall(stingray.World.link_unit, world, spawnedUnit, 0, bodyUnit, 0)
mark('link ok=' .. tostring(ok) .. ' ' .. tostring(err))
```
`World.unlink_unit` / `World.destroy_unit` / `Unit.set_unit_visibility` 也都在实测里成功。

#### (3) 运行时改父子关系：`Unit.scene_graph_link`

HD2IAS 在运行时会拆/接场景图（`tmp/hd2ias/extracted/RuntimePackage/runtime.lua:5652-5654`）
```lua
W.unlink_unit(r.world,u)
U.scene_graph_link(u,r.offset,nil)
for n=r.offset+1,r.count-1+r.offset do U.scene_graph_link(u,n,r.offset)end
```
并在启动自检里**断言这些名字必须存在**（`runtime.lua:5443-5446`）
```lua
for _,name in ipairs({'spawn_unit','destroy_unit','unlink_unit','update_unit'})do
...
for _,name in ipairs({'name_hash','set_unit_visibility','scene_graph_link','scene_graph_parent','has_node','node','alive'})do
```
这与 `hd2-armor-physics-research.md:588` 的判定一致：
> | B2 | **改已有节点的父子关系** | ✅ 可以 | `Unit.scene_graph_link(self, i, parent)`，`parent` 传 `nil` 即解除 |

### B5. 每帧回调：是什么、多少帧、有没有 dt、有没有顺序/优先级

#### 机制：`_G.update(dt, ...)` 与 `_G.render(...)`，靠"包装 + 转发"串成链

最干净的实现（`HD2RuntimeArmorOverride/src/lua_surface_probe.lua:203-217`）
```lua
if type(previous_update) ~= 'function' then stop('update_unavailable'); return end
wrapper = function(dt, ...)
    if M.status == 'active' then
        if type(dt) == 'number' and dt == dt and dt >= 0 and dt < 10 then
            elapsed = elapsed + dt
        end
        local success, reason = pcall(tick)
        ...
    end
    return previous_update(dt, ...)
end
M.status = 'active'; rawset(_G, 'update', wrapper)
```
`previous_update` 来自 `:64`：`local previous_update, wrapper = rawget(_G, 'update'), nil`。

参考框架同时给两相（`tmp/pb-ref/HD2PhysBone-ReferenceSource-main/runtime/lua_patch/physbone_reference.lua:205-224`）
```lua
local function install_hooks(callbacks)
  local original_update = rawget(_G, "update")
  local original_render = rawget(_G, "render")
  if type(original_update) ~= "function" then return nil, "_G.update 不是函数" end
  if type(original_render) ~= "function" then return nil, "_G.render 不是函数" end
  rawset(_G, "update", function(...)
    ...
    pcall(function() if callbacks.update then callbacks.update(tick, unpack(arguments)) end end)
    return original_update(...)
  end)
  rawset(_G, "render", function(...) ... return original_render(...) end)
  return true
end
```
最朴素的用法（我们的探针脚本，`tmp/probe/bones.lua:81-87`）就是**直接给全局 `update` 赋值**：
```lua
update = function()
	local ok, err = pcall(tick)
	if not ok and phase ~= 'done' then
		phase = 'done'
		write('units-error.txt', tostring(err))
	end
end
```

#### 频率与 dt

- **有 `dt`**，但**不可靠**：`Application.time_since_launch` **每帧只更新一次**，
  一次回调内部取首尾差值恒为 0；一帧里被调多次时后面几次拿到 0。
  见 `HD2PhysBoneCompat/src/physbone_runtime.lua:789-829`
```lua
-- dt 用引擎自己的时间差，不写死 1/60。
-- 实测回调约 120 次/秒（不是 70，也不是 375），写死 1/60 会让物理比真实时间快/慢一截，
-- ⚠️ `Application.time_since_launch` **每帧只更新一次**，所以：
--   ① 只能在**帧之间**做差（在一次回调内部取首尾差值恒为 0，踩过）；
--   ② 一帧里被调用多次时，后面那几次拿到的差值是 0 —— 这时**绝不能再用上一次的 dt 积分**，
--      正确做法是：时钟没前进 ⇒ 这一拍不是新的时间步 ⇒ 什么都不做。
local now = try(S.Application.time_since_launch)
...
if not advanced then return end
```
⇒ **实测回调约 120 Hz**，与渲染帧率解耦。旧版写死 `1/60` 导致"物理比真实时间慢 15–30%"
（`docs/physbone-no-physics-intel-20261101.md:157`）。

#### 顺序 / 优先级

- **没有官方优先级机制**，只有"包装链"：后装的人拿到 `_G.update` 的当前值，包一层再写回。
  ⇒ **最后安装的那个最先执行**（在调用 `previous(...)` 之前）。
- 我们能控制的唯一顺序是"我的代码在原函数之前还是之后"。
  我们的生产运行时故意选 **`render` 之后**（`physbone_runtime.lua:1111-1112`）：
  > 先让原来的 render 跑完（别的 mod 可能也在包装），再写我们的姿态：
  > 这样引擎已经算完动画、还没画，我们写进去的 local pose 会直接用于本帧蒙皮。
- 参考框架的 C4-Quick-Actions 侧把相位明确标成 `before_original_update`
  （`docs/HD2-C4-Quick-Actions-main/src/automatic_tick.lua:9-11`）：
```lua
local function tick(dt)
    M.phase='before_original_update';M.tick=M.tick+1
```

#### 加载器侧（Bingus Shared Loader）

- 模块登记：`CowboyBingusModLoader.api == 1`，按资源名逐个 `Application.can_get('lua', name)` + `require`
  （`docs/BingusSharedLoader-main/docs/TECHNICAL.md:17-19`）。
- 日志：`CowboyBingusModLoader.open_log(filename)`（`:67`）。
- **加载器本身不提供任何骨骼 API**，也明确声明 `The loader itself performs no process-memory writes`（`:39`）。

### B6. 运行时能不能克隆/替换 unit 的 bone 列表（加骨）？

| 做法 | 可行性 | 证据 |
|---|---|---|
| 运行时**新增**骨节点 | ❌ | `hd2-armor-physics-research.md:587`；`hd2-physics-roadmap-and-handover.md:251` |
| 运行时**克隆整个 unit**（同资源再 spawn 一份） | ✅ | `World.spawn_unit(world, Unit.name_hash(unit), pos, quat)` — HD2IAS `runtime.lua:5644`；探针 `v05.txt` |
| 运行时**改父子关系** | ✅ | `Unit.scene_graph_link(unit, node, parent)` — `runtime.lua:5653-5654` |
| **离线**往 unit 的骨骼列表里加骨，运行时只写姿态 | ✅ **这就是本项目现行走法** | `UnitTransformInfoAppender.cs:34-104` + `VpkPatchAssembler.cs:461` |
| 给**玩家 avatar** 骨架加骨（离线） | ❌ 已终结 | `hd2-native-bone-baseline-20261030.md:746-758` |
| 给**护甲/头盔 Unit** 加骨（离线） | ✅ | 见 §A3 末的 `0x44 +64` 结论 + §12 实测 |

玩家 avatar 加骨为什么堵死（`hd2-native-bone-baseline-20261030.md:753-758`）：
```
- 新骨**放进 LOD0** → 动画资产（每份都写死 **90** 根骨，见 F.3）对不上 → 堆被写坏 → 非确定性崩溃。
- 新骨**排除出 LOD0** → 堆不坏了，但它不是一个"真正被动画求值的骨"，绑定/剔除链在它身上失配 → 材质解析失败 + unit 逐个消失。
**没有一个索引能同时满足"动画能对齐"和"物理能用"。⇒ 纯 patch 加骨到此终结。**
```
而**护甲 Unit 没有自己的 bones 资源、没有状态机**（`hd2-native-bone-baseline-20261030.md:96-102`），
所以给它加"非 avatar 骨名"的节点是**引擎接受的**（`hd2-native-bone-baseline-20261030.md:98-100`）：
> **附带一条对 L1 有利的证据**：护甲 Unit 里已经存在 `*_culling` 这种 avatar 骨架没有的节点，
> 说明"Unit 里放非 avatar 骨名"这件事本身引擎是接受的。

### B7. 真实 API 名对照表（全部来自实际调用，不是猜的）

| 想做的事 | **真实写法** | 不是这个（会失败/不存在） | 出处 |
|---|---|---|---|
| 读局部变换 | `Unit.local_pose(unit, nodeIndex)` → `Matrix4x4` | — | `physbone_compat.lua:1632` |
| 读局部旋转 | `Unit.local_rotation(unit, nodeIndex)` → `Quaternion` | `get_local_bone_rotation` ❌ | `physbone_compat.lua:2262` |
| 读局部位置 | `Unit.local_position(unit, nodeIndex)` → `Vector3` | `get_local_bone_position` ❌ | HD2IAS `runtime.lua`（`U.local_position(`） |
| 读世界位置 | `Unit.world_position(unit, nodeIndex)` → `Vector3` | — | `physbone_compat.lua:1656` |
| 读世界旋转 | `Unit.world_rotation(unit, nodeIndex)` → `Quaternion` | — | `physbone_compat.lua:1216` |
| 写局部变换 | `Unit.set_local_pose(unit, nodeIndex, Matrix4x4)` | — | `physbone_compat.lua:2238` |
| 写局部旋转 | `Unit.set_local_rotation(unit, nodeIndex, Quaternion)` | `set_local_bone_rotation` ❌ | `physbone_compat.lua:2231` |
| 写局部位置 | `Unit.set_local_position(unit, nodeIndex, Vector3)` | — | HD2IAS `runtime.lua:3900,5639` |
| 骨名 → id32 | `Application.string_to_id32(name)` | `IdString32(name)` ❌（只有 equal/from_hex/to_hex/to_string） | `hd2-physics-roadmap-and-handover.md:660-661` |
| 骨名 → 节点下标 | `Unit.node(unit, id32)`（**1 基**） | — | `physbone_compat.lua:1538` |
| 有没有这个节点 | `Unit.has_node(unit, id32)` | `Unit.node` 判空 ❌（会返回假数字） | `physbone_compat.lua:1519-1522` |
| 节点总数 | `Unit.num_scene_graph_items(unit)` | `num_nodes` ❌ | `physbone_compat.lua:1523` |
| 父节点 | `Unit.scene_graph_parent(unit, node)` | `num_children` / `child` ❌ | `hd2-physics-roadmap-and-handover.md:715` |
| 改父子 | `Unit.scene_graph_link(unit, node, parent)` | `set_parent` ❌ | HD2IAS `runtime.lua:5653` |
| 提交姿态 | `World.update_unit(world, unit)` | — | `physbone_compat.lua:2152` |
| 矩阵 → 数字 | `Matrix4x4.to_elements(m)`（**只 12 个**） | — | `physbone_compat.lua:1135` |
| 向量 → 数字 | `Vector3.to_elements(v)` | — | `physbone_compat.lua:1204` |
| 四元数 → 数字 | `Quaternion.to_elements(q)`（xyzw） | — | `physbone_compat.lua:1218` |
| 构造四元数 | `Quaternion.axis_angle(Vector3, rad)` | `Quaternion(0,0,0,1)` ❌ **直接把游戏打死** | `physbone_compat.lua:1505-1506` |
| 四元数 ← 4 数 | `Quaternion.from_elements(x,y,z,w)` | — | `physbone_compat.lua:2229` |
| 构造矩阵 | `Matrix4x4.from_quaternion_position(q, Vector3)` | — | `physbone_compat.lua:1198` |
| 是否存活 | `Unit.alive(unit)` → boolean | — | `physbone_compat.lua:1285` |
| 按资源找 unit | `World.units_by_resource(world, resource)` | — | `physbone_runtime.lua:213` |
| 遍历所有 unit | `World.units(world)` / `World.num_units(world)` | — | `physbone_runtime.lua:153,158` |
| spawn | `World.spawn_unit(world, resource, Vector3, Vector4)` | — | `tmp/probe/link.lua:69-70` |
| 挂到骨 | `World.link_unit(world, child, parent, 'head')` 或 `(world, child, childNode, parent, parentNode)` | — | `tmp/probe/link.lua:82`、`wear.lua` |

⚠️ **绝对不要**：`Quaternion(x,y,z,w)` 构造（`physbone_compat.lua:1505-1506`：*"直接把游戏进程打死（日志停在上一行，连 Lua 错误都没有）"*）、
批量 `Unit.bones` / `Unit.mesh` / `Unit.resource_name`（`hd2-physics-roadmap-and-handover.md:690-692`）。

---

## C. 现有 HD2 模组怎么做自定义骨骼 / 物理

### C1. 官方 wiki 的说法（保守、且已经过时）

- 《Jiggle Bones》（`boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/armor modding/jiggle-bones.html`）：
  > *"Currently, the SDK does not support adding custom jiggle bones to models. However, we can decently
  > fake the effect using existing bones on the Helldiver player rig."*

  这和我们量化出来的 L0 上限一致（**6 根，实可用 5 根**，`hd2-native-bone-baseline-20261030.md:619`）。
- 《Rig Editing》（`.../dev/animation/rigediting.html`）：
  > *"Editing the rig of any unit in Helldivers 2 is deeply intertwined with said unit's animations.
  > This means that if you want to modify the rig of a unit in any way, you will likely need to modify
  > all of its animations for the changes to function properly."*

  这正是我们实测的 F.3 结论（3522 份 `animation` 全部写死 90 根骨）。

### C2. 社区**已经做到**了自定义骨架 + 物理 —— 但都是 native 插件

| 项目 | 形态 | 能力主张 | 与我们的关系 |
|---|---|---|---|
| **C-Rig Runtime**（v0.3+） | 作者称"**Lua 基础运行库**"，有 **ShareLoader 版本**；前置：Bingus Shared Loader + Vanilla Plus Megapack；加载顺序要求 C-Rig 在 Loader 之上 | 「**自定义骨架**：突破原版游戏骨架限制，为角色替换完全不同的身体比例和结构」；「**物理效果**：衣物飘动、头发摇摆、身体部位抖动」；「**自定义动画**：眼部眨眼」 | 依赖它的 mod 例：`Kindred(C-Rig supported)`（*"Custom rig & Hair, tuft, ears, you-know-what physics & eye blinking animation. Requirements C-Rig Runtime(v0.3+)"*） |
| **HD2 Integrated Armor System (HD2IAS)** | `HD2IndependentRig.addon64`（2.25 MB native）+ `runtime.lua`（518 KB / 7314 行）+ ReShade/addon64 加载 | 「**real physics and a custom skeleton**」「摆脱绝地潜兵原有骨架的限制」；作者 AQ_Ex + WisA9 | 本仓有完整反编译/提取：`tmp/hd2ias/extracted/RuntimePackage/` |
| **HD2-PhysBoneTool / HD2SDK-AQ-Edition**（Estecsky） | Blender 插件 + `HD2IRG1` 二进制 rig 格式 | 作者化自定义 PhysBone rig | 本仓有 SDK 模块：`tmp/pb-ref/sdk-raw/hd2_system_rig_format.py` 等 |

**C-Rig 的定位说明（重要）**：它的介绍文案自称"Lua 基础运行库"，但从我们拆过的同类实现看，
**纯 Lua 无法提供碰撞**（§B3）。因此 C-Rig 若真有布料/头发物理，几乎必然
**要么只有距离约束、要么另有 native 组件**。仓库里**没有 C-Rig 的源码**，这一点无法证实 —— **标记为未知**。

### C3. `tmp/pb-ref/HD2PhysBone-ReferenceSource-main/` 是什么

**它是 HD2IAS 的"运行时参考源码 + 发布工具"仓**（不是插件本体）。`README.md:3-12`：
```markdown
这个仓库整理了 HD2 PhysBone 的运行时参考源码、Redist 校验工具，以及角色 mod 适配参考工具与示例。
- `redist/`：按 `runtime.lock.json` 校验 E-119 运行时文件并组装输出目录。
- `kit/`：角色 mod 适配工具、示例与流程编排。
- `runtime/`：C++、Lua patch 工具与 Lua 运行逻辑参考源码。
```

内容分三块：

1. **C++ solver 摘录**：`runtime/cpp/excerpts/spring_runtime.reference.hpp`（75 KB）、
   `addon.reference.cpp`；头文件 `lua_feed.hpp` / `spring_calib.hpp` / `palette_classify.hpp`。
2. **Lua 参考框架**：`runtime/lua_patch/physbone_reference.lua`（226 行，我们上面引用了它的
   `make_guard` / `make_scene_finder` / `make_node_sweeper` / `install_hooks`）。
   `docs/LUA-PATCH-MODULES.md:19-26` 列了它的职责：日志与 inflight crash 标记、四元数运算、
   world/avatar/camera 与 respawn 跳变发现、`update/render` 生命周期 hook、节点高度 sweep、`pcall` 隔离。
3. **kit 工具链**（作者侧的 Blender/Python 侧）：`inject_bone.py`、`make_runtime_rig.py`、
   `grow_palette.py`、`force_lod0.py`、`weight_transfer.py`、`spring_solver.py`、
   `hd2_skel.json`（护甲骨架）、`bonehash.txt`。

⇒ **对本次调研的价值**：它是"**离线加骨 + 运行时每帧写姿态**"这套路子的完整外部佐证，
和我们自己的 `HD2PBX01` 容器 + `HD2PhysBoneCompat` 是**同一层的两种实现**
（`hd2-native-bone-baseline-20261030.md:216-219`）。

### C4. 我们自己的定位

| 已有产物 | 作用 |
|---|---|
| `PhysBoneCarrier.cs` / `PhysBoneContainerEmbedder.cs` | 把 jiggle 参数（`hd2_spring_rig.bin`）嵌进 patch 的 `.gpu_resources` 尾部（`HD2PBX01` 容器） |
| `UnitTransformInfoAppender.cs` | 离线把合成骨追加进护甲 Unit 的 `TransformInfo` |
| `Retargeter.JiggleBones.cs` | 规划合成骨 + 顶点权重目标 + 锚点名 |
| `HD2PhysBoneCompat/src/physbone_compat.lua` | 运行时弹簧求解 + 每帧写 local pose（2597 行） |
| `HD2PhysBoneCompat/src/physbone_runtime.lua` | 运行时外壳：找 avatar/护甲、attach、hook `render`、分帧预算（1157 行） |

---

## D. 结论

### D1. 用 Lua 真正支持 L4D2 的自定义骨骼链：**部分可行**

把问题拆成 5 件事，逐条判定：

| # | 要做的事 | 可行性 | 缺什么 / 靠什么 |
|---|---|---|---|
| 1 | **把 L4D2 的骨骼层级搬过来**（骨数、父子关系、rest 位置） | ✅ **可行** | 离线：`UnitTransformInfoAppender` 往护甲 Unit 追加 `NameHashes/LocalTransforms/Matrices/Entries`。骨名可用 `source_extra_N` 占位（`SourceModel.cs:108-121`），**不需要**是 avatar 骨名 |
| 2 | **把 jigglebone 参数搬过来** | ✅ **可行** | 参数存在我们自己的 `HD2PBX01` 容器里（不碰状态机），29 个源参数 → 求解器增益的换算已完成（`physbone_compat.lua:532-545`） |
| 3 | **每帧驱动它们动** | ✅ **可行（已实机验证）** | `set_local_rotation` / `set_local_pose` + `World.update_unit`；插节点 + 重指向 + `0x44+64` 后实测"乳房完整、无拉伸、被驱动抬起"（`hd2-physics-roadmap-and-handover.md:977-978`） |
| 4 | **碰撞体（胶囊/球）** | ❌ **纯 Lua 不可行** | HD2 裁掉整个物理层（§B3）。见 D2 |
| 5 | **对 avatar 同名骨生效**（比如想动 `head`、`l_elbow` 本身） | ❌ **不可行** | 同名骨的渲染变换来自 avatar 重定向，写 local pose 无效（§B2.3）。要么离线改 avatar 骨架（已堵死），要么走 `set_animation_bone_mode(ignore)`（**未实测**） |

⇒ **结论：L4D2 的"自定义骨骼链 + jigglebone"整体搬迁是"部分可行"——**
**能搬的部分**（骨链拓扑、参数、二级运动）已经跑通；
**搬不了的部分**只有两块：**碰撞** 与 **接管既有骨**。

### D2. 胶囊/球碰撞体能不能做？

**取决于引擎物理查询 API —— 而它不存在。**（§B3，证据 `hd2-armor-physics-research.md:453-457`）

三条可选路径，按成本排序：

| 方案 | 做法 | 成本 | 效果上限 |
|---|---|---|---|
| **A. 骨骼间距离约束**（纯 Lua，零引擎调用） | 纯数字域：每节算世界位置（父世界位姿 × 我们写的 local pose，全在 Lua 里），对"同一条链的相邻节"施加最小距离约束；对"链 vs 链"施加分段胶囊-胶囊最近距离约束（把每节当成 line segment，用解析几何求最近点） | **低**（几十行 Lua，O(n²)，n 是骨数，几十根以内完全够） | 能防**自穿插**和**链间穿插**，但只能对**我们自己知道位置的东西**（同一条链、其它链、以及从 `world_position` 读到的少量锚点）。**碰不到世界几何**（墙、地面、敌人），因为没有 raycast |
| **B. 用 `Unit.world_position` 采样身体骨当"碰撞球"** | 把 `head/chest/hips/l_shoulder/r_shoulder` 等 avatar 同名骨当球心（能读到世界坐标，§B1），半径写死，对头发链做球-段约束 | 低—中 | 能在"头发 vs 躯干"上做出可信效果。**这是纯 Lua 能达到的最接近"胶囊碰撞"的东西** |
| **C. 自带 native 组件**（HD2IAS 路线） | addon64 + LuaJIT FFI，引擎侧提供 `max_colliders=1024 / max_contacts=32768 / max_link_collisions=8192`（`tmp/hd2ias/.../runtime.lua:706-709`） | **高**（要写 C++、要 ReShade/addon64、要绑游戏构建） | 真物理级碰撞 |

**建议**：先做 **A + B**（都在 Lua 数字域，不碰任何不存在的 API），把"自穿插 + 头发穿胸"这两类最刺眼的问题解决掉；
真要做布娃娃级碰撞再评估 C。

### D3. HD2 本身（非 Lua）有没有物理系统可以借？

有，但**都借不到**（这一节直接把三个"看起来可行"的猜想否掉，证据 `hd2-native-bone-baseline-20261030.md:405-413`）：

| 系统 | 是什么 | 能不能借 | 依据 |
|---|---|---|---|
| **每 Unit 的物理骨系统**（状态机里的 `RagdollItem`） | 全库 **52 个 Unit / 3121 条物理骨条目**，单 Unit 最多 32 根，骨名任意，106 种参数组合 | ⚠️ **能借，但只有 player avatar 那 6 根** | `hd2-native-bone-baseline-20261030.md:104-143`；参数可改（P3-1 实测生效，`:290-297`），但**没法加第 7 根**（`:746-758`） |
| **布娃娃**（`ragdoll_profile`） | Havok，23 具刚体，`hkRagdollConstraint002` + `hkHingeConstraint002` | ❌ **不可用** | 只在 `can_ragdoll`（倒地/死亡）时激活；且 `can_ragdoll/start_ragdoll/is_ragdolled` **不在方法表里**（`hd2-physics-roadmap-and-handover.md:243`） |
| **披风布料**（`cloth`） | Havok Cloth；全库**只有 3 个** cloth 资源 | ❌ **不可用** | 披风是**独立 Unit + 独立 bones + 独立 cloth**；**Lua 里没有 `Cloth` 接口**（`hd2-armor-physics-research.md:509-517`） |
| **护甲 Unit 自带 `bones` + `state_machine`**（L1） | 给护甲件内联自己的骨架和极简状态机 | ❌ **已实测否定** | "护甲 Unit 自己的 `state_machine` 不会被 tick"（`hd2-native-bone-baseline-20261030.md:655-658`），两轮 6 个变体全部"与原包无区别" |

### D4. 最值得走的技术路线（一句话）

> **离线加骨 + 运行时纯数字域弹簧 + 自写骨间距离约束**：
> 转换期用 `UnitTransformInfoAppender` 把 L4D2 的 jiggle 链作为"非 avatar 骨名"的合成节点追加进**护甲 Unit**（别忘了 `0x44 +64`），
> 顶点权重重指向这些节点（`AnchorIndex` 保住锚点形变）；运行时 Lua 只从 `Unit.local_pose` / `world_position` 取数字、
> 在自己的数字域里积分弹簧、每帧 `set_local_rotation` 写回并 `World.update_unit`——
> **不碰 avatar 骨架、不碰状态机、不碰任何物理 API**，碰撞用"骨骼间最小距离 + 躯干骨当球"近似。

---

## 附录 1：仍未确认的项（不得当事实用）

| # | 未知项 | 为什么未知 | 怎么验证 |
|---|---|---|---|
| U1 | `Unit.set_animation_bone_mode(unit,'ignore')` 到底能不能把**护甲 Unit 的同名骨**从 avatar 重定向里摘出来 | 成员表里有、官方文档有语义，但**仓库里从未实际调用过** | 游戏内：对护甲 unit 的 `chest` 调 `set_animation_bone_mode('ignore')`，再 `set_local_rotation` 转 90°，看子节点跟不跟 |
| U2 | `Unit.update_jiggle_bone_data` / `update_jiggle_bone_orientation` 的参数签名 | 从未调用过（探测脚本只调白名单） | `pcall` 试调，错误信息会暴露参数个数（`hd2-armor-physics-research.md:646`） |
| U3 | 抖动骨 `params[9]` 在内存里有没有运行时副本 | 未做内存取证 | FFI 扫描 + 与资源值比对 |
| U4 | `AnimationBlenderComponent.crossfade` 能否直接往护甲/avatar 上叠动画层 | 只确认存在与官方语义 | `EntityManager.animation_blender_component(world)` 取到后试 `crossfade` |
| U5 | `SceneGraphComponent.create` 到底是"造实体"还是"造节点" | 两种文档表述冲突（`hd2-armor-physics-research.md:595-600` vs `:653-655`） | 试调 + 看 `num_scene_graph_items` 变化 |
| U6 | `Unit.set_bones_lod` 的效果 | 未实测 | 改 LOD 后回读 `local_pose` 是否被重置 |
| U7 | C-Rig Runtime 是否含 native 组件 | 仓库里没有它的源码 | 下载包看有没有 `.addon64` / `.dll` |

## 附录 2：已实测的崩溃边界（务必遵守）

`docs/hd2-physics-roadmap-and-handover.md:682-695`
```
| **`Unit.bones`** | 遍历到第 ~42 个就 **AV 崩溃**（`0xC0000005`，两次复现） | ❌ **禁止批量调** |
| **`Unit.mesh(unit, 0)`** | 崩溃（对没有 mesh 的 unit 越界） | ❌ 禁止 |
| **`Unit.resource_name`** | 崩溃 | ❌ 禁止（改用 `debug_name`） |
```
补充：`Quaternion(x,y,z,w)` 构造直接打死进程（`physbone_compat.lua:1505-1506`）；
`World.update_unit(nil, unit)` 是 AV 崩溃而不是 Lua 错误、`pcall` 拦不住（`physbone_compat.lua:2149-2151`）；
对已销毁 Unit 调任何方法 = AV（`physbone_runtime.lua:23`）。
