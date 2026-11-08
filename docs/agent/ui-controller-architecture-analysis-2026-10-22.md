# UI 控制器化：现状测绘与契约设计（讨论稿）

**日期：** 2026-10-22
**性质：** 只做分析，不改代码。
**目标（复述成工程语言）：** 把"外观 + 几何 + 动画 + 主题"从逻辑层彻底抽到一个可复用的控件层，逻辑层只递交**声明**（我要什么、什么时候要、多大、能不能改大小），控件层负责**实现**（长什么样、怎么进场退场、怎么重排）。目标是让多个 agent 能按文件边界并行工作，而不需要互相读对方的上下文。

---

## 0. 结论

1. **这个项目不需要新框架，需要的是把三个已经存在、各自只做了一半的"控制器"合并成一个契约。**
   - `ModListPanel`：行级契约最完整（数据与外观分离、8 个页面复用），但**行内动作是 XAML 硬编码**，且只服务"行"。
   - `BottomBarRegistrationStore` + `BottomBarLayoutEngine` + `BottomBarSurface`：**分层最干净**（纯数学的布局引擎 + 无 WPF 的注册表 + 只负责动画的视图），但内容是 `object`，控件层无法保证外观。
   - `FloatingActionCluster` + `PageActionViewModel`：**声明式动作最接近目标**（集合变化 → 重算几何 → 逐项动画），但契约里漏了 `Brush`/`Icon`，还反向依赖了 Shell。
2. **两处真正的空白是"卡片"和"顶栏"。** 卡片今天有 46 处手写、0 处抽象；顶栏 5 个按钮全是 XAML 硬编码，加一个入口要动 4 个文件、8 个左右的编辑点。
3. **这个方向对 agent 分工的收益是可量化的**：现在"逻辑 agent 想加一个顶栏按钮"必须同时理解 `MainWindow.xaml` 的模板、`WorkspaceMode` 的语义、指示器的测量逻辑——这正是上下文被浪费的地方。

---

## 1. 现状测绘

### 1.1 规模与分层

| 层 | 文件 | 行数 | 现状 |
|---|---|---:|---|
| 页面 XAML | `Views/*PageView.xaml` | 15 个 | 每页手写卡片、按钮、间距 |
| 壳层 XAML | `MainWindow.xaml` | 385 | 顶栏 5 按钮、双槽、消息面板全硬编码 |
| 壳层代码 | `MainWindow.xaml.cs` | 787 | 窗口 chrome + 底栏布局应用 + 导航指示器 + 消息动画 |
| 逻辑 | `ShellViewModel.cs` | 2365 | 路由、命令、顶栏状态、页面装配 |
| 逻辑 | `PageViewModels.cs` | 2054 | 多页面 VM 合集 |
| 逻辑 | `BottomBarCoordinator.cs` | 533 | 底栏行注册与事务 |
| 控件 | `ModListPanel.xaml.cs` | 1176 | 行列表（契约最好） |
| 控件 | `BottomBarSurface.xaml.cs` | 138 | 底栏动画（分层最好） |
| 控件 | `FloatingActionCluster.xaml.cs` | 228 | 动作簇（声明式最接近） |

### 1.2 三个先例，各自的成色

**A. `ModListPanel` —— 数据/外观分离的标杆**

- 契约：`IModListSelectable` / `IModListKeyed` / `ModListRowPresentation`（6 个可选事实 + 5 种语义色调）。
- 复用：8 个页面、7 种 `RowActions` 组合。
- 不足：15 个行内按钮写死在 `DataTemplate` 里；加一个动作要同时改 XAML + flags 枚举 + 3 个 converter。
- **可偷的点：** 用"语义色调"而不是颜色；用 flags 裁剪能力；用"事实可缺席"避免空占位。

**B. `BottomBarLayout` / `BottomBarSurface` —— 分层最干净**

- `BottomBarLayoutEngine.Build()` 是**纯函数**，`RowHeight`/`RowGap` 是常量，不引用 WPF。
- `BottomBarRegistrationStore` 管注册、顺序、插入位置、去重，产出不可变快照。
- `BottomBarSurface` 只做一件事：把快照变成带进场/退场/位移的视觉。
- 不足：`Content` 是 `object`，控件层不知道里面是什么，所以**无法统一外观**；`RowHeight`/`RowGap` 仍写在逻辑侧。
- **可偷的点：** 注册 → 快照 → 视图 的单向数据流；`SourceId` + `InsertAtRow` 的插入语义；`Upsert` 幂等。

**C. `FloatingActionCluster` / `PageActionViewModel` —— 声明式动作最接近目标**

- 集合变化 → `UpdateShapeMetrics()` 重算圆角、宽度、总高 → 逐项 Y 位移动画（260ms 展开 / 190ms 收起，QuinticEase/CubicEase）。
- 不足（这是重点）：
  1. `PageActionViewModel` 带 `Brush Background` / `Brush Foreground` / `object Icon` / `double ExpandedWidth` —— **逻辑层在选颜色、选宽度、甚至选字形**。
  2. `Icon` **零消费者**：XAML 用 `Kind` 的 DataTrigger 选矢量图标，逻辑层传的 `"＋"`/`"□"`/`"↻"` 全被丢弃。契约已经漂移，但没人发现。
  3. 构造函数里 `Application.Current?.MainWindow?.DataContext is ShellViewModel` —— **控件反向依赖壳**，无法被别的页面/agent 复用。
  4. 动作**增删没有逐项动画**，只有展开/收起有。
- **可偷的点：** "集合变化即重排 + 逐项动画"的骨架；`Kind` → 图标的映射放在 XAML 侧（正确的那一半）。

### 1.3 两处空白

**卡片：46 处手写，0 处抽象。**

| 视图 | 手写卡片数 |
|---|---:|
| `ModDetailsPageView` | 9 |
| `HelmetToArmorMergePlanPageView` / `PatchResourceRepackagingPageView` | 5 / 5 |
| `CrossArmorPlanPageView` | 4 |
| `SettingsPageView` / `MainWindow` | 3 / 3 |
| 其余 9 个视图 | 1–2 |

它们今天**已经会动态出现**：详情页 9 张卡里至少 5 张由 `Visibility` 布尔控制（`ShowPatchSettingsCard`、`CanShowDecorationPlan`、`IsVpkMod`…），只是"啪"地出现，没有动画、没有尺寸约束、没有统一的空态。

**顶栏：5 个按钮全硬编码。**

`MainWindow.xaml:209-253` 每个按钮 = 一段 20 行 XAML + 一个 `Tag=Active` 的 DataTrigger；`MainWindow.xaml.cs:UpdateWorkspaceNavigationIndicator` 用 `switch (shell.CurrentMode)` 硬编码"哪个模式高亮哪个按钮"。新增一个入口（以 `ArmorRegistry` 为例）需要动：

- `WorkspaceMode.cs`（+1 枚举）
- `WorkspacePageType.cs`（+1 枚举）
- `ShellViewModel.cs`（命令 + `IsXxxActive` + 路由）
- `MainWindow.xaml`（4 处：按钮、样式触发器、绑定）
- `MainWindow.xaml.cs`（3 处：指示器 switch）
- `WorkspacePagePresentationController.cs`（1 处）

**共 4–6 个文件。** 这就是逻辑 agent 被迫读 UI 上下文的具体形态。

### 1.4 泄漏清单（谁越界了）

| 现象 | 数量 | 位置 |
|---|---:|---|
| ViewModel 里出现 `SolidColorBrush` / `Brushes.*` | 11 | `PageActionViewModel`、`ShellViewModel`、`PageViewModels` |
| ViewModel 里出现 `Color.FromRgb` | 6 | 同上（`"设为活动配置"` 绿色、`"停用"` 灰色…） |
| ViewModel 里含中文的字符串字面量 | 751 | 最大：`ModDetailsPageViewModel` 118、`ShellViewModel` 117、`ArmorRegistryPageViewModel` 114 |
| 视图/控件里 `Application.Current.MainWindow...ShellViewModel` 全局查找 | 21 | `LibraryPageView` 8、`ModDetailsPageView` 7、`ProfilePageView` 5、`FloatingActionCluster` 1 |
| 代码后置里 `DataContext is XxxViewModel` 强转 | 47 | `ModDetailsPageView` 11、`ArmorRegistryPageView` 10、`LibraryPageView` 8、`ProfilePageView` 8 |
| XAML 里由 VM 布尔驱动的 `Visibility` | 106 | 每个新卡片都要新增一个布尔 |

**判断：** 视觉泄漏是**少量但关键**（颜色/图标/宽度），文案泄漏是**大量但无害**（真正的问题不是文案在 VM，而是 VM 在拼版式，例如 `"显示 6 / 6 个 Mod · 活动配置：… · 正在编辑活动配置"`）。真正卡住复用的是**反向耦合**：控件知道 Shell，页面知道具体 VM。

---

## 2. 为什么"卡片宿主"是对的

1. **一处改，46 处生效。** 圆角、内边距、描边、标题字号、空态、hover 行为现在散落在 46 个 `<Border Style="{StaticResource CardContainer}">` 里；改成宿主后只改宿主。
2. **动画只有写一次的机会。** 高度变化动画在 WPF 里不平凡（`Height=Auto` 不能直接动画），必须"测量 → 动画 → 收尾归 Auto"，且要防并发版本冲突。这个逻辑如果由每个页面各写一遍，必然各写各的。
3. **尺寸策略需要统一。** "最大高度 xxx、最小高度 xxx"如果没有宿主，就只能每个页面自己包 `ScrollViewer` / `MaxHeight`——今天 `ModDetailsPageView` 已经有 `MaxHeight="{Binding ActualHeight, ElementName=DetailsScrollViewer, Converter={StaticResource AttachmentListMaxHeight}}"` 这种页面级补丁。
4. **主题系统需要一个落点。** 边框/背景/阴影如果由宿主解析 token，切主题只改 token；如果散在页面里，切主题要改 46 处。
5. **对 agent 分工的收益：** 逻辑 agent 只写"我要一张卡，Kind=DecorationPlan，MaxHeight=420，可见性绑定 X"，不需要打开任何 XAML。

---

## 3. 契约设计（只定形状，不实现）

