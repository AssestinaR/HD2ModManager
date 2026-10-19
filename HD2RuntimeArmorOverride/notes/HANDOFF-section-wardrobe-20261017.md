# 交接文档：section 级衣柜系统（2026-10-17）

> 面向接手的进程/代理。读完这一份应当能直接开工，不需要重新摸索。
> 所有"已证实"的结论都有实测记录，**不要重新论证，直接当前提用**。
> 需要复核的地方标了「待测」。

---

## 0. 一句话目标

把 HD2 的护甲从"切 unit"改成"切 section"，做到**运行中按装饰粒度显隐、不重启游戏、不扩容、不改代码段**，
最终给用户 L4D2 那样的无缝换装体验。

---

## 1. 项目背景

### 1.1 这是什么

`E:\Data\source\repos\WpfApp1` —— 一个 HD2（绝地潜兵2）模组管理器 + 模型迁移工具链。
核心业务是把 **L4D2 的模型/皮肤迁移成 HD2 可用的 unit 资源**，并让玩家在游戏里换装。

### 1.2 三个相关功能（本次要大改的范围）

| 编号 | 功能 | 现状 | 本次目标 |
|---|---|---|---|
| 1 | 装饰生成 / 装饰合并 | 把外置 unit 作为装饰插进目标 unit，**构建期**完成；改启用必须重新合并 + 重启 | 输出带 **section 掩码**；**不提供**显隐开关 UI（只负责生成） |
| 2 | 护甲映射 / 自定义素体 | 按 unit 切换显隐；UI 是卡片表单 | **彻底改为 section 级可视化编辑**（本轮重点） |
| 3 | 头盔合并到护甲 | 装饰合并的变体 | 同 1 处理 |

功能 2 里"自定义素体"不是独立功能，是护甲映射延伸出来的附加能力，两者按同一个东西处理。

### 1.3 为什么必须改

旧的 unit 模式撞两道硬天花板（都已实测）：

* **30 个 `(type, slot)` 键**：3 个 bank × 10 个 slot。身体段实际可用 **18**。
* **每 (kit, body 变体) 只处理 20 个 Piece**：三个 20 项栈数组，`cmp ..., 0x14; jae`，
  且"Any"组排在最前会吃满名额，导致 body 专属件一个都轮不到。

改成 section 级之后，**一个槽位只需要 1 个键**（7 个槽位 = 7 个键），两道天花板同时消失。

---

## 2. 环境与路径

### 2.1 仓库与构建

```
仓库          E:\Data\source\repos\WpfApp1   （git master）
解决方案      WpfApp1.sln
CLI           HD2ModCli\bin\Debug\net10.0\hd2mod.exe
管理器        HD2ModManager\bin\Debug\net10.0-windows\HD2ModManager.exe
管理器根目录  E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows
              （这个目录里有 settings.json，CLI 的 --manager-root 指它）
Mod 库        E:\mod
游戏          D:\SteamLibrary\steamapps\common\Helldivers 2
游戏 data     D:\SteamLibrary\steamapps\common\Helldivers 2\data
```

**构建约定**：`TreatWarningsAsErrors=true`；注释用中英双语并解释**为什么**；
真实数据测试在样本缺失时静默跳过；`docs/` 是 gitignored。

`Build.ps1` 整体会失败（缺 `evidence/composition-samples.json`），这是已知问题，不是你的改动造成的。

### 2.2 工具链

```
Python    C:\Users\Administrator\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe
          （已装 numpy / capstone / PIL）
LuaJIT    C:\Users\Administrator\AppData\Local\Programs\LuaJIT\bin\luajit.exe
          用来做语法校验：luajit -e "print(loadfile('x.lua') and 'OK' or 'ERR')"
```

### 2.3 运行时目录

