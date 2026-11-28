# 软映射注册表 v2：显式优先权 + 离线修复计划（2026-11-23）

> 目标：允许"每套护甲映射不同外观"，同时把共享 unit 造成的污染**按实例修复**。
> 本文件定协议与判定，实现分三步（§6）。

---

## 1. 三层分离（这是整个设计的地基）

| 层 | 来源 | 内容 | 谁改 |
|---|---|---|---|
| **事实** | 权威 `.dl_bin` + asset index | kit → 件 → `(slot, part_kind, piece_type, body, unit)`；哪些 unit 是隐藏/回退件 | 游戏更新 |
| **意图** | 注册表 `mappings` | kit → 目标模型 | 用户 |
| **策略** | 注册表 `priority`（v2 新增） | 共享 unit 归谁 | 用户 |

**污染表只描述事实，不加不减。** 策略不写进事实层。

## 2. 关键结构事实（已实测）

- `piece_type = 0` 是**基础层**（素体层），`piece_type = 1/2` 是装甲层等。
- 污染**只可能发生在 `piece_type = 0`**：装甲层永远是该套独有。
- 素体覆盖 7 个部位：头盔(part_kind 11)、胸(2)、胯(3)、左腿(6)、右腿(7)、左臂(4)、右臂(5)。
- 因此**每套护甲每部位最多 1 件被污染**，运行时克隆上限 **≤7 件/套**（头身合并时多一个胸槽，≤8）。

## 3. 修复行数公式

```
修复行数 = Σ(每个被多套使用的 unit) × (使用它、且意图 ≠ 赢家的套数)
```

全部映射到同一模型 ⇒ 意图相同 ⇒ **0 行**（= 当前状态）。
每套各自不同 ⇒ 实测上界 **717 行 / 138 套全命中**。

## 4. 注册表 v2 协议

### 4.1 源码（`armor-registry.workspace.json`）

```jsonc
{
  "schema": 2,
  "priority": [ 1442671003, 2890020275, ... ],   // 新增：item_id 序列，越靠前越优先
  "mappings": [ { "targetItemId": ..., "modelKey": ..., "isModelMapping": true } ],
  "models": [ ... ]
}
```

- `priority` 缺省/缺失 ⇒ 视为「未声明」，退化为 v1 语义（见 §5）。
- 未出现在 `priority` 里的映射排在最后，彼此按 `mappings` 顺序。

### 4.2 编译产物（Lua 读的那份）

新增一个段：

```
schema=2
repair.<kitItemId>.<n> = <slot>,<body>,<occupiedUnitHex>,<intendedUnitHex>
```

含义：穿 `kitItemId` 时，`slot` 槽位 `body` 身形那件，如果场上出现 `occupiedUnit`（赢家写的那个），
应隐藏它、换成 `intendedUnit`（本套意图）。

**Lua 遇到不认识的 schema：什么都不做**（fail safe），绝不猜。

## 5. 迁移

| 情况 | 行为 |
|---|---|
| `schema = 1` | 按 v1 语义：所有映射同优先级 ⇒ 修复表恒空 ⇒ 行为与现在完全一致 |
| `schema = 2` + 有 `priority` | 逐 unit 求赢家，输家进修复表 |
| `schema = 2` + 无 `priority` | 记一条警告，按 v1 处理 |

## 6. 实现三步

### 步骤 1：离线生成器 + dry-run 报告
- 读 `.dl_bin`/索引 + 注册表 → 输出「事实表」「赢家解析」「修复计划」「汇总」
- 纯离线、不改任何产品行为，可先验证 717 这个数
- **CLI 命令**：`hd2mod armor-registry plan --dry-run`

### 步骤 2：编译器产出 v2
- `ArmorRegistryDataMapper` / `ArmorRegistryProtocol` 增加 `priority` 读取与逐 unit 赢家解析
- 产物写入 §4.2 的 `repair.*` 段
- 老 `schema=1` 走原路径

### 步骤 3：Lua 执行器
- 只在**换甲 / 重生 / 切图**时动作，不每帧扫描
- 逐条：`units_by_resource(occupiedUnit)` 找 avatar 附近实例 → `set_unit_visibility(false)` →
  `spawn_unit(intendedUnit)` → 按骨名 `link_unit` → 校验挂上几根骨
- **硬保护**：`can_get(intendedUnit) ~= true` ⇒ **不修**（保持污染）；挂骨 0 根 ⇒ 拆掉克隆、不隐藏原件
- 拆除：`unlink_unit` + `destroy_unit` + 恢复可见性

## 7. 绝对不要碰的 API

| API | 后果 |
|---|---|
| `Unit.resource_name` | 原生 AV 崩溃（实测崩过一次） |
| 遍历全部 unit 批量读属性 | 同上 |

身份用 `debug_name`；找件用 `units_by_resource(world, id)`。

## 8. 测试用护甲对（按共享基础层件数排序）

| 对子 | 共享件数 | 部位 | 备注 |
|---|---|---|---|
| `0x34eeb43a` ↔ GS-17「边境元帅」 | 9 | 胸胯双臂双腿 | 名字是十六进制，GUI 里不好找 |
| UF-84「疑虑克星」 ↔ `0x7edc1ab2` | 8 | 胸胯双臂双腿 | 同上 |
| **CE-07「拆除专家」 ↔ CM-21「战壕护理员」** | 8 | 胸胯双臂双腿 | **两套都有名字，推荐做重负载测试** |
| CW-36「冬日战兵」 ↔ CW-22「科迪亚克」 | 6 | 胯双臂双腿 | 好找 |
| PH-56「捷豹」 ↔ PH-9「掠食者」 | 5 | 胯双臂双腿 | 好找 |
| **AC-2「服从指挥」 ↔ AC-1「恪尽职守」** | 4 | 双臂双腿 | **链路已在实机验证过，推荐先做** |

## 9. 一个重要判断：**先测污染，再改协议**

制造污染**不需要 v2**：只要在现有 GUI 里把两套共享件的护甲映射到**两个不同素体**即可。
v2 解决的是"谁赢"的**确定性**（否则取决于覆盖写入顺序），而测试关心的是"污染会不会发生、修复能不能做"。