### 3.1 所有权边界

```
HD2ModManager.Contracts/     ← 只放接口与 record，无 XAML、无业务
        ▲                ▲
        │                │
HD2ModManager.Controls/   HD2ModManager/ViewModels/
   （UI agent 独占）          （逻辑 agent 独占）
   控件、模板、动画、token      页面逻辑、命令、文案、可见性规则
```

三条铁律：

1. **逻辑层不得出现** `Brush`、`Color`、`FontSize`、像素宽度、图标字形、动画时长。需要"看起来是危险操作"就声明 `Role = Danger`。
2. **控件层不得出现业务判断**。控件不知道"Mod"是什么，也不知道"活动配置"是什么；它只认 `Id`/`Kind`/`Role`。
3. **内容模板由 UI 层拥有**。逻辑层给 `Kind` 和 `Data`，`Kind` 通过资源键约定映射到 `DataTemplate`；逻辑层永远不写 XAML。

### 3.2 卡片

逻辑层想要的写法（对应你说的"我要一个卡片"）：

```csharp
var card = cards.Add(new CardDescriptor(
    Id: "decoration-plan",
    Kind: "DecorationPlan",          // UI 层用这个键找模板
    Title: "装饰计划",                // 可为 null：无标题卡
    Order: 20,
    IsVisible: canShowPlan,          // 逻辑层决定
    MinHeight: 120,
    MaxHeight: 420,                  // 超过就内部滚动
    Data: planViewModel));           // 模板自己绑定

card.SetVisible(isVpkMod);           // 之后随时改
card.SetHeightRange(min: 160, max: 420);
```

控件层契约（已实现，见 §9）：

```csharp
public enum CardRole { Static, Interactive, Elevated, Notice, Danger }
public enum CardOverflow { Scroll, Clip, Grow }

public sealed class CardDescriptor : INotifyPropertyChanged
{
    public CardDescriptor(string id, CardKind kind, object? data = null);
    public string Id { get; }
    public CardKind Kind { get; }
    public string? Title { get; set; }              // null = 不渲染标题行
    public object? HeaderContent { get; set; }      // 标题行右侧动作（如"应用"）
    public object? Data { get; set; }               // 模板自己绑定
    public int Order { get; set; }                  // 宿主按此排序
    public bool IsVisible { get; set; }
    public double MinHeight { get; set; }
    public double MaxHeight { get; set; }
    public double? PreferredHeight { get; set; }    // null = 自适应；有值 = 动画到该高度
    public CardOverflow Overflow { get; set; }
    public CardRole Role { get; set; }
}
```

宿主通过 `Cards` 依赖属性接收 `ObservableCollection<CardDescriptor>`，监听集合与每个描述的
`PropertyChanged`。**没有引入 `ICardHost` 接口**：逻辑层只需要一个普通集合，不需要反向持有控件。
删除动画的"先动画后移除"需要宿主接管集合的增删时机，代价大于收益，暂时只做存活卡片的 FLIP 合拢。

宿主负责：外壳（圆角/描边/内边距/标题行）、插入/移除动画、高度变化动画、空态、主题解析、`Role` → 样式。
宿主**不**负责：卡片里放什么。`Kind` 找不到模板时降级为"只有外壳 + 提示"，而不是崩溃。

### 3.3 顶栏

```csharp
public enum NavItemRole { Primary, CapsuleZone, Utility, Danger }
public enum NavIconKey { Home, Library, Profile, Settings, ArmorRegistry, Messages }

public sealed record NavItemDescriptor(
    string Id,
    string Label,                    // 逻辑层给文案（这是数据，不是版式）
    NavIconKey IconKey,              // 枚举，不是字形
    int Order,
    bool IsVisible,
    bool IsActive,
    ICommand Command,
    NavItemRole Role = NavItemRole.Primary,
    string? Group = null);           // 同组渲染成一个胶囊（保留现在的三合一）
```

`INavBarHost` 负责：按钮外观、hover/pressed、激活指示块的滑动与尺寸动画、增删按钮后的重排动画、溢出折叠（窗口变窄时进"更多"）。
逻辑层负责：有哪些项、顺序、可见性、哪个是激活的。

**关键收益：** 现在"顶栏高亮"由 `switch (CurrentMode)` 决定；改成 `IsActive` 由逻辑层声明后，`MainWindow.xaml.cs` 里那段 switch 和 3 个按钮字段可以整体删除。

### 3.4 需要先修的既有契约（最小、可独立交付）

1. `PageActionViewModel`：删 `Brush Background/Foreground`、`object Icon`、`double ExpandedWidth`，换成 `PageActionRole`（Primary/Success/Neutral/Danger）、`PageIconKey`、`ExpandedPreset`。**这是验证整套思路的最小切口，只影响 3 个文件。**
2. `FloatingActionCluster`：去掉构造函数里对 `ShellViewModel` 的全局查找，改由宿主传事件/命令。
3. `BottomBarLayoutEngine.RowHeight/RowGap`：挪进控件层（逻辑层不需要知道 40 和 6）。

---

## 4. 难点与取舍（不先定就会返工）

1. **卡片内容归谁？**
   如果逻辑层能塞任意 XAML，等于没分离。建议：逻辑层只能给 `Kind` + `Data`，模板由 UI 层注册；一次性卡片允许页面内联，但**凡是会动态增删/改尺寸的卡片必须走宿主**。

2. **高度动画怎么实现？**
   WPF 不能动画 `Height=Auto`。可行路径：测量目标高度 → 动画 `Height`（或 `LayoutTransform`）→ 完成后归 `Auto`。项目里已有三块可复用原语：`MainWindow.AnimateContainerDimension`（带 version 防竞态）、`ModListTransitionController` 的 FLIP、`BottomBarSurface` 的位移补间。**建议直接抽成 `DimensionAnimator`，而不是各写一遍。**

3. **最小/最大高度与溢出的语义。**
   超出 `MaxHeight` 时是内部滚动、裁切、还是让卡片长高？三种都要能声明（`CardOverflow`）。默认 `Scroll`，因为它最不容易丢内容。

4. **排序语义。**
   `Order` 是"建议顺序"还是"绝对位置"？建议 `Order` 只在插入时使用，之后以 `Add` 顺序为准（与 `BottomBarRegistrationStore` 的 `InsertAtRow` 一致：插入提示，不是永久锁）。

5. **虚拟化 vs 逐卡动画。**
   卡片数量少（一页 ≤ 10），不做虚拟化，可以做逐卡动画；行列表数量大，必须虚拟化，只做视口级过渡。**不要把两者统一**，否则会为了动画牺牲滚动性能。

6. **主题系统的接口点。**
   宿主在加载时解析 token，不缓存 `Brush` 到 VM。若将来要热切换主题，宿主需要 `OnThemeChanged` 重新解析；现在只要保证"所有外观值都从 token 来"即可。

7. **动画与无障碍。**
   系统开启"减少动画"时应退化为即时切换。这需要宿主统一判断，而不是每个页面各自判断。

---

## 5. 迁移路径（每步可独立交付）

| 步 | 内容 | UI agent | 逻辑 agent | 验收 |
|---|---|---|---|---|
| S1 | 修 `PageActionViewModel` 契约（去 Brush/Icon/宽度） | 改 `FloatingActionCluster` 映射 Role→颜色、IconKey→Path | 改 13 处构造调用 | `UiSnapshot` 出图，浮动簇外观不变 |
| S2 | 抽 `DimensionAnimator` + `CardStackHost`（先只做外壳与增删动画，不做尺寸动画） | 实现控件 + 模板 | 选 1 个页面（建议 `ModDetails`）迁移 3 张卡 | 详情页三张卡的增删有动画、外观不变 |
| S3 | 卡片尺寸动画 + `CardOverflow` | 实现测量/动画/收尾 | 给现有卡声明 Min/Max | 长卡片内部滚动，不撑破页面 |
| S4 | `NavBarHost`（含胶囊分组与溢出折叠） | 实现控件 + 激活指示块动画 | 把 5 个入口改成描述符，删掉 switch | 顶栏外观不变，增删按钮自动重排 |
| S5 | 其余页面逐步迁移（每次一页） | 提供模板 | 改成描述符 | 每页出 before/after 图 |

**S1 是最小切口**：只碰 3 个文件、不改任何页面结构，却能立刻验证"逻辑层不再选颜色/字形"是否可行。建议从这里开始。

---

## 6. 给多 agent 分工的具体建议

1. **文件所有权写进仓库**：`tools/UiSnapshot` 已是现成的验收工具；再加一个 `docs/ui-contract.md` 作为冻结契约（改契约需人工确认）。
2. **契约先冻结、后实现**：`Contracts` 项目里只允许"新增"，不允许"改签名"；需要改就开新版本 record。
3. **逻辑 agent 的验收标准**：不打开任何 `.xaml` 也能完成一个功能；如果做不到，说明契约还缺东西。
4. **UI agent 的验收标准**：只改 `Controls/` + `Views/`，不碰 `ViewModels/`，且能用 `UiSnapshot` 出 before/after。
5. **禁止跨层兜底**：逻辑层不要写 `MaxHeight` 像素，UI 层不要写 `if (mod.IsVpk)`。

---

## 7. 风险与"不要做"的事

- **不要做通用 UI 框架。** 只服务本项目的三种宿主（卡片、顶栏、动作簇），不做数据绑定框架、不做控件库。
- **不要为了统一而统一。** 一次性、静态、无动画的卡片允许内联；强行描述符化只会增加间接层。
- **不要动 `ModListPanel` 的行契约。** 它是目前最成熟的一处，先把它抽出来当范本，而不是重构它。
- **不要在一次改动里同时动主题。** 宿主化本身已经改变 46 处外观来源，主题切换必须等宿主稳定后单独做。
- **不要在逻辑层引入 WPF 类型。** 契约项目只能引用 `System.Windows.Input`（`ICommand`）与基础类型。

---

## 8. 已确认的决策（2026-10-22）

