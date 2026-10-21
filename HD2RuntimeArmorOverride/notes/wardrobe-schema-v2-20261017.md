# 衣柜掩码 schema v2：定稿（2026-10-17，2026-10-19 修订）

> 阶段 1 的产物。这份文档是**契约**：CLI、管理器编辑器、运行时 Lua 三方都按它实现。
> 已在本机真实数据上验证的部分标了「实测」；仍未定的标了「待拍板」。

---

## 1. 一句话

把"哪些 section 要隐藏"从**协议**里拆出来，放进注册表目录下的 `wardrobe.masks`，
只存"禁用"、按 `(掩码集, unit, section 下标, 内容指纹)` 寻址，
**编译成扁平投影**：每个映射目标直接指向一个掩码集，运行时不做任何继承判断。

---

## 2. 两份数据，不要混淆

这是整个设计的地基。衣柜数据有**两份**，生命周期完全不同：

| | ① 未编译源文件 | ② `wardrobe.masks` |
|---|---|---|
| 位置 | **mod 库内**，随库走 | 注册表目录，随用户走 |
| 谁写 | 管理器（编辑即保存） | 管理器/CLI 在"应用"时编译生成 |
| 内容 | 视觉来源的禁用状态 + 每个目标"是否声明为例外" + 例外自己的禁用状态 | 只有运行时需要的东西：目标 → 掩码集 → 要隐藏的 section |
| 丢失后果 | 数据真的丢了 | 无所谓，重新应用一次即可再生成 |
| 需要继承关系 | **需要**（来源改了，继承它的目标立刻跟着变） | **不需要**，已解析成扁平投影 |

**关键点：编译产物里没有 inherit/override。** 继承是源文件里的活关系，属于管理器；
运行时只做两次查表。这样做的直接原因是**多个视觉来源**——不同目标可以映射到
不同的来源，全局一份"来源掩码"根本表达不了。

---

## 3. 为什么单独成文件，而不是塞进 `armor-registry.protocol`

协议是 `key=value` 行格式，有 **64 KiB 硬上限**，而且有**三份独立实现**
（`armor_registry_protocol.lua`、`HD2ModCli/CliArmorRegistryProtocol.cs`、
`HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs`）。
把掩码塞进去意味着三份解析器同时改、同时受 64 KiB 挤压。

掩码的规模不是小数目：7 个槽位 × 每 unit 11~40 个 section，全关就是几百行；
多个来源、多个目标再乘上去就顶到上限了。

**所以**：协议保持原样，只当"权威 + 重载触发器"；掩码另开一个文件，
**只有两份实现**（C# 写入/读取、Lua 只读），上限单列 1 MiB。

---

## 4. 位置与写入时序

```
%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\
  ├─ armor-registry.protocol      协议（不变）
  └─ wardrobe.masks               ← 本文件（编译产物）
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

## 5. 文法

行格式，`#` 开头是注释，空行忽略。键名区分大小写，值为 `,` 分隔。

```
schema=2
revision=<0..2147483647>
enabled=true|false

# 掩码集里的一条：某个集合要隐藏的某个 section
off=<setId>,<unit>,<sectionIndex>,<fingerprint>

# 目标 → 掩码集
target=<targetItemId>,<setId>
```

字段定义：

| 字段 | 形式 | 说明 |
|---|---|---|
| `setId` | 十进制 0..65535 | 掩码集编号，由编译方分配，只要求文件内自洽 |
| `unit` | 16 位小写 hex，**不带 `0x`** | unit 资源的 FileId，如 `cae35ee15168145f` |
| `sectionIndex` | 十进制 0..65535 | **该 unit LOD0 mesh 的 section 列表下标**（0 起） |
| `fingerprint` | `<materialId>-<triangles>` | 见 §6 |
| `targetItemId` | 十进制 uint32 | 映射目标护甲 item id |

**语义**：

* `target=T,set=S`：目标 T 隐藏掩码集 S 里的 section。
* **没有 `target` 行** = 该目标不隐藏任何 section。
* `target=T,set=S` 但 S 里一条 `off` 都没有 = **显式例外，且一条都不关**。

后两种情况运行结果相同（都不隐藏），但在源文件里是**不同的数据**：
用户可能为视觉来源关了一些件，却希望某个护甲全部可见。若把"没关"当成"没声明"，
这种意图就丢了。所以编译产物必须能表达"指向一个空集"。

**确定性输出**：`off` 按 `(setId, unit, sectionIndex, fingerprint)` 排序，
`target` 按 `targetItemId` 排序。同样的语义必须产出逐字节相同的文件——
否则"内容没变"会被误判成"变了"，白白触发一次重载。

---

## 6. 指纹

```
fingerprint = <materialId> "-" <triangles>
materialId  = 16 位小写 hex（不带 0x），section 的 MaterialId
triangles   = section.NumIndices / 3，十进制
```

材质缺失（`materialIndex` 越界 / 该槽位无材质 / `MaterialId` 解析出来是 0）时写成 `none`：

```
fingerprint = none-<triangles>
```

> 实测：`E:\tmp\vanilla-unit-test\unit_0x143a0d93d14d6587.toc` 这个 unit，
> 5 个 mesh 的 `materialSlotIds[0]` 全是 **0**，拿 0 去查材质绑定表查不到
> （表里确实有 3 条绑定，sectionId 是 `0x93fc934` / `0x5e0858a6` / `0x69adc318`），
> 所以它的指纹会退化成 `none-<triangles>`。
> 我们自己构建的 unit 没有这个问题：`cae35ee15168145f` 的 11 个 section
> 全部拿到了材质 ID（`3210307590` → `aa6b1bd624443008`）。
> 结论：**指纹必须允许退化**，不能因为材质缺失就拒绝整份文件；
> 退化后的指纹仍然能检测"三角形数变了"这一半。

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

