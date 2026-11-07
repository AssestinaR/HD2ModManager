# 物理 Lua「有时完全没效果」· 情报与工作顺序（2026-11-01）

> 触发：用户在管理器里开「物理 lua 独占模式」后逐个测 VPK mod，**经常出现完全没物理**，
> 且"运行时没有任何日志"。
> 本文只记录**已证实的**事实与证据位置，以及据此排出的工作顺序。实现前先看 §6。

---

## 0. 结论先行

运行时**其实是跑起来的**（`%LOCALAPPDATA%\hd2physbone.log` 4663 行，一次会话里 7 轮 attach 全部成功），
但：

1. **生产运行时没有任何"是否真的在驱动"的观测**（探针里那套心跳/看门狗**一条都没移植**）⇒
   "没有物理"和"有物理"在日志里长得一模一样，这才是"没有任何日志"的真身。
2. **22 / 134 条链永远挂不上**（每套护甲 4–7 条，`lime_un` 是整条裙子 5/25）。
   原因与旧文档写的不一样：不是"锚点是另一根 jiggle 骨"，而是**链根的父骨是非 jiggle 的源骨，
   名字映射不到 HD2 骨名**。
3. **"这个 mod 没有物理"这条路全程静默**：转换期不嵌容器，导入末尾补一步 `physbone embed-mod`，
   而它**永远 exit 0**，Skip 原因只写在 JSON 的 `reason` 里，管理器只认退出码 ⇒ 用户永远看不到原因。

---

## 1. 证据：日志里到底发生了什么

`%LOCALAPPDATA%\hd2physbone.log`（2026-11-01 14:06，一次游戏会话）：

| 事件 | 次数 |
|---|---|
| `运行时 1.0.0 已安装（独占 boot）` | 1 |
| `读到 N 份物理容器` | 1（N=3） |
| `attach 进入` | 42 |
| `已挂载 N 个 unit / M 条链` | **7**（6 / 7 / 7 / 7 / 1 / 7 / 7 个 unit） |
| `N 个会话失效，准备重扫` | **6** |
| `[LIFECYCLE]` / `[失败]` / `[G4]` / `[RESYNC]` / `[护栏]` / `驱动失败` | **0** |
| `[IDENTITY] 抽样骨名 has_node=false` | 42（每会话每轮一条，**已知假信号**） |

⇒ attach 全部成功、`缺节点 0`；但**每轮挂上之后，会话都在下一次身份采样（30 帧）前后被整体丢弃**，
然后重扫重挂。日志里**没有任何一帧的证据**说明这中间到底写了没有、写了多少。

`已挂载 7 个 unit / 224 条链` 这类行里 224 = 7 × 32，说明 7 个 unit 用的是同一份 rig（人台 + 自己）。

---

## 2. F1 · 生产运行时没有观测（最高优先）

`HD2PhysBoneCompat/src/physbone_runtime.lua` 全文 418 行，日志点只有：
`已安装` / `读到 N 份容器` / `已挂载` / `N 个会话失效` / `attach 失败时的一句 driveError`。

`update` 里算出来的东西**全部存进 session 就再没输出**：
`written`、`skippedSame`、`engineCalls`、`tipApplied`、`firstFailReason`（后者只在第一次失败时打一行）。

对比开发探针 `tmp/lua-boot/probe.lua`（1996 行），它**早就有**下面这些，生产版一条没带过来：

| 探针里的东西 | 位置 | 生产版 |
|---|---|---|
| 阶段心跳（每 60 帧一行：phase / 会话有无 / applied） | `probe.lua:796` | ❌ |
| `[drive] D applied=… written=… skipped=… static=… zero=…` | `probe.lua:891` | ❌ |
| `[jigphys] live … 峰值=…° 各节=[…]` | `probe.lua:921` | ❌ |
| **`handled = written + skippedSame`；连续 45 帧 == 0 ⇒ 判"驱动失效"重挂** | `probe.lua:996-1022` | ❌ |
| 护栏 `item.disabled` 触发 ⇒ 重挂 | `probe.lua:1005` | ❌ |
| 退避按"存活帧数"决定（活过 5 s 就立刻重挂，抖动才越等越久） | `probe.lua:835-844` | ⚠️ 无条件 `reArm+1`，只在挂上时清零 |
| 会话失效时把 `dead` / `rebuilt` 原因写进日志 | `probe.lua:817` | ❌（`pruneSessions` 只数个数） |

**这条不修，后面所有排查都是猜。** 而且 `handled` 看门狗本身就是"没物理"的自动发现器。

---

## 3. F2 · attach 的 6 条静默失败路径

