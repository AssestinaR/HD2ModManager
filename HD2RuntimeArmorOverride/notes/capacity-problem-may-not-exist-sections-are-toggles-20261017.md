# 容量问题可能根本不存在：43 个源网格已经在 7 个 unit 里全量可见（2026-10-17）

## 这一轮做了什么

写了一个离线探针 `tmp/unitprobe`（引用 `HD2ModAdaptation`，只读），把任意 patch
归档里的每个 Unit 条目连同它的 stream 描述、每个 mesh 的 `NumSections`、
`SemanticInfo`（slot / pieceType / bodyType / weight）全部打出来。
配套汇总脚本 `scripts/summarize_unit_dump.py`。

跑的对象是库里真实发布的 Lunalice 包：
`E:\mod\Chiffon-Lunalice-抹茶芝士z&阿斯-替换RE2310\9ba626afa44a3aa3.patch_0`（550,931 B）。

## 结果一：`VertexBufferId` 是常量，不是标识符

```
VertexBufferId: distinct=1 shared-across-streams=1
  0x0000000006ffffff -> 1:s0,2:s0,2:s1,3:s0,3:s1,4:s0
IndexBufferId:  distinct=1
  0x0000000007ffffff -> ...
```

23 个 unit、所有 stream 的 `VertexBufferId` **全是 `0x06ffffff`**，
`IndexBufferId` **全是 `0x07ffffff`**。它们不含任何身份信息，也不参与寻址。

配合 `vOff=0`（主 stream）与 `vOff=6260792`（该 unit 自己 6,260,870 字节 gpu 切片内），
可以定论：**寻址 = 本条目自己的 `.gpu_resources` 切片 + `VertexBufferOffset`。**

> 一个 unit 的 TOC **不能**引用另一个 unit 的网格资源。合并必然复制顶点。
> 之前"`VertexBufferId` 可能是归档全局 key"的怀疑到此排除。

## 结果二：真实包只有 23 个键，其中 12 个是隐藏占位

| 类别 | 数量 | gpu 大小 | 说明 |
|---|---|---|---|
| 真实模型（`type=Undergarment`） | 11 | 0.5 – 8.1 MB | Helmet / Torso×2 / Hip×2 / LeftLeg / RightLeg / LeftArm×2 / RightArm×2 |
| 隐藏占位（`type=Armor`） | 12 | 1.2 – 15.7 KB | 每槽压制原版几何 |

**gpu 总计 47.4 MB**（含重复条目；去重后约 30 MB 量级）。

按 body 变体算，一个 Stocky 玩家实际生效：

```
Any 内衣 7 件 + Any 甲 4 件 + Stocky 甲 4 件 = 15 件  ≤ 20  ✅
```

**所以真实内容根本没撞上 20 件上限，也没撞上 18 键上限。**

## 结果三：43 个源网格已经在 7 个 unit 里

每个 unit 是"一个部位 + 一整套 LOD"（`lod=4,3,2,1,0` 五个 mesh）。
LOD0 的 section 数：

| 部位 | sections |
|---|---|
| Helmet | 22 |
| Torso | 25 |
| Hip | 9 |
| LeftLeg | 19 |
| RightLeg | 8 |
| LeftArm | 14 |
| RightArm | 1 |
| **合计** | **98** |

`materials=110` = 5 个 LOD mesh × 22 个 section，说明 section 与材质槽一一对应。

**推论（待构建期核实）**：43 个源网格 → 7 个 HD2 unit → 98 个 section，
平均每个源网格约 2.3 个 section（一个装饰用多份材质）。
这个 2.3 的比值是从"43 个源网格"和"98 个 section"两个独立来源推出来的，
**没有直接验证过映射关系**，需要在构建期把"每个源网格写了哪几个 section"记下来才能确认。

**但无论映射比例如何，有一点已经确定**：这 98 个 section 现在就在 7 个 unit 里
同时可见，只用了 7 个键。**"43 件全可见"在当前这版 mod 里已经成立**，
不是待解决的问题。

## 结果四（**已推翻**）：section 不是装饰开关，是材质批