## 7. 应用规则

1. **LOD 一致性前提**。掩码的 section 下标取自 LOD0 mesh。
   应用时只对该 unit 中 **section 数量与 LOD0 相等**的 LOD mesh 生效；
   数量不等的 LOD **跳过并记警告**。

   > 实测依据：我们自己用 vpk2patch + manifest 出的包，4 级 LOD 都是 11 个 section、
   > 顺序一致（`cae35ee15168145f` / `9841b16544c8fbae`）。
   > 但**原版 unit 不是这样**：`E:\tmp\orig-patch` 里同一个 Torso，
   > lod3 只有 1 个 section、lod2 有 2 个。所以这条前提不能省。
   >
   > 2026-10-19 补测：当前部署的那份 mod（`E:\mod\1061927f…`）是**作者原包风格**，
   > 5 级 LOD（4/3/2/1/0）× 22 个 section，各级数量一致。
   > 也就是说"数量一致"在作者包上也可能成立，但 22 个 section 对 12 个材质，
   > **装饰边界不可判定**——开关能用，但"哪几个 section 是一件装饰"只能靠人给。

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
| `schema` 不是 2 | 不支持（schema=1 是上一版，明确拒绝而不是半解析） |
| 未知键 | `未知字段：<key>` |
| `unit` 不是 16 位 hex | `unit ID 无效` |
| `sectionIndex` 越界 | `section 下标越界` |
| `fingerprint` 不匹配 `^([0-9a-f]{16}\|none)-\d+$` | `指纹格式无效` |
| 同 `(set, unit, index, fingerprint)` 重复 | `重复的掩码条目` |
| 同一 `target` 重复 | `目标的掩码集重复` |

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

1. ~~**`sectionIndex` 以 LOD0 为锚**是否会踩到"一个 unit 每级 LOD 有多个 mesh"的情况。~~
   **已定（2026-10-19，用户确认）：来源 mod 的多级 LOD 模型完全一致**——
   每级一个视觉 mesh、section 数量与顺序相同。所以键里不带 mesh 序数，
   实现按单 mesh 处理；万一遇到数量不一致的 LOD 会跳过并计数（见 §7.1）。
   原问题描述：
   现有样本（含原版、含作者包）都是每级 1 个视觉 mesh。若真出现多 mesh，需要把
   mesh 序数也编进键里——那会让键变长，值得先确认有没有这种 unit。

> 已定（2026-10-19）：
> * "继承模式下编辑写哪里"——**不落进编译产物**。继承是源文件里的活关系，
>   由管理器维护；编译时解析成扁平投影。编辑器在继承模式下改动时，是否顺手把
>   该目标切成"显式例外"，是编辑器交互问题，不影响本文件的格式。
> * "档案级掩码按什么分桶"——源文件里按**护甲**（视觉来源自己一份，每个例外自己一份）；
>   编译产物里按**掩码集**，天然支持多个来源，不存在共用 unit 导致串味的问题。

---

## 11. 与既有文件的关系

| 文件 | 角色 | 谁写 |
|---|---|---|
| mod 库内的源文件 | **权威**衣柜数据（含继承关系与编辑器结构） | 管理器（编辑即保存） |
| `armor-registry.protocol` | 映射关系、模型、Piece 覆盖、target_mode、revision | CLI / 管理器 |
| `wardrobe.masks` | 编译产物：目标 → 掩码集 → 要隐藏的 section | CLI / 管理器 |
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
mesh + 128    materialSlotIds[numMaterials]
section 记录（24 字节，绝对位置 = mesh + sectionsOffset）
  +0 materialIndex  +4 vertexOffset  +8 numVertices
  +12 indexOffset   +16 numIndices   +20 groupIndex
```

> **勘误（2026-10-19 实测）**：`materialSlotIds` 的起点是 `mesh + 128`，不是 `+132`。
> 自洽检查：`128 + numMaterials × 4 == sectionsOffset`（部署包上 `128 + 22×4 = 216`，
> 正是实测的 `secOff`）。按 `+132` 读会让每个 section 的材质都错位一格，
> 指纹随之全错——表现为"掩码里的指纹在运行时一条都匹配不上"。

**离线验证（两处，都不是推断）**：

1. 对 `E:\tmp\vanilla-unit-test\unit_0x143a0d93d14d6587.toc` 用纯字节读取
   （`tmp/walk_unit_toc.py`，不引用任何 C# 代码）走出 5 个 mesh，
   lod 依次 `-1,3,2,1,0`、每个 1 个 section，节记录地址单调递增且全部落在 5696 字节内。
2. 对当前部署的 `E:\mod\1061927f…\9ba626afa44a3aa3.patch_0` 走同一套偏移，
   得到 unit `0xaf431352d9b95e83`、`streamInfo=0x4bbe`、`meshInfo=0x50fe`、
   `materials=0x5fb2`、5 个 mesh（lod `4,3,2,1,0`）各 22 个 section、`secOff=216`。
   算术自洽：每个 mesh 记录 = 216（节表之前的字段）+ 22×24（节表）= 744 字节。
   m0 起点 `0x512a`、节表 `0x5202`；m1 起点 `0x5412 = 0x512a + 744`、节表
   `0x54ea = 0x5412 + 216`；依此类推到最后一张 `0x5da2`，全部落在 592887 字节的
   patch 内、节表不重叠。偏移错一个字段就会立刻表现为节表越界或 lod 乱值。

**两个已知边界**：

1. **composite-backed unit**（`compositeRef != 0` / `StreamInfoOffset == 0`）的材质与 mesh
   数据在 composite 载荷里，上面这套偏移不适用。阶段 3 先只支持非 composite，
   遇到 composite 就跳过并计数。
2. **内存副本的头部是否与文件一致尚未实测**。交接文档 §3.3 记录的是"99.19% 相同，
   差异只在头部"——`0x5c / 0x64 / 0x70` 恰好都在头部。**这是阶段 3 第一个要测的东西**。

### 12.1 待跑的实测（只读探针已就位）

探针由脚本从**当前部署的 patch 现算**，不硬编码：

```powershell
python HD2RuntimeArmorOverride\scripts\make_section_layout_probe.py `
  --patch <当前部署的 patch_0> `
  --install "%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_layout_probe\mod.lua"
