# 标题卡：搜索 / 分类合并成「标签式搜索框」——规划（2026-11-18）

状态：**规划，未动代码**。已过一轮 Kimi 专家审阅（见第 10 节），用户已拍板 D1–D6（见第 5 节）。
遗留待定：D7（护甲登记页那枚 3 态按钮要不要一起改）、D8（「选项」标签的语义方向）。

涉及页面：**配置页**（`ProfilePageView`）与**模组库页**（`LibraryPageView`）的两张标题卡。

---

## 1. 现状取证

### 1.1 标题卡是"两段固定 + 若干可注册状态"

`ModListPanel` 的标题卡（`ModListPanel.xaml:26-94`）是两行 Grid：

| 位置 | 行/列 | 元素 |
|---|---|---|
| 标题行（高 32） | row0 / col0 | `HeaderTitleElement`、`HeaderSearchBox`（平时 `Opacity=0`） |
| 标题行 | row0 / col0（`RowSpan=2`） | `HeaderModeLayer`：宿主注册的"额外状态" |
| 标题行 | row0 / col1 | `HeaderModeToggles`（代码生成）+ 搜索切换键（放大镜） |
| 摘要行（高 22） | row1 跨两列 | `HeaderSummaryElement`（标题态）/ `HeaderSearchActions`（搜索态） |

面板只内置两种模式：**标题**、**搜索**；第三态由宿主注册 `ModListHeaderMode`（`Views/ModListHeaderMode.cs`），面板负责互斥、淡入淡出、生成切换键（`ModListPanel.xaml.cs:263-400`）。

三个使用者：

1. `LibraryPageView.xaml:40-50` —— `Key="Categories"`，内容 `CategoryChipBar`（**用户说的"新加的分类模式"**）
2. `ProfilePageView.xaml:34-44` —— 同上
3. `ArmorRegistryPageView.xaml:131-212` —— `Key="ArmorTabs"`（套装 / 护甲 / 头盔 / 已映射），**与本轮无关，必须保住**

### 1.2 撞车的具体表现

- 分类模式占 **row0**（标题/搜索框那格），打开时标题与摘要一起淡出，卡片剩一整块芯片墙。
- 搜索模式占 **row0 + row1**：搜索框在 row0，`SearchActionsTemplate` 的小按钮在 row1。
- 两者互斥（`ActiveHeaderMode`，`ModListPanel.xaml.cs:365-366`）。
- 同一件事（筛列表）被拆成两个入口、两个位置、两套控件（芯片 vs 纯文字按钮）。

### 1.3 搜索态那一排按钮，现状全清单

| 页面 | 按钮 | 交互模型 | 文案 | 数据 |
|---|---|---|---|---|
| 模组库 | 过时 | 2 态 toggle | 显示过时 ↔ 显示全部 | `ShowOnlyOutdated`（`LibraryPageViewModel.cs:117-127`） |
| 模组库 | 选项 | 2 态 toggle | 显示选项 ↔ 隐藏选项 | `ShowOptions`（`:129-139`） |
| 配置页 | 过时 | 2 态 toggle | 显示过时 ↔ 显示全部 | `PageViewModels.cs:695-707` |
| 护甲登记（可用护甲列表） | Unit 覆盖 | **3 态 cycle** | 全部 → 已命中 → 未命中 | `AvailableArmorUnitFilter`、`CycleAvailableArmorUnitFilter`（`ArmorRegistryPageViewModel.cs:23-28,206-211,265-273`） |

全部经由 `SearchActionsTemplate`（`LibraryPageView.xaml:6-12`、`ProfilePageView.xaml:6-11`、`ArmorRegistryPageView.xaml:10-15`）渲染在 row1。

### 1.4 分类筛选的数据流

- `ModCategoryClassifier`（`HD2ModCore/Infrastructure/`）：按资产摘要分类，服装优先；`Ordered` = 护甲 / 披风 / 武器 / 音效 / 材质贴图 / 界面 / 动画 / 其他。
- `ModCategoryFilter`（`ViewModels/ModCategoryFilter.cs`）：每页一份；自建分类与手动归类（`CategoryStore`，落 settings.json）、`Chips`、**单选** `_selected`（`:172`）、`Matches`（`:270-274`）、`Rebuild`（`:291-339`）、总开关 `IsEnabled`（`:189-211`）。
- 刷新顺序（`LibraryPageViewModel.cs:308-319`）：文本 → 过时 → `Category.Rebuild` → `Category.Matches` → 排序提交。
- 开关持久化（`SettingsService.cs:38,272-276` → `CategoryFilterPreference`）；`BottomBarCoordinator.ShowSetCategory`（`:88`）与行内动作可用性（`ModListPanel.xaml.cs:690-692`）都读它。

### 1.5 三个「设置分类」入口（都要留）

| 入口 | 触发 | 打开 |
|---|---|---|
| 行内动作 | 行悬停药丸里的 `ModListRowAction.Category` | `CategoryPickerCard`（给这几行归类） |
| 底部选择栏 | 多选后出现 | 同上 |
| 分类芯片条末尾 | `CategoryChipBar` 的 `IsAdd` 芯片 | `CategoryManageCard`（管理自建分类） |

弹窗开合统一由 `CategoryPopup`（`Views/CategoryPopup.cs`）管。

### 1.6 现有样式

