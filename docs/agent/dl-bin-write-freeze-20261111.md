# 真映射停写：`.dl_bin` 写入导致游戏闪退（2026-11-11）

> **一句话**：游戏这一版改了护甲注册表文件的校验方式。文件本身仍能正常解密、格式没变、
> 写进去也不报错——但**写过的文件一进游戏就闪退**。所以真映射这一层停写，外观改由软映射
> （运行时 Lua）承载。

## 1. 现场（用户实测 + 本仓取证）

游戏在 2026-11-11 更新（Steam buildid `26568081`，`LastUpdated` 18:19:35），
`bundles.*.nxa` 与全部 `data/game/*.dl_bin` 在 18:18 被重写。

用户的实测结论有两条，都很关键：

1. **物理 Lua 仍然正常运行** —— 不是"两条 Lua 都失效"；
2. **真映射写完的 `.dl_bin` 一进游戏就闪退**，虽然文件能写、也不报错。

### 1.1 物理这条线没坏（先排除的误判）

`%LOCALAPPDATA%\hd2physbone.log` 当天 18:26 仍在活跃输出，帧号到 2308：

```
18:26:45 f2084  [physbone][裙摆] 推 15/44 条（权重>0 20）· 角度 均值 13.4° ...
18:26:49 f2308  [physbone][RESYNC] 锚点跳变，2 个尖端已对齐目标（第 1 次）
```

引擎侧契约也没变。抽取当前原版 `boot`（`0xa14e8dfa2cd117e2` / `0xf476df93691895fa`）：

```
size=509   header: len=501 ver=1   first byte=0x1B (Lua 5.1)
字符串: init / update / render / shutdown / jit  各 1 次
```

与 2026-10 的记录逐项一致，`settings.ini` 的 `boot_script = "boot"` 也还在。
**结论：Lua API 不需要重新探索。**

### 1.2 `.dl_bin` 的实测状态

对 `data/game/generated_customization_armor_sets.dl_bin`（453252 B）：

| 检查 | 结果 |
|---|---|
| 封面 | `7A 17 56 98 34 D4 AA D5 …`（密文，正常） |
| 解密 | **成功**，453252 → 453204 字节 |
| 明文头 | 魔数 `LDLD`，`typeId = 0xD9A55AA0`（= 代码里的 `ArmorCustomizationTypeId`） |
| 文件属性 | `Archive`，无只读 |
| 独占句柄 | 拿得到；目录可写 |
| 重新加密 | 长度一致、可再解 |
| 同批其它 5 个 `.dl_bin` | 全部可解、全是 `LDLD`、`typeId` 各异 |

也就是说：**读、解、写、再解这一整圈，在这一侧全部正常**。
失效点不在文件 I/O，而在游戏读文件时的**校验**：我们伪造回原值的那套做法（`AppendForgeWord`
+ 闭式求解 MurmurHash64A）对新的校验方式不再成立，进程起来就崩。

### 1.3 当前那份文件是干净的

```
LIVE   sealed=373BB4EB…7E8AE  plain=9DAB2EA0…FE6274
BACKUP sealed=373BB4EB…7E8AE  plain=9DAB2EA0…FE6274
```

与 `ArmorRegistry/backup/generated_customization_armor_sets.original.dl_bin` **逐字节相同**，
且明文哈希与 `armor-data-mapping.state` 第 3 字段一致。**当前没有需要还原的真映射改动**。