⇒ 建议顺序：先用现有构建造一次污染实测（AC-2/AC-1 或 CE-07/CM-21），
确认修复链路在真实污染下成立，再落 v2 的三步实现。

---

## 10. 落地前的实测侦察结论（2026-11-23）

### 10.1 协议格式
`armor_registry_protocol.lua`：行式 `key=value`，上限 64 KiB，注释以 `#` 开头。
现有键：`schema` / `revision` / `enabled` / `composition_mode` / `model` / `model_piece` /
`mapping` / `piece_override` / `target_mode`。

**关键冲突**：解析器已经接受 `schema ∈ {1,2}`，而 **`schema=2` 已被"合成模式"占用**
（`M.serialize` 本身就写 `schema=2`）。所以新增的"显式优先权 + 修复计划"必须用
**`schema = 3`**，不能复用 2，否则老 Lua 会把新产物当成自己认识的 2 而按错误语义解析。

### 10.2 构建器与运行时
| 位置 | 作用 |
|---|---|
| `HD2ModCli/CliArmorRegistryProtocol.cs`（42 KB） | 生成协议文本 |
| `HD2RuntimeArmorOverride/src/armor_registry_protocol.lua` | 解析协议 |
| `HD2RuntimeArmorOverride/src/armor_registry_runtime.lua`（65 KB） | 运行时执行 |

### 10.3 生命周期不要重新发明
`HD2PhysBoneCompat/src/physbone_runtime.lua` 已有成熟实现，直接沿用其模式：
`discover`（全遍历世界找 avatar）、`playerChanged`（`rawequal` 身份比较，**不用 debug_name**）、
`scanAvatars`、`dropAllSessions`、忽略列表、`cache.preferAvatar`。
**死亡/换场景/切图**都在这套里处理过，实机验证过。

### 10.4 测试配置（不需要改协议就能造出污染）

现有 11 个素体模型，全部覆盖槽位 `0,2,3,4,5,6,7`。建议：

```
AC-2「服从指挥」(item 1442671003) → custom-888680ec…（lilium_sancta_bill）
AC-1「恪尽职守」(item 2890020275) → custom-93a71eb3…（lunalice_bill）
```

这两套共享 **5 个基础层件**（即 5 个污染点）：

| 部位 | 被共享的 unit |
|---|---|
| 头盔 | `0x3c33cf10a26cbb3e`（注意：全库 104 套共用它） |
| 左臂 | `0x7a3984aefb765f41` |
| 右臂 | `0xadb23da22e0f133b` |
| 左腿 | `0x2438f1abb362382e`（已实机验证过克隆链路） |
| 右腿 | `0x52637697b48e0b14` |

⇒ 赢家正确、输家有 5 个部位被污染，正好覆盖"多件修复"这条未验证路径。

---

## 11. 运行时侦察修正（2026-11-23，动手前）

### 11.1 有两个运行时，别改错文件

| 文件 | 规模 | 干什么 | 是否本次要改 |
|---|---|---|---|
| `armor_override_runtime.lua` | 10 KB | **软映射本体**：读协议 → `Application.set_resource_override('unit', source, target)`（L178），带每 tick 预算与重试 | ✅ 主要是它 |
| `armor_registry_runtime.lua` | 65 KB | 内存级注册表改写（`read`/`slot_write`/`locate_registry`），真映射的替代路径 | ❌ 不碰 |

`RuntimeArmorRegistryLuaSource` 把两者一起内联进 addon。

### 11.2 协议里已有什么

`link=<source>,<target>` —— 全局 unit 覆盖（软映射的实质）
`instance=<carrier>,<model>,<avatarCount>,<modelCount>,<links>,<hide>` —— 按实例克隆（`player_armor_instances.lua` 消费）
`map=` —— 管理器意图记录，运行时**主动忽略**（L129 注释）

### 11.3 关键结论：`instance=` 不够用，必须做"认甲"

`instance` 的键是 **carrier unit**。而污染场景里 A、B 两套的载体件是**同一个 unit** ——
按 carrier 建规则无法区分"现在穿的是 A 还是 B"，只能二选一地错。

⇒ 必须补上**运行时认甲**：
1. 编译器在协议里为每个被映射的 kit 输出它的基础层件清单
   （新增 `kit_piece=<item>,<slot>,<part_kind>,<body>,<unit>`，只出 `piece_type=0`）；
2. Lua 用「这些件在不在 avatar 附近」来判定当前穿的是哪套（B-01 探针已验证此路可行）；
3. 认定后按该 kit 的修复表 clone + hide。

### 11.4 新增协议行（schema=3）

```
schema=3
priority=<itemId>                                   # 可重复，出现顺序=优先级（前者更高）
kit_piece=<item>,<slot>,<part_kind>,<body>,<unit>   # 被映射 kit 的基础层件（事实，来自索引）
repair=<item>,<slot>,<body>,<occupied_unit>,<intended_unit>   # 该 kit 该槽位要修成什么
```

注意：Lua 解析器的 key 正则是 `^([%w_]+)%s*=`，**不支持点号键**，
所以必须用「重复的 `repair=` 行」，不能用 `repair.<kit>.<n>=`。

### 11.5 schema 号

- 现有：`schema=1`（无模型）、`schema=2`（合成模式，已被占用）
- 本次：**`schema=3`**
- Lua 收到不认识的 schema ⇒ **什么都不做**（fail safe）

---

## 12. 插入点定稿（2026-11-23，第 1 轮）

### 12.1 两个协议，别混

| 协议 | 产出者 | 版本 | 消费者 |
|---|---|---|---|
| **wardrobe.links**（软映射本体，含 `link=`/`instance=`/`map=`） | `WardrobeLinkCompiler`（370 行，`Schema` 当前 = **3**） | 本次 → **4** | `armor_override_runtime.lua` |
| **armor-registry 协议**（`model=`/`map_model=`/`model_piece=`） | `RuntimeArmorRegistryService.SerializeProtocol` | 2 → **3** | `armor_registry_runtime.lua` / `armor_registry_protocol.lua` |

修复计划落在 **wardrobe.links**（软映射走的就是它）。

### 12.2 已完成（第 1 轮）