1. **卡片宿主是新控件，不虚拟化。** 卡片一页最多十张，虚拟化只会让动画和测量变复杂。
2. **`Kind` 用"枚举 + 资源键约定"，不用显式注册表。** 理由见 §8.1。
3. **顶栏不折叠，改为横向滚动盒**：不显示滚动条，两侧做渐隐遮罩。窗口变窄时按钮滑出视野而不是缩排或藏进菜单。
4. **迁移从 `ModDetails` 开始。**

### 8.1 为什么是"枚举 + 约定"而不是注册表

对 agent 来说，最贵的不是写代码，是**发现**：要改一张卡，先得知道"项目里有哪些卡、这张卡长什么样、模板在哪"。

| 方案 | 发现成本 | 写错时 | 新增一张卡 |
|---|---|---|---|
| 裸字符串 `Kind: "DecorationPlan"` | 只能全局搜 | **静默**：找不到就空白 | 改 XAML，逻辑层无感知 |
| 显式注册表 `Register(kind, template)` | 要读注册代码 | 运行时才发现 | 改注册表 + 模板 |
| **枚举 + 键约定（选用）** | **打开一个 20 行的枚举文件** | **编译错误** | 枚举 +1、模板 +1 |

具体落地：

- `CardKind` 枚举是唯一的"卡片目录"，逻辑层写错值直接编译失败。
- 键约定 `CardTemplate.{Kind}` 让"这张卡长什么样"永远只有一个搜索目标。
- 模板解析用 `CardBodyPresenter.TryFindResource`（从自身往上找），**页面局部模板优先于全局模板**——内嵌 `ModListPanel` 那种带事件处理的卡，模板必须能写在页面资源里。
- 找不到模板时渲染红色占位并写 Debug 日志，不静默留白。

代价：枚举是"新增友好、修改昂贵"的契约。这正好符合我们的用法——卡片种类只增不改。

---

## 9. 已落地（2026-10-22，可运行）

| 文件 | 作用 |
|---|---|
| `Controls/Cards/CardKind.cs` | 卡片目录（枚举），逻辑层唯一取值来源 |
| `Controls/Cards/CardDescriptor.cs` | 逻辑层递交的卡片描述（Id/Kind/Title/可见性/高度/溢出/角色） |
| `Controls/Cards/CardStackHost.xaml(.cs)` | 宿主：外壳、排序、增删与可见性 FLIP、高度动画、缺模板兜底 |
| `Controls/Cards/CardBodyPresenter.cs` | Kind → 模板解析（页面局部优先，应用级兜底） |
| `Controls/Cards/CardTemplateKeys.cs` | 键约定 + 高度/溢出转换器 |
| `Themes/CardTemplates.xaml` | 应用级模板库（Text / Notice / KeyValueList） |
| `tools/UiSnapshot` 场景 `cards` / `cards-insert` | 模板画廊 + 插入动画中间帧 |

已用离屏渲染验证：按 `Order` 排序、`IsVisible=false` 不渲染、`Role=Notice` 换肤、`PreferredHeight`+`MaxHeight` 内部滚动（滚动条正确出现）、缺模板红色占位、插入时其余卡片 FLIP 让位。

### 9.1 ModDetails 已迁移（2026-10-22）

| 文件 | 变化 |
|---|---|
| `ViewModels/ModDetailsPageViewModel.Cards.cs`（新增） | `Cards` 惰性构建 + `ModDetailsCardLayout`（9 张卡的声明与可见性同步） |
| `Views/ModDetailsPageView.xaml` | 9 张手写卡片 → `<cards:CardStackHost Cards="{Binding Cards}"/>` + 9 份页面局部模板 |
| `tools/UiSnapshot/ModDetailsStub.cs`（新增） | 45 属性替身，用来出 before/after 基准图 |

页面从 252 行降到 ~300 行（模板拆开写，行数没少），但**卡片外壳、间距、标题、动画、主题全部移出了页面**。逐像素对比：6.6% 差异，全部来自文案 token 化（标题 `#000`→`#20242C`、正文 `#555`→`#626B7A`）、缩略图底色（`#EEF1F6`→`#F4F6F9`）和正文 12→13px；结构与位置一致。

**迁移中发现的一条硬约束（已写进契约）：** 带事件处理的卡片（修改图像 / 名称 / 备注、VPK 下拉滚轮）不能放进全局模板字典，只能写在页面资源里。所以 `CardBodyPresenter` 的解析顺序必须是"页面 → 应用"，不能反过来。

### 9.2 顶栏已迁移（2026-10-23）

| 文件 | 变化 |
|---|---|
| `Controls/Nav/NavItemDescriptor.cs`（新增） | `NavItemDescriptor`（Id/Label/IconKey/可见/激活/角标/命令）+ 按 Role 选模板 |
| `Controls/Nav/NavBarHost.xaml(.cs)`（新增） | 横向滚动盒（无滚动条 + 两侧渐隐）、激活块滑动、增删/可见性重排、角标、图标枚举 → 矢量 |
| `ViewModels/ShellViewModel.Nav.cs`（新增） | 声明 `LeadingNavItems` / `TrailingNavItems` / `UtilityNavItems`，并在状态变化时同步激活与角标 |
| `MainWindow.xaml` | 4 个导航按钮 → `NavBarHost` + 胶囊作为 `CenterContent`；消息按钮**保持原样**（见下） |
| `MainWindow.xaml.cs` | 删除 `UpdateWorkspaceNavigationIndicator` 里的 `switch (CurrentMode)` 与两处调用 |

顶栏现在这样声明：

```csharp
_leadingNavItems  = [ new("nav.home", "首页", NavIconKey.Home, ShowHomeCommand) ];
_trailingNavItems = [ new("nav.armor", "护甲映射", NavIconKey.ArmorRegistry, ShowArmorRegistryCommand),
                      new("nav.settings", "设置", NavIconKey.Settings, ShowSettingsCommand) ];
```

新增一个入口 = 一行 + 一个 `NavIconKey` 值 + 一条 Path 几何，`MainWindow.xaml` 不用动。

**消息按钮刻意没有并入**：它和导航不是一类东西（功能入口 vs 通知），而且后续可能换成 Windows 系统通知，届时整块删掉即可。所以它仍是 `MainWindow.xaml` 里那个独立的 `TopNavButton`，`TopNavButton` 样式也保留着给它用。

**顺带修掉的两个问题：**

- 顶栏按钮以前对读屏是空名字（`Content` 是 `StackPanel`，WPF 不聚合子文本）。现在模板里绑了 `AutomationProperties.Name`，实测 UIA 能读出"首页 / 护甲映射 / 设置"。
- **高亮块比按钮大 8px**：药丸原来按 `ContentPresenter` 取尺寸，而按钮样式里的 `Margin="4,0"` 会被算进容器宽度。改成取容器内 `Button` 本体的边界后，实测宽度从 86 DIP 回到 78 DIP（正好是按钮宽度），高度 31 DIP。

**验证方式：** 离屏渲染（`nav` / `nav-narrow` 两个场景）+ 真机截图 + UIA 名字核对 + 高亮块像素测量。窄窗口场景里"护甲映射"右侧正确出现渐隐。

**尚未做：** `PageActionViewModel` 去 Brush/Icon、其余页面迁移、主题系统。

### 9.3 动画纪律（从 `ModListTransitionController` 提炼，2026-10-23）

卡片宿主的第一版动画是"随手 FLIP"：改完可见性直接 `UpdateLayout()` 再读位置。结果就是用户看到的三个症状——出现没有动画、消失时下面的卡片乱跳、顶部闪出一段来自页面底部的卡片残影。

原因有三条，全部由 `ModListTransitionController` 的写法可以避免：

| 错误做法 | 后果 | 正确做法 |
|---|---|---|
| `UpdateLayout()` 之后立刻 `TransformToAncestor` | 读到的是**旧布局**，delta 被算成"从视口外飞进来" | 订阅 `ItemsHost.LayoutUpdated`，并等 `ItemContainerGenerator.Status == ContainersGenerated` 再读 |
| 用索引/容器对齐前后状态 | 容器被复用时对错行 | 按 **Id** 抓快照（`Dictionary<string, Snapshot>`） |
| 消失的卡片直接从集合里没了 | 没有退场，下面的卡片瞬间合拢 | 用**最近一次量到的矩形**在覆盖层上生成幽灵副本，140ms 淡出 |

现在 `CardStackHost` 的五步流程与控制器一致：

```
Idle --(集合增删 / IsVisible 变化)--> 抓 before 快照 --> AwaitingLayout
     --(LayoutUpdated + 容器就绪)--> 抓 after 快照 --> 逐项 StageMoved / StageAdded / StageRemoved
     --(下一个 CompositionTarget.Rendering 统一启动时钟)--> Playing
     --(240ms 计时器)--> Idle，并排空等待中的下一轮
```

**一个容易踩的坑：** `IsVisible` 是**绑定**驱动的，绑定的优先级高于 `PropertyChanged` 订阅者，所以回调收到通知时容器已经塌陷、量不到位置。解决办法是维护 `_lastKnown`（每次抓快照时顺手更新），消失时从里面取矩形。

**另一个坑：** 离屏渲染环境里动画时钟推进与墙钟不同步，按固定毫秒采样会错过中间帧。`UiSnapshot` 的两个动画场景改成**按状态采样**（等幽灵副本进入 0.15–0.85 的透明度区间再截帧），结果可复现。

### 9.4 首次出现不淡入（2026-10-23）
VPK 组件卡片的可见性依赖异步解析结果，实测有三种情况会丢掉淡入：

1. **页面还没加载完**：`BeginRound` 直接 `return`，变化被丢弃。
2. **上一轮还在播**：只置 `_pendingRound = true`，下一轮重新抓 `before` 时卡片已经在里面了，于是按"移动"处理。
3. **排队的那一轮永远不开始**：它等到的是"布局稳定之后"，此时不会再有 `LayoutUpdated`，卡在 `AwaitingLayout`。

修法：

- 新增 `_pendingShow` 集合，记录"在不能开播的时候变成可见"的卡片 Id；`PlayRound` 开播前把它们从 `before` 里摘掉，强制走 `StageAdded`。
- `BeginRound` 增加"布局已就绪就直接播"的分支——但**只对排队轮次生效**。刚发生的可见性变化不能走这条捷径：此刻 `IsMeasureValid` 可能仍为真，塌陷/展开却还没进布局，直接读位置会拿到旧值，下面的卡片就不会 FLIP（这个坑是 `cards-hide` 场景抓出来的）。
- 首次布局就绪后做一次 `SeedFirstLayout`：建立 `_lastKnown` 基线，并把加载期间变成可见的卡片补一轮淡入。

