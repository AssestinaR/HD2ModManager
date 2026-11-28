# 交接：护甲识别 + 抗污染修补（2026-11-24 状态）

> **⚠️ 2026-11-28 更新：这份交接描述的是已封存的实验。**
> 修补表 / 克隆 / NPC 会话那一整套（本文的 ①②、③ 的大部分实现、④）**没有进 master**，
> 完整代码在 `archive/repair-clone-20261128`（tag 与分支同名）。
> 进 master 的只有：只读探针（A 组）、民主官的头补成 slot0 件（`WithOfficerHead`，B 组）、
> 同位素标记可行性测试（D 组）。
> 取舍与理由见 `docs/agent/changes-review-20261128.md`；本文的**事实与踩坑部分仍然全部有效**，
> 只有"当前正在做"的措辞过期。

给接棒的人（或压缩上下文后的我）。**先读这一份，再读文末"必读文档"。**

---

## 0. 一句话现状

软映射的**修补表**已经能正确工作（AC 系列实机验证：进图 / 换甲 / 死亡重生 / 多名玩家都正常），
但**认甲**（判断玩家穿的是哪套）在"两套护甲共用承载件"时会失效 —— 因为引擎的
`set_resource_override` 是按**资源 id 全局**生效的，共用件只能显示一个结果。
当前正在做的是"给每套护甲造一个专属隐藏件当身份标记"（同位素标记），**尚未实现**。
民主官（NPC）的位置和部件清单**已用探针 v6 确认**（他穿 FS-05，锚点 `82595b5d94c57343` 实例存在），
只剩"他的头是哪个 unit"没定 —— 需要用 v7 跑一次定点扫描。

**分支**：`wip/repair-rig-experiment`（本地提交，未推送、未发布）。用户明确：发布要等他点头。

---

## 1. 用户的核心诉求（原话要点）

- 软映射必须还原**管理器里设定的外观**；不还原就"移除不合适的 unit、克隆合适的 unit"。
- 额外的注册表（修补表）是允许的，但**不要重构映射本身**。
- 观感要"尽可能完整"：**对半径内所有可疑 unit 都施加效果**（展台/队友/尸体一并处理），
  不必像物理那样只照顾玩家（物理省性能是因为它每帧写几百根骨，我们只是改可见性 + 克隆）。
- 头盔（slot0）、披风（slot1）是**独立装备**，不能用来判断"穿的哪套护甲"（可以混戴）。

---

## 2. 已确证的硬事实（不要再重复验证）

### 引擎能力
1. `set_resource_override('unit', 源, 目标)`：**按资源 id、进程级、只影响之后实例化**；
   **同一 source 先写生效**（多次写只有第一次起作用）。
2. 被覆盖出来的实例登记在**目标（donor）名下**；`World.units_by_resource(源 id)` 返回空。
3. `Unit.debug_name(u)` 返回 `#ID[<资源id>]`（**按资源**，不是按实例）；对活 unit 调安全。
4. `Unit.resource_name` / `Unit.has_mesh` / `Unit.bones` / `Unit.num_meshes` / `World.entities`
   —— 都有崩点前科，**禁用**。
5. **`World.units` 扫描是安全的，但有纪律**（`kit_instance_hider.lua` 实机结论）：
   - 句柄**不能跨帧**：每拍重新取列表；
   - 读坐标前必须 `alive` → `num_scene_graph_items > 0`（否则原生 AV，pcall 拦不住）；
   - 命中项在同一拍内处理完。
   v3/v4/v5 按此跑完 2200+ unit 扫描，**零崩溃**。
6. **引擎没有可读的"当前装备"数据**（v1–v3 探针实测）：
   `_G` 只有 51 个全局、无 `data`；`Application.has_data` 六键全 false；
   `Application.get_data({})` / `{'kits'}` / `{'armor'}` / `{'equipment'}` 全 nil；
   `Unit.has_data(avatar, 26 键)` 全 false；`Unit.get_data(u, {})` 对 16 个身边件全 nil。
7. **引擎有键盘输入，且静态形状可用**（v6 实测）：`Keyboard.pressed(Keyboard.F8)` 直接返回布尔、
   不报错；`Window.keyboard(main_window)` 返回 `string`（不是设备）⇒ 带设备参数那种形状不通。
   命名空间里另有 `released` / `button_id` / `F2…F12` 常量。