```
护甲注册表    %LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\
              ├─ armor-registry.protocol   协议（mappings/models/piece_overrides/target_modes）
              ├─ runtime.status            运行时状态（blob_base / index_base / revision）
              ├─ refresh-auto.config       自动重载开关（当前 enabled=true）
              └─ refresh-auto.status       重载状态（state=auto_refresh_baseline）
MDL（Lua 加载器） %LOCALAPPDATA%\MDL\Helldivers2\
              ├─ MDL.cfg                   启停配置（enabled 会持久化）
              ├─ Mods\<name>\mod.lua        实时 Lua mod 目录
              └─ Logs\MDL.log
```

### 2.4 本次新加的工具（都已提交）

| 路径 | 作用 |
|---|---|
| `tools/unitprobe/` | 离线读 patch：列 unit / mesh / section，就地改 section 字段，产出 section 映射表 |
| `HD2RuntimeArmorOverride/scripts/find_unit_toc.py` | 外部只读定位内存里的 TOC 副本 |
| `HD2RuntimeArmorOverride/scripts/build_lua_mod.py` | 把 Lua 打包成可导入的 mod zip（照抄 `LuaDeploymentService.cs:549-611`） |
| `HD2RuntimeArmorOverride/scripts/make_section_toggle_probe.py` | 生成运行期开关探针 Lua |
| `HD2RuntimeArmorOverride/scripts/summarize_unit_dump.py` | 汇总 unitprobe 的输出 |

---

## 3. 已证实的结论（不要重新论证）

### 3.1 unit 资源的结构

```
unit 资源（TypeId = 0xE0A48D0BE9A7453F）
 ├─ Streams[]     顶点/索引缓冲的描述（偏移/长度/stride/分量布局），字节在本条目自己的 gpu 切片里
 ├─ Meshes[]      每个 LOD 一条；引用一个 stream、挂在一个骨骼节点上
 │    ├─ Sections[]   绘制批，24 字节记录
 │    └─ Materials[]  → 64 位 MaterialId（外部资源，不在 unit 里）
 ├─ BoneInfos[]   骨骼调色板 / 逆关节矩阵
 └─ Transforms[]  骨骼节点层级（按骨骼名哈希索引）
```

**section 记录布局（24 字节，`UnitMeshReader.cs:679-686`，composite 路径 `:415-422` 相同）**：

```
+0  materialIndex   +4  vertexOffset   +8  numVertices
+12 indexOffset     +16 numIndices     +20 groupIndex
```

**要改的是 `+16`**。偏移**相对该条目自己的 TOC 切片**，不是相对整个 patch 文件。

### 3.2 section 是什么，不是什么

* 一个 mesh 的所有 section **共用同一个顶点区间**（实测 `vtxOff=0, nVtx=24424` 全一样），
  只有索引区间和材质不同。
* **section 不是装饰**。作者原始包里头盔 LOD0 有 22 个 section 但只有 12 个材质，
  一个材质跨 3–4 个 section。
* **装饰 → section 是一对多**：一件装饰用 k 个材质就落成 k 个 section，且**顺序连续**。
* 这个 1:1（装饰 ↔ section 组）**只对我们自己用 vpk2patch + manifest 构建的包成立**
  （实测 11 个 section ↔ 11 个 manifest group，三角形数与材质 ID 逐个相等）。

### 3.3 显隐机制（核心结论）

| 结论 | 证据 |
|---|---|
| `section.NumIndices = 0` 隐藏该 section 的几何 | 全 patch 683 个 section 清零 → 整个角色模型消失 |
| 粒度是 section 级，可以只掉一块 | 清 `0x9841b16544c8fbae` 的 section 0..12 → 胸部躯体消失、其余完好 |
| 引擎在**加载时**解析 TOC，之后不回读 | 运行期写内存 → 画面不变 |
| **写入 + 一次重载 = 生效** | 写完内存后换装一次再换回 → 胸部消失 |
| 重载是**重新解析内存缓冲**，不是重新读文件 | 同上（文件当时是原始状态） |
| 内存里有 TOC 副本，与文件 99.19% 相同 | 推导基址 `0x…90080`，30003/30248 字节相同，差异只在头部 |
| 定位方法：扫 section 记录的 48 字节签名 | `基址 = 命中地址 - 0x6e6c`，用首个目标的当前值复验 |