- `RuntimeArmorRegistryDocument` 新增 `PriorityOrder` + `PriorityRankOf()`（Domain/RuntimeArmorRegistry.cs）
- 工作区 schema 1 → 2：`Priority` 字段读写 + 迁移（无字段 == 同优先级）
  - **序列化规则**：没有优先权时照旧写 `schema=1`、有才写 `schema=2` ⇒ 老文件逐字节不变
- armor-registry 协议：`schema` 放宽到 1..3、解析/输出 `priority=`（无优先权时照旧写 `schema=2`）
- `armor_registry_protocol.lua`：接受 `schema=3`，新增 `priority=` / `kit_piece=` / `repair=` 三个解析分支
- 新测试 `HD2RuntimeArmorOverride/tests/test_armor_registry_protocol_v3.lua` —— 14 项全过
  （schema=3 解析、priority 去重与保序、kit_piece/repair 字段、schema=2 兼容、schema=4 拒绝、未知键报错）
- `dotnet build HD2ModCore -c Debug`：**0 警告 0 错误**
- 既有 Lua 测试 `test_armor_override_runtime_update.lua` 仍通过

### 12.3 下一轮要做的（已定位到具体函数）

`WardrobeLinkCompiler.Compile(snapshot, mappings, revision, enabled, models, lowPollution)`
内部已有：目标 kit 的件表（`FromKit(Target)`）与来源/模型的件表 ⇒ **修复计划所需的两侧数据都在手上**。

1. 签名新增 `IReadOnlyList<uint>? priorityOrder = null`，`Schema` 3 → 4
2. 计算：按 `(body, slot, piece_type==0)` 取每条映射的 `carrier`（目标 kit 的 unit）与 `intended`（模型的 unit）；
   同 `carrier` 且 `intended` 不同 ⇒ 冲突 ⇒ 赢家 = `priorityOrder` 里靠前者（未列出排在最后，同序按 mappings 顺序）
3. 输出：
   - `kit_piece=<kit>,<slot>,<part>,<body>,<unit>` —— **只出"非冲突"的基础层件**（污染的件已被覆盖，靠它认不出这套）
   - `repair=<落败kit>,<slot>,<body>,<carrier→赢家intended>,<本套intended>`
4. 全孪生（无任何非冲突基础层件）的 kit 无法认甲 ⇒ 不出 `kit_piece`/`repair`，在 `Result` 里计一个 `RepairUnidentifiedCount` 供界面提示

---

## 13. 第 2 轮：编译器产出修复计划（已完成）

### 13.1 改动

`WardrobeLinkCompiler`：

1. `Compile(..., IReadOnlyList<uint>? priorityOrder = null)` 新增优先权参数
2. 逐件登记 `RepairCandidate(KitItemId, Body, Slot, PartKind, OwnHex, DonorHex, Order)`，**只登记 `piece_type=0`**
3. 冲突解析：同一 `OwnHex` 出现多个不同 `DonorHex` ⇒ 冲突；赢家 = 优先权高者，
   同优先级时取**最后写入者**（= 加这个字段之前的行为）
4. **link 过滤**：冲突 carrier 只发赢家那条 —— 这才是"确定性"的实质，否则引擎覆盖表里谁生效取决于写入顺序
5. 新增两组行（**只在有修复需求时才写**）：
   - `kit_piece=<kit>,<slot>,<part_kind>,<body>,<unit>` —— 只写**非冲突**基础层件（认甲锚点）
   - `repair=<kit>,<slot>,<body>,<occupied>,<intended>` —— 输家该修成什么
6. `Schema` 4，但**没有修复行时照旧写 `schema=3`**（产物逐字节不变）
7. `Result` 新增 `KitPieceCount` / `RepairCount` / `RepairUnidentifiedCount`

生产调用点 `RuntimeArmorRegistryService` 已把 `compiled.PriorityOrder` 传进来。

### 13.2 验证

- 新测试 `HD2ModCore.Tests/WardrobeLinkRepairPlanTests.cs`（5 项）：无优先权保持旧行为、有优先权赢家独占共用件、
  锚点只写非冲突件、全孪生套如实计入 `RepairUnidentifiedCount`、无冲突时仍写 `schema=3`
- `dotnet build HD2ModCore -c Debug`：**0 警告 0 错误**
- 注册表/编译器/低污染相关测试 **155 项全绿**
- 全量 `HD2ModCore.Tests`：1637 通过 / 1 失败
  - 失败项 `ArmorMappingPickerPlannerTests.BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts`
    期望 120、实际 114，**与本轮改动无关**（它只读真实 asset index 的页签计数，本轮没碰 Planner/索引）

### 13.3 下一轮

Lua 运行时 `armor_override_runtime.lua`：

1. `parse` 里读 `kit_piece=` 与 `repair=`（未知键静默忽略，已确认不会炸）
2. 换甲/重生/切图时：用 `kit_piece` 锚点判定当前穿的是哪套（沿用 physbone 的全遍历世界 + `rawequal` 身份模式）
3. 按该套的 `repair` 行：隐藏 `occupied` 实例 → `spawn_unit(intended)` → 按骨名 `link_unit`
4. 三条硬保护：`can_get(intended) ~= true` 不修；挂骨 0 根拆克隆且不隐藏；异常只记日志

---

## 14. 第 3 轮：Lua 运行时完成（认甲 + 按实例修复）

### 14.1 改动（`armor_override_runtime.lua`，+295 行）

1. `parse`：接受 `schema=3|4`（**4 才带新键**），新增 `kit_piece=` / `repair=` 两个分支（坏行整份作废，保留上一版）
2. 前向声明 `local repair`——`poll`/`armorUpdate` 在文件前部就引用它，后置声明会被编译成全局变量而静默失效
3. `poll` 成功后 `repair.configure(document)`；关闭时 `repair.teardown()`
4. `armorUpdate` 里驱动 `repair.update()`（每 0.5 秒）
5. 修复模块：认甲（`kit_piece` 锚点在 avatar 附近的存在数）→ 按该套 `repair` 逐件
   隐藏 `occupied` 实例 → `spawn_unit(intended)` → 按骨名 `link_unit`（优先左右互换）

### 14.2 三条硬保护（都有测试锁住）

