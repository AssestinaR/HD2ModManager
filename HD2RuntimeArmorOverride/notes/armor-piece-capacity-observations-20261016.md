# 护甲 Piece 容量：**现状观察**（2026-10-16）

> ⚠️ **这不是结论文档。** 本文只记录"在实机里观察到什么"以及"用什么方法观察到的"。
> 下面每一条都是**经验边界**，不是引擎上限；任何一条要变成结论，都必须在真实游戏代码里
> 找到对应的定长结构或分支（见文末"下一步"）。当前代码级证据只有一条：`game.dll+0x81f470`
> 会读 Piece 的 7 个辅助字段，并在它们全为零时跳过辅助逻辑；bank 选择在 `0x8201f5`。

## 1. 背景：为什么会撞上限

运行时把自定义素体的 Piece 组合 clone 进目标护甲的 Kit，替换注册根指针。
clone 的 Piece 记录里 `+8 = slot`、`+12 = piece_type`、`+16 = weight`。

原生消费者（一个被捕获的 `game.dll` 路径）把 Piece 投影到
**`(body 变体, slot, A/B/C 句柄 bank)`**，其中

```
bank = 0 if piece_type == 0 else 1 if piece_type == 1 else 2
```

（逐字取自 `scripts/analyze_registry_slots.py:59-61` 与 `scripts/review_armory_report.py:37-41`。
该脚本自己声明：这个模型只描述**一个**捕获到的消费者，不覆盖每条预览/清理路径，
"aliasing is a testable lead, not a crash diagnosis"。）

推论：`piece_type >= 2` 的 Piece 全部挤进 bank C，同一个 bank 只有**最后写入**的句柄能活。

## 2. 游戏本体数据的实测上限

`scripts/Inspect-CompositionLimits.lua` 跑 `data/game/generated_customization_armor_sets`
（明文那份，`9b 01 00 00` = 411 个 Kit）：

```
kit_count=411            piece_count=3455
max_pieces_per_kit=29
max_pieces_per_body=11
max_pieces_per_body_slot=3        <- 每槽最多 3 个
duplicate_body_slot_piece_type_keys=0
observed_piece_types=0,1,2        <- 只有三个
observed_slots=0..9
engine_capacity_proven=false
```

`scripts/Inspect-SlotUsage.lua` 进一步给出每个 slot 在原版里承担什么：

| slot | 使用它的 Kit 数 | body | 用途 |
|---|---|---|---|
| 0 | 158 | 3 | 头盔 |
| 1 | 255 | 3 | 披风 |
| 2 / 3 | 135 | 0/1 | 胸 / 胯，A/B/C 三个 bank 都在用 |
| 4 / 5 | 135 | **3**（0/1 各只有 3 个） | 腿，原生基本只用 bank A/B |
| 6 / 7 | 135 | 0/1/3 混用 | 臂 |
| 8 / 9 | ~120 | 0/1 | **MaterialLut + PatternLut**（颜色/花纹载体，不是几何槽） |

## 3. 实机观察：12 个配置

方法：`scripts/use_armor_piece_config.py` 直接改写运行时协议（Lua 约 1 秒轮询，
**不需要重启游戏、也不需要开管理器**），每级只改一个变量。每次切换后脚本都会打出
"逐槽清单 + 别名组 + 被压住的 Piece 数"，用来对照"应该看到什么"。

| 配置 | 总 Piece | 生效 | 超容量的槽 | 别名组/被压 | 实机：身体部件 | 实机：衣服 |
|---|---|---|---|---|---|---|
| S4…S8 | 17…21 | 11…**15** | 1（slot 4，深到 9） | 1 / 2…7 | **完好** | 只掉部分配件 |
| C1 / M2 | 25 / 23 | 17 | 0 | 0 / 0 | **完好** | 正常 |
| K1 | 16 | 10 | 1 | 1 / 2 | **完好** | 掉臂环、吊带、右腿过膝袜、腿环 |
| K2 | 19 | 13 | 2 | 2 / 4 | **完好** | +吊带、裙子 |
| K3 | 22 | 16 | 3 | 3 / 6 | **完好** | +腿环 |
| K4 | 25 | 19 | 4 | 4 / 8 | **完好** | 无变化 |
| **X6** | 27 | **21** | 4 | 4 / 8 | **完好**（双臂在） | 掉手套、鞋、发箍、猫耳 |
| **Y** | 28 | **22** | 1（slot 4 深到 9） | 1 / 7 | **两条胳膊消失** | 掉裙子、手套；鞋正常 |
| **M3** | 28 | **22** | 5 | 5 / 10 | **右臂消失** | 掉裙子、手套 |
| M4 | 32 | 26 | 5 | 5 / 14 | 左臂、胯、双腿消失 | |
| M5 | 35 | 29 | 5 | 5 / 17 | 再掉胸 | |
| C3 | 43 | 37 | 5 | 5 / 21 | 全部消失 | |

