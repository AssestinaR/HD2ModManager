# 护甲映射：旧实现的确切边界

日期：2026-10-23
目的：在把护甲映射切到 `set_resource_override` 之前，先钉死"哪些代码属于这个功能"、
"哪条缝是可移植的"、"换了接口会失去什么"。

## 1. 数据契约（协议 schema 2）

文件：`%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry/armor-registry.protocol`
（管理器写、原子替换、`revision` 递增；Lua 每秒读一次完整内容并比对）

属于**护甲映射**的键：

| 键 | 语义 |
|---|---|
| `map=<target_item>,<source_item>` | 目标护甲保留身份与玩法元数据，采用源护甲的视觉组合 |
| `map_model=<target_item>,<model_key>` | 目标采用一个虚拟模型基座（不对应任何可装备物品） |
| `model=<key>,<source_item_or_empty>` | 虚拟模型基座，可克隆自真实护甲 |
| `model_piece=<key>,<body>,<unit>,<slot>,<piece_type>,<tone>[,enabled[,default_base]]` | 模型的一件：**body / unit / slot / piece_type / tone** |
| `piece_override=<owner>,<body>,<unit>,<slot>,<piece_type>,<enabled>` | 按件开关 |
| `target_mode=<target>,follow\|manual` | 目标是否跟随源更新 |
| `composition_mode=full\|existing_only` | 重建结构，或只替换 unit |

**不属于**这个功能（下一阶段）：`wardrobe.masks`（section 掩码）及其
`armor_section_apply/layout/locate/publish`。

## 2. 执行管线与那条缝

`armor_registry_runtime.lua`：

```
locate_registry()                        :639   内存：找注册表 blob
snapshot_root(root, checkpoint)          :881   内存：读原生 kit（rows/pieces）
snapshot(item)                           :923   → {header, rows[{body, pieces}]}
        ↓  target = snapshot(mapping.target)
        ↓  source = snapshot(mapping.source)  或  model = 协议里的虚拟模型
ARMOR_REGISTRY_COMPOSITION.plan(...)     :964   ★ 纯函数：bytes in → bytes out
allocate_plan(plan)                      :906   内存：为新 payload 分配
publish_document(plan, document)         :1012  内存：换根指针、校验 guard
```

**缝就在这里**：`C.plan` 的输入是"目标组合 + 源组合 + 元数据"，输出是一个新组合
的字节 payload。它不碰任何指针（文件头原话：*Pure, pointer-free*）。

所以：

- **内存依赖只有两处**：`snapshot_root`（读原生组合）与
  `allocate_plan`/`publish_document`（写克隆、换指针）。
- 中间的组合解析是**完整可移植**的。

## 3. 逐文件边界

| 文件 | 属于护甲映射？ | 换接口后的处置 |
|---|---|---|
| `armor_registry_protocol.lua` | 是，纯 | **保留**。schema 2 的解析/序列化规则、校验、排序全部照用 |
| `armor_registry_composition.lua` | 是，纯 | **保留并裁剪**。只留解析部分，丢掉 96 字节 piece 打包 |
| `armor_registry_runtime.lua` | 部分是 | 拆：目录/日志/文件 I/O/轮询**保留**；ffi、locate、snapshot、allocate、publish **换掉** |
| `armor_registry_transaction.lua` | 否（内存事务） | **丢弃**，引擎自己管覆盖表 |
| `registry_slot_snapshot.lua` | 否（内存快照） | **丢弃**，组合数据改由管理器提供 |
| `armor_metadata_probe_core.lua` | 只服务于 full 模式 | 新链路**不需要**（见 §5） |
| `armor_section_*` | 否 | 下一阶段 |

## 4. 组合解析里真正要移植的那段

`C.plan` 有两条分支，只有一条是新链路需要的：

**`existing_only`（第 47–108 行）—— 这就是新链路**

```
按 (body, slot, piece_type) 把源的 unit 分组
逐件遍历目标：
    queue = grouped[目标行.body, slot, kind]
    queue = queue or grouped[3, slot, kind]      -- Any body 兜底
    candidate = 从 queue 弹一个（一件只消费一次）
    candidate 存在 → 把该件的 unit 换成 candidate
    否则          → 保留原件
```

