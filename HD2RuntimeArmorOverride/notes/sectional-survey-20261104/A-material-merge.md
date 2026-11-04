# A. 追加时「共用材质会不会并段」——实测结论（2026-11-04）

> 方法：可运行探针 `E:\tmp\secprobe`，`<ProjectReference>` 指向仓库 `HD2ModAdaptation.csproj`。
> **全程调用产品代码**：`CanonicalDecorationUnitAppender.TryAppendLodFamily` 端到端跑到
> `CanonicalUnitRebuilder` 出 TOC 字节，再用 `UnitMeshReader` 把产出的 TOC **重新读回来**数 section。
> 没有改仓库任何产品代码、没有往仓库加文件、没有 commit。

## 1 一句话答案

**会并段。** donor 的某个 section 与目标 unit 用**同一个 `MaterialId`** 时，该段 `occurrence=0`
命中 `ResolveSdkTargetSlot` 的「复用目标槽」分支，拿到目标已有的 `MaterialSlotId`，
随后被 `MergeSectionsByMaterialSlot` 按 `(MaterialIndex, MaterialSlotId)` **无条件并进目标那一段** ——
边界丢失，几何还在，但再也不能单独开关。

设计稿 §2.2 有两处与实测不符，需要修正：

- **破口不是「一个 donor」，而是「每一个 occurrence 落在目标槽数组长度内的 section」。**
  两个 donor 共用目标材质时，**只有第 1 个** donor 的段被并，第 2 个反而拿到新槽（见 ③）。
- **并段是整张 mesh 范围的，不只作用于追加段。** `MergeSectionsByMaterialSlot` 收的是
  「目标段 + 全部追加段」的最终 mesh ⇒ 目标自己原本同槽的两段也会被并 ——
  **实测 ⑥：donor 用的是完全不同的材质，目标自己的两段照样 2 段并成 1 段**（4 个三角形）。

## 2 四组对照实测（端到端，非代码推断）

合成夹具：目标 unit 单 mesh、LOD 0、1 个 stream（`vec3_float`, stride 12）、1 个 BoneInfo；每段 2 个三角形。`occurrence` 语义由 `CanonicalUnitMaterialLayoutCompiler` 真实执行。

| 组 | 目标 unit | donor(s) | 最终 section 数 | 每段 MaterialSlotId | 每段三角形 |
|---|---|---|---|---|---|
| ① 对照：材质不同 | 1 段 / M1(0x111) | 1 段 / **M2(0x222)** | **2** | `77` \| `3920966529` | 2 \| 2 |
| ② 破口：共用目标材质 | 1 段 / M1(0x111) | 1 段 / **M1(0x111)** | **1** ❌ | `77` | **4**（2+2 已并） |
| ③ 两 donor 共用目标材质 | 1 段 / M1(0x111) | A: M1，B: M1 | **2**（部分并） | `77`（目标+A=4tri） \| `2260070994`（B=2tri） | 4 \| 2 |
| ④ 目标无该材质 | 1 段 / M1(0x111) | A: M3，B: M3 | **3** ✅ | `77` \| `1711142610` \| `2174436211` | 2 \| 2 \| 2 |

补充组（同样端到端）：

| 组 | 目标 | donor(s) | section 数 | 槽 | 三角形 | 说明 |
|---|---|---|---|---|---|---|
| ⑤a donor 自己两段共材质、目标也有 | 1 段 M1 | 2 段都 M1 | **2** | `77` \| `250744977` | 4 \| 2 | donor 第 1 段并进目标，第 2 段独立 |
| ⑤b 目标两槽都是 M1、donor 也 M1 | 2 段 M1（77,78） | 1 段 M1 | **2** | `77` \| `78` | 4 \| 2 | donor 仍走 `occurrence=0` → 并进槽 77 那段 |
| ⑤c 两 donor 两个全新材质 | 1 段 M1 | M2, M3 | **3** | 三槽互异 | 2/2/2 | 安全 |
| ⑤d 目标两材质、donor 共用第 2 个 | 2 段 M1,M2 | 1 段 M2 | **2** | `77` \| `78` | 2 \| **4** | 与槽 78 那段合并 |
| ⑥ **目标自己同槽两段**、donor 用**不同**材质 | 2 段同槽 77 | 1 段 M2 | **2** | `77` \| `1149343303` | **4** \| 2 | ❗目标自己两段被并，与 donor 无关 |
| ⑦ 同 ⑥ 但 donor 共用该材质 | 2 段同槽 77 | 1 段 M1 | **1** | `77` | **6** | 三段全并 |

