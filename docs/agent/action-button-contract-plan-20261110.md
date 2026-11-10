# 按钮契约重构规划：汉堡菜单 + 底栏（2026-11-10）

> **状态**：待审（v2，已按四方专家评审重做处方部分）。
>
> **版本说明**：v1 由主 agent 起草；随后用外部模型（`openwrt-cn/cn:deepseek-v4.1-flash`）
> 分四路独立审查（架构合理性 / 破坏面与迁移顺序 / 契约完备性 / 事实核验）+ 一次主裁合并，
> 结论为"诊断可信、处方需重做"。**v2 保留全部诊断，重写处方**，并逐条记录被推翻的判断。
>
> **本文要解决的一句话**：全仓库"这个按钮此刻能不能用"由**四份互不知情的实现**分别回答，
> 而"不能用的原因"没有任何统一的呈现约定；于是用户看到的是灰按钮——不知道为什么不给点。

**v3 变更（用户 2026-11-10 定，最重要的一次收敛）**：**按钮永不禁用、永不配逐按钮文字，
一律按最坏情况（密集场景）设计；点了之后由执行器发现不能执行再报错。**
⇒ 状态塌成两态（`Shown` / `Hidden`），`Blocked` / `Busy` / 原因渲染整节删除。
理由见 §2.2；UI 完整性硬约束见 §2.4；两条护栏见 §2.5。

**证据标注**：【核实】= 主 agent 亲自读过该行；【审计】= 只读审计报告所载（附 file:line，未经二次复核）；
【评审】= 四方评审结论（含 file:line，主 agent 抽查过关键几条）；【未确认】= 推理得出、未运行验证。

**v2 修订摘要（哪些被推翻）**：

| v1 的说法 | v2 | 依据 |
|---|---|---|
| "说明原因的出口物理上做不到，必须先建出口" | **错**。已有三种常驻出口形状，缺的是统一契约 | `CrossArmorPlanPageView.xaml:59-60`【核实】等 |
| 二态：`Shown/Hidden` + `Busy` | **错**。需要四态，`Blocked(reason)` 必须存在——用户原话"不允许**不说明原因**就禁用"本身就预设了禁用可以带原因 | `AppUpdateCard.xaml:31`【评审】 |
| `ActionResult{Ok|Failed|Canceled}` 作为返回值 | **错**。`ICommand.Execute` 是 void，`AsyncRelayCommand` 是 `async void`，结果拿不回来 | `RelayCommand.cs:18,44`【核实】 |
| D3：把 `ModStorageKind` 提到库模型 | **降格**为管理器侧只读投影，且不进指纹、不写 `ModNode` | 【评审】 |
| 底栏 `Show*` 14 个 | **17 个** | `BottomBarCoordinator.cs:86-117`【核实】 |
| 详情页 15 条动作里 13 条判据分叉 | **10 条带 `canExecute`，真正会不一致的 8 条** | 【评审】 |
| 跨甲右键菜单是违规 | **不是**。`Header` 即原因，属 Windows 菜单项标准语义 | `CrossArmorPlanPageView.xaml.cs:45,50`【评审】 |
| "解除映射目标"文案重复要改 | **不能改**。现有测试断言两处出现 | `ArmorRegistrySelectionScopeArchitectureTests.cs:122`【评审】 |
| "编辑 section 组"是违规 | **不是**。它是唯一入口，且有成文的布局理由 | `ModDetailsPageView.xaml:90-98`【核实】 |
| P0 验收"界面行为完全一致" | **撤回**，改为可判定口径（JSON 基线逐项相同） | 【评审】 |
| 底栏并发框冻结排在 P3 | **提前到 P0b**，但理由先后两次都说错了；真正只有一条"重跑不重置 `IsFinished`"（详见 §1.4b B‑1） | `VpkRebuildBottomBarViewModel.cs:208-225`【核实】 |

## 0. 用户已定的规则（不可协商）

1. **全仓库不允许出现"不说明原因就被禁用"的按钮。**
2. 分层：**UI 层只渲染**；**逻辑判断层**决定"哪个动作放在哪个按钮上、要不要出现"；
   **动作层**只按传入参数干活。
3. 用户原话（2026-11-10）：

   > 点下去才发现不行（没有可用 patch、索引没建…）是**动作执行器的活**，跟按钮无关。
   > 用户点了按钮，执行器发现不行就返回报错；报错由消息中心显示还是别处显示，都是执行器/外壳的事。

**专家对这三条的裁定**（评审，采纳）：

- 第 1 条**字面上就预设了"禁用可以存在，前提是有原因"** —— 因此需要 `Blocked(reason)`，
  而 v1 把它推成"只有执行中才许禁用"，是对用户原话的过度推演。
- 第 2 条方向正确，但"动作执行器不许有 `CanExecute`"要**精确化**：
  不许有**业务判据**的 `CanExecute`；**防重复点击必须经 `Busy`，而 `Busy` 在 WPF 里就该落成 `CanExecute`**。
  说"不许有"会把 13 处合法的防重入一起删掉。
- 第 1 条与第 2 条应**解绑**：它们是两件独立的事，绑在一起是 v1 过度抽象的根源。
- 合法/违规的界线在**语义**（正在执行 / 结构事实 / 执行结束之后），不在**机制**（是否用 `CanExecute`）。

## 1. 现状事实

### 1.1 "能不能用"有四个裁判

