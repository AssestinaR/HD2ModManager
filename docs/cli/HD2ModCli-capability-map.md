# HD2 Mod CLI（hd2mod）能力全景与评价

> 生成时间：2026-10-14 ｜ 对应协议版本 `1.27` ｜ 工程 `HD2ModCli`（net10.0，27,522 行）
> 本文是**盘点 + 评价**文档，不是使用手册。使用手册以 `hd2mod --help` 与 `hd2mod capabilities --json` 为唯一权威来源。

---

## 0. 一句话定位

`hd2mod.exe` 是**面向 Agent 的、以"计划-验证-执行"为骨架的 Mod 库操作器**：GUI 给人用，CLI 给 Agent 用。它的设计哲学不是"命令快"，而是**"任何写操作都必须先被冻结成计划、被重新验证、再凭显式 `--confirm` 执行"**。

---

## 1. 数据规模总览

| 维度 | 数量 |
|---|---|
| 顶层命令族 | **21 个**（`--help` 视角）：`agent` / `armor-registry` / `assets` / `capabilities` / `context` / `custom-model` / `decoration` / `deploy` / `diagnostics` / `equipment` / `game` / `gamedata` / `help` / `host-derived` / `import` / `library` / `mod` / `option` / `patch` / `profile` / `source` / `status` |
| `capabilities --json` 登记的条目 | **140 条** |
| `--help` 列出的命令行 | 151 行 |
| 可导出能力 Skill | **28 个**（+1 路由器 +1 退役聚合 = 30 个定义） |
| CLI 异常错误码 | **372 个去重**（`CliUserException`，672 处调用） |
| 诊断条目码 | **275 个去重**（各类 `Cli*Diagnostic`，303 处调用） |
| 错误码合计去重 | **636 个** |
| 参数校验辅助函数 | **121 个** `Ensure*` / `Require*` |
| CLI 测试文件 | 33 个 `*CliTests.cs` + 夹具 |
| 安全等级取值 | 5 种（见 §3） |

---

## 2. 基层能力（Atomic / Primitive）

这一层是"能读能写的最小动作"，不含工作流编排。

### 2.1 环境与自描述

| 命令 | 作用 |
|---|---|
| `hd2mod help` | 完整人类可读命令表（151 行） |
| `hd2mod version --json` | CLI 版本 + 协议版本 |
| `hd2mod capabilities --json` | **机器可读能力清单**：每条含 `name` / `safety` / `description`，另带 `workflow`、`notSupportedYet`、`safetyRules` |
| `hd2mod status --json` | 管理器 / Mod 库 / GameData 三路径的配置状态 |
| `hd2mod game locate --json` | 自动探测游戏 data 目录（Steam 注册表 → `libraryfolders.vdf` → 常见库根），并报告与管理器配置是否一致 |
| `hd2mod context export --output <file>` | 导出 AI 上下文 JSON（一次性快照） |

### 2.2 工作区解析（所有命令的公共地基）

`CliWorkspace.Open()` 从 `--manager-root` + `--library` 推导三根路径：

- **`--library` 显式优先**，可传目录也可传 `library.json` 本身；
- 否则从 `--manager-root/settings.json` 读 `ModLibraryFolder` / `GameDataFolder`；
- 相对路径按管理器目录解析；
- 读不到就记进 `issues`，不猜、不建空库（`LibrarySnapshotNotFound` 明确拒绝"CLI 不会创建空库或迁移旧数据"）。

### 2.3 Mod 元数据（不碰 Patch 字节）

`mod list` / `show` / `analyze` / `external inspect` / `create` / `rename` / `notes set|clear` / `image show|set|clear` / `convert-to-option`

### 2.4 选项（Option）关系与顺序

`option show` / `enable` / `disable` / `enable-all` / `disable-all` / `move` / `insert`（=`move` 别名）/ `parent add` / `parent remove`

支持**一个 Option 有多个父级**，每个父级独立持有启停与顺序状态。

### 2.5 Profile

