# serenade 手套/袖套消失 —— 交接单（2026-11-01 21:5x）

> **这份文件是压缩上下文前的交接单。压缩后照这份继续，不要重新发散。**
> 用户明确的结论：**问题出在「切换护甲（换甲 / rekey）+ section 变化」这条链上。**
> 别的方向全部停下，只查这里。

---

## 0. 一句话现状

同一个 VPK mod：

- **换甲（rekey）→ 进 O-3 后，手套/袖套/丝袜整段不显示**（手、皮肤、头发、蕾丝都正常）
- **拷贝 + 映射（不走换甲）→ 显示正常**

用户已确认这个对照成立过。但后来**发布版换甲也开始出现同样现象**，所以不能简单归因为"我们版本坏了"。
→ 现在是"换甲这条链本身有 bug"的定性，需要精确定位。

---

## 1. 用户的原始观察（最重要的一手信息）

1. 同**一个 unit** 里：手能看见、手套看不见 ⇒ **不是"整个 unit 没被映射"**，是 section 级。
2. 完全没有材质时（把材质包全部取消部署），手套和臂环**能显示**（品红占位色）⇒ **几何完整、section 存在、没被隐藏**。
3. 有材质时那些 section **不画**（不是品红，是"没有"）。
4. 裙子的蕾丝一直正常 ⇒ alphaclip 这一路本身没问题。
5. 用户曾在 lilium_sancta_bill 上试过"关闭袖子 section"，**当时毫无效果**，后来又打开。
6. 用户自己的推断（大概率正确）：**关闭 section → 烘焙进 Overwrite** 这个功能有 bug，可能和换甲一起把 section 搞坏。

---

## 2. 已经**排除**的方向（别回头）

| 方向 | 怎么排除的 |
|---|---|
| 材质 id 世代混用 | 现在 82 个共有材质的 id 与发布版**完全一致**，模板 0 处不同 |
| 39 个新金属材质 | 撤掉它们（`HD2_NO_SYNTHETIC_BASE=1`）→ 手套仍不显示，只是多了品红 |
| `$translucent` → 不透明 那条改动 | 已回退到发布版行为（alphaclip），遮罩逐字节相同 |
| matcap / 金属 F0 / metal 模式 | `--matcap metal` 已收窄成"只对没有 `$basetexture` 的材质生效"；布料不受影响 |
| 转换器整体 | 全开关关掉时，我们的产出与**发布版二进制逐字节相同**（patch + gpu 三个包全等） |
| 物理参数容器追加 | 两份产物里的容器位置/长度完全一致（都在数据之后，10933 字节） |
| section 被 `numIndices=0` 隐藏 | 所有 Overwrite 都是 0 个；`sections.json` 记录值与 patch 实际值 **0 处不一致** |
| lilium 关闭袖子的跨 mod 残留 | lilium 的 unit id `9a5e6dc74cdb08a3` 只出现在它自己的两个文件里 |
| GUI 与 CLI 不一致 | GUI 只是 spawn 旁边的 `hd2mod.exe`；4 个相关文件自发布版零改动；两个二进制 rekey 输出**逐字节相同** |
| 发布版护甲列表为空 | 已查明是发布版自身索引缺陷（`game_data_unit_parts` 0 行），已用 Debug 的索引覆盖修好；与手套无关 |

---

## 3. 现在要查的地方（**唯一焦点**）

换甲/rekey 会做两件事，其中任意一件都可能把 section 搞坏：

### A. rekey 对 Unit 的重建

入口：`HD2ModCli/CliVpkRekey.cs` → `HD2ModCore/Infrastructure/CanonicalCrossArmorOrchestrator.cs`

已知它做：按 `MeshInfoIndex` 把来源网格映射到目标网格、重编顶点流、重编 LOD、重算骨骼、应用 Transform、**隐藏目标护甲原网格**（`hiddenMeshes`）。

关键自承风险（代码注释里写着）：
- 「某个部位没有落点 → 来源 Unit 整份没写出去、原版又已被隐藏 → 衣服还在、人体胳膊不见了」
- 「护甲层被隐藏的前提是同一个部位在更低的层上还有一个 Unit 顶着」

### B. section 开关烘焙

入口：`HD2ModManager/Services/VpkRekeyService.cs` → `HD2ModCli/CliVpkRekey.cs` 的 `BakeAsync` / `TranslateDisabled` → `HD2ModCore/Infrastructure/WardrobeSectionBaker.cs`