**判定依据是重新解析产出的 TOC**：② 里只有一个 24 字节 section 记录，`numIndices=12`（4 个三角形），
`materialSlotIds=[77]`，`unitMaterials=[77->0x111,77->0x111]` ⇒ 几何没丢（12 个索引全在），
但**段数从 3 降到 1**。

> 一句话：**几何安全，section 边界不安全。** 这正好是「拆件」方案最在意的那一半。

## 3 探针在哪、怎么复现

```
E:\tmp\secprobe\
    secprobe.csproj   # ProjectReference -> E:\Data\source\repos\WpfApp1\HD2ModAdaptation\HD2ModAdaptation.csproj
    Program.cs        # 四组 + 补充组（端到端）+ L1..L4（layout 层）
    Bonus.cs          # 附加项：扫游戏本体 unit
    out\results.txt   # 上次运行的完整 ASCII 输出
```

复现（PowerShell，主程序约 5 秒；附加项扫描约 8 分钟）：

```powershell
cd E:\tmp\secprobe
dotnet run                    # 四组对照 + 补充组 + layout 层，同时写 out\results.txt
dotnet run -- bonus "D:\SteamLibrary\steamapps\common\Helldivers 2\data" 4000 4
```

夹具要点（`Program.cs`）：

- `BuildUnit` 造自洽最小 unit：1 mesh / 1 stream（`vec3_float`, stride 12）/ 1 BoneInfo / 1 TransformInfo（单位矩阵）。
- `BuildTocBuffer` 造 16 KB TOC 底稿，只写 `BoneInfoOffset=0x200 / StreamInfoOffset=0x400`；
  `CanonicalUnitRebuilder` 只重写自己拥有的偏移，这两个头字段不存在的话重读会报 `StreamInfoOffset is zero`。
- `avatarTransformInfo` 必须含 donor 的 palette bone hash，否则 `CanonicalTransformInfoExpander` 抛异常。
- 返回的 `PatchUnitMeshEditResult` 只有字节，所以用 `UnitMeshReader.Read(..., StructureOnly)` 重解析回来数 section。

`L1..L4` 是第二层证据（只跑 layout 编译器 + 反射调**真实**的私有 `MergeSectionsByMaterialSlot`）：

| 编号 | provenance | 合并前 | 合并后 |
|---|---|---|---|
| L1 | `UsesTargetUnitMaterialSlotLookup=true, PreferMeshLocalSlotReuse=true`，材质=目标材质 | 2 段同槽 77 | **1 段**（4 tri） |
| L2 | `UsesTargetUnitMaterialSlotLookup=false`，`PreferredTargetSlotId=0x9001` | 槽 77 / 36865 | **2 段**（保住） |
| L3 | `UsesTargetUnitMaterialSlotLookup=false`，`PreferredTargetSlotId=目标槽 77` | 2 段同槽 77 | **1 段**（仍然并） |
| L4 | 两个装饰段各给独立 `PreferredTargetSlotId` | 槽 77/36865/36866 | **3 段**（全保住） |

L3 很关键：**只把 `PreferMeshLocalSlotReuse` 关掉没用**（关掉后退到 unit 级 `targetSlotsByMaterial`，
`occurrence=0` 依旧命中目标槽）。真正起作用的是「让这一段的 `PreferredTargetSlotId` 与目标槽不同」。

## 4 规避方案：可行，代价是槽数

**「多个 slot 指向同一个 MaterialId」完全合法，现有代码已经在这么干。** 三份证据：

1. `bindingsBySlot` 是 `Dictionary<uint, ulong>`（slot → material），结构上不禁止多对一；
   `UnitMaterialBinding` 表本身就是「每个 section occurrence 一条 pair」的有序表。
2. 实测 ③/④ 里就出现了 `[77->0x111, 2260070994->0x111]` 与 `[1711142610->0x333, 2174436211->0x333]`：
   **两个槽、同一个 MaterialId**，产品代码自己分配的，TOC 能正常重读。
3. 仓库既有测试 `CanonicalUnitMaterialLayoutCompilerTests.Compile_PreservesDistinctTargetSlotsForTheSameSourceMaterial`
   断言的正是 `[(900, 0x222), (901, 0x222)]`。