`profile list` / `show` / `create` / `delete` / `rename` / `set-active` / `clear-active` / `add` / `remove` / `insert` / `move` / `reorder`

### 2.6 GameData 只读查询（5 个子族）

| 子族 | 量级 |
|---|---|
| `gamedata status` | 1 条 |
| `gamedata archive list\|show` | 分页读取持久化原版 Archive 索引 |
| `gamedata asset find` | 用十六进制 TypeID/FileID 查映射 |
| `gamedata override status\|list\|show` | 读当前实际 Data-Patch 覆盖链与赢家 |
| `gamedata browser` | **高开销全景**：Archive + 库 + 活动配置 + 实际赢家一次性对照 |

### 2.7 GameData 直读提取（**不依赖索引、不依赖管理器安装**）

`gamedata extract list|show|asset|archive` —— 直读游戏数据包，能把单个资产的三段载荷（`toc`/`stream`/`gpu_resources`）写出，或整包按类型分目录导出 + `extract-manifest.json`。

> 这一族是**最独立、最"原始"的能力**：它绕开了 SQLite 索引和管理器，直接和游戏包对话。

### 2.8 护甲注册协议（Runtime Armor Registry）

`armor-registry status|show|plan|validate|apply` —— 只改外置 JSON 协议文件（`armor-registry.protocol`），由 Lua 在运行时轮询应用；**绝不触碰加密的 `generated_customization_armor_sets.dl_bin`、运行状态、定位缓存或进程内存**。

### 2.9 Agent Skill 自举

`agent skills list|export` / `agent skill export` —— CLI **能把自己的能力文档导出成 Agent Skill 目录**，带 SHA-256 manifest。

### 2.10 命令族速查表（含"四段式完整度"）

这张表是本文最实用的部分：**一眼看出哪个族缺哪一段**。

图例：`✅` 有 ｜ `—` 无 ｜ `(别名)` 兼容入口

| 族 | 子命令数 | inspect | plan | validate | apply | 备注 |
|---|---|---|---|---|---|---|
| `import` | 8 | ✅ `import inspect` | ✅ | ✅ | ✅ | +`batch` 全套 8 条 |
| `library sync` | 4 | ✅ | ✅ | ✅ | ✅ | 完整四段 |
| `decoration package` | 3 | — | ✅ | ✅ | ✅ | 预览靠 `decoration inspect source` |
| `decoration edit` | 5 | ✅ `export` + `inspect` | ✅ | ✅ | ✅ | **五段**：export→inspect→plan→validate→apply |
| `mod export` | 3 | — | ✅ | ✅ | ✅ | 无独立 inspect |
| `profile align` | 5 | ✅ `export` + `inspect` | ✅ | ✅ | ✅ | **五段**，同 decoration edit |
| `deploy` | 3 | — | ✅ | ✅ | ✅ | **无 inspect** |
| `deploy deactivate` | 3 | — | ✅ | ✅ | ✅ | **无 inspect** |
| `patch repackage` | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `patch repair`（Same-key） | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `patch repair batch` | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `equipment helmet-merge` | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `equipment armor-normalize` | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `equipment cross-armor` | 4 | ✅ | ✅ | ✅ | ✅ | 完整 |
| `host-derived` | 7 | ✅ | ✅（4 种） | ✅ | ✅ | 完整，plan 有 4 个变体 |
| `gamedata index` | 4 | ✅ | ✅（3 种） | ✅ | ✅ | 完整 |
| `assets metadata` | 4 | ✅ | ✅（唯一联网） | ✅ | ✅ | 完整 |
| `armor-registry` | 5 | — | ✅ | ✅ | ✅ | **预览靠 `status`/`show`**，不叫 inspect |
| `mod delete` | 5 | ✅ `preview` | ✅ | ✅ | ✅ | **预览叫 `preview`**，另有直连 `mod delete`（别名） |
| `option` | 9 | ✅ `show` | — | — | ✅ | **单步写入**，无计划流程 |
| `mod`（元数据） | 20 | ✅ `list`/`show`/`analyze` | — | — | ✅ | 元数据是单步写入 |
| `profile`（基础） | 12 | ✅ `list`/`show` | — | — | ✅ | 单步写入；仅 `align` 有完整流程 |
| `source` | 1 | ✅ | — | — | — | 只读解析（VPK） |
| `custom-model` | 2 | ✅ | — | ✅ | — | `convert` 用 `--confirm` 直接写，**无 plan/validate** |
| `diagnostics armor` | 6 | 交互式 | — | — | — | **完全另类**：交互向导 |
| `agent` | 4 | ✅ `list` | — | — | ✅ `export` | 自举 |

