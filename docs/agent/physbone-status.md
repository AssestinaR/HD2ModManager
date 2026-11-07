# 物理骨骼（jigglebone）适配 · 状态与交接

> 唯一入口文档。**只写"有这么回事"和判据**，细节自己去代码/工具里抓。
> 上一版长文档（`physbone-compat-design-consolidated.md`、`agent-brief-physbone-compat-design.md`、
> `l4d2-to-hd2-physbone-migration.md`）已降级为历史存档，只在对拍/追溯时翻。

最后更新：2026-11-01

---

## 0. 一句话

L4D2 的 `$jigglebone` 参数现在**随 patch 一起交付**，在 HD2 里驱动头发 / 大袖子 / 裙摆，观感已达标（3 个模型实机验证）。
闪退（军械库 / 空投仓）的机制已定位并修好（`Unit.alive` 生命周期闸 + 快速就绪），**待实机确认**。

---

## 1. 现在什么能用（全部实机验证过）

| 能力 | 状态 |
|---|---|
| 源模型 jiggle 骨 → 合成节点插入 Unit | ✅ 3 个模型（Lunalice 57 骨 / Lilium 54 骨 / MourningVeil 56 骨） |
| 顶点权重真实落到新节点 | ✅ 调色板引用新下标，非"重指向整槽" |
| 参数封成 `HD2PBX01` 容器、**跟着 patch 走** | ✅ 换模型不再手动换 rig 文件 |
| 链根锚点上跨到 HD2 骨名 | ✅ 转换期写 `vpk2patch/physbone-rig.bin`，实机 25 条链 0 缺锚点 |
| 运行时按骨名找节点、逐链驱动、写 local pose | ✅ 见日志 `限位来源：…（共 N 根骨）` + 心跳 |
| **三种约束形式（锥 / 偏航 / 俯仰）** | ✅ 实测 1 套资产 21/55 根用分轴约束，过去被当成无限位（§9 第 5 条） |
| **分轴限位与"世界朝向锚定"的正确配对** | ✅ 三个轴同一参考系；纯 yaw/pitch **头发**不做世界锚定（§9 第 6 条） |
| 头发 / 裙摆观感 | ✅ 用户确认主体正常；分轴约束改用"相对自身绑定方向"后翘边已修 |
| dt 跟随引擎时间 | ✅ 同拍重复调用不再重复积分 |
| 热调参数（改文件 + 命令通道 reset） | ✅ 不必重启 |
| 离线回归 | ✅ 12 套 Lua + 874 Core / 224+1 SourceMigration / 182 Adaptation / 68 SourceModel / 195 Cli |

**未覆盖**：只有 LOD0 被驱动；非对称约束按对称处理（§9 第 5 条）；不处理碰撞。

---

## 2. 四层架构（各一句话）

| 层 | 干什么 | 代码 |
|---|---|---|
| 资产层 | 把源 jiggle 参数封进 patch 尾部容器 | `HD2SourceModel/PhysBoneContainer.cs`、`HD2ModAdaptation/.../PhysBoneCarrier.cs` |
| 骨架层 | 转换时插合成节点 + 重写权重 | `HD2SourceMigration/Retarget/Retargeter.JiggleBones.cs`、`VpkConversion/UnitTransformInfoAppender.cs` |
| 运行时层 | 求解器 + attach + 生命周期闸（**纯库**） | `HD2PhysBoneCompat/src/physbone_compat.lua`（无依赖） |
| 生产入口 | 发现目标 + 调度 + 挂/卸钩子（**部署的就是它**） | `HD2PhysBoneCompat/src/physbone_runtime.lua` |
| 观测层 | 探针 + 命令通道 + 面包屑（仅开发侧） | `tmp/lua-boot/probe.lua`、`build_probe.py`、`%LOCALAPPDATA%\hd2pbx_*.txt` |

**生产入口 vs 探针**：两者共用同一个求解器库，区别只在"发现目标 + 调度"这一层。
探针带 cmd 通道、阶段门、诊断打印；`physbone_runtime.lua` 是给玩家跑的干净版本，
同时支持**独占 boot**（被管理器的 boot 文档 require）与**外置模块**（被第三方加载器 require）两种形态。

---

## 3. 硬事实（错了会静默变形/崩溃，别重推）

### 3.1 引擎（实机一致性电池 E0–E7 验过）

- `Matrix4x4.to_elements` = **packed9**（旋转 1..9，平移 10..12），行主读出来是 **Rᵀ**。
- `Quaternion.to_elements` = **xyzw**。
- `axis_angle` 支持任意轴，但**不归一化**（必须传单位轴）。
- `Matrix4x4.rotation(pose)` 不可用。
- **缓存句柄 0.10 s 就发散（diff = inf）⇒ 绝不缓存句柄**。
- ⚠️ **`update` 的实际频率是「每渲染帧一次」，约 60–80 次/秒**，不是 375。
  早期记的 375 是误读（那是引擎内部 tick，不是我们的回调）。实测 `frames=600 elapsed=8.5s` ⇒ 70 Hz。
  这条很关键：任何"把物理降频到 120 Hz"的想法都是**提速**，不是降载。
- 计时一律用引擎墙钟（`Application.time_since_launch`），**不要用 `os.clock()`** ——
  它在 Windows 上量到的是进程时钟，引擎其它线程忙时会虚高（实测同工作量 1.58 ms vs 5.21 ms）。
- 但 `time_since_launch` **每帧只更新一次**，同一帧内首尾读到同一个值 ⇒ **无法用它给块内计时**（差值恒为 0）。
  ⇒ 性能只报**每帧引擎调用次数**（可核对、不被噪声污染），不报毫秒。
- `Unit.num_scene_graph_items` 是**唯一可靠的"场景图被重建"信号**；
  `Unit.has_node` 会对同一根骨假翻（`nodes 171→171` 不变而 `has_node` 从 true 变 false），**不能用它判死**。- HD2 **世界**上轴 = **+Z**。
- `Unit.node(unit, id)` 对**不存在的名字**也会返回一个数字 ⇒ 任何"名字→节点"的判断都必须先 `has_node == true`。
- `Unit.has_node(unit, id)` 会**中途翻成 false**（实测同一骨名 `nodes 171→171` 不变，`has_node` 从 true 变 false）
  ⇒ 只能当"这一次能不能解析"的瞬时查询，**不能**用来判断"Unit 是不是换了"。
- `Unit.alive(unit)` 是唯一可信的判活原语（对已销毁 Unit 返回 false，不抛错）。

### 3.5 骨名会跨模型重名（多 mod 的头号坑）

插入节点的名字来自源骨：有真名就用真名，**没真名就用 `source_extra_<源骨下标>`** ——
纯下标派生，所以**两个不同模型上同一个下标必然同名**。实测两套护甲模型 56 根骨里有 **50 根同名**。

后果：运行时靠"骨名能对上几根"判断哪份物理参数属于这套护甲，重名就选错 ⇒
**用别的模型的参数与 bind 方向驱动 ⇒ 头发高高翘起**（2026-11-01 实机，只在装了多个物理 mod 时出现）。

对策（与材质的 `idNamespace` 同一思路）：
- **转换器**：占位骨名加按来源模型派生的命名空间 → `j<12hex>/source_extra_72`。
  只给**占位名**加前缀；有真名的骨保持原样，否则父骨名查不到源→HD2 锚点映射。
- **运行时**：骨名判定一律"带前缀/不带前缀都试"（`M.bone_name_aliases`），新旧产物互通。
- **旧 patch 仍是裸名**，要拿到命名空间隔离必须**重新导入**该 mod。

### 3.2 Unit 本地坐标 ≠ 世界坐标（踩过）