### 3.4 容量与合并

* 键预算：`3 bank × 10 slot = 30`，身体段 18。
* 每 (kit, body 变体) 处理 20 个 Piece（三个 20 项栈数组，**零余量**，改常量会踩栈 cookie）。
* `VertexBufferId` / `IndexBufferId` 是**常量**（`0x06ffffff` / `0x07ffffff`），不含身份信息。
* **合并必然复制顶点**：跨 unit 没有共享通道，`CanonicalDecorationUnitAppender` 会重新编码顶点。
* 一个 unit 承载一个槽位的全部装饰时，顶点数据是 **1 份**（不翻倍）——因为不再同时存在独立的单件 unit。

### 3.5 反作弊边界（实测）

| 行为 | 可用 |
|---|---|
| 读游戏内存（自己进程，`ReadProcessMemory`） | ✅ |
| 写游戏**数据**（注册表 blob、堆里的 TOC 副本） | ✅ |
| 替换**资源资产**（patch 归档） | ✅ |
| 改 **mod 自己的** patch 文件（管理器部署的是符号链接） | ✅ |
| 改**原版**资源文件（磁盘 `dl_bin`） | ❌ 哈希校验 |
| 改**代码段**（`VirtualProtect` + 写） | ❌ 两次实测被判「可疑程序」 |

**结论：只做数据写入。永远不要碰代码段，也不要外部改 DLL。**

---

## 4. 设计

### 4.1 核心数据模型：三层掩码

```
① 护甲档案级的「来源掩码」
   键： (unit 资产键, section 序号, section 内容指纹)
   值： 禁用
   性质：惰性、与 mod 无关、换 mod 后仍保留

② 映射目标：默认继承来源掩码（实时继承，不是快照）

③ 映射目标：独立掩码（覆盖）
   键： (该目标, unit 资产键, section 序号, 指纹)
   生效：不再继承 ①
```

**生效规则**

```
对某个映射目标：
  maskMode == override ？ 用 ③
                       ： 用 ①（实时，来源改了目标立刻跟着变）
```

**只存"禁用"**，不存"启用"。缺失 = 启用。这样新出现的 section 自动可见。

**section 内容指纹** = `materialId + triangleCount`。应用时指纹对不上就**跳过该条**。
作用：把"换 mod 后关错部件"降级为"设置静默失效"。

**section 序号的定义** = 该 mesh 的 section 列表下标（0 起），应用时**对所有 LOD 用同一个下标**。
（实测各 LOD 的 section 顺序一致。）

**整体禁用判定**：一个 unit 的所有 section 都被禁用 ⇒ 该 unit 视为禁用 ⇒ **不注册它的 Piece**
（这样引擎根本不会加载它，省显存；注意这条的显存收益**尚未实测**，见 §7）。

### 4.2 树结构（编辑器与管理器共用）

树的输入 = **该护甲档案最终的胜出 unit 列表**（不是按 mod 组装）。

```
套组（头盔 / 身体）
 └─ 身形变体（健壮 / 纤细）           ← 头盔这一支没有变体
      └─ 槽位（胸部 / 胯部 / 左腿…）
           └─ 装饰（胸部0、胸部1）      ← 一件可开关的装饰
                └─ section（本体/默认、内衣、外套…）  ← 真正的最小单位
```

**规则**

* 层次信息全部来自 unit 自带的语义：`slot` / `pieceType` / `bodyType` / `weight`
  （`UnitMeshSemanticInfo`），**不需要用户填**。
* **legacy unit（没有装饰级分组信息）在 UI 层折叠成一个节点「默认」**，
  勾选它等于勾选该 unit 的全部 section。**数据层保持真实**（有多少 section 就写多少），
  否则以后想细化就得重新生成映射表。