**从这张表能直接读出的三个不平整处：**

1. **"预览"这个词有四种写法**：`inspect`（多数）、`status`/`show`（armor-registry）、`preview`（mod delete）、`export`（decoration edit / profile align 的规格导出）。Agent 不能靠词根推断。
2. **有三个族完全没有 plan/validate**：`option`、`mod` 元数据、`profile` 基础操作。它们是**单步写入**——虽然有 `--confirm` 和写租约，但**没有"可审阅的中间产物"**。这也是 Spec 里 `mod-management` 等单步命令被标为"Agent 应优先使用 plan/validate/apply"的原因。
3. **`custom-model convert` 是唯一的例外**：会写库（登记素体）+ 输出 Patch，但**没有 plan/validate**，只有 `--confirm`。在整体四段式设计里是**唯一的破例**。


---

## 3. 包装的向导类能力（Workflow / Wizard）

这一层是"把多个基层动作编排成一个受控流程"，也是 CLI 真正的价值所在。

### 3.1 统一的四段式骨架

绝大多数写流程都长这样：

```
inspect  →  plan  →  validate  →  apply --confirm
 (只读预览)  (冻结计划)  (重验漂移)   (执行)
```

**没有 `--dry-run`**——这是刻意的：计划是**落盘的、可审阅的、带指纹的文件**，比"干跑一遍"更可审计。

计划的健壮性机制：
- 每类计划有独立 `schema` + `version`（如 `CliImportPlanDocument.CurrentSchema` / `CurrentVersion`），版本不符直接拒绝并要求重新生成（`*PlanVersionUnsupported`）；
- `validate` 会重扫现实并**拒绝任何漂移**（库状态、Patch 指纹、GameData 索引、文件内容）；
- 在**库写租约**（跨进程）+ 桌面端关闭的前提下才执行。

### 3.2 28 个可导出 Skill = 28 条受控工作流

| 分类 | Skill |
|---|---|
| **读状态** | `library-inspection`、`patch-analysis`、`gamedata-inspection`、`gamedata-extraction` |
| **导入** | `safe-import`、`batch-import` |
| **库维护** | `library-synchronization`、`safe-library-deletion`、`mod-export`、`mod-metadata`、`mod-media`、`option-management` |
| **Profile** | `profile-management`、`profile-alignment` |
| **部署** | `safe-deployment`、`lua-reshade-external-deployment`、`generated-patch-integration` |
| **Patch 生产** | `patch-resource-repackaging`、`helmet-to-armor-merge`、`cross-armor-transfer`、`same-key-reconstruction` |
| **原地修复** | `batch-patch-repair`、`host-derived-overwrite` |
| **装饰** | `decoration-package`、`decoration-edit` |
| **索引 / 缓存** | `gamedata-index-management`、`asset-metadata-management`、`armor-registry` |

每条 Skill 只有 14–55 行，是**窄而自足的操作手册**；路由器 `cli-router` 负责"按任务分发"，并**明确标注 `mod-management` 是退役聚合指针、不可导出**——这种"主动标记死路"的做法在 Agent 工具里很少见，值得肯定。

### 3.3 独立诊断向导（**唯一的交互式例外**）

`diagnostics armor wizard|quick|matrix|composition|memory|recover`