`UiSnapshot` 的 `cards-swap` 场景专门复现第 2 种：藏一张、同一时刻显示另一张，用 `rounds=2 / pendingShow=0` 断言第二轮确实按新增处理。

### 9.5 空状态组件（2026-10-24）

`Controls/EmptyState/`：

| 文件 | 作用 |
|---|---|
| `EmptyStatePresentation.cs` | `record (Title, Detail, ActionText, ActionCommand, Icon)` + 图标枚举与转换器 |
| `EmptyStateView.xaml(.cs)` | 图标 + 标题 + 解释 + 可选主操作，居中；`State` 依赖属性收内容 |

约定与卡片/顶栏一致：**逻辑层只给内容，控件层决定外观**。宿主 `ModListPanel` 新增 `EmptyState` 依赖属性；没给就回落到原来的 `EmptyMessage` 单行文字（颜色从 `#888` 提到 `TextSecondary`，3.54:1 → 5.38:1）。

库页按原因分三态，且**搜索优先**：

1. 有搜索词 → 「没有匹配的 Mod」+「清除搜索」
2. 隐藏已加入配置的 Mod → 「所有 Mod 都已加入此配置」（无动作）
3. 库为空 → 「模组库是空的」+「导入 Mod」（命令由壳层注入，页面不复制导入逻辑）

实测（真机）：输入 `zzz` → 显示"没有匹配的 Mod" + 清除搜索；点击后列表恢复。

---

## 10. 滚轮链（滚动劫持）设计（2026-10-23）

### 10.1 现状：机制已经存在，只是没被用起来

`SmoothScrollBehavior` 挂在页面级 `ScrollViewer` 上，它的 `PreviewMouseWheel` 会先问一句：

```csharp
var nested = FindNearestEnabledViewer(e.OriginalSource);   // 光标下最近的"已启用"内层 ScrollViewer
if (nested is not null && !ReferenceEquals(nested, viewer) && CanScroll(nested, direction)) return;  // 让内层滚
if (!CanScroll(viewer, direction)) return;                  // 内层到底了，页面接手
```

也就是说**链式滚动已经实现了**，前提是内层 `ScrollViewer` 被标记为 `SmoothScrollBehavior.IsEnabled="True"`。

三处实际表现：

| 场景 | 内层滚动区是谁 | 现状 | 结论 |
|---|---|---|---|
| VPK section 树 | `TreeView` 模板里自建的 `ScrollViewer` | 没人启用它 → **先滚页面**；页面到底后树才滚（见 §10.6 实测） | 顺序反了，属于体验问题，**不是"滚不动"** |
| VPK 目标护甲下拉 | `Popup` 里的 `ScrollViewer`（独立视觉树/窗口） | 弹层内滚轮本来就归它；只有"指针停在展开的下拉框本体上"会被页面吃掉 | 页面里那段 `OnVpkTargetArmorPreviewMouseWheel` 是补丁 |
| 详情页里的 ModListPanel | `ListBox` 模板里的 `ScrollViewer` | 控件自己 `SmoothScrollBehavior.SetIsEnabled(viewer, true)` | **已经正确**，可作为范本 |

### 10.6 实测（2026-10-24）：先页面、后内层

用 `UiSnapshot` 的 `wheel-probe` 场景，在离屏窗口里抛真实事件（先隧道 `PreviewMouseWheel`、未处理再冒泡 `MouseWheel`），结果：

```
tree viewer enabled=False
before: page=0.0   tree=0.0
after : page=78.4  tree=0.0   handled=True     ← 页面还能滚时，滚的是页面
at page bottom: page=960.0 tree=0.0 → 48.0    ← 页面到底后，树才滚
```

所以先前"树完全滚不动"的判断**是错的**，需要更正：树能滚，只是**顺序反了**——外层页面先吃滚轮，页面到底后内层才接手。详情页里这棵树靠近页面底部，用户滚到它时页面通常已经到底，所以体感是"正常"。

真正的不一致在于：`ModListPanel`（已启用）是**内层优先**，树是**外层优先**——同一个页面里两种行为。`WheelScroll` 仍然值得做，但定位从"修 bug"降级为"统一顺序"。

### 10.2 卡片宿主该不该提供"劫持"？

**不该。** 理由：

1. **粒度不对。** 一张卡片里可能同时有树、列表、下拉三种滚动区；"整卡劫持"会把这些都压成一个开关，反而做不了"树滚到底再让页面滚"。
2. **宿主不该懂输入语义。** 宿主的契约是外壳 / 布局 / 动画 / 主题；把滚轮路由塞进来，会让它重新变成"什么都知道"的控件。
3. **责任已经有主。** 滚轮属于"谁拥有滚动区"——也就是内容控件自己。宿主只需要保证两件事：它自己创建的正文滚动区参与链式滚动；覆盖层（幽灵副本、指示块）`IsHitTestVisible=false` 不吞事件。

### 10.3 建议的形态：一个挂在外层控件上的附加属性

内层 `ScrollViewer` 是控件模板创建的，逻辑层和页面都拿不到它；让每个页面去 `FindDescendant<ScrollViewer>` 也不现实。所以属性挂在**拥有滚动区的控件**上，行为自己去找那个 `ScrollViewer`：

```xml
<TreeView local:WheelScroll.IsEnabled="True" .../>
<ComboBox local:WheelScroll.IsEnabled="True" .../>
<ListBox  local:WheelScroll.IsEnabled="True" .../>
```

```csharp
public static class WheelScroll
{
    // 让这个控件"自带"的滚动区参与滚轮链：内层能滚就滚内层，到底了自动交给页面。
    public static readonly DependencyProperty IsEnabledProperty = ...;   // 附加属性，挂在 Control 上

    // 可选：Capture = 永不交给父级（例如模态面板里的列表）
    public static readonly DependencyProperty ModeProperty = ...;        // Chain（默认）| Capture
}
```

行为内部做三件事：

1. 控件加载后（以及模板更换后）在可视树里找到它的 `ScrollViewer`，`SmoothScrollBehavior.SetIsEnabled(viewer, true)`；
2. `ComboBox` 额外处理弹层：`IsDropDownOpen` 时把落在**下拉框本体**上的滚轮转给 `PART_Popup` 里的 `ScrollViewer`；
3. `Mode=Capture` 时跳过第 1 步的"让位"，并自己 `e.Handled = true`。

### 10.4 为什么这对逻辑 agent 最省事

- 逻辑层**完全不需要知道**滚轮存在：树、下拉、列表都是 UI 层模板里的控件，属性由 UI agent 写；
- 没有新增概念（不用理解 `ScrollViewer`、`Popup`、`PART_` 这些）；
- 三处现状的差异被抹平：树和下拉从"各写一段代码"变成"一个属性"；
- 页面里那段 `OnVpkTargetArmorPreviewMouseWheel` 可以删掉。

### 10.5 迁移步骤

1. 新增 `Controls/Scrolling/WheelScroll.cs`（约 120 行，含 ComboBox 弹层分支）；
2. 把 `CardStackHost` 的正文 `ScrollViewer` 接进来（它自己创建的，宿主负责）；
3. VPK 卡片模板的 `TreeView` 加 `WheelScroll.IsEnabled="True"`；
4. VPK 下拉加同一属性，删除页面代码后置里的滚轮补丁；
5. `UiSnapshot` 增加一个"嵌套滚动"场景：外层可滚 + 内层可滚，验证内层先滚、到底后外层接手。

**验收：** 光标停在 VPK 树上滚动 → 树先滚；树到底后 → 页面接着滚，中间没有"卡住一下"。

### 10.7 已落地（2026-10-24）

| 文件 | 变化 |
|---|---|
| `Controls/Scrolling/SmoothScrollBehavior.cs` | 从 `Views/` 移入控件层（命名空间改 `Controls.Scrolling`），5 个页面的 XAML 前缀跟着改 |
| `Controls/Scrolling/WheelScroll.cs`（新增） | 附加属性：控件自己就是 ScrollViewer / 模板里自带 ScrollViewer / ComboBox 弹层三种情况 |
| `ModDetailsPageView.xaml` | VPK 树与目标护甲下拉各加 `scroll:WheelScroll.IsEnabled="True"`；页面代码后置里的 `OnVpkTargetArmorPreviewMouseWheel` 删除 |
| `CardStackHost.xaml` | 卡片正文的 `BodyScroll` 也启用，长卡片先滚自己再滚页面 |

**ComboBox 为什么不能只"启用内层"**：它的滚动区在 `Popup` 里，是独立视觉树，页面拿不到。做法是下拉展开期间让 ComboBox **认领**滚轮（内部 `ClaimsWheel` 附加属性），`SmoothScrollBehavior` 在滚动前先问一句"有控件认领吗"，有就让位；随后 ComboBox 自己的 Preview 处理把滚轮转给弹层里的列表。这样既不用把 ComboBox 的特例写进通用行为，也不会和页面的隧道顺序打架。

**实测（`wheel-probe` / `wheel-probe-fixed`，真实路由事件）：**

| | 页面能滚时 | 页面到底后 |
|---|---|---|
| 修复前 | page 0→82.8，tree 0.0 | tree 0→48 |
| 修复后 | **page 0.0，tree 0→83.7** | tree 84→167.7 |

即：内层优先，内层到底后外层接手——和 `ModListPanel` 的行为一致了。


---

## 11. 参考评估：smooth-option-switcher（2026-10-24）

参考物：`artifacts/refs/smooth-option-switcher`（社区根据视频总结的 skill，含可运行 HTML/CSS 与无障碍版）。它的核心不是配色，而是四件事：

1. **常驻指示块**：indicator 一直在 DOM 里，切换只移动它，不重建；
2. **等宽分段**：指示块宽度 = 轨道 / N，位移 = index × 自身宽度；
3. **可逆的连续过渡**：`cubic-bezier(0.34, 0.96, 0.6, 0.99)`，被打断时从**当前视觉位置**继续；
4. **嵌套展开**：选中项原地展开成子选项，父标签下沉淡出，子指示块出现在父指示块内部。

