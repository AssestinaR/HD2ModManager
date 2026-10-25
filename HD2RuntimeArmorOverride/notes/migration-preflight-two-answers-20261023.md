# 迁移前两问的实测结论

日期：2026-10-23
目的：回答切换 `set_resource_override` 链路前必须确认的两件事。

## 问题 2：`piece_override` 与 `tone` 在现有配置里用不用

**答案：完全不用。**

现网 `%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\armor-registry.protocol`
（7544 字节，2026-10-20 22:29，revision=124）逐行统计：

| 键 | 行数 |
|---|---|
| `schema` / `revision` / `enabled` | 各 1 |
| `map` | **297** |
| `model` | 0 |
| `model_piece` | 0 |
| `map_model` | 0 |
| `piece_override` | 0 |
| `target_mode` | 0 |
| `composition_mode` | 0 |

也就是说，用户实际使用的全部配置就是 297 条 `map=目标护甲,源护甲`，源只有两个：
`820333399`（RE-2310 Honorary Guard 护甲套）与 `1745464610`（RE-2310 头盔套）。

`wardrobe.masks` 同样极小（153 字节，3 行）：1 个 target、2 条 `off`
（`af431352d9b95e83` 的第 0、1 段）。

**结论**：迁移到 unit 覆盖链路**不会丢失任何在用能力**。`full` 模式、虚拟模型基座、
按件开关、tone 全都是"实现了但没人用"的能力，可以安全退役或留到下一阶段。

## 问题 1：权威库能否为每个 item 导出完整件表

分三层看，结论是**源头可用、派生件表不可用**。

### 1.1 注册文件（源头）：覆盖完整

用 `GameDataAuthorityProvider` 直接读游戏数据目录（`tmp/WardrobeKitProbe`）：

```
snapshot: source=DecryptedDataLibrary kits=422 issues=2
wanted=299 present=299 missing=0
pieces=3404 usable=1965 (58%)
kits with zero usable pieces=0
kit kinds: Armor=138, Helmet=161
```

299 个在用 kit **全部存在**，没有一个 kit 是"零可用件"。

### 1.2 但件记录本身有大量脏行

同一次输出里，RE-2310 护甲套（820333399，23 条）只有 14 条能解析成 unit，
且夹杂明显的错读：

```
body=3 slot=1  Accessory  weight=1        unit=3c33cf10a26cbb3e   ← 真件
body=3 slot=0  Head       weight=1425437231 unit=ffffffffffffffff  ← 脏
body=3 slot=0  Head       weight=0        unit=0000000000000000   ← 脏
body=3 slot=3001656025 Unknown piece_type=2721816194 unit=…0001   ← 脏（槽位越界）
body=0 slot=3  Pelvis     weight=1        unit=45d79d84b8933a7f   ← 真件
body=0 slot=9  RightShoulder weight=1     unit=3a91f5a56da3201e   ← 真件
body=1 slot=6  LeftArm    weight=1        unit=91eb7be84016ceab   ← 真件
body=1 slot=8  LeftShoulder weight=1      unit=144d95c1500b73bc   ← 真件
```

特征很清楚：**真件 weight=1 且槽位在 0..9；脏行 weight 是天文数字、槽位越界
（甚至出现字符串 `11-12883266`）、unit 是 0 / 1 / 0xFFFF**。这是记录步长读错的典型表现。

### 1.3 SQLite 派生表：更坏，且是这次更新引入的回归

`asset-index.sqlite` 里的 `game_data_authority_pieces`（在用的 299 个 kit）：

| 指标 | Debug（新构建，2026-10-21） | Release（旧构建，2026-09-22） |
|---|---:|---:|
| 在用 kit 是否都在 | 299 / 299 | 287 / 299（缺 12） |
| 件行数 | 2875 | 3264 |
| 槽位在 0..9 | 2500（87%） | 3264（100%） |
| `unit_file_id` 为 0/-1 | **1416（49%）** | **0** |
| `weight` 为 0/-1 | 972（34%） | 731（22%） |
| 三项都干净 | **676（24%）** | 2533（78%） |
| 零可用件的 kit | 34 | 42 |

旧库结构干净（槽位 100% 在范围内、unit id 一个不缺），新库丢掉了一半的 unit id。
**这是 2026-10-20 游戏更新引入的解析回归**，不是表设计问题。