### 数据结构
8. 头盔 slot0、披风 slot1 独立；**slot2–9 才是护甲本体**。
9. 护甲记录之间存在大量**共用承载件**。实测（全库 141 条有身体件的记录）：
   **110 条有"只属于它"的身体件，31 条一件都没有** —— 后者包括 B-01（6 条）、B-24（3）、
   FS-05 两个玩家记录（3970674111 / 1490803405）、SC-30（2）以及 AF-02/AF-91/B-16/B-22/CM-09/
   DP-40/GS-17/SC-37/TR-40/TR-7/TR-9 各 1 条和 7 条无名字记录。
10. **CM-17（2509257439）与 FS-05（3970674111）在实机上完全无法区分**：两者 19 件承载件共用、
    各自独有的件全被映射成隐藏 ⇒ 可见 unit 集合相同。用户实测两边都显示"lilium 整体 + 多两只
    milfy 手臂"。要让两边各自正确，只能靠**我们人造的标记件**（下面第 3 节）。

---

## 3. 未完成的工作（按建议顺序）

### ① 同位素标记（身份）—— **正在做，尚未实现**
设计（已定，见 `docs/agent/soft-mapping-identity-markers-20261124.md`）：
- 每套已映射护甲一个确定性 id：`Murmur64A("hd2modmanager/marker/{kitId}")`（用 `StingrayHash.Murmur64A`）。
- 打给它名下"**全库独占**且**本来就映射成隐藏**"的承载件（视觉零变化；实例只出现在穿那套的身体上）。
- 现有单条隐藏件 patch（`HD2RuntimeArmorOverride/resources/wardrobe_hidden_unit.patch`，
  1 个 unit 条目 `def7dc54e86d6054`，TocData 4240 / gpu 418 / stream 0）扩成多条目：
  **原型已验证**——`PatchArchiveWriter.WriteAsync(..., additionalEntries)` +
  `PatchArchiveAdditionalEntry(AssetKey, TocData, StreamData, GpuResourceData)`，
  回归测试 `HD2ModAdaptation.Tests/MarkerHiddenUnitPatchTests`。
- 协议新增 `mark=<kit>,<markerHex>`；运行时**优先按标记认甲**（`units_by_resource(marker)` 有实例即该套），
  并**彻底移除头盔/披风锚点**（`RepairTableBuilder.BuildAnchors` 里限定 `piece.Slot >= 2`）。
- 落点：`HD2ModCore/Infrastructure/RepairTableBuilder.cs`（当前锚点逻辑所在）、
  `LuaDeploymentService`（patch 投放）、`HD2RuntimeArmorOverride/src/armor_override_runtime.lua`（认甲）。

### ② 共享承载件的 donor 规则 —— **未做**
规则：**共享件让"不可标记的一方"赢**。这样 FS-05（不可标记）天然正确、CM-17（可标记）靠身份重建，
两边都正确且不需要牺牲外观。落点：`WardrobeLinkCompiler` 的 link 生成（用户之前要求"不要重构映射"，
这次是**新增一条排序规则**，需先跟用户确认口径）。

### ③ 民主官的头 —— **已确认，并且已并进映射链路（不再走专线）**
- **位置已确认（v6/v7 实测，7 轮）**：民主官的根 = `#ID[15678e360612b805]`，x=1.470 y=13.869 z=19.570，
  84 节点、anim、全船唯一。它 3 米内 28 个 unit，17 个护甲件与根同坐标（明细见
  `soft-mapping-identity-markers-20261124.md`）。**他穿的就是 FS-05**，还带斗篷
  （`ea846ef460cc5ba0` 是 124623533 的 slot1，玩家记录没有 slot1）。
- **头 = `387f271c7f49614f`**（179 节点、anim、全船唯一实例、坐在根坐标上）——v8 实机确认：
  `hide` 只命中 1 个实例，游戏里只有他的头消失。
- **实现方式（2026-11-24 定稿）**：不再"按实例藏头"，而是 `KitNonPlayerArmors.WithOfficerHead`
  把他的头补成他那套的 **slot0 件**。于是映射民主官 = 映射普通护甲：
  来源有头 ⇒ `link=头,来源的头`（引擎自己挂上去）；来源没头 ⇒ `link=头,隐藏占位`（头消失）。
  **修补表一律不写 slot0 的行**（克隆的头按骨名挂不上，只隐藏会把新头一起藏掉）。