- `AppTextBox` / `AppSearchBox`（`Themes/Controls.xaml:119-140,202-204`）：32 高、1px、圆角 6。
- `AppHeaderTextButton`（`:259-281`）：22 高、12 号字、无边框、悬停强调色 —— row1 小按钮的样式。
- `AppCategoryToggleButton`（`:232-257`）：32×32 圆形图标键（标题卡右上角）。
- `CategoryChipBar.xaml:26-83`：圆角 12 芯片、`Padding=9,2`、`SurfaceMuted` + `StrokeSubtle`、选中换 `Accent` —— 「设置分类」按钮的样式来源。
- 图标：`RowIcon.Category`、`ActionIcon.Cross`。
- 离屏验收：`tools/UiSnapshot`（不在 sln，单独 `dotnet build`）。

---

## 2. 目标形态

一张卡、两个状态：**标题** / **搜索**。所有筛选（分类、过时、选项、自由词）都是**搜索框里的标签**。

```
标题态
┌──────────────────────────────────────────────────────────────┐
│ 模组库                                                  [🔍] │   ← 分类切换键消失
│ 显示 12 / 30 个 Mod                                          │
└──────────────────────────────────────────────────────────────┘

搜索态（无标签）
┌──────────────────────────────────────────────────────────────┐
│ [ 搜索名称或备注…                                  ]   [🔍] │
│ 过时  选项  护甲  披风  武器  音效  材质/贴图 …     ┆设置分类┆ │
└──────────────────────────────────────────────────────────────┘

搜索态（有标签；胶囊方角、高密度；标签条溢出横向滚动、无滚动条）
┌──────────────────────────────────────────────────────────────┐
│ [ [过时 ✕][护甲 ✕][大衣 ✕] 健壮▏              ]   [🔍] │
│ 过时  选项  护甲  披风  武器  音效  材质/贴图 …     ┆设置分类┆ │
└──────────────────────────────────────────────────────────────┘
```

三条不变量：**入口只留一处**（搜索态里筛）；**标签即状态**（筛了什么就摆着什么，点得掉）；**卡片不长高**（32 / 22 两行固定）。

---

## 3. 交互规格（逐条可验收）

**S1 统一标签模型**：搜索框下面那一排按钮全部是"标签模板"——点一下把该条件变成框内一枚胶囊；行内按钮**常驻不消失**（否则加不回来），已加入时显示为已选外观（强调色文字）。
**S2 再点即取消**：点已选的按钮 = 去掉对应胶囊（幂等），不做反向文案。
**S3 无反向文案**：删掉「显示全部」「隐藏选项」这类"重置态文案"；一个按钮只有一名字。
**S4 加分类标签**：分类按钮 → 分类胶囊；分类胶囊之间取**并集**。
**S5 加自由标签**：框内输入若干字符后按**空格** → 整段按空白切分成若干自由标签，草稿清空；自由标签与草稿/分类之间取**交集**。
**S6 删标签**：点胶囊（整枚可点）= 删除；草稿为空且非输入法组合时按**退格** = 删最后一枚。
**S7 规范化**：标签去首尾空白、按空白切分、同名（忽略大小写）只留一枚；顺序 = 宿主筛选标签 → 分类标签 → 自由标签，各自内部保持原有顺序。
**S8 空格不误伤中文**：`Key.ImeProcessed` 或 `InputMethod.Current?.IsComposing == true` 时**不提交、不删胶囊、不清空、不设 Handled**。
**S9 关闭搜索 = 复位全部筛选（本次要修的既有缺口）**：`HideSearch()` 现在只清 `SearchText`，`ShowOnlyOutdated` / `ShowOptions` 会**留在生效状态而按钮已经消失**——这正是要防的"找不到搜索框却还在筛选"。改为：收起搜索时清空草稿、自由标签、分类选择、过时、选项，全部回到默认。
**S10 空态**：因标签无结果时空状态说"没有匹配"，动作文案改「清除全部筛选」；部分清除的两条路径仍在：逐枚 ✕、点按钮取消。
**S11 没有「全部」按钮**：离开搜索态就是一键清理（D5）；因此 `Chips` 里的「全部」与 `CategoryChipKind.All` 一并删除。
**S12 设置分类**：标签条最右端、放大镜正下方一枚「设置分类」按钮（沿用现有芯片外观：`RowIcon.Category` + 文字），点开 `CategoryManageCard`；新建/重命名/删除分类后标签条与胶囊同步刷新。
**S13 不撑高**：标签条整行 22；「设置分类」按钮自然高度 ≤22（`Padding=9,2` + 16 图标 + 1px = 22）。
**S14 横滚**：标签条与胶囊区都可横滚、**不显示滚动条**；滚轮在条上滚的是横向（纵向增量转横向），触控板横向滚动原生可用。
**S15 可达**：新增/切换/聚焦的胶囊与按钮必须 `BringIntoView`；滚动区两端渐隐遮罩提示"还有更多"。
**S16 焦点**：内部 TextBox 是唯一编辑焦点宿主。**鼠标**点按钮/胶囊后焦点回输入框（文末）；键盘 Tab 进按钮、Enter/Space 激活后焦点留在按钮。
**S17 加载即一致**：页面重建时若仍有任何标签/筛选生效，面板自行进入搜索态——不允许"标题态 + 筛选仍在生效"。
**S18 窄宽度**：输入区保底 96；胶囊区上限 = 框宽 − 96 − 内边距；更窄时胶囊区滚动，输入框不被挤没。
**S19 计数口径**：分类按钮 tooltip 的数量 = "文本与过时筛选之后、分类选择之前"的候选数（现有 `Chips` 口径），不写成"当前结果数"。
**S20 粘贴/空输入**：粘贴含空白文本进草稿不自动切标签（只有按键空格才提交）；提交结果为空则无操作。
**S21 胶囊外观（按用户口径）**：**方形 + 极弱圆角（1px，只磨掉角上一个像素）**，高 18，左右内边距 5，字号 11.5–12，✕ 8px 常驻；分类标签加一枚 12px 的分类图标；自由标签无图标、用中性底色。**目的：信息密度优先，绝不撑高 32 的输入框**。
**S22 三类标签的底色**：宿主筛选标签（过时/选项）= `SurfaceSelected` 底 + `Accent` 字；分类标签 = 同族更浅（`SurfaceMuted` + `StrokeFocus` 描边）；自由标签 = `SurfaceMuted` + `TextSecondary`。不只靠颜色区分：分类标签带图标。
**S23 无静默默认**：任何按钮点下去都要么改状态要么可见反馈；不得"点了没反应"。
**S24 分类按钮样式**：复用 22 高的 `AppHeaderTextButton`，**不做成芯片**（芯片 32 高会撑破 22 行）。

