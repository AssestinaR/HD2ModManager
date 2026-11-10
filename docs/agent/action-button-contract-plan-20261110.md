# 按钮契约重构规划：汉堡菜单 + 底栏（2026-11-10）

> **状态**：待审（v2，已按四方专家评审重做处方部分）。
>
> **版本说明**：v1 由主 agent 起草；随后用外部模型（`openwrt-cn/cn:deepseek-v4.1-flash`）
> 分四路独立审查（架构合理性 / 破坏面与迁移顺序 / 契约完备性 / 事实核验）+ 一次主裁合并，
> 结论为"诊断可信、处方需重做"。**v2 保留全部诊断，重写处方**，并逐条记录被推翻的判断。
>
> **本文要解决的一句话**：全仓库"这个按钮此刻能不能用"由**四份互不知情的实现**分别回答，
> 而"不能用的原因"没有任何统一的呈现约定；于是用户看到的是灰按钮——不知道为什么不给点。

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
| 底栏并发框冻结排在 P3 | **提前到 P0b**（但原因不同：不是"冻结用户的值"，而是**文案承诺了一个走不到的分支**） | `VpkRebuildBottomBarViewModel.cs:156-203`【核实】 |

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

> 结论：`ToolTipService.ShowOnDisabled` 只能作**过渡**，不能作**实现**；
> `Blocked/Busy` 的 `reason` 必须渲染成**常驻文字**。

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
| B‑1 | **「再跑一遍」实际上跑不了** | `IsFinished` 只在取消/失败时置位（`VpkRebuildBottomBarViewModel.cs:192,197,203`）⇒ **成功一次之后它永远是 false**，于是 `StartText` 恒为"开始重构建"，文字承诺的"再跑一遍"根本不存在；连带 `CanEditConcurrency = !IsRunning && !IsFinished`（`:140`）的 `IsFinished` 分支**恒走不到** | 症状是**文案撒谎 + 死代码**，不是"输入框被锁"（v1 判断错）。修法：`finally` 里无条件 `IsFinished = true`，或删掉这个分支与那句文案 |
| B‑2 | 并发非法时「开始」灰且零解释 | `VpkRebuildBottomBarViewModel.cs:61,130-133`；`VpkBulkRebuildBottomBarViewModel.cs:43,84` | 见 §1.4a #3 |
| B‑3 | `sourceScan == null` 时「重新计算可见部件」永久灰 | `CrossArmorTransferPlanWindowViewModel.cs:59`；点击路径 `:381` 直接 return | 死路且无提示 |
| B‑4 | 导出失败/取消时「打开所在文件夹」出现但灰 | `ModExportBottomBarContent.xaml:27-28`（可见性= `HasStatus`）+ `ModExportBottomBarViewModel.cs:55`（判据= `_lastExportPath is not null`） | 出现即灰，且状态行说的是失败原因，不是它为什么不能点 |
| B‑5 | "点了静默无事"一类 | `SelectionActionBarContent.xaml.cs:19-20`（设置分类）、`BottomBarCoordinator.cs:294`（移动）、`:244`（插入）、`ShellViewModel.cs:1965-1967`（设为选项）、`CloneVariant.cs:36` | 与"灰着不知道为啥"同类，用户原话"点了什么都没发生" |

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
| **动作执行器** | 拿到参数就干活；干不了就**写常驻状态属性**并/或发通知 | 不许有**业务判据**的 `CanExecute`；不许自己弹窗；**不许决定自己何时不可用** |
| **声明表**（每处一份，纯数据） | 一行一个动作：`id / 文案 / 图标 / 槽位 / 放置谓词 / BlockedReason / BusyReason` | 不许出现具体业务布尔表达式 |
| **放置谓词**（纯函数） | 吃状态快照，答 `Placement` | 不许碰盘（禁 `Directory.` / `File.` / `Resolve(` / `Exists` / `Enumerate*`）；不许读控件 |
| **渲染** | 按状态渲染；`Hidden` 不渲染；`Blocked`/`Busy` 渲染成禁用 **并渲染 reason** | 不许有任何业务判断；不许在 XAML 直接绑业务 `IsEnabled` |

**硬禁令（B8）**：`Placement` / `Blocked` / `Busy` 变化时**只允许改 `Filter`，禁止增删 `PageActions`**。
理由：`MainWindow.xaml:282` 与 `:283` 绑定**同一个** `LeftSlotActions`，增删方案在双簇场景下行为不可预测。

### 2.2 状态：两维四态

```
Placement = Shown | Hidden          // 逻辑层对"放不放"的唯一输出
Shown 时再看：
  Blocked(reason)?                  // 结构性事实：点击前就确知、一句话说清、不会自动消失
  Busy(reason)?                     // 仅"动作正在执行"，会自动消失
  否则 → 可点
```