* `bodyType = Any` 的装饰会**同时出现在健壮和纤细两支下**，是同一个身份，
  加**锁链图标**标示；在一处切换，两处同步。
* 单身形装饰只出现在对应身形下。

### 4.3 可视化编辑器（本轮新增应用）

**形态**：管理器内的**独立窗口**（不是独立进程），`Show()` 非模态，**不锁主窗口**。
渲染器放在 `IPreviewRenderer` 接口后面，将来要拆进程或换 D3D 宿主时编辑器其余部分不用改。

**单实例**：已打开编辑器时再请求打开别的护甲 → 提示"请先关闭当前编辑器"，并把已有窗口激活到前台。

**启动条件**：该护甲在管理器里的角色 ∈ {视觉来源, 映射目标}。
**不要求**已编译进注册文件。右侧护甲库列表里的条目不提供入口。

**渲染规格**

* **纯几何，不做材质**。绑定姿势直接显示，不额外计算动画。
* **固定 LOD0**。
* 选中 = 亮色实心；其余 = **压暗到 10~15% 灰**。**不做描边**（已决定）。
* 两种身形放在世界中心**左右各 1m**。
* 3D 对象**不可交互**，只读。
* 相机：中键拖动 = 以当前焦点旋转；Shift+中键拖动 = 平移；滚轮 = **以鼠标位置为基准**缩放。

**UI 规格**

* 树**无背景**，作为叠加层浮在 3D 视图表面。
* 切换启用：左键点条目**最右侧的勾选框**，或右键菜单。
* 点树条目 → 3D 里对应网格高亮，其余压暗。
* 底部/角落有**保存**按钮：保存参数给管理器，关闭窗口。
* 打开时**读条一次**（解析 unit、建缓冲），之后编辑过程实时重绘，不做帧缓存。

**技术选型建议**：WPF 自带 `Viewport3D`（Media3D）。每个 section 一个 `GeometryModel3D` +
`DiffuseMaterial`，天然按 section 上色；不需要 D3D interop、不需要第三方依赖。

### 4.4 旧 UI 的处置

**移除**护甲映射卡片的表单内容，对应卡片内直接换成**同一棵树**（无 3D 视图，轻量树）。

但下面四类能力**必须先安置好**，否则会变成"界面简单了但没法用"：

| 现在住在旧 UI 里的 | 建议去处 |
|---|---|
| 映射关系（source → target） | 保留，放在护甲条目上（映射本来就是产品核心概念） |
| 冲突处理（多个 mod 抢同一个 unit） | 树/列表上标出"胜出者"，或槽位层放来源选择 |
| 校验与错误 | 树节点标红 + 底部状态条 |
| 应用/回滚、诊断命令 | 工具栏按钮 + CLI |

护甲条目缩略图**异步计算**后替换占位图，不要同步阻塞列表。

### 4.5 功能 1 / 3（装饰合并、头盔合并）

* 输出的 overwrite **必须包含 section 掩码**。
* **不提供**切换 section 显隐的 UI/CLI —— 它只负责生成，显隐由功能 2 的编辑器负责。
* 外部的"变更是否启用"能力**暂时保留**，后续再议。

### 4.6 迁移（旧 mod 怎么进新体系）

**只迁移元数据**，不做资产重建。

**旧 mod = 粗粒度装饰**：每个 unit 贡献一个装饰，拥有该 unit 的全部 section。

> 注意措辞：不是"单 section"，是"单装饰 = 整个 unit 的 section 集"。
> 一个 unit 的 section 不能合并成一个（材质不同）。

**关键解锁点**：粗粒度映射表**可以从构建好的 patch 里直接生成，不需要源资产**
（`tools/unitprobe --emit-map` 已经做过一遍）。

**映射表位置**：`<mod目录>/sections.json`（随 mod 走）。
里面**必须**写 patch 与 gpu_resources 的 SHA256；运行时校验失败则**拒绝应用**并在 UI 报错。

