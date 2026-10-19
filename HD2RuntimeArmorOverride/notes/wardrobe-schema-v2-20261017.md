# 衣柜掩码 schema v2：定稿（2026-10-17）

> 阶段 1 的产物。这份文档是**契约**：CLI、管理器编辑器、运行时 Lua 三方都按它实现。
> 已在本机真实数据上验证的部分标了「实测」；仍未定的标了「待拍板」。

---

## 1. 一句话

把"哪些 section 要隐藏"从**协议**里拆出来，放进注册表目录下的
`wardrobe.masks`，只存"禁用"、按 `(unit, section 下标, 内容指纹)` 寻址，
分「档案级来源掩码」和「映射目标独立掩码」两层，目标默认实时继承来源。

---

## 2. 为什么单独成文件，而不是塞进 `armor-registry.protocol`

协议是 `key=value` 行格式，有 **64 KiB 硬上限**，而且有**三份独立实现**
（`armor_registry_protocol.lua`、`HD2ModCli/CliArmorRegistryProtocol.cs`、
`HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs`）。
把掩码塞进去意味着三份解析器同时改、同时受 64 KiB 挤压。

掩码的规模不是小数目：7 个槽位 × 每 unit 11~40 个 section，
全关就是 280 行、约 17 KiB；再乘"目标独立掩码"就顶到上限了。

**所以**：协议保持原样，只当"权威 + 重载触发器"；掩码另开一个文件，
**只有两份实现**（C# 写入/读取、Lua 只读），上限单列 1 MiB。

---

## 3. 位置与写入时序

```
%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\
  ├─ armor-registry.protocol      协议（不变）
  └─ wardrobe.masks               ← 本文件新增
```

**时序是硬约束**（实测依据：`auto_refresh.published(revision,changed)`
要求 revision **严格递增**，且 tick 时 `host.revision()` 必须等于待执行的 revision）：

```
1. 先原子写 wardrobe.masks（临时文件 + 替换）
2. 再把协议的 revision 加一
3. 运行时看到新 revision → 读掩码 → 写 NumIndices → 触发一次重载
```

反过来（先 bump 协议再写掩码）会出现"重载用的还是旧掩码"的窗口。
**只改掩码不改协议 revision = 什么都不会发生**，这是有意的：重载必须显式。

---

## 4. 文法

行格式，`#` 开头是注释，空行忽略。键名区分大小写，值为 `,` 分隔。

```
schema=1
revision=<0..2147483647>
enabled=true|false

# ① 档案级（来源）掩码：只记录"禁用"
off=<unit>,<sectionIndex>,<fingerprint>

# ③ 映射目标独立掩码：只记录"禁用"
off_target=<targetItemId>,<unit>,<sectionIndex>,<fingerprint>

# 目标掩码模式；缺省 inherit
mask_mode=<targetItemId>,inherit|override
```

字段定义：

| 字段 | 形式 | 说明 |
|---|---|---|
| `unit` | 16 位小写 hex，**不带 `0x`** | unit 资源的 FileId，如 `cae35ee15168145f` |
| `sectionIndex` | 十进制 0..65535 | **该 unit LOD0 mesh 的 section 列表下标**（0 起） |
| `fingerprint` | `<materialId>-<triangles>` | 见 §5 |
| `targetItemId` | 十进制 uint32 | 映射目标护甲 item id |

**确定性输出**：`off` 按 `(unit, sectionIndex, fingerprint)` 排序，
`off_target` 按 `(targetItemId, unit, sectionIndex, fingerprint)` 排序，
`mask_mode` 按 `targetItemId` 排序。同样的语义必须产出逐字节相同的文件——
否则"内容没变"会被误判成"变了"。

---

## 5. 指纹

```
fingerprint = <materialId> "-" <triangles>
materialId  = 16 位小写 hex（不带 0x），section 的 MaterialId
triangles   = section.NumIndices / 3，十进制
```

材质缺失（`materialIndex` 越界 / 该槽位无材质 / `MaterialId` 解析出来是 0）时写成 `none`：

```
fingerprint = none-<triangles>
```

> 实测：`E:\tmp\vanilla-unit-test\unit_0x143a0d93d14d6587.toc` 这个原版 unit，
> 每个 mesh 的 `materialSlotIds[0]` 都是 **0**，拿 0 去查材质绑定表查不到，
> 所以原版 unit 上大量 section 的指纹会是 `none-<triangles>`。
> 这不影响掩码——掩码只作用在我们自己构建的 unit 上，那些 slot 是解析得出来的
> （`cae35ee15168145f` 的 11 个 section 全部拿到了材质 ID）。
> 但**指纹必须允许退化**，不能因为材质缺失就拒绝整份文件。

