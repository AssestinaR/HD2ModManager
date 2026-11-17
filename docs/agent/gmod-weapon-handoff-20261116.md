# GMod 武器 → HD2：交接（2026-11-16）

> 只放「现在的状态 + 去哪找细节 + 下一步」。细节按需读下面的报告，不要一次全读。

## 1. 文档地图（按需读）

| 文件 | 何时读 |
|---|---|
| `gmod-weapon-offline-validation-20261116.md` | **首先读**：V0/V1/V2/V5 实测结果、证据、局限、对路线的影响 |
| `gmod-weapon-validation-plan-20261116.md` | 实验定义与成功标准（V0–V6）、Lua 能力层四维度 |
| `gmod-weapon-plugin-architecture-20261116.md` | 集成架构（插件 DLL + 进程外宿主 + 复用现有更新器）——实现阶段才需要 |
| `gmod-weapon-to-hd2-research-20261116.md` | 第一轮调研（外部项目、障碍清单、已决定事项 §6）——一般不必再读 |
| `gmod-weapon-lab-20261116/README.md` | 实验脚本清单与依赖 |

## 2. 已确定的事（用户已认可）

- 独立 GPL-3.0 转换器，不做 CLI/GUI；管理器通过「插件 DLL + 独立 Worker 进程 + 现有 Releases 更新器/镜像」集成。
- 资源优先；Lua 只做增强，白名单内、能力检测、逐级降级；不碰伤害与玩法；**默认禁止污染其他武器**。
- 复用代码：先用现有项目验证，再按实际依赖提取。
- 优先级（2026-11-16 用户指示）：**下一步先验证武器模型替换（V6）与 HD2 原生动画写回**；粒子路线暂时保留、不推进。

## 3. 实测结论速记（证据见离线报告）

- **模型**：AR-23 机匣 Unit `0x968211c0033dce64` 刚性蒙皮（每顶点 1 骨），只有 5 根变形骨 `boss/bolt/bolt_carrier/ejection_cover/fire_selector`，弹匣随 `attach_mag` 动。部件→骨刚性映射可行。该 Unit 被 3 个实体共用。
- **附件**：枪口/弹匣/瞄具是独立 Unit（裸 5mm 枪口被 12 个实体共用）→ 换机匣后原生附件会与 GMod 模型重叠。
- **粒子**：枪口火焰引用只在 `generated_entities.dl_bin` / `generated_entity_deltas.dl_bin`（`ProjectileWeaponComponent` 偏移 224），按枪口附件类型共享 → 纯资源无法只改一把枪；**暂停**。
- **动画**：AR 的 11 段动画只被 AR 自己的状态机引用；HD2 `.animation` 读写字节一致、单键编辑只改 4 字节（离线）→ 资源改写动画可能不需要 Lua（游戏是否接受未测）。
- **Source 解码**：SourceIO 与自写独立解码器在 `c_smg1`(v48) 上 16/18 一致；SourceIO 在分段 delta 动画末帧有全零四元数缺陷。v49 未测；Crowbar 对照被权限拦截未做。
- **已知限制**：写过的 `.dl_bin` 进游戏闪退（2026-11-11 起）；HD2SDK-CE 无许可证（只能参考/本地测试）；仓库 `UnitMeshWriter` 对武器 Unit 语义等价但非字节一致（`EndingOffset` +16）；filediver 的 projectile dumper 与当前游戏错位，不可信。

## 4. 待用户决定

1. 原生附件处理：(a) 保留原生附件（纯资源）／(b) Lua `Unit.set_unit_visibility` 隐藏（需 V3）。
2. 机匣 Unit 被 3 个实体共用（疑为 AR-23 各型号），换模型一起变是否可接受。
3. V3（开火/换弹只读 Lua 探针）的加载方式——暂缓，待 V6/动画实验后再定。
4. 是否允许运行第三方 Crowbar 命令行版补做 V5 对照（无许可证、无摘要）。

## 5. 下一步计划（需用户确认后执行，均为游戏内实验，需用户进游戏观察）

| 顺序 | 实验 | 做法 | 成功标准 | 失败退出 |
|---|---|---|---|---|
| 1 | **A-W 动画写回** | 只把 AR `fire.animation` 中枪栓后坐量放大 2 倍（用已验证的读写模型重编码），单独 patch | 游戏加载不崩；AR-23 开火时枪栓后坐明显变大；其他武器不变；删 patch 恢复 | 崩溃或无效 → 动画只走 A1（借原生）/A2（Lua） |
| 2 | **V6 模型替换** | GMod `c_` 步枪：剥手臂 → 部件刚性绑到 AR 的 5 根骨 + `attach_mag` → 以 AR Unit 为模板替换 mesh/材质 → 单独 patch（用现有项目代码，在实验目录，不改正式代码） | 第一/第三人称显示新枪；持握无明显偏移；开火枪栓、换弹弹匣随原生动画动；其他武器不变；删 patch 恢复 | Unit 写出不被接受 → 先解决武器 Unit 写出（`EndingOffset` 差异）；持握偏移无法单一根变换修正 → 记录为手调参数 |

两项都先做离线准备（产出 patch 并离线自检），再请用户进游戏验证。附件重叠在 V6 中按用户对 §4-1 的选择处理（未决前按 (a) 保留原生附件）。

## 6. 当前状态

- **代码**：正式功能代码未改。分支 `master`，未提交。
- **未跟踪文件（全部是本任务产物，未提交）**：
  `docs/agent/gmod-weapon-*.md`（5 份）、`docs/agent/gmod-weapon-lab-20261116/`（脚本 + 证据）、本交接、`gmod-weapon-handoff-prompt.md`。`.serena/` 是会话前就有的，不属于本任务。
- **临时目录（保留，未清理）**：`%TEMP%\gmod-weapon-lab`（约 1.6 GB）
  - `tools/`：filediver v0.7.67 CLI、filediver 源码克隆（含实验加的 `datalibrary/zz_lab_probe.go`、`cmd/lab-probe/`）及构建出的 dumper、Go 1.27.4、HD2SDK-CE、hd2-particle-modder、SourceIO 克隆、Crowbar 命令行版（已下载、**未运行**）。
  - `out/`：全量文件清单 `list-all.txt`、AR 导出 `v0-ar/`、原始文件 `v2-raw/`、各 dumper JSON、解密的 `.dl_bin`（`game-plain/`）、V5 解码 JSON。
  - `samples/`：GMod `c_smg1`、`c_pistol`。
  - 注意：`%LOCALAPPDATA%` 下文件对其他程序可能不可见；脚本副本已在仓库 `gmod-weapon-lab-20261116/`。
- 路径：HD2 游戏 `E:\SteamLibrary\steamapps\common\Helldivers 2`；GMod `D:\SteamLibrary\steamapps\common\GarrysMod`（无创意工坊内容）。