| 裁判 | 位置 | 说明 |
|---|---|---|
| ① 要不要出现 | `ModDetailsPageViewModel.Actions.cs:64-96` `SyncActionVisibility` | **全仓库唯一**写 `PageActionViewModel.IsVisible` 的地方【审计】；其余 4 个页面**没有这一维**，动作恒显示 |
| ② 能不能点 | 各命令构造时传入的 `canExecute` 委托 | 详情页 15 条动作里 **10 条带 `canExecute`**，其中**8 条**与①是独立表达式【评审】 |
| ③ 点击时再判一遍 | 每个 `Execute` 内部 | `ModDetailsPageViewModel.cs:1571`、`:1629`、`CloneVariant.cs:36`、`PageViewModels.cs:720`【审计】 |
| ④ 执行器内部再校验 | 各 Core 服务 | 正常，但前面三层已各判过一次 |

**WPF 侧事实**：`ButtonBase.IsEnabledCore` 把 `IsEnabled` 与 `Command.CanExecute` 相与；
`PageActionViewModel.IsEnabled` **从不被赋值**（`PageActionViewModel.cs:58-62`）【核实】
—— 它是**恒真的死字段**，不是"第二裁判"。真实可点性 = `true && ②`。
**D6 的改造对象因此不是它，而是"放置谓词与 `CanExecute` 的合流"。**

**为什么"灰掉就亮不回来"**：`RelayCommand` / `AsyncRelayCommand` 不挂 `CommandManager.RequerySuggested`
（`RelayCommand.cs:17,19-30,46`）【核实】。

**扫盘（已按评审降低严重度）**：`CanCloneVariant`（`CloneVariant.cs:22-32`）在**绑定属性里**做
`Directory.Exists` + `ModStorageKindResolver.Resolve`。但该 resolver 是 `TopDirectoryOnly`
枚举 + `File.Exists` 短路 + 可能的 1 次小 JSON 读（【评审】），**不是**"整目录递归 + 读 JSON"。
仍应消除（因为它每按一个键都可能触发），但**不是性能灾难**。

### 1.2 原因出口：已有三种形状，缺统一约定

| 形状 | 位置 | 形态 |
|---|---|---|
| ① 紧贴按钮的常驻行 | `CrossArmorPlanPageView.xaml:59-60`（`SourceScanState`） | 本仓库最接近标准的形状：按钮旁边一行，随时可读 |
| ② 底栏状态行 | `CustomModelConversionBottomBarContent.xaml:26-29`（`Status` + `HasStatus`） | 与按钮同栏，带 ToolTip |
| ③ 底栏提示行 | `VpkRebuildBottomBarContent.xaml:21-29`（`DefaultHint` / `ScopeText`） | 静态提示 + 作用域说明 |

**缺口不是"存在性"，是"统一"**，另有两条硬缺口：

1. **六个结构性原因属性零绑定**：`VpkComponentStatus`（其开关 `HasVpkComponentStatus` 在
   `ModDetailsPageViewModel.Vpk.cs:358`【核实】）、`SameKeyReconstructionSummary`、
   `MaterialPackagingSummary`、`MaterialDeliverySummary`、`AdvancedAnalysisSummary`、
   `DependencyGraphTestSummary` —— 在全部 XAML 里零命中【核实】。
2. **`ToolTipService.ShowOnDisabled` 全仓库 0 处**，WPF 默认不在禁用控件上显示 ToolTip
   ⇒ 三处把原因写进 ToolTip 的努力（`CrossArmorPlanPageView.xaml:58`、`:105`、
   `VpkRebuildBottomBarContent.xaml:21`）在真正变灰时**用户看不到**【审计】。

> 结论（v3 已改写）：禁用态在本方案里被**整体取消**（§2.2），因此
> ① `ToolTipService.ShowOnDisabled` **不需要加**；② 六个零绑定属性不再是"唯一原因渠道"，
> 它们是**常驻信息**（归卡片），不是"给按钮配的注解"（§2.4）。

### 1.3 已有的合规范例（照抄它们，不另发明）

| 范例 | 形状 | 证据 |
|---|---|---|
| **禁用 + 常驻原因**（全仓库唯一完整样例） | 更新卡「安装并重启」被开发构建拦住，旁边一行 `BlockedNote` | `AppUpdateCard.xaml:31` + `AppUpdateSectionViewModel.cs:103-106`【评审】 |
| **禁用 + 常驻状态行**（本轮唯一的"执行结果"形状） | 生成sectional版底栏：`Status` 同时承担"为什么不能生成"与"跑完的结果" | `CustomModelConversionBottomBarViewModel.cs:51-56` + `CustomModelConversionBottomBarContent.xaml:26-29`【核实】 |
| **不可用就不生成该项**（而非灰色） | 行内动作：模板里既无 `IsEnabled` 也无 `Command` | `Controls/Lists/RowActionListConverter.cs:28-31` + `ModListPanel.xaml:231-255`【审计】 |
| **点击时给原因**（执行期发现不行） | 立即重新部署无活动配置 / 一键重构无目标 | `ShellViewModel.cs:1453-1457`、`:2489-2495`【审计】 |
| **同一属性同时表达可见与可点** | `CanConvertToSectional`、`CanCloneVariant` | `Vpk.cs:129-132`、`CloneVariant.cs:19,22`【核实】 |
| 声明表 + 纯数据布局（分层已有先例） | 图标目录、行级 hidden 过滤、卡片布局 | `PageActionKind.cs:59-96`、`RowActionListConverter.cs:28-31`、`ModDetailsCardLayout.Sync`【评审】 |

### 1.4 违规清单（"不说明原因就禁用"）

