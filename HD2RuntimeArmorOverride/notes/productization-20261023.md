# 产品化：资源覆盖运行时接入管理器

日期：2026-10-23
前置：`migration-preflight-two-answers-20261023.md`（机制与边界）

## 做了什么

### 1. 归纳出一个正式运行时

`HD2RuntimeArmorOverride/src/armor_override_runtime.lua` —— 由探针脚本收敛而来：

- 去掉了所有探针路径，日志与协议都走 `ArmorRegistry` 目录约定
  （`loader.log_directory` 去掉 `/Logs`，否则 `%LOCALAPPDATA%/CowboyBingus/Helldivers2`）
- 只解析 `schema/revision/enabled/link`；`map=` 是管理器意图，运行时不关心
- 整文件读取并按文本比较：半写入的文件不会被当成新版本；解析失败保留上一版
- 按帧节流写入（32/帧），2000 条不会拖住一帧
- **从不调用 `clear_resource_override`**，删除/关闭只记录"需要重启"

### 2. 换掉管理器里的载荷

`RuntimeArmorRegistryLuaSource.Load()` 现在只返回上面那一个文件。

同时摘除：

| 摘除的东西 | 原因 |
|---|---|
| `armor_registry_runtime` + transaction + composition + metadata probe | 依赖 ffi；26088801 上该模块不存在，加载即退出 |
| `armor_wardrobe_masks` + section apply/layout/locate/publish | 同样依赖 ffi 写 unit TOC；**按用户要求暂时关闭 section 关闭功能** |
| 各 `armor_refresh_*` 探针 | 诊断用，不属于产品链路 |

这些 .lua 源文件仍保留在 `src/` 作历史记录，只是不再被嵌入载荷。

### 3. 新增 C# 链接编译

`HD2ModCore/Infrastructure/WardrobeLinkCompiler.cs` —— 把测试脚本的算法移植过来：

- 按 `(body, slot, piece_type)` 分组来源 unit，目标逐件消费，`body=3`（Any）兜底，一件只消费一次
- **来源缺的槽位用隐藏 unit 补齐**，所以目标件永远有对应物（实测：RE-2310 补 217 处，RE-1861 补 828 处）
- 跳过自链接（`src == dst`）
- 输出 `schema=3 / revision / enabled / mode=apply / map=... / link=...`

接入点：`RuntimeArmorRegistryService.ApplyAsync` 在写完协议后编译并原子写出
`<ArmorRegistry>/wardrobe.links`。**没有游戏数据目录或编译失败时不写文件**——留上一版
比写一份空映射安全。

### 4. 测试改为锁契约

`RuntimeArmorRegistryLuaSourceTests` 原来逐条断言旧载荷的内部结构，现在改为断言契约：

- 载荷含覆盖运行时标记（`wardrobe.links`、`set_resource_override`、`queue drained`）
- 定义四个引擎钩子（`init/shutdown/render/update`）
- **不含 `clear_resource_override`**，且含"需重启"文案
- 不含 ffi / section / 旧协议字样
- 以注释开头、无 BOM（BOM 会让 Lua 5.1 编译失败，而引擎不 pcall 引导脚本）

## 验证

全量测试：**1264 通过 / 0 失败 / 1 跳过**
（ModCore 712、ModCli 175、ModAdaptation 185、SourceMigration 137、SourceModel 50、Decryptor 5）

## 2026-10-23 追加：换源后打开军械库崩溃（覆盖表累积）

**现象**：用管理器部署映射后，无论 RE-1861 → RE-2310 还是反向，换完打开军械库都崩；
不变就没事，重启就正常。

**日志证据**（`Logs/ArmorOverrideRuntime.log`）：

```
revision=129 links=1989 ... queue drained (1772 sets), total overrides=2004
```

- **`total overrides` 长期大于 `links`**（2004 vs 1989），多出来的是"已删除但按契约不释放"的陈旧项；
- 每次换外观都会新增 **15–23 条** removals，表只增不减；
- 2048 是很常见的表容量上限，我们当时只差 44 条。

**根因**：编译器**跳过自链接**（某个件在两套外观里恰好是同一个 unit 时不发 link），
于是 link 的**源键集随来源抖动**。运行时按源键做增量 diff，抖掉的键就成了"删除"，
而删除永不释放——表单调增长，最终撞上容量。

这也解释了为什么"加了头盔映射"之后才炸：不是头盔本身特殊，而是映射总数被推高到 2005，
离上限只剩几十条，再叠加每次换源的累积就越界了。

**修复**：

1. `WardrobeLinkCompiler` 现在**为每个目标件都发 link，包含自链接**（`src→src`）。
   于是源键集只由"目标护甲有哪些件"决定，与外观来源无关：换外观 = 同一批键的纯重指向，
   零增删、零累积。
2. 运行时把被引擎拒绝的写入单独计数并写进日志（`%d rejected`），
   这样"表满"会显式出现在日志里，而不是静默降级成"某些件没换"。
3. 回归测试 `WardrobeLinkCompilerTests` 锁住两个性质：
   换源后源键集必须完全一致；同键件必须发自链接。

**验证**：全量 1266 通过 / 0 失败 / 1 跳过。

## 2026-10-23 追加二：引擎覆盖表上限（已确认为外部限制）

用户实测：映射 297 套护甲（约 2005 条覆盖）时，**变更映射后打开军械库必崩**；
把映射数量减半后不再崩溃。这说明瓶颈是引擎/本体的覆盖表容量（约 2048），
不是我们的实现。反作弊环境下令我们无法修改本体去扩大它。

**因此确定的可用行为**：