patch 里 Unit 的网格/骨骼是 **up = −Y、正面 = +Z、左右 = X**。
用 Blender 看图时按这个摆相机，否则会得到倒置/俯视的假象。

### 3.3 单位与语义

- 源单位 = 0.75 inch = **0.01905 m**；`length` 与 `tipMass` **两个比例分开**（长度可能已是米，重力永远 ×0.01905）。
- Valve 尖端弹簧：`a = k·e − c·v`（无质量除法）⇒ `ωn=√k`、`ζ=c/(2√k)`。
- 阻尼用**绝对** tipVel（匀速走动时的持续滞后是故意的）。
- 骨名哈希 = Stingray MurmurHash64A 的**高 32 位**（种子 = len×0xC6A4A7935BD1E995）。
- patch 必须**编号连续**，中间缺号 ⇒ 后面的 patch 被静默忽略。

### 3.4 运行时能读 data 目录（关键）

Lua 用**相对路径**就能打开部署后的 patch（进程工作目录 = 游戏根）：

```lua
io.open("data/9ba626afa44a3aa3.patch_0.gpu_resources", "rb")   -- 成功
```

⇒ 参数注入不需要引擎提供"数据目录"API。

---

## 4. 不变量（每条都对应一次真实事故，有测试守着）

| # | 不变量 | 事故表现 | 守门 |
|---|---|---|---|
| I1 | `LocalTransforms.Position` 是**父空间**平移 | 网格被拉伸 1–2 m | `UnitTransformInfoAppender.AssertLocalAndWorldAgree` |
| I2 | 合成骨位置必须**经过 A-pose 等姿态变换** | 袖子骨离网格 1.31 m，布横着捅出去 | `RealData_JiggleBonesSitInsideTheirVertices`（< 0.5 m） |
| I3 | 插骨**不得改变任何顶点位置** | 头发上出现闪电形折痕 | `JiggleBoneGeometryTests`（0/118014） |
| I4 | **任何重写 patch 的路径都要把尾部容器带走** | 参数静默丢失 → 拿别的模型的骨去驱动 | `PhysBoneCarrier.CarryFromAnyAsync` + `PatchArchiveWriterContainerTests` |
| I5 | 容器**幂等**（先剥再追加） | patch 越导越大 | 同上（`Append(Append(x)) == Append(x)`） |
| I6 | 尾附数据放在 TOC 引用范围**之后** | 不影响任何偏移 | 协议文档 |
| I7 | **失效 Unit 一律不碰**：每帧第一件事是 `Unit.alive`，会话记 `nodeCount` + 抽样骨名 id | 军械库/空投仓闪退（原生 AV，pcall 拦不住） | `HD2PhysBoneCompat/test/lifecycle-test.lua`（8 组，含"死后零引擎调用"） |
| I8 | **探针的 `render` 钩子不许裸 pcall**；jigphys 的就绪闸**不能**依赖 legacy `discover()` 填 `state.armor`；**身份漂移只能观察，不能据此停驱动** | ①物理完全不生效、日志零线索（死锁 + `nodeCount` 未定义）②头发"竖着冻结、每秒在垂直/原样之间切一次"（误判 rebuilt ⇒ attach 循环） | `HD2PhysBoneCompat/test/probe-harness-test.lua`（假引擎跑整链，含"稳态不许重挂"） |
| I9 | **占位骨名必须按来源模型命名空间化**（`j<12hex>/source_extra_N`）；**rig 是每个 unit 自己的属性**，不是全局的 | 多 mod 时头发高高翘起（用了别的模型的参数/绑定方向） | `Read_PlaceholderBoneNamesAreScopedToTheirSourceModel`、`HD2PhysBoneCompat/test/rig-select-test.lua`（T7/T8/T9） |
| I10 | **任何一个会话失效都必须重新扫描重挂**，不能"还剩几个就继续开" | 军械库切几次护甲后物理"丢失"：老甲那 7 个 unit 死了、人台 13 个还活着 ⇒ 永不再扫描 ⇒ 新甲没 attach（日志却一切正常） | `HD2PhysBoneCompat/test/probe-harness-test.lua` |
| I11 | **候选护甲件按"到 avatar 的距离"排序** | 军械库里 20 个护甲 unit，不排序时 `armorList[1]` 可能是人台 ⇒ 就绪采样/选 rig/截断全看错对象 | 同上 |
| I12 | **场景图重建只看 `num_scene_graph_items`**；`has_node` 假翻不可用作判据；**快路径必须校验"写回后平移没变"** | ① 用 `has_node` 判死 ⇒ 每秒 attach 一次 ② 节点下标越界/写坏平移 ⇒ 写野内存 ⇒ 引擎挂起（GPU 0%、CPU 烧、无日志） | `lifecycle-test` T5/T8、`fast-write-test` T5 |

### 4.1 写入器有**两个**（别再只改一个）

| 写入器 | 谁在用 |
|---|---|
| `PatchArchiveWriter` | 补丁原语、Lua 投放、护甲标准化、装饰合并、`PatchSubsetWriter` |
| `CanonicalPatchWriter`（经 `PatchWorkspaceWriter`） | **`vpk rekey`（应用为 RE2310）**、同键重建、头盔合并 |

I4 的规则**只在一处实现**：`PhysBoneCarrier.CarryFromAnyAsync`，三处调用。
`CanonicalPatchWriter` 从会话条目的 payload 区间自动推导来源，所以新路径自动覆盖。

---

## 5. 参数策略（定稿）

| 项 | 取值 |
|---|---|
| `length` / `tipMass` / `k` / `c` / `angleLimit` | **作者原值**（袖子 360° 也原样保留，不夹） |
| 自然频率 ωn | 夹进 **[4, 12]** |
| 阻尼比 ζ | 夹进 **[0.4, 1.2]**（下界取 0.4 而非 0.5，否则头发也会被推） |
| 重力 g | **保持作者值，绝不放大**（放大到 152 会把尖端压在球面底部 = "被夹住的钢片"） |
| 频段内的骨 | **逐位不变**（头发 ω=7.07/ζ=0.495 就没被碰） |

**世界朝向锚定**：源 flags 同时含 `yaw_con(0x04)+pitch_con(0x08)` 的骨（实测 = 左右袖子，`0x3d`），
父级旋转只带动**平移**、不改变朝向（"轴承铰链"）。头发 `0x31` 不含，行为不变。

### 5.1 多 mod 同时安装时的选 rig（`M.select_rig`）

每个 patch 尾部都有自己的容器，运行时会读到好几份 rig。选错 = 用别的模型的骨名/参数去驱动。

**核心原则：rig 是每个 unit 自己的属性，不是全局的。** 多 mod 时世界里同时有玩家 / 队友 / 人台的
好几套护甲，每套只认自己那份容器。所以流程是"**逐 unit 选容器**"，而不是"选一份全局最优"。

单个 unit 的判定（只在 `physbone_compat.lua` 实现一次，可离线单测）：

1. **全命中优先**（该 rig 每根骨名都能在这个 unit 上解析）—— 比"命中数多"可靠；
2. 同为全命中取骨更多的；再打平取 patch 序号小的；
3. **没有全命中就跳过这个 unit**（宁可不驱动，也不写错参数）；`partial=1` 可强制旧行为做 A/B。

配套：
- 载体扫描范围 **patch_0..patch_63**（原来只到 15，多 mod 时高序号的容器等于不存在）；
- 载体路径按"去掉 `data/` 前缀后的相对名"**去重**（同一个文件会被多个前缀打开三次）。

日志会打出 `逐 unit 选容器：N 个 unit 有匹配，M 个跳过；用到 K 份容器：…` +
每个 unit 的 `rig=…(57/57) → 可用链 N 条`，选错时这两行就是线索。