**口径修正（v1 错）**：原"明确违规 23 处"基数不可用——至少 2 条误判、1 条应删、数字两处错。
**P4 的"白名单清零"必须按新版清单走，否则永远到不了零。**

**已从违规表移除**：

| 原条目 | 移除理由 |
|---|---|
| 跨甲右键菜单「恢复自动」/「强制隐藏」 | `Header` 本身就是原因（`CrossArmorPlanPageView.xaml.cs:45,50`），Windows 菜单项条件性适用的标准语义 |
| "解除映射目标"两条同文案 | 现有测试 `ArmorRegistrySelectionScopeArchitectureTests.cs:122` 断言两处出现；改文案必须先改测试，当前构建不可验证 ⇒ **保留文案，补作用域 ToolTip** |
| 「编辑 section 组」`ModDetailsPageView.xaml:95-98` | 它是进入分件编辑页的**唯一入口**，且有成文布局理由（同文件 `:90-94`：置灰不改变布局，避免卡片抖动） |

**必须拆分的一条**：护甲注册页「应用」（`ArmorRegistryPageViewModel.cs:134-136`）里
`IsBusy` 部分**合法**（防重入），`applyCooldownActive` 部分**违规**（执行**结束后**的 10 秒，且理由只在注释里）。
**明写：不得因此删除 `IsBusy`。**

#### 1.4a 无理由禁用（按"用户会不会被气到"排）

| # | 位置 | 为什么会踩雷 |
|---|---|---|
| 1 | `PatchResourceRepackagingPageView.xaml:70-75`（判据 `PatchResourceRepackagingPageViewModel.cs:115-119`） | 勾了两个同 AssetKey 的冲突版本（正常误操作）就让主按钮变灰，页面无解释 |
| 2 | `SettingsPageView.xaml:207-208`（判据 `PageViewModels.cs:2070-2072`） | 没配游戏目录时**永久灰**，唯一线索是不显示的 ToolTip |
| 3 | `VpkRebuildBottomBarViewModel.cs:61,130-133` / `VpkBulkRebuildBottomBarViewModel.cs:43,84` | 把并发数框清空（最普通的编辑）即锁死主按钮；`DefaultHint` 是静态范围说明 |
| 4 | 详情页浮动簇中 `CanExecute` 比可见性多 `TryGetCurrentNode`/`_disposed` 的 6 个动作 | 可能"显示着但灰"；簇里唯一文字是动作名 ToolTip（禁用时不显示），视觉只降到 `Opacity 0.4` |
| 5 | `HomePageView.xaml:198-203`（判据 `PageViewModels.cs:73`） | 库为空时灰；上面那行"Mod 库 0"是计数不是原因 |
| 6 | `SameKeyRebuildBottomBarContent.xaml:12`（判据 `SameKeyRebuildBottomBarViewModel.cs:20`） | 底栏没有状态行；清空输出路径即静默变灰 |
| 7 | `CrossArmorPlanPageView.xaml:105`、`:55-58` | 判据是内部事实就绪状态，用户无从感知；原因只写在禁用后不显示的 ToolTip 里 |
| 8 | 护甲注册页「应用」的 `applyCooldownActive` 部分 | 执行结束后无条件灰 10 秒（含失败/取消），"冷却"只写在 5 处注释里 |
| 9 | `CrossArmorCandidateOutputPageViewModel.cs:48` 与 XAML `:36` 双判据 | 「生成」同时绑 `Command` 与 `IsEnabled`，两套判据 |
| 10 | `CustomModelConversionBottomBarViewModel.cs:22-23` 名称为空 | 按钮灰，状态行仍说"可选一份护甲和一份头盔"，没说名称不能为空 |

#### 1.4b 行为 bug（比风格问题严重，必须先修）

