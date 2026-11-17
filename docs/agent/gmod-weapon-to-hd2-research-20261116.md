# GMod 武器 → HD2：第一轮技术调研与路线（2026-11-16）

> 范围：调研 + 架构设计。**本轮不改代码、不建仓库。** 交由 GPT 独立审核后再决定实施。
> 目标：尽可能保留武器原始视觉（模型 / 材质 / 粒子 / 可选动画）；不涉及伤害与玩法。
> 证据标记：**[F]** 已验证事实（附出处）· **[I]** 推测（写明依据）· **[E]** 待实验。
>
> **修订（2026-11-16，GPT 审核后）**：第一轮通过。§0/§2 中「不能写回 / 不能创建」改为「已调查工具尚未证明」；粒子独立性提为最高优先；§5 的集成方式由 `gmod-weapon-plugin-architecture-20261116.md` 取代；§5.3 能力层扩为四维度；§6 已决定。下一步见 `gmod-weapon-validation-plan-20261116.md`。

---

## 0. 结论先行

1. **模型 + 材质是唯一可以立刻落地的部分。** 现有 L4D2 管线的 Source 读取层原样适用于 GMod（版本范围已覆盖 v44–v53，GMod 主流是 v48/v49）；缺的是「武器」侧：HD2 武器 Unit 的骨骼/部件事实、Source 的 attachment/sequence 读取、手臂网格剥离。
2. **粒子是最大的未知数，但不是死路。** HD2 `.particles` 已被社区部分逆向（emitter：burst/rate；visualizer：billboard/light/mesh，均带 material/unit ID）[F]，但**已调查的工具尚未证明能从零创作 HD2 粒子**（HD2SDK-CE 有读写对称的粒子序列化，未证明可新增结构）。所以路线是**模板克隆 + 参数改写**，不是 PCF 逐算子翻译。**比还原度更优先的是独立性**：枪口粒子被多把武器共享，且引用很可能在 `.dl_bin` 触发表里，而 `.dl_bin` 自 2026-11-11 起写入即闪退。
3. **动画不要走「写 HD2 `.animation`」。** 已调查的工具尚未证明能把**新**动画写回并被游戏接受（filediver 只导出；HD2SDK-CE 有 `animation.py` 的 `save()`，但该仓库无许可证、实机可用性未证）[F/E]。可行路线按稳健度排序：① 把 GMod 部件**绑到 HD2 原武器的同功能骨**上，借用 HD2 原生动画（纯资源，零 Lua）；② 用白名单 Lua `Unit.set_local_pose` 驱动额外骨（物理骨运行时已实机证明这条路可用）[F]；③ 二进制 `.animation` 写入，只当研究项。
4. **Lua 只做增强，且必须集中在一个「能力层」里。** 白名单里有 `World.create_particles / link_particles / destroy_particles`、`Unit.set_local_pose`、`Unit.animation_event` 等 [F]；但**「玩家开火」这个时机没有直接 API**，能否从动画事件流拿到是首要实验 [E]。
5. **架构（已修订）**：独立 GPL-3.0 仓库产出插件 DLL，管理器一次性接入插件宿主与更新机制，之后转换器独立发版；先用现有项目验证，再按实际依赖提取代码。详见 `gmod-weapon-plugin-architecture-20261116.md`。
6. **思考强度建议：** 本轮 Medium 足够。**路线 P 的粒子映射设计与 §5.2 的代码切分清单**进入正式设计时建议切 High。

---

## 1. 现有能力盘点（本仓库）