---

## 4. 实现方案

### 4.1 三份状态的唯一事实源（审阅 P0）

| 状态 | 唯一持有者 | 其他位置 |
|---|---|---|
| 分类选择 | `ModCategoryFilter.SelectedKeys` | 面板的胶囊、按钮已选态都是**只读投影** |
| 宿主筛选（过时/选项…） | 宿主 VM（`ShowOnlyOutdated` 等） | 面板通过 `ModListSearchFilter` 读 `IsSelected`、调它的 `ToggleCommand` |
| 自由标签 + 草稿 | `ModListPanel` / `SearchTagBox` | — |
| 有效查询 | 计算值 `Join(自由标签 + 草稿)` → 写 `SearchText` DP（宿主 `Query`） | 宿主**只读** |

`SearchTagBox` 只显示投影、只发意图事件（`TagRemoveRequested` / `CommitRequested` / `DraftChanged`），**不直接改筛选**。写入统一走：`ToggleCategory` / `RemoveFilterTag` / `AddFreeTag` / `RemoveTag` / `ClearAllFilters`。

### 4.2 查询计算单向（审阅 P0）

```
草稿/自由标签变化 → EffectiveSearchText = Join(freeTags, draft) → 写 SearchText（_writingSearchText 守卫）
外部改 SearchText  → 唯一解析入口：空 ⇒ 草稿与自由标签一起清；非空 ⇒ 视为草稿、清自由标签
```
禁止把有效查询反向绑到草稿；程序写入 `try/finally` + 同值短路；`Loaded` 顺序 = 解析 `SearchText` → 按 `CategoryFilter` 与宿主筛选重建胶囊 → 决定是否进搜索态（S17）。

### 4.3 新抽象：`ModListSearchFilter`（替代 `SearchActionsTemplate`）

`Controls/Lists/ModListSearchFilter.cs`：

```csharp
sealed class ModListSearchFilter : BaseViewModel
{
    string Key;            // "outdated" / "options" / "unit-hit" …
    string Label;          // "过时" / "选项" / "Unit 命中"
    bool IsSelected;       // 宿主状态
    string? ToolTip;
    ICommand ToggleCommand;// 宿主自己实现：翻转 IsSelected + 刷新列表
}
```

- `ModListPanel` 新增 DP `SearchFilters`（`IEnumerable<ModListSearchFilter>`）。
- 面板渲染：row1 左半 = 这些按钮 + 分类按钮（横滚）；row1 右端 = 「设置分类」。
- 点击 = 执行 `ToggleCommand`；胶囊删除 = 同一个 `ToggleCommand`。
- **删掉 `SearchActionsTemplate`**（三处用法全部迁移，见 4.7）；它保留下来只会让"按钮模型"分裂成两种。

### 4.4 新控件：`SearchTagBox`

`HD2ModManager/Controls/Search/SearchTagBox.xaml(.cs)`，与面板解耦，UiSnapshot 可单独渲染。

```
Border（沿用 AppTextBox 外观：32 高 / 1px / 圆角 6 / 聚焦 StrokeFocus）
└ Grid
  ├ col0: Auto（MaxWidth 由 SizeChanged 一次结算，见 S18）
  │   └ ScrollViewer(Horizontal=Hidden, Vertical=Disabled) > ItemsControl
  │        └ 胶囊（S21/S22）：方角 1px、高 18、图标 + 文字 + ✕；整枚可点 = 删除
  │              AutomationProperties.Name = "移除标签 X"
  └ col1: *  TextBox（无边框、透明底、VerticalContentAlignment=Center）
```

尺寸只在 `SizeChanged` 里**一次**结算 `capsuleMax = Max(0, boxWidth − 96 − padding)`，不做反馈式测量。

### 4.5 纯逻辑下沉到 Core（为了能写测试）

Manager 没有测试工程，而这次的风险几乎全在"状态归约"。把这些无 UI 依赖的规则搬进 `HD2ModCore/Infrastructure/`：

- `ModCategorySelection`：`Toggle / Remove / Clear / IsActive / PruneKnown(knownKeys) / Matches(modKeys)`，并集。
- `ModSearchQuery`：`Normalize(tags)`、`Compose(freeTags, draft)`（去空白、去重、保序、空格拼接）。

`ModCategoryFilter` 与面板都委托给它们；`HD2ModCore.Tests` 直接测：并集 / 空选择恒真 / 大小写与空白去重 / 分类被删后摘键 / 拼接 / 纯空格 / 重复词。