| # | 缺陷 | 证据 | 后果 |
|---|---|---|---|
| B‑1 | ~~「再跑一遍」跑不了~~ → **真正的问题只有一条：重跑不重置** | `StartAsync` 的成功/取消/失败**三个分支都设了** `IsFinished = true`（`VpkRebuildBottomBarViewModel.cs:214,219,225`，旧版在 `:192,197,203`），`IsFinished` setter 也早就 Raise 了 `StartText`/`CancelText`（`:167-168`）。**缺陷是它从不在重跑时置回 `false`** ⇒ 第二次运行期间 `CancelText` 仍写「关闭」，点下去实际是取消——文本承诺了另一个分支 | **已修**：`StartAsync` 里 `IsFinished = false;`（`:208`）。<br>**记录两次误判**：v1 说"成功跑完后并发框永久锁死"（错）；v2 说"成功路径没设 `IsFinished`、`StartText` 恒为开始重构建"（也错）。两次都是我静态读代码时的臆断，**正确结论来自子代理逐行核对** |
| B‑2 | 并发非法时「开始」灰且零解释 | `VpkRebuildBottomBarViewModel.cs`（原 `:61,130-133`）；`VpkBulkRebuildBottomBarViewModel.cs`（原 `:43,84`） | **已修**：两个 `StartCommand` 的 `canExecute` 全删；点下去先自校验，原因 + 当前值 + 合法范围一起写进该栏**已有**的 `Status`（范围取 `VpkBulkRebuildService.Min/MaxConcurrency` ＝ 1–12，不写死） |
| B‑3 | `sourceScan == null` 时「重新计算可见部件」永久灰 | `CrossArmorTransferPlanWindowViewModel.cs`（原 `:59`） | **已修**：删 `CanRescanSourceUnits` 与 `canExecute`；`sourceScan` 为空时写 `SourceScanState` 说明"没接上来源解析，请从详情页重开计划" |
| B‑4 | 导出失败/取消时「打开所在文件夹」出现但灰 | `ModExportBottomBarContent.xaml`（原 `:27-28`）+ `ModExportBottomBarViewModel.cs`（原 `:55`） | **已修**：可见性改绑新的 `HasExportPath`（**放置**，不是禁用）；「导出」上那条 `IsEnabled` 也删了，防重复点击改由 `RunExportAsync` 说一句 |
| B‑5 | "点了静默无事"一类 | `SelectionActionBarContent.xaml.cs:19-20`（设置分类）、`BottomBarCoordinator.cs:294`（移动）、`:244`（插入）、`ShellViewModel.cs:1965-1967`（设为选项）、`CloneVariant.cs:36` | **已修**：五处都改成"当场说清原因 + 下一步"。<br>关键发现（推翻我的担心）：`BottomBarCoordinator` **本来就持有 `NotificationService`**（`:14/27/32`，已在用 7 处），所以"给它注入回调"根本不需要，也不必让调用方代劳（`RelayCommand.Execute` 是 `void`，调用方拿不到结果，那样更贵）。<br>可达性：③插入**真能撞上**（零配置 + 库/配置并排）；①设置分类可触发（全局记忆值与本页开关语义不同）；②移动、④设为选项、⑤复制变体按当前放置谓词**疑为死守卫**（静态推理，**未确认**）——加了提示也验证不到，只算契约补齐 |
| B‑6 | 取消后底栏状态停在上一个阶段文案 | `VpkRebuildBottomBarViewModel.cs:216-220` 的 `catch (OperationCanceledException)` 在真实链路里是**死分支**：`ModDetailsPageViewModel.VpkRebuild.cs:151`、`:376` 内部吞掉了 OCE | 用户点了「取消」，状态行却还写着"正在压缩…"之类；**未修，待 P3 与调用方一起处理** |
| B‑7 | 批量重构 0 目标时状态停在"正在重构建 0 个…" | `ShellViewModel.RunBulkVpkRebuildAsync` 在 `targets.Count == 0` 时直接 return，没人调 `ReportFinished` | 同上；**未修**，与 B‑6 同批 |
> 另有一类需在 P0b 确认：`VpkBulkRebuildBottomBarContent.xaml:18-20` 的并发框 `Visibility` 绑 `IsOptions`，
> 而 `IsOptions` 由行类型决定、运行中不变 ⇒ **"编辑时被禁"与"显示时被藏"不冲突**，
> 该条**不是** v1 所说的"防重复按钮被藏"。【主 agent 核实，推翻评审原判】

### 1.5 死代码 / 死字段 / 死判据

| 项 | 证据 | 判定 |
|---|---|---|
| `PageActionKind.PatchRebuild`「重构 patch」恒不显示 | `Actions.cs:55,88`（可见性 `IsDecoration`）+ `ModLibraryService.cs:1390-1392`（`IsDecoration = false`）【核实】 | **删** |
| `IsDecoration` 及 15 处 `!IsDecoration` 守卫 | `ModEntity.cs:19,32`（进 `ModCapabilities.For`）、`ModDetailsPageViewModel.cs:106,109,121,126` | **单独成批**（改签名牵出量大） |
| `PageActionViewModel.IsEnabled` | 恒真死字段【核实】 | 删（D6 的对象改为谓词合流） |
| `CommandParameter` / `ExpandedContent` / `ExpandedWidth` | 29 处构造无一传值 | 删 |
| `ProfileSwitchActionViewModel` / `ProfileRenameActionViewModel` | 从未实例化 | 删（含 2 个 DataTemplate） |
| `Kind` 同值复用 | `Table` 兼作 sectional 编辑器「返回详情」（`SectionalEditorPageViewModel.cs:30`）；`Fix` 与 `PatchRebuild` 同命令；`RebuildAllVpk` 名称与判据不符 | 拆 Kind / 改名 |
| 死判据 `_isLoadingGameDataIndex` | `PageViewModels.cs:2028-2029` 从不赋值 | 删 |
| `SelectionPrimaryText` / `SelectionDeleteText` | XAML 已改硬编码按钮【核实】 | 删 |
| `ShellViewModel.SelectionSummary` | 被 `BottomBarCoordinator.SelectionSummary` 取代【核实】 | 删 |
| ~~`SelectionSummary` 全部删~~ | **错**：`SelectionActionBarContent.xaml:8` 仍绑 `BottomBarCoordinator.SelectionSummary`【核实】 | **保留** |
| 无按钮绑定的死命令 | 材质三连、分析三连、`RemoveCustomModelCommand`、任务四命令 | 保留并标注"暂未接线" |

### 1.6 没有护栏

- 无 Roslyn 分析器 / 无 `.editorconfig` / **无 `HD2ModManager.Tests`**（`tools/UiSnapshot` 刻意不进 sln）【审计】。
- 已有"读源码文本做断言"的先例（`HD2ModCore.Tests/*ArchitectureTests.cs`），但**只断言"按钮存在/已接线"**，
  且 `ExtractMember` 用**硬编码制表符缩进**当分隔符（`ArmorRegistrySelectionScopeArchitectureTests.cs:142-146`）
  ⇒ 仓库缩进不统一（`.Actions.cs` 4 空格、`.Vpk.cs` Tab），**基于抽取成员的断言会随缩进静默变空**。

## 2. 目标契约

### 2.1 四层职责（每层一句话，越界即违规）