| 能力 | 位置 | 状态 | 对 GMod 武器的适用性 |
|---|---|---|---|
| VPK 读取 | `HD2SourceModel/VpkArchive.cs` | [F] 可用 | GMod 用 **GMA** 打包，不是 VPK；需新增 GMA 读取（格式简单，[I] 一天量级） |
| MDL 读取 | `HD2SourceModel/SourceModelReader.cs` | [F] 版本 v44–v53，骨骼、bodypart、材质、jigglebone | 版本已覆盖 GMod。**缺：attachment、sequence/animation、`$includemodel`、hitbox** |
| VVD / VTX | `SourceVertexReader.cs` / `SourceIndexReader.cs` | [F] 可用，VTX 步长 25/33 自适应 | 直接复用 |
| VMT → HD2 材质 | `HD2SourceMigration/VpkConversion/SourceMaterialConverter.cs`、`Hd2MaterialTemplateLibrary.cs` | [F] 可用（护甲语境） | 复用；武器需补 HD2 武器材质模板（武器涂装系统会不会与之冲突 [E]） |
| VTF 读取 | `VtfFile.cs` | [F] DXT1/3/5 等直转 BC，含 7.1 兼容 | 直接复用；粒子 sprite 的多帧（`frames`）已能读出帧数，**HD2 粒子能否播序列帧未知** [E] |
| 骨骼重定向 | `HD2SourceMigration/Retarget/*` | [F] 针对 ValveBiped → HD2 人形骨架 | **武器基本用不上**：武器是刚体部件，不是人形蒙皮 |
| 弹簧骨声明 + Lua 驱动 | `PhysBoneContainer.cs`（`HD2SRG01`/`HD2PBX01`）、`HD2PhysBoneCompat/src/*.lua` | [F] 实机可用 | **可直接作为「动画/弹簧」的运行时载体范式**：版本化二进制声明嵌进 patch，Lua 只读声明 |
| Lua 能力探测模式 | `physbone_compat.lua`（`rawget` + `type == "function"` + `pcall`） | [F] 已在用 | 作为能力层的现成写法 |
| HD2 Unit/patch 写出 | `HD2ModAdaptation`、`UnitAuthor.cs`、`VpkPatchAssembler.cs` | [F] 护甲可用 | 武器 Unit 未覆盖：管理器对武器 archive「只知道存在，不知道是什么」（`professional-workbench-architecture-20261026.md` §3，覆盖率 8.4%）[F] |
| HD2 粒子 / 动画 / state_machine | — | **[F] 仓库内零知识** | 全部要从外部补 |

**已知实机纪律（来自物理骨运行时，直接适用于武器 Lua）[F]**：
- 对已销毁 Unit 调方法 = 原生 AV，**`pcall` 拦不住** → 每帧先 `Unit.alive`。
- 引擎句柄不能缓存（约 0.1 秒失效）。
- 写骨后必须 `World.update_unit` 才可见；必须从缓存的基准姿态算，不能累乘。
- 加载界面期间 `update`/`render` 不跑 4–15 秒。
- 这些都意味着：**能力层的「降级」不能只靠 `pcall`，必须在调用前用结构性检查避开 AV。**

---

## 2. 外部项目调研

### 2.1 HD2 侧工具 [F]

