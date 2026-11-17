# GMod 武器 → HD2：离线实验结果（V0 / V1 / V2 / V5，2026-11-16）

> 状态：**待用户审核。** 本轮只做离线实验；没有进游戏，没有修改正式功能代码。
> 计划：`gmod-weapon-validation-plan-20261116.md`。实验脚本与少量证据：`gmod-weapon-lab-20261116/`。
> 证据标记：**[F]** 本轮实测 · **[I]** 推测（写明依据）· **[E]** 仍需实验（多为游戏内）。
> 游戏数据：`E:\SteamLibrary\steamapps\common\Helldivers 2`（文件时间 2026-11-11 19:18）。GMod：`D:\SteamLibrary\steamapps\common\GarrysMod`。

## 0. 结论

| 实验 | 结果 | 一句话 |
|---|---|---|
| V0 AR-23 结构 | **通过** | 武器是「一个刚性蒙皮的机匣 Unit + 挂在 `attach_*` 节点上的独立附件 Unit」；动画只驱动 5 根骨。部件→骨的刚性映射路线成立。新问题：**原生附件 Unit 会和 GMod 模型重叠**。 |
| V1 枪口粒子 | **通过（结论不利）** | 枪口火焰引用**只存在于 `.dl_bin`**（实体组件 + 附件增量），AR-23 的那一个粒子被 13 个实体共享，且实际按「枪口附件类型」共享。按既定规则：资源路线下**无法只改一把枪**，粒子默认降级为 HD2 原生。 |
| V2 格式往返 | **部分通过** | 动画 11/11、状态机 1/1 **字节一致**，单关键帧编辑只改 4 字节；粒子 2/2 能往返，但约一成内容是不透明块；仓库的 Unit 写出器对武器 Unit「语义等价、非字节一致」。 |
| V5 动画解码 | **通过（发现 1 个 SourceIO 缺陷）** | 自写独立解码器与 SourceIO 对 GMod `c_smg1`（v48）18 段动画逐帧比较：16 段完全一致；2 段 delta 动画的最后一帧 SourceIO 输出全零四元数（缺陷）。Crowbar 对照因权限被拦，未做。 |

对路线的影响（详见 §5）：
- **模型（路线 M）**：可行性提高；需先决定原生附件怎么处理。
- **粒子（路线 P）**：纯资源路线按「默认禁止污染」规则**关闭**；只剩 Lua 叠加（去不掉原生火焰）。
- **动画（路线 A）**：比预期乐观 —— 武器动画是**按武器族独立**的资源（AR 的 `fire.animation` 只被 AR 的状态机引用，已核对全部可解析的 435 个状态机），且格式可无损读写；「改写现有骨的动画」可能不需要 Lua [E]。

---

## 1. V0：AR-23 资源结构普查

工具：filediver v0.7.67（BSD-3，SHA-256 与 Release 摘要一致），只读导出 glb / json / raw。

### 1.1 身份与引用 [F]

- 实体 `content/fac_helldivers/equipment/primary_weapons/assault_rifle/assault_rifle`，百科组件 `prefix = "AR-23"`、`loc_name = "Liberator"`。
- Unit 同路径，哈希 `0x968211c0033dce64`。**这个 Unit 被 3 个实体引用**（`assault_rifle` + 两个未解析名称的实体 `0x3357eebb0184fdee`、`0x6db0cace51c8a0ed`）→ 换机匣模型会同时影响这 3 个实体 [F]；它们是否都是 AR-23 系列（如 P/C 型）未确认 [I]。
- 名称上只有一个武器 Unit，没有独立的第一人称 Unit [F，文件清单]；第一人称是否确实渲染同一个 Unit 仍需游戏内确认 [E]。

### 1.2 骨架（43 个节点，关键部分）[F]