| 层 | 只做这件事 | 明确不许 |
|---|---|---|
| **动作执行器** | 拿到参数就干活：**先校验、后动手**；干不了就写常驻状态并/或发通知；**自己守重入** | 不许有 `canExecute`；不许自己弹窗；**不许决定自己何时不可用** |
| **声明表**（每处一份，纯数据） | 一行一个动作：`id / 文案 / 图标 / 槽位 / 放置谓词` | 不许出现具体业务布尔表达式；**不许有原因文案字段**（v3：按钮不承载原因） |
| **放置谓词**（纯函数） | 吃状态快照，答 `Shown` / `Hidden` | 不许碰盘（禁 `Directory.` / `File.` / `Resolve(` / `Exists` / `Enumerate*`）；不许读控件 |
| **渲染** | `Shown` 就画成**可点**、`Hidden` 就不画；按钮上**只有名字** | 不许有任何业务判断；不许在 XAML 绑业务 `IsEnabled`；**不许给按钮配说明文字** |

**硬禁令（B8）**：`Placement` 变化时**只允许改 `Filter`，禁止增删 `PageActions`**。
理由：`MainWindow.xaml:282` 与 `:283` 绑定**同一个** `LeftSlotActions`，增删方案在双簇场景下行为不可预测。

**硬禁令（B9）**：**动作按钮永不禁用**。没有"灰色按钮"这种状态，也没有"给它配一行原因"这种事。
见 §2.2 / §2.4。

### 2.2 状态：只有两态 + 一个执行结果（v3）

```
Placement = Shown | Hidden        // 逻辑层的唯一输出：放不放
Outcome   = Ok | Failed(reason)   // 执行器的唯一输出：跑成没跑成
```

**没有 `Blocked`、没有 `Busy`、没有"禁用"。按钮永远可点、永远只有名字。**

为什么这么收（对自己前两版的反省）：

- v1 二态把 `Blocked` 判成不存在，与用户原话冲突；v2 补回四态，却**引出了渲染灾难**——
  多选底栏一次能站 8 个按钮（`SelectionActionBarContent.xaml:9-39` 共 18 个），
  若每个都挂一行原因，整栏就是"按钮＋小字"的一锅粥。用户的判断是对的：**那比灰按钮更糟**。
- 用户给出的收口更简单也更健壮：**任何按钮都按最坏情况（密集）设计**。
  于是"要不要给原因"这个判断被**整体删除**——不需要判断，就不需要分档；没有分档，就没有会漂的档位判断。
- 规则一在字面上依然成立：**不存在禁用**，所以"不说明原因的禁用"不可能出现（前提被真空满足）。
- 与用户第 3 条原则（"点下去才发现不行是执行器的活"）完全同构：**按钮不判断，执行器判断**。

由此**自动消失**的四个老问题：

| 老问题 | 为什么消失 |
|---|---|
| `ToolTipService.ShowOnDisabled` 全仓库 0 处 | 没有禁用态，用不上 |
| 六个"原因属性"零绑定（§1.2） | 不再是唯一渠道；它们回归**卡片里的常驻信息**（§2.4） |
| "灰掉就再也亮不回来"（`RelayCommand` 不挂 `RequerySuggested`） | 没有灰态，这一整类不可能发生 |
| 可见性与可点性两套判据 | 只剩一套：放置 |

**必须承认的代价**：用户要**点一下**才知道"这里做不了"。因此补两条硬约束：

1. 失败必须**说清下一步**（"先到设置页配好游戏目录"），不能只说"失败"；
2. **"永远不可能成功"是放置 bug，不是禁用理由** ⇒ 必须 `Hidden`，不许留着让人反复撞墙（D11）。

### 2.3 动作执行器契约（v3：结果不经 `ICommand`）

v2 的反对意见是"`ICommand.Execute` 是 `void`，结果传不回来"——**成立**，但它反对的是"把结果塞进 `ICommand`"，
不是"要结果"。v3 把两者拆开：

```
IAction                // 动作层，不实现 ICommand，不持有 canExecute
  string Id
  Task<ActionResult> RunAsync(ActionArgs args, CancellationToken ct)   // 先校验，后动手

ActionRunner           // 外壳侧适配器，唯一持有"谁在跑"
  Task<ActionResult> RunAsync(IAction action, ActionArgs args)         // 同 id 重入 → 立刻 Failed("正在进行中…")
  event ActionFinished                                                 // 状态属性 / 消息中心由它写
```

- 按钮绑定 `RelayCommand(async () => await runner.RunAsync(action, args))` —— **无 `canExecute`**；
- **重入归执行器**：不用禁用、不用改 `CanExecute`。重复点击得到一条"正在进行中"的状态，**不会起第二个任务**；
- 同一时刻**不同 id 可以并行**（`ModDetailsPageViewModel.cs:96-98` 的高级分析与依赖图自检是真并发）；
- **先校验后动手**是硬约束：校验失败时**不得有任何写入**（要有用例覆盖，否则"可点"会变成"点了破坏数据"）；
- 失败文本进消息中心；该栏若已有状态行（`CustomModelConversionBottomBarContent.xaml:26-29`、
  `VpkRebuildBottomBarContent.xaml:48-50`）就同时写进去——**不需要新增任何文字控件**。

即：**"按钮该不该在这里"归放置谓词，"这次能不能做成"归执行器**——两者不再混。

### 2.4 信息与按钮解耦（UI 完整性硬约束）

这一节就是为了防"一堆按钮旁边挂一堆灰色小字"那种灾难。