```

它**不写内存**，只定位 TOC 副本、校验结构头、走一遍 mesh 表，把结论写到
`ArmorRegistry\section-layout.status`，末行是 `VERDICT=PASS|FAIL|NOT_FOUND`。

判定：
* `PASS` ⇒ 运行时自己走 TOC 这条路成立，阶段 3 可以开工。
* `FAIL` ⇒ 结构头与文件不一致，但探针会打印候选基址处的真实值与前 128 字节 hex，
  能直接看出差异落在哪几个字段上。
* `NOT_FOUND` ⇒ 签名没命中（多半是当前部署换了包，重新生成探针即可）。

**2026-10-19 已实测：`VERDICT=PASS`。** 详见
`notes/section-toc-memory-header-differs-only-in-unit-id-20261019.md`。要点：

* 内存副本与文件在前 128 字节里**只差 8 个字节**，就是 `+0` 处的 unit FileId
  （文件那里是 0）。`0x5c / 0x64 / 0x70` 两处完全相等。
* 因此定位一个 unit 的副本不再需要"节记录签名"（那个与构建绑定，换包就失效），
  只要找 **`FileId(8 字节) + 8 个零字节`**，再做结构校验即可。
* 走查器 `armor_section_layout.lua` 已按这套偏移实现，并在**离线**状态下用文件读取
  复现了游戏内探针的同一组数字（5 个 mesh、lod `4..0`、22 个 section、
  首节 `numIndices=26556`、`materials=110`），27 项检查全过。
* **本节早先写的 section 0 指纹 `a648fd251c740b12-8852` 是错的**（那是"section 1 的材质 +
  section 0 的三角形数"拼出来的，正是 `+132` 那个错位造成的）。按 `+128` 重算，
  unit `af431352d9b95e83` 的真实身份是：
  section 0 = `a47787804c45e1a9-8852`，section 1 = `a648fd251c740b12-1024`。
  构建期（`sections.json`）与运行时（Lua 走查器）现在对这两条**完全一致**，
  并且这份掩码已在游戏内实测生效。

**若这条结论被后续样本推翻**，阶段 3 退回"运行时读 `sections.json`"的方案，
那时阶段 2 就必须把映射表部署到运行时读得到的位置，架构要改。

---

## 13. 构建期工具链（阶段 4，已实现并实测）

三条命令，一条链：**编辑 → 编译 → 校验**。

```powershell
# 1. 从 patch 现算映射表（身份 + 指纹 + 各级 LOD 落点）
hd2mod wardrobe sections <patch_0> --output sections.json --json

# 2. 规格 → 掩码（指纹一律取自映射表）
hd2mod wardrobe compile wardrobe-spec.json --output wardrobe.masks --json

# 3. 重建之后必跑：这份掩码还跟这份构建产物对得上吗
hd2mod wardrobe validate --file wardrobe.masks --sections sections.json --json