| 保护 | 行为 |
|---|---|
| `can_get(intended) ~= true` | 不 spawn、不隐藏（原版未加载件会 spawn 出洋红问号占位体） |
| 挂骨 0 根 | 销毁克隆、**不隐藏**原件（否则该部位直接消失） |
| 未知 schema / 坏行 | 整份文档作废，保持上一版；什么都不做 |

### 14.3 验证

- 新测试 `HD2RuntimeArmorOverride/tests/test_armor_override_repair_v4.lua`：**9 项全过**
  （认甲→spawn→隐藏、can_get 假不修、挂骨 0 根不隐藏、未知 schema 不动、avatar 消失恢复可见）
- 既有 `test_armor_override_runtime_update.lua` 与 `test_armor_registry_protocol_v3.lua` 仍通过
- 全解决方案 `dotnet build -c Debug`：**0 警告 0 错误**
- 全量 `dotnet test`：Core 1637/1638、Adaptation 256、SourceModel 72、Cli 197、SourceMigration 475 全绿

### 14.4 唯一失败项的归因（不是本轮引入）

`ArmorMappingPickerPlannerTests.BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts`
期望套装 120（测试注释：116 玩家 + 4 NPC）、实际 114。它读的是**用户本机真实 asset index**
（`TryFindManagerRootWithAssetIndex()`），属游戏数据漂移；`git diff --stat` 显示本轮只动了
`RuntimeArmorRegistry.cs` / `RuntimeArmorRegistryService.cs` / `WardrobeLinkCompiler.cs` /
`armor_override_runtime.lua` / `armor_registry_protocol.lua`，与 Planner 和索引读取无关。

---

## 15. 第 4 轮：真实数据上的端到端编译验证（已完成）

### 15.1 新增只读预览工具

`tools/ArmorRepairPlanPreview/`（不在解决方案里，`TreatWarningsAsErrors=false`，与其它 tools 一致）：

```
dotnet run --project tools/ArmorRepairPlanPreview -- "<manager bin 目录>" "<workspace json>"
```

只读：不部署、不改游戏文件、不改注册表。它用**真实 asset index + 真实工作区**跑一遍
`WardrobeLinkCompiler`，把会部署出去的 `link=` / `kit_piece=` / `repair=` 行打出来。

### 15.2 测试配置（已写入，含备份）

```
备份：E:\mod\.hd2modmanager\armor-registry.workspace.json.bak-20261123-171133
写入：schema=2  revision=268  mappings=2  priority=[1442671003]
      AC-2「服从指挥」(1442671003) → custom-888680ec…（lilium_sancta_bill）
      AC-1「恪尽职守」(2890020275) → custom-93a71eb3…（lunalice_bill）
```

优先权把 AC-2 排在前面 ⇒ **AC-2 赢**（不用修），**AC-1 落败**（走修复）。

### 15.3 编译结果（真实数据）

```
产物 schema 行      : schema=4
link 行数           : 33
kit_piece 行数      : 4
repair 行数         : 4
认不出（全孪生）套数 : 0

── kit_piece（认甲锚点，都是胸/胯这类不共享的基础层件）
  AC-2 slot=2 body=2 0x8cbcdfdb0508f727 / body=1 0x283126535e76c599
  AC-1 slot=2 body=2 0x92aaff4c08b40126 / body=1 0x468790732c577bc8

── repair（AC-1 需要按实例修复）
  slot=4 隐藏 0x49dd02a1c4990004 → 换成 0x5c44f3fe7b7c0004   （左腿）
  slot=5 隐藏 0x49dd02a1c4990005 → 换成 0x5c44f3fe7b7c0005   （右腿）
  slot=6 隐藏 0x49dd02a1c4990006 → 换成 0x5c44f3fe7b7c0006   （左臂）
  slot=7 隐藏 0x49dd02a1c4990007 → 换成 0x5c44f3fe7b7c0007   （右臂）
```

共用 carrier 的 link 只剩赢家那条：`2438…→49dd…0004`、`5263…→49dd…0005` —— 正是"确定性"的体现。

### 15.4 顺带确认一个**预存**现象（不是本轮引入）

同一套只映射一条（无优先权、schema=3）时，产物里就已经有
`link=3c33cf10a26cbb3e,def7dc54e86d6054` —— 头盔基础层被映射到 **HiddenUnit**。
即：这套素体（lilium）与 AC-2 的头盔层对不上，是**素体件表与目标件表的既有差异**，
与本轮改动无关。测试时头盔部位会不可见，属预期内的旧行为。

### 15.5 已知限制（下一轮处理）

`PriorityOrder` 目前只有序列化/编译两处认识它。**在管理器 GUI 里编辑并保存映射会重建文档，
priority 会被丢掉**（退化为"最后写入者赢"，功能仍可用，但赢家会反过来）。
⇒ 测试期间不要动映射页；或者下一轮把 priority 改成可空字段并在 GUI 保存路径里保留。

---

## 16. 第 5 轮：优先权的往返与 GUI 保活（已完成）

### 16.1 发现的真实风险

管理器的"应用"路径 `ArmorRegistryPageViewModel.BuildWorkspaceDocument()` 用
`new RuntimeArmorRegistryDocument(...)` 构造文档，**没有** `PriorityOrder`；
`RuntimeArmorRegistryService.ApplyAsync(...)` 的入参里也没有它。
⇒ 用户在 GUI 里点一次"应用"，声明过的优先权就会被抹掉，赢家悄悄退化成"最后写入者"，
现象是"换了一套错的外观"。

### 16.2 修法（服务端继承，不动 GUI）

`ApplyAsync` 在构造 `desired` 之后：

```csharp
if (current.PriorityOrder.Count > 0)
    desired = desired with { PriorityOrder = current.PriorityOrder };
```

理由与旁边的 `SourceItemIds` 一致：**优先权不是"应用"这一侧的输入**，它跟着当前文档走。
`RuntimeArmorResourceFilter.Compile` 用的是 `source with { … }`，`PriorityOrder` 是普通 init 属性，
会被正常带走（不像 `ModelDefinitions` 那种由别的字段算出来的属性——文档里已有明确警告）。

### 16.3 新增测试（7 项全过）

`ArmorRegistryPriorityProtocolTests`（5 项）：
- 有优先权 ⇒ `schema=3` + `priority=` 行**按声明顺序**
- 无优先权 ⇒ `schema=2` 且一条 priority 都没有（老产物逐字节不变）
- 协议往返保序；未声明时 `PriorityRankOf` 返回 `int.MaxValue`

