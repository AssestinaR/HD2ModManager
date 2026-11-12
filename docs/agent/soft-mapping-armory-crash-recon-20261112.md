# 软映射与"军械库/债券页崩溃"：情报侦察（2026-11-12）

> **本文只做情报，不改代码**。用户本轮要求："先大致搜查一下情报，不着急直接改，一步一步来。"
> 结论按"已证实 / 强怀疑 / 待验"分级，别把怀疑当结论用。

---

## 0. 一句话

**独占 boot 模式下，管理器只投放了 Lua，没有投放"隐藏 unit"补丁；而生效的 `wardrobe.links` 里有
948 条映射指向那个不存在的 unit。** 本仓自己的代码注释写着这种情形的后果是
"**引擎记住加载失败 → 军械库整页空白并崩溃**"。

---

## 1. 软映射到底在做什么（现状：只有一条链路在役）

| 层 | 实现 | 说明 |
|---|---|---|
| 意图 | `armor-registry.protocol` + workspace | 管理器侧权威记录（本机 299 条映射） |
| 编译 | `WardrobeLinkCompiler.Compile` | 把意图编译成 `wardrobe.links`：**target 件表逐件 → 源 unit**；源缺槽位时指向**隐藏 unit** |
| 投放 | `LuaDeploymentService` | 独占 boot ⇒ 只发 boot 补丁；外置入口 ⇒ 发 `armor-registry-addon`（Lua + 隐藏 unit） |
| 运行时 | `armor_override_runtime.lua`（195 行，无 ffi） | 1 Hz 读 `wardrobe.links`，**只做** `Application.set_resource_override('unit', src, dst)`，32 条/帧，**从不释放** |

关键事实（代码层）：

- `armor_registry_runtime.lua`（65 KB 的 ffi 内存修补链）**早已不在役**：那版引擎没有 `require('ffi')`，
  `RuntimeArmorRegistryLuaSource.Load()` 只投放 `armor_override_runtime.lua`。
  ⇒ 之前（9 月）关于"composition / piece 数量不匹配"的假设属于**旧链路**，与现在的崩溃嫌疑无关。
- 运行时**不在开页面时写任何东西**：只在 `wardrobe.links` 内容变化时增量写。
  ⇒ 用户猜的"打开军械库时写了什么"不成立；成立的是**"写了从不回收"**（覆盖表只增不减）。
- 删除/关闭映射按契约**不释放**，只记"需要重启"（`clear_resource_override` 一次性释放 1988 条会当场崩，
  见 `notes/migration-preflight-two-answers-20261023.md`；memo 里那条 API 发现指的正是这里可再研究）。

---

## 2. 本机实测（2026-11-12，全部只读）

### 2.1 生效的映射规模

`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\wardrobe.links`（78 KB，revision 245）：

```
schema=3  revision=245  enabled=true  mode=apply
link 数 2007（去重源 2005）   目标 unit 只有 8 个
    def7dc54e86d6054  ×948      ← 隐藏 unit（不画任何东西）
    5c44f3fe7b7c0006  ×216      ┐
    5c44f3fe7b7c0007  ×210      │ Lunalice 分段素体的 7 个 unit
    5c44f3fe7b7c0002  ×201      │
    5c44f3fe7b7c0003  ×151      │
    5c44f3fe7b7c0001  ×116      │
    5c44f3fe7b7c0004  ×84       │
    5c44f3fe7b7c0005  ×81       ┘
自链接 0 条
```

运行时日志（`Logs/ArmorOverrideRuntime.log`）每次都一样：

```
queued 2005 sets
queue drained (2005 sets, 0 rejected), total overrides=2005 (open the armoury to see it)
```

⇒ **引擎全收，包括那 948 条指向不存在 unit 的**。`set_resource_override` 不做存在性校验。

### 2.2 目标 unit 是否真的在"已部署"的游戏数据里

用 `RuntimeArmorResourceFilter.ReadDeployedUnitsAsync`（扫 `data\*.patch_*` 的真实 TOC，
会跟随 0 字节符号链接）扫了一遍：

```
已部署 unit 总数：91
缺失   def7dc54e86d6054      ← 隐藏 unit
存在   5c44f3fe7b7c0001 … 0007   （Lunalice 七件）
```

### 2.3 为什么隐藏 unit 不在

`LuaDeploymentService` 的模式分支（`:316-347`）：

| 映射模式 | 投放内容 |
|---|---|
| `ExternalLuaEntry`（外置入口） | `CreateArmorRegistryAddonAsync(...)`：**运行时 Lua + 隐藏 unit** 打成一个补丁 |
| `ExclusiveLuaEntry`（**本机当前**） | **只** `CreateArmorRegistryDocumentAsync(...)`：把运行时塞进 boot 文档，**没有任何隐藏 unit 补丁** |
| 真映射 | 不投放 Lua |