已读到的实现要点：
- `Bake` 会**先全量校验**每个 toggle 的当前值（必须等于 0 或 记录值），否则抛异常 → 不会写偏
- 只写"与目标不同"的 dword
- `TranslateDisabled` 会把来源 unit 的 section 下标翻译成目标 unit 的下标

**待查疑点（按优先级）：**

1. **`TranslateDisabled` 的下标翻译是否在"unit 被换到目标 id 之后"仍然正确** —— 换甲后 unit 的 section 顺序/数量可能变，若仍按来源下标写，就会关错 section（甚至关掉别人的）。
2. **同名 section 的下标漂移**：serenade LeftArm 有 10 个 section，其中 sec1/2/8 用的是同一个材质 id（`a06e5361…`）。按"材质 → section"反查会命中多个，翻译时若只取第一个就会关错。
3. **`components.json` 的 `disabled` 定义 vs 实际开关状态**：`components.json` 里每个 switch 都有一个 `disabled: [{unit, sections}]`——那是"关掉时该隐藏哪些"，**不是当前状态**。别把它误读成"已隐藏"。
4. **换甲是否把 target 的原网格 hide 了、但我们对应 section 没写成功** —— 这正是"只有一部分消失"的形状。

---

## 4. 手头的样本（可直接对比，不必重跑）

```
artifacts/serenade-rekey-baseline/
  Good-拷贝映射/serenade_bill/           ← 能显示（Overwrite = vpk2patch 原样拷贝）
  Good-拷贝映射/serenade_bill_2/         ← 同上（另一个 mod 实例）
  Broken-O3-rekey/                       ← 换甲到 O-3（丢袖子）
  Release-rekey-O3/                      ← 发布版二进制换甲到 O-3
```

**已核对：`Broken-O3-rekey` 与 `Release-rekey-O3` 三个文件逐字节相同**（patch 452911 / gpu 30252271 / sections.json 114054）。
⇒ 发布版和我们的换甲输出在这份输入上是等价的。

### 已测量的事实（样本之间对比）

| 项 | Good（拷贝） | Broken（换甲 O-3） |
|---|---|---|
| units | 7 | 17（换到 O-3 后 1→N） |
| LeftArm sections | 10 | 10（`411e`、`9375` 各一份，Slim/Stocky） |
| LeftArm 每个 section 的 numIndices | 18636,14568,16374,2634,6120,4086,2496,24903,4848,1740 | **完全相同** |
| LeftArm 每个 section 的 materialId | a3c1d0e6 / a88acb76 / a06e5361 / a5f62823 / a27be8ce / ac439d61 / a48216c4 / a570608d / a06e5361 / a5f62823 | **完全相同** |
| 网格表（mesh 0..4 / lod / sec 数 / meshId / streams） | 5 meshes，lod0 在 index4，99952 verts / 385620 idx | **完全相同** |
| 骨骼 | 152 根 / 4 组 | 完全相同 |

**⇒ sections、材质、网格表、骨骼、流长度全部相同。**
剩下没查的就是：**这些 section 在目标 unit 里的"身份"是否被游戏认可**（例如 section 的 material binding 表顺序 / material 索引 / pieceType / bodyVariant），以及**被替换的 unit id 是否是游戏真正渲染的那一个**。

---

## 5. 下一步建议（按顺序，别再发散）

1. **对比 Good / Broken 两份 unit 的 TocData 原始字节**，把 material id 归一化后逐字节 diff，
   看除了 unit id / 材质 id 之外还有没有别的字段变了（尤其 section 记录的 `numIndicesOffset`、
   material 索引、`meshInfoIndex` 顺序）。
   工具：`tmp/patchdiff.py`、`tmp/deepcmp2.py` 可以改造；unit 内部结构需要按 section 记录手工解。
2. **确认换甲后目标 unit id 是否就是游戏渲染的那个**：O-3 的 LeftArm 原本有哪几个 unit？
   我们替换的是 `…411e`(Slim) 和 `…9375`(Stocky)。若 O-3 实际只渲染其中一个，且我们写错身形，就会整段不显示。
   数据来源：`components.json` / `sections.json` 里的 `bodyType`、以及 `hd2mod equipment ...` 的输出。