`ArmourUnitTableSource.cs` 早就写过这条：那张表是"注册文件的派生缓存，实测会丢行"，
并且额外警告：**件表的 slot 编号与 unit 自身语义在 5/6/7 档不一致，两套编号不是一回事**。

## 结论与推荐路径

**不要在件表上做映射。** 正确路径管理器已经具备：

1. **kit → unit 键**：`ArmourUnitTableSource.ReadUnitIds(snapshot, itemIds)`
   —— 从注册文件取，只取 `UnitTypeId == 0xe0a48d0be9a7453f` 的 unit 资产，
   并正确处理组合护甲（要按成员逐个取）。
2. **unit → (body, slot, piece_type)**：读每个 unit 自己的 `CustomizationInfo`
   （`UnitMeshReader.ReadCustomizationInfo`，字符串形态：`Slim` / `Torso` / `Any` /
   `Undergarment`）。`HD2ModCli` 已经在用：`CliApplication.cs:2337`、
   `CliUnitInspection.cs:238-241`。
3. 两者拼起来才是可信件表，且**完全不依赖那张坏掉的派生表**。

代价：导出时要为每件护甲打开 N 个 unit 资源（22 件就是 22 次读取）。离线做、带缓存即可。

## 仍待确认的一点

`re824-to-re2310-mapping.csv` 里的 22 个哈希（`c9904983657e9fc8`、`cae35ee15168145f` …）
**不在**基础游戏这 299 个 kit 的可解析件里。说明那套映射的来源是**模组自带的注册文件**
（用户 mod 里的 RE-824 / RE-2310），不是基础游戏数据。

所以导出器必须能对"已安装模组的注册文件"跑同一套流程，而不只是基础游戏。
这一点要先确认 `GameDataAuthorityProvider` 读取的 `data` 目录是否已包含已部署的
patch 资源；若不含，就需要一条针对模组 patch 的读取路径。

## 续查（同日，已回答上面这条）

### A. 信息中心已经存着 mod 的完整 unit 列表

`data/indexes/mod-facts.sqlite` 是信息中心的缓存，`mod_assets` 按 `type_id` 统计：

| type_id | 数量 | 是什么 |
|---|---:|---|
| `504b55235d21440e` | 4895 | 纹理/贴图类 |
| **`e0a48d0be9a7453f`** | **2463** | **unit（与 `PatchUnitMeshReader.UnitTypeId` 一致）** |
| `cd4238c6a0c69e32` | 1276 | 材质类 |

拿 `re824-to-re2310-mapping.csv` 的 44 个哈希去查：

```
distinct hashes in CSV: 44
present in mod_assets(unit): 44 / 44
```

**全部命中。** 导入 mod 时信息中心已经把 unit 列表按 `node_id`（mod）+ `group_id`（patch）
存好了，正是"导入期就生成的 unit 列表"。

`asset_references` 里也有 `slot_id`，但实测是 1772995352 / 2838600006 这类大数，
**不是护甲槽位**，不能当槽位用（与 `ArmourUnitTableSource` 的警告一致）。

### B. item → unit 的关联：存在，但被解析缺陷吃掉大半

在权威快照里按全部 44 个哈希搜索（含被判为脏的件）：

```
45d79d84b8933a7f -> item=820333399 (Armor, RE-2310 Honorary Guard) slot=3 Pelvis  weight=1
3a91f5a56da3201e -> item=820333399 (Armor, RE-2310 Honorary Guard) slot=9 RShoulder weight=1
91eb7be84016ceab -> item=820333399 (Armor, RE-2310 Honorary Guard) slot=6 LeftArm  weight=1
144d95c1500b73bc -> item=820333399 (Armor, RE-2310 Honorary Guard) slot=8 LShoulder weight=1
62159622b3ed9438 -> item=4259903063 (Armor, RE-824 Bearer of the Standard) slot=2 Torso weight=2
107564155e2f08e2 -> item=4259903063 (Armor, RE-824 Bearer of the Standard) slot=8 LShoulder weight=2
d79fa7882bb836e6 -> item=4259903063 (Armor, RE-824 Bearer of the Standard) slot=3 Pelvis weight=2
fba2839df9dee3fe -> item=4259903063 (Armor, RE-824 Bearer of the Standard) slot=8 LShoulder weight=2
hits=8; hashes absent from snapshot: 36
```