**迁移流程**

```
CLI 提供：
  hd2mod library migrate plan     --manager-root <目录> --output migrate-plan.json --json
  hd2mod library migrate validate --plan migrate-plan.json --manager-root <目录> --json
  hd2mod library migrate apply    --plan migrate-plan.json --manager-root <目录> --confirm --json
```

* 所有逻辑在 CLI 里，**不要写一个含业务逻辑的外部脚本**（.ps1 会撞执行策略和杀毒软件）。
* 管理器启动检测到旧库 → 展示迁移摘要（版本、mod 数、预计耗时）→ 用户确认 → 调用 CLI。
* **逐个 mod 跳过并提醒**：失败不拖垮整库。
* 每个 mod 有独立 `schemaVersion`，被跳过的保持 v1 标记，以后可重试。
* 库整体版本只在"所有 mod 都被成功读取过（迁移成功或明确跳过）"时才升。
* 被跳过的 mod **可见但不可启用**，并显示具体原因。
* 迁移要可中断可恢复：复制式（新库建在旁边，成功后整体替换，旧的留 `.bak-<版本>`）。
* 失败要**锁死**，不能无限重启循环。

---

## 5. 分阶段落地

```
阶段 0  测量（决定方案上限，最优先）
        ├─ 重载成本：写内存 → 重载 的耗时；顶点缓冲是否重传
        └─ 注销 Piece 是否真的释放显存
阶段 1  schema v2 定稿（映射表 + 三层掩码 + 指纹）
阶段 2  生成器改造：装饰合并 / 头盔合并 输出带掩码的 overwrite
阶段 3  运行时：掩码应用 + 整体禁用判定 + 并入现有 apply 事务
阶段 4  CLI：衣柜编译、掩码编辑（JSON 级）、迁移
        ↑ 到这里产品已经能用，只是没有界面
阶段 5  编辑器窗口：树 + 勾选 + 保存（先用占位预览）
阶段 6  3D 视口：纯几何 + 压暗
阶段 7  新护甲映射 UI（替换旧界面）
```

**为什么这个顺序**：阶段 0-4 决定产品成不成立，且全部可无界面验证；
阶段 5-7 是体验层。反过来先做 UI，可能在阶段 0 发现性能不可接受就白做了。

---

## 6. 关键代码位置

| 用途 | 位置 |
|---|---|
| section 解析 | `HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshReader.cs:679-686`（composite 路径 `:415-422`） |
| unit 写入 | `.../UnitMesh/UnitMeshWriter.cs`、`.../Canonical/CanonicalUnitRebuilder.cs` |
| 装饰合并 | `HD2ModCore/Infrastructure/DecorationHostRebuildService.cs:208`、`ArmorNormalizationService.cs:145`、`HelmetToArmorMergeService.cs:313` |
| Lua 打包（可复用） | `HD2ModCore/Infrastructure/LuaDeploymentService.cs:549-611`（含 `StingrayHash`） |
| 运行时 Lua | `HD2RuntimeArmorOverride/src/armor_registry_runtime.lua`（1270 行） |
| 自动重载 | `HD2RuntimeArmorOverride/src/armor_refresh_route_probe.lua`（`auto_refresh.published(revision, changed)`） |
| 协议格式 | `HD2RuntimeArmorOverride/src/armor_registry_protocol.lua` |
| Lua 源装配顺序 | `HD2ModCore/Infrastructure/RuntimeArmorRegistryLuaSource.cs`（嵌入式 `src/*.lua` 按固定顺序拼接） |

**Lua 的帧回调**：运行时用 `rawset(_G,'update',wrapper)` 包装全局 `update`。
（MDL 沙箱里则必须用普通赋值 `update = fn`，见 §8。）

---

## 7. 待测 / 未知