---

## 6. 崩溃（K1–K4：换甲/切场景）

### 6.1 根因（已定论）

军械库和空投仓的换甲页会**另起一套玩家模型**，换甲时旧 Unit 被销毁、新的被创建；
切图更是整世界换掉。我们的会话存的是 `unit` userdata + **节点下标** ——
Unit 一死，这两样都是野指针，再调 `world_position` / `set_local_pose` 就是**原生 AV**。

**判据只能是事前的 `Unit.alive`，不能是事后的 pcall。**
`stingray.Unit.alive(unit) -> boolean` 在 HD2 在线（两条独立证据：本仓库
`HD2RuntimeArmorOverride/src/armor_refresh_world_probe.lua` 实机调过 577 次、
MDL3 的 `engine_counts` 也用它；对已销毁 Unit 返回 `false`，不抛错）。

### 6.2 为什么"每帧查一次"就够严密

Lua 跑在主线程，引擎**不可能**在我们一次 `update` 的中途销毁 Unit
⇒ "帧首活着" ⇒ "这一帧内都活着"。所以闸只需要一道，位置在**所有引擎调用之前**。

### 6.3 已实现（2026-11-01）

| 位置 | 做了什么 |
|---|---|
| `physbone_compat.lua` `M.unit_alive` | 唯一判活入口；没有 `Unit.alive` 的老引擎退回乐观为真 |
| `M.session_health` | `ok` / `dead`（Unit 没了）。**判死只信 `Unit.alive`**。身份漂移（节点数变 / 抽样骨名解析不到）默认只记 `[physbone][IDENTITY]` 日志、**不停驱动**；`strictIdentity=true`（cmd 里 `strict=1`）才据此停手 —— 实测默认开着会每秒 attach 一次 |
| `M.update` / `M.update_tip` | 第一行过闸；失效即返回 0，一个引擎调用都不发 |
| `M.release` | 失效时**不写**基准姿态（写回野指针 = 崩），只清弹簧数值 |
| `M.attach` | 进门验活；每条链开始前再验一次（保险：万一某个引擎调用内部把它弄失效） |
| 探针 | 驱动前 `prune_sessions`；全丢则回 `wait` 并按 1s→10s 退避重挂；`census` / `find_avatar` / `armor_candidates` / attach 候选表逐个先验活 |

### 6.4 实机验证结果（2026-11-01 第二轮）

| # | 现象 | 状态 |
|---|---|---|
| K1 | 打开军械库卡住/崩溃 | ✅ 不再崩（10 次真实换场景全部正常，0 次误判） |
| K2 | 进入空投仓闪退 | ✅ 同源，已消失 |
| K3 | 切换地图 | ✅ 同源，已消失 |
| K4 | "没找到对应头盔" | 同源（节点解析失败→0 链→不驱动），未单独复现 |
| K5 | 纯材质 VPK `Parameter 'info'` | ✅ 已修（null 进 null 出） |

那一轮的量化结果：`written=315` **2110 次全满写、零失败**；`Lua错误 0`；
10 次 attach 的存活帧数 15151/3820/2986/2872/1824/954/490/393/155 —— 全部是真实换场景，不是抖动。

**工具**：`%LOCALAPPDATA%\hd2pbx_boot.txt`（面包屑）+ `tmp/lua-check/dump-digest.py`、`dump-stack.py`（minidump 解析）。
实机看这两行就能判断闸有没有工作：`[physbone][LIFECYCLE] 会话失效（dead）…`、
`[jigphys][LIFECYCLE] 丢弃 N 个已销毁 Unit …`。

---

## 6b. 就绪速度（进舰船 / 进地图后多快能挂上）旧的等待路径：`每 300 帧轮询 + 连续两次采样一致`，80 fps 下 ≈ 3.7 s + 3.7 s，
再加 12 s 启动闸 ⇒ 首次挂上常在 8–15 s。

现在（`fast_discover`）：

| 手段 | 效果 |
|---|---|
| 轮询节拍 `READY_POLL_FRAMES = 20`（≈0.25 s） | 发现延迟从秒级降到帧级 |
| 场景稳定性只花**一次** `World.units`（比 unit 总数，不逐个碰 unit） | 判"还在加载"几乎免费 |
| **一趟**扫完 avatar + 护甲件（原来 `find_avatar` 扫一遍、`armor_candidates` 再扫一遍） | 引擎调用减半 |
| `pick_world_fast`：记住主世界下标，命中只发一次 `World.units` | 省掉遍历所有世界 |
| 就绪采样间隔 `READY_SETTLE_FRAMES = 10` + 位移阈值 2 cm | 从 600 帧降到 ~30 帧 |
| 启动稳定期只对**首次**生效（6 s），之后换图/换甲只看场景稳定 | 二次进入不再空等 |
| 挂上成功即清退避计数 | 下一次失效立刻重挂 |

安全性没有让步：所有扫描都过 `Unit.alive`，所有驱动都过 `session_health`。


---

## 6c. 性能（2026-11-01 优化）
热路径的成本几乎全在**引擎调用**上，不在 Lua 数学。所以优化的目标只有一个：**少调引擎**。

| 手段 | 省了什么 | 量级 |
|---|---|---|
| **脏标记**：和上次写进去的比，几乎没变就跳过 | 静止时每帧几百次 `set_local_pose` → 0 | 900 帧全写 10800 次 → **144 次** |
| **锚点读数按节点缓存**（每帧每 unit） | 一条护甲的 15 条头发链都挂 `head`，原来每条链读 2 次 | 30 次 → 2 次/帧 |
| **输入自检替代回读校验**（`rotation_sane`） | 每骨 `is_valid` + `to_elements` 两次调用 | −630 次/帧 |
| **`pcall(fn, args)` 代替 `pcall(function() … end)`** | 每骨每帧新建一个闭包 | −315 闭包/帧 |
| **求解器复用缓冲**（`dirs` / `rotBuf` / `accQ`） | 每节每帧新建 2 个短命表 | 每秒数万个表 → 0 |
| **同款护甲 attach 去重**（`M.attach_cache_key`） | 多人穿同一套时，节点解析（231 骨 ≈ 460 次调用）只做一次 | 第 2 个玩家起 **−83%** |
| **运动门限**（`session.motionEpsilon`，默认 1 mm） | 原来只有锚点**逐位相同**才跳过；人台/待机的亚毫米抖动抓不到 | 抖动不累积的链 ⇒ 整条链不再求解 |
| **锚点缓存常驻 + 帧序号** | 不再每帧 `pairs` 清空 + 重建 table | 每秒上万张短命表 → 0 |
| **`step_tip` 去掉 acos/sqrt** | 限位判断改比 `dot < cosLimit`；速度只存平方 | 每骨每子步省 2 次 C 库调用 |
| **局部化 + 数字 for + 去 `math.abs`** | 热点引用提到循环外、`ipairs`→数字 for、两次比较代替 abs | 纯 Lua 开销（量级小但免费） |
| **快路径**（`M.probe_fast_write`）：Lua 里算四元数，引擎只做 `from_elements` + `set_local_rotation` | 每骨 5 次引擎调用 → **2 次** | 自检通过才启用，否则静默退回矩阵路径 |
| `session_health` 帧戳去重 | 同一帧被问两次 `Unit.alive` | −1 次/会话/帧 |

**快路径为什么必须自检**：`Quaternion.from_elements` 存在，但**参数顺序从未验证**（E0 只确认了它存在）。
顺序错不会崩，只会得到错误旋转 —— 最难查的一类问题。所以 attach 时用已验证的 `axis_angle`
造参考四元数、按 `to_elements` 的顺序用 `from_elements` 造一个、比两者；一致才启用。
`fastwrite=0` 可强制回退做 A/B（见 `HD2PhysBoneCompat/test/fast-write-test.lua`）。