**为什么不哈希**：这个值的作用是"检测 section 内容漂移"，不是安全边界。
直接写可读原文的好处是——三份语言（C# / Lua / Python）都不需要实现同一个哈希，
也就不会出现"端序写反了但看起来像对的"这种事故。
（`MaterialId` 是 64 位、按小端读，Lua 侧要按 `%08x%08x` 拼高低双字——
所以**指纹一律按字符串拼接，绝不做数值运算**。）

实测样本（`E:\tmp\lilium-final`，unit `cae35ee15168145f`，LOD0 mesh 3）：

| section | materialId | numIndices | triangles | fingerprint |
|---|---|---|---|---|
| 0 | aa6b1bd624443008 | 27870 | 9290 | `aa6b1bd624443008-9290` |
| 3 | aadf05da56ef08cb | 2466 | 822 | `aadf05da56ef08cb-822` |
| 10 | a36f928eed5e1ead | 15414 | 5138 | `a36f928eed5e1ead-5138` |

---

## 6. 三层掩码与生效规则

```
① 档案级来源掩码      off=...           惰性、与 mod 无关、换 mod 后仍保留
② 映射目标：默认继承    （不落盘）        实时继承 ①，来源改了目标立刻跟着变
③ 映射目标：独立掩码    off_target=...    生效时**不再**继承 ①
```

对某个映射目标 `T`：

```
mask_mode[T] == override  →  只用 ③（T 名下的条目）
否则                       →  只用 ①
```

**只存"禁用"**：缺失 = 启用。新出现的 section 自动可见，
不会被一条旧掩码连坐。

**切到 override 时必须物化**：把切换瞬间的继承结果写进 ③。
否则一按"独立"就等于"全部启用"，用户看到的是所有件突然出现。
这条由编辑器负责，文件格式不表达它。

---

## 7. 应用规则

1. **LOD 一致性前提**。掩码的 section 下标取自 LOD0 mesh。
   应用时只对该 unit 中 **section 数量与 LOD0 相等**的 LOD mesh 生效；
   数量不等的 LOD **跳过并记警告**。

   > 实测依据：我们自己用 vpk2patch + manifest 出的包，4 级 LOD 都是 11 个 section、
   > 顺序一致（`cae35ee15168145f` / `9841b16544c8fbae`）。
   > 但**原版 unit 不是这样**：`E:\tmp\orig-patch` 里同一个 Torso，
   > lod3 只有 1 个 section、lod2 有 2 个。所以这条前提不能省。

2. **指纹不符就跳过该条**，不报错、不中断应用。
   作用：换 mod 后把"关错部件"降级为"设置静默失效"。

3. **整体禁用判定**：一个 unit 的所有 section 都被禁用 ⇒ 该 unit 视为禁用
   ⇒ **不注册它的 Piece**。（显存收益尚未实测，见交接文档 §7。）

4. **写入是双向的**：禁用写 `0`，启用写回映射表里的原值。
   原值来自构建期产出的 section 映射表，不在本文件里。

---

## 8. 校验与错误

解析期（任何一条不满足即**整份拒绝**，不做部分接受）：

| 条件 | 错误 |
|---|---|
| 文件 > 1 MiB | `wardrobe.masks 超过 1 MiB 上限` |
| 缺 `schema` / `revision` / `enabled` | 分别报缺 |
| `schema` 不是 1 | 不支持 |
| 未知键 | `未知字段：<key>` |
| `unit` 不是 16 位 hex | `unit ID 无效` |
| `sectionIndex` 越界 | `section 下标越界` |
| `fingerprint` 不匹配 `^([0-9a-f]{16}\|none)-\d+$` | `指纹格式无效` |
| 同键重复 | `重复的掩码条目` |
| `mask_mode` 重复 | `目标的掩码模式重复` |

应用期（跳过而非拒绝）：

| 条件 | 行为 |
|---|---|
| 指纹与内存里的 section 不符 | 跳过该条，计入 `skipped_fingerprint` |
| unit 不在内存 / 未加载 | 跳过该 unit，计入 `skipped_unit` |
| LOD section 数量不一致 | 跳过该 LOD，计入 `skipped_lod` |

**跳过必须计数并可见**。静默跳过会让用户以为"勾了但没生效"却查不到原因。