两个重要事实：

1. **RE-824 与 RE-2310 都是基础游戏条目**：RE-824 = `4259903063`，
   RE-2310 = `820333399`。所以"模组自带注册文件"这个担心不成立——条目本身在基础数据里，
   mod 只是替换了它们的 unit。
2. **item → unit 的关联存在且正确**（上面 8 条槽位/部位/重量全部自洽），
   但 44 个里只解析出 8 个，**36 个被件解析丢掉**。

### C. 结论：唯一的阻塞项是件解析

不是数据缺失，不是模组不可见，而是**注册文件里 piece 记录的解析在这版游戏上错了**：
行被丢弃、字段错位（槽位越界、weight 天文数字、unit 为 0/1/0xFFFF）。

可用的交叉验证材料已经齐了：

- 信息中心 `mod_assets`：mod 的**完整** unit 列表（44/44）。
- 每个 unit 自己的 `CustomizationInfo`：`BodyType / Slot / PieceType` 字符串
  （`Slim`/`Torso`/`Any`/`Undergarment`），由 `UnitMeshReader.ReadCustomizationInfo` 读取。
- 旧构建（Release）的 `asset-index.sqlite`：结构干净的黄金样本
  （槽位 100% 在范围、unit id 一个不缺），可用于对比修复后的解析结果。

### D. 步骤 1 已完成：piece 步长 96 → 112

**根因**：`HD2GameItemAssets/CustomizationArmorSetsReader.cs` 里
`PieceSize = 96`，而 2026-10-20 的更新把 piece 记录加宽到 **112 字节**——
字段偏移不变，多出的 16 字节追加在尾部。步长仍是 96 时，每行第一件读对，
其余每件早 16 字节，于是出现槽位越界、weight 天文数字、unit 落到 0/1/0xFFFF。

**证据（从解密后的原始 payload 直接量得）**：

- 两个 kit 的 payload 都是 2712 字节 = `64 + 3×24 + 23×112`；按 96 除不尽。
- 逐行 delta 精确吻合：`784 = 7×112`、`896 = 8×112`，且
  `pieceOffset + pieceCount×112` 正好等于 payload 末尾（2712）；按 96 只有 2584。
- 用 112 解析：两个 kit 各 23 件、零个不合理字段（slot≤9、piece_type≤4、weight≤16），
  CSV 的 44 个哈希 **44/44 命中**。

**改动**：

- `PieceSize = 96 → 112`，并写明来由。
- `KitSize / BodySize / PieceSize` 改为 `public const`，测试夹具改用同一组常量
  —— 这次回归的根源就是"每个消费者各存一份 96"。
- `HD2ModCore.Tests/GameDataAuthorityProviderTests.cs` 的合成写入器同步。

**修复前后（走真实 provider 路径，299 个在用 kit）**：

| 指标 | 修复前 | 修复后 |
|---|---:|---:|
| 可解析件 | 1965 / 3404（58%） | **3404 / 3404（100%）** |
| 槽位分布 | 含 3001656025 等越界值 | 全部 0..9 |
| CSV 44 哈希命中 | 8 | **44** |
| 缺失哈希 | 36 | **0** |

RE-824（`4259903063`）的 weight=2、RE-2310（`820333399`）的 weight=1，
部位名解析为 Torso/Pelvis/LeftArm/RightArm/LeftShoulder/RightShoulder/LeftLeg/RightLeg/Accessory/Head，
与 CSV 的语义标签完全一致。

**必须做的一步**：`asset-index.sqlite` 里的 `game_data_authority_*` 仍是 2026-10-21
用旧步长构建的坏数据。修复只影响新解析，**必须重建一次资产索引**，那张表才会刷新。
在那之前，读表得到的仍是 24% 可用的旧结果。

### F. 步骤 2、3 结果

**步骤 2：索引已重建**（`gamedata index plan rebuild → validate → apply --confirm`）。
`baseIndexCurrent: true`，`game_data_authority_pieces` 从 24% 可用变为
**3404 行全部槽位在 0..9、unit id 一个不缺**，与源头的 3404 件完全一致。
（旧库里 22% 的 `weight=0` 是正常值，不是脏数据——新旧库比例一致。）