3. **检查 `TranslateDisabled`**：手工构造一次"关掉 LeftArm sec1"的输入，跑 rekey，
   看它到底把哪个 unit 的哪个 section 写成了 0（用 `tmp/checknumindices.py` 校验）。
   虽然 serenade 的 `disabled` 是空的，但用户是在 lilium 上操作过、且怀疑这功能——**先证明这个功能能不能正确工作**。
4. 如果以上都正常，再查 **canonical 重建后的 section ↔ material 绑定顺序**：
   目标 unit 的 material 表顺序若与来源不同，而我们按来源顺序写 material 索引，section 就会拿到错误的材质
   （表现不是品红，而是"画了但看不见"或"贴图错乱"）。

---

## 6. 工具与路径（压缩后直接用）

### 常用脚本（`tmp/` 下，一次性工具，可改）

| 脚本 | 用途 |
|---|---|
| `vpkpeek.py` | 读 L4D2 VPK（v1/v2），`list` / `dump`。**注意：offset 相对数据段，已修** |
| `matpeek.py` | 解 HD2 Material 的 TocData（槽位/变量/值区） |
| `texpeek.py` | 读贴图 TocData 头（尺寸/格式/unkId） |
| `shaderhash.py` | 槽位键/着色器变量哈希 → 名字（读 SDK `hashlists/shadervariables.txt`） |
| `patchdiff.py` | 两个 patch 容器的条目级 diff（键集合、TOC 差异、头部） |
| `deepcmp2.py` | **按名字对齐**后逐项比材质/贴图内容（id 归一化后才可比） |
| `checknumindices.py` | 校验 `sections.json` 记录的 `numIndicesOffset` 与 patch 实际值是否一致 |
| `swapmaterial.py` | 原地等长替换某个材质的 TocData（做过"换成皮肤材质"的对照实验） |
| `alphamap.py` / `vtfalpha.py` | 解 DXT5 alpha、打覆盖图（判断"图集形状遮罩"还是"AO"） |
| `texbright.py` / `srcbright.py` | 量贴图/源 VTF 的平均亮度分布 |
| `texconflict.py` | 找同批里"同名贴图不同内容"的冲突 |
| `modmat.py` / `patchscan.py` / `opacityscan.py` | 按 mod 导出材质 / 扫全库父模板 / 扫不透明度槽 |

### 关键路径

```
仓库                E:\Data\source\repos\WpfApp1
Debug 管理器        HD2ModManager\bin\Debug\net10.0-windows\HD2ModManager.exe
                    （同目录 hd2mod.exe 就是 GUI 会调的那个）
发布版（1.0.0+754ad25，2026-11-01 13:16 构建）
                    artifacts\publish\HD2ModManager-win-x64-20261101-131435407-requires-dotnet\
                    （CLI 在 bin\hd2mod.exe）
Mod 库              E:\mod          （.hd2modmanager\ 里有 registry / wardrobe 状态）
游戏 data           D:\SteamLibrary\steamapps\common\Helldivers 2\data
```

### 目录约定

- Mod 根 = 来源 VPK
- `vpk2patch/` = 转换产物（权威事实）
- `Overwrite/` = 变体（换甲 / section 烘焙的产物，**每次从 vpk2patch 重新生成**）
- `wardrobe.json` = 组件树状态：`componentTreeEnabled`、`targetArmorArchiveId`、`disabled`

### 当前库状态（可能已被用户改动，用时先确认）

- `E:\mod` 下有 **18 个 `serenade*` 目录**，来自**两代导入**（`xxx` 与 `xxx_2`），**两代都部署过**
- 部署走软链接：`data\9ba626afa44a3aa3.patch_N` → `E:\mod\<mod>\vpk2patch|Overwrite\9ba626afa44a3aa3.patch_0`
- `serenade_bill` 的 Mod GUID = `fea708ec-4be3-4365-96b4-0e3dee2e4c62`
- O-3「自由之魂」archive id = `f9ced2d898787705`（换甲时还会带上头盔 `a2999407862595df`）

### 我用过的命令（可直接复跑）

```powershell
# 换甲（发布版二进制，等价于 GUI 的调用）
artifacts\publish\...\bin\hd2mod.exe vpk rekey fea708ec-4be3-4365-96b4-0e3dee2e4c62 `
  --archive-id f9ced2d898787705 --manager-root HD2ModManager\bin\Debug\net10.0-windows `
  --library E:\mod --game-data 'D:\...\Helldivers 2\data' --json

# 校验 Overwrite 里 section 的 numIndices 是否与 sections.json 一致
python tmp\checknumindices.py E:\mod\serenade_bill Overwrite

# 两个 patch 内容对比（按名字归一化）
python tmp\deepcmp2.py <A.patch_0> <B.patch_0> <A.json> <B.json>
```