这是一族**面向人类、交互式、非 JSON** 的故障诊断工具（军械库崩溃六组对照实验），带：原配置优先备份、断点恢复、进程守护（`CliDeploymentProcessGuard`）、证据 ZIP 脱敏导出。

> ⚠️ 它和 CLI 主体的"Agent 静默 JSON"哲学**完全相反**：`--json` 会被显式拒绝，输入重定向也会被拒绝。它是"同一可执行文件里的第二个程序"。

---

## 4. 安全模型（这一层做得相当扎实）

| 机制 | 说明 |
|---|---|
| **写租约** | 库根 / Manager data 目录的跨进程独占锁（`CliLibraryWriteSession`、`CliManagerDataWriteLock`） |
| **进程探测** | 桌面端、游戏进程必须退出（`CliManagerProcessProbe`、`CliDeploymentProcessGuard`） |
| **写目标守卫** | `CliWriteTargetGuard` 禁止输出落在库内 / 游戏目录内 / 游戏根内 |
| **归属证明** | 部署只接管有完整归属证明的 Patch，**"绝不接管、清空或覆盖未登记的 Patch"** |
| **不执行 Mod Lua** | 只做编译校验，不在本地 Lua 运行时执行 |
| **危险参数门禁** | `--allow-unsafe-lua` 是唯一"解锁风险"的开关，且逐命令校验 |
| **参数白名单式校验** | 121 个 `Ensure*` / `Require*` 函数逐命令拒绝不相关参数（如 `--registry-root` 只准用于 `armor-registry`） |

`safety` 等级分布（140 条中）：

| 等级 | 条数 | 含义 |
|---|---|---|
| `read-only` | 59 | 不写任何文件 |
| `explicit-confirm` | 46 | 需 `--confirm` 的写入 |
| `explicit-export` | 29 | 只写显式命名的输出文件 |
| `read-only-expensive` | 5 | 只读但昂贵（需预期等待） |
| `explicit-export-network` | 1 | **唯一允许联网**（`assets metadata plan`） |

---

## 5. 面向 Agent 的设计——做得好的地方

### 5.1 `capabilities --json` 是"能力契约"，不是帮助文本

它是**机器可读的自描述**：Agent 不必靠猜或靠读长文档就知道有哪些命令、各自什么安全等级。这在同类工具里属于上乘。

### 5.2 自举式 Skill 分发

`agent skills export --output <dir>` 让 CLI **自己把自己的使用手册交付给 Agent**，还带 SHA-256 清单和版本号。避免了"产品版本和文档版本脱节"这个老大难。

### 5.3 分层文档：路由器 + 窄技能

不是给 Agent 一本 400 页手册，而是"先读路由器 → 只导出这次任务需要的那一页"。**对上下文窗口友好**。

### 5.4 主动标注不可用路径

`notSupportedYet`、`IsLegacy` 标记、`capabilities` 里明说"基于桌面设置的隐式自动导入…不支持"。**Agent 不会在死路上反复试**。

### 5.5 错误输出双模

- 人类：`错误 [Code]：消息`（stderr）
- Agent：`{"ok":false,"error":{"code":"...","message":"..."}}`（stdout）

退出码：`0` 成功 / `1` 未处理异常 / `2` 用户错误 / `130` 取消。

### 5.6 十六进制 ID 的字符串化

`gamedata` 系列**强制 16 位十六进制字符串**，文档明确写"绝不转成 JavaScript 数字"——直接堵住了 Agent 用 JS 处理 64 位 ID 时的精度丢失坑。这是**踩过坑才知道要防的**。

---

## 6. 弱项盘点（按优先级）