**可行写法**（L2/L4 已跑通）：给追加段一个「每段唯一」的 `PreferredTargetSlotId` ——
`BuildMaterialProvenance` 对装饰段不再走 `UsesTargetUnitMaterialSlotLookup=true`，
改成按 `(SectionIndex, MaterialId)` 算稳定 hash 当 `PreferredTargetSlotId`
（碰撞时 `ResolveOutputSlot` 会让位，因为 `occupied` 里没有它）。

**代价**（按重要性排）：

- 材质槽数 +1/段，`UnitMaterialBinding` 表 +1 条/段（这张表本就按 section 逐条写，增量很小）。
- `CanonicalFinalMaterialLayout` 的 ordinal 按「不同 slot」计数 ⇒ `CanonicalAppendBoneCompiler` 生成的
  `BoneInfo.Remaps` 条数 = 槽数 ⇒ 每个多出来的槽都要一份 fake-index 表（`groupHashes.Count * 4` 字节）。
  一个 unit 若有 100 段就是 100 份 remap 表 —— 这是**真正的成本**，不是 slot id 本身。
- 与 SDK「一个 Blender 材质槽 = 一个 section」的惯例偏离更远（见 §7，游戏本体自己也不遵守）。

**但要清楚：这需要改产品代码。** `BuildMaterialProvenance`（`CanonicalDecorationUnitAppender.cs:178-180`）把 `PreferMeshLocalSlotReuse: true` 写死，没有任何开关能让调用方要求「每段独占槽」；L3 证明现成两个 flag 的任意组合都救不了这个 case。

## 5 设计稿 §2.2 要不要改写 —— 要

现文（第 126-153 行）的三行表基本正确，但结论句与「唯一破口」的定义要收紧，并补上两点实测事实。建议改成：

> ### 2.2 追加时 section 边界能不能保住
>
> `CanonicalDecorationUnitAppender.cs:189-197` 末尾无条件调用 `MergeSectionsByMaterialSlot`，
> 按 `(MaterialIndex, MaterialSlotId)` 分组把三角形并起来。**这是整张最终 mesh 的范围，
> 不只作用于追加进来的段。**
>
> 装饰段的 provenance 带 `UsesTargetUnitMaterialSlotLookup: true` + `PreferMeshLocalSlotReuse: true`
> （`:178-180`），走 `ResolveSdkTargetSlot(localTargetSlotsByMaterial, materialId, occurrence, …)`
> （`CanonicalUnitMaterialLayoutCompiler.cs:92-95`）。`occurrence` = 本 mesh 内该 materialId 第几次出现
> （`:84-89`）。`occurrence < 该材质的槽数组长度` ⇒ 复用目标槽；否则 `AllocateSlot(identity)`
> （`:146-148`），`identity` 含 `SectionIndex`（`:90, 203-204`）⇒ 每段唯一槽。
>
> | 情况 | 结果 |
> |---|---|
> | 两块几何材质不同 | 槽不同 ⇒ 段不合并 ⇒ 可独立开关 ✅ |
> | 材质相同、目标自己也没有这个材质 | 各走 `AllocateSlot` ⇒ 槽不同 ⇒ 不合并 ✅（实测：2 donor 共新材质 → 3 段） |
> | **材质相同、目标也有这个材质** | 该材质的第 k 段（k < 目标该材质的槽数）**复用目标槽** ⇒ 与目标那一段合并 ❌ |
>
> **实测（2026-11-04，`E:\tmp\secprobe`，端到端跑 `TryAppendLodFamily` 后重读 TOC）**：
> 共用材质时 1 段目标 + 1 段 donor → 最终 **1 段**（4 个三角形），几何完整但边界丢失；
> 两个 donor 共用目标材质是**部分丢失**（donor A 并入目标段，donor B 拿到新槽仍是独立段）。
> 另：目标自己就有同槽两段时，**任何** append 都会把它们并成一段（与 donor 材质无关）。
>
> **⇒ 结论：不共用同一 `MaterialId` 时能保住 section 边界；共用时会丢，丢的是「该材质在目标侧
> 已有槽位」的那几段。** 规避手段是「每段独占一个槽」——多个槽指向同一个 `MaterialId` 合法
> （`bindingsBySlot` 就是多对一，实测 ③/④ 已在这么用），代价是槽数与 `BoneInfo.Remaps` 条数随段数
> 线性增长。**这需要给 appender 加一个 "unique slot per section" 开关，现有两个 flag 都做不到。**

同时 §3.0 的「做法」建议加一句：判据不是「两段、两个 materialSlotId」，而是
「**每段三角形数 == 追加前该段三角形数**」—— 并段后段数下降但几何总量不变，只看总数会漏。