**步骤 3：导出器可用**（`tmp/WardrobeKitProbe` 的 `links` 模式）。
按旧实现 `existing_only` 的算法（源按 `(body, slot, piece_type)` 分组、目标逐件消费、
`body=3` Any 兜底、一件只消费一次）把现网 297 条 `map` 展开成：

```
mappings=297  links=1777  unresolved=0
target pieces without a unit=0
target pieces without a source match=337
self-links=1
```

**与 CSV 的 22 对基准对照：20/22 复现。** 两处不一致的原因是匹配规则不同，不是解析错误：

| CSV 行 | RE-824 源件 | CSV 目标 | 实际槽位 | 严格 join 结果 |
|---|---|---|---|---|
| `d79fa7882bb836e6,3692b4d3a0b8d5d3,crotch_armor_slim` | slot=3 pt=0 | slot=2 pt=2 | 跨槽位（裆→躯干） | 无匹配 |
| `dcf96504e28aca18,914e7539788418fc,crotch_armor_stocky` | slot=3 pt=0 | slot=2 pt=2 | 跨槽位 | 无匹配 |

RE-2310 的 `slot=3` 只有 `piece_type=1`，没有 `pt=0`；CSV 用"语义近似"跨到了躯干层。
而旧运行时 `existing_only` 是严格 join —— 也就是说**这两行即便在旧实现里也不会被应用**。

### G. 待决策：匹配规则

| 规则 | 覆盖 | 风险 |
|---|---|---|
| 严格 `(body, slot, piece_type)`，Any 兜底 | 1777 条，337 件保留原外观 | 无跨部位错配；覆盖面小 |
| 语义近似（CSV 那种） | 覆盖更高 | 可能把裆部件换成躯干层，穿模 |

建议默认走严格 join，把"未匹配的 337 件"作为状态暴露给 UI，由用户决定是否放宽。
这也符合"安全第一"：宁可少换，不可换错。

### H. 端到端试跑结果与"遗留件"分析

**试跑（probe 21）**：1776 条 link 全部注册成功（0 失败，1 条自链接跳过），
实测大多数护甲正确显示为 RE-2310 外观。

**现象**：不少被映射的护甲仍有部分 unit 照原样显示。

**原因（三条，按贡献排序）**：

1. **严格 join 的必然结果**。337 个目标件在源 kit 里找不到同
   `(body, slot, piece_type)` 的对应件，于是保留自己的 unit，继续渲染。
   分布高度集中：

   | (slot, piece_type) | 数量 | 占比 |
   |---|---:|---:|
   | slot=3 pt=0（裆内层） | 244 | 72% |
   | slot=3 pt=2（裆第三层） | 60 | 18% |
   | 其余 pt=2 各槽 | 33 | 10% |

   而 RE-2310 的 `slot=3` **只有 pt=1**，没有 pt=0 / pt=2。
   这 337 件分布在 **131 套护甲**上（每套 1–6 件）。

2. **覆盖链路没有"隐藏"原语**。它只能把 A 换成 B，不能"不加载"。
   旧链路正是靠 `piece_override enabled=false` 与 section 掩码把这些内层藏掉的。

3. **一件只消费一次**。同一 `(body, slot, pt)` 组里目标件多于源件时，多出来的保留原样。

**能否用运行时隐藏来补（安全性已量化）**：

337 个遗留件对应 **211 个不同 unit**。按"这些 unit 还被哪些 kit 引用"检查：

| 情况 | 数量 |
|---|---:|
| 只被**已映射**护甲引用（隐藏安全） | **210** |
| 被**未映射**护甲引用（隐藏会误伤） | **1** |

唯一的例外是 `ea846ef460cc5ba0`（披风，41 个 kit 引用，其中 **5 个未映射**）——
全局隐藏它会让那 5 套未映射护甲丢披风。

**结论**：运行时隐藏可行且边界清晰——隐藏 210 个 unit，单独排除 `ea84`。
代价是映射护甲上那 2 个披风遗留件仍可见，但不会误伤未映射护甲。这符合"安全第一"。

### J. 最终方案：满槽位来源 + 常驻轮询运行时（已验证）

**遗留件的根本解法**：不是逐个隐藏，而是把**来源补齐到满槽位**——统计所有目标护甲在
每个 `(body, slot, layer)` 上最多需要几件，源 kit 缺的位置用隐藏 unit 填满。
这样映射本身没有空洞，遗留件从构造上消失。