- **按钮不承载原因**：不许在任何动作按钮旁逐条挂说明文字；按钮上**只有名字**。
- 原因只有两个去处：
  1. **执行后**：消息中心（失败/完成），以及**该栏已经存在的状态行**
     （`CustomModelConversionBottomBarContent.xaml:26-29`、`VpkRebuildBottomBarContent.xaml:48-50`）——
     **复用已有控件，不新增文字元素**；
  2. **常驻信息**：卡片里本来就有内容位置的地方（`VpkComponentStatus`、`AppUpdateCard` 的 `BlockedNote`）。
     它是**卡片内容**，不是"给按钮配的注解"，因此不受上面那条禁令约束。
- **硬约束**：禁止出现"按钮 × N + 逐按钮说明 × N"的结构。
  密集容器——多选底栏（`SelectionActionBarContent.xaml`，18 个按钮）、
  行内动作（`ModListPanel.xaml:231-255`）、浮动簇折叠态——**一律不得新增文字元素**。
- 浮动簇展开时的说明胶囊（`FloatingActionCluster.xaml:69-93`）**不用于放原因**：
  其容器是 `MinWidth/MaxWidth` 自适应 + `TextTrimming`，加内容必变尺寸（P0a 基线已记录当前尺寸）。
- `ToolTipService.ShowOnDisabled` **不需要加**——没有禁用态。
- **UI 完整性是独立验收项**：每批改完必须看一次实际截图（`tools/UiSnapshot` 可手工跑），
  不能只看基线文本；验收问题只有一句："**有没有多出文字**"。

### 2.5 护栏

1. **编译期（最强）**：动作层**不实现 `ICommand`**（`IAction` 只暴露 `RunAsync`）⇒
   "给动作挂 `canExecute`"在类型上**写不出来**。
2. **语义单测（强）**：放置谓词为纯函数（快照 record 进、`bool` 出），跑在 **`HD2ModCore.Tests`**（`net8.0`，不需要 WPF）。
   不变量四条：
   - `Hidden ⇒ 渲染层不存在`；
   - `Shown ⇒ 绑定命令无 `canExecute`（必然可点）`；
   - **先校验后动手**：校验失败时**零写入**；
   - `ActionRunner` 同 id 重入只跑一次，第二次拿到"正在进行中"。
3. **反射 / 文本断言（弱，且禁止依赖缩进）**：
   - 放置谓词禁 IO：`Directory.` / `File.` / `Resolve(` / `Exists` / `Enumerate*`；
   - `PageActionViewModel` 构造点不得传带 `canExecute` 的命令（用**反射**写单测，不读 XAML 文本）；
   - XAML 动作按钮**不得绑 `IsEnabled`**（`FloatingActionCluster.xaml:98` 那个恒真绑定一并删）；
   - 现有 `ExtractMember` 式断言**不得新增**（缩进一改会静默返回空串 ⇒ 真空通过）。
4. **硬禁令**：B8（只改 `Filter`，不增删集合）、B9（动作按钮永不禁用）；
   另加 **D12**：密集容器内新增任何文字元素都要单独评审（§2.4）。

**UI 层不可测**是客观约束（主工程 `net10.0-windows` + `UseWPF`，`HD2ModCore.Tests` 是 `net8.0`）：
所以"界面行为一致"靠 **P0a 的状态基线**证明，"界面没变丑"靠**截图**证明。

### 2.6 状态快照（v1 缺失，v2 补）

由**管理器侧**组装（不进 Core 领域模型），至少包含：

```
ModNodeId            条目身份
ModStorageKind       Legacy | Logical | Sectional | Vpk2Patch | Unknown   （只读投影，见 D3）
HasPatchContent      根上是否有 patch
IsLogicalContainer   是否逻辑容器
HasChildren          直接/递归子级是否存在（两个布尔分开，别混）
DescendantCount      含子级重构建用
HasSourceVpk         根上是否有来源 .vpk
HasSectionalPayload  （现名 IsVpkMod）是否有 sectional 载荷
HasSectionalSource   是否有 sectional-source.json
IsDecoration         恒 false（待删）
Scope                当前选择作用域（库 / 配置 / 装饰宿主 / 护甲注册三作用域 / 无选择）
SelectedIds          当前选中项 id 集合（多选动作需要）
```

**不放 `ActionBusy`**：v3 里"谁在跑"归 `ActionRunner`，放置谓词**不该**知道它——
否则又会出现"执行中 ⇒ 按钮变灰"的回流。

**谓词只读快照字段，永不自己算**。

- **跳板面板里的文字是面板自己的内容**，不受"按钮旁不许挂小字"约束——
  否则这条规则会和 §2.7 的跳板方案自相矛盾。判据是**位置**：字在按钮旁 = 违规；字在面板里 = 正常内容。

### 2.7 动作形态：执行 / 跳板 / 确认（v4，用户 2026-11-10 定）

**"危不危险"也不是按钮该管的事。** 但它不是运行期谓词，而是**动作的形态属性**——
设计期写死进声明表，不随状态重算。所以它不会重新引回会漂的判据。

| 形态 | 判据 | 现状例子 |
|---|---|---|
| **直接执行** | 幂等或可回退、影响面小、无需参数 | 打开文件夹、修改名称/备注/图像、复制为变体、详情表格 |
| **跳板 → 底栏面板** | 不可回退、写 `Overwrite/` 或运行时目录、影响面随选择变化、**需要参数才能安全执行** | 一键重构所有 VPK、重构选中、用来源重构建、生成sectional版、同名重构建、导出、材质打包、头盔合并计划、跨甲计划 |
| **执行器内确认** | 危险但**没有参数可调**，纯"你确定吗" | 检测并修复过时 Mod（`PageViewModels.cs:328`）、删除 Mod（`ModDeletionConfirmation.cs:23`）、导出时覆盖已有文件（`ModExportBottomBarViewModel.cs:149`） |