### 🔴 P0 — 影响正确性 / 可恢复性

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| **1** | **取消信号断在入口这一环** | 叶子实现层**普遍已支持** `CancellationToken`（63 个文件里都有，`CliLibraryMutationOperations` 达 31 处）；但 **`CliApplication.cs` 里出现 0 次**——命令入口既不创建 `CancellationTokenSource`，也不挂 `Console.CancelKeyPress`，更不往下传。`RunAsync` 里 `catch (OperationCanceledException) → 130` 因此是**事实上的死代码** | 长任务（`gamedata browser`、`patch repair batch inspect`、`mod analyze`）**无法优雅中断**；Ctrl+C 走运行时默认终止，可能截断原子写（`WriteAtomicTextAsync` 的临时文件残留）。**注：这是"最后一公里没接"，不是"没有取消能力"**——修起来比从零加要便宜得多 |
| **2** | **`apply` 失败后的恢复语义不统一** | 各 Skill 的 `safetyRules` 各自描述，口径不一：`import batch` 明说"已完成来源不伪装为自动回滚"；`host-derived apply` 说"失败时回建已完成主体"；`mod delete apply` 有 `PostCommitIncomplete` 状态 | Agent 无法用**统一规则**判断"失败后库处于什么状态、要不要人工介入"。这是最容易被误用的一类信息 |
| **3** | **错误码 636 个去重、无集中注册表** | 372 个异常码 + 275 个诊断码，全部是分散在 30 个文件里的字符串字面量 | Agent 无法枚举、无法查文档、无法做稳定映射；无法判断"这个码是永久失败还是可重试"。**没有 `hd2mod errors --json`** |

### 🟠 P1 — 影响 Agent 使用效率

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| **4** | **无统一"plan 生命周期"管理** | 计划是散落的 JSON 文件，各自带 schema/version | 没有 `plan list` / `plan status` / `plan discard`。Agent 在长会话里**记不住自己生成过哪些计划、哪些已过期**；只能靠自己的对话上下文，一旦上下文被压缩就断线 |
| **5** | **`capabilities` 与 `--help` 双源，且只有单向测试** | 140 条 vs 151 行，两处手写。已有的 `CliWorkflowCoverageTests.CapabilityProjection_CoversPrimaryDesktopAgentWorkflows` 只做**单向**断言——"关键工作流的命令**是否登记在 capabilities 里**"；**没有**反方向（`--help` 有而 capabilities 漏登记），也没有 `--help` ↔ `capabilities` 双向一致测试 | 二者可能静默漂移；Agent 若只读其中一个，会漏掉或读到不存在的命令 |
| **6** | **输出 schema 无版本化承诺** | 只有计划文件有 schema；**命令的 JSON 输出没有** | Agent 写死的字段解析在 CLI 升级后可能静默失效。`protocolVersion` 存在于 `version`/`capabilities` 输出里，但**没有声明"哪些命令的输出受该版本保证"** |
| **7** | **`inspect` / `plan` / `validate` 命名不完全统一** | 多数族是 `inspect→plan→validate→apply`，但：`mod delete` 的预览叫 `preview`；`deploy` / `deploy deactivate` / `mod export` / `decoration package` / `armor-registry` **没有 inspect 段**（其中 `armor-registry` 用 `status`+`show` 顶替）；`decoration edit` / `profile align` 是**五段**（多一个 `export` 规格导出） | Agent 不能靠"族名 + 固定后缀"推断命令存在性，**必须逐个查 capabilities**（见 §2.10 速查表） |
| **8** | **无 stdin 支持** | 全工程无 `-` 作为 stdin 的约定 | 大计划文件只能落盘传递；Agent 无法用管道串接。这与其"计划即文件"哲学一致，但**限制了组合性** |