### 11.1 映射到我们的三合一胶囊

| 参考特征 | 我们是否已有 | 结论 |
|---|---|---|
| 常驻指示块 | 有（`NavBarHost.ActiveIndicator`） | 已经一致 |
| 等宽分段 | **没有**（32 / 160 / 32） | 参考明确要求"不等宽必须显式测量并验证"，我们属于测量式适配，可以，但要保证指示块与分区严格等宽（上一轮刚修掉 8px 溢出） |
| 可逆连续过渡 | **没有**：`BeginAnimation` 用默认 `Replace`，打断时从基值重来 | **该借鉴**：改 `SnapshotAndReplace` + `From=当前值` |
| 标签颜色过渡 | **没有**：`DataTrigger` 瞬时切 Foreground | 可借鉴：两层文字交叉淡入 |
| 嵌套展开 | 无对应产品语义 | **不借鉴** |
| 三层柔和投影 + 1.5px 浅描边 | 没有（1px 描边、无阴影） | 可选，取决于是否接受"浮起"观感 |

### 11.2 评估暴露的结构问题

做对照图时发现：**胶囊自己并不自洽**——它只把激活区文字改成白色，蓝底一直由外部 `NavBarHost` 画的指示块提供。单独使用该控件（或宿主没画指示块）时，激活区就是**白底白字**。上一轮"组合区变白、文字消失"正是这个耦合的外在表现。

建议：让胶囊自己拥有激活背景，或提供 `ActiveZoneBrush` 由宿主注入，把"谁画底"变成显式契约。

### 11.3 三合一的结构备选

现在是三个不等宽热区：`[图标] [配置页 | 模组库] [图标]`。中间那块既是文字又是按钮，两侧图标没有可点的暗示。

备选（借参考的"指示块几何反映状态"）：**两段 + 中间连接**

```
[ 配置页 ][⇄][ 模组库 ]    单页：一个药丸
[ 配置页 ][⇄][ 模组库 ]    双页：两个药丸（左右各一）
```

好处：状态一眼可读（哪几块亮着 = 哪几块开着），不用记"点文字=双页"。代价：`CapsuleButtonGroup` 要支持双指示块，VM 的三个命令重新映射（左段→仅配置页、右段→仅库、连接→双页）。

这是产品决策，先不改。

### 11.4 已落地（2026-10-24）

只做了两条，视觉保持扁平（没有加投影）：

**1. 指示块可逆续播。** `PlaceIndicator` 的四处 `BeginAnimation` 改成
`HandoffBehavior.SnapshotAndReplace`：打断一段进行中的滑动时，新动画从**当前视觉位置**
继续，而不是退回基值重来。默认的 `Replace` 会让快速连点看起来"跳一下"。

**2. 标签交叉淡入。** 每个导航项现在是两层文字：底层跟随按钮 `Foreground`（未激活配色，
悬停时变强调色），上层固定白色、`Opacity=0` 叠在上面。`IsActive` 变化时两层在 220ms 内
互换透明度——比指示块的 240ms 略短，看起来是"指示块滑到位时字已经是白的"。

顺带修掉一个只有看中间帧才会发现的问题：**逻辑层是逐个改 `IsActive` 的**，"旧项置 false"
和"新项置 true"之间有一瞬谁都不激活，原来的代码会立刻把药丸隐藏，于是它在移动途中
消失又淡入（实测中间帧只有 32% 不透明度）。现在改为把隐藏推迟到下一批数据绑定之后
再确认一次，中间态不再闪。

**还留着一个观感问题（未改）：** 指示块从"首页"滑到"护甲映射"时会**横穿中间的胶囊**，
胶囊的图标因此短暂压在蓝底上（深色图标 / 蓝色底）。根因还是 §11.2 那个耦合——胶囊的
激活区需要外部药丸透出来当底色，所以胶囊不能有不透明背景，药丸也就藏不到它后面。
如果将来让胶囊自己拥有激活背景，这个横穿就可以变成"药丸从胶囊背后穿过"。

---

## 12. ModListPanel 能力盘点（2026-10-29）

**性质：** 只做盘点，不改代码。
**问题：** 面板已经很成熟（10 个实例、8 个页面），但仍有一批"模板本可以提供、调用方在重复实现"的能力。样本取最常用的两个页面（模组库 `LibraryPageView` / 配置文件 `ProfilePageView`），并用其余 8 个实例做对照。

### 12.1 结论

1. 被点名的三件事里，**两件已经提供了、但只提供了一半**：
   - **拖入叠层**：面板内置了一份（`ExternalProfileDropOverlay`），但"关系拖放"的叠层由 3 个页面各写一遍 → 全项目 **5 份同形叠层**，2 种不透明度、3 组淡入参数、2 种标题字号。
   - **空列表内容**：面板有富空态 `EmptyState`，但 10 个实例里**只有 2 个用**，其余 6 个仍是单行 `EmptyMessage`；过渡层的旧投影也只认 `EmptyMessage`。
   - **拖到外部**：只有两个带域名的布尔（`AllowExternalProfileDrag` / `AllowExternalProfileRemovalDrag`），第三种拖出意图无处声明。
2. 真正缺失的能力集中在 **"拖放的语义与反馈"** 和 **"行内动作的声明化"** 两块。
3. **不该模板化的**：选择策略（`ModListSelectionPolicy`）、键盘命令语义（页面解释 Delete 的含义）、叠层**文案**。这三处已经是正确形态。

### 12.2 拖放能力

| 能力 | 现状 | 谁在实现 | 判定 |
|---|---|---|---|
| 拖入叠层（配置方向） | 面板内置 `ExternalProfileDropOverlay` | 面板 | 已提供 |
| 拖入叠层（关系方向） | 每页自建 `Border` + 淡入/淡出/hit-test | Library、ModDetails、ArmorRegistry（×2） | **该上收** |
| 叠层视觉一致性 | `#D9FFFFFF`(面板) vs `#E8FFFFFF`(3 页)；淡入 0.92/140ms、0.94/120ms；标题 15/16px | — | **已漂移** |
| 叠层覆盖范围 | 面板=列表卡片；页面=整页（含表头与页边距） | — | **不一致** |
| 拖出意图声明 | 2 个布尔，名字里带 "Profile" | 面板 | **能力被写死** |
| 多拖放目标 | 关系协调器支持 N 个目标；两个 profile 协调器是单例（只记 1 个） | — | **架构分叉** |
| 目标能否接受这批条目 | 关系拖放有 `CanAcceptRelationshipDrop`；profile 拖放只看一个布尔 | — | **缺口** |
| 放下位置 | 内部重排有 `GetInsertionIndex`；外部拖放只能追加 | 面板 | 设计如此（文案已声明"不改变顺序"），但模板化时应可声明 |
| 不可接受的反馈 | 只有"显示叠层 / 什么都不显示"两种 | — | **缺口** |
| 拖拽期间边缘自动滚动 | 只在内部重排时启用 | 面板 | 小缺口 |
| 手势优先级 | 内部重排 > 关系 > 外部，固定链（`OnItemsListPreviewMouseMove`） | 面板 | 页面无法表达"拖左边重排、拖右边建关系" |

### 12.3 空状态

- **采用率 2/10**：库 / 配置用 `EmptyState`；ArmorRegistry×3、BatchDecoration、DecorationPlan、HelmetToArmorMerge、PatchResourceRepackaging 仍用 `EmptyMessage` 单行文字。
- **过渡层只认 `EmptyMessage`**（`OutgoingPresentationLayer` 内只有一个 `TextBlock`）→ 批量过滤时两侧空态不同形（居中图标+标题 vs 左上单行），切换瞬间会跳。
- **"过滤后为空"与"真的为空"的判断每页各写**：`LibraryPageViewModel.EmptyState` 是三分支（搜索 → 隐藏成员 → 真空），`ArmorRegistryPageViewModel` 用两个 `EmptyMessage` 属性。面板已经同时持有 `SearchText` 和原始 `ItemsSource`，可以自动产出"没有匹配"的默认态，页面只在"真的空"时提供自定义内容。
- **空态与拖放提示是两套东西**：列表空时最需要"把 Mod 拖进来"，但提示要等真的拖进来才出现。
- `EmptyMessage` 应当标 `[Obsolete]`，否则富空态永远只是可选项。

### 12.4 行级

- **16 个行内按钮硬编码**（`ModListPanel.xaml:192-283`）。加一个动作要改 4 处：`ModListRowAction` 枚举 + XAML 按钮 + 3 个 converter 之一 + 页面 `switch`。
- **动作 pill 覆盖元信息**：`RowIconButton` 宽 28 + 边距 2 → 32px 步进；库页 7 个动作 ≈ 228px 宽的 pill，压在同一条右对齐的备注上（`ActionPanel` 与文本同处 `Grid.Column=2`，纯叠加，不占位）。
- **10 个死模板**：`LibraryPageView.xaml:6-20` 与 `ProfilePageView.xaml:6-20` 各 5 个 `DataTemplate x:Key="...Icon"`，面板根本不用（自己内联 `Path`）。这是"调用方以为模板提供了图标槽位"的化石。
- **元信息 6 个固定槽位**（类型/激活/顺序/警告/摘要/备注），不能增减、调序或声明优先级；`ModListRowPresentation` 加一个事实要动 record + XAML + converter。
- **缩略图固定 36×36**，无声明。
- **无右键菜单**：右键只 raise `RowRightClicked`，两个页面各自去开详情；用户期望的重命名/删除/打开文件夹没有入口。
- **行内动作仅鼠标可达**：键盘只有 F2 / Enter / Delete。
- `ShowSelectionCheckbox` 的 `CheckBox` 是 `IsHitTestVisible="False"`，点 checkbox 本身无效（整行才是命中区），三个计划页的勾选语义因此只能靠"点行"。

### 12.5 搜索 / 表头

- **搜索展开状态是 UI-only**：`SearchText` 由 VM 持有（双向绑定），`HeaderSearchBox.Visibility` 由面板持有。页面视图重建后会出现"列表已被过滤、但看不到搜索框、也不知道怎么清除"。
- `SearchActionsTemplate` 已是正确形态（模板 + 命令），不动。