`physbone_runtime.lua` 的 `tryAttach` 有多处 `return false`，**一条日志都没有**：

| 位置 | 条件 | 用户看到 |
|---|---|---|
| `discover` 返回 nil | 世界没就绪 / 找不到 avatar / unit 总数还在变 | 什么都没有 |
| `nodeCount < 10` / 取不到世界位置 | 候选件不是真护甲 | 什么都没有 |
| 两次采样未稳定 | 正常等待 | 什么都没有（合理） |
| **`#plan == 0`** | **没有任何一份容器能"全命中"这个 unit** | **什么都没有 ← 最常见** |
| `#sessions == 0` | 容器命中但一条链都建不出来 | 什么都没有 |

⇒ 一个"没有物理参数"的 mod，日志里就只有 2 行（已安装 + 读到 N 份容器），
看起来和"根本没加载"一样。

---

## 4. F3 · 22/134 条链永远挂不上（`缺锚点`）

日志证据：`attach：可用链 32 条（缺节点 0、缺锚点 7）`（mourningveil）、`33 条（缺锚点 6）`（lunalice）。

离线复算（`tmp/probe_physrig.py` + `tmp/report_rig.py`，读部署后的 4 份容器）：

| 护甲 | rig 骨 | 链 | 死链 | 死链骨 | 活链 |
|---|---|---|---|---|---|
| lunalice | 57 | 39 | **6** | 12 | 33 |
| mourningveil | 56 | 39 | **7** | 8 | 32 |
| serenade | 55 | 31 | **4** | 10 | 27 |
| lime_un | 55 | 25 | **5**（整条裙子 `UN_Skirt_L/R`） | 5 | 20 |
| 合计 | 223 | 134 | **22** | 35 | 112 |

### 4.1 真正的原因（更正旧文档 §9.5）

旧说法是"嵌套锚点链（锚点是另一根 jiggle 骨）部分跳过"。**不成立**——
锚点是另一根 jiggle 骨时，`build_chains` 根本不会把那根骨当链根（父在 rig 里），日志里
`缺锚点` 全是另一种情况：

```
rig 里 8 根"非 jiggle 的父骨"（idx 80/81/90/94/100/102/105/107）不在 rig 中，
但它们的子骨（91/95/101/103/106/108/109…）在 rig 里
⇒ 子骨成为链根，parentName = source_extra_90
⇒ M.anchor_name() 查不到映射（表里只有 ValveBiped 名字）
⇒ 回退成"把 source_extra_90 当 HD2 骨名查" ⇒ 不存在 ⇒ 缺锚点
```

`lime_un` 同理，只是父骨有真名：`UN_Skirt_R_02`（源骨，非 jiggle，不是 HD2 骨名）。

### 4.2 为什么转换期能修

`Retargeter.PlanJiggleBones` 在插骨时**已经算出了答案**：
`resolved[父骨下标]` = 这根非 jiggle 父骨回退到的 HD2 原生骨下标
（`Retargeter.JiggleBones.cs:106`，`parentIndex = nativeParent`）。
`RetargetExtraBone.AnchorIndex` 也已经是"这根骨在烘焙时会落到的 HD2 骨"。

⇒ **只要把 `_rig.Bones[parentIndex].Name` 写进 rig 的 `parentName`，运行时立刻就能解析。**
运行时不用改：`M.anchor_name("head")` 查不到映射时会回退成把 `parentName` 当 HD2 骨名查，
这条回退路径本来就在（`physbone_compat.lua:1382-1387`）。旧 rig 也不受影响。

难点只在**管线位置**：rig 是在 `PhysBoneContainerEmbedder` 里从 `SourceModelInspector`（纯源模型）序列化的，
那里没有 retarget 上下文。可选做法见 §6-P1。

---

## 5. F4 / F5 · 另外三条

### F4 "没有物理"这件事全程静默

- `vpk2patch` 里的容器嵌入被 `HD2_PHYSBONE_CONTAINER=1` 门控，**默认关**
  （`CliApplication.cs:2402-2417`，理由：转换期往 `.gpu_resources` 追字节会让下游记账错位 → 材质品红）。
- 管理器在导入末尾补跑 `physbone embed-mod`（`VpkImportService.cs:297-316`），
  但 `embed-mod` **永远 `return 0`**（`CliApplication.cs:2100`），Skip 原因只在 JSON `reason` 里；
  管理器只在 `code != 0` 时记 note ⇒ **"来源模型没有 jigglebone" / "没有找到含 Unit 的 patch" /
  "文件被占用" 用户一个字都看不到**。