1. **重载时顶点缓冲会不会重新上传？** 决定每次切换的卡顿量级。**最优先。**
2. **注销 Piece 是否真的释放显存？** 引擎可能缓存已加载资源。若缓存激进，该优化无意义。
3. **`tone` 字段**（`model_piece` 的第 6 个参数）的派生来源。可能要挪到 mod 层配置。
4. **`composition_mode`（`full` / `existing_only`）**在新模型下是否可以自动判断。
5. **`default_base` 字段**的语义（协议里标注为"编辑器遗留标志，不参与原生布局"）。
6. 一个槽位一个来源的**实际体验**是否够用（这是新模型唯一的真实代价）。

---

## 8. 踩过的坑（务必避免重犯）

### 8.1 PowerShell 覆盖只读文件

`Copy-Item -Force` 覆盖只读目标时**会保留目标的只读属性**，随后
`File.WriteAllBytes` 抛 `UnauthorizedAccessException`。如果用 `Select-String` 过滤输出，
**异常行会被滤掉**，看起来像"写入成功"。

**规矩**：改完文件**必须验证 SHA256 变化**，不能只看工具自报的写入条数。
清只读用 `[IO.File]::SetAttributes($p,[IO.FileAttributes]::Archive)`。

### 8.2 内存扫描器

* **必须先拍区域快照再扫描**。边枚举边读时 `VirtualQueryEx` 一次瞬时失败就 `break`，
  后面所有高地址区域静默丢失，表现为"扫完没找到"但不报错。
* **分块重试的下限必须夹到区域大小**，否则小于下限的区域被整体跳过（游戏堆里大量小块）。
* `ReadProcessMemory` 只要范围内有一页不可读就整个失败，必须逐级缩小重试。

### 8.3 MDL 沙箱

* **装帧回调必须用普通赋值 `update = fn`**。MDL 用环境代理的 `__newindex` 捕获
  `update`/`shutdown` 的赋值；`rawset` 会绕过代理，MDL 报
  "installed no update callback"。
* **MDL 改写了 `rawset`**（`env.rawset` 会标记 `mod.globals`），
  所以 `rawset(_G,...)` 实际写的是真实全局表，可能顶掉别的 addon 的钩子。
* 只启用一次即可，`MDL.cfg` 的 `enabled` 会持久化；`ui.auto_reload = true` 但只在页签打开时轮询。
* 不要在 MDL 界面里乱点其他按钮——它改 ESC 菜单原生控件，点错容易崩。

### 8.4 中文文件

* **绝对不要**用 `Get-Content -Raw | Set-Content` 处理 UTF-8 中文文件（会按 ANSI 解码，把文件搞坏）。
  用编辑工具改。
* PowerShell here-string 里带 `'''` 和反斜杠会炸。多行脚本用文件写，不要内联。

### 8.5 Lua 模板

用 `str.format()` 套 Lua 模板会撞上表构造器的花括号。用唯一占位符 + `str.replace()`。
（本次就因为 `{{}}` 残留导致 `table.concat` 拿到一个空表而报错。）

### 8.6 部署与符号链接

管理器部署的是**符号链接**：

```
data\9ba626afa44a3aa3.patch_0 → E:\mod\<modId>\9ba626afa44a3aa3.patch_0
```

库里的目标文件是**只读**的（防篡改标记）。`Get-ChildItem` 对符号链接报的长度可能是 0，
用 `os.stat` / Python 读才准。

---

## 9. 当前状态（交接时刻）

* **已部署**（活动配置）：`patch_0` = Lunalice 几何，`patch_1` = MDL（Lua 加载器），
  `patch_2` = 护甲注册表运行时 Lua。
* **库里的 Lunalice patch 已还原为原始状态**，只读属性已恢复，
  备份在 `E:\tmp\hd2-mem\lib-patch_0.orig`（SHA256 `280DC065F3C667F2…`）。
* **MDL 里有一个测试探针**：`%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_toggle_probe\mod.lua`，
  已在 `MDL.cfg` 里 `enabled=true`。它会在进入游戏后自动运行、把躯干 52 个 section 写 0。
  **新进程接手时建议先删掉它**（或把 `enabled` 改 false），否则每次进游戏躯干都会缺一块。