`UnitMeshSectionInfo` 的字段：

```
Offset, MaterialIndex, MaterialSlotId,
VertexOffset, NumVertices, IndexOffset, NumIndices, GroupIndex
```

实测头盔 LOD0 的 22 个 section：

```
sec idxOff=     0 nIdx= 26556 vtxOff=0 nVtx=25344 matSlot=2558606028 group=0
sec idxOff= 26556 nIdx=  3072 vtxOff=0 nVtx=25344 matSlot=2372378577 group=1
sec idxOff= 29628 nIdx= 17484 vtxOff=0 nVtx=25344 matSlot=2002760751 group=2
...（22 个，全部 vtxOff=0 nVtx=25344）
```

躯干 25 个 section 同样：全部 `vtxOff=0 nVtx=24424`，`group == matIdx`。

**结论：一个 LOD mesh = 一个共享顶点池 + 按材质切分的连续索引区间。**
`section` 是**材质批**，不是装饰件。所以 `NumIndices = 0` 关掉的是**一整个材质**，
不是一件装饰。

失败模式正好是用户指出的两种：

* 手套和袖套共用一个材质 → 关手套会连带关掉袖套；
* 饰带和它上面的金属小件是两个材质 → 关饰带会留下金属件。

**材质既不是装饰的超集也不是子集，因此任何"按材质分组"的方案都不成立。**

装饰件的身份在产物里**完全不存在**，只存在于源模型（Source `.mdl` 的
bodygroup / mesh 结构）里。要恢复它，必须在转换时记录"每个源网格贡献了哪些
三角形/顶点"，而不是事后从产物反推。

## 结果五：材质不构成划分，必须显式标记

把每个 section 的 `MaterialSlotId` 通过 `UnitMaterialBinding(SectionId, MaterialId)`
解析成 64 位材质 ID（`unitprobe --sections`）：

```
matId=0xa810ff3628961b39 -> matIdx 4, 9, 14, 20
matId=0xad7911554fbb151f -> matIdx 7, 12, 18
matId=0xa2c36ab8b34fa73f -> matIdx 8, 13, 19
matId=0xa8a8c8c946b13cf2 -> matIdx 11, 15, 21
matId=0xae334bffabcbe65c -> matIdx 10, 16
其余 7 个各出现 1 次
```

**22 个 section 只有 12 个不同材质。** 所以「section → 材质 → 装饰」这条链不成立，
一个材质跨了 3–4 个 section。

但重复出现的位置近乎等距（4/9/14/20、7/12/18、8/13/19、11/15/21），
说明 section 是**按装饰分块、块内按材质切**：`section = (装饰 × 材质)` 对，
装饰之间连续。而连续两件装饰共用同一材质时，边界**不可判定**。

结论：**装饰边界必须显式记录，不能从产物反推。**

构建侧其实已经知道：vpk2patch 的 manifest 里有 `groups[]`，每个 group 带
`material`（互不相同）、`slots`、`triangles`。例如 `lilium-sancta-re2310.manifest.json`
11 个 group / 11 个不同材质，`lunalice-m8.manifest.json` 14 个 group / 14 个不同材质。
**但 group 是按整模型分的，不是按 unit 分的，而且 group 数（14）和 unit 的 section 数
（22/25/…）对不上**，所以还需要一步"group → 目标 unit 的 section 区间"的映射。

## 标记能插在哪、Lua 能不能读到

| 位置 | 能写吗 | 会进内存吗 | Lua 能读吗 |
|---|---|---|---|
| patch TOC 里新增一条自定义 `AssetKey` 条目 | ✅ `PatchArchiveAdditionalEntry` 支持任意 key | ❌ **不会**——游戏只加载被引用的资源 | 只能从**磁盘**读 patch，等价于读 sidecar |
| TOC 条目的 `Unknown1..4` 备用字段 | ✅ 我们自己的写入器 | ❌ 同上（条目级，不是 section 级） | 同上 |
| **unit 内部 `section.GroupIndex`** | ✅ | ✅ 就在 TOC 里，随 unit 进内存 | ✅ `ReadProcessMemory` 直接读 |
| 独立 sidecar 映射文件 | ✅ | — | ✅ Lua 本来就在读 `%LOCALAPPDATA%` 下的协议文件 |