本机设置：`MappingModeSchema=2`、`ArmorMappingMode=1` ⇒ **独占 boot**；`PhysicsMode=1` 同为独占。
库里的外置模式产物 `E:\mod\.runtime\armor-registry-addon\40d746db…\9ba626afa44a3aa3.patch_0`
（11658 B）**停在 2026-11-01**，之后再没生成过 —— 与"11-11 切到独占模式"的时间线吻合。

`CreateArmorRegistryAddonAsync` 甚至已经支持 `includeRuntime: false`（注释写着"用户关掉独占映射 Lua 后，
映射仍然需要这个 unit 存在"），但**独占分支没有调用它**。

### 2.4 本仓自己写下的判据（原话）

- `WardrobeLinkCompiler.cs:24-28`：
  "**不能用"不存在的 id"——那会让引擎记住加载失败，之后军械库整页空白并崩溃。**"
- `LuaDeploymentService.cs:606-608`（同一个函数里）：
  "空 unit 必须真实存在于游戏里：运行时把"来源缺的槽位"指向它，指向不存在的 id 会让引擎记住加载失败，
  之后军械库整页空白并崩溃。"
- `notes/sectional-u0-findings-20261104.md:103` / `sectional-survey/C-placement.md:47,93` 同一条。

---

## 3. 崩溃现场（转储）