| 状态 | 渲染 | 判据 | 原因必须 |
|---|---|---|---|
| `Shown` 且非 Blocked/Busy | 可点 | 正常 | — |
| `Blocked(reason)` | 禁用 + **常驻 reason** | 结构性事实（例：没配游戏目录 / 没有子级 / 没有 section 树） | 是，禁止只放 ToolTip |
| `Busy(reason)` | 禁用 + 常驻 reason | 动作正在执行 | 是 |
| `Hidden` | 不渲染 | 此语境下无意义，**且不是唯一入口** | 否（看不到的东西不需要理由） |

**`Blocked` / `Busy` 的唯一区别是"是否会自动消失"。**
**§D2 判据（取代 v1 的"一律 Hidden"）**：有出路可指 ⇒ `Blocked`；无出路**且不是唯一入口** ⇒ `Hidden`；
**是唯一入口 ⇒ 必须 `Blocked`**（否则等于永久丢失功能）。

**`Busy` 按动作 id 多值**（不允许多值单例）：`ModDetailsPageViewModel.cs:96-98` 的高级分析与依赖图自检是**真并发**，
单值 `Busy` 会强制串行，是功能倒退。
`Begin(actionId, reason)` 返回 `IDisposable`，接 `CancellationToken`，页面 `Dispose()` 时必须清空，
**且所有变更必须在 UI 线程发布**（`AsyncRelayCommand.cs:46` 的 `RaiseCanExecuteChanged` 缺 Dispatcher 保护，
与 `RelayCommand.cs:22-29` 不一致，必须修）。

### 2.3 动作执行器契约

**不用 `ActionResult` 作返回值**（`ICommand.Execute` 是 `void`，`AsyncRelayCommand` 是 `async void`）。
改为：

- 执行器**必须**有一个与其一一对应的**常驻状态属性**（形状见 `CustomModelConversionBottomBarViewModel.cs:51-56`），
  写入"这次做了什么/为什么没做成"；
- 失败文本同时进消息中心（`NotificationService`）；
- 按钮旁的原因**走声明表**（`BlockedReason` / `BusyReason` 两段字符串），**不由执行器提供**。

即：**"为什么不能点"是声明表的责任，"这次跑成没成"是执行器的责任**——两者不混。

### 2.4 原因渲染：固化形状①，补六个零绑定属性

- **形状①（紧贴按钮的常驻行）** 定为唯一标准出口：`Blocked` / `Busy` 的 reason 渲染在按钮**所在处**；
- 六个零绑定属性（§1.2）**在 P0b 逐个补宿主**，或改成声明表的 `BlockedReason`；
- `ToolTipService.ShowOnDisabled` 作为**过渡**（一次样式级改动），不作为实现手段；
- 浮动簇的 reason 渲染在**展开时的说明胶囊**（`FloatingActionCluster.xaml:69-93` 的 `ExpandedRail` 已存在，
  但内容容器是尺寸自适应的 `MinWidth/MaxWidth` + `TextTrimming=CharacterEllipsis` ⇒ **加内容会变尺寸**，
  必须在 P0a 基线里记录当前尺寸并约束"不得改变尺寸"）。

### 2.5 护栏：三层 + 一条硬禁令

1. **编译期（最强）**：动作层**不实现 `ICommand`**；`Blocked` / `Busy` 的构造器私有，只能
   `Blocked(reason)` / `Busy(reason)`，`reason` 为空则 **throw**。（`Busy` 最终仍经 `CanExecute` 生效。）
2. **语义单测（强）**：状态快照为纯 record，谓词为纯函数，跑在 **`HD2ModCore.Tests`**（`net8.0`，不需要 WPF）。
   三条不变量：`Busy != None ⇒ 不可点`；`Hidden ⇒ 渲染层不存在`；`无 Blocked/Busy ⇒ 可点`。
3. **文本/反射断言（弱，禁用缩进依赖）**：
   - 谓词禁 IO：`Directory.` / `File.` / `Resolve(` / `Exists` / `Enumerate*`；
   - `PageActionViewModel` 构造点不得传带业务 `canExecute` 的 `RelayCommand`（用**反射**写单测，不读 XAML 文本）；
   - 现有 `ExtractMember` 式断言**不得新增**。
4. **硬禁令**：见 §2.1 B8。