它保持目标的行结构、件数、顺序不变，**只换 unit**。这恰好是
`set_resource_override('unit', 目标件.unit, 源件.unit)` 的能力范围。

**`full`（第 109–241 行）—— 新链路做不到**

它会按源或模型**重建** rows/pieces：改件数、改顺序、注入目标本来没有的件、
从元数据恢复 weight、把 tone 写进 piece、还要处理原生 20 件预算与行序。
这些全都依赖"改 piece 记录"，而新链路一个 piece 记录都不碰。

## 5. 能力边界：新链路 = `existing_only`，且只到 unit 级

| 旧能力 | 新链路 | 原因 |
|---|---|---|
| `map=target,source` 整套换视觉 | ✅ | 逐件 unit 覆盖 |
| 多个目标共享一个源、不复制 GPU 负载 | ✅ | 覆盖按 unit id 生效，天然共享 |
| `existing_only` 只换 unit | ✅ | 完全等价 |
| `full` 重建结构（增/删件、改顺序） | ❌ | 结构由游戏原生 kit 决定 |
| `piece_override` 关掉某一件 | ❌ | 覆盖不能"不加载"；要隐藏需 `Unit.set_unit_visibility`/`set_mesh_visibility`，属另一类能力 |
| `tone`（每件 0–255） | ❌ | tone 是 piece 字段，不是资源；材质替换是另一条路 |
| weight 恢复 | 不需要 | piece 记录不动，weight 天然保留 |
| 20 件预算 / 行序 | 不需要 | 同上 |
| 头盔 Any-body 展开 | 仍需要 | 匹配阶段的事，与写入方式无关 |

一句话：**新链路不是"退化版"，而是旧实现里那个本来就更安全的模式被单独留下来，
其余模式随内存修补一起退役。**

## 6. 由此推出的一条硬性结论

旧链路的组合数据来自 `snapshot_root`（读游戏内存）。新链路不读内存，所以
**每套护甲的件表必须由管理器提供**。

这也正好符合你要的分工：

- **管理器（WPF）**：舒适的选择界面；把用户的选择 + 所有被引用护甲的件表
  编译进注册文件。
- **Lua**：读文件 → 按 §4 的 `existing_only` 算法解析成 unit 对 → 校验 → 调用
  `set_resource_override` → 失败整体回滚 → 状态写回。

核心逻辑仍在 Lua，管理器只负责"用户意图 + 事实数据"，不参与运行时决策。

## 7. 注册文件该长什么样（建议）

沿用现有协议的风格，新增两段，语义自足：

```
schema=3
revision=N
enabled=true

# 用户的选择：目标护甲 → 源护甲
map=488691438,820333399

# 事实数据：每套被引用护甲的件表（管理器从权威库导出）
kit=488691438
kit_piece=488691438,3,af431352d9b95e83,0,0
kit_piece=488691438,3,45d79d84b8933a7f,3,1
...
kit=820333399
kit_piece=820333399,3,c9904983657e9fc8,0,0
...
```

Lua 侧：`kit_piece` 提供 (body, slot, piece_type, unit)，`map` 提供选择，
运行时按 §4 的算法做 join，得到 `(目标 unit, 源 unit)` 列表，再逐条覆盖。

这样 `revision` 语义、原子替换、每秒轮询、失败保留上一版——全部沿用，
只有"最后一步怎么写进去"从内存写入变成 API 调用。

## 8. 需要在实现前确认的两件事

1. **`map` 的 target 是 32 位 item id，而覆盖需要 64 位 unit id。**
   旧链路靠内存快照把 item → kit → pieces 连起来；新链路靠 `kit_piece`。
   要确认管理器的权威库里，每个 item id 都能导出完整的件表（含头盔 slot 0、
   披风 slot 1），否则映射会在那些槽位上静默缺失。
2. **`piece_override` 与 `tone` 是否真的在用。** 若现有用户配置里存在这两类条目，
   切到新链路会丢失它们的效果，需要明确是"暂不支持并提示"，还是"迁移时丢弃"。
