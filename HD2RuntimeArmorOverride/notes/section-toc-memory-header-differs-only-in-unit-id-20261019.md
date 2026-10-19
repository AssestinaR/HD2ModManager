# 内存里的 unit TOC 只在 `+0` 一处与文件不同（2026-10-19）

## 一句话

内存副本与文件逐字节相同，**唯一差异是前 8 字节**：文件那里是 0，
内存那里是 **unit 自己的 FileId**。所以运行时不但能自己走 TOC，
还能用这 8 个字节直接定位任意一个 unit 的副本——不再需要"section 记录签名"。

## 怎么测的

只读探针（`probes/section_layout_probe.lua`，由
`scripts/make_section_layout_probe.py` 从当前部署的 patch 现算），
在游戏进程内定位 TOC 副本，把前 128 字节和结构头写进
`ArmorRegistry\section-layout.status`；随后用 Python 从 patch 里切出同一 unit 的
TOC 切片，逐字节比对。

* 部署包：`E:\mod\1061927f24504409a8753788f5b172ee\9ba626afa44a3aa3.patch_0`（592887 字节）
* unit：`0xaf431352d9b95e83`，TOC 切片在 patch 内偏移 `0x77b92`，长度 25830
* 内存基址：`0x2f3e98e0080`

## 结果

```
前 0x80 字节里不同的位置：8 个，正好是 [0..7]
文件   header[0..7] = 00 00 00 00 00 00 00 00
内存   header[0..7] = 83 5e b9 d9 52 13 43 af   ← 0xaf431352d9b95e83 小端
其余 0x78 字节完全一致，包括 0x5c / 0x64 / 0x70
```

三个结构偏移两处相等：

| 偏移 | 文件 | 内存 |
|---|---|---|
| `0x5c` StreamInfoOffset | `0x4bbe` | `0x4bbe` |
| `0x64` MeshInfoOffset | `0x50fe` | `0x50fe` |
| `0x70` MaterialsOffset | `0x5fb2` | `0x5fb2` |

走一遍 mesh 表，两处也一致：

```
meshes=5   lod=4,3,2,1,0   nsec=22(各级相同)   secOff=216
mesh0 首节 numIndices=26556  groupIndex=0     （探针与文件都读到 26556）
materials=110
VERDICT=PASS
```

## 为什么这条比"签名定位"更好

交接文档里的定位办法是：扫 section 记录的 48 字节签名，`基址 = 命中地址 - 0x6e6c`。
那个 `0x6e6c` 是**某一次构建**里节表的位置，换个包就失效——本次就因为部署包换了，
旧签名一个都没命中。

现在有了更强的特征：**`FileId(8 字节) + 8 个零字节`**，就落在 TOC 起点。
定位一个 unit 的副本变成：

```
1. 在私有堆区里找 FileId 那 8 个字节（其后 8 字节应为 0）
2. 校验：u32(base+0x64) 指向的 mesh 表数量在 1..64，且每个 mesh 的节表落在合理范围
3. 校验通过即认为找到
```

这个特征与包无关、与构建无关，任何 unit 都能用同一套代码定位。

## 顺带挖到一个会静默出错的坑：Lua 的 double 截断 64 位材质 ID

材质表布局（`UnitMeshReader.ReadMaterials`）：`u32 数量` → `数量×u32 sectionId` →
`数量×u64 materialId`。真实值：

```
materialsOffset=0x5fb2 count=110
values 起点=0x616e（不是 8 字节对齐）
前三个 materialId = 0xa47787804c45e1a9 / 0xa648fd251c740b12 / 0xa9a062cc3252d161
```

而探针用 `low + high*4294967296` 拼出来的同一批值是
`0xa47787804c45e000 / 0xa648fd251c740800 / 0xa9a062cc3252d000`——
**低位被截断了**。原因是 Lua 5.1/LuaJIT 的 number 是 double，只有 53 位精度，
`high*2^32` 一旦超过 2^53，低位就悄悄丢掉（本例粒度约 0x800）。

它不报错、不抛异常，只会让"指纹永远对不上"变成一个查不出原因的谜。
**规矩：64 位值一律按字节拼 hex，绝不经手 Lua number。**
`armor_section_layout.lua` 里的 `hex64` 就是这么写的。

## 边界（不要外推）

* 只测了 **1 个 unit / 1 个 patch / 非 composite**（`compositeRef = 0`）。
* 没有验证"同一个 unit 在内存里是否只有一份副本"。若有多份，取第一个校验通过的即可，
  但那时"改哪一份生效"需要另行确认。
* 没有验证 `+0` 处放 FileId 是否对**所有** unit 都成立（只有一条样本）。
  所以运行时定位必须带结构校验，不能只认这 8 个字节。
* 重载后基址会变（本次与上次不同），必须每次写前复验、失效就重扫。

## 复现

```powershell
python HD2RuntimeArmorOverride\scripts\make_section_layout_probe.py `
  --patch <当前部署的 patch_0> `
  --install "%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_layout_probe\mod.lua"
# 在 MDL 里启用该 mod，进游戏，然后看
#   %LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\section-layout.status
# 末行应为 VERDICT=PASS
```

比对脚本：`tmp/compare_header.py`（一次性验证脚本，不入库）。