**UI 层不可测**是客观约束（主工程 `net10.0-windows` + `UseWPF`，`HD2ModCore.Tests` 是 `net8.0`）：
所以"界面行为一致"只能靠 **P0a 的状态基线 JSON** 证明，不能靠编译。

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
ActionBusy           `IReadOnlySet<string>` 正在执行的动作 id
```

**谓词只读快照字段，永不自己算**。

## 3. 决策（含专家裁定）

| # | 决策 | 结论 | 依据 |
|---|---|---|---|
| **D1** | 原因出口放哪 | 固化形状①（紧贴按钮常驻行）；执行失败进消息中心；`ToolTipService.ShowOnDisabled` 只作过渡 | §1.2 |
| **D2** | 结构性不可用一律 Hidden？ | **否**。四态；有出路 ⇒ `Blocked`；唯一入口 ⇒ 必须 `Blocked` | §2.2 |
| **D3** | 把 `ModStorageKind` 提到库模型？ | **降格为管理器侧只读投影**：`SynchronizeAsync` 时一次算好 `IReadOnlyDictionary<ModNodeId, ModStorageKind>`，指纹变化时失效。**kind 不进 `ComputeFingerprint`**（`ModLibrarySynchronizer.cs:219-232`），**不写 `ModNode`**（否则存量库 kind 永久停在 `Unknown`） | 【评审】 |
| **D4** | 死代码是否现在删 | 删；`IsDecoration` **单独一批**；`Kind` 拆分与改名同批 | §1.5 |
| **D5** | 底栏是否本轮一起改 | 分两批（P4 独立），与 v1 一致 | — |
| **D6** | 改 `PageActionViewModel` 契约 | 允许，但**对象是"放置谓词与 `CanExecute` 合流"**，不是那个恒真死字段 | §1.1 |
| **D7（新）** | 双簇禁令 | 禁止增删 `PageActions`，只改 `Filter` | B8 |
| **D8（新）** | 护栏实现方式 | 编译期 + 语义单测（`HD2ModCore.Tests`）+ 反射断言；**禁止依赖缩进的文本断言** | §2.5 |

## 4. 落地顺序（v2 重排）

**与 v1 的关键差异**：①把"建出口"从 P0 的内容改成 P0 的**验证对象**（先在存量上验证出口有效）；
②把 D3 从 P1 剔出；③把**行为 bug**（§1.4b）前移到 P0.5，优先级高于一切风格整改；
④重排理由：`Vpk.cs:379-392` 的注释记录过"`CanExecute` 判据在异步加载完成后才有值 ⇒ 永远卡在第一次的 false"
—— 所以**必须先在存量上证明出口有效，再建模型**，否则新模型会原封不动重建这批永久灰。

| 阶段 | 内容 | 独立性 | 验收 |
|---|---|---|---|
| **P0a** | **基线锁定**：导出每页 × 每种库形态的 `{Kind, Label, IsVisible, CanExecute 实测, 尺寸}` JSON；护栏骨架（全白名单） | ✅ 只新增文件与测试，29 处注册零改动 | 基线 JSON 存在；`dotnet build` 0 警告 0 错误；现有测试全绿 |
| **P0b** | **原因出口（形状①）验证 + §1.4b 行为 bug**：先拿「Steam 校验完整性」（`PageViewModels.cs:2070-2072`）做试点——**不新建任何类型**，让"没配游戏目录"显示出来；再修 B‑1～B‑5 | ✅ 改渲染 + 局部 VM，不动契约 | 试点处灰按钮旁出现原因文字；B‑1 成功跑完后 `StartText` 变成"再跑一遍"（或删掉该文案分支）；B‑3 不在 `null` 时死灰；B‑5 每条都有可见反馈 |
| **P1** | 契约类型：`Placement` 四态 + `Blocked`/`Busy` 多值 + **双写兼容层**（旧 `IsVisible`/`IsEnabled` 保留） | ✅ 29 处零改动 | 新增类型有单测；旧行为不变（基线 diff 为空） |
| **P2** | 详情页 `Actions.cs` switch → 表；10 条 `canExecute` 改为从同一谓词派生；**剔除 D3** | ✅ 谓词未改，只合并来源 | 三态表用例（legacy / vpk2patch / sectional × 逻辑容器 / 空容器）；**迁移后 `Evaluate()` 必须在每次判定后显式 Raise** |
| **P3** | 逐页迁移注册点，**护甲注册页最后** | ✅ 每页独立 | 每页迁移前后基线 diff 为空 |
| **P4** | 底栏（18 按钮 / 17 个 `Show*`）：合并 4 组重复表达式；两条同文案补作用域 ToolTip；§1.4a 逐条归类 | ✅ 另一套机制 | 选中态矩阵用例；§1.4a 每行标注"改成了什么" |
| **P5** | D3 只读投影 | ✅ 独立 | 不新增 resolver 断言；`ModNodeSemanticComparer.cs` 零改动 |
| **P6** | §1.4a 剩余逐条修 + 白名单清零 | ✅ 每条可回退 | 白名单清单为空（按新版基数） |
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

## 7. 与既有文档的关系

- 本文是**行为契约**层面的规划，不替代 `sectional-rebuild-and-bulk-selection-plan-20261109.md`（那是功能落地）；
- 该文档 §13 记录的两个同类事故（"入口没通"与"借用别人的判据"）是本文的直接动因；
- 若 D6 生效，`PageActionViewModel` 的契约变更需回写该文档 §4/W2c，避免两份口径。
