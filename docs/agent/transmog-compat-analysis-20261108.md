# Transmog 兼容包照出的软映射 API 缺口（2026-11-08）

> **状态：只记录，不实现。** 这份文档是"别人为了接我们，被迫做了哪些事"的取证；
> 结论是**我们该对外发布什么**，不是"我们马上要改什么"。

来源：`C:\Users\Administrator\Downloads\幻化适配护甲映射.zip` 中的
`Transmog-HD2MM-Compat-0.2.4`（作者为一位用户 + 其 agent 所作）。
本仓核过：`python tools/verify.py` → `FAILURES: 0`（它的自证是真的）。

## 1. 它做了什么

上游是 **HD2 Transmog 1.1.2**（幻化，Nexus 16633，作者 tyrypyrking），单文件 Lua bundle
2.4 MB / 18,784 行，靠 `MDL3(游戏 boot) → Chef Shared Loader → Rat's Shared Loader` 起来。

**显示方式是"克隆"，不是换资源**：对要显示的件 `World.spawn_unit` 复制一份 → 按 avatar
骨架名 `link_unit` 挂骨链 → 绑材质；**原生件只是 `set_unit_visibility(false)` 隐藏，
属性/被动仍由它提供**。上游注释原话：`a spawned copy has no cloth simulation`。

⇒ 结构性代价（用户已指出）：幻化过去之后**原件仍在渲染，等于同屏两套模型**；
它自己的文档把这条列在"已知限制"里（"重模型会被渲染两份"）。

兼容包在它上面做 7 处注入，解决两件事：

| 链路 | 做法 |
|---|---|
| **外观** | 1 Hz 读我们的 `armor-registry.protocol`，把被 `map_model` 命中的 kit 的 `data.kits[kit].bodies` 整表换成我们的 donor unit；保留快照、逐字段还原 |
| **物理** | 把 `spawn` 出来的克隆件塞进我们物理运行时的会话表（借 `Compat.attach`），并**复刻运行时内部字段**（`owner`/`ownerOrder`/`attachFrame`/`rigSource`…），另加"隐藏件停车"与"单副本 24 链上限" |

## 2. 它照出的 8 个缺口

| # | 缺口 | 证据（本仓 / 对方文档） |
|---|---|---|
| 1 | **协议在游戏里没人读得懂** | `RuntimeArmorRegistryLuaSource.Load()` 只投放 `armor_override_runtime.lua`（195 行、无 ffi）；唯一读协议的 `armor_registry_runtime.lua` 首行 `require('ffi')`，本版引擎无 ffi ⇒ 加载即退出（`CliWardrobe.cs:25`、`RuntimeArmorRegistryLuaSource.cs:13` 自己写着这句）。对方只好自写 507 行 Lua 解析器 |
| 2 | **权威与生效分家，且生效那份单向有损** | `armor-registry.protocol`（权威，本机 30.4 KB / 299 条 `map_model`）vs `wardrobe.links`（生效，本机 80.4 KB / 2061 条 `link`）。links 只有 `link=<own unit>,<donor unit>`，不表达 body/slot/件类型，也不表达"这套由哪些件组成" |
| 3 | **`revision` 每次写都 +1，与内容无关** | `RuntimeArmorRegistryService.SerializeProtocol` 前身 `SaveAsync` 里 `current.Revision + 1` 无条件。对方被迫自造"签名不含 revision"的去重（`notes-registry.md` §5.5） |
| 4 | **协议没有能力声明** | 我们有 `piece_override=`、`composition_mode=`、target modes，但对外没有 `abi=`/`features=`；对方只能一律"未知键忽略"（`hd2mm_registry.lua:121`），无从判断该不该处理 |
| 5 | **物理运行时没有对外 API** | `physbone_runtime.lua` 有 `M.VERSION`、吃 `options`，但没有"外部挂一个 unit"的入口。对方只能读源码后**复刻内部字段**：`owner=nil`（不然会被回收）、`ownerOrder=1`（否则不每帧驱动） |
| 6 | **运行时只认"自己扫到的件"** | 按"节点数 ≥90 + 最先看到"挑护甲 unit；幻化状态下它挂上的是**隐藏的原生件**，对方只能靠 `parked` 事后摘除 |
| 7 | **热/冷边界没有可观测状态** | "删除映射 / 整体关闭必须重启"（`set_resource_override` 建立的实例进程内不可撤销）只写在 `armor_override_runtime.lua` 注释里；`runtime.status` 没有 `pending_restart` 之类字段，界面无法提示 |
| 8 | **规模已超出对方测过的量级** | 对方机器：58 映射 / 457 links。**本机现在：299 映射 / 2061 links / 80.4 KB**。我们的运行时 `SET_BUDGET_PER_TICK = 32` ⇒ 一次全量变更约 65 tick（≈1 秒）才收敛 |