### 4.6 `ModCategoryFilter`：单选 → 多选

- `_selected` → `SelectedKeys`（委托 `ModCategorySelection`）；`Toggle(key)` / `ClearSelection()`；`IsActive` = 非空。
- `Matches`：无选择恒真；否则并集。
- `Rebuild`：**不再自动丢弃"数量为 0 的内置分类选择"**；只保留"自建分类被删除 → 摘键"。
- 删 `IsEnabled`、`CategoryFilterPreference`、`SettingsService.CategoryFilterEnabled`（旧 JSON 该键被忽略，无需迁移）。
- `Chips` 不再有「全部」与"设置分类"两项；`LibraryCategoryChip` 删 `Text` / `IsAdd`，`CategoryChipKind` 删 `All` / `Add`。

### 4.7 三个宿主页面

| 页面 | 改动 |
|---|---|
| 模组库 | `SearchFilters` = 「过时」「选项」两枚（`ShowOnlyOutdated` / `ShowOptions` 包一层）；删 `SearchActionsTemplate`；删 `HeaderModes`；`EmptyState` 把"有标签"算进"没有匹配"，动作改「清除全部筛选」 |
| 配置页 | `SearchFilters` = 「过时」一枚；删 `SearchActionsTemplate`；删 `HeaderModes`；`CanReorder` 继续用 `Category.IsActive` |
| 护甲登记 | `SearchFilters` = 「Unit 命中」「Unit 未命中」两枚**互斥**标签（替掉 3 态 cycle）。**需 D7 确认** |

### 4.8 样式

- 抽 `Themes/Controls.xaml` 的 `AppTagChipButton`（圆角 12 / `Padding=9,2` / 选中 `Accent`）给「设置分类」按钮。
- 分类/筛选按钮继续用 `AppHeaderTextButton`（22 高）。
- 胶囊用新样式 `AppSearchTagChip`（方角 1px、高 18），**不复用芯片样式**（S21）。

---

## 5. 决策记录

| # | 决策 | 用户口径 |
|---|---|---|
| **D1** | 标签条**只在搜索态**出现 | 「只在搜索态，并且确保不在搜索模式时清空搜索框，避免用户没找到搜索框但仍在筛选中」→ S9 落实为"收起即复位全部筛选" |
| **D2** | 分类标签之间取**并集** | 「两个都看」 |
| **D3** | 胶囊溢出**单行横滚**，卡片不长高 | 已确认 |
| **D4** | 分类按钮**只写名字**，数量进 tooltip | 已确认 |
| **D5** | **不要「全部」按钮** | 「用户只需要离开搜索模式就可以一键清理」+ 「分类标签保持克制、不要太大，避免撑大输入框；胶囊不必圆形，方形 + 极弱圆角；搜索框容量有限，尽量提高信息密度」→ S11 / S21 |
| **D6** | 老按钮**去掉所有反向文案**，统一成标签 | 「不需要全部这类说法了，过时标签移除后自然就是全部了、选项标签移除后自然也就是全部了，这是顺其自然的过程」→ S1–S3 |

### 5.1 按 D6 的思路，现有四个按钮逐个过一遍

1. **「过时」**（库页 / 配置页）：语义就是"缩窄到过时的"，天生是标签。改叫「过时」，选中=只看过时，移除=全部。`OutdatedFilterText` 与它那对反向文案整对删掉。**完全顺**。
2. **「选项」**（库页）：**这一项不顺**，必须点出来——它现在的语义是"把默认被折叠的选项 Mod 纳入列表"，是**扩宽**，不是缩窄。所以"移除后自然就是全部"在它身上不成立：移除后是"全部的非选项 Mod"。两条路（**D8 待定**）：
   - (i) **保留现语义**：标签名「选项」，tooltip 写"把作为「选项」的 Mod 也列出来（默认不列）"。选中=加进来，移除=回到默认。零功能损失；代价 = 这一枚与其余标签方向相反，且"移除=全部"的说法不成立。
   - (ii) **改成筛选语义**：默认列出全部（含选项），标签「选项」= 只看选项。语义最统一；代价 = 库页默认列表变长变吵，且失去"隐藏选项"的能力。
   - 推荐 **(i)**（不丢能力，只改文案形态），并在 tooltip 里把方向说清。
3. **「Unit 覆盖」3 态 cycle**（护甲登记）：循环按钮是"用文案扛状态"的最坏形态（点三次才回原点，且没有一键回全部）。按同一模型应拆成**两枚互斥标签**「Unit 命中」「Unit 未命中」：点其一 = 只看那类，再点 = 回到全部。这两枚都是**缩窄**，"移除即全部"完全成立。**D7 待定**（不在你指定的两张卡里，但 row1 重构绕不开它）。
4. **「设置分类」**：不是筛选，是动作，保持芯片样式、固定在最右端（S12）。

### 5.2 两个待定

- **D7**：护甲登记页那枚 3 态按钮，本轮一起改成两枚互斥标签吗？（推荐：一起改。否则 row1 上会同时留着"标签模型"和"循环模型"两套，且 `SearchActionsTemplate` 删除后它无处安放。）
- **D8**：「选项」按 (i) 保留现语义，还是按 (ii) 改成"只看选项"？（推荐 (i)。）

---

## 6. 风险与对策