```
root/boss                      ← 机匣主体（13183 个顶点绑在这里）
  attach_mag / attach_muzzle / attach_optic / attach_underbarrel / attach_ammo / attach_paintjob   ← 附件挂点
  bolt, bolt_carrier, ejection_cover, fire_selector, trigger                                    ← 机械部件
  sight, ejector/ejector_socket, IK_left, IK_right, cog/pivot                                     ← 功能点
```
组件数据：持握节点 `attach_hand_r`、背挂 `sling`、射击节点 `attach_muzzle`、瞄具 `sight`、抛壳 `ejector`/`ejector_socket` [F，entity-component-settings]。

### 1.3 网格与材质 [F]

- 单个蒙皮网格 `g_reciever`，14297 顶点，LOD0–LOD4 + 独立阴影 LOD；2 个图元（`m_weapon`、`m_rail`）。
- **刚性蒙皮**：每顶点最多 1 根骨。参与蒙皮的骨只有 5 根：`boss` 13183、`bolt_carrier` 428、`bolt` 317、`fire_selector` 260、`ejection_cover` 109。
- 材质槽 `m_weapon` / `m_rail` 各带 11 套涂装变体（`paint_scheme_*`）→ 换模型后涂装如何表现仍是 [E]。

### 1.4 动画与状态机 [F]

- 11 段动画，只驱动 `attach_mag`、`bolt`、`bolt_carrier`、`ejection_cover`、`fire_selector`（`trigger` 不动）。
- 位移幅度：`fire` 时 `bolt`/`bolt_carrier` 约 8 cm；`reload` 时 `attach_mag` 位移 0.96 m（弹匣被抽出）；`eject_cover_open` 抛壳盖转 1.48 rad。
- 状态机 3 层：
  - 第 0 层：`fire` / `reload` / `reload_fast` / `Idle`（默认，循环）/ `ammocheck`；事件 `fire`、`reload`、`reload_fast`、`ammocheck`、`reset`、`reload_done`。
  - 第 1 层：保险 / 快慢机混合；事件 `draw`、`holster`。
  - 第 2 层：抛壳盖 `open` / `close`（`fire` 事件会打开）。
  → 这就是 V3 要观测的东西：武器 Unit 上 `fire` / `reload` 事件与第 0 层状态 [I：可用 `Unit.animation_get_state` 轮询，待 V3]。

### 1.5 附件是独立 Unit [F]

- 默认枪口 `attachment/muzzle/muzzle_5mm_naked.unit`（`muzzle` 节点 + 网格），**被 12 个实体用作默认枪口**（含 AR-23P、Stalwart、多种无人机武器）。
- 弹匣 `attachment/magazine/assaultrifle_standard_magazine.unit`（`bullet_01..04` 节点 + `g_mag`）；它被哪些武器共用未统计 [I：名称通用，多半共用]。
- 附件材质槽 `m_attachments` / `m_magazine`，同样带涂装变体。

### 1.6 对照成功标准

| 标准 | 结果 |
|---|---|
| 列出全部骨骼与父子关系 | ✅ |
| 指出弹匣、枪栓、扳机骨且在动画中有运动 | ✅ 弹匣 `attach_mag`、枪栓 `bolt`/`bolt_carrier`；`trigger` 骨存在但**不参与蒙皮也不动** |
| 指出第一人称用哪个 Unit | ⚠️ 文件层面只有一个；渲染层面待游戏内确认 |

---

## 2. V1：枪口粒子的引用位置与共享度

### 2.1 引用在哪里 [F，三种方法互证]

AR-23 的枪口火焰 = `ProjectileWeaponComponent.muzzle_flash` = 粒子 `0xd033b685f434bf37`（名称未知）。

1. filediver 的 entity-component-settings 导出直接给出字段 `muzzle_flash = 0xd033b685f434bf37`。
2. 按游戏类型库给出的**真实记录长度 616 字节**切分 `ProjectileWeaponComponent` 记录，再拿全部 3062 个粒子资源哈希去匹配：AR 记录里恰好 3 个粒子引用 —— 偏移 160（抛壳效果）、176（`casings/rifle_5mm`）、**224（枪口火焰）**；224 = `casing_eject`(152, 72 字节) 之后的下一个字段，与字段顺序吻合。
3. 枪口附件的实体增量（entity deltas）恰好改写偏移 224：例如 `muzzle_5mm_flashhider → 0xe97a19938695f2c6`、`muzzle_5mm_compensator → 0xdfecce6551991481`。