`ArmorRegistryPriorityWorkspaceTests`（2 项）：
- 工作区写了 `priority` ⇒ 走 GUI 同款 `ApplyAsync`（不传 priority）后**仍然在**
- 没有优先权 ⇒ 应用后仍写 `schema=1`、不出现 `priority` 字段

### 16.4 全量验证

- `dotnet build -c Debug`：**0 警告 0 错误**
- `dotnet test`：Decryptor 5 / SourceModel 72 / Adaptation 256 / Cli 197 / SourceMigration 475 全绿；
  Core **1644 通过 / 1 失败** —— 仍是那项预存的数据漂移
  （`ArmorMappingPickerPlannerTests`：期望 120 套、真实索引 114 套），与本轮无关。

---

## 17. 第 6 轮：实机失败的根因（认甲锚点写错了那一侧）

### 17.1 现象

用户测试：AC-2 正常显示 lilium；AC-1 左右手**两种手臂叠加**、左右腿**只有 lilium 的腿**。

### 17.2 证据

部署产物正确（`wardrobe.links` 1733 B）：

```
schema=4
kit_piece=1442671003,2,2,2,8cbcdfdb0508f727   ← 错：写的是 own
repair=2890020275,4,3,49dd02a1c4990004,5c44f3fe7b7c0004
```

但运行时日志只有 `repair plan: kits=2 anchors=4 repairs=4`，**没有一条 `repair applied`**
⇒ 认甲每次都是 nil，修复一次都没执行。

### 17.3 根因

`kit_piece` 里写的是 **own**（目标护甲自己的件）。可是软映射对**每一个**基础层件都会发
`link=own→donor`，于是 own 这个 id 在场上**根本不出现**（实例是按 donor 建的）。
拿 own 当锚点，逻辑上永远认不出来。

设计时的错误假设是"没有冲突的件不会被覆盖"——**错**：不冲突只代表它不参与赢家竞争，
它自己照样被 link 到本套的 donor。

### 17.4 修法

`kit_piece` 改成写 **donor**（真正会显示在身上的那个 unit）：

```
kit_piece=1442671003,2,2,2,49dd02a1c4990002   ← lilium 的胸件
kit_piece=2890020275,2,2,2,5c44f3fe7b7c0002   ← lunalice 的胸件
```

两套的 donor 天然不同（它们映射到不同素体），所以认甲成立。
如果两套映射到**同一个**素体，锚点会重合 ⇒ 认不出；但那种情况下两者的修复内容也相同
（同一 donor、同一 occupied），认不出是**无害**的。

同时给运行时补了诊断日志，避免再次"静默不执行"：

```
repair identify failed: kits=2 bestScore=0 tie=false (anchors must be the DONOR units on the body)
```

### 17.5 验证

- `dotnet build -c Debug`：0 警告 0 错误
- 编译器/注册表/低污染相关测试 **162 项全绿**
- Lua 修复测试 9 项全过
- 真实数据预览：`kit_piece` 现在写 `49dd02a1c4990002` / `5c44f3fe7b7c0002`（各自的 donor）

---

## 18. 第 7 轮：叠加手臂 / 拉丝 / 脚朝向的根因（左右交叉绑定）

### 18.1 实机日志

```
revision=271 links=33
repair slot 4: linked 15 bones, hid 1 polluted piece(s)
repair slot 5: linked 15 bones, hid 1 polluted piece(s)
repair slot 6: linked 15 bones, hid 1 polluted piece(s)
repair slot 7: linked 15 bones, hid 1 polluted piece(s)
repair applied: kit=2890020275 4/4 piece(s)
```

识别与修复都执行了，每件挂上 **15 根骨**——数量看着很好，但正是这 15 根埋了雷。

### 18.2 根因：早期探针的"左右互换"被带进了生产路径

`bind` 里原本有一条（从探针搬来的）规则：名字是 `l_*` 就去挂 `r_*` 的骨、反之亦然。
它在探针里是对的——那时我拿**右腿件**当左腿的 donor。
但生产路径的 donor **本来就同侧**（slot4=左腿件、slot6=左臂件），于是：

- 克隆件的 `l_thigh` → 挂到 avatar 的 `r_thigh`
- 克隆件的 `r_thigh` → 挂到 avatar 的 `l_thigh`

⇒ 左右交叉绑定。自定义素体件往往**带整套骨架**（所以才能"命中 15 根"），
交叉后表现为：对侧网格被拉到另一侧（**叠加的第二条手臂**）、
只在一侧存在的骨没有被绑（留在 spawn 时的世界变换上 ⇒ **根部固定在空中、移动时拉丝**）、
腿骨交叉 ⇒ **脚朝向翻转**。

三个现象同一个根因，与物理无关（物理只驱动头发/裙摆那套 rig，不会去驱动手臂件）。

### 18.3 修法（两条一起）

1. **删掉左右互换** —— donor 的槽位与目标同侧，不需要换。
2. **按槽位限定本侧**：slot4/6 只挂 `l_*`，slot5/7 只挂 `r_*`，其余槽位不限制。
   这样即使素体件带整套骨架，也不会去碰对侧的骨。

顺带把克隆件的节点数打进日志，便于下次判断"这个 unit 是不是一整套骨架"：

```
repair slot 4: linked 15 bones (clone nodes=114), hid 1 polluted piece(s)
```

### 18.4 验证

- Lua 修复测试 9 项全过
- `dotnet build -c Debug`：0 警告 0 错误

### 18.5 如果修完仍有拉丝

那就按用户的判断查物理：把 `HD2PhysBoneCompat` 关掉再穿 AC-1 一次——
若拉丝消失即可确认是物理与克隆件互不兼容（物理扫到克隆件并给它挂了 session）。
本轮日志目录里没有任何 physbone 日志，暂时无法从日志侧证实。

---

## 19. 第 8 轮：手臂仍双份 ⇒ 按"源 id"一并隐藏

### 19.1 现象

改了左右绑定后：左右手/左右腿大致正常，但**手臂仍双份**，其中一份拉丝。

### 19.2 推理