# 4. 正式流程：权威工作区 + 注册协议 → 掩码（跨 mod 也行）
hd2mod wardrobe project --library <mod库> --output wardrobe.masks --json
```

### 13.1 规格文件 `hd2mod.wardrobe-spec/1`

规格是**人写的**，掩码是**机器写的**。分开的理由只有一个：指纹是内容哈希，
人写不出来，也不该写。规格里用 `(unit, section 下标)` 说"关掉哪几块"，
编译器去映射表里查指纹——所以指纹**不可能**凭空写错。

```json
{
  "schema": "hd2mod.wardrobe-spec/1",
  "revision": 3,
  "enabled": true,
  "sections": "sections.json",
  "patch": {
    "sha256": "280dc065f3c667f2…",
    "gpuResourcesSha256": "5b496331115ed76d…"
  },
  "sets": [
    { "id": 1, "off": [
      { "unit": "af431352d9b95e83", "section": 0 },
      { "unit": "af431352d9b95e83", "section": 1 }
    ] }
  ],
  "targets": [ { "itemId": 1745464610, "set": 1 } ]
}
```

* `sections` 相对**规格文件所在目录**解析（规格和映射表通常一起放在 mod 目录里）；
  `--sections` 可以覆盖它，相对当前目录。
* `patch` 可选。**写了就必须对得上**，否则拒绝编译——这是"这份掩码照着哪份 patch 做的"
  唯一机器可验证的形式。
* `off` 省略 = 空集。空集 + 有目标引用 = **显式例外**：目标声明了掩码集，但一块都不关。
  这是正常语义，不是错误。
* 未映射的字段**直接报错**（`JsonUnmappedMemberHandling.Disallow`）。把 `sections`
* 打成 `section` 会被默认行为静默忽略，然后编译出一份"看起来对、其实一条都没关"的掩码。
* 有 error 时**不写文件**。半份掩码会让运行时关错几何。

### 13.2 三条不变式

1. **指纹只有一个来源**：映射表。规格里就算写了也不采纳。
2. **一次报全**：编译/校验都把问题收集完再返回，不是一次只报一个。
3. **布局常量不符就拒绝读**：`sectionRecordSize=24`、`numIndicesOffsetInSection=16`、
   `offsets=tocRelative`。它们是运行时硬编码的假设，放过去就会写到别的字段上。

### 13.3 2026-10-19 实测（阶段 4 验收）

| 检查 | 结果 |
|---|---|
| `wardrobe sections` 对部署包 | 23 unit / 160 section，1 个 unit 节数不一致，0 跳过 |
| 与 `tools/unitprobe --emit-sections` 逐字段对比 | 23 unit × 160 section × 全部 toggles，**0 处差异** |
| `wardrobe compile` 用 `(unit, 下标)` 规格 | 产出与**当前游戏内生效**的 `wardrobe.masks` **逐字节相同**（仅行尾 CRLF/LF 不同） |
| `wardrobe validate` 对当前掩码 | 通过，`2/2` 条可解析，目标 1745464610 隐藏 2 个 section |
| 负向：篡改一个指纹 | `fingerprint_mismatch`，退出码 2，`1/2` 可解析 |
| 负向：规格 patch sha 写错 | `patch_sha256_mismatch`，退出码 2，**不写出文件** |
| 负向：引用不存在的 section | `section_not_in_unit`，退出码 2，**不写出文件** |

离线用例：`HD2ModCore.Tests/WardrobeSectionMapTests.cs`（11 项）、
`WardrobeMaskCompilerTests.cs`（20 项）。

---

## 14. 权威数据：`wardrobe.workspace.json`（阶段 4 后半，已实现存储层）

### 14.1 位置与身份

| | |
|---|---|
| 路径 | `<mod库>/.hd2modmanager/wardrobe.workspace.json` |
| 键 | **护甲 itemId**（不是 mod、不是 unit） |
| 写者 | 管理器（编辑即保存）/ CLI |
| 消费者 | 编译成 `<ArmorRegistry>/wardrobe.masks` |

与 `armor-registry.workspace.json` 同一个先例：权威数据在工作区，编译产物在运行时目录，
直接改编译产物会被下一次应用覆盖。

**为什么不放在 mod 目录里**：用户勾掉的是"这件护甲上第 N 块几何不该出现"，
这个意图与哪个 mod 提供模型无关。换 mod 之后它仍然成立——哪怕新 mod 的第 N 块
恰好是别的东西。这是用户明示接受的风险（"释放视觉来源即可全部清除"）。

**为什么这里不存指纹**：指纹是构建产物的属性。存进来就把权威数据绑死在一次构建上，
"换 mod 后仍保留"立刻失效。指纹只在编译时从当时的 `sections.json` 现查。

### 14.2 格式 `hd2mod.wardrobe/1`

```json
{
  "schema": "hd2mod.wardrobe/1",
  "revision": 4,
  "armours": [
    {
      "itemId": 820333399,
      "mode": "own",
      "units": [ { "unit": "af431352d9b95e83", "off": [0, 1] } ]
    },
    { "itemId": 1745464610, "mode": "inherit", "units": [] }
  ]
}
```

* **只存禁用**：`off` 里没有的 = 启用。新出现的 section 自动可见，不必逐个补写。
* **`mode` 必须显式**，不能靠"有没有条目"推断。原因：用户可能把某个目标**全部取消勾选**
  来表达"一块都不关、别继承来源"。那时条目是空的，而"没有条目"的含义是继承——
  两者必须能区分开。这个空集在编译产物里就是 `target=T,set=S` 配一个没有任何 `off`
  的集合（§7 的"显式例外"）。
* `mode = inherit` 与 `units` 里有条目同时出现是**错误**：继承的含义就是没有自己的数据。
  放过去会变成"编辑器显示一份、编译用另一份"。
* 未映射的字段直接报错（`JsonUnmappedMemberHandling.Disallow`）。`"offs"` 这类手误默认
  会被静默忽略，然后整份设置变成"什么都没关"。
* **树结构不落盘**。它是按当前赢家 unit 临时构建的视图，保存后立刻丢弃；
  落盘的只有"某 unit 的第 N 个 section 是禁用"。

### 14.3 三种角色怎么落到编译产物上

| 用户的设置 | 工作区 | `wardrobe.masks` |
|---|---|---|
| 护甲是**视觉来源** | 自己的条目，`mode=own` | 自己的 set，`off=` 来自它 |
| 护甲是**映射目标**，未独立编辑 | 没有条目 | `target=T,set=<来源的 set>` ← 同一个 set id = 继承 |
| 护甲是**映射目标**，独立编辑过 | 自己的条目，`mode=own` | 自己的 set，不再指向来源的 set |

**继承不需要新机制**：两个 target 指向同一个 set id 就是继承。这也是为什么编译产物
里必须是"目标 → 掩码集"的扁平投影，而不是"全局一份来源掩码"。

### 14.4 投影：工作区 → `wardrobe.masks`（已实现并实测）

```powershell
hd2mod wardrobe project --library <mod库> [--registry-root <协议目录>] [--sections <文件或目录>] --output wardrobe.masks --json
```

* `--library`：工作区在 `<mod库>/.hd2modmanager/wardrobe.workspace.json`。
* `--registry-root`：注册协议在 `<协议目录>/armor-registry.protocol`，取其中的 `map=目标,来源`。
* `--sections`：**文件或目录**。缺省等于 `--library`，递归找所有 `sections.json`
  （映射表随 mod 走，用户不该为此记住每个 mod 的子目录名）。
  多份表按 unit 合并；同一 unit 在两份表里内容不一致时**拒绝**——那说明两份表来自不同的
  构建，按哪一份都会关错几何。

**set id 分配**：需要 set 的所有者（有禁用内容的来源 + 显式独立设置的目标）按 itemId
升序编号。同一份输入必须产出同一份字节，否则"内容没变"会被误判成"变了"，白白触发一次重载。

**两条刻意的取舍**：

1. **解析不到的条目只警告、不报错**。工作区是惰性、与 mod 无关的，可能引用当前没部署的
   mod 的 unit。为此让整次编译失败，等于"换一次 mod 就再也编不出掩码"。
2. **空集目标照样写 `target=` 行**。那是"覆盖掉继承、一块都不关"的唯一表达方式。

**生效时机**：运行时只在**协议被应用**时读掩码（`armor_registry_runtime.lua` 的
`prepare_section_masks` 在 apply 协程里）。所以改完掩码要再触发一次协议应用——
单改 `wardrobe.masks` 不会触发重载。`project` 的输出里会提示这一点。

**实测（2026-10-19，真实注册协议 + 部署包的映射表）**：

```
掩码集 1 ← 护甲 820333399（视觉来源，2 条）；继承者：530315896, 3106805633, 3544585106
off=1,af431352d9b95e83,0,a47787804c45e1a9-8852
off=1,af431352d9b95e83,1,a648fd251c740b12-1024
target=530315896,1
target=3106805633,1
target=3544585106,1
```

三个目标指向同一个 set id —— 继承。`validate` 对这份产物通过，`2/2` 可解析，
三个目标各隐藏 2 个 section。

**与 `wardrobe compile` 的关系**：`compile` 读手写的规格文件，是阶段 4 前期的过渡路径；
`project` 读权威工作区，是正式路径。两者产出同一种文件，`validate` 一视同仁。

**用例**：`HD2ModCore.Tests/WardrobeWorkspaceTests.cs`（20 项）、
`WardrobeMaskProjectionTests.cs`（15 项）、`HD2ModCli.Tests/WardrobeCliTests.cs`（11 项）。

---

## 15. 2026-10-20 更新后的 Lua 资源格式（已实测并适配）

这一节是**独立于衣柜**的底层事实：运行时护甲注册靠独占 `boot.lua` 部署，所以
"新格式长什么样"决定了整个护甲映射能不能装进游戏。

### 15.1 新的封装：8 字节前缀 + 字节码

游戏自己的 `boot.lua`（`0xf476df93691895fa`，archive `9ba626afa44a3aa3`）现在是：

```
f5 01 00 00 | 01 00 00 00 | 1b 4c 75 61 51 00 01 04 04 04 08 00 ...
  长度=501   |  version=1  | \x1bLua 5.1 头
