# 三种映射模式与真映射落地（2026-10-25）

## 1. 设置页：二态开关 → 三态下拉

旧的「独占映射 Lua」布尔开关已移除，替换为 `ArmorMappingMode`：

| 模式 | 值 | 投放 Lua | 热切换 |
|---|---|---|---|
| 防污染兼容模式（独占 lua 入口） | 0 | 自己占 boot | ✅ |
| 防污染兼容模式（外置 lua 入口兼容） | 1 | 独立补丁，交给外置加载器 | ✅ |
| 真映射模式 | 2 | 不投放 | ❌ |

关键改动：**由模式决定投放方式，不再自动探测加载器**。旧代码里
`HasActiveBingusLoader` 会在装了加载器时悄悄改变行为，现在这种行为差异是用户选的。

设置迁移：老 `settings.json` 里 `DeployLuaMods=true` → 独占入口；
`false` → 外置入口（旧行为就是"交给外置加载器"）。新值写在 `ArmorMappingMode`（整数）。

## 2. 真映射模式

`ArmorRegistryDataMapper` 直接改写
`data/game/generated_customization_armor_sets.dl_bin`：

```
读原始备份 → 解密 → 按映射重建目标套件的件集 → 把内容哈希伪造回原值 → 重新封装 → 原子替换
```

- **始终从原始备份重建**，所以重复应用幂等，删掉映射就自动还原。
- 件集按 `(bodyType, slot, pieceType)` 匹配，来源有而目标没有的件会被补上，目标多出的会被删掉。
- 单次伪造 < 2 ms（453 KB 文件）。

### 2.1 必须保留目标的 `weight`（护甲等级）

件记录 `+16` 的 `weight` 是**玩法字段**——它决定这件是轻甲/中甲/重甲。旧 Lua 运行时的
受控实验早就记录过这一点（`armor_metadata_probe.lua:181`）：

> `+16 weight; ... weight is gameplay-sensitive based on prior controlled armor-value tests`

旧实现因此在重建件表时显式把 weight 换回目标自己的
（`armor_registry_composition.lua:231-233`：只换 unit，weight 取 `resolve_weight` 的结果）。

整条记录照搬会让目标甲继承来源甲的等级，等于顺手改了数值。所以克隆时对每一件：

1. 先按 `(bodyType, slot, pieceType)` 找目标自己的 weight，取众数；
2. 没有就退到同 body 的众数；
3. 再没有就退到整套的众数。

这样"来源独有的件"（目标原本没有该槽位）也拿到目标甲的等级，而不是来源的。

**兼容模式不受影响**：运行时只调用 `set_resource_override('unit', ...)` 重定向 unit 资源，
从不触碰件记录，weight 天然保留。

### 2.2 字段来源规则：只有三个字段取自来源

进一步核对后发现，"只保 weight"还不够。旧实验对件记录的分类是
`Unit/resources/tone are visual candidates; slot/body/piece_type are structural;
weight is gameplay-sensitive`，但真正决定"映射该换什么"的是**哪些字段属于目标这套护甲自己**：

| 偏移 | 字段 | 来源 |
|---|---|---|
| 0 | unit | **来源** |
| 8 | slot | **来源** |
| 12 | piece_type | **来源** |
| 16 | weight（护甲等级） | 目标 |
| 20–23 | 未解码 | 目标 |
| 24–80 | 8 个资源哈希（LUT 等） | 目标 |
| 88 | tone | 目标 |
| 89–111 | 保留区 | 目标 |

实现方式：先用目标件整条记录当模板（按 同 body+slot+type → 同 body+slot → 同 body
逐级回退），再只覆盖 unit / slot / piece_type 三个字段。

这样做的两个后果都是想要的：

1. **真映射与兼容模式语义一致**——运行时本来就只重定向 unit，件记录一个字节不动。
   同一份映射在两种模式下外观相同，不会因为换了模式而"变了个样子"。
2. **不碰任何玩法或配色字段**，映射不可能顺手改数值。

### 2.3 `tone`（+88）实测

| 偏移 | 分布（422 套 / 3527 件） |
|---|---|
| +88 tone | `0`×3497、`4`×4、`5`×26 |
| +89 保留 | `0`×3527（永远是 0） |

非零只出现在 6 套护甲：PH-56 Jaguar、PH-9 Predator、O-2 Heavy Operator、
RS-40 Beast of Prey、IE-57 Hell-Bent（均 5）、PH-202 Twigsnapper（4），
且全部挂在 `pieceType=1` 的变体件上。