### 12.6 优先级建议

**P0（真实缺陷 / 一致性，成本低）**

1. 搜索框从 `SearchText` 非空自动展开。
2. 过渡层旧投影也渲染 `EmptyState`。
3. 6 个 `EmptyMessage` 迁到 `EmptyState`（或先由面板自动派生"没有匹配"态）。
4. 删掉 10 个死图标模板。

**P1（就是"该模板化但没模板化"的那批）**

5. `DropHint` 声明 + 面板统一渲染叠层，替掉 3 个页面约 70 行 × 3 的样板与 4 个 `Border`。
6. `ExternalDragKind` 取代两个 profile 布尔；两个 profile 协调器合并进 `ModRelationshipDragCoordinator`（N 目标 + 每目标 `CanAccept` + 每目标叠层文案）。
7. `RowActionDescriptor` 声明化 + pill 溢出菜单（顺带解掉 pill 覆盖元信息）。

**P2**

8. `RowFact` 列表取代 6 个固定槽位（顺带解掉警告标签被摘要挤掉的老问题）。
9. 右键菜单由同一套 `RowAction` 生成。
10. 拖拽期间目标列表的边缘自动滚动。

**不做**：选择策略、键盘命令语义、叠层文案上收到控件层。

### 12.7 需要拍板

1. **关系拖放叠层盖多大**：整页（现状，含表头/页边距）还是列表卡片（与面板内置一致）？
2. **外部拖放要不要插入点**：现在只能追加，文案已声明"不改变排列顺序"。保持还是支持插入？
3. **pill 溢出形态**：全部显示（现状，压备注）/ 前 N 个 + "更多"菜单 / 悬停时显示并让备注临时让位？

### 12.8 已落地（2026-10-29）

**拍板结果：** 叠层盖"列表卡片"（与面板内置一致）；外部拖放保持只追加；pill 保持全部显示（因为 pill 本来就只在悬停时出现，覆盖只发生在悬停期间，不算缺陷）。所以 P1-7 缩成"纯声明化"，不做溢出菜单。

#### 搜索：从"能搜"变成"不会骗人"

不变式：**查询非空 ⟺ 搜索框可见**。两个方向都补上了：

- `SearchText` 有 `PropertyChangedCallback`：非空且搜索框收着 → 自动展开（不抢焦点）。
- `HideSearch()` 先清空 `SearchText` 再淡出。以前收起搜索框只藏控件、保留过滤，列表停在筛过的结果上而解释它的控件已经消失——用户会以为 Mod 丢了。

#### 空状态：面板自己派生"没有匹配"

`ModListPanel` 新增只读 DP `EffectiveEmptyState`：

1. 宿主给了 `EmptyState` → 用宿主的（页面文案更具体）。
2. 没给、但 `SearchText` 非空 → 面板派生通用态「没有匹配的条目 / 试试更短的关键词… / 清除搜索」，动作命令由面板自己实现（清空 `SearchText` 并聚焦搜索框），宿主不需要提供命令。
3. 都没有 → 回落到 `EmptyMessage` 单行文字（6 个老页面）。

这一步让 6 个还在用 `EmptyMessage` 的页面**白拿了**一个正确的"过滤后为空"态，不需要改任何 VM。

顺带修掉一个谎报：整批替换（removed + inserted >= 16）时，旧投影层原来渲染 `EmptyMessage`——但那个绑定是**当前**文案，旧投影为空时它显示的是新状态的文案。现在旧层不渲染空态（留空、让新投影自己淡入），`HasOutgoingPresentedItems` 随之删除。

#### 行内动作：声明化

契约搬到控件层 `Controls/Lists/`（与 `Controls/Cards`、`Controls/Nav` 同级）：

| 文件 | 内容 |
|---|---|
| `ModListRowAction.cs` | 枚举（从 `Views` 移入）、`IModListRowActionFilter`（行级抑制）、`ModListRowActionItem`（渲染项）、`IRowActionToolTipSource`（宿主文案覆盖） |
| `ModListRowActionCatalog.cs` | 16 条声明：Action / IconKey / 默认提示 / 线宽 / 是否危险 |
| `RowActionListConverter.cs` | 唯一生成点：`[宿主 flags, 行对象, 宿主]` → 渲染项 |

行模板从 **16 个手写 Button + 3 个可见性转换器** 变成 **一个 `ItemsControl` + 一个 Button 的 `DataTemplate`**。加一个动作现在是：枚举 + catalog 一行 + 一个 `RowIcon.*` 几何资源，模板一行都不用改。

- 行级抑制从"模板按属性名猜 `IsCustomModel` / `CanJoinProfile` / `IsAttachable`"改成行自己回答：`ModCardViewModel` 与 `RuntimeArmorChoiceViewModel` 实现 `IModListRowActionFilter.HiddenActions`。可选接口，其余 6 种行类型不用改。
- 4 个护甲相关文案仍由面板 DP 覆盖（"解除视觉对象" vs "解除映射目标"），通过 `IRowActionToolTipSource` 注入，控件层不反向依赖 `Views`。
- 顺带删掉 10 个死模板（库页 / 配置页各 5 个 `DataTemplate x:Key="...Icon"`，面板从来没用过）。

**踩到并修掉的坑：** 按钮进了 `ItemsControl` 之后，按钮的 `DataContext` 是动作项而不是行对象。所以 `Tag="{Binding}"`（整个动作项），行对象从 `ListBoxItem` 容器上取回。这个错是靠新加的探针场景抓到的，不是靠肉眼。

#### 验证

新增两个快照场景（`tools/UiSnapshot`）：

- `modlist-empty-search-derived`：只设 `SearchText`、不给 `EmptyState` → 渲染出派生空态（图标 + 标题 + 解释 + 清除搜索按钮）。
- `rowaction-probe`：真的点一次第一个动作按钮、再点一次搜索开关，把结果写进表头。当前输出：

  ```
  PROBE: FakeRow/Rename · buttons=6 · searchAutoOpen=True
       · derived='没有匹配的条目' · clearedOnHide=True
  重命名 | 查看详情 | 覆盖文案-加入视觉来源 | 启用或禁用装饰 | 打开文件夹 | 从模组库彻底删除
  ```

  一次证明六件事：事件带回的是**行对象**而不是动作项；动作是 catalog 的第一项；行级抑制生效（7 声明 − 1 隐藏 = 6）；宿主文案覆盖生效且顺序由 catalog 决定；查询非空时搜索框自动展开并派生空态；点开关收起后查询被清空、派生态消失（不会留下"看不见的过滤"）。

回归：`modlist` / `modlist-selected` / `modlist-checkbox` / `modlist-empty` / `modlist-empty-state` / `moddetails` / `cards` 重渲染无变化；整个解决方案 0 警告 0 错误。

#### 还剩的

- **P0-3 的剩余部分**：6 个页面仍用 `EmptyMessage` 的单行文案（"真的为空"那种情况）。派生态只覆盖了"过滤后为空"。要迁移得逐页写文案，属于内容工作。
- **P1-5 `DropHint` 上收**：3 个页面约 70 行 × 3 的叠层样板 + 4 个手写 `Border`，加上 5 份视觉的 2 种不透明度 / 3 组动画参数 / 2 种字号。
- **P1-6 拖放协调器合并**：`ModRelationshipDragCoordinator`（N 目标 + 每目标 `CanAccept`）与两个 profile 单例协调器的架构分叉。
- **过滤器残留（新发现）**：`SearchActionsTemplate` 里的过滤开关（只显示过期 / 装饰 / 选项）跟着搜索行一起隐藏，但**过滤状态留在 VM 里**。收起搜索框后列表仍被过滤、控件却看不见——和搜索那个坑同一类。面板管不了（模板是黑盒 + 命令在 VM），需要拍板：收起时也清过滤，还是把过滤做成常驻可见的 chip。

  **（2026-10-29 拍板：保持现状，不做处理。）**

### 12.9 DropHint 上收（2026-10-29）

原来有 **5 份手写的同形拖放叠层**：面板内置 1 份（`ExternalProfileDropOverlay`，`#D9FFFFFF` / 1px / 0.92-140ms）+ 3 个页面各写 1–2 份（`#E8FFFFFF` / 1.5px / 0.92-140ms 或 0.94-120ms，标题 15 或 16px），每份都自带一套 `PointFromScreen` 命中、淡入淡出、`Reset` 样板。

现在只剩 **1 个控件**。

#### 新增

| 文件 | 内容 |
|---|---|
| `Controls/Lists/ModListDropHint.cs` | `record (Title, Detail?)`——宿主只说给用户听的话 |
| `Controls/Lists/DropHintOverlay.xaml(.cs)` | 叠层视觉 + `Show/Hide/HideImmediately` + `ContainsPoint(Point)`；`#D9FFFFFF` / 1px / 0.92-140ms 入 / 0-120ms 出 |

#### 两种目标形状，对应两种接入方式

1. **列表形状的拖放区**（库页：整页就是一个列表）→ 面板自己当目标：

   ```xml
   <local:ModListPanel AcceptsRelationshipDrop="RemoveOptionParent"
                       RelationshipDropRequested="OnRelationshipDropRequested"/>
   ```

   面板负责注册/注销（随 `AcceptsRelationshipDrop` 与 `Loaded` 同步）、叠层、命中、`IsVisuallyActive` 判定、按操作类型的默认文案。宿主可选给 `RelationshipDropFilter`（域判定）和 `RelationshipDropHintFactory`（更具体的文案）。

2. **页/列形状的拖放区** → 继续自己实现 `IModRelationshipDropTarget`，但叠层用 `DropHintOverlay`：
   - 详情页：拖到整页 = 设为该主体的选项。
   - 护甲映射页：左列 = 设为视觉来源 / 添加映射目标，右列 = 解除管理。

#### 两个踩到的坑（都是"看起来等价、其实不等价"）