- 映射数量在容量以内 → 启动时全量生效，视觉完整；
- **变更映射需要重启游戏**（应用是热的，但变更后打开军械库会崩）；
- 保持映射不变则始终正常。

这是产品契约的一部分，UI 上应明确提示"变更映射后请重启游戏"。

## 2026-10-23 追加三：「独占映射 Lua」开关（按用户定义重做）

设置页开关 → `DeployLuaMods` → `LuaDeploymentService.DeploymentEnabled`（缺省 true，
`CliWorkspace` 负责接线）。

**定义**：

| 状态 | 行为 |
|---|---|
| 开（默认） | 除了标准部署之外，**额外**投放护甲映射运行时补丁（含隐藏 unit，双条目） |
| 关 | **标准部署完全不变**；不投放运行时补丁；但**仍然投放只含隐藏 unit 的那一个补丁**（unit 必须始终存在）；同时把运行时脚本导出到文件夹并打开，供 MDL / Bingus 加载 |

为什么关闭时仍要投 unit：它是运行时的"填空"目标（来源套件没有的槽位指向它），
而用户可能正用第三方加载器跑我们的脚本——脚本运行时依然需要这个 unit 存在。
它是纯数据资源，不执行任何脚本、不影响游戏启动。

实现要点：

- 护甲运行时那段按 `DeploymentEnabled` 在三个分支里选：unit-only / addon（加载器）/
  直接引导补丁（独占且无加载器）；其余流程一行未动；
- 删除了原来"关闭即整份计划返回空"的一票否决——那会连其它 mod 的 Lua 一起禁掉，
  与这个开关的语义不符；
- 新增 `ArmorRuntimeScriptExport.Export(directory)`：把内置运行时写成
  `<目录>\armor_override_runtime.lua`，**不带 BOM**（Lua 5.1 不跳过 BOM，带 BOM 会编译失败，
  而加载器不 pcall 引导脚本 → 游戏启动即退出）；
- 设置页 `DeployLuaMods` 的 setter 在关闭时导出到
  `<默认导出路径>\wardrobe-lua` 并打开该文件夹，导出失败不影响设置保存。

**验证**：Core 714 / Adaptation 185 / SourceModel 50 / Decryptor 5 全绿。
CLI 的部署用例在游戏运行时会因 `IsGameRunning` 守卫失败（预期，关游戏后 175/175）。


## 已知缺口（发布前需要知道）

`独占` 开启且**机器上没有 Bingus/MDL 加载器**时，走的是"直接替换游戏引导资源"那条路径
（`CreateArmorRegistryDocumentAsync`），它目前**只带 Lua 条目、不带隐藏 unit**。
影响：缺槽部件会露出原版件（不会崩）。有加载器的机器不受影响（走 addon 路径，双条目）。
要把隐藏 unit 也塞进这条路径，需要在它合成 patch 时追加同一个 unit 条目。


## 隐藏 unit 的部署（方案已定，实现中）

运行时把"来源缺的槽位"指向隐藏 unit `def7dc54e86d6054`，这个 unit 必须真实存在于游戏里
（实测：指向不存在的 id 会让引擎记住加载失败，之后军械库整页空白并崩溃）。

**用户定下的方案**：一个 patch 里两个条目，不占第二个 patch。

| 条目 | 类型 | 内容 |
|---|---|---|
| Lua 资源 | `0xA14E8DFA2CD117E2` | 运行时源码 |
| unit | `0xe0a48d0be9a7453f` | 空 unit `def7dc54e86d6054`（TOC 4240 + GPU 418） |

并且：**关闭"独占映射 lua"时只部署这个 unit 条目**（不含运行时），所以其它 Lua 模组的
加载不受影响。以后管理器的运行时一律塞进同一个 patch。

实现方式（不需要手搓字节）：

- 把构建好的空 unit 补丁作为**模板**嵌入管理器：
  `resources/wardrobe_hidden_unit.patch` + `.gpu_resources`（已加入 csproj）
- 用现成的 `PatchArchiveWriter.WriteAsync` 以该模板为源、把 Lua 资源作为
  `PatchArchiveAdditionalEntry` 追加进去，输出即"一个 patch 两个条目"
- 独占映射关闭时不追加 Lua 条目，模板原样投放

归档布局交给已验证的写入器，不引入第二套字节逻辑。

**已实现**：

- `resources/wardrobe_hidden_unit.patch` + `.gpu_resources` 作为模板嵌入
  （`WardrobeHiddenUnitTemplate`，key `def7dc54e86d6054`）
- `CreateArmorRegistryAddonAsync` 改为：把模板写到临时目录 →
  `PatchArchiveWriter.WriteAsync` 追加 Lua 条目 → 拷回输出目录，
  同时产出 `.gpu_resources` 侧车（模板自带 418 字节 GPU）
- 测试断言补丁里恰好两个条目：Lua（fileId `0x0A2E0E3386517D4C`）+ unit
  （fileId `0xDEF7DC54E86D6054`，TOC 精确 4240、GPU 精确 418）
- 构建器已支持 `includeRuntime: false` → **只投放那个 unit**，不追加 Lua 条目

**还差一步**：代码里目前**没有**"独占映射 lua"这个开关（按
`Exclusive`/`ManagedLua`/`OwnBoot`/`TakeOverBoot` 等命名都没搜到）。需要在设置页
新增一个布尔项并从调用点传入 `includeRuntime`，那一行接线完成即可生效。

注意 Lua 条目的 fileId 必须是**资源名哈希** `StingrayHash("mods/codex/runtime_armor_registry")`，
不是 `LuaAssetKeys.BootFileId`（后者是游戏自身引导脚本的 id，用错会与之冲突）。