```
distinct slots required by targets=46
links=1988  unresolved=0
target pieces without a source match=0
hidden-unit padding=1035（实际被消费 217 处）
```

（填充按全局最大需求做，单个护甲用不满的那些不会产生 link，所以 1035 → 217。）

**隐藏 unit 的取值**：`ffffffffffffffff`。实测：
- `can_get('unit', 全1)` = false，但覆盖照样注册成功；
- 引擎加载失败时**安静跳过**，不渲染、不崩溃；
- 打开军械库无异常，游戏正常运行。

因此**不需要**在 patch 里内置空 unit——那个兜底方案保留但未启用。

**常驻运行时**（`tmp/probe/runtime.lua`）：
- 轮询 `%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\wardrobe.links`，1 秒一次；
- 整文件读取并按文本比较，避免把半写入的文件当成新版本；
- 解析失败**保留上一版**，不会半应用；
- 按 diff 增量应用：新增 `set_resource_override`、消失 `clear_resource_override`；
- `enabled=false` 撤销全部。

**实测**：

| 事件 | 日志 |
|---|---|
| 首次加载 | `revision=1 links=1988 added=1988 removed=0 total=1988` |
| 改为 `enabled=false` | `cleared 1988 overrides (enabled=false)`（热生效，未重启） |
| 游戏退出 | 无崩溃转储、Windows 事件日志无应用程序错误 → 正常退出 |

**结论**：改协议文件 → 1 秒内自动应用 → 开一次军械库即可见。**不需要重启游戏。**
唯一的固有约束是视觉刷新（覆盖只影响之后创建的实例）。

用户实测确认："本次的结果简直完美。"

### L. 撤销为什么不可行（2026-10-23 实测三次否证）

应用/修改映射是安全的，**撤销不是**。三次尝试全部失败，而且失败形态一致：

| 尝试 | 结果 |
|---|---|
| ① 一次性 `clear_resource_override` × 1988 | 撤销瞬间进程死亡 |
| ② 按帧节流（8/帧）后撤销 | 撤销本身走完、游戏存活，**打开军械库时全部空白 → 立刻崩溃** |
| ③ 改为身份覆盖 `src→src`（不释放表项） | 大幅改善：多数护甲正常还原，**但身上那套变透明**，2–3 秒卡顿 → 恢复 → 崩溃 |
| ④ 惰性还原（只还原"无活体实例"的源） | **永远无法推进**：每条都被判定为有活体实例 |

**根因**：

1. **通过覆盖创建的实例，无法在进程内被取消覆盖。** 无论释放表项还是改回自己，
   这些实例在军械库重建时都会让引擎踩空。崩溃无转储、无 WER 事件（反作弊吞掉）。
2. **军械库预览实例是按"被请求的源 id"登记的**，不是按解析后的目标 id。
   所以开过一次军械库之后，几乎每个源都"看起来有活体实例"，惰性还原的判据失效。

**因此确定的产品语义**：

| 操作 | 热生效 |
|---|---|
| 新增映射 | ✅ |
| **修改**映射（同一源改指向新目标） | ✅ 重指向即可，从不释放表项 |
| 删除映射 / 整体关闭 | ❌ **需要重启游戏** |

这也解释了为什么"修改"安全："后写覆盖前写"，同一源的多条表项共存也无害；
而"删除"必然释放，就会踩到上面第 1 条。

### M. 最终验收状态

- 映射应用：`revision=10 → queue drained (1988 sets), total overrides=1988`，视觉完美（用户确认两次）
- 热更新：改协议文件 1 秒内 diff 应用，无需重启
- 修改映射：热（重指向）
- 删除/关闭：运行时只记录"需重启"，绝不执行

### N. 进度清单

1. ~~修件解析（96 → 112）~~ —— 已完成（见 §D），全量测试 1260 通过 / 0 失败。
2. ~~重建资产索引~~ —— 已完成（见 §F），件表 100% 干净。
3. ~~导出器~~ —— 已可用（见 §F），297 条 map → 1777 条 link，0 个未解析 kit。
4. 端到端试跑：把 `tmp/wardrobe.links` 交给运行时 → `set_resource_override` → 开军械库看效果。