附：`ArmorRegistry/` 目录里 `runtime.status` 是 **2026-10-19 的 `runtime_version=0.2.0`**
（ffi 内存修补时代）残留，报 `last_prepare_ms=1470` / `located_after_ms=11616` /
`first_applied_after_ms=13093`——**看起来像现状，其实是旧运行时的化石**，会误导读日志的人。

## 3. 如果要做，建议的顺序（本期不做）

| 层 | 内容 | 判据 |
|---|---|---|
| **P0** | 发一个**无 ffi、只读、纯 Lua 5.1** 的协议读端（把"解析"与"内存修补"彻底解耦），随 addon 投放并挂 `_G`；协议加 `abi=` / `features=` / `digest=`（内容摘要，`revision` 只表示写次数） | 第三方 `require` 就能拿到"某件护甲该显示哪些件"，不必自写解析器；`revision` 不涨而内容不变时，消费方零成本判定"没变" |
| **P1** | 物理运行时对外 `api.attach/release/capabilities`；会话带"来源"标签，`parked` 成为运行时语义而不是外部补丁 | 外部 mod 用 3 个调用挂上克隆件，**不再碰 `owner`/`ownerOrder`/`sessions` 内部字段** |
| **P2** | `runtime.status` 报 `pending_restart` / `applied/total` / `converge_ms`；重估 `SET_BUDGET_PER_TICK`；清理 0.2.0 时代残留文件 | 界面能说"这次要重启"；变更收敛时间有数字且不超预算 |

## 4. 明确的边界

- 上游 Transmog 是**克隆显示**架构：物理桥接能救"克隆件不动"，**救不了"两套模型都在渲染"**。
  这条成本不归我们，也不建议我们去救。
- 对方这套是 **Foundation 1.1.2 的 fork**（7 处注入、同 Guid），上游一升级就得重做。
  所以"对外契约"的价值不只是性能，而是**让别人不必 fork**。
- 对方版本矩阵核的是我方 **1.5.1 / 1.5.2**；本仓当前 **1.5.5**。它自己文档也写明
  "升级后要按真机判据复核"（`hd2mm.mapping` 的 `kits=`、`physbone.engine` 的 `id32=yes probe=ok`、
  `physbone.match … full=true`）。

## 5. 未验证

真机帧数、多人环境下他人看到的幻化效果、donor unit 的真实可加载性、
以及"1.5.5 上还被它那套认不认"——都需要实机/多版本对照，本文一条都没验。

## 6. 参考

- 对方文档：`docs/01-使用指南.md`、`docs/02-技术文档.md`（在兼容包 `release-0.2.4.zip` 内）
- 对方实测成本（真机，其 `notes-physbone.md` §4.6）：运行时基线 **10 unit / 373 链 /
  ≈1084 引擎调用每帧 / dt 0.017**；挂 7 个克隆件后 **17 unit / 625 链 / ≈1800 / dt 0.023**；
  即 **≈2.9 次引擎调用/链/帧**。他们靠"停车 + 单副本 24 链上限"把预期压到 ≈490。
