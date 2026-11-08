# 双面管理器评价：GUI 与 CLI 各自的病，以及「agent 能不能救场」

> 2026-11-06。本轮只做信息收集与评价，不改代码。
> 一手证据：我在**发布版安装目录**（`HD2ModManager\bin\Debug\net10.0-windows`）里跑 CLI，全部只读命令。
> 三个子代理分别做：CLI 盲测（A）、gamedata 库表审计（B）、mod 缓存审计（C），结论已并入。

> **目录名换算（2026-11-08）：** 全文写于 2026-11-06，其中的 `vpk2patch/` / `vpk2patch\` 一律指
> **今天叫 `sectional/` 的那个基底目录**（旧名读侧仍兼容，新写入只用新名）。这是路径名换算，
> 不是结论变化——按下面的规则读：讲**当时在哪**的照原文读换算即可；讲**今天该往哪写**的，
> 已在命中处逐条加了 `⛔ 已过时（2026-11-08）` 外壳。见 `docs/agent/sectional-compile-base-20261108.md` §1.3。

---

## 0. 结论先说

这套系统的问题不是"功能不够"，而是**同一件事有三套说法**：GUI 里叫「重构建」，CLI 里只有 `vpk2patch` + `vpk rekey` 两个零件，代码里又是 `VpkRebuildService` 一层包装；产品把 mod 分四种，CLI 只认 `standard`/`option`，Core 里**根本没有"种类"这个概念**。

所以：

- **GUI 用户**看到"杂而不精"——那是新概念没被建模、只散落在各处的目录探测里；
- **agent**（CLI 的真用户）看到一堆零件和一份 259 行清单，得自己猜哪几个零件拼成「重构建」；
- **出故障时**，最该救场的 CLI 恰好读不出那个坏文件，因为读路径比写路径严格。

关于你最关心的那个问题——**"发布版用户能不能靠 agent 通过 CLI 紧急修好简单问题"**——我的回答是：**现在只能修 2 个，半个能修 2 个，修不了 3 个。** 拦路的是缺诊断、缺配方、以及读路径比写路径严格，不是缺命令。

---

## 1. CLI 实测（我当一次拿不到源码的 agent）

### 1.1 入口在发布版是干净的；脏的是我们的开发目录

先纠正一个我自己差点误判的点。**发布版只有一个 CLI**：

```text
发布产物 <stage>\
    hd2mod.cmd        149 B   ← 优先 bin\hd2mod.exe，没有才退回根目录
    HD2ModManager.exe 9.3 MB
    bin\hd2mod.exe   11.1 MB   ← 唯一的 CLI
```

`.cmd` 的两级回退是特意写的（`if exist bin\hd2mod.exe goto :inbin`），发布版里命中 `bin\`，一切正常。**所以"入口不可信"在发布版不成立。**

**但开发目录是另一回事**——它同时存在两份，而且 `.cmd` 优先的那份是旧的：

| | 根目录 `hd2mod.exe` | `bin\hd2mod.exe`（`.cmd` 优先调用的） |
|---|---|---|
| 来源 | csproj `CopyHd2ModCliArtifacts` 每次构建刷新 | **9 月一次发布写进这个目录的残留** |
| 大小 / 时间 | 162 KB / **2026-11-05 23:20** | 8.88 MB / **2026-09-23 18:23** |
| commit | `bf47843f` | `f0d3debd` |
| 命令数 | **155** | **133** |

差的 22 条正好是**诊断与底层修复那一整层**：`game locate`、全部 `wardrobe *`、全部 `patch entries / entry / compose / edit / hide-unit`、`unit *`、`gamedata extract *`、`custom-model *`。

这条只影响我们自己的工作流（比如我这次派子代理盲测，它按"最自然的方式"调 `.cmd`，前半程用的就是 9 月的构建），不影响用户。**但值得花一行修**：构建后清掉残留的 `bin\hd2mod.exe`，或者让 `version --json` 带上"我是不是最新构建"的自检。否则下次盲测还会踩同一个坑，而"这个命令不支持"看起来像 bug、不像过期。

### 1.2 冷启动：路在哪

| 要搞清的事 | 能不能查到 | 怎么查到的 |
|---|---|---|
| 哪个是 CLI | ✅（发布版） | 发布版只有一个 `bin\hd2mod.exe`，`.cmd` 直达。开发目录另说，见 §1.1 |
| 服务于哪个游戏 | ✅ | `capabilities` 描述里有《绝地潜兵 2》 |
| 游戏装在哪 | ✅ | `game locate --json` |
| mod 库在哪 | ✅ | `status --manager-root <目录> --json` → `library.root = E:\mod` |
| **四个目录** | ⚠️ **3 个能查，第 4 个查不到** | `status` 给 manager / library / gameData；**C 盘编译目录没有任何命令报告**，全盘 settings 里也没有 `C:\` 路径（协议目录硬编码在 `LocalAppData`） |
| 库里有几个 mod / 什么种类 | 数量 ✅ / 种类 ❌ | `mod list` 给 55 条；`kind` 只有 `standard`(12) / `option`(43)。真实种类只能靠数磁盘：41 个 `vpk2patch.json`、8 个带 `vpk2patch\sections.json`（sectional）、2 个顶层 `.patch_0`（legacy）、4 个空目录（≈逻辑容器） |
| 有哪些命令 | ✅ 但地图不全 | `capabilities` 155 条带 `safety` 分级（read-only 67 / explicit-confirm 46 / explicit-export 36）；**但 6 个家族只在 `--help` 里、`capabilities` 里没有**（`source *`、`vpk rekey`、`vpk2patch`、`gamedata deployment`…），而 `decoration show` 两边都列着却**实测 `UnknownCommand`** |
| 出错了看哪 | ❌ | `doctor` / `verify` / `diagnose` / `search` / `settings` **全部 UnknownCommand**。日志在两处（`<manager>\logs\` 与 `%LOCALAPPDATA%\…\Logs\`），CLI 一处都不提 |

**一条最刺眼的对比**，同一 CLI、同一天、两种态度：

```text
$ hd2mod status --json
{ "mode": "read-only",
  "manager":  { "root": null, "settingsPath": null, "settingsExists": false },
  "library":  { "root": null, ... },
  "gameData": { "root": null, ... },
  "issues": [] }              ← 全是 null，却一条 issue 都不报

$ hd2mod mod list --json
{ "ok": false, "error": { "code": "LibraryNotConfigured",
  "message": "未能确定 Mod 库。请显式传入 --library <Mod库目录>，或传入包含 settings.json 的 --manager-root <管理器目录>。" } }
```

`mod list` 的报错是我见过最标准的一条：错误码 + 两条具体出路。而 `status`——agent 第一个会敲的命令——返回"一切正常（issues 空）"，其实什么都没配置。

**根因只有一行**：`hd2mod.exe` 就住在管理器目录里，`--manager-root` 却不默认成自己的目录。补上这一条，`status` / `context export` / 所有需要 manager-root 的命令立刻自举。

### 1.3 `context export` 是最好的东西，但它被埋了

`hd2mod context export --library <库> --output ctx.json` 产出 145 KB 一份 JSON：`paths` / `manager` / `library` / `capabilities` / `mods`(55) / `profiles` / `issues`。**这就是 agent 要的地图。** 但是：不在 `capabilities.workflow` 里，帮助里排倒数第几行，描述六个字，且必须显式给 `--library`（否则 `paths.libraryRoot` 也是 null）。

### 1.4 词汇对不上：产品有四种 mod，CLI 只认两种

```text
$ hd2mod mod show 178ba581 --library E:\mod --json
{ "mod": { "kind": "option", ... "isLogicalContainer": false } }
```

这个 `178ba581` 是 `【2k材质】恋星milfy`，磁盘上：

```text
【2k材质】恋星milfy\
    【2k材质】恋星milfy.vpk          ← 来源
    vpk2patch.json                   ← 转换报告
    vpk2patch\9ba626afa44a3aa3.patch_0(+.gpu_resources)   ← 产物
    option.json
```

**一个标准的 vpk2patch mod，CLI 报 `kind: "option"`，一个字的 vpk2patch 都没有。** 它报的是"节点角色"，你要的是"存储种类"——两个轴，只暴露一个，还叫 `kind`。

代码侧：`ModNodeKind` 只有 `Standard / Option / Logical / Decoration(废弃)`，**Core 里没有"存储种类"这个概念**；legacy/sectional/vpk2patch 是各服务**每次去看文件夹里有没有 `vpk2patch\` 现推的**。

> 这就是"老功能新功能混杂"的技术定义：**新概念没被建模，只是散落在各处的目录探测。** GUI 每个页面自己判一次，CLI 干脆判不了。

### 1.5 诊断会说假话：健康的 mod 被报成"存储不完整"

```text
$ hd2mod mod analyze 178ba581 --library E:\mod --json
  "patchGroups": [],
  "diagnostics": [
    { "code": "ModAnalysisPatchGroupsMissing", "message": "…未发现可解析的 .patch_n 主文件；…**或其存储已不完整**。" },
    { "code": "ModAnalysisRegisteredPatchMissing", "message": "library.json 登记的 Patch 在当前 Mod 目录中不存在：9ba626afa44a3aa3.patch_0。" }
  ],
  "isReady": true