| 风险 | 表现 | 对策 |
|---|---|---|
| 中文输入法误提交 | 打"大衣"按空格选词 → 变标签 | S8 双判据（`ImeProcessed` + `IsComposing`）；实机微软拼音过一遍 |
| 双写回环 | 标签一加，输入框重排/光标乱跳/清空被写回 | 4.2 单向计算 + 守卫 + 同值短路 |
| 状态漂移 | 胶囊还在、筛选已清 | 4.1 唯一事实源 + 投影重建；新建/重命名/删除分类走同一入口 |
| 22px 行被撑高 | 卡片变高、列表下移 | S13/S24：筛选按钮用 22 高文字按钮；胶囊只进框内（S21 高 18） |
| 隐藏滚动条 = 不可达 | 新加的标签在视野外 | S14/S15 滚轮转横向 + `BringIntoView` + 两端渐隐 |
| 焦点被按钮抢走 | 点完分类继续打字没反应；空格触发按钮 | S16 |
| 收起搜索后筛选残留（**既有缺口**） | 列表仍是子集，但没有控件解释 | S9 收起即复位 |
| 空态说错话 | 有标签无结果却显示"模组库是空的" | S10 + 两个宿主 VM 一起改 |
| 单选改多选改变手感 | 以前点第二个是"换"，现在是"加" | 胶囊把状态摆明；取消＝点胶囊 |
| 分类一直算（没有开关了） | 每次刷新多一趟分类计算 | 无标签时 `Matches` 短路；`Rebuild` 有 per-summary 缓存；`SelectedKeys` 用键集合 |
| 中间态不可用 | 多选改完、旧芯片条还按单选写 | 7. P1 要求旧芯片条在多选下仍可用 |
| 护甲登记页受牵连 | 选项卡状态消失 | 不删 `ModListHeaderMode`；验收单列一条 |

---

## 7. 实施阶段（每阶段可独立验收、可回滚）

- **P0** Core 纯逻辑（`ModCategorySelection` / `ModSearchQuery`）+ 单测；`AppTagChipButton`、`AppSearchTagChip` 两个样式；`SearchTagBox` 控件 + UiSnapshot 场景。**不动任何现有行为**。
- **P1** `ModCategoryFilter` 改多选 + 去 `IsEnabled`（连带 settings / 底栏 / 行内动作 / 两个页面空态）。**旧 `CategoryChipBar` 照常可用**（芯片高亮跟 `SelectedKeys`）——这一阶段自己能跑。
- **P2** 引入 `ModListSearchFilter`，把库页/配置页的「过时」「选项」迁成标签；面板接线（标签条 + 胶囊 + 空格 + 焦点 + 横滚 + 全清 + 加载即一致）；`HideSearch` 补 S9。
- **P3** 删分类模式：`CategoryChipBar`、两处 `HeaderModes`、面板的分类切换键、`SearchActionsTemplate`；更新 `ModListHeaderMode` 注释（"分类"举例换成"选项卡"）。
- **P4** 收尾（含 D7 若采纳）：护甲登记页迁移、文档、UiSnapshot 场景入库、实机回归 S1–S24。

---

## 8. 验证计划

1. **构建**：`dotnet build WpfApp1.sln -c Debug` → 0 警告 0 错误（先 `Stop-Process -Name HD2ModManager -Force`）。
2. **单测**：`dotnet test WpfApp1.sln -c Debug` 全绿；新增 `ModCategorySelectionTests` / `ModSearchQueryTests`。
3. **离屏**（`tools/UiSnapshot`）：`header-title` / `header-search-empty` / `header-search-tags`（3 分类 + 2 自由，故意溢出）/ `header-search-narrow`（窄卡片，验 S18）；除出图外**断言** `ActualHeight`（22 / 32）、胶囊高 18、`ScrollableWidth`、`HorizontalOffset`。
4. **实机**：真库 + 真分类，逐条过 S1–S24；**微软拼音**单独过 S8（组合中空格、组合中退格）；护甲登记页过 `ArmorTabs` 未被牵连。
5. **回归**：库 ↔ 配置切换后各自独立且不出现"标题态 + 隐藏筛选"；Ctrl+F；空态文案与清除动作；多选后底栏「设置分类」仍在；行内「设置分类」仍在；收起搜索后 `ShowOnlyOutdated` / `ShowOptions` 确实复位。

---

## 9. 明确不做

- 不改 `ModSearchMatcher` 的匹配算法（标签不引入"精确匹配"新语义）。
- 不动护甲登记页的选项卡状态（`ArmorTabs`）；不动行内动作与选择栏两个弹窗的内容。
- 不做标签持久化（切页不记忆标签；同页返回由 S17 保证一致）。
- 不改列表行与空状态外观。
- 不按"顺带优化"的名义改分页/排序等无关项。

---

## 10. Kimi 审阅结论与采纳情况（2026-11-18）

三名 Kimi（交互一致性 / WPF 实现与状态机 / 既有约定与回归面）并行审阅，终审 `fix_plan_first`：方向与 D1–D5 无分歧，列出 10 条必补。