## 6 对「拆件」方案的影响面

**会中招的条件（三个同时成立）**：目标 unit 里已有某 `MaterialId` 的槽；donor 有 section 用**同一个
`MaterialId`**；且这个 section 在该材质上是「第 k 个、k < 目标槽数」。

| 情形 | 会不会中招 | 常见度 |
|---|---|---|
| donor 与目标是**同一 Mod 的不同 unit**，共用同一批材质资源（典型：一套衣服 7 个 unit 全用同一套 `Undergarment` 材质） | **会** | **高**。这正是「一个 legacy 大包拆成 7 槽」的主场景 |
| donor 用 `$no_draw` / `StingrayDefaultMaterial`（155175220） | 不会（resolver 对默认材质 `continue`，不产生 claim，段保留原槽） | 中 |
| donor 独立建模、自带新材质 | 不会 | 高 |
| 目标 unit 自己原本就有同槽多段（本体里存在，见 §7） | **会**，且**与 donor 材质无关**：只要这个 unit 跑过一次 append，目标自己的同槽段就被并（实测 ⑥） | 低但非零（本体 12/47358 张 mesh） |

**影响的性质**：

- 丢的不是几何，是「可寻址性」。产物仍能看、能加载，坏的是「勾一个组 → 关掉一块几何」这条链：
  被并掉的几何从此与目标几何**同生共死**，组表只能整段开关。
- 对 §4.4 的 `split`（存量升级器）**必须处理**：不检查的话用户会看到「我明明有 12 个部件，产物只有 9 段」。
  建议 `split` 预演里加**段数对账**（来源段数 vs 产物段数），不一致就报警并列出被并的材质。
- 对 §6「来源身份」的直接后果：被并掉的段在 `sections.json` 里**没有自己的记录**，组成员解析会 stale。
  所以来源身份必须允许「多个来源段 → 一个产物段」的**一对多**映射，否则升级器会静默丢成员。

**建议的产品口径**（最小代价）：① 追加路径加 `uniqueSlotPerSection` 开关（默认**关**，保持与 SDK 惯例一致）；
② `split`/拆件路径**打开它** —— 拆件的目的就是可寻址，多几个槽换边界，值；
③ `sections.json` 记「本段合并了几个来源段」，界面在组里明示「这块和主体绑在一起」。

## 7 附加项：注释里那句「non-SDK section table」有没有证据

`CanonicalDecorationUnitAppender.cs:185-188` 的注释说「保留同槽的多个 section 会产生 non-SDK section table」。
**这个说法被游戏本体数据否掉了。** 扫法（`Bonus.cs`，走产品 `GameDataPackageResolver` +
`GameDataUnitMeshReader`，`UnitMeshReadOptions.StructureOnly`，只读）：

```
packages=3567  units_read=7132  meshes_read=47358  read_failures=546
meshes_with_duplicate_material_index=12   units_with_duplicate_material_index=2
```

即 **7 132 个本体 unit、47 358 张 mesh 里，12 张 mesh 存在「同 mesh 内两个以上 section 共用同一个
`MaterialSlotId`」**，最多一张 mesh **5 段共用 1 个槽**：

```
e7765eef5f60acea unit=0x67dc32dca4f02d33 mesh=2 lod=6 sections=5 slots=[1677044429]              (mat0 x5)
e7765eef5f60acea unit=0x67dc32dca4f02d33 mesh=9 lod=0 sections=3 slots=[1161156669,1009533045]   (mat1 x2)
8abffb1f5544121b unit=0x74599e56f72f9d7e mesh=5 lod=9 sections=2 slots=[1677044429]              (mat0 x2)
```

**⇒ 结论**：同槽多段是引擎**真实会产出的形状**，不是「non-SDK」。那句注释的真实理由只可能是
「我们自己的 `BoneInfo.SetRemap` 按 material ordinal 建表，同槽多段会让 remap 与 section 不再一一对应」——
这是**本地实现的约束**，不是引擎的。建议注释改成事实描述并注明这条约束是本地选择。

> 附注：这同时说明「同槽多段」在游戏里能加载（本体资源）。但它**不能**证明「同槽多段可以被单独开关」——这 12 张是静态形状，没有证据说明它们的段边界被谁用过。这条只回答「是不是非法形状」：**不是**。另：仓库改动为零，只加了 `ProjectReference`（`HD2ModAdaptation` 会被正常编译进它自己的 `bin/obj`）。