- `embed-mod` 只取 mod 目录里**第一个** `*.vpk`（`VpkImportService.cs:299`）；
  多 VPK 包里选错模型 ⇒ 没物理且无提示。

### F5 日志本身不适合排查

| 问题 | 位置 |
|---|---|
| `io.open(path, "w")` —— **每次启动覆盖**，上一次的证据直接消失 | `physbone_runtime.lua:55` |
| 无时间戳、无帧号 ⇒ 7 轮 attach→失效的**间隔完全不可知**（换场景 vs 抖动无法区分） | 全局 |
| `[IDENTITY]` 每会话每轮一条（本次 42 条），是**已知假信号** | `physbone_compat.lua:1132` |
| `pruneSessions` 只报"N 个会话失效"，不报 `dead`/`rebuilt` | `physbone_runtime.lua:307` |
| attach 成功路径每条链 2–3 行（4663 行里 ~95%） | `physbone_compat.lua:1342,1501,1542` |

### F6 小 bug

`runtime.dt` 恒为 `1/60`（`physbone_runtime.lua:334`），不随实际帧时变。
`update` 实测频率 70–80 Hz ⇒ 物理比真实时间慢 15–30%。不致命，但要和 F1 一起改
（引擎时间 `Application.time_since_launch` 每帧只更新一次，够用于**帧间** dt）。

### 顺带更正

`docs/physbone-status.md` §9.2「物理独占入口还没接进 boot」**已过时**——日志证明独占 boot 已在跑。

---

## 6. 工作顺序（建议）

### P0 · 先装观测（不做这个，后面全是猜）

1. 移植探针的 `handled` 看门狗（连续 45 帧 `written+skippedSame==0` ⇒ 记一行原因并重挂）。
2. 移植阶段心跳：每 60 帧一行 `unit=N 链=M applied=K written=W skipped=S static=T calls=C zero=Z`。
3. attach 的 6 条失败路径**各打一句原因**（含"哪份容器命中了几根骨、为什么不算全命中"）。
4. `pruneSessions` 报 `dead` / `rebuilt` 分开计数。
5. 日志改**追加 + 带时间戳 + 帧号**（按启动分节，不覆盖旧会话）。
6. `[IDENTITY]` 降噪：只在"节点数也变了"时打，或每进程最多 1 条。
7. attach 逐链明细收进 `options.debug`（默认关）。
8. `runtime.dt` 用引擎时间（F6）。

**验收**：用户重跑一轮，日志能一句话回答"是没挂上 / 挂上了没写 / 写了被静止门限跳过 / 在别人的衣服上写"。

### P1 · 修 22 条死链（F3）

rig 需要带上"已解析的 HD2 锚点名"。两个方案，倾向 A：

- **A（不动协议、不动运行时）**：转换期把 `源骨 DisplayName → HD2 锚点名` 写成 sidecar
  （`vpk2patch/physbone-anchors.json`），`physbone embed-mod` 读同目录 sidecar，
  序列化 rig 时把 `parentName` 换成 HD2 名。旧 rig / 旧运行时都能读（`parentName` 本来就是"用来查锚点的名字"）。
- **B**：rig v3 加一个 `anchorName` 字段。干净但要同时改运行时版本判定，
  旧运行时会**整份 rig 拒收**（`parse_rig` 硬性 `version==2`）——失败模式太差。

**验收**：`tmp/report_rig.py` 复算死链 22 → 0；游戏里 `lime_un` 裙子会动。
离线单测：`rig-select-test.lua` 加一例"父骨非 jiggle"。

### P2 · 让"没有物理"可见（F4）

- 管理器把 `embed-mod` 的 `reason` 变成导入报告 note（不再只看退出码）。
- Mod 卡片/详情页标一个「无物理参数」状态（数据源：patch 尾部有没有 `HD2PBX01`）。
- `embed-mod` 支持多 VPK：按"含 Unit 的那个 VPK"选，选不中要明说。

### P3 · 收尾

- 更正 `docs/physbone-status.md` §9.2 / §9.5。
- 若 P0 的看门狗证明"会话失效"是**误判**（`Unit.alive` 抖动），再单独开一条修 `session_health`。

---

## 7. 复现/取证工具（本轮新增）

| 文件 | 用途 |
|---|---|
| `tmp/probe_physrig.py` | 解析部署后的 patch 尾部容器 → rig 骨表 → 复算 `build_chains` + `anchor_name` |
| `tmp/report_rig.py` | 输出 4 份 rig 的死链清单到 `tmp/physrig-report.txt` |
| `%LOCALAPPDATA%\hd2physbone.log` | 生产运行时日志（**注意：每次启动覆盖**） |