---

## 9. 兼容与迁移

* 文件**不存在** ⇒ 等价于"全部启用"。旧库、旧协议完全不受影响。
* `enabled=false` ⇒ 掩码整体不生效，但内容保留（用户可随时打开）。
* 旧 mod 迁移（交接文档 §4.6）：每个 unit 贡献一个装饰、拥有该 unit 全部 section。
  迁移只写元数据，掩码文件**初始为空**——旧行为本来就是全可见。

---

## 10. 待拍板

1. **在"继承模式"的目标上编辑，写哪里？**
   - A：自动把该目标切成 override 并物化（推荐：不污染来源，但用户会看到模式变了）
   - B：直接改档案级 ①（共享，影响所有继承该来源的目标）
   两种都实现得了，但 UX 差别很大。

2. **档案级掩码是否要按护甲 item 分桶？**
   现在按 `(unit, sectionIndex, fp)` 全局寻址，前提是"不同护甲不会共用同一个 unit"。
   实测里成立，但没在整库上验证过。若出现共用，两个护甲会共享掩码。

3. **`sectionIndex` 以 LOD0 为锚**是否会踩到"一个 unit 每级 LOD 有多个 mesh"的情况。
   现有样本（含原版）都是每级 1 个视觉 mesh。若真出现多 mesh，需要把
   mesh 序数也编进键里——那会让键变长，值得先确认有没有这种 unit。

---

## 11. 与既有文件的关系

| 文件 | 角色 | 谁写 |
|---|---|---|
| `armor-registry.protocol` | 映射关系、模型、Piece 覆盖、target_mode、revision | CLI / 管理器 |
| `wardrobe.masks` | section 显隐（本文件） | CLI / 管理器 |
| `<mod>/sections.json` | 构建期 section 映射表（偏移、原值、材质、三角形） | 构建管线 |
| `runtime.status` | 运行时状态 | 运行时 Lua |

`wardrobe.masks` **不记录偏移**。偏移是构建产物，属于 `sections.json`；
掩码只记录语义身份，两者靠 `(unit, sectionIndex, fingerprint)` 对齐。
这样重建 mod 后掩码要么继续有效（section 内容没变），要么安全失效（指纹变了）。

---

## 12. 运行时怎么拿到偏移（阶段 3 的地基，已离线验证）

掩码里没有偏移，运行时必须自己从内存里的 unit TOC 走出结构。
**不需要 `sections.json`**——这决定了阶段 2 的产物只服务于编辑器和 CLI，
不必部署到运行时能读到的地方。

TOC 布局（取自权威读取器 `UnitMeshReader.cs`，不是猜的）：

```
toc  + 0x5c   StreamInfoOffset
toc  + 0x64   MeshInfoOffset        → u32 数量，随后 数量×u32 相对偏移
toc  + 0x70   MaterialsOffset       → u32 数量，随后 数量×u32 sectionId，随后 数量×u64 materialId

mesh = toc + MeshInfoOffset + 相对偏移
mesh + 56     lod (int32)
mesh + 104    numMaterials
mesh + 120    numSections
mesh + 124    sectionsOffset        节记录相对 mesh 起点
mesh + 132    materialSlotIds[numMaterials]
section 记录（24 字节，绝对位置 = mesh + sectionsOffset）
  +0 materialIndex  +4 vertexOffset  +8 numVertices
  +12 indexOffset   +16 numIndices   +20 groupIndex
```

**离线验证**：对 `E:\tmp\vanilla-unit-test\unit_0x143a0d93d14d6587.toc` 用纯字节读取
（`tmp/walk_unit_toc.py`，不引用任何 C# 代码）走出 5 个 mesh，
lod 依次 `-1,3,2,1,0`、每个 1 个 section，节记录地址单调递增且全部落在 5696 字节内。
结构对得上，说明这条路可行。

**两个已知边界**：

1. **composite-backed unit**（`compositeRef != 0` / `StreamInfoOffset == 0`）的材质与 mesh
   数据在 composite 载荷里，上面这套偏移不适用。阶段 3 先只支持非 composite，
   遇到 composite 就跳过并计数。
2. **内存副本的头部是否与文件一致尚未实测**。交接文档 §3.3 记录的是"99.19% 相同，
   差异只在头部"——`0x5c / 0x64 / 0x70` 恰好都在头部。**这是阶段 3 第一个要测的东西**：
   在内存副本里读这三个偏移，与 `sections.json` 里的值比对，一致才能继续。