| 项目 | 许可 | 与本任务相关的能力 | 局限 |
|---|---|---|---|
| [HD2SDK-CommunityEdition](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition)（Blender，fork 自 io_scene_helldivers2） | **无许可证文件**（API `license: null`）→ 只能参考，不能移植 | 纹理 / 网格 / 材质导入导出；源码另有 `stingray/animation.py`（含 `save()`、位置/旋转/缩放压缩）、`stingray/particle.py`（读写对称 `Serialize`）、`stingray/state_machine.py`（`save()`） | README 未声明动画/粒子功能；这些序列化能否产出游戏可接受的新资源未证 [E] |
| [hd2-particle-modder](https://github.com/RaidingForPants/hd2-particle-modder)（Python） | MIT | 修改已有 `.particles`；附 `imhex_pattern.txt` 二进制模板 | 依赖 SDK 提取/回填；**未声明能新增 emitter** |
| [filediver](https://github.com/xypwn/filediver)（Go） | BSD-3 | 导出模型（含骨骼、贴图、动画，需开关）、prefab；解析 `animation_event_trigger_settings`（动画事件 → 音效/粒子/镜头震动/可见性组），附动画事件导出工具 | **只导出，不写回** |

`hd2-particle-modder/imhex_pattern.txt` 给出的 `.particles` 结构要点 [F]：
- Emitter：`Burst(0x0C)`（10 点时间–数量曲线）、`Rate(0x0B)`（初始速率 + 2 条曲线），另有 5 种未知类型。
- Visualizer：`Billboard(0)`（带 `materialID`）、`Light(1)`、`Mesh(2)`（带 `unitID / meshID / materialID`），另有 2 种未知。
- 曲线组件：`Graph`、`ColorGraph`，每条最多 10 个点。

社区佐证 [F]：HD2 枪口火焰粒子**被多把武器共享**（[Clear Sight](https://www.nexusmods.com/helldivers2/mods/18344) 说明）。→ 直接覆盖原生粒子资源会**污染其他武器**，与护甲映射遇到的「共享 Unit 污染」是同一类问题。

### 2.2 Source 侧工具

由 scout 子代理（claude-haiku-5-5 / high）抓取 GitHub 页面、API 与源码文件汇总，2026-11-16。**VDC（developer.valvesoftware.com）对抓取全部返回 403**，凡涉及 VDC 的只来自搜索摘要，按 [I] 处理。

| 项目 | 语言 / 许可 | 最近活动 | 已核实能力 | 对本项目的价值 |
|---|---|---|---|---|
| [SourceIO](https://github.com/REDxEYE/SourceIO) | Python / MIT | 2026-11-14 | README：MDL、VTF（导入/导出）、VMT 导入，标注 GMod「Full support」；`library/models/mdl/structs/local_animation.py`（2026-10-07 提交）解码 RAW_ROT / RAW_ROT2 / ANIM_ROT / ANIM_POS（RLE）、分段、外部 `.ani` 块、FRAMEANIM、delta 标记；IK 规则字段只读入未应用 | **动画解码的首选移植来源**（MIT，核心约 400 行）。移植前先做 V5 对照 |
| [srctools](https://github.com/TeamSpen210/srctools) | Python+Cython / MIT（`libsquish/` 除外） | 2026-11-16 | `particles.py`：PCF v1/v2 读取 + 导出为 DMX Element 树；`dmx.py`；`mdl.py` 只读 v44–49 **元数据**（sequence、activity、event、材质、skin、include），**不读骨骼/动画/attachment/几何** | **PCF 解析的最佳参考**；MDL 的 sequence/event 元数据可补我们 reader 的空缺 |
| [ValvePCF](https://github.com/pySourceSDK/ValvePCF) | Python / GPL-3.0 | 2023-08-05 | PCF 读写；文档自述「schema support is not yet there」 | 与本项目同为 GPL-3.0，可参考；活跃度低 |
| mdl-to-vmdl-converter | — | — | **未找到同名仓库**。最接近：[source2utils](https://github.com/AlpyneDreams/source2utils)（无许可，2020，**不解析 MDL**，只生成让 Source 2 编译 `.mdl` 的 `.vmdl`）；[source1import](https://github.com/kristiker/source1import)（MIT，2025-02，材质/模型/粒子导入 Source 2） | 目标是 Valve 自家引擎，依赖 Source 2 编译器，**对 HD2 几乎无可复用** |
| [Crowbar](https://github.com/ZeqMacaw/Crowbar) | **VB.NET** / **CC BY-SA 3.0**（LICENSE.txt） | 2026-11-08 | 社区说法：v48 动画/物理/网格反编译较完整，v49 动画部分支持 [I] | 只作对照工具（反编译出 QC/SMD 人工核对），**不抄代码**（许可证不适合代码复用） |
| [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat) | C# / MIT | 2026-11-15 | README：仅 Source 2 | 不适用 Source 1；附带线索 [Datamodel.NET](https://github.com/ValveResourceFormat/Datamodel.NET)（C# DMX 库，自称支持全部已知版本，**未核实**）——若属实，是 PCF 读取的 C# 现成底座 [E] |

**GMod 约定（已核实，Facepunch wiki）**：
- 手臂骨：`ValveBiped.Bip01_{L,R}_{Clavicle,UpperArm,Forearm,Hand}` + 可选手指链（[ValveBiped Bones](https://wiki.facepunch.com/gmod/ValveBiped_Bones)）→ 剥离手臂时按此名单识别。
- 枪口火焰事件：`WEAPON:FireAnimationEvent(pos, ang, event, options, source)`，示例处理 5001/5011/5021/5031，attachment = `floor((event-4991)/10)`（[wiki](https://wiki.facepunch.com/gmod/WEAPON:FireAnimationEvent)）。→ **枪口位置与触发时机都编码在 MDL 的 sequence event 里**，转换器能离线拿到。
- PCF 加载：`game.AddParticles` + `PrecacheParticleSystem`（[wiki](https://wiki.facepunch.com/gmod/game.AddParticles)）。→ 武器用哪个粒子**写在 SWEP 的 Lua 里**，不在资源里；转换器需要**静态扫描 SWEP Lua** 找粒子名（不执行它）。
- 有分歧：event 5001 对应 attachment 0 还是 1（wiki 公式得 1，TWHL 帖子说 0）→ 以样本实测为准 [E]。

**缺口**：
- SourceIO 已实现 v49 压缩动画解码（见上表，代码已核实），但未经独立对照；IK 规则未应用。动画路线 A2 先做 V5 对照再决定移植。
- 二进制 DMX/PCF **没有权威规范**；srctools（MIT）是最可读的实现。

---

## 3. 兼容障碍清单

| # | 障碍 | 类别 | 证据 |
|---|---|---|---|
| B1 | GMod 武器有三种模型：`v_`（第一人称，手臂烘在模型里）、`c_`（第一人称，手臂由玩家 `c_arms` 合并）、`w_`（第三人称）；HD2 武器第一/第三人称是否同一 Unit 未知 | 模型 | [E] |
| B2 | `v_` 模型含手臂网格，必须剥离；`c_` 模型的骨架里带手臂骨（ValveBiped），武器部件与手臂骨的父子关系要拆开 | 模型 | [I]，依据 GMod 惯例，待样本确认 |
| B3 | HD2 武器 Unit 的骨骼/部件/attachment（瞄具、弹匣、枪口）事实本仓库没有 | 模型 | [F] 覆盖率 8.4% |
| B4 | HD2 武器有涂装/定制系统（`generated_weapon_customization_settings`）；换模型后涂装、瞄具定制件如何表现未知 | 材质/模型 | [E] |
| B5 | PCF 是「算子图」（emitter / initializer / operator / renderer 数十种），HD2 `.particles` 是固定结构的 emitter + visualizer + 曲线；**一对一翻译不可能**，只能近似 | 粒子 | [F] 结构对比；近似程度 [E] |
| B6 | 已调查工具未证明能从零创作 HD2 粒子，现阶段只按「克隆已有粒子改参数」设计 | 粒子 | [F] 工具现状；引擎层面未判定 |
| B6b | 枪口效果引用很可能在 `generated_animation_event_trigger_settings.dl_bin`；`.dl_bin` 自 2026-11-11 起写入即闪退 → 「只让目标武器指向新粒子」可能无法通过资源实现 | 粒子 | [F] 闪退；引用位置 [I]，V1 验证 |
| B7 | 枪口火焰由谁触发：HD2 由武器设置引用粒子（玩法数据）；改引用 = 碰玩法数据，与「不改玩法」冲突 → 只能**改被引用的粒子资源本身**，或 Lua 侧自行生成 | 粒子 | [I] |
| B8 | Lua 侧没有「开火事件」API；`Unit.animation_event` / `World.animation_event_stream_get` 能否观测到开火 | 粒子/动画 | [E] 首要实验 |
| B9 | 已调查工具未证明新 `.animation` 能被游戏接受（HD2SDK-CE 有序列化代码，无许可证） | 动画 | [F] 工具现状；[E] 实机 |
| B10 | GMod 动画按 `ACT_VM_*` 活动名 + sequence 组织，HD2 由 `state_machine` 驱动；两套状态语义不对应 | 动画 | [I] |
| B11 | Lua 运行要靠加载器（Bingus Shared Loader 或管理器 boot）；没有加载器 = 整个 Lua 层不存在 | Lua | [F] `HD2PhysBoneCompat/src/physbone_runtime.lua` 头注释 |
| B12 | 白名单之外（含 FFI 写内存）有封号风险 → 武器 Lua **不得**沿用护甲注册表那种 FFI 路线 | Lua | [F] 用户 2026-11-12 约束 |

---

## 4. 三条独立验证路线

每条路线都能**单独成功、单独失败**，互不阻塞。顺序 = 优先级。

### 路线 M：模型 + 材质（P0，纯资源，不依赖 Lua）

- **前置调查**：选 1 把 HD2 原生步枪（如 AR-23），用 filediver 导出，列出它的骨骼树、mesh、材质槽、LOD；确认第一/第三人称是否同一 Unit（B1）。
- **最小样本**：1 把结构简单的 GMod `c_` 步枪（单 bodygroup、无 jigglebone、3 张以内贴图）。
- **做法**：GMA 解包 → 复用 MDL/VVD/VTX/VMT/VTF 读取 → 剥离手臂 → 每个部件**刚性**绑到 HD2 原枪对应骨（枪身→根骨、弹匣→弹匣骨、枪栓→枪栓骨）→ 以原枪 Unit 为模板替换 mesh 与材质 → 打成 patch。
- **成功标准**：进游戏后第一/第三人称都显示新枪；持握位置正确（误差人眼不可见）；换弹时弹匣随 HD2 原生动画移动；无崩溃；不影响其他武器。
- **失败退出**：① HD2 武器 Unit 无法以护甲同样方式替换 mesh（Unit 结构差异过大）→ 停，回到「解析 HD2 武器 Unit」专项；② 刚性绑定后持握偏差无法用单个根变换修正 → 记录为「需要手动对齐参数」，不再自动化。

### 路线P：粒子（P1，资源优先，Lua 次之）

- **先解决独立性，再谈还原度**（GPT 审核意见）：两个问题分开验证 —— ① 能否改已有粒子的形状/颜色/材质；② 能否只让目标武器生效。②优先，且默认**禁止污染**其他武器。
- **前置调查**：用 hd2-particle-modder 的模板解析原枪的枪口火焰 `.particles`；用 filediver 导出触发表，确认引用位置与共享度（验证计划 V1）。
- **最小样本**：1 个 GMod 枪口火焰 PCF（单 emitter、sprite 渲染、无子系统）。
- **做法（P1a 资源）**：**克隆** HD2 原枪口火焰粒子 → 改写曲线（数量/寿命/尺寸/颜色）与 billboard 的材质 ID，指向由 VTF 转来的 sprite 贴图 → 让「目标武器专属」的副本被引用（如何只影响一把枪是关键问题，见 B7）。
- **做法（P1b Lua 增强，实验性）**：若 B8 实验证明能观测到开火：`World.create_particles` 在枪口生成、`link_particles` 跟随。注意：能调用粒子 API ≠ 能播放 GMod 转来的效果 —— 只能生成已打包为 HD2 资源的粒子，而且只能**叠加**，去不掉原生枪口火焰。
- **PCF→HD2 映射原则**：只映射「可近似」的子集（sprite、发射数量、寿命、尺寸/颜色随时间、速度、重力）；其余算子**丢弃并写入兼容报告**，不猜。
- **成功标准**：开火时看到颜色/形状明显接近原效果的枪口火焰；**其他武器的枪口火焰不变**；关掉 Lua 后资源层效果仍在。
- **失败退出**：① 无法做到「只影响目标武器」→ **默认禁止**，粒子降级为 HD2 原生（用户决定，2026-11-16）；② 克隆粒子改参后崩溃/不显示 → 同样降级为原生。

### 路线A：动画（P2，可选）

- **前置调查**：filediver 导出原枪动画，看 HD2 武器动画实际驱动了哪些骨（弹匣、枪栓、扳机……）。
- **A1（纯资源）**：部件绑到 HD2 原骨上，借用原生动画。属于路线 M 的副产品，**不另立任务**。
- **A2（Lua 增强）**：GMod 独有的运动部件（转管、独立枪栓、拉索）→ 离线把 sequence 烘焙成关键帧表，嵌进 patch（复用 `HD2PBX01` 容器思路）→ Lua 在可观测的状态下用 `Unit.set_local_pose` 播放。
- **A3（研究）**：写 HD2 `.animation` 二进制。仅在 A2 证明价值且有人逆向出格式后再议。
- **最小样本**：1 把带转管或独立枪栓的 GMod 武器。
- **成功标准**：A2 的额外骨在开火/换弹时运动，节奏接近原作；关掉 Lua 后部件静止但模型完好。
- **失败退出**：B8 证伪（Lua 观测不到开火/换弹）→ A2 只能做「常驻」动画（如待机旋转），或整体放弃，保留 A1。

### 4.4 验证优先级

| 顺序 | 实验 | 原因 |
|---|---|---|
| 1 | 导出并解析 1 把 HD2 原生步枪 Unit（骨骼/mesh/材质/第一人称） | 三条路线都依赖它；成本最低 |
| 2 | **B8：Lua 能否观测到开火/换弹**（只读探针：`Unit.has_animation_event`、`World.animation_event_stream_get`、`Unit.animation_get_state`） | 决定 P1b 与 A2 是否存在 |
| 3 | 路线 M 最小样本 | 交付价值最高 |
| 4 | `.particles` 克隆 + 「只影响一把枪」可行性 | 粒子路线的生死点 |
| 5 | 路线 P / A2 最小样本 | 依赖 2 与 4 |

---

## 5. 架构：独立仓库 + 嵌入式核心库

> ⛔ 已被取代（2026-11-16）：§5.1–5.2 的「submodule + ProjectReference」与「一次性复制」结论，改为「插件 DLL + 进程外宿主 + Releases 更新」与「先验证、后按实际依赖提取」，见 `gmod-weapon-plugin-architecture-20261116.md`。以下保留作记录。

### 5.1 定位：转换器是能力的所有者，管理器只是消费方

用户决定（2026-11-16）：
- 独立 GPL-3.0 仓库，由用户维护；**不做 CLI / GUI**，只提供可嵌入的 .NET 核心库。
- 管理器（以及任何第三方）**直接拉取并调用**这些能力，只负责入口、界面和产物安装。
- 转换器的更新**不需要经过管理器作者审核**；管理器侧只在首次接入时做一次集成改动。

由此推出的硬约束：**转换器仓库必须自足 —— 不得引用管理器仓库里的任何项目。** 否则每次转换器要改 HD2 写出逻辑，都得等管理器那边合并，独立维护就不成立了。

| 转换器（新仓库，能力所有者） | 管理器 / 其他消费方 |
|---|---|
| GMA 解包、Source 资源解析（含 SWEP Lua 静态扫描） | 选择 GMod 包与目标 HD2 武器 |
| 模型 / 材质 / 粒子 / 动画转换 | 进度展示、取消 |
| HD2 资源生成与离线校验 | 导入、安装、卸载产物 |
| Lua 增强模块与能力层（随产物分发） | 展示兼容报告与降级档位 |
| 单元测试、样本回归、兼容报告 | 锁定所用的转换器版本 |

### 5.2 代码复用与依赖方向

现状 [F]：
```
HD2SourceModel        （零依赖，Source 格式读取）
HD2ModAdaptation      （HD2 格式读写，仅依赖 LZ4；被管理器 8 个项目引用，87 个 .cs）
HD2SourceMigration →  HD2SourceModel + HD2ModAdaptation
```

**推荐：一次性复制所需代码进转换器仓库，从此各自演进（fork 式复用）。**

| 复用对象 | 处理方式 |
|---|---|
| `HD2SourceModel` | 整个复制（零依赖，最干净）；在此基础上补 attachment / sequence / event / 动画读取 |
| `HD2SourceMigration/VpkConversion` 中的材质、贴图部分（`SourceMaterialConverter`、`VtfFile`、`Bc7Encoder`、`Hd2MaterialTemplateLibrary` 等） | 复制所需文件；**护甲专用的重定向（`Retarget/*`、`Segmentation/*`）不带** |
| `HD2ModAdaptation` | **只复制转换器真正用到的 HD2 写出子集**（Unit / 材质 / 贴图 / patch 打包），放进转换器内的 `Hd2Formats` 子模块；粒子、动画写出是新增的，本来就只在转换器里 |

代价与对策：
- **两份 HD2 写出代码会逐渐分叉。** 这是用「不被管理器审核卡住」换来的，可以接受；转换器自己的样本回归保证自己的那份正确。若将来管理器作者愿意，也可以反过来让管理器改用转换器的 `Hd2Formats`，但这不是前提。
- **不选**「转换器引用管理器的 `HD2ModAdaptation`」，因为它会构成「子模块反向依赖宿主」的循环，也会让转换器的发布节奏重新受管理器约束。

**消费方式**：
- 管理器：**Git submodule + `ProjectReference`**。管理器全靠 sln 内 `ProjectReference` 构建，没有私有 NuGet 源 [F]，submodule 接入成本最低；升级 = 移动 submodule 指针，一行 diff。
- 其他第三方：同一仓库可另外发布 NuGet 包（可选，后期再做）。
- 管理器仓库开启了 `TreatWarningsAsErrors` [F]：转换器代码在管理器里编译时同样受约束 → 转换器仓库自己也开启，保持零警告。

**对外接口（库 API，不是进程协议）**：
- 一个入口门面，例如 `GmodWeaponConverter.Inspect(...)` / `Plan(...)` / `Convert(..., IProgress<>, CancellationToken)`。
- 请求、进度、结果、**兼容报告**都是带 `SchemaVersion` 的不可变数据对象，可序列化为 JSON —— 为了落盘、给人看、做快照测试，也方便管理器跨版本读取旧报告。
- 门面之外的类型全部 `internal`；消费方只依赖门面与这些 DTO。这是转换器能自由重构内部而不影响管理器的关键。
- 转换器 → 游戏内 Lua 的产物契约，沿用 `l4d2-hd2-boundary.md` 的三条纪律（版本化 / 单向 / 自足），声明嵌进 patch。

**建议路径**：先在本仓库（用户的 fork）建一个**不引用任何管理器项目**的兄弟目录，做最小验证，用编译期检查（项目引用白名单）守住边界；路线 M 最小样本跑通后再拆成独立仓库，并以 submodule 接回。这与用户此前「最小验证后再正式建仓」一致。

**许可证**：本仓库 GPL-3.0，fork 自 `AssestinaR/HD2ModManager` [F]。复制出的文件保留原版权头，新仓库附 `LICENSE`（GPL-3.0）和来源说明（NOTICE：哪些文件来自 HD2ModManager 的哪个提交）。外部参考：SourceIO、srctools 为 MIT，可移植（需保留其版权声明）；ValvePCF 为 GPL-3.0，兼容；Crowbar 为 CC BY-SA 3.0，**只作对照，不移植代码**。

### 5.3 Lua 能力层：检测、隔离、分级降级

> 补充（2026-11-16）：能力判断扩为四维度 —— 存在 / 时机 / 对象有效 / 实机验证，见 `gmod-weapon-validation-plan-20261116.md` §3。

**一个模块、一张表、三个规则。**

1. **能力表（集中）**：启动时一次性探测，产出 `caps = { particles = bool, pose_write = bool, anim_events = bool, ... }`。探测只用 `type(rawget(S.X, "fn")) == "function"`，**不调用**有副作用的 API。所有业务模块只读 `caps`，不直接 `rawget` 引擎表。
2. **功能声明依赖**：每个增强功能在产物声明里写明 `requires = { "particles", "anim_events" }`；任一不满足 → 该功能整体不启动，其他功能不受影响。
3. **运行期熔断**：功能内部调用失败（`pcall` 返回 false）累计到阈值 → 熔断该功能本局不再尝试，记一条日志。**AV 类崩溃 `pcall` 拦不住**，所以每帧先做结构检查（`Unit.alive`、不缓存句柄）——这是现有物理骨运行时的实机结论 [F]。

**分级（设计目标，具体哪些能独立保留待实验）**：

| 档位 | 条件 | 呈现 |
|---|---|---|
| L3 完整 | 加载器在 + 所需 API 全在 + B8 成立 | 模型 + 材质 + 资源粒子 + Lua 粒子补强 + 额外骨动画 |
| L2 部分 | 加载器在，但部分 API 缺失或熔断 | 模型 + 材质 + 资源粒子 + 仍可用的那部分 Lua 功能 |
| L1 基础 | 无加载器或 Lua 全部失效 | 模型 + 材质 + 资源粒子（若路线 P1a 成立）+ HD2 原生动画 |
| L0 回退 | 资源粒子不可用 | 模型 + 材质，粒子与动画保持 HD2 原生 |

关键性质：**L1 及以下完全不依赖 Lua**，所以白名单变化最坏只会把用户从 L3 降到 L1，不会让武器变成透明或崩溃。

转换期也要把这张表写进兼容报告：转换器知道每个功能「需要什么」，管理器据此显示「这把枪在你的环境里会以哪一档呈现」。

---

## 6. 已决定（2026-11-16，用户与 GPT 审核）

| 问题 | 决定 |
|---|---|
| 1. 代码复用 | 接受复制，但**先用现有项目验证，再按实际依赖只提取必要代码**；独立 GPL-3.0 仓库，保留原版权与许可证 |
| 2. 粒子污染其他武器 | **默认禁止**；优先独立资源，做不到就降级为 HD2 原生 |
| 3. 武器替换方式 | 替换现有 HD2 武器的外观 |
| 4. Lua 加载器 | 不绑定特定加载器，支持可用的合规加载路径 |
| 5. Lua `io`/`os` | 仍未确认；先沿用 patch 内嵌声明 |
| 6. 来源信息 | 保留基本来源元数据，不做资源分发管理 |
| 集成方式 | 插件 DLL + Releases 更新，管理器一次性接入；详见插件架构文档 |