**保险**：脏标记假设"我们写的姿态引擎会保留"。实测成立，但每 `refreshEvery`（默认 240 帧 ≈ 2 s）
仍无条件全量重写一次；锚点跳变、`release` 也会清掉脏标记。

**性能口径**：只报 `callsPerFrame`（每帧引擎调用次数），不报毫秒 —— 原因见 §3.1 的计时两条。
换算：`callsPerFrame × 2~4 µs` 就是我们的帧内成本量级。

⚠️ **不要用"这一帧写了几个骨"判断驱动是否失效** —— 静止时它天然是 0。
判据应当是 `written + skippedSame`（见探针里的 `handled`）。

---

## 6d. 设置页集成（「内置 lua」卡片）

物理和映射是**同一套三态语义**的两路 Lua 功能，共用设置页一张卡片和一个总开关。

### 6d.1 总开关「启用管理器内置 lua 类功能」

这是所有内置 Lua 功能的**总闸**。关掉 ⇒ 管理器**永不占用 boot 入口**，于是「独占」在物理上无法实现。

**选项本身就是能力边界**——不可选的档直接从下拉里去掉，而不是让用户选完再发现无效：

| 总开关 | 映射下拉 | 物理下拉 |
|---|---|---|
| **开** | 独占入口 / 外置兼容 / 真映射 | 独占入口 / 外置兼容 / 关闭 |
| **关** | 外置兼容 / 真映射 | **只有 外置兼容**（单选） |

持久化值也会被收敛，避免存着一个用不了的档：
- 映射「独占」→ 外置（保留"用运行时"这层意图，不跳到语义完全不同的真映射）
- 物理任意值 → 外置（那一档是唯一合法的；"不想用物理"的方式是**不把包加进 mod 库**）

### 6d.2 两路 Lua 的落地形态

| 模式 | 映射 | 物理 |
|---|---|---|
| 独占入口 | 合并进管理器的 boot 文档 | 合并进**同一个** boot |
| 外置兼容 | 独立 addon 补丁 | 独立单文件包 → `%LOCALAPPDATA%\HD2ModManager\LuaPackages\physbone\` |
| 真映射 / 关闭 | 不投放 Lua | 不投放 Lua |

外置包**内联 compat**（`PhysBoneLuaSource.ComposeStandalonePackage`）：玩家只拖一个 `hd2physbone.lua`
进 mod 库就完事，不依赖第三方加载器的模块解析规则。代价是两个功能各带一份 compat 副本，可接受。

选「外置兼容」并应用时会**自动打开那个文件夹**——否则用户选了外置却找不到东西可装。

### 6d.3 外置包两种形态（2026-11-01 核对三家加载器）

一手资料（`boot-mod-loader` 的 README、Chef Shared Loader 的补丁、MDL3 的 Addon 包）核对结论：

| 加载器 | 加载方式 | 我们的适配 |
|---|---|---|
| **Boot Mod Loader** | 替换 `boot`，自己用 `io` 读 `data\9ba626afa44a3aa3.patch_N`（连续 64 个号缺失才停）；只认纯文本 Lua 资源；要求资源名 `mods/<作者>/<名字>` 且**首行**是 `-- HD2-Addon: 同名`；重名时高号 patch 胜 | ✅ patch 版 |
| **Chef Shared Loader** | 同上。它的 `boot` 只是个 stub，真 loader 在 `mods/chef_shared_loader/core_1`，用 `require` 起 | ✅ patch 版 |
| **MDL3 / HD2 Data Hub** | 替换 `boot`；它自己的 Data Hub 就是 `-- HD2-Addon: mods/codex/hd2_data_hub` | ✅ patch 版 |

⇒ **三家全都认 `-- HD2-Addon:` + `mods/<作者>/<名字>`**，所以 patch 版是通用解。

导出目录（`%LOCALAPPDATA%\HD2ModManager\LuaPackages\`）：

```
physbone/                      ← 物理
  lua版/hd2physbone.lua        ← 裸脚本（给按文件夹读 .lua 的加载器，也方便玩家自己看）
  patch版/                     ← 完整 mod 目录，拖进 mod 库即可
    manifest.json
    9ba626afa44a3aa3.patch_0
    ….patch_0.stream / .gpu_resources
  如何安装.txt
