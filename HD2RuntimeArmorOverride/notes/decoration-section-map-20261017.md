# 装饰开关表：schema、生成方式、以及它为什么成立（2026-10-17）

## 一句话

`tools/unitprobe --manifest ... --groups ... --emit-map ...` 产出一份 sidecar JSON，
里面是**每个装饰在 unit TOC 里要改的 dword 偏移和原值**。运行期把那些 dword 写成 0
就隐藏该装饰，写回原值就恢复。**不需要扩容、不需要插标记、不需要改代码段。**

## 为什么 section 就是装饰边界（实测，不是推断）

对 `E:\tmp\lilium-final` 这套构建（vpk2patch 产物 + manifest）：

```
sections 11   manifest groups 11
#   group                   tris_manifest  tris_section  material
0   manuka_face                     9290          9290   aa6b1bd624443008  OK
1   _13                            13186         13186   abbf113f979054e5  OK
2   manuka_body                    20118         20118   af340f7553ae5906  OK
3   ribbon_1                         822           822   aadf05da56ef08cb  OK
4   gray                           23010         23010   af1e9fd6799cf9da  OK
5   gray1                          37431         37431   ada86765fee4f7b5  OK
6   mat_diamond_b                    920           920   a0282f52c0711719  OK
7   mat_ornament_pearl             13304         13304   a15a87df340bb5d6  OK
8   mat_dress_cloth                27525         27525   a3545af6a33991cc  OK
9   _01_1                           3322          3322   aec98d599c197f76  OK
10  glove_manuka_white              5138          5138   a36f928eed5e1ead  OK
exact matches: 11 / 11
```

**顺序相同、三角形数逐个相等、材质 ID 逐个相等。** 我们自己的构建管线里
一个 group 恰好落成一个 section，所以 section 边界就是装饰边界。

> 注意：作者原始包（`Chiffon-Lunalice-...`）不是这样——它有 22 个 section 但只有
> 12 个材质，一个材质跨 3–4 个 section。**这个 1:1 性质只对我们自己的构建成立**，
> 所以开关表必须由构建期产出，不能对任意包反推。

## 材质不能代替装饰

同一份作者包里，`0xa810ff3628961b39` 出现在 matIdx 4/9/14/20，`0xad7911554fbb151f`
出现在 7/12/18。一个材质跨多个装饰、一个装饰也可能跨多个材质，两个方向都不成立。
**所以"按材质分组"这条捷径被实测否掉。**

## 语义分组：饰带 + 它的金属件

manifest 按材质切，但玩家想的是"饰带"（含上面的金属小件）和"手套"（哪怕和袖套同材质）。
这个决定只有人能做，所以分组是**显式输入**：

```json
{ "decorations": [
    { "name": "ribbon_with_studs", "members": ["ribbon_1", "mat_ornament_pearl"] },
    { "name": "glove",             "members": ["glove_manuka_white"] }
] }
```

产出：

```
ribbon_with_studs  tris=14126  toggles/unit=8  members=[ribbon_1, mat_ornament_pearl]
    lod=0 mesh=3 sec=3 off=0x00010a54 nIdx=2466
    lod=0 mesh=3 sec=7 off=0x00010ab4 nIdx=39912
    lod=1 mesh=2 sec=3 off=0x000108a0 nIdx=2466
    ...（4 级 LOD × 2 个成员）
```

**必须覆盖全部 LOD**，否则关掉的件会在别的距离上重新出现。

## 字段偏移的出处

section 记录 24 字节（`UnitMeshReader.cs:679-686`，composite 路径 `:415-422` 相同，
`EnsureRange(data, cursor, count * 24)` 印证）：

```
+0  materialIndex  +4  vertexOffset  +8  numVertices
+12 indexOffset    +16 numIndices    +20 groupIndex
```

**要改的是 `+16`。** 偏移是**相对该条目自己的 TOC 切片**（`itemStart + relativeOffset`），
不是相对 patch 文件。

## 自校验

`--emit-map` 会把每个记录的偏移从对应 unit 的 TOC 里**回读**一遍，比对数值：

* 未分组：`offsetsVerified=88 mismatched=0`（11 装饰 × 2 unit × 4 LOD）
* 分组后：`offsetsVerified=40 mismatched=0`（3 装饰，其中两个各 8 位）

只要 `mismatched=0`，表里每一个偏移都确实指向它声称的那个 dword。

## schema

```jsonc
{
  "schema": "hd2mod.unit-section-map/1",
  "patch": { "path": "...", "sha256": "...", "size": 164404, "gpuResourcesSha256": "..." },
  "manifest": { "path": "...", "sha256": "...", "groupCount": 11 },
  "sectionRecordSize": 24,
  "numIndicesOffsetInSection": 16,
  "offsets": "tocRelative",          // 相对 unit 的 TOC 起点
  "units": [ { "fileId": "0x…", "slot": "Torso", "bodyType": "Slim", "meshes": [...] } ],
  "decorations": [
    { "name": "ribbon_with_studs", "source": "grouping",
      "materialId": "0x…,0x…", "triangles": 14126,
      "members": ["ribbon_1", "mat_ornament_pearl"],
      "targets": [ { "fileId": "0x…", "slot": "Torso", "bodyType": "Slim",
                     "sections": [ { "lod": 0, "meshIndex": 3, "sectionIndex": 3,
                                     "numIndicesOffset": "0x00010a54",
                                     "numIndices": 2466 } ] } ] } ]
}
```

`patch.sha256` 和 `gpuResourcesSha256` 用来防"表是旧的、patch 是新的"。

## 还没解决的问题

1. **运行期怎么拿到 unit 的 TOC 基址。** 表里的偏移是 TOC 相对的，Lua 必须先找到
   该 unit 资源在内存里的起始地址。这是下一个未知。
2. **渲染器是否尊重 `NumIndices`。** 若它在加载时把绘制参数缓存到显存并不再回读，
   整条路失效。这是**第一个要实测**的东西——改 4 个字节，截一张图。
3. **`GroupIndex` 能否当自描述标记**（现在恒等于 `MaterialIndex`）。能的话可以把
   sidecar 降级成校验用。
