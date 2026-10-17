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

## 结果四：真正缺的是"单独开关"，而 section 天生可单独开关

`UnitMeshSectionInfo` 的字段：

```
Offset, MaterialIndex, MaterialSlotId,
VertexOffset, NumVertices, IndexOffset, NumIndices, GroupIndex
```

**每个 section 有自己的 `NumIndices` / `NumVertices`，且 `Offset` 就是它自己的 TOC 偏移。**

所以"关掉一个装饰" = 把该装饰贡献的那些 section 的 `NumIndices` 写成 0。
不需要合并、不需要扩容、不需要新键、不需要改代码段。

唯一缺的是**映射表**：`装饰名 → 它在该 unit 里贡献的 section 索引列表`。
这张表在**构建期**就有——合并的时候是我们自己把源网格写进去的。

## 因此方案变成这样

| 阶段 | 做什么 | 频率 |
|---|---|---|
| 构建期（管理器） | 合并时记录每个装饰贡献的 section 列表，产出开关表 | 装 mod 时一次 |
| 运行期（Lua） | 读玩家的开关，按表把对应 section 的 `NumIndices` 置 0 或还原 | 每次切换，微秒级 |

- 键数：**7**（远低于 18）
- 顶点数据：**1 份**（就是现在这份，没有翻倍）
- 切换要重启吗：**不要**
- 改代码段吗：**不改**
- 会掉帧吗：**不会增加任何 draw**——关掉的 section 本来就不再提交

## 需要修正的旧结论

* `code-patch-via-lua-plugin-20261017.md:79` 的
  "Lunalice 模型是 38 件身体 + 5 件头盔 = 43，纯数据侧放不下"
  —— 43 是**源模型网格数**，转换时已经合并进 7 个 unit。数据侧放得下。
* `armor-piece-capacity-observations-20261016.md` 的 C3（43 件）是**合成压力配置**，
  不是真实内容。真实内容 15 件。
* 容量扩容（3 → 8 bank）**不是当前需求的必要条件**。

## 待验证（进游戏才需要）

1. 把某个 section 的 `NumIndices` 置 0，那件装饰是否真的消失（其余不变）。
2. 改回原值是否恢复。
3. 是否需要同时把 `NumVertices` 置 0，或渲染器只用 `NumIndices`。

这三条都是**纯数据写入**，和已经验证可用的"写 armor registry blob"同一类。