| 审阅意见 | 采纳 | 落在哪 |
|---|---|---|
| 分类胶囊与筛选器两份状态会漂移 | ✅ | 4.1 唯一事实源 + 投影重建 |
| 草稿/自由标签/`SearchText` 双写 | ✅ | 4.2 单向计算 |
| IME 只判 `ImeProcessed` 不够 | ✅ | S8 双判据 |
| 焦点没有闭环（空格触发按钮） | ✅ | S16 |
| 两级隐藏滚动条会让内容不可达 | ✅ | S14 / S15 |
| 窄宽度与测量收敛未定义 | ✅ | S18 + 4.4 |
| 空态"清除搜索"文案与范围不符 | ✅ | S10 改「清除全部筛选」 |
| 静态截图证明不了行为 | ✅（折中） | 4.5 把纯逻辑下沉 Core 写单测 + 8.3 断言几何量；不新建 WPF 测试工程 |
| P1–P3 中间态没定义 | ✅ | 7. P1 明确要求旧芯片条可用 |
| 大小写/空白/重复/粘贴/组合中点击等边界 | ✅ | S7 / S20 |
| 分类重命名后胶囊文案同步 | ✅ | S12 |
| 计数 tooltip 口径 | ✅ | S19 |
| 「显示过时」「显示选项」必须去掉"显示" | ✅（经 D6 明确为**整排去反向文案**） | S1–S3；比审阅提的更进一步——不是保留二态只去字，而是改成标签 |
| 为两类标签加颜色以外的区分 | ✅ | S22（分类标签带图标） |
| 收起搜索即全清的误触成本 / 撤销 | ⏸ 不做 | 与 D1 一致：离开搜索本身就是唯一的一键清理 |

---

## 11. 实施记录（2026-11-18，P0–P4 全部落地）

### 11.1 最后落实的口径

| 项 | 结果 |
|---|---|
| D5 不要「全部」 | `Chips` 里不再产出「全部」与「设置分类」两枚；`CategoryChipKind` 只剩 BuiltIn / Pending / Custom |
| D6 整排标签化 | 过时 / 含选项 / 四个选项卡 / Unit 命中·未命中 全部是 `ModListSearchFilter`：一个名字 + 选中态，没有反向文案 |
| D8 「含选项」 | 语义 = **扩宽**（把默认折叠的选项 Mod 放出来），tooltip 写明"默认不列" |
| D7 护甲映射页 | 3 态 cycle 拆成「Unit 命中」「Unit 未命中」两枚；「套装/护甲/头盔/已映射」四枚也改成标签，**都不互斥**。四个之间方向不对称：前三个是缩窄（都不选 = 三类全要），**「已映射」是扩宽**——默认不列已映射的条目，选中才放出来（与「含选项」同一个模型，见 11.6） |
| 胶囊形态 | 方角 1px、高 18、字号 11、`Padding=5,0`、✕ 常驻 8px；分类胶囊多一枚 12px 小图标 |
| 「设置分类」 | `AppTagChipButton`（圆角 12、`Height=22`、`Padding=9,1`），钉在标签条最右端、放大镜正下方 |

### 11.2 新增 / 删除的文件

新增：
- `HD2ModCore/Infrastructure/ModCategorySelection.cs`（并集多选 + 摘键 + 命中）
- `HD2ModCore/Infrastructure/ModSearchQuery.cs`（标签/草稿 → 有效查询）
- `HD2ModCore.Tests/ModCategorySelectionTests.cs`（8 项）、`ModSearchQueryTests.cs`（6 项）
- `HD2ModManager/Controls/Search/SearchTagBox.xaml(.cs)`（标签 + 无边框输入框）
- `HD2ModManager/ViewModels/ModListSearchTag.cs`、`ModListSearchFilter.cs`
- `tools/UiSnapshot/HeaderSearchScenario.cs` + 4 个场景注册

删除：
- `HD2ModManager/Views/CategoryChipBar.xaml(.cs)`、`ModListHeaderMode.cs`
- `ModListPanel` 的 `HeaderModes` / `HeaderModeLayer` / `HeaderModeToggles` / `ActiveHeaderMode` / `SetHeaderMode` / `SearchActionsTemplate` / `HasSearchActions`
- `ModCategoryFilter.IsEnabled` / `CategoryFilterPreference` / `SettingsService.CategoryFilterEnabled`
- 各页的「显示过时 / 显示全部」「显示选项 / 隐藏选项」两对文案属性

### 11.3 顺手修掉的既有缺口

1. **收起搜索框不复位筛选**：旧 `HideSearch()` 只清 `SearchText`，过时 / 显示选项会留在生效状态而按钮已经消失。现在收起 = 复位全部（S9）。
2. **空状态说错话**：两个宿主 VM 的空状态都改成"有筛选 → 没有匹配"，动作文案「清除全部筛选」并且真的清干净。
3. **护甲映射页的已映射行**：以前靠页面级开关（`IsMappedTabSelected`）把整个列表设成只读；标签不互斥之后混在一张表里，改成逐行隐藏「注册」动作（`HiddenActions`），点击仍跳到左侧来源。

### 11.4 验证

- `dotnet build WpfApp1.sln -c Debug`：**0 警告 0 错误**。
- `dotnet test WpfApp1.sln -c Debug`：**2590 通过 / 1 失败 / 1 跳过**。
  唯一失败是 `ArmorMappingPickerPlannerTests.BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts`：
  它断言真实 `asset-index.sqlite` 里的套数（写死 120/116/118），而库里是 138 / 161 / 123 个 kit。
  DB 的 mtime 是 2026-11-17 17:34，本次改动没有碰它，也没有碰 `ArmorMappingPickerPlanner`。
  **这是环境相关的既有失败，不是本次引入的。**
- 离屏出图（`tools/UiSnapshot`，四个新场景）：标题态、搜索空态、搜索带标签态、560 宽窄态。
  确认：卡片仍是 32 + 22 两行、标签不撑高、胶囊是方角、输入区保底宽度生效、「设置分类」在放大镜下方。