```

这个 mod 完全健康——patch 就在 `vpk2patch\` 里。`mod analyze` 只看根目录，于是：一个**想帮忙的 agent** 会读到"存储已不完整"，然后去"修"一个没坏的 mod；它同时说 `isReady: true`，两条警告和"就绪"自相矛盾。

**这比报错更危险：报错会让人停下来，假警报会让人动手。**

### 1.6 配方只活在 GUI 里

你要的能力——"GUI 点了 X，agent 要知道它对应 CLI 的哪条工具链"——**现在不存在**。我把 GUI 的动作枚举（`PageActionKind`，30 种）逐条对到 CLI：

| GUI 动作 | CLI 等价物 | 判定 |
|---|---|---|
| 新建/切换/激活/改名/删除配置、部署/取消部署、导入、校验库、导出、新建逻辑 Mod、改名/备注/改图、修复 patch、资源重打包、跨护甲、头盔合并、生成自定义素体 | `profile *`、`deploy *`、`import *`、`library sync`、`mod export`、`mod create/rename/notes/image`、`patch repair/repackage`、`equipment *`、`custom-model convert` | ✅ **18 个动作有干净对应** |
| 刷新 / 打开文件夹 | 无需（读命令天然实时；`context export` 已给 `fullPath`） | — |
| 详细表格 | `mod analyze`（对新种类误报，见 §1.5） | ⚠️ 质量不行 |
| 头身合并 | 只有 `vpk rekey --head-merge on` 这个参数 | ⚠️ 藏在参数里 |
| 分件压缩（Compact） | 无 | ❌ |
| **用来源重构建（单个）** | 无 —— GUI 里是 `VpkRebuildService` → `vpk2patch --reuse-model` + `vpk rekey` + 骨骼嵌入 + 报告 | ❌ |
| **一键重构所有 VPK** | 无 —— GUI 里是 `VpkBulkRebuildService`（并发 5） | ❌ |

> ⛔ 已过时（2026-11-08）：表里「头身合并」那一行**反过来成立了**——GUI 的这个开关**已删除**
> （详情页不再提供这个条件），只剩 `vpk2patch --head-merge` / `vpk rekey --head-merge`
> 两条 CLI 实验 / 救急参数。常规链路里没有头身合并这个条件。
> 见 `docs/agent/sectional-compile-base-20261108.md` §4。

**7 个缺口，全在"新功能"那一侧。** 不是巧合：老功能当初是 CLI 驱动 GUI（`Hd2ModProcess` 调 CLI），新功能反过来变成 GUI 自己写服务、顺手调几个 CLI 原语。**配方沉淀在 GUI 代码里，CLI 只剩零件。**

而 agent 拿到的 `vpk2patch` 帮助是 60 行散文（`--matcap` / `--id-namespace` / `--reuse-model` / `--cutout` / `--normal-flip-y`），信息量足——**危险项埋在段落中间**：`--id-namespace` 不传会"和库里其它导入撞名"，这句排第 30 行。

### 1.7 agent 友好度的四个硬伤

1. **没有单命令 `--help`**。`hd2mod mod list --help` → `不支持的命令：mod list help。请运行 hd2mod help。`——`--help` 被当成命令 token 吞掉，然后把你指回 22 KB 的全局帮助。**冷启动最贵的一步就是这里。**
2. **"下一步"不是字段，是散文**。候选值塞在 message 里：`"Mod GUID 前缀不唯一：6。候选：60fb4056-…, 61389641-…"`。没有 `suggestedCommands[]` / 结构化 `candidates[]`。退出码不统一也无文档（usage=2，其余=1）。
3. **上下文炸弹**：`armor-registry status` **91 KB**、`armor-registry show` **83 KB**、`mod list` **82 KB**，全部无分页、无摘要模式（只有 gamedata 系列有 `--limit`）。一次调用能吃掉大半上下文。
4. **技能文档重复且有一对内容不一致**：59 个目录 / 32 个技能，26 对字节完全相同；`cross-armor-transfer`(3129 B) vs `hd2mod-cross-armor-transfer`(1611 B) 内容不同，而 manifest 指向**短的那份**。`mod-management` 被标 retired（这点做得对）。

---

## 2. 应急修复能力（回答你的问题）

假设发布版用户撞上我们这两周修过的故障，他身边的 agent 只有这个 CLI（子代理 A 实际逐条试过）：

| # | 故障 | 能修吗 | 命令 | 卡在哪 |
|---|---|---|---|---|
| 1 | 素体名变 `custom-xxxx` | ❌ | 无 | 名字在 `<mod>\vpk2patch\custom-model.json` 的 `name`。没有 `custom-model rename`；`custom-model convert` 会**重造 unit id**，等于毁掉已有映射——**这是个陷阱** |
| 2 | 应用映射"237 条全跳过" | ✅ 4~5 步 | `gamedata index inspect`（看 `advancedEquipmentFactsCurrent`）→ `index plan units-all` → `validate` → `apply --confirm` → 重写 spec → `armor-registry apply` | 需管理器关闭；`armor-registry show` 一次吐 83 KB，237 条要人工重建 spec。**但"基底过期"和"权威表过期"这两条病因，没有任何命令能区分** |
| 3 | 映射页一片「读取失败」 | ⚠️ 半 | `armor-registry status` 能报 `ArmorRegistryProtocolInvalid` + 行号 | **没有 reset/restore**；`backup\` 里只有 dl_bin 原件，没有协议备份；协议坏了 `show` 也读不出旧映射 |
| 4 | mod 看得见但不生效 | ✅ 3 步 | `deploy plan active` → `validate` → `apply --confirm`；诊断用 `gamedata override status` / `override list --only-competition` / `gamedata browser` | 报错质量好（`code/severity/blocking`）；但"用 browser 对比四层状态"这句埋在文档最后一行 |
| 5 | 库元数据加载失败 / 库看起来空 | ❌ | `library sync inspect` 能修"记录漂移" | `library.json` 本身坏了 → `UnhandledError` + `Library data is invalid JSON.`——**英文、无路径、无行号、无建议**，也没有"从磁盘重建"的命令 |
| 6 | sectional 组件树不显示 | ✅ 1 步（落点要猜） | `wardrobe sections "<…>\vpk2patch\9ba…patch_0" --output "<…>\vpk2patch\sections.json" --overwrite` | 重生成的 schema 与库内一致、实测可跑；但 CLI **不告诉你**管理器期望的落点是 `vpk2patch\sections.json`，也不走库租约 |
| 7 | 真映射关不掉 / 开了没效果 | ❌ | 只能诊断（`armor-registry status` 看 `runtimeProcessRunning`） | 开关是 `settings.json` 的 `"ArmorMappingMode": 2`，**CLI 没有 settings 读写**；`armor-registry` 明确声明"never edits dl_bin"。改开关必须开 GUI |

> ⛔ 已过时（2026-11-08）：表里 `<mod>\vpk2patch\...` 这类**落点路径**的目录现名是 `sectional/`
> （`vpk2patch/` 作旧名，读侧仍认；新写入一律用新名）。第 1 行的 `custom-model.json`、第 6 行的
> `sections.json` 都在这条规则下。第 6 行"CLI 不告诉你落点"这个缺陷本身仍成立，只是落点换名了。
> 见 `docs/agent/sectional-compile-base-20261108.md` §1.3。

**2 个能修，2 个半个，3 个不能。** 四条结构性原因：

1. **GUI 的按钮没有 CLI 对应物**（§1.6 那 7 个缺口），尤其是「重构建」。
2. **没有 doctor**：`status` 是空壳；`context export` 只枚举不诊断。唯一像样的诊断是 `gamedata status`（给了 `state` / `sourceFingerprint` / `diagnostics: []`）——**这个形状应该成为所有诊断命令的标准**。
3. **读路径严格、写路径宽松**：坏掉的派生文件在 agent 最需要它的时候拒绝工作（与上一轮审查的 A 级问题同源）。
4. **能修的路径是"手改 JSON"，而 schema 没有对外文档**。agent 不怕改文件，怕不知道字段含义。

---

## 3. 四个模型的评价

### 3.1 四目录分离 —— 方向对，但没有被强制

| 目录 | 实测 | 评价 |
|---|---|---|
| 管理器目录 | `data\indexes\asset-index.sqlite`(113 MB) + `mod-facts.sqlite`(21 MB) + `hidden-unit-cache`(91.5 MB) + `logs\` + `data\resources\*` | ✅ 符合 |
| mod 库 | `library.json` / `profiles.json` / `option-activations.json` / 每个 mod 的 `custom-model.json` / `.hd2modmanager\` | ✅ 符合 |
| C 盘编译目录 | `armor-registry.protocol` / `runtime.status` / `locator.cache` / `.manager.lock` | ✅ 符合，**但 CLI 完全不知道它的存在**（硬编码在 LocalAppData，无命令报告） |
| 游戏 gamedata | 只部署 patch + 更新时建索引 + 真映射改 dl_bin | ✅ 符合。`GameDataTargetGuard` 对 junction/网络盘的拒绝是唯一越界处（上轮审查 B6） |

**问题不在划分，在没有代码级守卫**：没有机制阻止某天某个新功能往游戏目录里写点别的。建议把四目录做成**唯一路径服务**，所有写入必须经过它，越界即启动期失败——而不是靠 review 记住。

### 3.2 四种 mod —— 概念对，但没建模（§1.4）

**建议**：引入唯一的存储种类判定（`ModStorageKind Resolve(modDir)`），四个值、一个实现，全仓库只有它看目录结构。然后：`library.json` **不存**它（它是目录的函数，存了就会漂）；`mod show` / `mod list` / `context export` 都输出它；GUI 的功能可见性由它决定（你的功能矩阵直接变成一张 switch 表）；`mod rebuild` / `wardrobe` 只对 sectional|vpk2patch 开放。

这一条同时解决"杂而不精"和"CLI 看不懂 mod"，因为**它们是同一个病**。

### 3.3 病不在双面，在双语义

同一份文件、同一天：

| | GUI | CLI |
|---|---|---|
| 注册协议里有不认识的字段 | 跳过坏行，继续 | 抛错（CLI 比 Core 严） |
| 部署前是否要求"归属证明" | 不要求 | 要求，缺证明永久 Blocked |
| mod 选择器 | 名称 / GUID | 只收 GUID 前缀（`profile` 却收名称） |
| `kind` 取值 | 按种类给功能 | 只有 `standard`/`option` |

**Core 只能有一套判定，两个前端只做呈现与确认的差异。** 现在等于两个产品共用了一半代码。

---

## 4. gamedata 库审计（子代理 B）

库：`data\indexes\asset-index.sqlite`，**113.6 MB**，15 表 + 24 索引，freelist 2,860 页（≈11 MB，10%）= 反复 DELETE+INSERT 的 churn。

### 4.1 一句话

**真正被读的只有 6 张表；另外 4 张写进去从没人读（24,704 行），4 张是空表；而 357,695 行的 `archive_entries` 有三份不同粒度的物化副本，占了全库约一半体积。** 外加记账与内容能互相矛盾（当前磁盘就是"记账说高级索引已完成、表里 0 行"）。

### 4.2 表清单要点

| 表 | 行数 | 归类 |
|---|---|---|
| `archive_entries` | 357,695 | 游戏原始事实（TOC） |
| `asset_archives` | 357,695 | **派生：与 entries 双向 EXCEPT 均 0 行** |
| `assets` | 99,276 | **派生：`df` 与 entries 计数不符的行数 = 0** |
| `archives` | 3,567 | 原始事实 + 命名 |
| `game_data_archive_names` | 16,532 | **写不读**（其中 1,049 行 `und` 与 `archives.display_name` 逐行相同） |
| `archive_domains` | 3,567 | **写不读**，启发式（全部 `derived:content-path` / confidence=20） |
| `game_data_authority_kits` / `_pieces` | 422 / 2,080 | 游戏原始事实（注册表）✅ 规范化得对，**不要合** |
| `game_data_authority_resources` | 3,940 | **写不读** |
| `game_data_authority_entities` | 665 | **写不读**（含 ≈350 KB 多语言 JSON） |
| `game_data_unit_parts` / `_stream_layouts` | **0 / 0** | 派生，**空**（第二次动作才填） |
| `archive_issues` / `authority_issues` | 0 / 0 | 诊断，空 |
| `meta` | 18 | 记账 |

### 4.3 可删/可合并（带证据）

1. **`asset_archives` 删表** —— 完全等于 `archive_entries` 的投影，双向 EXCEPT 都是 0 行。
2. **`assets` 删表** —— 完全可算（df 不符 0 行）；全库只有一处读它的一列（`AssetArchiveIndexService.cs:1363`，供阈值过滤）。
3. **`archive_entries.df` 删列** —— 357,695 行全部 = 1（插入时硬编码），而同名 `df` 在 `assets` 里表示"重复份数"。**命名陷阱**。
4. **`game_data_archive_names` / `archive_domains` / `authority_resources` / `authority_entities`**：全库无 SELECT，要么接上、要么删。建议保留**一张** `localized_name(subject_kind, subject_id, locale, name, source, confidence)` 统一收口，其余删。
5. **同一问题两条 SQL 路径**：`GetArchiveMetadataForAssetAsync`（走 asset_archives）与 `GetSharedArchivesAsync`（走 archive_entries）问的是同一件事。
6. **命名数据被存了 4 份**：`archives.display_name`、`kits.display_name`（构建时冻结的单语言）、16 语言表、`entities.localized_names_json`——后两份没人读。**正好反了**：该多语言的没读，该单语言的冻在构建时。

### 4.4 "一次重建够不够"：不够，而且现在磁盘是坏的

- 基础重建写 11 张表；**不写** `game_data_unit_parts` / `game_data_stream_layouts`（只有 `AdvancedEquipmentIndexService` 写，即"第二次动作"）。
- GUI 主按钮现在连着做两半（`PageViewModels.cs:2220→2247`），CLI 也连着，VPK 导入时也会补建——**但服务层仍是两个入口**，别的调用方仍可能只做一半。
- **当前磁盘实况**：`meta` 里 `advanced_equipment_index_version=equipment-v7` 且指纹与基础一致（"已完成"），两张表却是 **0 行**。原因链：`ClearIndexDataAsync` 删了这两张表（`SqliteSchema.cs:370-371`）却**不删 `advanced_*` 记账键**；且 `AdvancedEquipmentIndexService.cs:143` 吞掉每个 unit 的解析异常后仍写"完成"。

> **这是"记账可以永久骗人"的活样本**。修法：记账与数据同一事务写；或干脆不写"完成"键，只留"扫表判断"。

### 4.5 在线依赖：只有一处，且离线完全可用

`GitHubAssetMetadataSyncService` 拉 4 个文件（`archivehashes.json` / `friendlynames.txt` / `typehash.txt` / `bonehash.txt`），**全部随仓库分发**（`HD2ModManager\Resources\`，合计 ~400 KB），同步失败只返回 Failed、不删本地文件。离线后果：只丢**显示名**（退回哈希），`typehash` 连影响都没有（有内嵌副本）。

判断：**本地算得出的**——archive 清单、TOC、unit/asset 身份、护甲套装/件/资源、15 语言装备名（游戏自带 strings archive，已解析）；**必须外部的**——哈希→人类可读名的逆向字典（社区逆向积累）、图标。

### 4.6 对"单表更好"的评价

**对的一半**：同一**实体**的事实应在同一处，少一次 join、少一处过期副本。这套库的痛点恰恰是"同一事实存了 4 份"。

**错的一半**：**单表 ≠ 单实体**。113 MB 单宽表会带来：(a) 写放大（重建 = DELETE 全表 + 重插 357k 行，freelist 已 10%）；(b) 索引膨胀（每个二级索引复制一份键列）；(c) 迁移困难（本库已在用三种补丁手段）；(d) 语义污染（`df` 一个名字两种含义就是现成例子）。

**可执行的合并准则**：

1. **有 reader 才配存在**——发布前跑"0 引用即删"检查。
2. 能零误差 `GROUP BY`/`DISTINCT` 重建、且重建成本 < 一次查询 → **用视图，不物化**。
3. 只被一条查询取一列的表，不建表。
4. **身份事实 / 派生聚合 / 记账必须分表**，且记账与数据**同一事务**写。
5. 随设置变化的显示值（locale）不得与身份同表：identity 一列，名字进一张 `(subject, locale, name, source)` 表。
6. 只有列数稳定、无 NULL 填充、无 JSON 列时才允许合宽表。

**一句话**：你要的"检索全面"靠**视图**实现，存储仍然分表。这样"检索方便"和"改动安全"同时成立。

---

## 5. mod 条目缓存审计（子代理 C）

### 5.1 现状

按 mod 落盘的派生缓存**4 套，约 9 MB**（`mod-information` 493 文件/4.3 MB、`mod-data-index` 201 文件/1.4 MB、`mod-facts.sqlite` 3.4 MB），外加按 unit 存的 `hidden-unit-cache`（12,933 文件 / **91.5 MB**）。

### 5.2 同一份事实被存了几份

以"这个 mod 提供了哪些 unit/资产"为例——**5 份**：

1. patch 字节（权威）
2. `mod-information/AssetInventory_<node>_<gen>.json`
3. `mod-facts.sqlite` 的 `patch_groups` + `mod_assets`
4. `mod-data-index/<node>.json`（逐条 `ModDataIndexEntry`）
5. 内存里还有两份（`ModLibraryService._derivedData`、`DerivedStateCoordinator._snapshot`）

「引用关系」3 份、「缩略图」3 份、「父子关系」2 份（`library.json.hostModGuids` / `option.json`）、「patch 清单」3 份。

### 5.3 建议删（按安全性）

| 对象 | 证据 | 影响面 |
|---|---|---|
| **`MaintenanceAnalysis` 整条链** | 全仓 grep：**零生产调用方**（只有接口、实现、两个测试替身）；实测落盘文件数 0 | **无**（无界面、无命令）。顺带 `ShellViewModel.cs:1798` 一句中文映射 |
| **`mod-data-index/*.json`** | 唯一活着的读取是详情页一行字；3 个查询方法零调用方；数据与 `mod-facts.sqlite` 逐条重复 | 详情页「跨 Mod 索引」一行，改查 `SqliteModFactsStore` 即可（已有同批数据的 SQL 版） |
| **`mod-information/Thumbnail_*.json`** | 字段就是"图标路径 + 长度 + mtime"，一次 `File.Exists` 现算 | 首次加载 +0.1 ms 级；`cache/thumbs` 命中逻辑完全不依赖它 |
| **`mod-facts.sqlite` 两张 `*_snapshots` 表** | 只有建表/删行/一次性迁移读，**没有 INSERT**；里面存的是整份 facts 的 JSON 副本 | 无（要写成 DROP 迁移） |
| **三件套死代码** | `ModInformationPropertyKind.DecorationDefinition`（无 producer/consumer）、`StoragePaths.DecorationActivationsPath`（零读写）、`ModAssetRoleFactsService`（工厂从未被调用） | 无 |
| **44 个孤儿 generation** | 201 个节点却有 245 个 `AssetInventory_*.json`；GC 按节点存活判定、不按 generation 新旧 | 0.4 MB + 少 44 次目录枚举 |

### 5.4 必须保留

**🔴 删了会丢用户输入**：`library.json`（改名/备注/Kind/HostModGuids/OptionOrder）、`profiles.json`、`option-activations.json`、`pending-deletions.json`、`custom-model.json`（素体 key 是随机 GUID，丢了找不回）、`sections.json`/`components.json`/`wardrobe.json`/`vpk2patch.json`、`data\mod-replacement-backups\**`。

**🟡 派生但重建极贵**：`mod-facts.sqlite`（3 张活表）、`asset-index.sqlite`、`hidden-unit-cache`、`mod-information` 的 AssetInventory + ReferenceGraph + FileFacts、`cache/thumbs`。

### 5.5 `custom-model.json` 的三份副本（与你的新口径的关系）

**方向已经对了，只是旧副本没删干净。**

- **读：磁盘赢。** `RuntimeArmorRegistryService.cs:377-391`——先写 mod 目录的素体，工作区只 `TryAdd` 补缺。
- **但写路径把合并结果又写回工作区**（`CustomModelOwnershipService.cs:56`），于是工作区持续累积一份冗余副本。
- **症状证据**：`CustomModelSectionalCompaction.cs:108-112` 不得不**绕过快照**、单独调 `ReadWorkspaceModelsAsync` 才能判断"工作区里到底有没有这个素体"——因为快照已被磁盘污染。

**建议**：一次性迁移把 workspace 里的 `model_definitions` 清空，所有 `SaveWorkspaceAsync` 只写 `Mappings`/`PieceOverrides`。`CustomModelCatalog.cs:70-85` 的注释已经承认了这一点（"以前把同一份数据再存一份…于是出现两个真相"），只是代码没改完。

**两个模糊地带**（子代理不替你定，我也不替你定）：
1. **`option.json`**：写得勤（每次关系变更），只在导入时读一次，运行期父子关系全走 `library.json`。按你的口径它是权威结构文件；按"唯一读者是导入器"它是冗余镜像。**建议保留但停止每次同步**——它是 `library.json` 丢失时唯一的结构恢复源，直接删会让"快照损坏"从可恢复变成不可恢复。
2. **`ModInformationPropertyKind` 的枚举值**：删值会改变数值，而该文件注释明确写着"数值会出现在日志里"。建议**保留枚举值**、只删 producer 与注册表条目。

---

## 6. 对你几个观点的批评

### 6.1 「大多数数据都在一个表里，检索更全面、管理更方便」——一半对，一半要改

见 §4.6。补一句最关键的区别：**你要消灭的是"同一事实的第二份物化副本"，不是"表太多"。** 而"游戏原始事实 / 派生聚合 / 记账"三者**生命周期不同，合表会带来一个致命后果：重建索引时把记账冲掉**——那等于给派生数据发了稳定性承诺，正好违反你自己定的原则。

### 6.2 「移除对社区在线资产索引的依赖」——好消息：已经基本做到了

四张 hash 表**已经 vendor 进仓库**并随包发；联网只剩 `assets metadata plan <url>` 一条，是 `capabilities` 里唯一的 `explicit-export-network`，validate/apply 永不重新下载；`asset-metadata-manifest.json` 老实记着来源和日期。

所以判断要修正为：**依赖已降级为"可选更新通道"。** 真问题是两条：

1. **没人告诉你它过期了**。快照停在 `2026-08-25`（今天 73 天）。游戏更新后新 hash 查不到名字时，界面只显示一串 hash，**不说"你的名字表是 8 月的"**。
2. **帮助里它排第一屏**，让 agent 以为联网是常规路径。

**建议**：把快照日期放进 `gamedata status` / `doctor`；联网命令在帮助里下沉到"可选维护"。**别删在线通道**——它是唯一能跟进游戏更新的手段，删了会把"名字过期"变成永久状态。

### 6.3 「legacy 只保留 mod 级功能，所以大量缓存可删」——方向对，有个陷阱

- 你说得对：`mod analyze` 已经"直接调 analyzer、不写状态"，口径已存在，只是没贯彻。
- **陷阱**：legacy 的「详细表格」「修复 patch」**仍然要读 patch 内容**。删掉持久化缓存后它们会变慢（但可现算）。
- 判据：**"这个缓存是为了省 CPU，还是为了记住用户输入？"** 省 CPU 的都可删（或改成按需算 + 内存缓存）；记输入的绝对不能删。

### 6.4 「素体是 mod 的字段」——同意，但要允许复数

- 这条直接解决"名称丢失"的根因，我完全同意。
- **但"一个"是隐患**：一套服装 + 一个独立头部、或一个包里两套配色，都是**一个 mod 多个素体**的真实情况。建议 `custom-model.json` 允许**数组**，默认长度 1。
- **顺序要求**：素体是 mod 的字段 ⇒ 删 mod 会连带删素体 ⇒ **必须先解散引用它的映射，再删素体**。`CustomModelReconciler` 的"首次只标记、下次才解散"正是这个顺序，别在新模型里丢掉。

### 6.5 「mod 条目全部平铺」——同意，但有个一致性风险

平铺意味着**目录名就是物理身份**。改名 = 移动文件夹 = 所有记着 `relativePath` 的地方都要同步。建议：目录名不承载身份（`<GUID>` 或 `<GUID>-<名字>`），或 `relativePath` 只作缓存、每次扫描以 GUID 为准重建。否则"改名"迟早制造孤儿。

### 6.6 「把所有人类用户都视为傻瓜」——对 GUI 成立，对 CLI 要换一个字

CLI 的用户是 agent。agent 不是傻瓜，它的问题是**没有任何上下文**。所以它要的不是"更简单的措辞"，而是：可机读的自描述（缺 `example` 和前置条件）、幂等的探测（`status` 必须能自举）、可组合的原语（`patch entries/entry/compose/edit/hide-unit` 那组做得很好）、一条能自证的诊断链（`doctor`）。**措辞再简单，agent 也不会"觉得好用"；它要的是不用猜。**

---

## 7. 建议（按收益/风险排序，本轮不实施）

### 第 0 档：改几行，收益最大

1. **清掉开发目录里残留的 `bin\hd2mod.exe`**（构建后清理，或让 `version --json` 带构建自检）。发布版没这个问题，但我们的开发目录会让 agent 用到 9 月的构建。
2. **`--manager-root` 默认成 exe 所在目录**；`status` 什么都没配时**必须报 issue**，不许 `issues: []`。
3. **每条命令一个 `--help`**（参数 + 最小示例 + "成功之后通常接哪条"），并让 `--help` 不再被当成命令。
4. **帮助第一屏指向 `hd2mod-docs\SKILL.md`**（32 技能路由表质量很高，现在没人知道它在）；把 `context export` 提到第 2 行。
5. **地图对齐**：补 `capabilities` 缺的 6 个家族，删掉未实现的 `decoration show`。**地图不可信，agent 学到的第一课就是"别信 capabilities"。**

### 第 1 档：把"种类"建模（治"杂而不精"的根）

6. 一个 `ModStorageKind` 判定 + 一张功能矩阵 switch；三个命令都输出它；GUI 功能可见性由它决定。
7. 修 `mod analyze` 的假警报（认 `vpk2patch\` 子目录；`isReady` 与 `diagnostics` 不许自相矛盾）。

### 第 2 档：让 CLI 能救场

8. **`hd2mod doctor --json`**：四目录（自举）+ 版本 + 库完整性 + 索引新鲜度 + 护甲注册表一致性（**编译基底 vs 当前 dl_bin vs 工作区**）+ 素体目录状态 + 待删除台账 + hash 表快照年龄；每个问题附**一条可执行修复命令**。形状照抄 `gamedata status`。
9. **`hd2mod repair` 家族**：`workspace`（坏行跳过/重建）、`custom-model-names`（按 unit id 找回）、`library-snapshot`（改名留档 + 重扫 + 保 profiles）、`index`。全部 plan/validate/apply + `--confirm`。
10. **`hd2mod mod rebuild <GUID> [--all]`**：把 GUI 那条配方固化成一条命令（`vpk2patch --reuse-model` + `vpk rekey` + 骨骼嵌入 + 报告）。
11. **补三条缺失的写命令**：`custom-model rename`、`settings get/set`（至少 `ArmorMappingMode`/`PhysicsMode`/`DeployLuaMods`）、`armor-registry restore|reset`。**这三条正好覆盖故障 1、3、7 —— 目前只能开 GUI。**

### 第 3 档：缓存整理（两个方向相反，别一起做）

12. **gamedata：先删"第二份物化"**（`asset_archives`、`assets`、`archive_entries.df`），**再把 4 张 0-reader 表接上或删掉**，**然后记账原子化**（`ClearIndexDataAsync` 同步清 `advanced_*`）。这三步零语义风险，省约 1/4 体积。
13. **`unit_parts`/`stream_layouts` 纳入同一次重建**（现在服务层两个入口，别的调用方仍可能只做一半）。
14. **mod 条目：删死链**（`MaintenanceAnalysis`、`mod-data-index`、`Thumbnail_*.json`、两张 snapshots 表、三件套死代码）。
15. **`custom-model.json` 停写 workspace 的 `model_definitions`**（一次性迁移 + 先补写缺失的 `custom-model.json`，再停写）。这条是唯一"中等风险"的，要迁移脚本和回滚。

---

## 8. 需要你拍板

1. **开发目录要不要在构建后自动清理 `bin\` 残留**？发布版不受影响，但不清就会反复让 agent（包括我派的子代理）用到旧构建。
2. **`doctor` + `repair` 要不要做**？这是"用户能不能靠 agent 救场"的分水岭。
3. **`ModStorageKind` 建模**要不要现在做？它是 §1.4 / §1.6 / 功能矩阵三件事的共同前置。
4. **素体允许复数**（`custom-model.json` 单值 → 数组，默认 1）——同意吗？
5. **`option.json` 停止每次同步**（改成只在导入/导出时写）——同意吗？它现在写得勤、读得少，但它是 `library.json` 损坏时唯一的结构恢复源。
6. **`ModInformationPropertyKind` 的枚举值**：保留数值（日志兼容）还是接受一次日志不兼容？

---

# 9. 二次沟通：你的回答与我的核实（本节取代 §6.2–§6.6 的对应结论）

## 9.1 社区在线资产索引：它到底还能提供什么

我把四个文件和它们的消费者都查了一遍。

| 文件 | 提供什么 | 本地能否替代 | 移除后的损失 |
|---|---|---|---|
| `archivehashes.json` | archive 的**分类**（Armor/Helmet/Cape/Weapon）+ **显示名**（社区结论） | 部分。`ArchiveContentProfiler` 能从内容路径推分类，**但那个信号本身来自 friendlynames** | gamedata 浏览器里 archive 标题退成哈希，分类退成结构描述 |
| `friendlynames.txt` | `file_id` → **内容路径**（如 `characters/…/foo.unit`） | **不能**。游戏文件里只有哈希，没有路径名 | 浏览器里"这个文件是什么"退成哈希；连带 `archive_domains` 的启发式失效 |
| `typehash.txt` | 类型哈希 → 类型名 | **能**。`AssetTypeNameTable` 已有内嵌副本 | **零** |
| `bonehash.txt` | 骨骼哈希 → 骨骼名 | 不能 | 只有一条 CLI 命令受影响，显示哈希 |

**关键事实**：用户真正会看的装备名——护甲、头盔、披风、武器、单位、实体——**全部来自游戏自带的 strings 表**，和社区文件无关。`GameDataAuthorityProvider` 里四个常量写得很清楚：

```text
LocalizedNameSource = "GameData.generated_customization_armor_sets+strings"
WeaponNameSource    = "GameData.generated_weapon_customization_settings+strings"
UnitNameSource      = "GameData.generated_unit_customization_settings+strings"
EntityNameSource    = "GameData.generated_entities+strings"
```

所以社区文件的**作用范围只剩 gamedata 浏览器的标签**，而且没有一个功能依赖它：

- `IsEquipmentCategory` 只用在**一个起名启发式**里（`AssetArchiveIndexService.cs:936`）；
- 浏览器的分类筛选下拉框会从 `Armor / Helmet / Cape / Weapon` 退化成 `UnitBundle / MaterialPack / TexturePack`——**仍能用，只是更粗**；
- 搜索仍然按 `DisplayName / PackageName / Category` 工作，名字变哈希不影响可用性。

**结论：可以移除，代价是"人话标签"变粗，没有功能损失。** 但建议**分两步**，别一步到位：

1. **先切断联网**（零损失）：删 `GitHubAssetMetadataSyncService`、`assets metadata plan/validate/apply` 三条命令、GUI 的更新入口；把 `asset-metadata-manifest.json` 的语义从"可更新的来源"改成"随包快照，不更新"。这一步直接消灭你说的两个问题：**用户连不上 GitHub**、**你总忘记更新**。
2. **再决定删不删文件**：4 个文件合计 ~400 KB。删掉只省体积；留着还能让老用户的浏览器显示人话。**若删，要确认"分类全空"时浏览器不崩**——现在 `CategoryFilters` 只有"全部"一项，看起来是安全的。

**顺手一个副产品**：`typehash.txt` 有内嵌副本（`AssetTypeNameTable.cs:15`），**同一份数据存了两处**。这正是你讨厌的那种重复，建议先合并成一处。

## 9.2 详细表格 / 修复 patch 可现算：实测支持你的判断

我量了：`mod analyze` 单个 mod **≈235 ms**，其中 **135 ms 是进程启动**，所以**纯分析 ≈100 ms/mod**。10 个 mod 的配置 ≈1 s，55 个 ≈5.5 s。

**结论：你说得对，持久化的内容分析缓存可以删，留会话内存缓存即可。** 但别一刀切，要分清热冷：

| 缓存 | 服务谁 | 频率 | 结论 |
|---|---|---|---|
| `mod-information/AssetInventory_*.json` | 详细表格、材质诊断、override 图 | 冷 | **删持久化，留内存** |
| `mod-information/ReferenceGraph_*.json` | 详细表格 | 冷 | **删** |
| `mod-data-index/*.json` | 详情页一行字（且与 sqlite 重复） | 冷 | **删** |
| `mod-information/FileFacts_*.json` + `owners.index` | **部署主路径**（`ProfileApplyService`） | **热** | **留**（目录扫描级，很轻） |
| `mod-facts.sqlite` | 配置页状态投影（`ProfileOverrideGraphService`、`ProfileMaterialDiagnosticsService`）+ 详细表格 | 中 | 可改内存，但它是**跨 mod SQL 查询**；配置页要显示 40 条 entry 状态时需全量分析一次（≈100 ms × 活跃 mod 数）。**建议保留，但只留 3 张活表** |
| `hidden-unit-cache`（91.5 MB） | canonical 重建 | 冷但极贵 | **留** |

## 9.3 素体：不是"允许复数"，而是"严格禁止复数护甲"

收到，口径改成**校验**而不是数组：

- 一个 sectional / vpk2patch mod **只允许一份护甲 archive + 一份头盔 archive**；
- 违反就在**导入 / 转换时拒绝**（不是等到运行时才炸），并说清"这个包里检测到 2 份护甲来源：A、B"；
- `custom-model.json` 保持单值，名字 / 件表 / 映射都不需要"多个素体"的分支。

**需要你定**：已存在的违规 mod 怎么办？我建议"**能加载、但标成不可转换/不可重构建，并在详情页说明**"，而不是拒绝加载——否则老库会打不开。

## 9.4 身份丢失不重要：接受，撤回我那条建议

你的设计是：**mod 自己的配置在 mod 文件夹里**，所以库 = 可重建的索引 + 用户级元数据；误操作复制进来的 mod 能被无缝接收。这确实是被动兜底。**我上一轮 §6.5 那条"目录名不应承载身份"应该撤回**——你的可移植性目标比我的防孤儿目标更值钱。

但有一个后果必须回答：**重导入 → 新 GUID → 所有引用它的地方悬空**（配置条目、护甲映射来源、装饰 host、Option 父子）。三种处理，你选一个：

1. **接受悬空**，UI 把死条目显示成"已失效（原 mod 已不在库中）"，用户自己删。最简单、零猜测。
2. **按文件夹名重连**：新 mod 目录名与某个死条目的 `relativePath` 相同就认作同一个。**这会让"改名"变成破坏性操作。**
3. **按内容指纹重连**：`contentFingerprint` 相同即同一个。最准，但重构建后指纹会变。

我倾向 1，因为 2/3 都要"猜"，而你最讨厌猜。但如果用户经常整体搬迁库，1 的体验会很差——**这是个真问题，得你定**。

## 9.5 "人类是傻瓜，agent 要更聪明"——CLI 设计原则改写

这条纠正很关键，它把目标从"好读"改成"**能让 agent 还原人类意图**"。据此改写三条原则：

1. **给事实，不给结论**：`doctor --json` 要给"编译基底 453 KB / 当前 dl_bin 287 KB / 指纹不同"，而不是"你的映射可能有问题"。agent 擅长推演，不擅长猜。
2. **给可自证的诊断**：每项带 `code` / `observed` / `expected` / `fix` 四个字段，agent 才能自己判断"这是不是用户说的那个毛病"。
3. **给可自修的最小动作**：`repair` 家族 + `mod rebuild`。人类说不清"我的裙子不见了"，agent 需要一条命令重构建再对比结果——**而不是让人类去描述症状**。

这三条也解释了为什么 `status` 全 null + `issues: []` 是**最坏的一种输出**：它让 agent 既无法推演也无法自证，只能回头问那个说不清的人类。

## 9.6 上一轮清单的状态

| 上一轮建议 | 现在 |
|---|---|
| §6.2 删在线依赖 | 改为**两步**：先断联网（零损失），再决定删不删文件 |
| §6.3 legacy 缓存可删 | **成立**，实测 100 ms/mod 支撑；但部署路径的 FileFacts 要留 |
| §6.4 素体改数组 | **撤回**，改成"禁止复数护甲"的校验 |
| §6.5 目录名不承载身份 | **撤回**，你的可移植性设计更好 |
| §6.6 措辞更简单 | **改为**"给事实、给可自证诊断、给可自修动作" |

## 9.7 等你定的四件事

1. **社区文件**：只断联网（保留随包快照），还是连 4 个文件一起删？
2. **违规的多护甲 sectional**：能加载但标不可转换，还是直接拒绝？
3. **重导入后的悬空引用**：接受悬空 / 按文件夹名重连 / 按内容指纹重连？
4. **`mod-facts.sqlite`**：保留 3 张活表（配置页快），还是彻底改内存（每次打开配置页多 1~2 s）？

---

# 10. 三次沟通：可移植性落地设计（2026-11-06 晚）

## 10.1 你的三个决定，我已核实

**① 多护甲 sectional：库里零例，不需要校验。**

扫了 `E:\mod`：**49 个 vpk2patch mod**，其中 8 个是模型类（每个恰好 1 份 `matcapGeometry` + 1 份 `vpk2patch\custom-model.json`），41 个是纯材质包（无 `matcapGeometry`、无素体文件）。**多护甲的：0 个。**

所以"一份护甲 + 一份头盔"是**生产路径保证的事实**，加校验就是又一层过度防御。撤回 §9.3 那条建议。

（顺带纠正一个措辞：只有**模型类** sectional/vpk2patch 才必定含一个素体；41 个纯材质包没有素体，`CustomModelRecovery` 也正确地跳过了它们。）

**② 社区文件：只留快照、移除更新。** 已确认零功能损失（装备名全来自游戏 strings 表）。

**③ `mod-facts.sqlite` 改内存：CLI 完全不依赖它。** grep 确认 `HD2ModCli` 里 `AdvancedModAssetQueryService` / `IReferenceGraphQueryIndex` **一次都没出现**（`mod analyze` 走 `PatchGroupAnalyzer`，绕开了整条缓存链）。所以内存模式只影响 GUI，代价是打开配置页时多算一次（≈100 ms × 活跃 mod 数）。

## 10.2 可移植性：比"名字和备注丢了"更严重

`ModLibrarySynchronizer.ApplyInspection` 在发现一个新目录时，把节点**写死**成这样（`:132-139`）：

```csharp
new ModNodeMetadata(directory.Name, null, timestamp, null, ModNodeKind.Standard,
                    IsLogicalContainer: directory.IsLogicalContainer)
//                     ↑Name          ↑Notes    ↑Kind=Standard
... Array.Empty<ModNodeId>()   // ← HostModIds 空
```

所以把 mod 文件夹拷进另一个库、重载之后：

| 数据 | 现在能恢复吗 | 靠什么 |
|---|---|---|
| 名字 | ❌ 变文件夹名 | — |
| **备注** | ❌ **变 null** | — |
| **Option 归属（父级）** | ❌ **丢失，降级成独立 Standard** | `option.json` 在文件夹里，但**同步器不读它**（只读 `.hd2modmanager-logical.json`） |
| 素体 | ✅ | `vpk2patch\custom-model.json` |
| section 树 / 组表 | ✅ | `sections.json` / `components.json` |
| 转换参数（重构建要用） | ✅ | `vpk2patch.json` |
| 逻辑容器身份 | ✅ | `.hd2modmanager-logical.json` 里存了 nodeId |
| 映射 | 按设计不迁移 | — |

**这就是你说的那个问题的完整版**：不只是名字备注，**结构归属也会丢**。而且 `option.json` 里的宿主引用是**库分配的 GUID**：

```json
{ "kind": "Option", "hostModGuids": ["66ba45532334483e9abe2a58d1e74018"] }
```

迁移后宿主换了新 GUID → **即使同步器读了 `option.json` 也解析不到宿主**。

## 10.3 方案：`<mod>/mod.json`，带一个持久 id

```json
{ "schema": 1, "id": "66ba4553-2334-483e-9abe-2a58d1e74018", "name": "Elise_Bill", "notes": "…" }
```

**为什么必须有 `id`，而不只是 name/notes**：一个字段同时解决三件事——

1. **名字/备注可移植**（你要的）；
2. **Option 归属可解析**：`option.json` 改存持久 id 而不是库 GUID，迁移后仍能找回宿主；
3. **配置与映射的引用不再悬空**：这正好回答 §9.4 那个悬而未决的问题——node id 由文件夹携带，迁移后还是同一个 id，`profiles.json` 的条目、映射的来源、装饰的 host 全部自动接上。**你不需要在"接受悬空 / 按文件夹名重连 / 按指纹重连"里选一个了。**

而且这**不是新机制**：`.hd2modmanager-logical.json` 已经在给逻辑节点存 nodeId（`id = directory.LogicalNodeId ?? ModNodeId.New()`），所以逻辑 mod 迁移后 id 本来就不变。`mod.json` 只是把这个做法推广到所有 mod。

**落地口径**：

| 事项 | 建议 |
|---|---|
| 优先级 | **文件夹赢**。库里的 name/notes 只是快速索引，每次扫描以 `mod.json` 为准 |
| 写路径 | 改名/改备注：**先写 `mod.json`，再写 `library.json`**。文件夹写失败 → **必须报错**（不能静默成功，否则迁移时名字丢） |
| 老库迁移 | 首次扫描时，若 `mod.json` 缺失而库里有该节点的 name/notes → **按库里现有 node id 补写一份**，id 不变、零漂移 |
| 新 mod（手工拷入） | 缺失 `mod.json` → 用文件夹名 + 空备注，并**立即补写**，此后稳定 |
| id 冲突 | 同一个 id 出现两份（用户误复制）→ 第二份分配新 id 并**改写它自己的 `mod.json`**，记日志 |
| 不放什么 | kind / order / 激活状态 / 映射 —— 这些是库级事实，不属于 mod |
| 逻辑标记 | 建议合并进 `mod.json` 的 `kind` 字段，退役 `.hd2modmanager-logical.json`（读兼容、不再写），消灭"两个文件都存 nodeId"的重复 |

**素体名顺手解决**：`custom-model.json` 不导出时，素体名会由文件夹名/压缩包名生成。建议**默认取 `mod.json` 的 `name`**——这样即使素体文件没跟着走，名字也不会退化成 `custom-xxxx`。

## 10.4 导出功能因此变得很便宜

如果名字、备注、身份、section 树、组表、转换参数都在 mod 文件夹里，那么：

> **导出 = 复制文件夹**（排除残留备份）。

这正是你要的"CLI 完全不可用时的纯手工手段"：用户把文件夹拷进另一个库、重载，就完成了迁移。

**但必须先清理残留**——我实测 `E:\mod` 里有 **212.4 MB / 12 项** 备份残留，全在 mod 文件夹内部：

| 残留 | 大小 |
|---|---|
| `【2k材质】恋星milfy\vpk2patch.bak-20261105-103748` | 78.0 MB |
| `【2k材质】恋星milfy\vpk2patch.bak-20261105-100039` | 66.1 MB |
| `恋星milfy_bill\vpk2patch.bak-20261105-100039` | 39.6 MB |
| `MourningVeil_Bill\Overwrite.bak-150813` | 28.4 MB |

这些是重构建/换甲时留的"上一版产物"。它们不参与指纹（指纹只算 patch 命名的文件）、不被扫描（`vpk2patch.bak-*` 不在载荷目录列表里），所以**不影响正确性**，但会让库变胖、让导出变慢。建议：导出时排除 `*bak*`，并给一个"库体检"把它们列出来让用户决定。

顺带：库根还有三份早前探测留下的杂物（`library.json.backup-before-probe`、`probe-deploy.json`、`probe-original-only-deploy.json`），是我的遗留，可以删。

## 10.5 另外两处顺手发现的

**① `mod-replacement-backups` 放在"可再生"目录里。** `ModLibraryService.cs:872` 把**被替换掉的 patch** 备份到 `<管理器目录>\data\mod-replacement-backups\`，而 `ModIndexGarbageCollector` 还会主动清理"死"备份。按你的四目录口径，管理器目录是"丢了不重要"的——但这些备份是用户 mod 的上一版内容。**要么承认它可再生（源 VPK 还在，能重转），要么挪进库根。**

**② 同一件事两处布局判定，一处对一处错。** `ModLibrarySynchronizer` 用 `ModNodePayloadLayout.EnumeratePayloadFiles`（认 `vpk2patch/`，正确），而 CLI 的 `mod analyze` 只看根目录（把健康的 vpk2patch mod 报成"存储已不完整"）。建议 `mod analyze` 改用同一个函数。

## 10.6 等你定的四件事

1. **`mod.json` 要不要带持久 id？**（我强烈建议要：它让"迁移后引用悬空"这个问题直接消失，否则 §9.4 的三选一仍然要选）
2. **`.hd2modmanager-logical.json` 退役合并进 `mod.json`**（读兼容、不再写），还是两个文件各管一件事？
3. **`mod-replacement-backups`**：算可再生（留在管理器目录），还是算用户数据（挪进库根）？
4. **残留备份**（212 MB）：导出时排除即可，还是要给一个显式的"清理残留"动作？

---

# 11. 决定与回归（2026-11-06 收尾）

## 11.1 你已拍板的四条（取代 §10.6 的提问）

1. **`mod.json` 带持久 id** —— 做。
2. **`.hd2modmanager-logical.json` 退役** —— 做（但按审查，退役前必须先让残留清理认得"这是管理器自己的目录"，否则逻辑容器会被整目录删掉；当前库 0 个逻辑容器，风险窗口很小但必须堵）。
3. **`mod-replacement-backups` 算可再生** —— 留在管理器目录，不挪。
4. **残留备份允许 CLI 清理，体积过大时提醒使用 CLI 的 agent** —— 做（形式按审查建议改成 `library cleanup --backups --dry-run` + `library status` 里的 `reclaimableBytes`，不做弹窗式"主动提醒"）。

## 11.2 `bf47843` 的回归（已修，三个提交）

**现象**：gamedata 权威表从 422 套件 / 3527 件 / 2006 个不同 unit 塌成 422 / 2080 / 66，`unit_parts` 与 `stream_layouts` 变成 0 行，而 `meta` 仍写着"已完成"。

**根因链**（三层，缺一层都修不好）：

| 层 | 问题 | 修法 | 提交 |
|---|---|---|---|
| ① 语义 | 权威源读了当前 `dl_bin`，而它是真映射的产物（件表只剩被映射的那几个来源模型的 unit） | 改回读**原件副本**；副本只在"没有副本"或"游戏确实更新过（Steam manifest 的 LastUpdated 晚于副本时间）且当前文件件数不少于副本"时刷新 | `c8fd342` |
| ② 接线 | `registryRoot` 在生产里**从来没被传过**，所以①那条分支根本走不到（改完仍报 `current`、指纹不变） | `CoreServices.CreateAssetArchiveIndexService` 默认解析本机注册表目录；高级索引改用同一个组合根 | `e7ef56f` |
| ③ 判据 | `IsCurrentAsync` 要求"每个 unit 都有部件且有流布局"，而实测有 2 个 unit 永远解析不出来 ⇒ 这个门永远为假：每次"补齐缺失"都变全量重算 73 s，CLI 的 postcondition 永远报失败 | 改成"两张表都非空 + 章与基础指纹一致"（写入是单事务，非空即完整；"只有部件没有布局"的半成品仍被判不完整） | `a6e1269` |

**本机实测（修复前 → 修复后）**：

| | 修复前 | 修复后 |
|---|---|---|
| 权威源 | `…\data\game\…dl_bin`（映射产物） | `…\ArmorRegistry\backup\…original.dl_bin`（原件副本） |
| 套件 / 件 / 不同 unit | 422 / 2080 / 66 | 422 / **3527** / **2006** |
| `unit_parts` / `stream_layouts` | 0 / 0 | **16569 / 7415**（与 11-02 的健康库一致） |
| 高级索引是否最新 | false（永远） | **true** |
| 「补齐缺失」 | 73.4 s + 失败 | **3.3 s + 成功** |

**对老用户的影响**：升级后索引会因为指纹变了被判过期一次（`5a5bd7ec…` → `9c1ec449…`），重建一次即恢复。这是预期行为，不是故障。

**顺带修掉的"记账骗人"**：`AdvancedEquipmentIndexService` 插入 0 行时不再写 `advanced_equipment_index_version` / `source_fingerprint` —— 一行都没写进去就不许盖完成章。

## 11.3 待做：复制 sectional / vpk2patch mod（本轮只登记需求）

**需求要点**：sectional 类 mod 现在只能显示一种外观，有颜色变体 / section 变体就得重启后切换，无法两种变体同时存在。需要能**复制一个主要 sectional（含 vpk2patch）mod**，连同属于它的材质选项一起复制；复制时复用当前物理文件夹结构、生成不同命名空间的变体，**所有材质以新 id 重新引用**。

**做之前要先确认的四件事**（下一轮判断完再写进文档）：

1. **材质选项的归属怎么判**：一个 mod 的 `option.json` 里只有 `hostModGuids`，材质 Option 是独立目录（如 `Elise基础材质A1主体`）。复制的"连带范围"按 `directOptions` 递归，还是按 `option.json` 的 host 反查？
2. **命名空间**：`vpk2patch.json` 里有 `idNamespace`（如 `vpk-748bd2ca1f793950`），材质/贴图 id 由它派生。复制时必须换新 namespace，否则两个变体的材质 id 相同、在游戏里互相覆盖——**这正是"只能显示一种外观"的机制本身**。
3. **unit id 基**：`custom-model.json` 记着 unit id 基（`7bb226e101800001`…）。复制体重编号 = 两份独立素体，可同时存在；不重编号 = 两个 mod 抢同一批 unit，不能同时启用。按需求**必须重编号**。
4. **是否走 CLI**：这条功能天生适合做成 `hd2mod mod clone <GUID> --variant-name <名字>`——纯文件操作 + id 重写，没有 UI 依赖。做成 CLI 命令后，agent 就能替人类完成"我要两套配色同时装上"这种描述不清的需求。

**风险**：重编号会牵动 `custom-model.json`、`vpk2patch.json`、patch 字节里的 unit id 与材质 id 三处，任何一处漏改都会得到"看起来对、进游戏错"的结果。所以验收必须是实机 A/B，不能只靠内部检查。

---

# 12. 复制功能的前提（已查清）与重构步骤

## 12.1 结论：复制不需要改字节，它就是「重构建」的一个变体

三条已核实的事实，合起来把这件事从"改字节"降级成"换两个参数"：

**① 材质 / 贴图 id 是确定性的，且命名空间来自来源包名。**

```csharp
// HD2SourceMigration/VpkConversion/Hd2AssetId.cs:62-67
Material(baseTexturePath, idNamespace) => Derive(MaterialPrefix, baseTexturePath, idNamespace)
Texture(textureBaseName,   idNamespace) => Derive(TexturePrefix,  textureBaseName,  idNamespace)
```

`id = (prefix << 60) | (sha256(命名空间 + 名字)[0..8] 小端 & 掩码)`。命名空间 token 由**来源包名**哈希得出（`TokenFromSourceName`，转小写、去空白），注释写得很清楚：用来源名而不是随机 GUID，是为了"重转同一份包得到同一套 id"。

**所以：换一个来源名 / 换一个 token ⇒ 整包材质与贴图 id 全换，且逐位可复现。**

**② 命名空间在重构建时是被**原样回放**的。**

```csharp
// HD2ModManager/Services/VpkRebuildService.cs:188-190
// 命名空间：给了 token 就原样接上，与上一份产物的材质 id 逐位一致。
if (report?.IdNamespace is { Length: > 0 } token)
    arguments.AddRange(["--id-namespace-token", token]);
```

这正是"重构建之后观感不变"的机制。**复制要做的就是把这一行换成新 token。**

**③ unit id 的种子是随机的，除非显式复用。**

```csharp
// HD2SourceMigration/VpkConversion/VpkPatchAssembler.cs:413
var seed = unitIdSeed ?? (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fffffffffff0000UL);
```

重构建靠 `--reuse-model <旧的 custom-model.json>` 把这个种子接回去（`VpkRebuildService.cs:192`），否则 id 重新随机、已登记的映射与件表全部失效。**复制要做的就是不传 `--reuse-model`。**

**结论：复制 = 用新 token 重跑转换 + 不复用 model。** 不需要任何字节编辑，走的是已经验证过的重构建管线；`sections.json`、`custom-model.json`、`vpk2patch.json`、patch 字节全部由这条管线一致地重新生成。

## 12.2 复制时哪些搬、哪些重算

| 项 | 处理 | 理由 |
|---|---|---|
| `<来源>.vpk` | **复制** | 权威来源，必须跟着走（体积大，但无法避免） |
| `vpk2patch.json` | **重新生成** | 里面的 `idNamespace` / `patches` / `queue` 都要反映新 id |
| `vpk2patch\custom-model.json` | **重新生成** | 新 key + 新 unit id 基 |
| `vpk2patch\sections.json` | **重新生成** | 它绑定 patch 字节（含指纹/偏移），字节变了就失效 |
| `vpk2patch\*.patch_n` + `.gpu_resources` | **重新生成** | 材质/unit id 全变 |
| `Overwrite\` | **重新生成**（跑 rekey） | 派生自来源 patch + section 组表 |
| `components.json`（section 组表） | **原样复制** | 纯用户意图，不含字节指纹 |
| `wardrobe.json` | **原样复制** | 纯用户意图（勾选状态） |
| `option.json` | **改写**：宿主 GUID 换成新库里的宿主 | 关系要重新指向 |
| `icon.*` | 原样复制 | — |
| 备注 / 名称 | 原样复制（将来由 `mod.json` 承载） | 用户输入 |
| `*.bak-*` / `*.backup-*` 残留 | **不复制** | 垃圾 |

## 12.3 一个必须先定的产品问题：变体之间的差异体现在哪

这决定复制之后要不要自动建映射。

- 如果两个变体的差异是**映射到不同护甲**（同一套模型，A 甲显示这套配色、B 甲显示那套）——那复制体必须有自己的素体（新 key），并且**需要用户在新护甲上手动建一条映射**。自动建映射没有依据（不知道目标是谁）。
- 如果差异是**同一护甲下的不同材质选择**——那不需要复制，现有的材质 Option（`A1主体` / `B1光泽` 这种）本来就是干这个的，只是"同时存在"做不到。

**我的判断**：你要的是前者（两个独立外观同时在场），所以复制**不该**自动建映射，只该把新变体作为独立 mod 注册好、素体就绪，让用户去护甲映射页给它选目标。这样"两个变体同时存在"= 两条映射指向两个不同的目标护甲，机制上完全成立，不需要新概念。

## 12.4 重构步骤（把复制编进去）

依赖关系先说清：**复制 = `mod rebuild` 的变体 ⇒ 需要先有"这个 mod 是哪种、能不能重建"的判定 ⇒ 需要先做 `ModStorageKind` 建模。**

| 阶段 | 内容 | 为什么在这个位置 |
|---|---|---|
| **P0 记账与权威**（进行中） | ✅ 权威源读副本 + 指纹含副本 + 高级索引 gate（`c8fd342`/`e7ef56f`/`a6e1269`）<br>待做：`meta` 与数据同事务写（`AssetArchiveIndexService.cs:471-484` 的 13 条 meta 在 commit 之后）；清掉死枚举 `GameDataIndexState.Invalid` 与死字段 `deployed=null` | 这是所有上层判断的地基：先让"库说自己是最新的"这句话可信 |
| **P1 一套判定** | CLI 复用 Core 的协议解析（删 CLI 自己那份更严的）；`mod analyze` 改用 `ModNodePayloadLayout`（它现在只看根目录，把健康的 vpk2patch mod 报成"存储已不完整"）；**引入 `ModStorageKind` 一处判定**，`mod show`/`mod list`/`context export` 输出它，GUI 功能可见性由它决定 | 复制要知道"这是 sectional 还是 legacy"，而这正是现在唯一判不了的事 |
| **P2 mod 自描述** | `.hd2modmanager-mod.json`（持久 id / 名称 / 备注）；logical marker 退役（**先给残留清理加目录保护**，否则逻辑容器会被整目录删掉）；同步器读 `option.json` + 按持久 id 解析宿主；导出 = 复制文件夹（排除残留） | 复制出来的变体要能"自己说明自己是谁"，否则迁移/改名就丢名字 |
| **P3 CLI 门面** | `mod rebuild <GUID> [--all]`（把 GUI 那条配方固化成命令）；**`mod clone <GUID> --variant-name`**（= rebuild + 新 token + 不复用 model + 复制用户意图文件 + 挂 Option）；`doctor --json`；`repair` 家族；单命令 `--help`；`--manager-root` 默认成 exe 目录 | 复制只有做成命令，agent 才能替人类完成"我要两套配色同时装上" |
| **P4 缓存瘦身** | mod 条目：删 `MaintenanceAnalysis` / `mod-data-index` / `Thumbnail_*.json` / 两张 `*_snapshots` / 三件套死代码；`AssetInventory`+`ReferenceGraph` 改内存；`mod-facts.sqlite` 改内存（实测 100 ms/mod）<br>gamedata：删 `asset_archives`/`assets`/死列；4 张 0-reader 表接上或删；`unit_parts`/`stream_layouts` 纳入同一次重建<br>社区文件：切断联网，只留随包快照 | 与功能无关，可以并行；但要在 P1 之后做，避免两边同时改同一批文件 |
| **P5 架构债** | Core 的三件套（`GameDataPackageResolver` 501 行含 DSAR/LZ4、`PatchTocScanner`、`PatchEntryPayloadReader`）收敛到 Adaptation；Manager 不再直接 `using HD2ModAdaptation.*`；索引指纹纳入 `archivehashes`/`friendlynames`/`typehash` | 全仓唯一真正的架构债；做完之前"同一份 patch 两条路径"始终可能给出不同结果 |

**为什么复制放在 P3 而不是更早**：它复用 `mod rebuild` 的整条管线，而 `mod rebuild` 需要 P1 的种类判定；它要"复制备注/名称"，而那是 P2 的 `mod.json`。提前做就只能写一个"临时版本"，然后被 P1/P2 推翻。

## 12.5 复制的验收与风险

**验收（必须实机 A/B，不能只靠内部检查）**：
1. 复制一个模型类 vpk2patch mod（如 `Elise_Bill`），两个变体同时启用；
2. 两个变体分别映射到**两个不同的护甲**，进游戏同时可见且互不串味；
3. 材质 id 全不同（比对两份 `vpk2patch.json` 的 `idNamespace` 与两份 patch 的条目）；
4. 原 mod 的映射仍然有效（复制不能动原 mod 的任何 id）。

**风险**：
- **成本**：重跑转换对大包是分钟级（重构建本来就是"这几分钟"）；复制一个 158 MB 的包要复制 VPK + 重转。所以 `mod clone` 应当默认 `--dry-run` 先报告"要复制多少字节、预计多久"。
- **磁盘**：每个变体都带一份完整来源 VPK。两个变体 = 两份 VPK。这是需求本身的代价，不是实现的浪费。
- **`--material-source` 外链**：如果原 mod 的材质来自**库外的其他 VPK**（`vpk2patch.json` 的 `resolutionSet` 记录了），复制时必须确保那些来源仍然可达，否则重转会缺材质。这是最容易漏的一项。

---

# 13. 复制单位不是「一个 mod」，是「一个 namespace 家族」（实测）

上表最后那条"最容易漏的一项"，我去实测了。**结论比预想的严重，但也比预想的干净。**

## 13.1 实测：外部材质依赖是 100% 常态，不是边缘情况

扫了 `E:\mod` 全部 52 个 vpk2patch mod 的 `vpk2patch.json`：

```
vpk2patch mods: 52 | mods with external sources: 52 | missing source files: 0
```

**52 / 52 —— 每一个 mod 的 `queue` / `resolutionSet` / `patches[].vpk` 里都含有自己目录之外的文件。** 我之前把它标成"最容易漏的风险"，实际上它是**唯一的工作模式**：材质永远来自别的 mod 目录。

## 13.2 namespace 是"家族级"的，家族结构极其规整

按 `idNamespace` 分组，**9 个家族，每个都是「1 个模型 mod + N 个材质 Option」**：

| namespace | 成员数 | 结构 |
|---|---|---|
| `vpk-cc2a3c886bbae297` | 13 | `Lilium_Sancta_Bill`（model）+ 12 个 material |
| `vpk-448cbad731260154` | 13 | `Lunalice_Bill`（model）+ 12 个 material |
| `vpk-e688da0055f0a55f` | 11 | `MourningVeil_Bill`（model）+ 10 个 material |
| `vpk-748bd2ca1f793950` | 4 | `Elise_Bill`（model）+ 3 个 material |
| （其余 5 个） | 2~4 | 同构 |

`kind` 字段只有两种值：`model` 和 `material`。**没有任何一个家族有两个模型 mod**——这正好印证你说的"sectional mod 总是只自带一份盔甲和一份头盔，永远不会超容量"。

## 13.3 这改变了复制的设计单位

**复制单位必须是「namespace 家族」，不是「一个 mod 文件夹」。** 理由是一条硬约束：

> 模型 patch 引用的材质 id，必须与材质 patch 写出的材质 id 相等。两者都由**同一个** namespace 派生。

所以如果只复制模型 mod、换新 token，会得到：模型 patch 引用**新**材质 id，而材质 patch（还在原家族、用旧 token）写出**旧**材质 id ⇒ **白模**。

**正确做法**：拿这个家族的**全部 VPK**（1 模型 + N 材质），用**同一个新 token** 重跑一次 `vpk2patch`。

## 13.4 好消息：这正是 `vpk2patch` 原生的工作模式

`vpk2patch` 从设计上就是"一批 VPK 一次转换"，而且强制全批共用同一个 namespace：

```
--id-namespace <来源标识> | --id-namespace-token <token>
    材质/贴图 id 的命名空间。**一批必须共用同一个值**：
    模型 patch 引用的材质 id 要与材质 patch 写出的相等。
```

`VpkRebuildService` 已经就是这么做的——`FindSiblingVpks(modsRoot, modDirectory, idNamespace, notes)` 按 namespace 把兄弟 VPK 全找出来，塞进 `--material-source`，用**原 token** 重跑，所以重构建之后观感不变。

**所以 `mod clone` 几乎是 `VpkRebuildService` 的一个变体**：

| 步骤 | 重构建（现有） | 复制（新增） |
|---|---|---|
| 参数来源 | `report.IdNamespace` | **新 token**（由变体名派生） |
| `--reuse-model` | 传旧的 `custom-model.json`（保住 unit id） | **不传**（unit id 换新） |
| 输出目录 | 暂存 → 覆盖回原 mod | 暂存 → **新建 mod 目录** |
| 兄弟 VPK | 读库里的、不复制 | **连同 VPK 一起复制**（变体要能独立迁移） |
| Option 归属 | 不变 | 新建一组 Option，挂到新模型 mod 下 |
| 用户意图文件 | 保留原样 | 复制（`components.json` / `wardrobe.json` / `icon.*` / 备注 / 名称） |

## 13.5 复制的完整动作序列（修正版）

1. **定位家族**：读目标 mod 的 `vpk2patch.json` 拿 `idNamespace`，扫描全库找出**同 namespace 的所有 mod 目录**（模型 + 材质）。
2. **报告规模**：`--dry-run` 输出"将复制 N 个 mod、M 个 VPK、X GB、预计 Y 分钟"。这一步不能省——13 个成员、每个几十到上百 MB。
3. **生成新 token**：由变体名派生（复用 `TokenFromSourceName` 的规则），保证可复现、可读。
4. **复制来源 VPK**：把家族所有 VPK 复制到暂存目录（**不复制** `vpk2patch/` `Overwrite/` `*.bak-*`）。
5. **一次转换**：用新 token 跑一次 `vpk2patch`，输入是家族全部 VPK，输出到暂存。**不传 `--reuse-model`**。
6. ~~**分发产物**：每个 VPK 的产物落到对应的新 mod 目录（与原家族一一对应）。~~
   **⚠️ 已被后续的「折并」设计推翻（2026-11-06）**：现在材质 patch 不再独立成 mod，
   而是平铺进宿主 `sectional/`、在 `patches.json` 里成为一个选项。
   所以复制出来的应当也是**一个** Mod（含 N 个材质选项），不是 N+1 个目录。
   实际实现见 `mod clone`（`c99440d`）：复制宿主文件夹 + 同族 VPK，用新 namespace 重转一次。
7. **复制用户意图**：`components.json` / `wardrobe.json` / `icon.*` / 名称 / 备注，逐成员搬。
8. **建库节点**：新目录被库接收，建立"1 模型 + N 材质"的 Option 关系（`option parent add` 或等价）。
9. **等用户建映射**：新素体已就绪（新 key + 新 unit id），但**不自动建映射**——目标护甲由用户在护甲映射页选。

## 13.6 由此产生的三个新结论

1. **`ModStorageKind` 判定之外，还需要一个「家族」概念**。家族不是物理结构（成员平铺在库根），它是 `idNamespace` 的等价类。这可以纯派生——每次扫描现算，**不需要存**。
2. **`mod clone` 的正确粒度是家族**。若只提供"复制这个 mod"，用户会得到一个白模；所以要么只允许对 `kind=model` 的 mod 执行 clone（自动带上家族），要么显式说清"这只是家族的一个成员"。
3. **`vpk2patch.json` 是家族关系的唯一凭据**。它一旦丢失，模型与材质就再也配不上对——这解释了为什么它是"必需，不可重算"。也说明：**复制时必须原样保留每个成员的 `idNamespace` 语义（家族归属），但它记录的是旧 token，所以要么改写它、要么给家族关系单独一个不受 token 影响的标识。**

   我倾向后者：家族关系应当由**模型 mod 的目录名**或**新建的家族 id** 承载，而不是由 namespace token 承载。否则"换 token 复制"和"家族归属"这两个正交的东西会绑死在一起。

## 13.7 修正：家族定义要用 `resolutionSet`，不是 namespace

实测把 9 个家族逐一比对：

```
9 个家族，每个家族内 resolutionSet 只有 1 种取值，且大小 == 成员数
   vpk-cc2a3c886bbae297  members=13  distinctRS=1  rsSize=13
   vpk-448cbad731260154  members=13  distinctRS=1  rsSize=13
   vpk-e688da0055f0a55f  members=11  distinctRS=1  rsSize=11
   vpk-748bd2ca1f793950  members=4   distinctRS=1  rsSize=4
resolutionSet 条目不是库 mod 的：0
```

**`resolutionSet` 就是家族的精确定义**（它是"当初一起解析的 VPK 集合"），而且每个成员里都存了一份**完全相同**的副本 ⇒ 从任意一个成员出发都能还原整个家族。

**namespace 不能当定义**：它是 `TokenFromSourceName(组名)`，是派生值。今天 9/9 一致，纯属当初都是一批导入。风险很实：

- 用户单独重导一个材质包 → 它拿到 `hash(那个包名)` → **静默脱离家族** → 模型 patch 引用旧 id、材质 patch 写出新 id → **白模，零报错**。

**还有一条便携性陷阱**：`resolutionSet` 存的是**小写绝对路径**（`e:\mod\elise_bill\elise_bill.vpk`）。换库/迁移后这些路径全失效 ⇒ **只能按文件名映射到库目录，不能按存下的路径找**。

**外部依赖边界**：`--material-source` 允许指向库外（如 L4D2 根目录）。本机 0 例，但真出现时那些条目**不是库 mod**，无法随模组迁移 ⇒ 必须显式标成"外部依赖"，不能静默丢，否则复制体在别人机器上必然白模。

## 13.8 顺带纠正：section 与材质变体是两条正交的轴

同一批 mod 里有两个**不同 schema** 的 `components.json`，命名重叠本身是个坑：

| | 路径 | schema | 内容 |
|---|---|---|---|
| 分件组表 | `Overwrite\components.json` | `hd2mod.section-groups/1` | 12~14 组：`裙Skirt`、`耳坠Earrings`、`光环`、`猫耳`（**几何部件**） |
| vpk 组件 | `vpk2patch\components.json` | `hd2mod.vpk-components/1` | 1 组（源侧 bodygroup） |

> ⛔ 已过时（2026-11-08）：本节末句"材质变体是另一条轴 = 独立 Option mod"**已不成立**——
> 材质已折并进宿主：`sectional/` 里 `patch_0`（unit）+ `patch_1..patch_N`（材质变体）**平铺**，
> 另有 `patches.json` 记号说明每组是谁；材质选项不再是库里的兄弟节点。
> 「两种变体同时存在」现在只剩 section 一条轴需要靠复制 Mod。
> 表的第二个路径也已是 `sectional\components.json`（目录改名，见 §1.3）。

而材质变体是**另一条轴**：独立 Option mod（`MourningVeil基础材质B1服装色黑` 之类）。

所以"两种变体同时存在"实际是**两个问题**：section 配置要靠复制 mod，颜色也要靠复制。两者都指向同一个单位——**家族**。

---

# 14. clone 在库侧必须怎么做（子代理实测）

## 14.1 三个会静默毁掉复制的坑

**① 旧 `option.json` 必须删，而且要早删。**

`option.json` 里 `hostModGuids` 存的是**旧宿主的库 GUID**。两条接收路径对它态度完全不同：

| 路径 | 读 `option.json` 吗 | 后果 |
|---|---|---|
| `library sync` | **不读**（只读 `.hd2modmanager-logical.json`，`ModLibrarySynchronizer.cs:55`） | 节点是 `Standard`、无宿主；旧 GUID 不生效，但脏文件留在磁盘上 |
| `import` | **读**（`ObjectTreeImporter.cs:42,105-121`） | 原宿主还在 → 恰好自动挂回**旧**宿主；原宿主已删 → **悬空 Option** |

而悬空 Option 是**现在没有任何命令能修**的状态：关系图静默丢边（`OptionRelationshipGraph.cs:30-48`），变体既不算独立 Mod 也没有父级，任何 `option parent add/remove` 都会因宿主不存在直接抛错（`ModRelationshipMutationService.cs:267-281`）。只能手改 `option.json`。

⇒ **clone 在暂存阶段就要删掉 `option.json`、`.hd2modmanager-logical.json`、`*.bak-*`**，让 `import` 干净地把它当新 mod 收进去，然后由 `option parent add` 显式挂载。

**② 重复的素体 key 不会报错，会静默吞掉第二份。**

`CustomModelCatalog` 用 `entries.TryAdd(model.Key, …)`，按目录名排序**保留第一个**（`CustomModelCatalog.cs:129-131`）；而 `ValidateModels` 只查**同一份文档内**的重键（`RuntimeArmorRegistryService.cs:1071-1081`），合并走字典，永远触发不了。

结果：第二份变体在素体列表里**根本不存在**，但它的 patch 照常部署、照常写同一批 unit id → 两个 mod 抢同一批 unit，游戏里只有一份生效，映射还指着第一份。

⇒ 幸好不传 `--reuse-model` 天然会生成新 key。**最便宜的验收探针就是"两份 `custom-model.json` 的 `key` 不同"。**

**③ `import` 会再复制一份目录。**

`import` 必定复制到新的唯一目录名（撞名加 `_2`，`ModLibraryImporter.cs:1261-1273`）。所以：

- 产物**不要**先手放进库根再 import ⇒ 会得到 `名字_2`，同一批 patch 变两个节点、部署两遍。
- **正确顺序**：产物落**库外**暂存目录 → `import plan/validate/apply`。这条也是 `custom-model sectional apply` 走的路（`CliCustomModelSectional.cs:158-175`）。
- 已经手放进库根了 → 只能走 `library sync`，并接受"只是普通 Mod、没有关系、没有备注"这个现状。

## 14.2 GUID 有两种写法，改写时必须用对

| 位置 | 格式 | 实例 |
|---|---|---|
| `library.json` 的 `node.id` | **D**（带横线） | `66ba4553-2334-483e-9abe-2a58d1e74018` |
| `metadata.hostModGuids` | **N**（32 位无横线） | `66ba45532334483e9abe2a58d1e74018` |
| 各 mod 的 `option.json` `hostModGuids` | **N** | 同上 |

我自己在核对"家族 vs Option 关系"时就踩了这个：拿 D 格式去比 N 格式，全判成"没有关系"。

## 14.3 挂载顺序：先挂新模型，再把材质挂到**新模型**下

家族场景下这是硬顺序：

1. 先 `option parent add <新模型GUID> --host <原宿主GUID>`（挂到同一个宿主）；
2. 再把 N 个材质 Option 挂到 **新模型**（`--host <新模型GUID>`），**不是**原模型。

挂错了就是"新模型 + 旧模型的材质"——两个变体共用一套材质，正好违反这次要解决的事。

## 14.4 `mod clone` 的动作序列（库侧，与 §13.5 合并后的完整版）

| # | 动作 | 失败时要做的 |
|---|---|---|
| 1 | 读原节点 + `option show` 拿父级 + 读 `vpk2patch.json` 的 `resolutionSet` 定位家族 | — |
| 2 | **库外**暂存：新 token、不传 `--reuse-model`、家族全部 VPK 一起重转 | `CustomModelOwnershipService.RemoveAsync(newModelKey)` 回滚工作区登记 |
| 3 | 清理暂存：删 `option.json` / `.hd2modmanager-logical.json` / `*.bak-*` | — |
| 4 | 复制用户意图：`wardrobe.json` / `Overwrite\components.json` / `icon.*` / 名称 / 备注 | — |
| 5 | `import plan → validate → apply --confirm`（可带 `--add-to-profile`） | 保留暂存目录供排查 |
| 6 | `option parent add`：新模型 → 原宿主；各材质 → 新模型 | 报告而不是静默 |
| 7 | 自检：`library sync inspect` 无新增；两份 `custom-model.json` key 不同；新 `option.json` 的 host 指向新 GUID；**原 mod 的文件逐字节未变** | — |
| 8 | **不自动建映射** | — |

第 8 步不能省事：一个目标护甲只能挂一个模型（`RuntimeArmorRegistryService.cs:1006-1020`），所以"两套外观同时在场" = 两条映射指向**两件不同的护甲**。目标由用户在护甲映射页选，命令不替他决定。

## 14.5 对前文的两处修正

1. **§12.2 的 `option.json` 一行要改**：不是"改写宿主 GUID"，而是"**删掉，改由 clone 显式挂载**"。因为删掉比改写安全——改写要处理 N/D 两种格式，而且 import 的启发式仍可能把它清成 Standard 并删文件（`ModLibraryImporter.cs:770-778`），两套逻辑打架。
2. **§12.4 的 P2 位置要提前**：同步器**目前不读** `option.json`（读它是 P2 才做的）。这意味着在 P2 之前，**"复制体自描述"这件事必须由 clone 自己保证**——不能指望同步器把关系读回来。所以 clone 不能等 P2，只要 P1 的家族判定就绪就能做；反过来，P2 做完之后 clone 的这条手工挂载逻辑可以简化。

---

# 15. 第 5 个风险：`VpkPayloadSwap` 会把旧身份"保留"下来（推翻我 §13.5 的做法）

## 15.1 我上一轮的方案在这里是错的

§13.5 我写的是"用新 token 重跑转换、**不传 `--reuse-model`**"。这条**执行不了**——因为重构建路径是自动探测的，没有开关：

```csharp
// VpkRebuildService.cs:123-124
var previousModelPath = Path.Combine(payloadDirectory, "custom-model.json");
var reuseModel = File.Exists(previousModelPath) ? previousModelPath : null;
// :191-192
if (reuseModel is not null) arguments.AddRange(["--reuse-model", reuseModel]);
```

**文件在，它就会传。** 而换载荷又把身份文件"保留"下来：

```csharp
// VpkPayloadSwap.cs:31
public static readonly string[] PreservedFileNames = ["custom-model.json", SectionGroupFile.FileName];
```

注释写得很清楚：这两个文件"记的是这具素体是谁（key / unit id）与哪些 section 算开关，换一批产物不该把它们冲掉"。**对重构建完全正确，对复制完全反向。**

## 15.2 于是复制会走进一条"警告而不是失败"的静默坏路

如果把复制实现成"拷贝文件夹 + 走重构建路径"，实际会发生：

1. 复制来的文件夹带着原件的 `custom-model.json`（旧 key + 旧 `pieces[].unit`）；
2. `VpkRebuildService` **看到文件存在就自动传 `--reuse-model`** → unit id 被**沿用**，而不是换新；
3. 为了让 id 换新，你只能先删掉它 → id 重新随机；
4. 但 `VpkPayloadSwap` 又把 `components.json`（键是**老** unit id）**原样保留**；
5. ⇒ patch 里是新 id、`components.json` 里是老 id，开关全部指向不存在的 unit；
6. ⇒ 只得到一条 `warnings.Add("禁用集合里的 unit … 不在这个 patch 里")`——**警告，不是失败**。

**全库没有任何守门管这件事**：材质塌陷守门不管、`isUnitMod && sections.json` 那条也不管。

## 15.3 材质塌陷守门有洞，而且"换 token 的副本"恰好能穿过去

守门有三处漏洞：

1. `previousMaterials > 0` 才设防 —— `sections.json` 读不到时为 0 ⇒ **完全不设防**；
2. 材质包路径上 `FindPatch` 找不到且 `Patches.Count > 1` ⇒ `previousPackMaterials = 0` ⇒ 完全不设防；
3. **只比数量、不比集合** —— 12 → 12 就放行。

第 3 条正是"只给模型包换 token"能过门的原因：模型包 12 个材质 id 全变，而 12 个材质包定义的仍是旧 id，数量对上 → 放行 → **产物自洽、进游戏白模**。

## 15.4 `sections.json` 的 sha 从来没人校验

设计如此（`WardrobeSectionBaker.cs:44-48` 明确写"故意不校验，第一次烘焙后 sha 必然变"）。唯一发现者是 `Bake`，但它用的是另一套判据：**每个要写的 dword 当前必须 ∈ {0, 记录值}**。

后果两层：
- **表现延迟**：编辑器树照常显示（只读 materialId/slot），用户以为正常，**直到点「应用/切换」才炸**；
- **更危险的一半**：若偏移恰好落在一个等于 0 或等于记录值的 dword 上，会**静默把别人的字段清零**（源码注释自认这是最难查的失效）。

## 15.5 `resolutionSet` 目前是"只写不读"

我 §13.7 建议"用 `resolutionSet` 当家族定义"——方向对，但要补一句：**现在没有任何代码读它**。全库 grep 只有一处写（`CliApplication.cs:2316`）；`VpkConversionReport.cs:189-200` 只读 `idNamespace` / `conversion` / `queue` / `notes` / counts。

两个相关事实：
- **它能 100% 还原 `--material-source`**（证据：`CliApplication.cs:2088` 算 `resolutionPaths = vpkPaths ∪ extraPaths − switchPaths`，`:2316` 原样落盘）；
- **但组件开关包被故意剔除**（它们的 VMT 与真材质裸名相同，混进去会把真材质顶成 `$no_draw`）⇒ 照它重跑必须另用 `--component-source-list` 补回。

⇒ 所以要落这条建议，得**新写读取端**，这是 P1 的一部分。

## 15.6 `FindSiblingVpks` 的第三种失效最隐蔽

它靠三个条件认亲：同 `idNamespace`（读兄弟的 `vpk2patch.json`）+ 目录顶层有 `*.vpk` + 报告可读。三种失效：

| 失效 | 表现 |
|---|---|
| 兄弟被删 | 静默不在列表（守门会拦） |
| **兄弟在、但 `vpk2patch.json` 丢了/坏了** | `TryRead` 返回 null → `continue`，**连 note 都不加**（最隐蔽） |
| 来源不在库里（游戏目录 / `附加材质\`） | 永远找不回 |

## 15.7 修正后的 checklist

**复制 = 拷贝模型包 + 它的全部 Option 子包 → 删掉身份文件 → 整批同一新 token 重转 → 走导入路径。**

而"用来源重构建"那条路**不能用于复制**，因为它保留旧身份文件。

**要删的（不是复制）**：

| 文件 | 为什么不能照抄 |
|---|---|
| `custom-model.json` | 旧 key + 旧 unit id 基；留着会被自动 `--reuse-model` 沿用 |
| `components.json` | 键是老 unit id；留着 = 开关指向不存在的 unit（只给警告） |
| `wardrobe.json` | `targetArmorArchiveId = "custom-model:custom-<原件key>"`；照抄 = **副本抢原件的护甲** |
| `Overwrite/` | 它本身就是"numIndices 烘成 0"的字节状态，必须按新 id 重烘 |
| `option.json` | 旧宿主 GUID；照抄 = 悬空 Option（§14.1） |
| `vpk2patch/sections.json` | 绑 sha256 + TOC 相对偏移，字节一变必失效，必须重算 |

**要保留的**：`icon.*`、名称、备注（将来 `mod.json`）、以及 `Overwrite\components.json` 里**用户关掉了哪些件**这个意图本身——但它的**表达形式**（unit id）必须重映射。

## 15.8 由此产生一个必须回答的设计问题

用户的 section 配置（关掉了哪些件）是**按 unit id 表达的**；复制会换 unit id ⇒ 配置必须**翻译**，不能照抄也不能直接丢。

可行的路只有一条：**按位置重映射**。unit id = `seed + index`，而 `index` 由 plan 顺序决定；同一份来源 VPK + 同样参数 ⇒ plan 顺序确定 ⇒ 老 id[i] ↔ 新 id[i]。所以理论上可以做位置映射。

但前提是"plan 顺序稳定"，这一点**必须先验证**（同一份 VPK 用不同 token 跑两次，逐 index 的 unit 是否一一对应）。如果稳定，复制就能保住用户配置；如果不稳定，就只能放弃用户配置并明确告诉用户"section 开关需要重配"。

**这是 clone 能不能"保住用户意图"的分水岭，建议作为 P1 的第一个验证项。**