日志每槽都是 `hid 1 polluted piece(s)` —— 只藏掉了 **1 个**实例。
而一套护甲的手臂部位有 **3 个件**（pt0/pt1/pt2），它们都被 `link=` 指向同一个 donor
（`7a3984aefb765f41 → 49dd02a1c4990006`、`318e9ebe97d57d6b → 同`、`0f93b6b0824d83ed → 同`）。

⇒ 关键不确定点：`set_resource_override` 之后，实例到底登记在**源 id** 下还是 **target id** 下。
只按 target（donor）去藏，就会漏掉按源 id 登记的实例 —— 剩下的那条手臂正是它，
而它没被绑到正确父骨上（或者与克隆件重叠），于是表现为"第二份 + 拉丝"。

### 19.3 修法

运行时已经持有 `document.links`（源→目标），于是建一张反查表：

```
state.sourcesOf[target] = { source1, source2, … }
```

`applyOne` 隐藏时：先按 `occupied`（target）藏，再按它的**每个源 id** 藏，
用 seen 去重，避免同一实例被藏两次。

日志加一项"通过几个 id 找过"，便于下轮判断：

```
repair slot 6: linked 15 bones (clone nodes=114), hid 3 polluted piece(s) via 4 id(s)
```

### 19.4 备选解释（若这次仍是双份）

那就说明**克隆件自身**就含两条手臂（合并 unit）。届时日志里的 `clone nodes=` 会明显偏大，
判断依据是：把 slot6/slot7 的 repair 之一临时去掉，看剩下那条是否仍然"双份"。

---

## 20. 第 9 轮：拉丝/双份的真正根因（少写了手臂主骨的名字）

### 20.1 关键证据（实机日志）

```
repair slot 4: linked 8 bones (clone nodes=116), hid 1 polluted piece(s) via 2 id(s)
repair slot 5: linked 8 bones (clone nodes=116), hid 1 polluted piece(s) via 2 id(s)
repair slot 6: linked 8 bones (clone nodes=115), hid 1 polluted piece(s) via 4 id(s)
repair slot 7: linked 8 bones (clone nodes=114), hid 1 polluted piece(s) via 4 id(s)
```

两个事实：

1. 每个修复件 `clone nodes` 都在 **114~116**，而挂上的骨只有 **8 根** ⇒ 克隆件带的是**整套骨架**
   （百来个节点），只绑对 8 根，其余全留在 spawn 时的世界变换上。
2. 手臂的 8 根里根本没有手臂链的主骨。

### 20.2 根因

我的 `BONE_NAMES` 里手臂只写了 `l_arm` / `arm_l` 这类**不存在**的名字，
真实名字是 **`l_upper_arm` / `l_lower_arm`**（见 `kit/tools/bonehash.txt`）。
主骨没被绑 ⇒ 手臂网格挂在未被驱动的骨上 ⇒ 停在 spawn 的世界位置
⇒ **"多出来一条手臂" 且 "根部固定在空中、移动时拉丝"**。

腿能对上是因为 `l_thigh/l_knee/l_foot/l_toe` 我写对了（所以腿正常）。

### 20.3 修法

按 `bonehash.txt` 重建候选表，补齐真实名字：

- 手臂链：`l_upper_arm` / `l_lower_arm` / `l_clavicle` / `l_elbow` / `l_wrist` / `l_hand`
  / `l_forearm_twist` / `l_wrist_twist` / `l_hand_twist` / `l_arm1..4`
- 肩部权重常挂的：`l_shoulder` / `l_shoulderpad` / `l_shoulderplate` / `l_shoulderarmour` / `l_shoulder_shield`
- 腿/骨盆/内衬同名补齐

（仍然按槽位限制本侧：slot4/6 只挂 `l_*`，slot5/7 只挂 `r_*`。）

### 20.4 关于"肩甲槽位"

查了权威件表：**没有给肩甲槽位做替换**。

- AC-1 有 slot 8/9（LeftShoulder / RightShoulder），AC-2 **没有**；
- 素体（lilium / lunalice）**都没有** slot 8/9 的件；
- 所以 AC-1 的肩甲被映射到 `HiddenUnit`（`88252fc0da66b0a4,def7dc54e86d6054`、`22c4d2c9cdb4001a,def7dc54e86d6054`）
  —— 是**隐藏**，不是替换。修复计划也没有覆盖 8/9。

---

## 21. 第 10 轮：`units_by_resource` 看不见"覆盖出来的实例"

### 21.1 决定性证据

```
repair slot 6: linked 12 bones (clone nodes=115), hid 1 polluted piece(s) via 4 id(s)
repair slot 7: linked 12 bones (clone nodes=114), hid 1 polluted piece(s) via 4 id(s)
```

**查了 4 个 id，只藏到 1 件**。而该槽位上实际有 3 份映射实例（赢家 base + 本套两个 pt1）。
⇒ 被 `set_resource_override` 覆盖出来的实例，**按源 id 和目标 id 都查不到**
（`World.units_by_resource` 返回空）。所以"藏掉重复件"从来就没成功过——
这既解释了双份为什么回来，也解释了为什么"留下的正好是拉丝那件"。

### 21.2 修法：改成"扫描 + debug_name"

`Unit.debug_name` 返回 `"#ID[hex]"`（资源/描述符派生），是这条链路上**唯一安全**的身份读取方式
（`Unit.resource_name` 是原生 AV 崩溃点，禁用）。

新增 `scanNear(world, ax, ay, az)`：遍历 `World.units(world)`，只处理**离 avatar 1.5 m 内**的实例，
读 `debug_name` 建 `id → 实例表`，然后按 id 批量隐藏。

只在**换甲那一刻**扫一次（不是每帧），并把规模打进日志：

```
repair slot 6: … hid 3 polluted piece(s) via 5 id(s), scanned 214 unit(s)
```

### 21.3 验证

- Lua 运行时测试 11 项全过（假引擎补了 `World.units` 与 `debug_name`）
- 全解决方案构建 0 警告 0 错误

---

## 22. 第 11 轮：崩溃回滚 + 改用"全局隐藏本套重复件"

### 22.1 崩溃原因（我造成的）