- 冒烟：启动 `HD2ModManager.exe`，真实库（43 个 Mod）加载完毕，日志无异常。

### 11.5 只能人工验的

- 中文输入法下按空格（组合中不该提交标签；组合结束后按空格才提交）。备用路径：回车也能提交。
- 点分类按钮后继续打字（焦点应回到输入框）；Tab 进按钮、Enter 激活。
- 标签条/胶囊区滚轮横向滚动、触控板横向滑动。
- 鼠标点胶囊删除、空输入按退格删最后一枚。

### 11.6 追加修正（同日，用户口径）：「已映射」改成和「含选项」同向

用户的判断：已映射的条目也是"默认该藏起来"的东西，标签的作用是**把它们放出来**，不是去筛它们。

改动：
- `BuildVisibleAvailableRows` 分成两条轴——套装/护甲/头盔 缩窄（都不选 = 三类全要），
  **已映射 只在选中时追加**，且永不影响前三类。
- 所以默认列表 = 套装 ∪ 护甲 ∪ 头盔（不含已映射行），点「已映射」才多出一批只读行；
  只点「已映射」也不会把前三类筛掉（它是扩宽，不是"只看已映射"）。
- tooltip 改成"把已经映射到某个视觉来源的护甲也列出来（默认不列）"。

这与「含选项」构成同一条规则：**默认隐藏的条目由标签放出来，标签不负责把它们再筛掉。**

### 11.7 追加修正（同日）：「已映射」的行变成可选中 / 可拖放 = 重定向

用户指出：已映射的行被选中并拖到另一个来源，本身就是"重定向"——这比先解除再重新注册省事得多。
看了当前逻辑，卡点有三处：

1. `ResolveAssignableItems` 只查 `allAvailableChoicesByKey`（只收未管理的三类），已映射的行根本解析不到
   → `CanAssignFromAvailable` 恒 false → 拖放被拒；
2. `ApplyAvailableSelection` 同样只认那张表，所以已映射的行连"选中"都留不住；
3. `AssignFromAvailable` 里有一句 `Mappings.Any(mapping => mapping.Target.ItemId == id) → continue`：
   哪怕解析到了，已经映射过的护甲会被**静默跳过**——这正是"重定向"做不成的原因。

改法：

- 新增 `mappedChoicesByKey`（键 `mapped:<来源>:<护甲>`），只服务"按行键找回已映射的行"；
- `TryResolveAssignable` 统一判定：未管理的行随时可注册；**已映射的行只有在打开了一个来源时**才可注册
  （那时它才有目的地，否则会造出"既是目标又要当来源"的自相矛盾状态）；
- `AssignFromAvailable` 把"跳过"换成"搬走"：同一来源 → 幂等跳过；别的来源 → `Mappings.Remove(旧)` 再 `Add(新)`；
- `ApplyAvailableSelection` 改用 `TryResolveAssignable`，所以选中能留住，底栏「注册为映射目标」与拖放都能用；
- 行点击：左侧停在某个来源上时点已映射的行 = **选中**（别顺手把左侧来源换掉）；平时仍是跳到左侧来源。

副作用（照实说）：把某个来源名下**最后一条**映射挪走之后，那个来源会从左侧列表里消失——
它本来就只靠映射存在（和「解除映射目标」同一个后果），不是本次新引入的。

---

## 12. 护甲映射：层 2 加"来源表"（P1，已落地）

### 12.1 问题定性

层 2 的文档结构（`RuntimeArmorRegistryDocument`）原来只有 `Mappings / Models / PieceOverrides / TargetModes`，
**没有"来源"这张表**：一个游戏条目作为视觉来源，只以 `Mapping.SourceItemId` 的形式存在。于是

- 把一个护甲设为来源、还没配目标 → 保存后什么都不剩，重开就没了（`pendingProviderIds` 只在内存里）；
- 删掉某个来源的最后一个目标 → 来源随之蒸发；
- 而自定义素体没有这些问题，因为它们在 `Models[]` 里有独立记录。

结论：**来源必须是独立的一等数据**，和素体享受同一份待遇。

### 12.2 改动

- `RuntimeArmorRegistryDocument` 新增 `SourceItems`（`uint[]`）；对外读用 **`SourceItemIds` 纯计算属性**。
  为什么不做成 `ModelDefinitions` 那种 init 缓存：那张表必须显式重建，`with { Models = … }` 会带着旧值复制过去
  （同一个坑已经踩过两次，注释写在两个文件里）；这里从结构上不支持那个错误。
- 工作区 JSON：`sourceItems` 是新字段，**空表写成 null**（`WhenWritingNull` 整个省掉属性）——
  文件里"没有这张表"和"表是空的"长得一样，读回来看有没有映射可反推。
- 迁移：读到旧文件（字段缺失）时，用 `Mappings.Where(SourceItemId is not null)` 反推一次；
  素体映射的 `sourceItemId` 为空，天然排除。"已提升为来源但一个目标都没有"的旧数据本来就没落盘，救不回来。
- `pendingProviderIds` 整个删除（11 处引用），语义并入 `sourceItemIds`；
  `ProviderStillExists` 改成"在来源表里 / 有映射 / 是素体"三者之一。
- **刷新不再清空来源**：旧实现每次快照刷新都 `pendingProviderIds.Clear()`，正是"刚提升、还没配目标"被吞的原因；
  现在整表重读以磁盘为准，后台刷新只做并集。