**关键点：写进 patch 但没人引用的条目不会进内存**，所以它只能从磁盘读，
和 sidecar 没有本质区别，只是换了存放位置。

**唯一真正"随资源进内存、可被 Lua 直接读"的标记位是 `section.GroupIndex`。**
现在它等于 `MaterialIndex`（0..N），如果渲染器只是把它当绘制组序号用，
我们可以把它改成"装饰序号"——同一装饰的所有 section 写同一个值。
那样 unit 就自带装饰分组，不需要 sidecar。

**风险**：它当前恒等于材质序号，说明很可能是材质分组的一部分，改动可能破坏材质绑定。
必须先测。

### 建议的组合

1. **先用 sidecar 映射**（零风险，立刻可用）：构建期产出
   `装饰 → [(section 字节偏移, 原 NumIndices)]`，Lua 读它。
2. **同时验证 `GroupIndex`** 能否当自描述标记；能的话再把 sidecar 降级为校验用。

## 两条可行的设计，代价不同


前提都一样：**装饰身份必须由构建期从源模型记录下来**，产物里没有。

### 设计 A：按 (装饰 × 材质) 切 section

把每个材质批再按装饰切开，于是每个 section 只属于一件装饰。

* 运行期：把该装饰所有 section 的 `NumIndices` 置 0。**改的是 TOC，最可能被读取。**
* 代价：**draw call 增加**。当前头盔 22、躯干 25；切完取决于"一个材质被几件装饰共用"，
  最坏情况是每个装饰都用到每个材质。总量可能从 98 涨到 200–300。
* 这是**构建期**的改动，运行期不需要额外工作。

### 设计 B：保持材质批，按顶点区间退化

装饰在源模型里是独立网格，合并时顶点本来就按源网格顺序追加，
所以每件装饰天然占据一段连续顶点区间。记下 `装饰 → (vtxStart, vtxEnd)` 即可。

* 运行期：把该区间的**顶点位置全部塌到同一个点**，三角形面积归零 → 不产生像素。
* 代价：**draw call 不变**；但每次切换要写 `顶点数 × stride` 字节
  （例如 5000 顶点 × 44 B ≈ 220 KB），一次性，不在帧循环里。
* 风险：**依赖 CPU 侧顶点数据在改动后会被重新上传到显存**。这一条未验证。

### 为什么设计 B 比"改索引"更干净

索引在材质批内是按材质交错的，一件装饰的三角形散落在多个 section 的区间里，
按索引隐藏要做大量离散写。而按顶点退化**根本不需要知道索引**——
三角形引用了那些顶点，顶点塌了三角形自然退化。

## 决定性的未知（必须先测）

两条设计都卡在同一个问题上：**运行期改 unit 的 CPU 侧数据，渲染会不会跟着变。**

* 我们已知"改 armor registry blob 在运行期生效"，但那块是注册表，不是 unit 资源。
* 若渲染器在加载时就把 section 的绘制参数、顶点数据缓存到显存并且不再回读，
  那么 A 和 B **都不成立**，只能回到"构建期生成预设组合"。

所以下一步不是写代码，而是**在游戏里改一个 section 的 `NumIndices` 看有没有反应**。
这是最小的验证：改 4 个字节，截一张图。

## 需要修正的旧结论

* `code-patch-via-lua-plugin-20261017.md:79` 的
  "Lunalice 模型是 38 件身体 + 5 件头盔 = 43，纯数据侧放不下"
  —— 43 是**源模型网格数**，转换时已经合并进 7 个 unit。数据侧放得下。
* `armor-piece-capacity-observations-20261016.md` 的 C3（43 件）是**合成压力配置**，
  不是真实内容。真实内容 15 件。
* 容量扩容（3 → 8 bank）**不是当前需求的必要条件**。
* **本文早前写的"section 就是装饰开关"是错的**，已在上文更正为"section 是材质批"。