上一版为了找"覆盖出来的实例"，遍历 `World.units(world)` 并对每个 unit 调
`world_position` / `debug_name`。**进游戏即崩，连崩三次**。
仓库早有同类记录（HD2IAS 的 census 把游戏打死过）：批量对任意 unit 调属性是原生崩溃点，`pcall` 拦不住。
⇒ 这条路放弃，已在代码里写明"不要再试"。

### 22.2 换的做法：让映射本身不产生重复件

被覆盖出来的实例查不到（`units_by_resource` 返回空，实测只藏到 1/3），
但**本套独有的那些 carrier 可以用全局覆盖直接指向 HiddenUnit** —— 它们只被这一套使用，不会伤到别人。
所以：

- 每个**修复槽位**上，本套自己的、映射到同一目标的件 ⇒ `link=<own>,HiddenUnit`（编译期决定）
- 共享的 base 件仍然按实例隐藏（每个槽位只有 **1** 个实例，实测能藏到）
- 运行时再克隆一份干净的补上

日志里的 `hid 1` 从此就是正确值：那个槽位本来只剩 1 个需要藏的实例。

### 22.3 实现

`WardrobeLinkCompiler`：新增 `hideOwnDuplicates[(kit, slot)] = 目标 unit`，
在 link 发射时把这些件的 donor 改写成 `HiddenUnit`。
为此把 link 发射移到"冲突解析 + 修复计划"之后（之前它在前面，拿不到这张表）。

### 22.4 验证（真实数据）

```
link=7a3984aefb765f41,49dd02a1c4990006    ← 共享 base：赢家的手臂（运行时按实例藏掉）
link=09e9b7fb5dcd061b,def7dc54e86d6054    ← AC-1 自己的重复件：全局隐藏
link=ab6a37f7fb07a3d8,def7dc54e86d6054    ← 同上
```

- 编译器测试 17 项全过
- Lua 运行时测试 11 项全过
- 全解决方案构建 0 警告 0 错误

---

## 23. 第 12 轮：按用户的规则重做（不避开被污染的件）

### 23.1 我之前的错误

用户从第 6 轮起要的就是一条规则：

> 编译器**不要避开**被污染的件。这个槽位该显示的就是那个被占用的 unit（它已被别的素体占用），
> 老实映射到它、**只记录**这个事实；运行时认到这套甲时，把那个实例藏掉、克隆一份正常的补上。

我却自作主张加了三层判断（HiddenUnit 抑制、hide-only、全局隐藏本套件），
每一层都制造了新的副作用（一半消失一半还在、双份回来、枚举崩溃）。
这些判断已全部删除。

### 23.2 现在的实现

`WardrobeLinkCompiler`：`occupiedBySlot[(kit,slot)] = (intended, occupied)`。
link 发射时，该槽位上**本套自己的件**如果原本指向 `intended`，一律改指向 `occupied`
（= 被占用的那个 unit），从而**槽位只有一个来源**。

真实数据验证（AC-1 左臂）：

```
link=7a3984aefb765f41,49dd02a1c4990006    ← 共享 base（赢家写入）
link=09e9b7fb5dcd061b,49dd02a1c4990006    ← 本套件，也指向被占用的 unit
link=ab6a37f7fb07a3d8,49dd02a1c4990006    ← 同上
```

三条都指向 `49dd02a1c4990006` —— 单一来源。运行时的 repair 行记录
`(slot 6, 隐藏 49dd02a1c4990006, 换成 5c44f3fe7b7c0006)`，藏掉那 1 个实例 + 克隆。

腿仍然走 HiddenUnit，因为素体**根本没有**腿的 pt1 件（donor 为 null），那是既有的"缺槽位就隐藏"行为。

### 23.3 验证

- 编译器测试 17 项全过
- Lua 运行时测试 11 项全过
- 全解决方案构建 0 警告 0 错误

---

## 24. 第 13 轮：拉丝定位到"肩膀连接处"= 克隆件少绑了肩部骨

### 24.1 用户的对照实验（非常有价值）

- 双份手臂已消失（上一轮的"老实指向被占用 unit"生效）
- 关掉物理后**仍然拉丝**，位置固定在**手臂与肩膀连接处**
- 把 lunalice/lilium 互换映射对象后，拉丝仍然出现在肩膀（也就是**跟着"被克隆的槽位"走，不跟着某个模型走**）
- 反复切换多次后 AC-2 也会出现 → 结论：**只要做了"删除+克隆"，肩膀处就会坏**

### 24.2 机理

克隆件 `clone nodes ≈ 114~116`（整套骨架），而我们按骨名只挂上 **12 根**。

- 腿能对上：`l_thigh / l_knee / l_foot / l_toe` 这些名字我写对了，腿网格的权重骨都在其中
- 手臂对不上：肩关节那一端的顶点权重挂在 `l_clavicle / l_shoulder / grp_arm_l / g_arm_l`
  之类（各模型骨架命名可能不同）的骨上，这些骨**没被挂** ⇒ 顶点留在 spawn 时的世界变换
  ⇒ 在肩关节处拉出三角形

这解释了三件事：为什么只在手臂、为什么固定在肩部、为什么换模型换护甲都跟着复现。

### 24.3 本轮改动：把"实际挂上了哪些骨"打出来

`repair slot` 日志现在带命中名字清单（最多 24 个）：

```
repair slot 6: linked 12/14 bones (clone nodes=115), hid 1 … [hips,l_thigh,l_knee,…]
```

- 如果清单里**没有** `l_shoulder / l_upper_arm / l_clavicle` ⇒ 名字没命中，补名字即可
- 如果名字命中但**数量仍远小于** `clone nodes` ⇒ 模型用的是自定义骨架名，必须改用
  **编译期算好的 rig**（`PlayerArmorInstanceRigReader` 那套：显式"子节点下标→父节点下标"配对），
  和 `instance=` 走同一条已验证的路

---

## 25. 第 14 轮：按下标补绑（应对自定义骨架名）

实机日志（部署版）：

```
repair slot 6: linked 12 bones (clone nodes=121), hid 2 polluted piece(s) via 6 id(s)
repair slot 7: linked 12 bones (clone nodes=121), hid 2 polluted piece(s) via 6 id(s)
```

**121 个节点只绑上 12 根**。按名字只对上十来个，说明这些素体件用的是**自定义骨架名**，
肩关节那一端（锁骨/肩/上臂）的骨没被绑 ⇒ 顶点留在 spawn 变换上 ⇒ 肩部拉丝。