wardrobe/                      ← 映射，结构同上
```

**首行标记不能有任何前导空行或 BOM**：Boot Mod Loader 只看第一行；Lua 5.1 不跳过 BOM，
带 BOM 的脚本编译会直接失败（加载器通常不 pcall 引导脚本 ⇒ 游戏启动即退出，极难排查）。

---

## 6e. 多人局：按玩家分组（2026-11-03，运行时 2.0.0）

**用户报的现象**：多人模式里物理丢失（只有自己看到的那个角色有头发飘动 / 干脆全都没了）。

**根因不在物理，在调度层只认一个玩家**：

| 旧行为 | 多人局里的后果 |
|---|---|
| `discover` 只挑**一个** avatar（"有动画状态机 + `support_mg`"的第一个） | 多出来的候选一律当成尸体 |
| `playerChanged`：出现"没被忽略的新候选"就判定**玩家换人了** | 队友一进房间 ⇒ 丢掉自己的全部会话、重挂到队友身上，自己身上从此没物理 |
| 护甲件按"离那个 avatar 的距离"过滤（5 m） | 队友的护甲离 teammate 近、离"我"远 ⇒ 被当尸体丢掉 |
| 任何会话失效 ⇒ `sessions = nil` 全员重扫 | 一个人换甲，全场人一起闪一下没物理 |

**现在的模型：不回答"哪个是我"，只回答"哪些是玩家"。**

1. `World.units_by_resource(world, "content/fac_helldivers/cha_avatar/avatar_helldiver")`
   —— 一次调用列出场上**所有**玩家 avatar（老引擎没有这个函数时退回全量扫描，两条路都留着）。
   比"扫 2290 个 unit"便宜两个数量级，所以"玩家名单多久查一次"从 120 帧缩到 60 帧。
2. `World.debug_camera_pose` + `Matrix4x4.translation` → 相机位置；**离相机最近的 avatar 就是自己**。
   只用于排序与预算优先级；读不到不影响功能（日志写明用了哪种排序依据）。
3. 每个护甲件归属到**离它最近的那个玩家**；距离门限也相对**它自己的主人**算。
4. 会话记 `owner`（avatar unit）+ `ownerOrder`；名单变化只做**增量**：补没挂的、收主人不在的。
   已有会话对象一个都不重建 —— 这就是"队友加入时老人不闪断"（测试按对象同一性断言）。

**预算（用户要的"合并队列"）**：一帧成本 ≈ 3.5 次引擎调用/链（实测单人 52 链 ≈ 180 次/帧）。
链数超过 `maxChainsPerFrame`（默认 160）时，**第一位玩家（自己）每帧照算，其余玩家 stride=2 分帧**，
被跳过的帧把 dt **累积**到下一次（compat 内部再拆子步），所以物理速度不变、采样率对半。
⚠️ 分帧带来一个假信号：**这一帧按 stride 谁都不该算** 时 `written + skippedSame` 天然为 0，
必须用"有没有尝试过"（`attempted`）排除，否则"驱动失效看门狗"自己触发重挂 → 无限循环。

**离线验证**：`luajit HD2PhysBoneCompat/test/mp_harness.lua`
（假引擎 + 假求解器，24 项断言：四人各挂一件 / 中途有人加入不碰老人会话 / 有人退出只收他 /
分帧后每会话 dt 总量守恒 / 分帧不触发失效重挂 / 无 `units_by_resource` 时的扫描兜底 / 单人局不回归）。

**尚未实机验证**：`debug_camera_pose` 在某些版本上可能返回**开发自由相机**而不是玩家相机 ——
那种情况下"谁是第一位"会退化（只影响排序与预算优先级，不影响"每个人都有物理"）。
实机确认方法：日志里的 `[physbone] 玩家排序依据：…`；四人局心跳里应出现 `玩家=4`。

---

## 6f. 求解器保真度大修 + 碰撞层（2026-11-03）

这一天先做了**四路调研**（Valve `jigglebones.cpp` 原文 + 本机 L4D2 二进制统计 3392 条真实 jigglebone 记录 /
HD2 Lua 能力边界审计 / 现有实现逐行审计），再照着证据改。三份调研报告在
`HD2RuntimeArmorOverride/notes/`：`l4d2-jigglebone-research.md`、`hd2-bone-lua-capability.md`、
`physbone-current-state-audit.md`。**先读它们，别重新推。**

### 6f.1 关键事实（这次才定死的，别再猜）

| 事实 | 依据 |
|---|---|
| **原版 jigglebone 没有任何碰撞**，骨骼之间也不通讯 | `source-sdk-2013/src/public/jigglebones.cpp` 全文零碰撞查询 |
| **回正力就是"回到动画目标"的弹簧**：`err = goalTip − tipPos`，`goalTip = base + length·fwd` | 同上 |
| **重力 = 世界 −Z 的加速度，幅值就等于 `tip_mass` 的数值**（不是质量；Source 重力 600 u/s²） | 同上；`tip_mass 200` ≈ 0.33 g |
| **`length` 是虚拟摆杆长度，不是到子骨的距离** | Valve 自己的 24 根骨 `length/真实骨距 = 1.2×~34×`（中位 2.4×）；第三方 mod 一律写死 30（实际骨距中位 3.5 ⇒ 8.5×）。**我们测到 3.82× 完全正常，不要"修正"它** |
| 稳态下垂角 **`sinθ ≈ tip_mass/(stiffness·length)`**（不是 `tan`） | 单节水平杆实测：头发参数 tan 公式 7.59° vs 实跑 7.66°；布料参数 tan 公式 72.26° vs 实跑 **90.00°** |
| HD2 Lua **完全没有物理查询 API**（`PhysicsWorld`/`Actor`/`Joint`/`Mover`/`Cloth`/`Raycast`/`NavigationMesh` 整层被裁） | `hd2-armor-physics-research.md:453-457`；67 个 Lua 文件 `raycast\|spherecast\|PhysicsWorld` 零命中 |
| 写骨能让渲染变，但**与 avatar 同名的骨写了不参与渲染**（按骨名从 avatar 重定向）⇒ 只有"avatar 里没有的新骨"写入才生效 | `hd2-bone-lua-capability.md` §12；这正是我们插合成骨路线成立的原因 |

### 6f.2 修掉的三个确定 bug

| bug | 症状 | 修法 |
|---|---|---|
| **`prev` 直接抄上一节的模拟方向**（丢掉 bind 形状） | 零力平衡态是**一条直线**，且 `tip.ready=false` 时**首帧就把整条链掰直** ⇒ 用户说的"头发不会自然下垂、反而指向一个错误的方向" | 本节目标方向 = 上一节的模拟方向 **经「上一节→本节」的绑定相对旋转**转过去。`dirs[k-1] == rest[k-1]` 时正好等于 `rest[k]` ⇒ bind 形状就是平衡形状。每帧预算一次 `restDir`/`relDev`，子步里只剩 1 次 `axis_angle` + 1 次 `qrot` |
| **分叉树被 DFS 拍平成一条链** | 兄弟分支被当父子，真实资产 `silent_code_shinano` 骨 96→{97,98,100} 造出 **154.3°** 人造折角；而生产日志里数千帧恒定的"偏离绑定峰值"恰好 154.3° ⇒ 用户说的"闪电状折痕" | `build_chains` 改成**切成互不重叠的路径**：链根 + 分叉节点的每个子骨各起一条，分叉节点在自己那条路径收尾。每个骨只被一条路径驱动一次 |
| **`swing_axis` 反平行返回 nil**，调用方当零旋转 | 180° 的转弯变成 0°（实测目标 179.95°/180° 输出 0°，方向误差 179.95°/180°） | 反平行时返回**确定的**垂直轴 + π。同一输入必须每次同一条轴（随机翻会抖） |

### 6f.3 新增能力：球 / 胶囊碰撞（纯 Lua）

`M.build_colliders` + `M._push_out_of_colliders`。代理从 **HD2 原生骨名**生成
（`head↔neck`、`neck↔chest`、`hips↔chest`、`chest↔l/r_shoulder`、`l/r_shoulder↔l/r_elbow`、
`hips↔l/r_thigh`，共 9 个），半径按 `dist(hips→head)/0.62` 缩放。
缺一端 ⇒ 退化成球；两端都缺 ⇒ 计数跳过（**绝不静默关掉整层**）。

- 顺序：**先推出身体、再恢复定长**（反过来的话恢复定长会把点拉回身体里）。
- 速度：去掉**朝体内**的分量（法线朝外 ⇒ 朝内是 `−n`，别照抄"去掉朝墙外"）。
- 每帧每节点只采样一次（`head` 会被十几条链共用），按节点缓存。
- **没有代理时整段空转**，`colliders == nil` 与旧行为**逐位相同**（有测试钉着）。
- 成本：链节数 × 代理数 × 子步数。54 骨 / 28 链 / 9 代理 ≈ 每子步 250 次胶囊距离。
- 已知边界：定长约束把尖端钉在半径 L 的球面上，而那个球面**未必**与碰撞面相交 ⇒
  能保证"明显比不碰更远、且贴着碰撞面"，**不能**保证严格在外。这是单摆式求解的固有限制。

### 6f.4 验收（全部离线，`luajit`）

| 脚本 | 钉住什么 |
|---|---|
| `HD2PhysBoneCompat/test/bind-shape-test.lua` | 重力=0、锚点静止 ⇒ 每节 local 旋转 **0.000°**；弯曲链保留弯曲；分叉不造假折角；180° 真是 180° |
| `HD2PhysBoneCompat/test/collision-test.lua` | 推出后贴碰撞面（0.051→0.060）；**无代理时逐位不变**；球心重合/零长度胶囊不出 NaN 且确定；缺骨计数 |
| `tmp/lua-check/zigzag-lab4.lua` | 真实 rig 上的折返指标：`source_extra_95` **29%→0%**、`_93` **59%→6%**（其余两条 4 节链仍是 31%） |

验收类 `*-test.lua` 已从 gitignore 的 `tmp/lua-check/` **移进版本库**（`HD2PhysBoneCompat/test/`），
以后不会再"脚本还在不在看运气"。全部 16 套 Lua 测试 + `dotnet test HD2ModCore.Tests` 888 项通过。

### 6f.5 `serialBase` 定案：**不要开**

`step_tip` 里那段"用上一节模拟尖端当基座"的分支（注释说是为了修锯齿）**从来没被赋值过**，是死代码。
这次离线开它做了对照：折返**占比**下降，但最大折返**幅度**从 14.7~34.0° 恶化到 **69~96°**、
首尾夹角 60°→114°。**继续关闭**，并把实测数字写进注释，免得下次又有人想去开它。

### 6f.6 还没做（下次接手先看这里）

1. **非对称限位**（`pitch −45..0` 现在按 ±45 夹）。不只是猜正负号：当前 rig 里**没有源骨方向标架**，
   运行时的左右/上下轴是自己叉出来的 ⇒ 要先让容器带上源标架。
2. **`has_base_spring`（12 个 `base*` 参数）**：需要引入"局部平移驱动"，会改掉"只写旋转"的契约，独立设计。
   **本轮先保证它 100% 被报成"不支持"而不是静默忽略。**
3. **自穿插**：相邻节最小距离不能冒充完整自碰撞（相邻粒子距离本来就被定长约束保证）。
   真要做得检测**非相邻骨段**并做空间哈希。
4. **`solver` 选项没贯通**：`attachOptions` 不传 solver，`update_tip` 又把所有非 `"angle"` 的值
   都送进 `update_tip` ⇒ 设 `solver="chain"` 会静默继续跑 tip。加新核之前必须先补分派。
5. **物理总账**：现在报出了"可用链 N 条（缺节点/缺锚点）"和碰撞代理数，但还缺
   「源 jiggle 数 = 保留数 + 有原因未保留数」的**闭环核对**。
6. **"真正的自定义骨架"的边界**：jiggle 骨会被插成合成骨并保住自己的权重；**非 jiggle 骨一律回退到最近的
   HD2 原生骨**（权重被合并 ⇒ "刚性头发"的根源）。完整移植 L4D2 骨架做不到，因为动画来自 HD2 自己。
   下一步能做的是"装饰子树跟随"（rest offset + 锚点变换），但**它不会让刚性头发自己摆动**。

---

## 6g. 撤掉频段夹取 + 粒子链接线上岗（2026-11-03 第二轮）

用户实测反馈：**"头发不再乱飞、闪电折痕也不因僵硬而显眼，但甩动时像双截棍/多截棍
一段一段地折"**；并且明确要求「尽量复用来源参数，不要手写参数」。

### 6g.1 先回答那个问题：来源里有"硬 ↔ 软"这个旋钮吗？

**有，而且作者已经用它把两族分开了。** 就是 `yaw_stiffness` / `yaw_damping`
（换成 ωn = √k 与 ζ = c/(2√k)）：

| 类型（真实资产） | k | c | ωn | ζ | 作者意图 |
|---|---|---|---|---|---|
| 头发（Lilium 头发骨） | 50 | 7 | 7.07 rad/s | 0.49 | 弹、回位快、会回弹 |
| 麻花辫（旗袍 `pd.*`） | 50 | 7 | 7.07 | 0.49 | 同上 |
| 大袖子（`_93`/`_95`） | **1** | **10** | **1.00 rad/s** | **5.0** | 沉、滞、被重力主导 |

ωn 差 **7 倍**、ζ 差 **10 倍**。另外 `tipMass`（头发 200 / 袖子 500 / 辫子 **0**）
决定"会不会被重力拽下去"，`angleLimit` + `yaw/pitch min-max` 决定"能摆多大"。
`flags` 里的 `is_rigid(0x02)` 与 `base_spring(0x40)` **仍未实现**。

### 6g.2 我们以前正在擦掉它 —— 现已撤掉

`normalize_gains` 原先把 ωn 夹进 [4,12]、ζ 夹进 [0.4,1.2]：

- 头发 7.07 / 0.49 在带内 ⇒ 原样；
- 袖子 1.0 / 5.0 → 被夹成 **4 / 1.2** ⇒ "很软很滞的布"被改写成"中等硬、还有点弹的链"。

**这就是"多截棍感"的一部分来源。** 现在 `gains_of` **默认不夹**，
只有显式 `{ normalize = true }` 才走旧口径（诊断脚本与 A/B 用）。
`M.OMEGA_BAND` / `M.ZETA_BAND` / `M.normalize_gains` 保留但不再是默认路径。

### 6g.3 粒子链接线（`solver = "chain"`）

- `attach` 现在填 `tipData.spacing`（从 `offLocal` 量出的**真实骨距**）和 `chainOptions`；
- `update_tip` 真正按 `session.solver` 分派（以前**所有非 "angle" 的值都送进 tip**，
  设 `chain` 会静默跑旧核）；日志里新增一行
  `[physbone] 求解器：chain（粒子链/弹性杆）`，排查时一眼能确认；
- 运行时 `attachOptions` 补上 `solver` / `chainOptions` / 碰撞三项
  （以前不传 ⇒ 设置形同虚设）；
- 管理器新增**设置项「物理求解器」**（`PhysicsSolvers`：`tip` 默认 / `chain`），
  存字符串、原样写进 Lua 的 `install` options，独占 boot 与外置包两条路都传。

### 6g.4 补上缺失的**回正弹簧**（这一条很关键）

粒子链原先只有重力 + 距离 + 弯曲约束，于是**一条水平伸出的链会绕根整根转到竖直**——
那是单摆，不是头发。补上 Valve 语义里唯一的作用力：每个粒子被拉向
「当前锚点下的 bind 位置」，刚度取作者的 `kYaw`、阻尼取 `cYaw`。

结果就是**硬软完全由作者决定**（`chain-solver-test` T3 钉住）：

| 用同一套代码、同一激励 | 偏离 bind 平均角 |
|---|---|
| 头发族 k=50 | **6.5°**（贴着 bind 抖，不下垂） |
| 大袖子族 k=1 | **86.6°**（被重力拽到接近竖直） |

大袖子"垂到手臂正下方"是作者在 T-pose 水平摆好、靠重力落下去的设计（用户确认），
所以**那条链偏离大是对的**；要修的只是"折线"。

### 6g.5 修掉一个静默失效：碰撞层从来没生效过

碰撞代理一直挂在**会话项**上，而求解器拿到的是 `tip` ⇒ 读不到 `colliders`。
日志里"碰撞代理：建起 9/9"是真的，但**一次都没参与过求解**。现在挂到 `tip` 上。
（`chain-solver-test` T5 用"有代理会推开 / 没代理保持笔直"的对照钉住。）

### 6g.6 实测（真实 rig，同激励：偏航 ±40° @0.7Hz + 平移 ±15cm）

| 链 | tip 现状 折角/摆幅 | chain 折角/摆幅 |
|---|---|---|
| Lilium `_93`(7节) | 25.7° / 1.096 | **9.0°** / 0.495 |
| Lilium `_95`(7节) | 27.1° / 1.096 | **8.6°** / 0.495 |
| 旗袍 `pd.021`(4节) | 79.5° / 0.453 | **42.3°** / 0.442 |
| 旗袍 `pd.008`(3节) | 50.0° / 0.453 | **4.5°** / 0.442 |

`pd.021` 的 42.3° 是**模型 bind 自带的折角**（作者骨骼链本身就在那一节弯了 42°），
不是新增的铰链 —— 仿真没有在它上面再加折角（tip 核会加到 79.5°）。

**尚未实机验证**：`chain` 目前只有离线证据。默认仍是 `tip`，实机行为逐位不变。

### 6g.7 收尾：碰撞层与粒子链**已全部移除**（2026-11-03 晚，用户决定）

实机两轮测完，两个都判死，代码已删干净（不是关掉）：

**粒子链 `chain`** —— 摆幅只剩一半（0.36~0.53 m，tip 是 0.45~1.10 m）。
回正弹簧为了"不折"把整条链拉得太紧，观感就是"头发焊在头上跟着转"。
三种旋转搬运模式（不搬 / 刚体搬 / 带欧拉+离心+科氏惯性力）全试过，摆幅都上不去。
**判据教训：不能只看折角，摆幅掉下去同样不算修好。**

**碰撞层** —— 341 链 × 5 粒子 × 9 代理 × 4 次迭代 ≈ 6 万次胶囊测试/帧，
实机把主线程从 7.15 ms 顶到 17.23 ms、FPS 82.8→58。预计算常量 + 外接球早退只压到 6.8 ms，
仍然太重；而且**原版 L4D2 本来就没有碰撞**，属于锦上添花，性价比不够。

### 6g.8 性能实测与结论（这一轮唯一保留下来的性能改动）

游戏内性能采集器（同场景、同机位）：

| 设置 | FPS | 主线程 | GPU | GPU 占用 |
|---|---|---|---|---|
| 物理关 | 86.8 | 6.48 ms | 12.02 | 92.8% |
| 物理开（碰撞已关） | 88.2 | 10.96 ms | 10.23 | 84.9% |

**这一场景是 GPU 瓶颈**（12 ms），所以主线程多了 4.5 ms 也**没有掉帧**；
上一轮掉到 58 fps 是碰撞把主线程顶到 17.2 ms、超过 GPU 造成的。

离线拆解（`jit.off()` 模拟 PUC Lua 5.1，折算到 418 链）：

| 项 | 成本 |
|---|---|
| `step_tip`（含曲率扩散） | ≈ 1.2 ms |
| `local_rotations` | ≈ 0.5 ms |
| 曲率扩散本身 | 仅 +0.3 ms（很划算，留着） |
| **引擎调用（~1000 次/帧）** | **≈ 2.8 ms** |

⇒ **钱主要在引擎调用，不在 Lua 数学**。每次写姿态 = 2 次调用
（`Quaternion.from_elements` + `set_local_rotation`），所以唯一有效的旋钮是**少写**：
`writeEpsilon` 从 0.0004 rad（0.023°）提到 **0.0015 rad（0.086°）**。
对 10 cm 的骨是 0.15 mm 末端位移，肉眼看不出，但慢速摆动时能跳过一大批"刚刚超一点点"的写入。

工具：`tmp/lua-check/bench-prod.lua`（每帧成本拆解）、`chain-geometry.lua`（逐链几何体检）。

### 6h. 重大更正：jiggle 骨**没有动画**（"依赖动画"的推断已被证伪）

**结论（二进制证据，2026-11-03 晚）**：L4D2 里 jiggle 骨的本地旋转**恒定**，永远没有动画轨道。
详见 `HD2RuntimeArmorOverride/notes/jiggle-animation-evidence.md`。

- 用户自己的 `Lilium_Sancta_Bill.vpk` → `survivor_namvet.mdl`（v53 / 124 骨）：
  54 根 jiggle 骨在 64–123，5 个 animdesc 的 `mstudioanim_t` 链**最大只到骨 61**，
  jiggle 骨零入口；同文件 43 根身体骨有 40–56° 的真实运动（⇒ 不是解码失败）。
- 官方 20 个带动画的 jiggle 模型、**21,345 个「骨×clip」槽位：0 个有轨道**；
  同文件对照的 17,145 个非 jiggle 槽位都带 ANIMROT。
- 最锋利的反证：`v_electric_guitar` 的 jiggle 骨=63，keyed 的是 0–62 与 64–68，
  **63 是唯一空洞** —— 刻意跳过，不是越界。
- 角色动画在 `.ani` 而不是 mdl 里；`.ani` 记录格式已逆出（`+0 int == 24+骨数`，
  `+24` 起每骨一字节标志位），276/276 条自洽。

⇒ **观感 100% 来自 `jigglesbones.cpp` 那套求解器。我们缺的不是动画，是实现保真度。**
此前本文档 §6g 里"缺动画导致观感不同"的表述**作废**。

### 6h.1 由此暴露的真正缺口：运行时做的不是离线实验室里那个模型

离线（rig 源空间坐标）实测：
- `_73`（4 节、限位 35°、tipMass=200）：最大偏离 **4.7°**，**从不超限**，与作者应然的 7.7° 静垂角一致；
- `_93`（7 节、`angleLimit=360°` ⇒ 作者本就**不限位**、tipMass=500）：最大偏离 **113.4°**，但不超限（因为无限制）。

而**实机日志**：`_93` 偏离绑定峰值 46.9°、史上 **121.4°**；视频定量：能量频率右移 2 倍（1.58→3.11 Hz）、
周期比参照**更规则**（抖动 0.530→0.421）、几何伸展量反而更小（0.72×）。

**实验室"合规"、实机"不合规" ⇒ 运行时采样的骨骼位姿与 rig 源空间坐标不是同一套（H3：参考系/摆放）。**
下一步必须做两件事（都不改物理行为）：
1. `attach` 打一行诊断：**rig 推出的 bind 方向** vs **运行时采样到的 bind 方向**；
2. 转换报告补 **链 → 顶点归属**（每条链驱动多少顶点、bind 重心、半径）——否则无法把
   "画面上的某块布"对应到"某条链"，任何诊断都只能靠猜。

已排除的假设（连同证据，别重推）：
- 阻尼不足：阻尼 ×8，频率不变、稳定时间只降 28%（3.70→2.67 s）。
- 静止门限制造抖动：离线复刻门限，实验里门限根本不触发（跳过帧占比 1%）；且实机该场景手臂在动、门限本应关闭。
- 曲率扩散注入能量：bendBlend 0 / 0.5 / 1.0 的稳定时间 3.70 / 3.70 / 4.30 s，差异不显著。

### 6i. 用户确认的手感基线：`55fbff6`

**2026-11-03 定论：`55fbff6` 是用户认可的手感基线。** 后续任何物理改动都应以它为对照，
并且**一次只改一个变量**。

这一轮（b560aaf→353ff41）试过并**全部回退**的四项，方向都不算错，但单点改动顾此失彼：

| 改动 | 离线证据 | 为什么回退 |
|---|---|---|
| 尖端状态随锚点平移搬运 | 修掉"一动就整条链瞬间倾到底"（第 1 帧 46°→0.4°） | 连带把"拖动感"修没了（`tip.v` 从世界速度变成相对速度） |
| 阻尼改作用在世界速度（风阻） | 跑动 3 m/s 时逐段拖出 33.8/62/88° | 阻力 `c·V`=21 m/s² ≈ **5.5 倍重力** ⇒ 尾部飞过根部 |
| 锚点伪力（线加速度） | 起步/急停能把布料甩起来 | 二阶差分被帧时 ±40% 放大到 **435 g**；改低通后仍与上一条叠加失控 |
| 锥限位改相对自身静止方向 | 34/60/70° → 34/35/34°（不累加） | 与风阻叠加后实机观感更差 |

**结论：这四项要真做成，必须把「风阻系数 / 限位语义 / 重力」当作一组参数联合标定。**
下一步建议是**离线参数扫描台**（风阻 ×1.0/0.5/0.25 × 限位 parent/rest，输出最大拖拽角、
是否翻过根部、是否振荡），一次选型，而不是"改一版→进游戏看"。

保留的性能改动与求解器无关（都在 `physbone_runtime.lua`）：
- 分帧步长按总量自适应（多人局每帧链当量 ~346 → ~116，≈3×）
- 原生调用能力探针（只读能力）

---

## 7. 命令与工具速查

```bash
# 转换（GUI 导入走的就是这条；别用 CLI 手工拼，会缺材质/衣柜/登记）
hd2mod vpk2patch <vpk...> --output <dir> --game-data <data> \
  --bone-hash <bonehash.txt> --manager-root <管理器目录>