* 注册表运行时状态：`state=ready`、`revision=123`、`blob_base` 已知、`record_count=411`。

---

## 10. 相关笔记（都在 `HD2RuntimeArmorOverride/notes/`）

| 文件 | 内容 |
|---|---|
| `numindices-is-the-visibility-switch-20261017.md` | `NumIndices=0` 隐藏几何、粒度是 section 级（**核心证据**） |
| `runtime-toc-write-has-no-effect-20261017.md` | 运行期写入必须配一次重载；重载是重解析内存缓冲 |
| `unit-toc-found-in-memory-20261017.md` | 内存里 TOC 副本的位置、99.19% 相同、定位方法 |
| `per-decoration-runtime-toggle-works-20261017.md` | **最终链路总结**（先读这份） |
| `decoration-section-map-20261017.md` | 映射表 schema 与生成方式 |
| `capacity-problem-may-not-exist-sections-are-toggles-20261017.md` | 43 件其实已经在 7 个 unit 里全可见 |
| `armor-bank-code-evidence-20261017.md` | bank / 30 键 / 20 项栈数组的代码级证据 |
| `unit-toc-cannot-reference-another-unit-20261017.md` | 跨 unit 无法共享网格，合并必复制顶点 |
| `unit-pool-cost-analysis-20261017.md` | 掉帧分析：固有成本是几何量，不是链路 |
| `resource-memory-copy-cannot-expand-capacity-20261017.md` | 资源文件里没有容量字段 |

---

## 11. 复现关键实验的步骤

### 11.1 离线验证「某 section 控制哪块几何」

```powershell
# 1) 列 unit 与 section（--sections 打印每个 section 的偏移/数量/材质）
cd E:\Data\source\repos\WpfApp1\tools\unitprobe
dotnet run --project unitprobe.csproj -c Debug -- <patch_0> --only 0x<fileId> --sections

# 2) 就地改某个 section 的 NumIndices（支持 lod/section 通配与区间）
dotnet run --project unitprobe.csproj -c Debug -- <patch_0> --patch-numindices '0x<fileId>:*:0-12'

# 3) 改完必须确认 SHA 变了
(Get-FileHash <patch_0> -Algorithm SHA256).Hash
```

### 11.2 运行期开关（Lua 探针）

```powershell
# 生成探针 Lua（会打印签名偏移与目标数量）
python HD2RuntimeArmorOverride\scripts\make_section_toggle_probe.py `
  --patch <patch_0> --file-id 0x<fileId> --sections 0-12 `
  --out E:\tmp\probe.zip --dump-lua E:\tmp\probe.lua

# 语法校验
luajit -e "print(loadfile('E:/tmp/probe.lua') and 'OK' or 'ERR')"

# 放进 MDL 的实时目录，进游戏后 MDL 自动启动
copy E:\tmp\probe.lua "%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_toggle_probe\mod.lua"

# 看结果
type "%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\section-toggle.status"
```

探针成功时的日志形如：

```
signature hit 0x…
candidate base 0x… first target current=14304 expected=14304
wrote 52/52 numIndices to zero
APPLIED
```

### 11.3 让改动生效

运行期写完内存后，**在游戏里换装一次再换回来**（或触发一次协议 revision 变化让
`auto_refresh` 重载），几何才会更新。

---

## 12. 给接手者的三条建议

1. **先读 §3 和 §7**。已证实的不要再验证，待测的优先测——尤其是"重载成本"，
   它可能改变整个方案的可行性判断。
2. **每一步都留可回滚的备份**。这个项目里我已经两次因为"以为写进去了其实没写"
   得出错误结论。**改完必须独立验证**（SHA、回读、截图）。
3. **不要碰代码段**。所有能力都能通过数据写入实现，这一点已经反复验证。