**硬规则（B10）：跳板必须零副作用。** 点击只做两件事：**读事实** + **开面板**。
不许写任何文件、不许改任何状态。这是"按钮永远可点"能成立的前提——最危险的那一步被挪到面板里的二次确认之后，
点错按钮最多看到一层面板。

**确认只有两个合法位置**：①**跳板里**（面板即二次确认，还带参数、取消、进度）——首选；
②**执行器内的确认回调**（注入，不直接调 `MessageBox`），用于没有参数可调的纯确认。
**禁止第三种：按钮自己弹窗。**

**既有先例（仓库已有人写过同一原则，只是没成通则）**：

```csharp
// ShellViewModel.cs:2503-2505，「重构」命令
// <b>按钮不判断类型，这里才判断。</b>判定要扫盘（存储形态、后代关系），
// 放进绑定属性会让每次选择变化都读一遍目录；而且"选中里没有可重构的"是正常路径，
// 不是错误 —— 给一句提示就结束。
```

**与 D3 的依赖**：跳板要收集事实，而 `ShellViewModel.cs:2488`、`:2510` 现在是**同步扫盘**
（`VpkBulkRebuildService.EnumerateTargets`）。小库无所谓，大库会卡一下。
**D3 的只读投影（P5）正好把这一步变便宜** ⇒ 这是跳板形态能大规模推广的前提，D3 不能删。

## 3. 决策（含专家裁定）

| # | 决策 | 结论 | 依据 |
|---|---|---|---|
| **D1** | 原因放哪 | **按钮不承载原因**。执行后写消息中心 + 该栏已有状态行（不新增控件）；常驻信息归卡片（`VpkComponentStatus` / `BlockedNote`）。`ToolTipService.ShowOnDisabled` 不需要加 | §2.4 |
| **D2** | 结构性不可用一律 Hidden？ | **不存在"不可用"这个状态**：`Shown` 就可点。但"永远不可能成功"是**放置 bug**，必须 `Hidden`（D11） | §2.2 |
| **D3** | 把 `ModStorageKind` 提到库模型？ | **降格为管理器侧只读投影**：`SynchronizeAsync` 时一次算好 `IReadOnlyDictionary<ModNodeId, ModStorageKind>`，指纹变化时失效。**kind 不进 `ComputeFingerprint`**（`ModLibrarySynchronizer.cs:219-232`），**不写 `ModNode`**（否则存量库 kind 永久停在 `Unknown`） | 【评审】 |
| **D4** | 死代码是否现在删 | 删；`IsDecoration` **单独一批**；`Kind` 拆分与改名同批 | §1.5 |
| **D5** | 底栏是否本轮一起改 | 分两批（P4 独立），与 v1 一致 | — |
| **D6** | 改 `PageActionViewModel` 契约 | 允许，但**对象是"放置谓词与 `CanExecute` 合流"**，不是那个恒真死字段 | §1.1 |
| **D7（新）** | 双簇禁令 | 禁止增删 `PageActions`，只改 `Filter` | B8 |
| **D8（新）** | 护栏实现方式 | 编译期 + 语义单测（`HD2ModCore.Tests`）+ 反射断言；**禁止依赖缩进的文本断言** | §2.5 |
| **D9（用户定）** | 按钮是否允许禁用 | **不允许**。按钮永远可点、只有名字；不能做由执行器说，说清下一步 | §2.2 |
| **D11（新）** | "永远不可能成功"怎么办 | 视为**放置 bug**，必须 `Hidden`；不许留一个必然报错的按钮 | §2.2 |
| **D12（新）** | 密集容器加文字 | 单独评审；默认禁止（多选底栏 / 行内动作 / 浮动簇折叠态） | §2.4 |
| **D10（待你定）** | **输入控件**（并发数 `TextBox`、名称框）是否也"永不禁用" | **建议保留可停用**：它们不是按钮，且同排已有状态文字在解释（如进度行的"正在重构建…"）。若你要求同样不可停用，则改成"可编辑、运行中忽略并提示" | §6-7 |
| **D13（用户定）** | 危险动作怎么办 | 做成**跳板**（底栏面板：参数 + 二次确认 + 取消 + 进度）或**执行器内确认**；**不许按钮自己弹窗**；跳板**零副作用** | §2.7 B10 |

## 4. 落地顺序（v3）

**与 v1/v2 的关键差异**：①**不再"建原因出口"**——v3 取消了禁用态，`P0b` 改成"把一个灰按钮改成可点并给理由"的试点；
②把 D3 从 P1 剔出；③把**行为 bug**（§1.4b）前移到 P0b，优先级高于一切风格整改；
④先证"可点 + 说清"在一个真实按钮上不产生噪音与新控件，再推广到 29 处注册。