1. **护甲映射页不能改成面板级目标。** 左列同时承载来源列表和**组件编辑器**，而编辑器是面板的兄弟节点且声明在后——面板内部的叠层会被编辑器盖住，但 `ContainsScreenPoint` 仍然命中，结果是"看不见但会生效"的静默操作。旧实现的叠层是页面级 `ZIndex=20`，本来就该盖在编辑器之上。**结论：拖放区是"列"而不是"列表"时，目标必须留在页面级。**
2. **`IsAncestorOf(self)` 是 false。** 面板既是源又是目标（库页拖自己的行去建关系）时，`ContainsElement` 必须显式比较引用，否则会在自己身上弹"释放以解除关系"，松手还会真的执行。断言 `selfExcluded` 已加入探针。

#### 验证

`relationship-drop-probe`：两个面板并排，源 `RelationshipDragOperation=SetAsOption`，目标 `AcceptsRelationshipDrop=SetAsOption`，直接驱动目标接口并断言：

```
PROBE: accepted=True · hitsInside=True · hitsOutside=False · selfExcluded=True
     · dropped=SetAsOption/2 · detail='共 2 项'
```

叠层在截图里正常渲染（盖住列表卡片、不盖表头——即已拍板的"盖列表卡片"）。注意：叠层是淡入的，离屏渲染必须用 `host.PumpFor(260)` 把动画时钟推过去，`Pump()` 不会推进时间轴，否则截到的是 `Opacity≈0`。

#### 收益

| 位置 | 之前 | 之后 |
|---|---:|---:|
| `LibraryPageView.xaml.cs` | ~85 行接口实现 + 22 行叠层 XAML | ~18 行（一个事件 + 一个文案工厂） |
| `ArmorRegistryPageView.xaml.cs` | ~95 行接口实现 + 2×16 行叠层 XAML | ~48 行（两个叠层共用一个文案方法） |
| `ModDetailsPageView.xaml.cs` | ~70 行接口实现 + 24 行叠层 XAML | ~28 行 |
| 叠层视觉份数 | 5 份、2 种不透明度、3 组动画参数、2 种字号 | 1 份 |

#### 还剩

- **P1-6 拖放协调器合并**：`ModRelationshipDragCoordinator`（N 目标 + 每目标 `CanAccept`）与两个 profile 单例协调器（`WeakReference<ModListPanel>`，同页两个列表都声明时后者覆盖前者，且只看一个布尔就接受）的架构分叉。这需要先决定"外部拖放要不要支持多目标 + 每目标接受判定"，属于行为扩展，不是纯整理。

---

## 13. 浮动操作簇（汉堡菜单）盘点（2026-10-29）

**性质：** 只做盘点，不改代码。
**已知优点（保留）：** 收纳逻辑（集合变化 → 重算几何 → 逐项位移）、hover 出 label rail、`ExpandedContent` 支持内嵌控件（切换配置的 ComboBox、重命名的 TextBox）——这三件事是对的，不要动。

快照场景：`cluster` / `cluster-expanded`（`tools/UiSnapshot/ClusterScenario.cs`，把 `ModListPanel` 垫在下面好看出遮挡）。

### 13.1 契约：逻辑层仍在选外观，而且一半参数是死的

`PageActionViewModel` 有 12 个构造参数，其中 **4 个没有任何消费者**：

| 参数 | 状态 |
|---|---|
| `Icon` | 死。XAML 按 `Kind` 的 DataTrigger 选 12 个内联 `Path`。逻辑层传的 `"＋" "□" "↻" "▶" "■" "✎" "🗑" "⇄" "✓"` 全被丢弃 |
| `Group` | 死。没有任何读取 |
| `IsVisible` | 死。`Actions` 直接当 ItemsSource，面板不按它过滤 |
| `MainContent`（控件 DP） | 死。FAB 的图标写死在 XAML 里的 `Path` |

还活着但方向错的三个：

- `Background` / `Foreground`（`Brush`）→ 6 个动作给了 5 种颜色（蓝/绿/灰/蓝/红），其中 `30,99,214` 与控件默认值**完全重复**。
- `ExpandedWidth`（`double`）→ 布局尺寸写在逻辑层；而且 `GetRequiredExpandedWidth()` 取**最大值**，所以一个宽的内嵌编辑器会把所有 rail 一起拉宽。
- `Kind`（`string`）→ XAML 里 15 个 `DataTrigger` 映射到 12 个几何。加一个动作要改 XAML + 字符串。

另有一处反向依赖：控件 ctor 里 `Application.Current.MainWindow.DataContext as ShellViewModel`（为了 `PreviewMouseDown` 清临时选择）。这就是它无法被别的宿主复用的根因。

### 13.2 托管：只有壳能挂，页面只能"上交"

- 整个仓库只有 `MainWindow.xaml` 托管它，而且是 **3 个实例**：左槽左对齐、左槽右对齐（全屏时）、右槽右对齐，配 3 个可见性布尔（`ShowSplitLeftSlotActions` / `ShowFullScreenActions` / `ShowRightSlotActions`）+ `RaiseActionFlags()` 里 7 行 `OnPropertyChanged`。
- 页面侧唯一的入口是 `PageViewModel.PageActions`（`IPageActionProvider`），也就是"把动作交到角落"。只有 3 个页面填了：配置 6 个、库 4 个、护甲映射 3 个。其余页面空集合 → 簇隐藏。
- **容量没有上限**：`ClusterHeight = n × (48 + 8) + 48`。6 个 = 384px 还行；详情页那 14 个 = 832px，比窗口还高。这是"详情页动作卡片迁进汉堡"的硬前提。
- **动作执行后不自动收起**：动作 Button 只绑 `Command`，没有 collapse；`IsExpanded` 只在点 FAB 时翻转。
- **集合增删没有逐项动画**：`AnimateActionItems` 按索引遍历已实例化的 `ContentPresenter`，启动回调被推迟到 `DispatcherPriority.Loaded`（等布局），所以只有"整体位移"，新加的项要等下一次开关才会归位。

### 13.3 视觉：同一控件两种深度语言

- FAB **没有阴影**（`ClusterRoundButton` 的模板只有一个 `Border`），而展开后的 label rail **有** `ElevatedPanelShadow`。同一控件里一半有阴影一半没有。
- 展开态是 N 个**独立圆片**，没有容器、没有描边、没有分组，只有 hover 单个时才出 label——所以"这是一张菜单"的整体感只在悬停时出现。
- 6 个高饱和色里 2 个与 FAB 同色（`30,99,214`），**入口按钮因此不像入口**；灰色那个（停用）看着像禁用态。
- 圆片直径 48、间距 8，落在浅色列表上没有边界，只能靠颜色本身。

### 13.4 遮挡：没有避让能力

- 位置固定 `Margin="18"`，没有 offset DP，也没有"避让"概念。
- 实际遮挡（见快照）：最后一行的尾部信息被切（"体积 4…"、"低配可用"）、列表滚动条被压在按钮下面。
- 页面完全不知道它的存在，没有"安全区"。

### 13.5 建议

**P0（低风险，立刻见效）**

1. **统一深度**：FAB 与圆片都加 `ElevatedPanelShadow`（或一个更轻的 floating token），FAB 再加 1px 内描边，保证它压在同类色内容上也有边界。
2. **配色收敛成语义色调**：去掉 `PageActionViewModel.Background/Foreground`，改 `PageActionTone { Primary, Neutral, Danger }`，由控件层映射（Accent / SurfaceElevated+TextPrimary / StatusDanger）。6 个动作变成"1 蓝 + 4 中性 + 1 红"，和行内动作 pill 的"中性 + 危险色"一致。
3. **动作执行后自动收起**。
4. **删死参数**：`Icon`、`IsVisible`、`MainContent`（`Group` 留着做分组）。

**P1（能力）**

5. `PageActionKind` 枚举 + `ActionIcon.{Kind}` 几何资源，和 `RowIcon.*` / `NavIcon.*` / `EmptyIcon.*` 同一套约定；XAML 那 15 个 `DataTrigger` 全部删掉。
6. **任意页面可托管**：ctor 里的 Shell 依赖换成 `TransientSelectionRequested` 事件（由壳订阅），簇变成可复用的纯控件。
7. **容量**：用 `Group` 分组；再配一个"超出 N 个折叠成组"或"展开态改成卡片式菜单（分组标题 + icon+label 行 + 可滚动）"。后者一次解决容量、深度、整体感，但要改交互形态。
8. **避让**：静态 `ClusterOffset` DP（宿主可以推开）+ 可滚动内容的"底部右侧安全区"（`ModListPanel` 加 `ReserveFloatingActionSpace` → 底部内边距 = 簇足迹）；动态可选"空闲半透明、靠近实心"。

**P2（ModDetails 迁移）**

9. 详情页 14 个动作迁进汉堡，按 `Group` 分组（编辑 / Patch / 装饰 / 头盔合并 / 自定义素体）。**前提是先做 7。**

### 13.6 需要拍板

1. **展开形态**：保持"N 个圆片"（现在觉得舒服的那套），还是改成"卡片式菜单"？前者装不下 14 个。
2. **配色**：接受"主 / 中性 / 危险"三档，还是每个动作保留自己的颜色？
3. **避让**：给内容留安全区（按钮不动），还是让按钮动（展开/悬停时避让）？

**（2026-10-29 拍板：① 保持 N 个圆片，但圆片之间可以挤压——鼠标进入某个比例区域就把那一格放大、其余挤压；② 三档语义色调；③ 主按钮可以上下拖动，展开时按钮吸到底部、列向上弹，收起后回到拖到的位置。列高按宿主可用高度自适应，只有顶栏不参与。）**

### 13.7 已落地（2026-10-29）

#### 契约

`PageActionViewModel` 从 12 个参数收到 8 个，而且每个都有消费者：

```csharp
new PageActionViewModel(PageActionKind.Import, "导入 Mod", ImportCommand,
                        tone: PageActionTone.Primary, order: 20)
```

| 之前 | 之后 |
|---|---|
| `Icon`（object，死） | `PageActionKind` 枚举 → `ActionIcon.{Key}` 几何资源（与 `RowIcon.*` / `NavIcon.*` / `EmptyIcon.*` 同一套约定） |
| `Background` / `Foreground`（`Brush`） | `PageActionTone { Neutral, Primary, Danger }`，控件层映射到 `Accent` / `SurfaceElevated+TextPrimary` / `StatusDanger` |
| `Group`（死） | 删掉 |
| `IsVisible`（死） | **实现**：视图按它过滤，宿主可以一次注册全部动作、按页面状态切可见性 |
| `Order`（死） | **实现**：视图按它排序 |
| `MainContent`（控件 DP，死） | 删掉，主按钮图标固定为 `ActionIcon.Menu` |