---

## 7. 环境坑（会浪费时间，先记住）

- **管理器开着就不能重建** Debug 版（DLL 被锁）。要用户先关掉。
- **5 个 `DeploymentCliTests` 会失败**，只要管理器开着（它占着库写锁）——不是代码问题。
- 只拷 `asset-index.sqlite` 不拷 `-wal`/`-shm` → `database disk image is malformed`。
- 我的 Python VPK 读取器：**offset 是相对数据段起点**的，不是文件头（已修，别回退）。
- texconv 只吃 **148 字节 DX10 头**的 DDS；从 HD2 patch 里取头是 `toc[192:340]`。
  我自己写的 DXT5 DDS 头 texconv 不认（`已到文件尾`），要看图就直接解 DXT5 alpha 块。

---

## 7.5 2026-11-01 22:0x —— 第 5 节第 1、2 步做完了，结论和下一步

### 做完的事与结果

1. **第 1 步（unit TocData 逐字节对比）→ 结论：单位内容完全没问题。**
   `tmp/rekeyequiv.py` 把 vpk2patch 的 7 个 Unit 与当前 Overwrite 的 Unit 按几何指纹配对，
   **TocData 与 gpu 长度全部逐字节相同**（44611/39816/40422/29923/28708/33476/40838 全等）。
   ⇒ rekey 只是把同样的字节搬到目标护甲的槽位上，**没有改动任何几何/材质/section**。
   所以"rekey 把 unit 重建坏了"这条彻底排除。

2. **第 2 步（目标 unit id 是否是游戏真正渲染的）→ 结论：全覆盖。**
   `hd2mod gamedata extract archive` 拿到了 RE-824（现目标护甲 `f50c7fd427839c47`，名称「执旗手」）
   的原版 Unit 清单，`tmp/coverage.py` 对比：**22 个 Unit 全部写出**，0 个遗漏。
   `tmp/authority.py` 用权威装备表 `game_data_authority_pieces` 再校验：23 个 piece 里
   只差 1 个 `ea846ef460cc5ba0`（part_kind=11、slot=1、全游戏 41 套护甲共用、位于别的归档），
   换甲从来不碰它，与本次问题无关。

3. **新发现（当前唯一可疑点）：隐藏护甲的方式保留了原版的"剔除/挖洞"网格。**
   - 12 个 `Armor` 件全部被写成 **6564 字节空壳**，但空壳里 **mesh0（lod=-1，439 顶点 / 2280 索引）
     是原版原样保留的**，只把 LOD3..0 压缩成 3 顶点 3 索引。
   - 代码位置：`SameKeyCanonicalUnitRebuilder.cs:112-116`
     「A culling mesh with no explicit source counterpart must retain the target cutout」，
     开关 `MinifyUnmappedCullingMeshes` 存在，但**所有调用点都传 false**
     （`CanonicalHiddenUnitBuilder.Build(target, transforms)`）。
   - 也就是说：**护甲看不见了，但它的"挖洞"网格还在**，会把下面一层（我们的 Undergarment）
     在原来护甲覆盖的区域继续挖掉 ⇒ 表现正好是「几何在、材质在、就是不画、也不是品红」。
   - 反证仍存在：`docs/hd2-patch-mesh-retargeting-notes.md:374,387` 记录过一次实验，
     保留 vs 压缩 culling 网格「表现完全一致」，所以那条曾被判为假设错误。
     **但那次针对的是"小甲片没被隐藏"，不是"下层被挖洞"，症状不同，不能直接套用。**

### 已经布好的实机 A/B（单文件、自带左右对照）

`tmp/zero_cutout.py` 把指定 Unit 里 lod=-1 section 的 `numVertices/numIndices` 清零（只改 8 个字节，
长度不变，不动 gpu_resources/stream）。已对 **左侧** 两个护甲空壳执行：

- `eb769b5f32f01f79` LeftArm Armor Any ← 左侧手臂"挖洞"已去掉
- `a7730cbbad3a1190` LeftLeg Armor Any ← 左侧大腿"挖洞"已去掉
- **右侧 `38099f47ce1cc5d2` / `341d9af932c123be` 原样保留，作为对照**