AR 的 6 条「动画事件 → 效果」触发器**全是音效**，没有粒子 [F] —— 枪口火焰不走那张表。

### 2.2 共享度 [F]

- 基础记录：`0xd033…` 被 **13 个实体**引用（AR-23、AR-23 Legacy、Karbin、Rico、Large Calibre、Stalwart、弹药架炮塔及 6 个未解析名称的实体）。
- 附件增量：`muzzle_5mm_naked` 与 `muzzle_8mm_naked` 都写成 `0xd033…`。即**枪口火焰按枪口附件类型共享**：凡装裸枪口的 5mm/8mm 武器都用它；补偿器、消焰器、制退器各自一个粒子，跨口径共用。
- 该粒子文件本身在 15 个 archive 里各有一份。

### 2.3 数据存放位置与版本 [F]

- 基础值在 `generated_entities.dl_bin`，附件覆盖在 `generated_entity_deltas.dl_bin`。
- filediver 读的是**它仓库里内嵌的快照**，不是游戏文件。用仓库自带的 `Decryptor` 解密游戏的 4 个文件后比对：`dl_library.dl_typelib`、`generated_entities.dl_bin`、`generated_entity_deltas.dl_bin`、`generated_animation_event_trigger_settings.dl_bin` **与快照逐字节一致** → 以上结论对应当前游戏版本。

### 2.4 分支结论（按验证计划 §V1 表）

**命中第三行：「引用只在 `.dl_bin` 里」。**
- 想让「只有目标武器」换枪口火焰，就要改上述 `.dl_bin`。护甲注册表的 `.dl_bin` 自 2026-11-11 起写入即闪退 [F]；这两个文件是否同样如此未测 [I：同一机制，大概率相同]，且它们是全局共享数据。
- 退而直接覆盖粒子资源本身 → 至少波及 13 个实体及所有裸枪口 5/8mm 武器。
- **按用户决定（默认禁止污染）：粒子路线的纯资源部分关闭，粒子默认保持 HD2 原生。** 是否通过 patch 包覆盖 `.dl_bin`（而非改写磁盘文件）这条路，本报告**不建议**探索：它碰的是全局玩法相邻数据，风险与收益不对称。

### 2.5 本实验的失败与绕行

- filediver 的 `projectile-weapon-json-dumper` 输出是错位的垃圾数据（如重机枪射速 4.7e17）：它的 Go 结构体 388 字节，而当前类型库里该组件 616 字节。**没有采信**这份输出，改用类型库步长 + 哈希匹配（方法 2）。
- 类型库的字段名在 filediver 内嵌版本里被剥掉了（显示为 `b0507` 之类），字段语义靠方法 1、3 交叉确认。

---

## 3. V2：HD2 格式往返测试

| 格式 | 样本 | 工具 | 结果 |
|---|---|---|---|
| `.animation` | AR 的 11 段 | HD2SDK-CE `stingray/animation.py`（无许可证，仅本地测试用） | **11/11 字节一致**（从空缓冲区重新生成，不是拷贝原文件） [F] |
| `.animation` 编辑 | `fire` 第 6 条位置键 x +0.01 | 同上 | 重编码后大小不变（1068 B），**只有 4 字节不同**，其余 42 条键重新解析完全一致；新值 0.00977（uint16 量化精度） [F] |
| `.state_machine` | AR | HD2SDK-CE | **1/1 字节一致** [F] |
| `.particles` | 枪口火焰 `0xd033…`、抛壳效果 `0x598d…` | HD2SDK-CE `particle.py` | **2/2 解析失败**（读越界） [F] |
| `.particles` | 同上 | hd2-particle-modder（MIT）数据类 | 2/2 解析成功（11 个 / 3 个粒子系统），原位写回**字节一致**；改写到全零缓冲区时还原 95% / 91% 的非零字节，但其中相当部分是原样携带的**不透明块**（`emitter_chunk`、`component_chunk`、`visualizer.data`） [F] |
| Unit | AR 机匣 | 仓库 `HD2ModAdaptation` 的 `UnitMeshReader`/`UnitMeshWriter` | 读取成功（9 mesh、4 材质、43 变换）；写回后 **GPU 字节一致**，TOC 多 8 字节：`EndingOffset` 11872 → 11888，其余模型字段全部相同 → **语义等价、非字节一致** [F] |