| 阶段 | 内容 | 独立性 | 验收 |
|---|---|---|---|
| **P0a** | **基线锁定**：导出每页 × 每种库形态的 `{Kind, Label, IsVisible, CanExecute 实测, 尺寸}` JSON；护栏骨架（全白名单） | ✅ 只新增文件与测试，29 处注册零改动 | 基线 JSON 存在；`dotnet build` 0 警告 0 错误；现有测试全绿 |
| **P0b** | **把一个灰按钮改成"可点 + 点后给理由"**（试点：`PageViewModels.cs:2070-2072` 的「Steam 校验完整性」，**不新建任何类型**）+ §1.4b 行为 bug（B‑1～B‑5） | ✅ 改渲染 + 局部 VM，不动契约 | 试点处**不再有灰按钮**；点下去消息/状态行说清"先到设置页配好游戏目录"；**截图确认没有多出文字元素**；B‑1 成功跑完后 `StartText` 变成"再跑一遍"（或删掉该分支）；B‑3 不在 `null` 时死灰；B‑5 每条都有可见反馈 |
| **P1** | 契约：`IAction` + `ActionRunner`（无 `canExecute`、重入归执行器）+ 两态放置表 + **双写兼容层**（旧 `IsVisible` 保留） | ✅ 29 处零改动 | 新增类型有单测；`Hidden ⇒ 不存在`、`Shown ⇒ 可点`、重入只跑一次、校验失败零写入；旧行为不变（基线 diff 为空） |
| **P2** | 详情页 `Actions.cs` switch → 表；**删除**那 10 条 `canExecute`（不是"改成派生"，v3 里动作层不再有判据）；**剔除 D3** | ✅ 谓词未改，只合并来源 | 两态表用例（legacy / vpk2patch / sectional × 逻辑容器 / 空容器）：每格只答"放/不放"；**删除判据后不得出现"点了报错但以前能点"的回归**（基线 diff + 逐动作点检） |
| **P3** | 逐页迁移注册点，**护甲注册页最后** | ✅ 每页独立 | 每页迁移前后基线 diff 为空 |
| **P4** | 底栏（18 按钮 / 17 个 `Show*`）：合并 4 组重复表达式；删除所有 `CanExecute`；§1.4a 逐条归类为"`Hidden` / 可点 + 执行器解释" | ✅ 另一套机制 | 选中态矩阵用例；密集栏**零新增文字元素**（截图验收）；两条同文案「解除映射目标」**保持原样**（测试断言两处出现），不靠文案区分 |
| **P5** | D3 只读投影 | ✅ 独立 | 不新增 resolver 断言；`ModNodeSemanticComparer.cs` 零改动 |
| **P6** | §1.4a 剩余逐条修 + **危险动作三形态改造**（§2.7）+ 白名单清零。头号目标：护甲注册页「应用」现在是"直接写运行时目录 + 10 秒冷却"，应改成跳板或执行器确认 | ✅ 每条可回退 | 每个危险动作标注"属哪一形态"；白名单清单为空（按新版基数） |
| **P7** | 删旧字段与死代码（`IsDecoration` 单独一批） | ✅ 最后 | 0 警告；全量测试绿 |

**P3 为何护甲注册页最后**：`ArmorRegistryPageViewModel.cs:134-136` 走 `AsyncRelayCommand`，
而 `AsyncRelayCommand.cs:46` 缺 Dispatcher 保护 ⇒ 它会最先炸。

## 5. 本轮明确不做

- 不重写底栏布局引擎（`BottomBarLayout.cs` / `BottomBarSurface`）；
- 不改 `tools/UiSnapshot`；
- 不碰 `docs/参考项目/`、`_backup/`、第三方快照；
- 不改任何 Core 转换/编译逻辑**（但 P5 的投影会读 `ModStorageKindResolver`，不改它）；
- **不改"解除映射目标"的文案**（会撞 `ArmorRegistrySelectionScopeArchitectureTests.cs:122`）；
- 不动 `IsDecoration`（P7 单独一批）。

## 6. 未确认项（逐条落定）

| # | 结论 | 依据 |
|---|---|---|
| 1 | `ListCollectionView.Refresh()` **不重算 `CanExecute`**。`CloneVariant` 没坏是因为它与可见性是**同一属性**（属性变→`_view.Refresh()`→容器重建）。**注意**：`ModDetailsPageViewModel.cs:1600-1604` 每次刷新都会 Raise 那 5 个命令，但**`CloneVariantCommand` 一次都没 Raise**【主 agent 核实，推翻评审原判】 | 【核实】 |
| 2 | `ShowPatchTools` 与 6 个 `CanExecute` 的差（`TryGetCurrentNode`/`_disposed`）在真实库里**不可达**（显示着但灰） | 【评审】 |
| 3 | `CategoryFilterPreference.IsEnabled`（全局记忆）与 `ModCategoryFilter.IsEnabled`（每页）语义不同 ⇒ "设置分类"可点但静默无反应**可触发，是真 bug** | 【评审】 |
| 4 | `MaterialPackagingPageView` **确认不可达**（`ShellViewModel.cs:2396` throw + `:650` `CanReopenPage=false` + 无入页路径） | 【核实】 |
| 5 | `MainWindow.xaml:282/283` 双簇是否会在过渡帧同时可见 —— **唯一必须运行验证的一条**，在 P0a 一并跑掉 | 【未确认】 |
| 6 | 库数据变了但选择没变时 `Show*` 是否陈旧 —— 风险在**通知面**不在事实面（`BottomBarCoordinator.cs:86-117` 实时求值、不缓存） | 【评审】 |
| 7 | **输入控件（并发数 `TextBox`、名称框）是否也"永不禁用"——待用户定（D10）**。建议保留可停用：它们不是按钮，且同排已有状态文字在解释 | 待定 |

## 7. 与既有文档的关系

- 本文是**行为契约**层面的规划，不替代 `sectional-rebuild-and-bulk-selection-plan-20261109.md`（那是功能落地）；
- 该文档 §13 记录的两个同类事故（"入口没通"与"借用别人的判据"）是本文的直接动因；
- 若 D6 生效，`PageActionViewModel` 的契约变更需回写该文档 §4/W2c，避免两份口径。