# 物理参数：嵌进单个 patch / 整个 mod 目录 / 全库兜底
hd2mod physbone embed     <patch_0> <来源VPK或rig文件> [模型名] [--output <dir>]
hd2mod physbone embed-mod <mod目录> <来源VPK或rig文件> [--json]
hd2mod physbone repair-library --library <mod库>

# 应用为 RE2310（VPK mod 走这条，不是 custom-model convert）
hd2mod vpk rekey <Mod GUID 前缀> --manager-root ... --library ... --game-data ...

# 构建
dotnet build HD2ModCli/HD2ModCli.csproj -c Debug
dotnet build HD2ModManager/HD2ModManager.csproj -c Debug   # GUI；改完必须重开管理器

# 测试
dotnet test HD2SourceMigration.Tests/HD2SourceMigration.Tests.csproj
luajit HD2PhysBoneCompat/test/p1-regression-test.lua HD2PhysBoneCompat/src/physbone_compat.lua
luajit HD2PhysBoneCompat/test/p2-solver-test.lua     HD2PhysBoneCompat/src/physbone_compat.lua
luajit HD2PhysBoneCompat/test/lifecycle-test.lua     HD2PhysBoneCompat/src/physbone_compat.lua   # I7 生命周期闸
luajit HD2PhysBoneCompat/test/rig-select-test.lua    HD2PhysBoneCompat/src/physbone_compat.lua   # 多 mod 选 rig
luajit HD2PhysBoneCompat/test/attach-cache-test.lua  HD2PhysBoneCompat/src/physbone_compat.lua   # 多人同款护甲去重
luajit HD2PhysBoneCompat/test/fast-write-test.lua    HD2PhysBoneCompat/src/physbone_compat.lua   # 快路径自检门
luajit HD2PhysBoneCompat/test/motion-gate-test.lua   HD2PhysBoneCompat/src/physbone_compat.lua   # 运动门限
# 生产运行时整链自检（发现→就绪→逐 unit 选容器→attach→驱动→换甲重挂→uninstall）
$env:LOCALAPPDATA="<repo>\tmp\harness-appdata"; luajit HD2PhysBoneCompat/test/runtime-harness-test.lua
# I8 探针整链自检（必须把 LOCALAPPDATA 指到临时目录，否则会覆盖真实日志）
$env:LOCALAPPDATA="<repo>\tmp\harness-appdata"; luajit HD2PhysBoneCompat/test/probe-harness-test.lua
```

**探针**：库里的 `hd2pbx-probe` mod（`E:\mod\hd2pbx-probe\`），部署后是 `data\*.patch_N` 软链接。
改探针 = 改库文件，软链接自动跟随，**不用重新部署**。

**离线诊断（我用的）**：`tmp/m2-probe` 的 `nodes` / `weights` / `bonespan` / `localtest` / `matrel` / `obj` / `pose` / `rekey`。
网格可视化：`tmp/geom/render_fixed.py`（Blender 无头，up=−Y、front=+Z）。

---

## 8. 文档地图

| 文档 | 状态 |
|---|---|
| **本文件** | ✅ 唯一入口 |
| `physbone-next-plan-20261101.md` | ✅ **下一阶段执行计划**（压缩上下文后的续接点，自包含） |
| `hd2physbone-embedded-patch-protocol.md` | ✅ 容器格式与注入规则（**协议级**，实现时必看） |
| `physbone-authoring-protocol.md` | ✅ 作者侧字段/映射契约 |
| `l4d2-hd2-boundary.md` | ✅ 引擎边界 |
| `physbone-compat-design-consolidated.md` | 📦 历史（评审过程、分歧裁定） |
| `agent-brief-physbone-compat-design.md` | 📦 历史（早期交接简报） |
| `l4d2-to-hd2-physbone-migration.md` | 📦 历史（调研阶段） |

---

## 9. 欠账（未做，按优先级）

> **2026-11-03 更新**：本清单已经部分过期。**先看 §6f.6**，那里是这一轮之后真正剩下的。
> 下面第 5 条（锥类限位沿链累加）已随"bind 形状当平衡态"一起变化——现在锥是相对
> **父节的绑定位姿**夹的，不再相对"父节当前模拟方向"，语义与 Valve 一致；
> 世界空间里的累计弯曲仍然存在，但那是 Valve 本来的行为，不是 bug。
> **碰撞体已经做了**（§6f.3），不再是欠账。

1. **下一阶段执行计划：`docs/physbone-next-plan-20261101.md`**
   （P0 观测 ✅ → P0.5 ✅ 整组同帧消失=场景切换 → P1 死链 ✅ → P2 可见性（报告 ✅ / 角标待做）
   → P3 dt ✅ → P4 文档 ✅ → **约束形式 ✅ 锥/分轴都认**）
2. **K1–K4 实机确认**（机制已修，见 §6.3/§6.4）
3. **旧 mod 需要重新导入**：一次重新导入同时带来两样东西——命名空间隔离的骨名（§3.5），
   以及 `vpk2patch/physbone-rig.bin` 里的**链根锚点名**（没有它裙摆/缎带那类链会被记「缺锚点」跳过）
4. ~~双模运行~~ ✅ **已实现**：同一套 Lua 既能独占 boot（管理器的 boot 文档 require），
   也能被第三方加载器当模块加载（`physbone_runtime.lua` 的 `install()` 幂等、只链式包 render）
5. **锥类限位沿链累加**：`angleCon` 的限制是"相对父节"的，5 节 × 24° 可累到 120°。
   头发里最长的辫子/刘海属于这一类。改成"相对自身静止方向"是个设计选择，1.4.0 时未被投诉，暂不改
6. **约束只支持"对称"**：源模型的 `min/max yaw/pitch` 是非对称区间（实测 `pitch 0..30` 只许一边），
   现在按 `±max(|min|,|max|)` 对称夹。要真按单边夹得知道 Valve 的符号约定，
   猜错会让那根骨**完全冻住**——比多晃两下难看得多。见计划 §6.0c 末
7. 只驱动 LOD0（LOD1–4 未重定向权重）
8. ~~嵌套锚点链（锚点是另一根 jiggle 骨）部分跳过~~ —— **诊断有误**：
   真实原因是"非 jiggle 父骨不在 rig 里"，见 `physbone-next-plan-20261101.md §2.2`（已修，P1）
9. 两个写入器的字节布局层仍重复实现（可合并，但风险高）
10. 管理器"自管理设置项"：部署前自动兜底 `repair-library`（现在靠手动/导入时自动）
11. 作者协议文档对外发布（等导入链路完全稳）
12. Mod 卡片上的「无物理参数」角标（导入报告 note 已经有了；角标要给每行接新数据源，见计划 P2）

---

## 10. 第三方加载器（MDL3）情报 · 2026-11-01

从 `MDL3-3.0.0-dev.21-preview` 包里读到的事实（**只作参考，不依赖**）：

- **游戏 VM 是 PUC Lua 5.1，没有 `ffi`、没有 `jit`**。MDL3 自己注入的脚本里明写
  `require('ffi') is not available in the game VM (Lua 5.1)`。
- 所以它所谓的 "FFI 支持" 是**它自己的 native host**（`mdl_native_bridge.dll`，
  签名 + SHA256 校验）通过 `package.loadlib(dll, 'luaopen_mdlbridge_call')` 暴露的
  **只读代理**：`cdef/new/get/set/cast/C/run`，每帧在 `update` 里 `_update(deadline)` 轮询，
  `_retire/_closed` 管生命周期。**不是真 ffi**，也拿不到写能力。
- ⇒ 我们**不需要 FFI**：参数走 patch 尾部容器 + `io.open` 相对路径已经够了（§3.4）。
- 有价值的旁证：`stingray.Unit.alive` 被它用来判活（与我们的 I7 同一条路）。
- **兼容要求（后话）**：加载器把 mod 当 library addon 载入时，
  脚本拿到的是**被包装过的环境** —— `update`/`shutdown`/`init` 是捕获的替身，
  `package` 多一个 searcher，`require` 走它自己的缓存。
  我们的 Lua 若要做成"两种模式都能跑"，判据是：
  **不假设自己拥有 `_G`、不假设 `update` 只被调用一次、所有状态挂在模块表上而不是全局**。