### 🟡 P2 — 体验 / 工程性

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| **9** | **分页游标语义易错** | `--cursor` 在"索引或覆盖图变化后会拒绝混用" | 这是**正确设计**，但 Agent 容易在长分页中触发；文档只在 `gamedata-extraction` 里详细说明 |
| **10** | **121 个 `Ensure*` 手写校验** | 无模板化 / 无声明式 | 新增命令易漏校验；同类错误在不同命令里可能用不同码 |
| **11** | **`CliApplication.cs` 5619 行单文件** | 线性 `if` 链分派（不是表驱动） | 维护成本高；命令注册与实现耦合，**无法程序化枚举**（只能靠 `capabilities` 手写表） |
| **12** | **诊断向导族与主体哲学冲突** | 交互式、拒绝 `--json`、拒绝重定向 | 同 exe 两套心智模型；Agent 若误调会得到 `InteractiveRequired` |
| **13** | **无 shell 补全** | 无 bash/zsh/pwsh completion 生成 | 对 Agent 影响小，对人类用户影响明显（但这工具主打 Agent，故列 P2） |

---

## 7. 我的总评

### 7.1 优点（超出同类的部分）

1. **安全模型是"设计出来的"，不是"补上去的"**。写租约 + 进程探测 + 写目标守卫 + 归属证明 + 四段式，形成闭环。多数 CLI 工具只有 `--force`。
2. **计划即文件**（而非 `--dry-run`）是更适合 Agent 的选择：可落盘、可跨进程传递、可被人类审阅、可被 `validate` 重验。
3. **自描述 + 自举文档**：`capabilities` + `agent skills export` 让 CLI 能把自己的契约交付出去。
4. **主动标注死路**（`notSupportedYet`、`IsLegacy`）——节省 Agent 的试错预算。
5. **十六进制 ID 字符串化**等具体防坑措施，说明设计者理解 Agent 的真实失误模式。

### 7.2 核心短板

**这个 CLI 已经很强地解决了"怎么做才安全"，但在"Agent 怎么做才不迷路"上还有明显缺口。** 具体是三条：

1. **可枚举性不足**：636 个错误码无索引、命令名无规律、计划无统一管理 → Agent 需要靠**大量试错 + 大量上下文**才能操作，而它的目标用户恰恰是上下文有限的 Agent。
2. **可恢复性不统一**：失败后的状态语义散落在 28 个 Skill 的散文里，没有机器可读的统一声明。
3. **中断能力为零**：长任务无法取消，这在 Agent 场景下是**实际风险**（超时后进程悬挂、原子写被截断）。

### 7.3 我的建议（按性价比排序）

| 优先级 | 建议 | 理由 |
|---|---|---|
| **1** | 加 `hd2mod errors --json`（从代码里生成错误码目录，含 `retryable` / `category` 字段） | 一次投入，直接解决 P0-3 + P1-4；且可用源码扫描自动生成，几乎零维护 |
| **2** | 把 `capabilities` 改成**从命令注册表生成**，并加 `--help` ↔ `capabilities` **双向**一致性测试（现有测试只覆盖单向） | 消除 P1-5、P2-11；同时让 `CliApplication.cs` 有机会瘦身 |
| **3** | 给每个 `validate`/`apply` 的 JSON 输出加**统一的 `outcome` 字段**（`succeeded` / `partially-applied` / `rolled-back` / `blocked`） | 解决 P0-2，让 Agent 用一条规则判断后续动作 |
| **4** | **接 `CancellationToken`（只需接入口这一环）** | 解决 P0-1；叶子层已有 63 文件支持，工作量集中在 `CliApplication.DispatchAsync` 加 `CancellationTokenSource` + `CancelKeyPress`，再把 token 透传给各 `RunAsync`。**属于性价比最高的修复之一** |
| **5** | 加 `hd2mod plan list|status|discard --manager-root <dir>`（计划集中登记在管理器目录） | 解决 P1-4；也是"计划即文件"哲学的补完 |
| **6** | 在 `capabilities --json` 里为每条命令加 `outputSchema` 字段（哪怕只是 schema 名字 + 版本） | 解决 P1-6，给 Agent 稳定的解析契约 |

---

## 8. 附：文档与权威来源对照