顺带**去掉了控件对壳的反向依赖**（ctor 里原来 `Application.Current.MainWindow.DataContext as ShellViewModel`）。副作用：点击浮动动作不再清临时选择——动作本身要么不依赖选择，要么自己处理。

#### 视觉

- 主按钮和动作胶囊共用一套模板：`ElevatedPanelShadow` + 1px 描边；圆角由高度算出（`CapsuleCornerRadiusConverter`），所以挤压后是干净的胶囊而不是圆角对不上的方块。
- 6 个动作从"蓝蓝绿灰蓝红"变成"2 蓝 + 3 白 + 1 红"。

#### 挤压与聚焦

**模型：圆心排布，不是高度伸缩。** 控制器只算"圆心位置"，按钮怎么画与它无关：

- 一条从主按钮圆心向上的直线，长度 = 可用高度 − 按钮直径；所有动作圆心都在这条线上。
- 第一个动作固定离主按钮一个标准间距 `a = 直径 + 8`，之后按间距累加。
- 装得下（`n × a ≤ 线长`）就用 `a`，互不重叠；装不下就整体缩到 `a-`，按顺序重叠。
- 鼠标靠近某个圆心时，**它两侧的两个间距临时扩回 `a`**，其余间距压到 `a--`，总长仍不超线。
- 判定用"没有焦点时的均匀圆心位置"算最近圆心，而不是当前实际位置——否则焦点改变间距后会把自己推走、来回跳。
- **层级：主按钮最高且永远显示，离它越近的越高，最上面那个在最下层**（`ZIndex = count - i`，主按钮 1000）。
- **容器高度恒等于按钮直径**，圆片永远不会被裁——重叠完全由圆心间距实现。
- **抽屉宽度按文字自适应**：`MinWidth = ExpandedWidth`（168，也就是原来的宽度），`MaxWidth = 3 × ExpandedWidth`（504），中间按文字长度自然伸展，超出上限用省略号。容器宽度按上限算（520），否则抽屉会被容器的可用宽度夹住、长不出文字来。
- **凸显焦点不挪位置。** 焦点项只做三件事：胶囊弹出、阴影加重（`ActionFocusShadow`）、临时置顶（`ZIndex = 999`，仍在主按钮之下）去压住邻居。位置完全由数量和可用高度决定，跟悬停无关。
- 这一条是最后才收敛的：先后试过"压缩占位高度""按比例分区重排间距""防抖 + 锚点"，都会带来漂移、抖动、边界越界（最下层被挤走、最上层错位）或额外复杂度。**用布局表达层级是错的方向，层级应该由层级表达。**

#### 踩过的三个坑（都关于"看起来该生效却没生效"）

1. **把"挤压"实现成压缩容器高度**，圆片就被容器裁成了鼓形。正确做法是容器恒高、只改圆心间距。
2. **阴影只给焦点项。** 每片都带投影时，上面那个的投影落在下面那个身上，叠出一片柔和过渡，两个圆看起来融成一个（"煎蛋"）。非焦点项靠 1px 描边读边界。
3. **`ApplyLayout` 每次都给所有项重启动画**（标签、位移、高度），鼠标每跨一格就是十几份动画重启，前一个胶囊的收回被打断。现在三处都加了"值没变就不动"的守卫。

#### 拖动与吸附

- 主按钮可上下拖动（横向固定贴边），偏移夹在"按钮不出父容器"范围内；拖动超过 4px 判定为拖拽并吞掉随后的 Click。
- 展开时按钮吸附到底部、列向上弹；收起后回到拖到的位置。
- 动作执行后自动收起。

#### 验证

快照：`cluster` / `cluster-expanded` / `cluster-many`（14 个动作）/ `cluster-many-focus`（第 4 个被键盘焦点放大、其余挤压、标签滑出）。

真机（`artifacts/ui-audit/verify-cluster.ps1`）：导航到库页 → 点开（4 个动作）→ 收起 → 上拖 260px（按钮跟手）→ 再点开（按钮吸附回底部、列向上弹）。全部符合预期。

#### 两个坑

1. **底部那个动作被主按钮盖住。** 我把偏移从 0 起算，而旧公式是 `(count - index) × (直径 + 间距)`——最下面那个必须正好在主按钮上方一格。快照里 6 个动作只显示 5 个才发现的。
2. **在 `PreviewMouseLeftButtonUp` 里 `ReleaseMouseCapture()` 会把 Click 一起吃掉。** `ButtonBase.OnMouseLeftButtonUp` 依赖"仍持有捕获"才抛 Click，所以拖动结束时只清状态、不释放捕获。

#### 真机反馈后的两处调整（同日）

1. **展开即归位，收起不再回原位。** 原来收起时会先把按钮挪回"拖到的位置"再收起，看起来是"先上移再收起"的两段动作。现在展开时就把 `_restingOffset` 清零，收起时完全没有位移——按钮一旦归位就留在底部，拖动只影响"下次展开之前"的位置。
2. **收起时动作列整体 `Opacity = 0`。** 收起的胶囊全叠在主按钮背后，每个都带 `ElevatedPanelShadow`，不透明时阴影会累加成主按钮背后的一团黑影。现在展开时 160ms 淡入、收起时 150ms 淡出（`Opacity=0` 时整棵子树连同阴影都不参与渲染）。

#### 还剩：详情页动作卡片迁移

> ⛔ 已过时（2026-11-08）：下面这三步同日就做完了，动作卡片已整张删除；现行形态见 §13.8。

详情页那 14–16 个动作迁进汉堡的前提已经具备（挤压能装下），但还需要三步：

1. `PageActionViewModel.Label` 改成可变（"关闭头盔合并 / 头盔合并到盔甲并输出"是同一个动作的两种文案）。
2. 补 10 个左右的 `PageActionKind` + `ActionIcon.*`（Patch 修复、跨护甲、规整、重打包、装饰计划、批量装饰、头盔合并、自定义素体、详情表格、改图/改名/改备注）。
3. 三个 `Click` 事件后置处理器（改图像 / 改名称 / 改备注）改成命令，才能进集合。

另外详情页的动作可见性有 4 种条件（`CanUsePatchTools` / `ShowPatchSettingsCard` / `IsDecoration` / `CanShowDecorationPlan`），正好用刚实现的 `IsVisible` 表达——不需要宿主增删集合。

### 13.8 详情页动作已迁入汉堡（2026-10-29）

详情页那张"详情表格 + 14 个按钮"的卡片整个删掉了：`CardKind.ModActions`、`CardTemplate.ModActions`、`ModDetailsCardLayout.ActionsCardId` 全部移除，动作改为在 `ModDetailsPageViewModel.Actions.cs` 里声明一次（16 个），可见性由 `IsVisible` 按 4 个条件同步。

#### 三处前置改造

1. **`PageActionViewModel.Label` 改成可变**——"临时将头盔合并到盔甲 / 取消将头盔合并到盔甲"是同一个动作的两种文案。
2. **补 15 个 `PageActionKind` + 11 个 `ActionIcon.*` 几何**（表格、图片、备注、修复、规整、打包、衣架、衣架+、头盔、合并、素体）。
3. **三个 `Click` 后置处理器改成命令**：改图像（文件对话框）、改名称、改备注（底栏编辑器）都改成"VM 发事件、页面接住"——逻辑层不碰对话框，页面也不碰动作声明。

#### 验证

- 快照 `cluster-many`（14 个动作，重叠态）/ `cluster-many-focus`（焦点项置顶 + 标签滑出）。
- 真机：库页右键任意 Mod 打开详情 → 动作卡片消失、右侧汉堡出现 14 个动作（另 2 个按 `IsDecoration` 隐藏）→ 悬停任意圆片出标签。`artifacts/ui-audit/verify-details-actions.ps1`。

#### 一个必须记住的坑

**重叠布局里没有空隙，任何点击都会真的执行动作。** 我用"点击"来模拟悬停验证时，误触发了真实的头盔合并（该操作可逆，已用同一开关取消）。脚本里验证悬停只能 `SetCursorPos`，绝不能带 mouse down/up。

#### 另外两个 WPF 坑（标签的弹出/收回动画）

1. **`DataTrigger.Binding` 里的 `RelativeSource` / `ElementName` 解析不可靠**——用它做"焦点项显示标签"时触发器根本不触发，标签完全不出现。同一条 `ElementName=Root` 在 `Setter.Value` 的绑定里却是好的，所以不能凭"别处能用"推断。
2. **Storyboard 直接 `SetTarget` 到 `ScaleTransform` 这种游离的 Freezable 上，动画静默失效**——表现为"透明度动了、缩放一直停在初始值"，字被压扁。缩放必须走元素的属性路径：`(UIElement.RenderTransform).(ScaleTransform.ScaleX)`。

最终做法：代码里按焦点项构造 `Storyboard`（`HoldEnd`，后一次 `Begin` 直接替换前一次），目标写成元素 + 属性路径。这样连续跨项不会留下半透明残影，终值也一定落定。

#### 真机反馈后的修正（同日）

1. **重叠的圆片糊成"煎蛋"** → 阴影只给焦点项（见上）。
2. **快速掠过时前一个胶囊的收回被打断** → 三处动画加"值没变就不动"的守卫（见上）。
3. **圆片被裁成鼓形** → 容器恒高、只改圆心间距（见上）。
4. **层级方向反了** → 改成"离主按钮越近越高，最上面那个在最下层"，主按钮固定最高。

#### 还剩

- `PageActionViewModel.ExpandedWidth` 仍是逻辑层给的尺寸（内嵌 ComboBox/TextBox 需要），其余尺寸都已收进控件层。
- 三个"多页面槽位"布尔（`ShowSplitLeftSlotActions` / `ShowFullScreenActions` / `ShowRightSlotActions`）还在壳里；动作契约已经与壳解耦，但这三个布尔要等"每个页面自己托管簇"时才消失。