```

| 偏移 | 内容 |
|---|---|
| `+0` u32 | 载荷长度 = 资源总长 − 8 |
| `+4` u32 | **格式版本**：`1` = 字节码（当前），`2` = 源码文本（更新前） |
| `+8` … | 载荷本体 |

载荷是**标准 PUC Lua 5.1 字节码**，不是 LuaJIT：

| 字节 | 含义 |
|---|---|
| `1b 4c 75 61` | `\x1bLua` |
| `51` | 版本 5.1 |
| `00` | 格式 |
| `01` | 小端 |
| `04 04 04 08` | `int` / `size_t` / `Instruction` / `lua_Number` 宽度 |
| `00` | 非整数数值 |

LuaJIT 的字节码是 `\x1bLJ`，**加载不了**这种块；所以本机那个 `luajit.exe` 在这里
是有害的编译器，不能用它产出载荷。

### 15.2 原版 `boot.lua` 到底做了什么

反汇编（`tmp/LuaInspect`）出来的逻辑只有四行：

```lua
stingray.Application.init = function() end
stingray.Application.shutdown = function() end
stingray.Application.update = function(...) end
stingray.Application.render = function() end
if stingray.Application.build() ~= "release" then require("debug") end
local jit = rawget(_G, "jit")
if jit then jit.off() end
```

**它不加载任何 mod**。也就是说 Lua mod 一直靠"替换这个入口"生效，而不是靠游戏自己
去扫描目录。这也解释了为什么更新后**所有**旧 Lua mod 一起失效：入口的封装换了，
旧 mod 塞进去的是 version 2 + 源码文本。

### 15.3 管理器现在产出什么

载荷 = **Lua 5.1 字节码**，版本 = `1`。合并后的源码文本（带 `--@@HD2LUA-` 标记和
ledger，仍可读、仍可校验）被包进**一个固定原型**，等价于：

```lua
local f = load(<合并后的源码>)
if f then return f() end
return
```

八条指令：`GETGLOBAL / LOADK / CALL / TEST / JMP / CALL / RETURN / RETURN`，
`maxstacksize=2`、`vararg=2`、常量表 `{"load", 源码}`、无行信息——和原版一样是
strip 过的块（原版的 `source` 也是 NULL，所以错误信息里的 `?` 行为一致）。

**为什么不直接写源码**：`luaL_loadbuffer` 对文本块也能用，但游戏现在既然只发字节码，
就按它的样子发。这是唯一能保证"和游戏自己那份同类"的做法。

**为什么不用外部编译器**：没有 `lua51.dll`、没有 `luac`，而且上面说过 LuaJIT 不行。
所以写了个只覆盖"读—写"的 Lua 5.1 字节码编解码器
（`HD2ModAdaptation/PatchReconstruction/Lua/Lua51Bytecode.cs`），它**能把游戏自己那份
资源逐字节往返**——这就是它正确性的证据，而不是"照文档写的"。

### 15.4 验收证据（2026-10-20）

| 检查 | 结果 |
|---|---|
| 读 `boot.lua` 资源 → 再写回 | 501 字节**逐字节相同** |
| 读另一份 Lua 资源（6653 字节）→ 再写回 | **逐字节相同** |
| 用真实护甲注册运行时（281752 字节源码）跑完整管线 | 产物 **993424** 字节，与 `deploy plan` 里记录的 `sourceLength` **完全一致** |
| 产物 `+0xBC` | `1` |
| 产物载荷前 12 字节 | `1B 4C 75 61 51 00 01 04 04 04 08 00`（与原版一致） |
| 载荷再解析 | 通过，且往返一致 |
| `deploy plan` | `isReady: true`，`diagnostics: []`，Lua 合成 1 份 |

### 15.5 顺带修掉的两处

1. **检查器认字节码**。以前载荷必须是严格 UTF-8，否则报"不受支持"。现在以
   `\x1bLua 5.1` 开头就当作 `LuaPatchWrapperKind.Bytecode`：整块保留、整块重放
   （`load(BC)` + `pcall`），因为字节码**不能**像文本那样拼接。一份这样的输入
   会把整次合并切到重放包装——这正是管理器对原版 `boot.lua` 一直在做的事。
   这也让"我们自己部署出去的 patch 还能被下一次部署读回来"成立。
2. **CLI 读 GUI 的 Lua 开关**。`deploy plan/validate/apply` 以前完全忽略
   `settings.json` 里的 `DeployLuaMods`，于是同一个配置在 GUI 里会装 Lua、在 CLI 里
   不装。现在 `CliWorkspace.Open` 同步这个静态开关，缺省 `true`（和 `SettingsService`
   一致）。

### 15.6 还没验证的那一步

**游戏是否接受这份资源，只能在游戏里验证。** 本地能证明的到"格式与游戏自己那份同类"
为止。如果部署后仍然崩溃，剩下的可能就是"载荷必须是字节码且容器版本必须是 1"之外的
第三种要求（例如 TOC 里还有别的字段），那时第一件事是把崩溃日志和 `patch_3` 的实际
字节一起看。

## 16. 护甲注册表是**内容不可变**的（2026-10-21 已实测，这条路关闭）

### 16.1 要区分两个假设

改 `generated_customization_armor_sets.dl_bin` 后报
`Fatal Error! Game resource mismatch`，只有两种可能：

| 假设 | 含义 | 后果 |
|---|---|---|
| **A. 内容校验** | 解密后的明文必须与官方逐字节一致 | 整张表不可改，任何映射方案都不能落在这里 |
| **B. 引用校验** | 只校验"引用的 unit / 材质在所指归档里存在" | 只要引用自洽就能改，映射可行 |

以前那些失败（160 件头盔批量重定向、只改 unit 哈希）**两者都能解释**，因为都动了引用。

### 16.2 判别实验：改一个和引用完全无关的字段

挑 `0x6809ad22`（头盔，1 个真实 piece）的 `piece+0x10`（`weight`，u32），`1` → `2`。
这是纯玩法权重，**不引用任何资源**，长度不变，重封后明文只差 4 字节。

```
+10  01000000 ...   →   +10  02000000 ...
```

结果：**同一个报错**。

> Fatal Error! Game resource mismatch. Verify integrity of game files, or contact
> support at https://arrowhead.zendesk.com for further assistance.

### 16.3 对照：同长度重封、明文不变

用同一套工具把**完全相同的明文**重新封成盒子（密文必然不同，sealed box 的临时公钥
和 nonce 每次都变）：

| 项 | 值 |
|---|---|
| 原版文件 SHA-256 | `53F7923EE890618A…` |
| 重封后文件 SHA-256 | `B0E60DE975A3ADCC…` |
| 明文 | 逐字节相同 |
| 结果 | **正常进游戏**（21 s 出窗口，45 s 仍在） |

### 16.4 结论

| 问题 | 答案 |
|---|---|
| 校验发生在密文/文件字节上？ | **不是**——重封后文件字节全变也能进 |
| 校验发生在解密后的明文上？ | **是**——明文差 4 字节就拒 |
| 是引用校验吗？ | **不是**——改的字段与资源无关 |
| 是 EAC 吗？ | **不是**——是游戏本体的数据完整性对话框 |

**所以注册表是内容不可变的：解密后的明文必须与官方完全一致，否则游戏拒绝启动。**
这不是签名问题（我们能正确重封），是"内容必须对得上"的问题。

### 16.5 期望摘要找不到

如果校验是"明文哈希 == 某个值"，那个值总得存在某处。全目录扫过一遍
（`tmp/hashhunt`，跳过 >300 MB 的 bundle）：

| 搜索项 | 范围 | 结果 |
|---|---|---|
| 明文 / 密文的 MD5、SHA-1、SHA-256、SHA-384、SHA-512（含反序） | `data\`、`data\game\`、`bin\` 全部 ≤300 MB 的文件 | **零命中** |
| 文件名 `generated_customization_armor_sets`（ASCII + UTF-16） | 同上 | **零命中** |
| 报错文案 | `helldivers2.exe`、`game.dll`、`runtime.dll` | **零命中**（`game.dll` 里只有 XML 解析器的 `mismatch`） |

推论：期望值不在散文件里，要么在 bundle 内，要么由引擎在运行时从 bundle 数据推导
（"重新生成一份再比对"）。两种情况下我们都拿不到、也伪造不了。

### 16.6 对衣柜的影响

**逐护甲映射不能落在注册表上。** 这条数据侧入口关闭后，能改的只剩"某个 unit 资源
本身被补丁归档替换"——也就是现有 Mod 的做法，天然是"一份网格、所有引用它的护甲"，
但**不能按护甲逐件指定**。

工具与现场（本次新增，均在 `tmp/`）：

| 路径 | 用途 |
|---|---|
| `tmp/regedit/Program.cs` | 新增 `hex` / `poke` / `redirect` 三个模式（原来只有 `dump` / `debug` / `apply`） |
| `tmp/regedit/run-boot.ps1` | 启动 + 定时截图 + 记录窗口标题与存活时间，用于判定"进得去 / 弹框" |
| `tmp/regedit/runs/` | 每次实验的日志与截图（baseline / poke-weight / reseal-identical） |
| `tmp/hashhunt/` | 在游戏目录里找某个摘要或文件名的落点 |
| `tmp/plain-dump/` | 解封注册表得到明文，供离线分析 |

收尾状态：`generated_customization_armor_sets.dl_bin` 已还原为原版
（SHA-256 `53F7923E…`），`data\game\` 下早前遗留的明文副本已删除。

### 16.7 第二轮：连"保留字节"也拒（2026-10-21 18:25）

16.4 的结论基于"改 `weight` 被拒"，但那时还留着一个漏洞：也许 `weight` 是**有取值域的
枚举**，`2` 对头盔非法，所以失败是"语义不合法"而不是"内容被校验"。引擎自己的错误分级里
确实有一类 `semantic_mismatch`（见 `data\settings.ini` 的 `error_levels`），这个假设
必须先排除。

先把字段域摸清楚（`tmp/regedit/tabulate.py`，只统计每个 body 的 piece 0，其余是填充）：

| 字段 | 实际出现过的值 |
|---|---|
| `kit` | `0` 护甲 138 / `1` 头盔 161 / `2` 披风 123 |
| `slot`（`+8`） | `0,1,2,3,5,6,7,8,9` |
| `pieceType`（`+12`） | `0,1,2` |
| `weight`（`+16`） | **`0,1,2`**；头盔里 `0`×21、`1`×123、**`2`×17** |
| `tone`（`+88`） | `0`×577、`4`×1、`5`×2 |
| `+20`（u32） | **580 个真实 piece 里恒为 `0`** |

也就是说 `weight = 2` 对头盔是**合法值**（有 17 个真实 piece 就是这么写的），所以第一轮的
失败**不是**"值非法"。

于是做决定性实验：把 `0x6809ad22` 的 `piece+0x14`（就是那个恒为 0 的保留字段）从
`00000000` 改成 `01000000`。这个字段在整张表里没有任何语义、没有资源引用、也不参与
任何看起来像枚举的东西。

结果：**仍然是 `Game resource mismatch`。**

| 假设 | 判定 |
|---|---|
| A. 字段语义校验（值必须合法 / 必须与 unit 的 CustomizationInfo 一致） | **排除**——改的是无语义保留字节 |
| B. 整条内容完整性校验 | **成立** |

补充一条排除证据：给每个条目找 20 字节"逐条目摘要"也不成立——`payload+4` 处那 20 字节在
**同一批编译的条目之间完全相同**（422 条只有 291 个不同值，前两条完全一样），它是编译
批次的标记，不是内容哈希。

**最终结论：注册表是不可变的。** 校验粒度至少覆盖整条 item 内容，且期望值不在散文件里
（16.5）。字段语义、引用自洽、封装完整性三条都已排除。

### 16.8 这条结论对方案的最终影响

| 想做的事 | 能不能 |
|---|---|
| 改注册表把护甲 A 指向外观 B | 不能 |
| 改注册表把 160 件头盔批量指向一个 unit | 不能 |
| 补丁归档替换某个 unit 的几何（现 Mod 的做法） | 能，但只能"一份网格、所有引用它的护甲" |
| 逐护甲指定外观 | 只能靠"每件护甲一份 unit 副本"，代价是几何显存 |

## 17. 代码模块也被校验，代价是"静默停摆"（2026-10-21 18:47 实测）

第 16 节只证明了**数据**被校验。自然的下一步是"那就把校验代码本身改掉"。这一节记录
这条路为什么也不通。

### 17.1 对照值确实在 `game.dll` 里，但不可复现

`game.dll` 中有一张 16 字节槽的表（约 `0x2caec00–0x2caf490`），56 个 `data\game\*.dl_bin`
的**文件名 murmur64** 全部命中，每项后面跟一个 64 位"伴随值"：

```
0x2caf1e0  aa82aef137b06bdd  e1a9bd0b6849a957
           └ murmur64("generated_customization_armor_sets.dl_bin")