## 4. 现状：目前**只能**说这几句话

1. **"每槽几个"不是触发点。** S8 把 9 个塞进 slot 4、身体一个部件都没少。
2. **触发点与"生效 Piece 总数"相关，经验边界在 21 / 22 之间。**
   ≤21 的 7 个配置（10/11…15/16/17/19/21）身体全部完好；
   ≥22 的 5 个配置（22/22/26/29/37）都掉身体部件，且**掉的数量随总数单调上升**。
3. **总数决定"掉多少"，分布决定"掉哪个"。** Y 与 M3 的生效数都是 22，但 Y 掉双臂、
   M3 只掉右臂；两者差别在"超容量的槽有几个、每个多深"。
4. **别名（bank C 被压）只影响"哪件配件显示"，不影响身体部件。**
   K1…K4 身体全好、衣服逐级变化，正是 bank 里"最后写入者"随配置变化。
5. **`piece_type >= 3` 本身不损坏内存。** C2 里有一个 type=3 的 Piece，身体正常。

## 5. 明确**尚未**解释的部分

- **为什么边界是 21/22。** 21 恰好等于 `7 个槽 × 3 个 bank`，但 S8 违反"每槽 3 个"
  却没事，所以它不是"每槽容量"的简单求和。**这条必须去代码里找定长结构才能定论。**
- **为什么 Y 掉双臂、M3 只掉右臂。** 分布如何映射到"掉哪个槽"的规则未知。
- **掉的是"没渲染"还是"被换成原版"。** 观察只能看到"看不见了"，不能区分。

## 6. 下一步：从代码里取权威链路

> **先决条件（已确认）**：**盘上的 `game.dll` 不能直接反汇编。**
> `SizeOfImage = 74727424`（0x4744000），与 `runtime.status` 里的 `game_image_size`
> 完全一致；而盘上文件只有 15522408 B（0xECDA68）—— 小 4.8 倍，说明是压缩/加密的，
> RVA 只对**运行中的进程内存**有意义。之前那批 `game.dll-*.asm.txt` 正是用
> `scripts/Capture-ArmoryMemory.ps1` 从进程内存抓的，这条流程要复用。
> `scripts/disasm_armor_consumer.py`（capstone）已经能按 RVA 反汇编并挑出
> 带 +0x18/+0x20/... 偏移、常量 3/10/0x60、以及乘 3/10 的指令，喂它一份**未加密的镜像**
> 即可。

1. **反汇编 `game.dll+0x81f470`**（bank 选择在 `0x8201f5`）。
   已知它会读 Piece 的 `+0x18/+0x20/+0x28/+0x48/+0x30/+0x40/+0x50`，
   并在七个全零时于 `0x81faa0..0x81fad6` 跳过辅助逻辑；它还会"清掉三个 bank 的旧实例句柄，
   再按 slot/type 写新句柄"。要确认的是：**那三个 bank 的容器到底是什么形状**
   （`bank[slot]` 还是 `slot[bank]`、长度是 10 还是别的），以及**是否存在按 `piece_type`
   直接索引的定长数组**——后者才是"超过某个数就踩到别的槽"的唯一解释。
2. **Lua 模组探针**（`HD2RuntimeArmorOverride` 已有同类探针可复用）。
   目标不是再猜，而是**直接读出消费者持有的派生数组**：在 equip 前后快照候选容器，
   看它是否随 clone 的 Piece 数变化，以及第 4 个、第 22 个 Piece 写入时是否有越界写。
   需要的先决条件：定位到容器的基址与长度（第 1 步给出），以及一次普通 equip 事件作为触发点。
3. 只有在上面两步给出结构之后，才把本文第 4 节的"经验边界"升级为结论。

## 7. 复现本文全部数字的命令

```powershell
# 原版数据上限 / 槽位用途 / 某个 Kit 的 Piece 表 / Unit 共用情况
luajit.exe HD2RuntimeArmorOverride/scripts/Inspect-CompositionLimits.lua `
  HD2RuntimeArmorOverride/src/composition_schema.lua "<game data>/game/generated_customization_armor_sets"
luajit.exe HD2RuntimeArmorOverride/scripts/Inspect-SlotUsage.lua   ... 同上
luajit.exe HD2RuntimeArmorOverride/scripts/Inspect-KitPieces.lua   ... 同上 820333399
luajit.exe HD2RuntimeArmorOverride/scripts/Inspect-UnitSharing.lua ... 同上 820333399

# 离线算别名（不需要游戏）
python tmp/rig-lab/_probe_bank_aliases.py <custom-model.json>

# 切配置并热切换（约 1 秒生效）
python HD2RuntimeArmorOverride/scripts/use_armor_piece_config.py --config X6
```