本机 `%APPDATA%\Arrowhead\Helldivers2\dumps\` 近期 10+ 个转储，按 `dump-digest.py` 归一后是**两类**：

| 位置 | 参数 | 出现时间 | 归属 |
|---|---|---|---|
| `helldivers2.exe + 0x3ef06b` / `+0x3e564b` | `0x0 0x0` | 11-11 晚间集中出现、11-12 14:00 | **物理运行时探针**那一次（已修，2.1.35） |
| `helldivers2.exe + 0x664db6` | `0x0 0x1xxxx…`（第二个参数像堆地址） | 11-11 22:52、11-12 13:19、11-12 15:05 | **另一类**，与物理无关；栈里只剩加载器/dbgcore 帧，无法直接定位 |

`dump-stack.py` 在这三份上取不到业务栈（只有 `runtime.dll`/`ntdll`/`dbgcore` 的残帧），
所以**目前没有"崩溃发生在资源加载"的直接证据**；但"另一个固定偏移、参数带堆指针"与
"资源加载失败"这一类形状不冲突，需要下面第 5 节的 A/B 才能确定。

---

## 4. 结论分级

**已证实（可直接复核）**

1. 独占 boot 模式下隐藏 unit 不投放；`wardrobe.links` 有 948 条指向它。
2. 运行时把 2005 条覆盖全部写进引擎，0 条被拒。
3. 运行时不在开页面时写入；覆盖表只增不减，删除只记"需重启"。
4. 本仓代码/笔记明确写着"指向不存在 unit ⇒ 军械库空白并崩溃"。

**强怀疑（需要 A/B 才能定级）**

5. 军械库 / 债券页崩溃 = 那 948 条悬空覆盖触发的资源加载失败。
   支持：崩溃偏移固定、与"打开类"操作吻合、用户主观关联。
   反对/未排除：如果悬空 unit 只在被"装备/预览"时才加载，那为什么开页面就崩？
   也可能叠加了"整目录被覆盖 ⇒ 一页几十个重资源同时加载"的内存尖峰。

**待验（下一步的测量点）**

6. 覆盖数量与崩溃概率是否相关（2005 条 vs 更少）。
7. `can_get('unit', id)` 能否在运行时用来提前跳过不可用目标（笔记 U8：startup 阶段恒 false，时机未定）。
8. 删除映射时覆盖表容量上限（日志里的 `rejected` 计数就是它）。

---

## 5. 下一步（建议顺序，均不改产品行为前先取证）

| 步 | 动作 | 判据 |
|---|---|---|
| **A/D** | **修投放**：独占模式也发隐藏 unit 补丁（`includeRuntime: false`），然后重开军械库 | 若不再崩 ⇒ 结论 5 成立；`ReadDeployedUnitsAsync` 里应出现 `def7dc54e86d6054` |
| 对照 | 若想先取证不改代码：把 `wardrobe.links` 里那 948 条隐藏 link 临时删掉、重启游戏，再开军械库 | 不崩 ⇒ 同样是结论 5 的证据（代价：那些件会照常显示原版外观） |
| **B** | **编译/投放期校验**：`WardrobeLinkCompiler` 或 `LuaDeploymentService` 用 `ReadDeployedUnitsAsync` 校验每条 link 的目标（**含隐藏 unit**），缺失就丢弃并报出来 | 这类问题以后在部署期就被挡住，不会留到游戏里崩 |
| **C** | **运行时加固**：`drain` 前用 `can_get` 跳过不可用目标并计数（注意 startup 恒 false 的时机） | 需要先做小实验确认 `can_get` 何时变 true |
| **E** | **可观测性**：`runtime.status` 记 `applied/total/pending_restart/removed_pending`，界面上能说"删映射要重启" | 对应 transmog 分析的缺口 #7 |

⚠️ 语义提醒：修好投放之后，那 948 个槽位会真的"什么都不画"（隐藏 unit 的设计意图）。
军械库/债券页因此会出现大量**空卡片**（不崩，但看着像丢了内容）。
这要不要换成"缺槽就不覆盖、保留原版外观"，是产品选择，得用户定。

---

## 6. 与物理那一轮的区别（别再混）

- 物理崩溃（0x3e564b / 0x3ef06b）是**我们 Lua 持有已销毁 unit 的引用**，2.1.35 已修。
- 这一轮的嫌疑是**资源覆盖指向不存在的 unit**，属于**部署/编译**层，运行时只是"照单全收"。
- 两件事都会在"切场景/换外观"附近出现，但转储偏移不同（`0x664db6` vs `0x3ef06b`），
  以后看 dumps 先按偏移分类。

---

## 7. 处置（2026-11-12，已落地）

### 7.1 用户定的口径

1. 隐藏 unit **部署成一个额外的 patch 文件**（外置 / 独占都一样：它必须真的在游戏数据里）。
2. **独占模式下它恒定是"倒数第二个序号"，而 lua（boot）补丁恒定是"最后那个最大的序号"。**
3. **不做**"把隐藏 unit 塞进 boot 补丁"的合并（见 7.3 的理由）。

### 7.2 实现

| 位置 | 改动 |
|---|---|
| `LuaDeploymentService`（独占分支） | 除了 boot 文档，**额外**产出"只含隐藏 unit"的补丁：`CreateArmorRegistryAddonAsync(..., includeRuntime: false)`，并把它的 `PlacementOrder` 设为 `int.MaxValue - 1` |
| `LuaDeploymentService`（boot 组） | `PlacementOrder` 固定为 `int.MaxValue` ⇒ 它恒为最大序号 |
| `ArmorRegistrySourceFingerprint(bool includeRuntime)` | 指纹**纳入"是否带 Lua"**：只投放 unit 的那份与"unit+Lua"的那份字节不同，共用一个缓存目录会互相覆盖 |

序号是 `ApplyPlanner` 按 `PlacementOrder` **递增分配**的（不是写死文件名），所以"顺序"才是正确表达：
现有 mod 的补丁先占 0..k，接着是 unit 补丁（k+1，倒数第二），最后是 boot 补丁（k+2，最大）。

测试（`HD2ModCore.Tests/RuntimeArmorRegistryServiceTests.cs`）：

- `LuaDeployment_WithoutBingus_BuildsDirectBootPatchFromInstalledGameResource`：
  独占模式必须产出 **2 个补丁组**；隐藏 unit 补丁里**只有 1 个条目**（unit 类型、`0xDEF7DC54E86D6054`，
  TocData 4240 B / GPU 418 B 与冻结模板逐字节一致）；boot 组 `PlacementOrder=int.MaxValue`、unit 组 `int.MaxValue-1`。
- `LuaDeployment_ExclusiveBoot_PlacesHiddenUnitPatchJustBeforeTheBootPatch`：
  真的跑一遍 `ApplyPlanner`，在"前面还有一个别人的补丁"的情况下断言**最终序号**是 `unit=1、boot=2`，
  且 boot 是该计划里的最大序号。

### 7.3 为什么没做成"一个补丁两个条目"（结论，避免以后重复讨论）

patch **是外壳**，能装任意条目 —— 这点用户已经明确。障碍在**写出器方言**，证据（本机真实产物）：

```
boot 补丁 : +0x04 typeCount=1  +0x08 entryCount=1
            +0xB8 payload 长度   +0xBC 资源版本(=1)   +0xC0 裸 chunk（无 8 字节前缀）
addon 补丁: +0x04 typeCount=?  +0x08 entryCount=2
            Lua 条目的 payload 自己带 8 字节前缀（u32 长度 + u32 版本），没有 0xB8/0xBC 尾标
```

也就是说"boot Lua 条目"和"标准 Lua 条目"是两种编码；`PatchArchiveWriter` 只会写标准那种，
拿 boot 补丁当源会把那条 Lua 条目重写成标准方言（丢尾标、payload 位移）⇒ 游戏可能直接没有我们的启动脚本。
要做合并就得给 boot 写出器加"多条目 + 保留 boot 尾标"的能力，并靠进游戏验证 —— 风险落在启动路径上，
暂不采用。**当前做法（多发一个 unit 补丁）是零风险的等价方案。**

### 7.4 验收（部署后一看就知道）

```
# data 目录里应当出现两个新的补丁，且 unit 在 boot 之前一个序号
Get-ChildItem "<游戏>\data" -Filter "9ba626afa44a3aa3.patch_*" | Sort-Object Name | Select-Object -Last 2
```

再用 `RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(gameData)` 扫一遍：应当能看到
`def7dc54e86d6054`（之前是缺失的）。之后开军械库 / 债券页看是否还崩。