补充：
- 枪口火焰粒子结构：11 个系统，其中 4 个 billboard（材质 `0x76faeb209c201078`、`0x9392bee6b96d3616`×2、`0x8cff9d11bdf20744`）、1 个灯光、6 个无可视化器 [F]。可编辑的是寿命、最大粒子数、颜色/不透明度曲线、可视化器材质 ID 等；**新增/删除系统或发射器**没有证据可行。
- 粒子测试里 scipy 的旋转对象用「原样保存矩阵」的替身代替；真实 scipy 会把矩阵重新正交化，可能引入微小字节差 —— 未测 [I]。
- 以上全是**离线**结论；游戏是否接受改过的动画 / Unit 写出器的布局差异，都是 [E]。

---

## 4. V5：Source 动画解码对照

### 4.1 样本 [F]

GMod 自带 `models/weapons/c_smg1.mdl`（来自 `garrysmod_dir.vpk`，用仓库 `HD2SourceModel.VpkArchive` 提取）：**v48**、41 骨（含 ValveBiped 手臂）、18 段动画描述、14 个 sequence。
本机没有创意工坊内容，所以计划里的「v49 样本」**没有测**。

### 4.2 方法

1. SourceIO（MIT，提交 `a50cea6`，2026-11-14）的库层（不依赖 Blender）解码全部动画，导出每帧局部位置/旋转。
2. **Crowbar 对照未执行**：第三方 Crowbar 命令行版（无许可证、2022 年后无更新、无发布摘要）在运行时被自动权限审查拦截，我没有绕过。
3. 改为**自写独立解码器**：只依据 Source SDK `studio.h` 的结构与压缩定义（`mstudioanimdesc_t`、`mstudioanim_t`、RLE `mstudioanimvalue_t`、Quaternion48/64、Vector48、RadianEuler→四元数），不引用 SourceIO 代码；两者逐骨逐帧比较。

### 4.3 结果 [F]

- **16/18 段完全一致**：位置误差 ≤ 2e-6（英寸）、旋转误差 0.0000°；含 `fire01–04`、`reload`、`draw`、`idle` 等全部武器动作。
- **2/18 段不一致**：`d.a_adjustment_cstrike_neutral`、`d.a_adjustment_dod_neutral`（delta、121 帧、每 30 帧分段）。**SourceIO 在第 120 帧输出全零四元数 (0,0,0,0)**（不是合法旋转）；独立解码器给出单位四元数（delta 动画「无变化」的正确值）。判断：SourceIO 在「最后一帧恰好开启新分段」时的边界缺陷 [F 现象；I 成因]。
- 序列事件：`fire01–04` 在 cycle 0 带 `AE_MUZZLEFLASH`（options `SMG1 MUZZLE`）与 `6001`（抛壳）；挂点 `muzzle`、`1` 都在 `ValveBiped.base` 上 → 枪口位置与开火时刻能从 MDL 离线取到；**用哪个 PCF 粒子不在 MDL 里**，仍需扫 SWEP Lua。
- SourceIO 加载时打印 4 次 `No animation in embedded anim block`，但 18 段都解出且与独立解码一致 → 看起来是无害日志 [I]。

### 4.4 局限

- 两个解码器都可能共同误读规范；独立实现降低了这个风险，但**不等于**与引擎渲染结果比对过。Crowbar（或 GMod 实机）对照仍是 [E]。
- IK 规则未应用（两边都没做）；对武器 viewmodel 影响小 [I]。

---

## 5. 对路线的影响与下一步建议

### 5.1 模型（路线 M）