回退：`E:\mod\serenade_bill\Overwrite\9ba626afa44a3aa3.patch_0.bak-cutout-test-20261101-220608`
（`sections.json` 同目录有同名 `.bak-cutout-test-*` 备份）。
⚠️ 这份改动是**诊断态**：重新导入 / 重新换甲会重新生成 Overwrite，测试就没了。

### 判读方式

- 左手套/袖套回来了、右边没有 → 挖洞假设成立 ⇒ 改隐藏逻辑：整件隐藏时连 culling 网格一起压掉。
- 左右都没回来 → 挖洞不是原因 ⇒ 回头查材质/贴图层（`tmp/texconflict.py` 那条同名不同内容的悬案）。

---

## 7.6 2026-11-01 22:5x —— 又排除了两条，方向和"改 unit id"这条链本身

### 实机结果（都是用户亲自测的）

| 实验 | 结果 | 结论 |
|---|---|---|
| 左臂/左腿空壳去掉 culling 网格（挖洞），右侧留对照 | **完全没有变化** | 挖洞/culling 不是病根 |
| 把 12 个空壳条目整份从 patch 里删掉（RE-824 护甲件回落原版、可见） | **仍然完全不显示** | **隐藏不是病根** |

⇒ 「自动隐藏其余部件」这条也排除了。`CliVpkRekey.cs:203` 的 `AutoHideUnmappedTargetUnits: true`
改不改都不会有效果，别再往这个方向使劲。

工具：`tmp/drop_units.py`（从 patch 删条目、重排 TOC、保持 gpu/stream 偏移不变，带自检）。

### 新的数据：工作/不工作的部位切不开

原版目标 Unit 的网格结构（`artifacts/vanilla-RE824/unit/*.toc`）：

| 部位 | 网格数 | lod0 段数 / 总索引 |
|---|---|---|
| LeftArm Slim/Stocky | 5 | 2 段 / 11964 |
| RightArm Slim/Stocky | 5 | 2 段 / 11964 |
| Hip Slim/Stocky | 5 | 2 段 / 12252 |
| Torso Slim/Stocky | 6 | 2 段 / 17112 |
| LeftLeg / RightLeg Any | 6 | 2 段 / 6384 |

网格数、段数、有无"无材质段"**都切不开**：Hip 是 5 网格且（据用户）正常，LeftArm 也是 5 网格却不显示。
我们那 11 份副本的 TOC 与 `vpk2patch` 逐字节相同。

### 还剩的唯一差别

**同样的字节，挂在不同的 Unit id 上。**
- 能用：挂在 `0x2fe1cb63905c000N`（我们自己的命名空间 id），由 `custom-model.json`
  把「护甲定义」指过来，游戏里选那件自定义素体 → 完全正常。
- 不能用：挂在 RE-824 自己的 Unit id 上（`8fea7071…` / `8d5749d3…` 等），靠覆盖原版 Unit 生效。

而且 `vpk2patch` 那份**永远正常**（用户反复确认）——它只差"没有换甲"这一步。

### 下一步方向（推荐，待用户点头）

**别再走"往目标 Unit id 里塞字节"这条路。** 产品里已经有一条"能用的换甲"：
`RuntimeArmorRegistryService` 的「目标护甲 → 视觉来源」映射（+ `HD2RuntimeArmorOverride` 工程、
`hd2mod custom-model convert`）。它和"拷贝+映射"是同一族，已经实机验证能用。

打算把 VPK 卡片的「替换到目标护甲」改成：
**用 armour-registry 把目标护甲映射到这份 VPK 的自定义素体**，不再生成 `Overwrite` 换甲 patch。
用户侧操作不变（选目标护甲 → 完成）。

### 待确认（问用户）

1. 裙子（躯干）和头发是不是**我们的模型**，还是原版的？这决定"整套没生效"还是"只有四肢没生效"。

---

## 8. 用户偏好（协作方式）

- 用户不是工程师，**不要让他做复杂排查**；要给他"只换一个文件"或"只点一下"的测试。
- 他明确要求：**不要没取证就下结论**。这一轮我判断错了四次（两代导入 / `$translucent` / 39 个金属 / 材质 id），
  每次都是"从数据推断运行时行为"。**先做 A/B，再改代码。**
- 他有发布版二进制可用（`artifacts/publish/...`），这是最便宜的取证手段，优先用它做对照。
- 不要静默改他的配置；改了要说清楚，并且留可回退的备份。