> 备份目录位置容易被漏掉：`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\backup\`。
> 它**不在**游戏目录、也不在管理器目录，在游戏目录/管理器目录里搜 `*original*.dl_bin` 会一无所获。

## 2. 处置（用户口径，2026-11-11）

> "降级到软映射，并且消息弹窗提示，并且需要软映射也不再修改 `.dl_bin`。"

后续又追加了一条口径：

> "软映射的选项需要改为独占boot、外置加载器兼容、关闭；这三种，与物理模式类似"

四条落地为：

1. **硬停写**。`ArmorRegistryDataMapper.ApplyAsync` 入口直接拒绝，不再判断"要不要写"。
2. **连还原一起停**。`mappings` 为空的"还原"调用同样是写、同样会闪退——这条最容易漏，
   而且恰好是"离开真映射模式"必经的那次调用。若不拦，用户想安全回退的动作本身就是那次崩溃。
3. **软映射改成与物理一致的三档**：`Off` / `ExclusiveLuaEntry` / `ExternalLuaEntry`，
   **数值也逐档对齐**（0/1/2）。真映射作为第四档被删除。
4. **旧存档自动换算**，并在启动后弹窗告知。

### 2.1 三档改造与数值迁移（易错点）

`ArmorMappingMode` 原先是 `0=独占 / 1=外置 / 2=真映射`，新口径是 `0=关闭 / 1=独占 / 2=外置`。
**两套数值重叠但含义不同**，直接按新口径读旧存档会把"开着独占"的用户悄悄变成"关闭"。

所以存档里加了 `MappingModeSchema`：

| 存档情况 | 读法 |
|---|---|
| 无 `MappingModeSchema`（旧档） | 走 `ArmorMappingModes.MigrateStoredValue`：0→独占、1→外置、≥2→关闭 |
| `MappingModeSchema >= 2` | 按新口径原样读 |
| 写设置时 | 一并写 `MappingModeSchema = CurrentSchema` |

CLI 与 GUI **必须用同一套翻译**（`CliWorkspace` 与 `SettingsService` 都调 `MigrateStoredValue`），
否则同一份设置会得出两个不同结论——这比读错更糟，因为现象是"管理器里开着、命令行说关着"。

### 2.2 代码落点

| 位置 | 改动 |
|---|---|
| `HD2ModCore/Domain/ArmorMappingMode.cs` | 三档重排 + `MigrateStoredValue` + `TrueMappingWritable=false` + `CanWriteRegistryData` + `AvailableFor` 不再含真映射 |
| `HD2ModCore/Infrastructure/ArmorRegistryDataMapper.cs` | `ApplyAsync` 入口返回"已暂停写入…"，不碰任何文件 |
| `HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs` | **整块删除**真映射落地/还原（两条都写盘）；`DataMapping` 恒为 null |
| `HD2ModCore/Infrastructure/LuaDeploymentService.cs` | **整块删除**"离开真映射时还原注册表"（它本身就是那次写） |
| `HD2ModManager/Services/SettingsService.cs` | `LastMappingModeWasMigrated`；读写都走 schema 迁移 |
| `HD2ModManager/App.xaml.cs` | 启动后（`ApplicationIdle`）用 `AppDialog` 弹一次提示 |
| `HD2ModManager/ViewModels/PageViewModels.cs` | 热切换提示不再说"写进注册表数据、必须重启" |
| `HD2ModCli/CliWorkspace.cs` | 与 GUI 同一套迁移；设置模型加 `MappingModeSchema` |

### 2.2 为什么用 `static readonly` 而不是 `const`

`const bool = false` 会让编译器把判断当常量折叠，直接报「无法访问的代码」（本仓库把警告当错误）。
而且折叠掉之后再也没法用测试覆盖这条路径。改成 `static readonly` 后保留运行时可读性，
将来恢复只需改一个值。

### 2.3 测试怎么保住编译算法的覆盖

编译算法（件表改写、校验和伪造、对齐、幂等与撤销）有 20 多个用例，它们必须走**真实写盘路径**
才能证明自己没坏。停写之后那条路径在正常配置下走不到，所以留了一个**仅供测试**的入口：
`PushAllowWriteForTests/PopAllowWriteForTests`（进程级计数，用计数而非布尔是因为 xUnit 按集合并行）。

- `ArmorRegistryDataMapperTests`、`ArmorRegistryCompileSemanticsTests`：构造时 push、`Dispose` 时 pop，
  继续验证编译逻辑；
- `ArmorRegistryWriteDisabledTests`（新增 5 个）：验证写入被拒、还原被拒、不落备份、磁盘字节不变；
- 三者同挂 `ArmorRegistryWriteGate` collection（`DisableParallelization = true`），
  避免"放开写入"与"断言被拒"两个方向互相踩。

## 3. 软映射能不能正确映射到原版护甲记录（实测核对）

停写之后外观全靠软映射，所以必须证明它**读得对**。软映射不读 live `.dl_bin`，
读的是索引库 `data/indexes/asset-index.sqlite`（`ArmorAuthorityIndexReader`），
所以判据是"索引是不是更新后重建的" + "映射目标是否都在索引里"。

本机实测（2026-11-11 18:5x）：

| 检查 | 结果 |
|---|---|
| 索引构建时间 | `meta.built_utc = 2026-11-11T10:27:41Z`（本地 18:27，游戏 18:18 更新之后） |
| 索引内容 | 422 套 / 3527 件；护甲均 23 件，头盔 161 套各 1 件、披风 123 套各 1 件（正常） |
| 协议侧 | 11 个自定义素体，`map_model` 299 条 |
| **`map_model` 目标是否在索引里** | **299/299 命中，0 条悬空** |
| `wardrobe.links` 结构 | 2006 条，**0 重复、0 自链接、0 悬空来源** |
| **件覆盖完整性** | **原版 unit 件 3404 个，0 个漏 → 覆盖率 100%** |
| 其中真实换成素体件 | 431 件（12.7%） |
| 其中用隐藏 unit 填 | 2973 件（87.3%） |

**结论：软映射能把自定义素体正确映射到原版 `.dl_bin` 的护甲记录。**

关于 87% 的隐藏填充——这与用户确认的口径一致：**是那个素体本身件少**（13 件 / 原版 23–29 件），
缺的槽位按 `WardrobeLinkCompiler` 的既有契约用隐藏 unit 填满（避免留下没被替换的残留件）。
不是丢件，也不是映射失败。

> 期间一个容易误判的现象：11 个素体里只有 1 个的件被用上，其余 74 个件"未使用"。
> 查明原因是 `map_model` 只指向那一个素体——另外 10 个**本来就没配映射**，属于正常。

## 4. 附带修掉的：软映射"只有头盔成功、护甲主体全隐藏"

停写之后外观全靠软映射，用户上机一测就暴露出一个**早已存在**的编译器缺陷。
它不是这次更新引起的，只是以前被真映射挡在前面没被看见。

### 4.1 症状与实测

一条自定义素体映射到 299 套护甲时，24 件里只有 2 件真实替换，**护甲主体全部隐藏**，
只有头盔能显示。

### 4.2 根因一：两套身形编码被当成同一套比较

| 侧 | 编码 | Stocky | Slim | Any |
|---|---|---|---|---|
| 素体件（`RuntimeArmorRegistryModelPiece.Body`） | **注册表原始编码** | **0** | 1 | 3 |
| 权威表件（`GameDataAuthorityPiece.BodyVariant`） | `UnitMeshBodyVariant` | **2** | 1 | 3 |

`WardrobeLinkCompiler` 直接拿两者比较。Stocky 在两边分别是 0 和 2 ⇒ **永远对不上**；
Slim(1) 与 Any(3) 只是**碰巧相同**。头盔是 Any ⇒ 成功；护甲主体是 Stocky ⇒ 全落空。

`ArmorStructure.ToBodyVariant` 的注释早就警告过这类错误（"别处一律调用它，
不要写 `(UnitMeshBodyVariant)raw`"），`SectionalModelDefinition` 也写明只能写原始编码。

### 4.3 根因二：缺两条真映射时代已有的匹配规则

`ArmorRegistryDataMapper`（已停写的真映射）里有两套经过实机验证的判据，
`WardrobeLinkCompiler` 一条都没有：

1. **Any 行接受按身形拆的来源件**：两份指向同一 unit 才用；不同则隐藏这一层，
   绝不挑一份塞给两种身形（那会让一半身形穿错网格）。
2. **整片对不上时最底层借用**：原生 138 套里胯有 16 套只有 `pt1`、左腿 9 套、右腿 11 套、
   右臂 12 套同样只有 `pt1`，而素体只按 `pt0` 声明。严格按层号对齐会把这些部位整片隐藏，
   表现为"这套护甲缺胯/缺腿/缺手"。

### 4.4 修法与效果（真实数据、真实编译器）

- 素体件入索引前先换算身形编码（`ArmorStructure.ToBodyVariant`）。
- 移植上面两条判据：单格解析 `ResolveByLayer` + 整片回退 `ResolvePartFallback`，
  判据与 `ArmorRegistryDataMapper` 逐条对齐，两侧不能分叉。

用真实 `asset-index.sqlite` + 真实 `armor-registry.protocol` 跑真实编译器：

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 真实替换 | 284 | **1059** |
| 隐藏 | 1722 | 948 |
| 变差的件 | — | **0** |

逐套看（`29533812 AD-26`）：槽位 2 胸 / 3 胯 / 4 左腿 / 5 右腿 / 6 左臂 / 7 右臂
**全部拿到真实来源**；仍隐藏的只有 1 披风、8 左肩、9 右肩 —— **素体本来就没有这些件**，
隐藏是正确行为。

> 槽位编号见 `CustomizationKitSlot`：0 头盔 / 1 披风 / 2 胸 / 3 胯 / 4 左腿 / 5 右腿 /
> 6 左臂 / 7 右臂 / 8 左肩 / 9 右肩。

### 4.5 测试

`WardrobeLinkCompilerTests` 原有 4 个用例的目标与素体件**全用 Any 行**，
Any 与 Any 匹配，所以这个 bug 长期没被发现。新增 5 个：

- `Compile_ConvertsTheModelBodyEncodingBeforeMatching`（旧实现失败）
- `Compile_AnyTargetAcceptsPerShapeSourceOnlyWhenBothShapesAgree`（旧实现失败）
- `Compile_BorrowsTheLowestLayerWhenTheWholePartMisses`（旧实现失败）
- `Compile_DoesNotBorrowWhenThePartIsAlreadyCovered`（防过度修复）
- `Compile_KeepsPartsTheModelDoesNotHaveHidden`（防过度修复）

前三个在旧编译器上确实红（已用 stash 回退验证），不是写了就算。

## 5. 恢复条件

`TrueMappingWritable` 翻回 `true` 之前，必须满足：

1. 弄清游戏新的校验方式（或官方回退这次改动），使伪造方案重新成立；
2. 有一份**确认干净**的原件副本（当前那份与 live 文件相同，是干净的）；
3. `ArmorRegistryWriteDisabledTests.FreezeFlagMatchesTheAnnouncedState` 会随之变红——
   这是刻意留的信号：改这个标志就必须同时更新本文与发版说明。

## 6. 已知未做（刻意）

- **没有探测"能不能安全写"**。校验能过、进游戏仍然会崩，探测得出的"可以写"是假阳性。
- **没有删备份、没有改设置文件**。用户当前的 `settings.json` 里 `ArmorMappingMode = 0`，
  本来就不是真映射；将来恢复时不需要用户改回来。
- **没有动物理线**。它没坏。

## 7. 发版时的用户口径

- 物理照常。
- 如果你的设置里存的是真映射，这一版会自动改用软映射（运行时 Lua），并弹窗说明；
  映射内容不丢，但真映射这一档暂时不可选。
- 别去手动改 `data/game/generated_customization_armor_sets.dl_bin`；
  当前那份是干净的，改了反而会让游戏闪退。