- **成立**：GMod 武器部件按功能刚性绑到 `boss` / `bolt` / `bolt_carrier` / `ejection_cover` / `fire_selector` / `attach_mag`，即可借用 HD2 原生动画（开火枪栓后坐、换弹抽弹匣）。不需要人形重定向那一套。
- **新问题 1：附件重叠。** 原生枪口、弹匣（以及可选瞄具/下挂）是独立 Unit，挂在 `attach_*` 上，换机匣模型后它们仍会出现，和 GMod 模型自带的枪口/弹匣叠在一起。可选做法：
  - (a) 只换机匣，保留原生附件（视觉上「GMod 枪身 + HD2 枪口弹匣」）—— 纯资源，零风险；
  - (b) 运行时用白名单 `Unit.set_unit_visibility` 隐藏挂在本枪上的附件 Unit —— 属 Lua 增强档，需 V3 一并验证 [E]；
  - (c) 替换附件 Unit 本身 —— 共享（裸 5mm 枪口被 12 个实体使用），按「默认禁止污染」规则不选。
- **新问题 2：机匣 Unit 被 3 个实体共享**，换模型会一起变。需确认这 3 个是否都是 AR-23 系列。

### 5.2 粒子（路线 P）

- 纯资源路线关闭（§2.4）。粒子在 L0/L1 档保持 HD2 原生。
- 剩余唯一增强途径：Lua `World.create_particles` **叠加**一个打包进 patch 的新粒子（以克隆的 HD2 粒子为模板改颜色/材质）。去不掉原生火焰；能否生成 patch 里新哈希的粒子、开火时机能否拿到，都依赖 V3 [E]。
- 建议：把粒子从「核心目标」降为「可选 Lua 增强」，兼容报告里明确写「枪口火焰保持原生」。

### 5.3 动画（路线 A）

- A1（借用原生动画）已被 V0 证实可行。
- **新机会**：AR 的动画文件只被 AR 的状态机引用 [F：导出全部 448 个状态机，435 个成功，其中只有 `assault_rifle.state_machine` 引用 `assault_rifle/animation/*`；13 个导出失败的是敌人、喷火器与舰桥人员]（机匣 Unit 只被 3 个实体共享），格式也能无损读写。所以「把 GMod 的开火/换弹动作重采样后**写进 AR 现有骨的动画**」是一条**纯资源**的路，不需要 Lua —— 但限于现有 5–7 根骨；游戏是否接受改写后的动画是 [E]（游戏内一次实验即可判断）。
- A2（Lua 驱动额外骨）维持原计划，依赖 V3。

### 5.4 建议的下一步（需要你确认）

1. **V3 开火/换弹只读探针**：观测目标已明确（武器 Unit、第 0 层状态、`fire`/`reload` 事件）。需要你决定 Lua 加载方式。
2. **一次游戏内「动画资源改写」小实验**：只把 AR `fire.animation` 里枪栓后坐量放大一倍，打 patch，看游戏是否接受、是否只影响 AR 系列。这比原计划的 V4（粒子）更有价值，因为 V4 的前提已不成立。
3. **V6 模型最小样本**：等你对 §5.1 的附件处理方式（a/b）和机匣共享问题拍板后再做。
4. （可选）若允许运行第三方 Crowbar 命令行版，补做 V5 的 Crowbar 对照；或在有创意工坊 v49 武器时补测 v49。

---

## 6. 复现

- 脚本：`docs/agent/gmod-weapon-lab-20261116/scripts/`（每个文件头注释写明用途与参数）；说明见同目录 `README.md`。
- 证据：同目录 `evidence/`（AR 的骨骼表、状态机 JSON）。
- 实验工作目录在 `%TEMP%\gmod-weapon-lab`（约 1.6 GB：filediver、Go 工具链、各仓库克隆、导出结果）；**仍保留**，等你审核后决定是否删除。注意 `%LOCALAPPDATA%` 下的文件对其他程序可能不可见，所以脚本另存了一份在仓库里。
- 本轮修改：只新增了 `docs/agent/` 下的报告与实验脚本；没有改动任何正式代码。filediver 克隆里加的 `zz_lab_probe.go` 只在临时目录里，副本存为 `scripts/filediver_zz_lab_probe.go`。