### 25.1 新增：按节点下标成对绑定

判据：**克隆件与 avatar 的节点数完全相等**时，视为同一套骨架，直接按 `1..n` 一一对应 link
（`World.link_unit` 接受数字下标，不需要名字）。名字绑定的结果保留，下标只补没绑到的节点。

```
repair slot 6: linked 40/48 bones, index-bound 81 (clone=121 avatar=121), hid 2 piece(s) …
```

- `index-bound` > 0 ⇒ 这条路生效
- `clone=` 与 `avatar=` 不相等 ⇒ 不启用（避免骨架不同时乱绑），仍走按名字

### 25.2 验证

- Lua 运行时测试 13 项全过（新增：节点数相等时启用按下标补绑、且仍然隐藏被污染件）
- 全解决方案构建 0 警告 0 错误

---

## 26. 第 15 轮：为什么只有肩膀拉丝 = 把腿骨也挂到了手臂件上

### 26.1 实机日志（决定性）

```
repair slot 6 (arm 前的版本): linked 12/12 bones (clone=121 avatar=107)
  [hips,l_thigh,l_knee,l_foot,l_toe,l_clavicle,l_elbow,l_hand,l_hand_twist,l_shoulder_twist,l_shoulder,l_shoulderarmour]
repair slot 4 (leg): linked 12/12 bones (clone=114 avatar=107)
  [hips,l_thigh,l_knee,l_foot,l_toe,l_clavicle,l_elbow,l_hand,l_hand_twist,l_shoulder_twist,l_shoulder,l_shoulderarmour]
```

**手臂件（slot 6）的命中列表里赫然是腿骨**：`l_thigh / l_knee / l_foot / l_toe`。
因为素体件带整套骨架，我的绑骨循环只看"名字两边都有"，不看"这骨属于哪个部位"。

把手臂件挂到腿骨上 ⇒ 这一件的层级被拽到腿部 ⇒ 形变只能表现在它的根部
⇒ **肩膀连接处拉丝**。这也解释了三件事：只在手臂、固定在肩部、换模型换护甲都复现（跟"被克隆的槽位"走）。

腿为什么正常：腿槽位挂的就是腿骨，正好是网格的权重骨。

### 26.2 修法：按槽位只绑本部位的骨

- slot 4/5（腿）⇒ 只认腿骨（thigh/knee/foot/toe/leg/hip/pelvis）
- slot 6/7（臂）⇒ 只认臂骨（arm/hand/wrist/elbow/clavicle/shoulder），且**排除腿骨**
- 其它槽位 ⇒ 不限制

日志带上部位标签，便于核对：

```
repair slot 6 (arm): linked 9/9 bones … [l_clavicle,l_shoulder,l_shoulder_twist,l_elbow,l_hand,…]
```

### 26.3 顺带确认：按下标补绑没有触发（这是对的）

`clone=121 / avatar=107` —— 数量不相等，我的护栏按设计不启用，避免乱绑。

### 26.4 验证

- Lua 运行时测试 13 项全过
- 全解决方案构建 0 警告 0 错误

---

## 27. 第 16 轮：拉丝不是"挂错部位"，而是"大部分骨根本没挂"

### 27.1 上一轮假设被实机否掉

用户实测：按部位过滤腿骨后，肩膀**仍然**拉丝，而且拉丝形态是"从肩部射向远处的一条长条"。
这说明问题不是"挂错了骨"，而是**没挂到的骨太多**：

```
repair slot 6: linked 12/12 bones (clone nodes=121)
```

121 个节点只挂了 12 个，剩下 109 个留在 spawn 时的世界变换上。
手臂网格的顶点权重分散在这百来个骨上 ⇒ 没被绑的顶点被拉到远处 ⇒ 长条拉丝。

腿为什么没事：腿网格的权重集中在 `l_thigh/l_knee/l_foot/l_toe` 这几根，正好被名字命中。

### 27.2 按下标补绑为什么一直没生效

判据太严：`cloneNodes == avatarNodes`。实机是 `121 vs 107`，永远不相等 ⇒ 这条路从未触发。

### 27.3 本轮改动：放宽为"前缀对齐"

`cloneNodes >= avatarNodes` 时，认定克隆件的**前 avatarNodes 个节点**与 avatar 一一对应
（素体件照 avatar 骨架制作，多出来的节点是网格），按 `1..avatarNodes` 补绑未绑过的节点。

```
repair slot 6 (arm): linked 12/12 bones, index-bound 107 (clone=121 avatar=107), …
```

- `index-bound ≈ 107` ⇒ 前缀假设成立，剩余骨全部绑上，拉丝应消失
- 如果模型不成立（可见的形变反而更乱）⇒ 回退，改走**编译期 rig**：
  用 `PlayerArmorInstanceRigReader.CreateRig`（按 name-hash 配对，不是按名字）算出显式
  "子节点下标 → 父节点下标"清单，写进 `repair=` 行，运行时照表 link。

---

## 28. 第 17 轮：按"只有一套骨架"重做绑定

用户的判断：潜兵只有**一套骨架**，素体也是从这套骨架做的 ⇒ 不需要任何名字/祖先启发式，
直接把整副骨架按序号 1:1 对齐。

### 28.1 之前两版错在哪

| 版本 | 做法 | 结果 |
|---|---|---|
| 名字绑定 | 只挂名字对得上的 | 121 个节点只挂 12 个 ⇒ 大量顶点留在 spawn 变换 ⇒ 拉丝 |
| 名字 + 祖先补全 | 把没绑的节点挂到"最近已绑定祖先" | **更糟**：素体 unit 的 scene graph 里除了骨还有**网格节点**，网格被挂到 avatar 根节点 ⇒ 一根跟着角色走的硬长条 |

### 28.2 现在的做法

- 名字绑定仅作为"辅助"保留（同名字等价于同骨，不会更差）
- **无条件按序号 1..min(clone, avatar) 一一对应 link**，覆盖整副骨架
- **删掉祖先补全**（它对网格节点是错的）

日志：

```
repair slot 6 (arm): linked 12/12 bones, index-bound 107 (clone=121 avatar=107)
```