字段名 `ToneVariations` 与取值形态（0/4/5）指向"这套护甲有几种配色变体"。无论
切换机制是什么，**取目标的**都是保守且正确的选择：目标原本没有第 2~5 套材质，
把来源的计数写进去只可能让游戏索引到不存在的变体。

**旧 Lua 这里有个 off-by-one**：`armor_metadata_probe_core.lua:55` 与
`composition_schema.lua:42` 都读 `+89`，而 +89 恒为 0 —— 所以旧链路的
`tone_variations` 从来没真正读到过这个字段。我们 C# 读 `piece[88]` 是对的。

### 2.4 自定义素体接入

素体的 unit 是全新 id，注册表里没有对应 kit，所以两条链路原先都把它过滤掉了：

| 位置 | 原代码 |
|---|---|
| `WardrobeLinkCompiler` | `.Where(mapping => !mapping.IsModelMapping && ...)` |
| `ArmorRegistryDataMapper` | `.Where(... && !mapping.IsModelMapping)` |

结果就是**自定义素体在新链路里完全不生效**（只有已退役的 ffi 运行时认它）。

现在两条链路都把来源解析成同一张件表 `(body, slot, pieceType, unit)`：

- 游戏护甲 → 从注册表 kit 取；
- 自定义素体 → 从工作区 `models[].pieces` 取（`Enabled` 的件才参与，unit 是 16 位 hex）；
- 素体没有显式件表时 → 用它的 `SourceItemId` 模板护甲。

素体天然只提供 unit / slot / pieceType，正好符合 2.2 的来源规则，不需要额外分支。

`map=` 行只是管理器的意图记录（运行时读 `link=`），素体映射没有来源物品 id，
因此不写该行。

## 3. 避免"游戏已更新"误报

这是必须处理的一点：该文件的**解密内容直接参与 GameData 索引指纹**
（`GameDataAuthorityProvider.ComputeFingerprint` 的 `authoritativeBytes`，以及
`ComputeLocalizationSourceFingerprintAsync` 里的文件长度/修改时间）。不改就会导致
用户每次应用映射都被提示"游戏已更新"。

做法（`ArmorDataFileIdentity`）：

- 写文件时把 `原始密文SHA256 | 写出密文SHA256 | 原始内容SHA256` 记进状态文件；
- 计算指纹时，只有当磁盘上的密文**正是我们写的那份**，才用"原始内容哈希"代替；
- 其余情况返回 `null`，调用方走原算法。

两个性质因此同时成立：

1. 我们的改动对指纹不可见 → 不再误报；
2. 游戏真的更新后，密文既不是备份也不是我们写的 → 身份回落 → 更新仍被识别，
   映射器也会把新文件当作新基准重新应用。

另外：**从未使用过真映射模式的用户，指纹值与旧版本完全一致**，不会因为这次改动
被无谓地判为过期。

## 4. 验证

| 测试 | 结果 |
|---|---|
| Core | 720 |
| Adaptation | 185 |
| SourceModel | 50 |
| Decryptor | 5 |
| CLI | 175 |
| SourceMigration | 137（1 跳过） |

`ArmorRegistryDataMapperTests` 覆盖：改件集后哈希仍为原值、幂等、无映射时还原字节、
缺套件跳过、多映射连续重建、身份隐藏与回落。

## 5. 实机已验证（真机，2026-10-25）

| 实验 | 改动 | 结果 |
|---|---|---|
| 1 | 只改 9 个自由字节 | 进大厅 |
| 2 | 21 件换 unit | RE-824 变成 RE-2310 外观 |
| 3 | 加 1 件（23→24），文件 +112 字节 | 不崩，无可见变化 |
| 4 | 件集整体替换（删 2 件 + 加 2 件） | RE-824 与 RE-2310 完全一致 |

实验 3 说明"文件长度可改"（`size` 字段 + `pieceOffset` 平移都被接受），
实验 4 说明"删件 + 插件"都正常。

## 6. 已知边界

- 真映射模式改的是游戏安装目录，Steam「验证文件完整性」会还原。
- 备份在 `<注册表根>/backup/`；删除它会让下一次应用以当时的文件为新基准。
- 真映射模式目前只做"整件集克隆"，尚未做 unit 去重（把未引用的 unit 换成隐藏 unit 以省显存）。