- 新建映射后**不再**把来源从表里摘掉（来源是独立存在的）；
  解除来源 = 从来源表里摘掉 + 删它的映射（不变）。
- 应用（`ApplyAsync`）与读快照（合并素体）两处都是**整条重建文档**，都必须带上 `current.SourceItemIds`。
  这两处第一版都漏了，被新单测当场抓住（见 12.4）。

### 12.3 已知边界（不处理，按"忠诚翻译"）

- 工作区 JSON 用 `JsonUnmappedMemberHandling.Disallow`：**旧版管理器读新文件会把它当成损坏并隔离**。
  这是既有的前向不兼容行为，任何字段新增都一样；不做额外兼容层。
- 一个条目既当来源、又当别的来源的映射目标：层 2 允许，游戏内也接受。不检测、不拦、不修复。
- 来源表的变化本身不改变 `HasChanges`（它没有运行时效果，协议里只有 `model=`/`mapping=` 两种行），
  但会自动保存进工作区。

### 12.4 验证

- 新增 `HD2ModCore.Tests/ArmorRegistrySourceTableTests`（4 项）：
  无映射的来源表往返 / 旧文件反推 / 空表不写且读回为空 / 素体保留式保存不丢来源表。
- `ArmorRegistrySelectionScopeArchitectureTests.TargetCardStaysOpenWhileTheSourceStillExists` 里
  对 `pendingProviderIds.Contains` 的源码断言改成 `sourceItemIds.Contains`（判据不变，只是字段换名）。
- 构建 0 警告 0 错误；全量测试 **2593 通过 / 1 失败**（仍是 `ArmorMappingPickerPlannerTests` 的真实游戏数据套数，
  环境相关、与本次无关）；真实库冒烟启动正常。
- 用户当前的真实工作区：268 条映射**全是素体映射**、11 个素体、0 个游戏来源 ⇒ 迁移后来源表为空，行为不变。

### 12.5 下一步（未做）

- **P2 右侧单表**：`availableChoicesByTab` 四张投影 → 一张行表（`IsSource` / `MappedTo` / `IsHidden`），
  删 `mapped:` 伪键与 `mappedChoicesByKey`；「已映射」退回成"显示被占用的行"的可见性开关；筛选标签只筛选。
- **P3 降级与确认**：把左侧来源拖进某来源的目标列表 = 降级（释放它自己的目标）；
  名下目标数 ≥1 时汇总弹窗（每行一句"「X」已经作为来源并映射了 n 个对象" + 一句"重定向将会使这些条目被释放"），
  确定则一起降级，取消则只跳过这几个、其余"干净"的照做。

---

## 13. 护甲映射：右侧单表 + 来源降级（P2 / P3，已落地）

### 13.1 结构

右侧表**只有一张数据源**：游戏数据里的一行一条（套装 / 护甲 / 头盔）。每行自带占用状态：

| 行属性 | 含义 |
|---|---|
| `IsSource` | 这一项是视觉来源（在来源表里、或是某条映射的来源） |
| `IsMapped` / `MappedProviderKey` | 这一项被映射到了某个来源 |
| `IsHidden` = IsSource \|\| IsMapped | 被占用 ⇒ 默认不列 |

- 三个标签（套装 / 护甲 / 头盔）是**纯筛选**，都不选 = 三类全要。
- 「已映射」是**可见性开关**（`showOccupiedRows`），不是第八种筛选：只决定要不要把被占用的行放出来。
- 两轴分离：`IsBlocked` 仍然只表达真正的不可用（低污染 / 头身合并 / SEAF / NPC）；**占用不再影响能不能选、能不能拖**。

删除：`mapped:` 伪键、`mappedChoicesByKey`、`availableChoicesByTab[Mapped]` 那张投影、`TryResolveAssignable` 的第二张表分支；
规划器不再吃"已管理"表（改传空表），于是它不再**删行**也不再**置灰**被占用的行。

### 13.2 降级（把来源改成另一个来源的目标）

`AssignFromAvailable` 拆成两条路：

- **`AssignAsProviders`**（没打开来源）：把选中项登记进来源表；已经是来源的不重复登记，
  但会给一条提示告诉他"要改成别人的目标，先在左侧打开那个来源"——不静默。
- **`AssignAsTargets`**（打开了来源）：先看选中项里有没有**本来就是来源**的——
  - 名下 0 个目标：直接解散（摘来源表 + 删它名下映射，是空操作）后当目标收下；
  - 名下 ≥1 个目标：汇总成一次 `AppDialog` 确认（每行一句「X」已经作为视觉来源并映射了 n 个对象 +
    末尾"重定向将会使这些条目被释放"）。**确定** → 一起降级；**取消** → 只跳过这几个有目标的，其余"干净"的照做。
- 降级后目标之间仍然走"重定向"逻辑（同一来源幂等跳过、别的来源搬移而非新增）。

### 13.3 验证

- 构建 0 警告 0 错误；全量测试 **2593 通过 / 1 失败**（仍是那条真实游戏数据套数，环境相关）。
- 右侧表 XAML 的 20 条 `Binding` 全部能在 VM 上解析（脚本核过），避免"页面一打开就炸"。
- 真实库冒烟启动：三个页面 VM 正常预热，日志无异常。
- 仍需人工过的：打开护甲页 → 点亮「已映射」看被占用的行是否出现 → 拖一个来源到另一个来源的目标列表 →
  确认弹窗文案与"取消只跳过有目标的那几个"是否符合预期。