- 旧记录 `7862870bc9822eeb`（"他的脸"）**一个实例都没有**：v8 对 36 个候选 id 各查了一次
  `units_by_resource(IdString64)`，14 个有实例，**它不在里面**。按它隐藏从来没生效过（作废）。

### ④ NPC 宿主（`npc=` 协议）—— **已修好（标记换成根 unit）**
原来的标记是那个查不到的脸，所以 `npc=` 从来没解析成功过。现在：
- 首选 `15678e360612b805`（他的根 unit：全船唯一、**不在任何护甲记录里** ⇒ 打映射永远改不到它），
  兜底 `82595b5d94c57343`（他独有左肩）。运行时按顺序试，第一个查得到实例的赢。
- **顺带不再牺牲槽位**：以前为了让运行时还能找到他，要留一两个"只属于他"的槽位不替换；
  标记是根 unit 之后 8 个槽位全部参与整身替换（`BuildNpcRows`/`BuildNpcMarkers` 里按
  "标记是不是他那套的件"分流）。

### ⑤ 女舰长 / 维修技术员 / SEAF 士兵 —— **未开始**
它们不是护甲结构（单根 unit 承载整身 + 自带骨架）：女舰长根 unit `0xd87d04aacbc0269c`、
脸 `0x85d4ba84db7d27e5`；维修技术员根 `0x93fb8680b7ff7f96`、脸 `0x4d4017549585cb8e`；
SEAF 根 `0x75f7e96af7dcd303`、骨架 `0x7a3637cd33a54e4c`；共用 NPC 骨架 `0xb5c27e4a810df382`（69 骨，
命名与玩家同一套）。要支持得另开"根 unit 级"链路，槽位级修补接不上。

---

## 4. 已完成并提交（本地 commit）

| commit | 内容 |
|---|---|
| `6bbdcde` | 修补表最小设计：认甲锚点 + 按槽位/按 avatar 会话换件；回滚此前的映射重构实验 |
| `4234f90` | 民主官整身替换 + 运行时骨架节点数守卫（rig 只对 107 节点的玩家骨架成立） |
| `1bad2b5` | 会话失效只看认甲结果（修死锁）；NPC 加兜底定位标记；死亡重生维护 |
| `f3ce12e` | 认甲改按视觉来源分组；同槽位"多余件"也要出修补行 |
| `f2c26b0` | 修补行不得把"该显示的件"写进要藏的列表；隐藏占位不算显示 |
| （探针若干） | `armor_identity_probe.lua` v1→v6、可独占 boot 投放、`identity-probe.on` 开关 |
| （v7） | 探针 v7：定点（marker）扫描 + watchlist（0 节点也记）+ 全部 avatar 实例 + `lookup` 键形状；离线测试 |
| （v8 + NPC 标记） | 探针 v8：按实例隐藏/恢复测试（`hide=` / `show=1`）；`npc=` 标记换根 unit、头换成实测候选、不再牺牲信标槽位 |
| （民主官映射） | `WithOfficerHead`：把他的头补成他那套的 slot0 件 ⇒ 映射民主官和映射普通护甲同构（换头 / 藏头都由 `link=` 决定）；修补表不再碰 slot0；回归 `OfficerHeadMappingTests` |

**运行时代码版本**：`armor_override_runtime.lua` `RUNTIME_VERSION = '1.10'`（1.10 = 宽松隐藏不再吃掉
另一个套的 NPC 宿主身上的件）。

---

## 5. 环境与操作（本机）