```

用 55 个数据库当样本，逐一比对 14 类候选函数（murmur64 多 seed、FNV-1a、MD5/SHA-1/
SHA-256/SHA-3/BLAKE2b 的首尾 8 字节、CRC32），**密封态与解封态都试过，命中 0**。
所以伴随值是**构建期令牌**（不是可由文件内容复现的摘要），它不是那个对话框的依据。

### 17.2 活进程转储被拒

```
OpenProcess(QUERY_INFORMATION|VM_READ) -> 0x2c4   (成功)
MiniDumpWriteDump(...)                 -> False, error 0x80070005 (E_ACCESSDENIED)
```

反作弊把"读进程内存"这条权限剥掉了。有 AC 在，**活进程转储不可能**，只能等游戏自己写。

### 17.3 改 `game.dll`：窗口在，但永远不开始加载

两次独立实验，改动量都小到不可能影响逻辑：

| 实验 | 改动 | 结果 |
|---|---|---|
| 字符串 | `.rdata` 里一句废弃的 Beta 文案，改 1 个字节（`a`→`e`） | 进程启动、窗口出现、存活 90 s，**画面全黑** |
| 追加 | 在最后一个节之后追加 32 字节（PE 不会映射这段） | 同上，**画面全黑** |

关键旁证来自现场观察：加载资源时屏幕上会先出现**极淡的蓝色山脉剪影**，而这两次
**从未出现过**——说明游戏根本没进入加载阶段，不是渲染失败，也不是被别的窗口遮挡。

还原 `game.dll`（SHA-256 `A457433F…`）后立刻恢复正常。

### 17.4 结论：两条防线，两种惩罚

| 被改的东西 | 惩罚 | 说明 |
|---|---|---|
| 数据数据库（`generated_*.dl_bin`） | **弹框**：`Game resource mismatch` | 游戏本体，可见、可诊断 |
| 代码模块（`game.dll`） | **静默停摆**：黑屏、不加载、不报错 | 校验在模块之外，所以改模块本身无效 |

推论：校验器**不可能在 `game.dll` 内部**（否则改了就自证不了）。它要么在
`helldivers2.exe`，要么在反作弊里；而"改模块 → 连加载都不开始"这种惩罚方式，说明
它在模块被信任之前就已经判定完毕。

**所以"改代码绕过校验"这条路不成立**：被改的模块走不到校验那一步。

### 17.5 工具与现场

| 路径 | 用途 |
|---|---|
| `tmp/re/find_xref.py` | 在 PE 的 `.text` 里找指向某地址的 RIP-relative 引用 |
| `tmp/re/find_table_xref.py` | 用重定位表把数据表追回指针槽，再找代码引用 |
| `tmp/re/identify_companion.py` | 用全部 56 个数据库做样本，识别表里的伴随值 |
| `tmp/re/livdump/` | 活进程 minidump（实测被 AC 拒绝） |
| `tmp/decodeall/` | 批量解封 `data\game` 下所有数据库 |
| `tmp/reseal/` | 解封 → 改一个字节 → 重封，用于"改别的数据库"类实验 |

现场已还原：`game.dll`、`generated_customization_armor_sets.dl_bin`、
`generated_credits_settings.dl_bin` 全部回到原版字节。

## 18. 两个"绕过"想法的实测：都封死（2026-10-21 19:00）

第 16、17 节证明了"改内容会被拒""改代码会停摆"。还有两个理论上可以绕开的思路：
**(a) 让校验看到原版、让游戏用我们的（TOCTOU）**，**(b) 构造一个校验值不变的改动（伪装）**。
这一节把两个都测掉。

### 18.1 校验发生在 18 s 之前，且游戏不长期占用文件

以**改动版**启动，在 **18.1 s** 把文件换成原版：

```
t=18.1s MOVE ok -> 53F7923EE890618A
t=20.0s title='Fatal Error!'          ← 仍然报错
```

两个结论：**校验在 18.1 s 之前就读完了**；而且替换能成功，说明游戏**读完即关句柄**，
不存在"文件被锁住"的障碍。

### 18.2 校验之后替换：游戏不会再读

以**原版**启动（过校验），在 **25.2 s** 换成"161 个头盔全部指向同一个 donor 外观"的版本：

| 观察 | 结果 |
|---|---|
| 是否弹框 | **没有**（一路进到船上） |
| 军械库里 161 个头盔 | **没有任何变化** |

所以库在启动时**只读一次**，校验和解析共用同一份内存。**换文件对解析结果没有任何影响，
TOCTOU 不成立。**

### 18.3 弱校验也被排除

如果校验是字节和 / 异或这类弱校验，那么"字节置换"（既不改变和、也不改变异或）应该能过。
把某条目 20 字节批次标记的前两个字节互换（`a4 2e` → `2e a4`，字节和不变）：

**仍然 `Game resource mismatch`。**

⇒ 校验对字节**顺序敏感**：密码学哈希 / CRC / 逐字节比对 / 重新生成后比对，四者之一。
"构造校验值不变的改动"这条路也不成立。

### 18.4 汇总：九条路

| # | 方案 | 判定 | 依据 |
|---|---|---|---|
| 1 | 运行时 Lua 改内存 | ✗ | 新版 PUC Lua 5.1.4，`ffi` 消失 |
| 2 | 原生 DLL 注入 | ✗ | 未签名模块 → 577 |
| 3 | 跨进程写入 | ✗ | `error 5` |
| 4 | 引擎几何 API | ✗ | 只有网格级开关 |
| 5 | 薄 unit 共享网格 | ✗ | 实测重复读取、浪费显存 |
| 6 | 改注册表内容 | ✗ | 内容完整性校验（16 节） |
| 7 | 改代码绕过校验 | ✗ | 模块自校验，静默停摆（17 节） |
| 8 | 校验后替换文件 | ✗ | 单次读取（18.2） |
| 9 | 构造校验值不变的改动 | ✗ | 顺序敏感（18.3） |

工具：`tmp/re/swap/`（`swap2.ps1` 可在任意时刻把注册表换成另一份并截图取证）。