| 来源 | 用途 | 权威性 |
|---|---|---|
| `hd2mod --help` | 人类可读全命令表 | **唯一命令表之一** |
| `hd2mod capabilities --json` | 机器可读能力契约 | **唯一命令表之一** |
| `HD2ModCli/SKILL.md` | Agent 路由器 | 导航，非权威 |
| `HD2ModCli/skills/*/SKILL.md` | 28 条窄工作流手册（14–55 行/个） | 逐命令细则 |
| `HD2ModCli/README.md` | 入门三步 | 入门 |
| `HD2ModCli/ARMOR-DIAGNOSTICS.md` | 诊断向导人工手册 | 诊断族专用 |
| 本文 | 盘点 + 评价 | **不是权威命令表** |

---

## 9. 补记（2026-10-14 增补）：Patch 底座原语 + 装备权威表

应"增强基层能力"的目标，本轮向 CLI 补了 **8 条"文件进、文件出"的底座命令**，全部走**纯转换**安全线（不取库租约、不走四段式、只写 `--output` 新目录）：

| 命令 | safety | 底座 | 作用 |
|---|---|---|---|
| `patch entries list <patch_0>` | read-only | `PatchTocScanner` | 枚举**任意 Patch 文件**（库外/原版/下载而来）的条目、类型、三段大小 |
| `patch entry show <patch_0> <typeid> <fileid>` | read-only | `PatchTocScanner` | 读单条目定位与大小 |
| `patch entry export ... --output <dir>` | explicit-export | `IPatchEntryPayloadReader` | **把一条 assetkey 拆成 toc/stream/gpu 三个裸文件**（用户原话的"解包成每一个 assetkey 一个离散文件"） |
| `patch compose --header <tmpl> --pick <src> ...` | explicit-export | `PatchSubsetWriter.WriteSelectedAsync` | **指定 header + 多源合并**（"头盔并入盔甲 archive"的独立底座） |
| `patch edit <src> --remove-key/--add-key/--keys` | explicit-export | `PatchWorkspaceChangeSet` + `PatchArchiveWriter` | 条目级增删改 / 就地隐藏 Unit |
| `patch hide-unit <src> --keys <...>` | explicit-export | `HiddenUnitGenerator` + `CanonicalUnitRebuilder` | 独立隐藏 Unit 生成 |
| `gamedata equipment list --kind cape\|armor\|helmet\|all` | read-only | `GameDataAuthorityProvider` | **读游戏装备/披风权威表**，输出 Unit AssetKey 列表（`keys[]` 可直接喂给上一条） |
| `gamedata equipment show ...` | read-only | 同上 | 输出命中套装的完整 Unit 明细 |

**`--keys` 统一支持三种形态**：内联 `0xtypeid:0xfileid,...`、`@file.json`（JSON 数组或含 `keys` 数组的对象，即 `equipment list` 的直接输出）、内联 JSON。

**实测验证**（真实数据，非构造）：`patch entries list` 对 `Lilium-Sancta-RE2310` 枚举出 64 条；`patch hide-unit` 把一个 **16,917,432 B 的可见承载 Unit GPU 压缩到 2,984 B**（TOC 保留骨骼 69,244→71,716），耗时 2.8 s；`patch compose` 从两个不同来源 patch 各取一条合并成新 patch；`patch edit --remove-key` 正确减条且源文件 sha256 不变；`gamedata equipment list --kind cape` 读出游戏里 **118 个披风**及其显示名与 Unit AssetKey。四组负对照全部正确报错。

**契约同步**：`capabilities --json` 从 140 → **148** 条；新增 2 个 Agent Skill（`patch-primitives`、`equipment-facts`），可导出 Skill 从 28 → **30**；`--help` 与 capability 覆盖测试双向补断言。新增测试 `PatchPrimitiveCliTests`（7 条，含负对照），CLI 测试全量 **154 通过 / 0 失败**。

> 仍未做（依赖 core 侧新能力，非本轮范围）：`--add-key` 目前只能引用**同一 patch 内已有条目**，尚未支持"从外部 patch 复制条目进来"。原语层已具备（`PatchArchiveAdditionalEntry` 可带任意字节），只需在命令层接一个 `--add-from <src> <key>` 参数。