- 管理器二进制：`E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows\`（Debug 运行）
- 模组库：`E:\mod`；游戏数据：`D:\SteamLibrary\steamapps\common\Helldivers 2\data`
- 协议/开关目录：`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\`
  - `wardrobe.links`（映射协议）、`identity-probe.on`（探针开关）
  - `probe-scan.txt`（探针触发文件：改内容即触发一次扫描）
    - **v8 语法**：`;` 分隔的 key=value；
      `id=<16位hex>`（锚点，默认 `82595b5d94c57343`）、`r=<米>`（默认 3.0）、
      `hide=<hex>[,<hex>]`（要藏/恢复的 id，默认 `387f271c7f49614f` 即民主官的头）、
      `show=1`（只恢复）、`nohide=1`（只扫描）、`label=<名字>`
    - 纯文本（`A`/`B`）= 用默认值跑一轮：扫描 + 藏 `387f271c7f49614f` 并写 `hide` 行
- 日志：`%LOCALAPPDATA%\CowboyBingus\Helldivers2\Logs\`
  - `ArmorOverrideRuntime.log`（运行时时序）、`ArmorIdentityProbe.log`（探针，追加写）
- 触发脚本：`E:\Data\source\repos\WpfApp1\.local-notes\probe\scan-A.bat` / `scan-B.bat`
- 构建前要 `Stop-Process -Name HD2ModManager -Force`（它会锁 `bin\`）。
- **两种模式值**：`ArmorMappingMode` / `PhysicsMode`：`0=关`、`1=独占 boot`、`2=外置加载器`。
  **独占 boot 下没有加载器** ⇒ addon 形态的 Lua mod 不会被任何东西加载；启用了任何独占模式，
  boot 就被管理器占着。当前测试是**两个都关**（用户手动关的）。
- 探针投放：`identity-probe.on` 存在时，管理器会把探针并进启动文档；boot 空着时探针**单独占一次 boot**。

---

## 6. 踩过的坑（别再踩）

1. **`World.units` 枚举 + 不判节点数读坐标 = 原生崩溃**（v1）。必须按第 2.5 条纪律。
2. **句柄跨帧**也是崩因（v1 把列表存进 `state.units`）。
3. 修补行里 **`Donors` 不能包含本槽位的 `desired`**，否则会把该显示的那件藏掉（实机："整身只剩
   两只手臂"）。
4. **隐藏占位 `def7dc54e86d6054` 不算"显示了东西"**，否则每个槽位都被判成异常。
5. **认甲锚点不能用头盔/披风**（独立装备）。之前的两个锚点正好都是头盔，是错的。
6. 会话是否成立**只看认甲结果**；用"藏过多少件还活着"判断会死锁（`maintain` 每拍都在增加 hidden）。
7. 运行时**不能重试 `World.units` + 读属性**这种大范围调用，除非按纪律分帧。
8. 管理器进程会锁 `bin\`；`dotnet build` 失败先 `Stop-Process`。
9. **`World.units_by_resource` 的键必须是 `IdString64`**（v7 实测的四种键形状）：
   `IdString64.from_hex(hex)` → 1/1/8/9 都对得上全量扫描的实例数；hex 字符串 / `#ID[hex]` / 大写 hex
   **全是 0**。以前"按资源查不到"的结论里至少有一半是被这个坑误导的。
   注意：`units_by_resource` 返回空 **≠** 该 id 没有实例 —— 判断"有没有实例"要全量扫 `World.units` + `debug_name`。
10. 探针的 pass A 列表有上限（v6 是 24，写满后把玩家自己的 avatar 挤掉了）。要"离玩家最近的锚点"
    就必须把上限放大到大于世界里的 anim 活体数。
11. **宽松隐藏会吃掉别人身上的"正确件"**（2026-11-25，民主官身体消失）：两套共用承载件时，
    实际显示的是先写的那个 donor；修补表于是给另一方补"藏掉这个 donor"的行。而隐藏半径是宽松的
    （2 米内同 id 全藏），旁边那具**另一套**的身体穿的正是这个 donor ⇒ 被一起藏掉。
    修法：`ownedByOtherKitNpc` —— 最近的 avatar 是别的套的 NPC 宿主就不藏（他由自己的会话负责）。
    同套的展台/队友/尸体仍然宽松处理。运行时 1.10。

---

## 7. 必读文档

- `docs/agent/soft-mapping-identity-markers-20261124.md`（标记方案 + 两条实测结论）
- `docs/agent/soft-mapping-repair-npc-20261123.md`（NPC 支持、民主官数据结构、女舰长等三个 NPC 的 id）
- `docs/agent/softmapping-runtime-identity-api-recon-20261114.md`（引擎 API 侦察，含"没有装备命名空间"）
- `HD2RuntimeArmorOverride/src/armor_override_runtime.lua`（运行时，含大量实机结论注释）
- `HD2RuntimeArmorOverride/src/kit_instance_hider.lua` 第 8–16 行、第 655–670 行（扫描纪律的出处）
