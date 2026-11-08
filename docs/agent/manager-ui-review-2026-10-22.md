# HD2ModManager UI 审查

**日期：** 2026-10-22
**范围：** `HD2ModManager` 的壳层、页面、共享控件与主题资源；不含业务逻辑与 Patch 链路。
**基线：** `docs/agent/manager-ux-redesign-plan.md`（2026-07-18）、`docs/agent/manager-ui-design-language-implementation-plan.md`（2026-07-19）。

> **关于这两份基线文档的时效性：** 它们是**历史需求**，不等于当前验收标准。已经确认过时的一条：首页当前形态（说明书 + 健康摘要 + 启动游戏）**是作者预期的样子**，文档里"首页只保留精简健康摘要"的目标不再成立。本文凡引用文档得出的"应该怎样"，都只作为**待确认的假设**，不直接当作缺陷；改动前需逐条确认。

---

## 0. 结论先行

1. **这个项目的 UI 不是"没有设计"，而是"设计只落地了一半"。** 两份基线文档已经把 token、控件合同、动效节奏、验收清单写得很完整，`Themes/Controls.xaml` 里的控件族也基本建齐；真正缺的是**页面层对这套语言的采用率**，以及**把 modlist 卡片那套"行"语法推广到非列表页面**。
2. **全项目最成熟的一处确实是 modlist 卡片。** 它是唯一一个把「信息层级 / 状态语义 / 操作密度 / 虚拟化性能 / 键盘与拖放」五件事同时做对，并且被 8 个页面、7 种动作组合反复验证过的组件。它不是"好看的卡片"，而是一个**契约清晰的列表渲染器**。
3. **最值得先修的是"状态语义"，不是版式。** 离屏渲染证实：悬停与选中在视觉上几乎无法区分，而空状态是一张几乎全白的卡片角落上一行 3.5:1 的灰字。这两处不需要重新设计，只需要把已有的 token 用对。

---

## 1. 审查方法

- **静态：** 30 个 XAML、2 套主题字典、`ModListPanel` 及其 4 个协作控制器（Transition / State / Projection / Drag）的代码后置。
- **运行时：** 构建并启动应用（`net10.0-windows`，33 个 Mod 的真实库），用 UI Automation 读取实时控件树、文本与边界。
- **量化：** 字面色值、字面间距、token 采用率、可访问性标注、WCAG 对比度计算。
- **限制：** 本机图形会话在启动首帧之后不再合成新帧（截图与 `PrintWindow` 都只能拿到首帧，鼠标点击虽被 UIA 树确认生效，但画面不更新）。因此本文的视觉判断以 **XAML 几何 + UIA 控件树 + 首帧截图** 为准；阴影观感、字重渲染这类纯像素问题未做确认，凡涉及视觉观感的结论都标注为"待渲染验证"。

---

## 2. 六个维度

### 2.1 设计系统：颜色收敛得不错，间距和排版没有

| 维度 | 现状 | 数据 |
|---|---|---|
| 颜色 | 语义色板完整，数据表已 100% 引用 token | `Controls.xaml:4-21` 共 21 个 token；`DataTables.xaml` 无字面色值 |
| 颜色（视图层） | 仍有漂移 | 10 个视图文件里 **30 处字面色值 / 20 个不同值**，集中在 ModDetails(9)、MainWindow(5)、ModListPanel(4)、CrossArmor(4) |
| 间距 | token 形同虚设 | `Space4/8/12/16/24` 各只被引用 **1 次**；视图里有 **385 处字面 `Margin`/`Padding`** |
| 圆角 | 有 token 但被绕过 | `RadiusControl/Item/Card` = 6/10/14，行内药丸用字面 `16`、导航胶囊用 `16`、详情缩略图用字面 `14` |
| 排版 | 没有阶梯 | 全项目只有 3 处 `FontFamily`；没有字号/行高 token；未显式设置字号的正文回落到 WPF 默认 12px |

**判断：颜色是第一公民，间距和排版是二等公民。** 这正是设计语言计划 2.1 节自己指出的问题——"同类元素呈现不同高度、圆角、焦点色"——而 UI-0 批次只完成了颜色那一半。

### 2.2 版式与信息架构

壳层结构：48px 标题栏 + 居中胶囊导航（首页 / 配置页·模组库 / 护甲映射 / 设置 / 消息）+ 双槽工作区 + 卡片堆（`MainWindow.xaml:173-336`）。

**问题：**

1. **首页形态与旧文档不一致（已确认：现状是预期，文档过时）。** 旧计划写"首页只保留精简健康摘要"，实际 `HomePageView.xaml:49-114` 是一份 219 行、12 个小节、约 1800 字的说明书，占据 `1*`（约 65% 宽度）；健康摘要被挤进 `0.54*` 的右栏，带 `Margin="30,0,30,40"`（`:117-120`）。**本条不作为缺陷，仅记录文档与实现的差异。**
2. **顶部入口比计划多一个。** 计划是 4 个顶级入口 + 浮动任务入口；现在多了"护甲映射"这个重工具页常驻顶栏。
3. **三合一胶囊是自创控件，可发现性靠 ToolTip。** 左=配置页、中=双页、右=库，三个热区共用一个胶囊（`CapsuleButtonGroup.xaml`）。实测 UIA Name：中键 = "配置页 | 模组库"，左右键 = **空字符串**。
   → 2026-10-24 澄清：左右两个圆点**是刻意的**，语义由中间的组合按钮承担，不算缺陷。残留一点：读屏用户 Tab 到圆点时听不到内容。若要补，最小改法是把已有 ToolTip（"只打开配置页" / "只打开模组库"）同时设成 `AutomationProperties.Name`，视觉零变化。
   → 另：胶囊以外的顶栏按钮 2026-10-23 已改为声明式，读屏名全部可用（首页 / 护甲映射 / 设置）。
4. **最小窗口对 150% DPI 不友好。** `MinWidth=1200 / MinHeight=720`（`MainWindow.xaml:14`）。1920×1080@150% 的逻辑分辨率只有 1280×720，最小高度正好等于可用高度，任务栏一压就出不来。

### 2.3 密度与层级

- **modlist：** 50px 节距（48 行高 + 上下各 1），1200×720 下一屏 11–12 行，33 个 Mod 滚动一次即到底。密度是对的。
- **设置页：** 150px 标签列 + 输入 + 3 个同权按钮（浏览/打开/重置），单行 4 个控件；整页 45 处字面间距，**没有主次**——所有按钮都是 `AppSecondaryButton`（`SettingsPageView.xaml:24-37`）。
- **Mod 详情：** 5 行右对齐按钮簇（`ModDetailsPageView.xaml:66-94`），最多 13 个同款 Secondary 按钮平铺；"修复 patch / 生成替换 / 规整 patch 包 / 资源重打包"四个动词并列，用户无法判断该点哪个。**这是全项目最该重构的一页。**
  > ⛔ 已过时（2026-11-08）：那张动作卡已于 2026-10-29 整张删除（`CardKind.ModActions` 已不存在），详情页动作改为汉堡（浮动操作簇）里的声明式动作项（今天 15 项，逐条见 `HD2ModManager/ViewModels/ModDetailsPageViewModel.Actions.cs`）。
- **首页：** 说明书无目录、无锚点、无搜索，且被截断在卡片底部。

### 2.4 交互与反馈

**做得好的（这部分水准很高）：**

- 搜索态与标题互换：标题/摘要淡出 140ms，搜索框与筛选动作淡入 160ms（`ModListPanel.xaml.cs:191-231`）。
- 选中指示条：4px 宽，`ScaleY` 200ms `CubicEase` 生长，取消时 120ms 收回（`:982-1011`）。
- 行内操作药丸：120ms 淡入，`IsHitTestVisible` 同步切换（`ModListPanel.xaml:306-327`）。
- 批量筛选用"双层真实 ListBox"交叉淡化，而不是位图快照（`ModListPanel.xaml:356-389`）。
- 底栏多行系统：预测量、尺寸预发布、行位移动画 180ms、离场 120ms（`BottomBarSurface.xaml.cs:30-101`）。
- 平滑滚轮状态机（`SmoothScrollBehavior`）+ UI 卡顿诊断（`UiPerformanceDiagnostics`）。**在 WPF 应用里这种自觉很少见。**

**做得不够的：**

1. **hover 与 selected 曾经几乎同色（已修，2026-10-22）。** 原来 `SurfaceInteractiveHover #EEF4FF` vs `SurfaceSelected #EAF1FF`（RGB 各差 3–4），而且两者都改用 `StrokeFocus` 蓝色描边（`ModListPanel.xaml:301-331`）。静止态行是纯白无分隔线，于是"悬停 / 选中 / 悬停+选中"三态只能靠 4px 指示条区分。
   → 现已拆出 `SurfaceRowHover #F7F9FC` + `StrokeRowHover #DFE5EE`，悬停回归中性；选中保留蓝底 + 蓝描边 + 4px 条。改动落在 `Controls.xaml`（新增 2 个 token）与 `ModListPanel.xaml`（1 个 Trigger）。
2. **药丸遮挡元信息。** 操作药丸与内容同处 `Grid.Column="2"`，右对齐（`ModListPanel.xaml:184`），悬停时正好盖住 summary / note——想看清再点，偏偏看不到了。
3. **不可见按钮仍在 Tab 序列。** 药丸 `Opacity=0 + IsHitTestVisible=False`，但内部按钮没有移出 Tab 序列。键盘 Tab 会走进完全看不见的按钮，直接违反设计语言计划 3.5 的"所有仅 hover 可见的操作必须可通过键盘聚焦"（本意是可达，不是可瞎跳）。
4. **缓存页仍在 Tab 序列。** `LeftPreviousPageHost` / `RightPreviousPageHost` 只设了 `IsHitTestVisible="False"`（`MainWindow.xaml:315,326`）。实测切到双页后，Tab 第一次落点就是首页说明书里的 B 站超链接。
5. **行没有焦点框。** `ListBoxItem` 的 `FocusVisualStyle="{x:Null}"`（`App.xaml:136`），键盘选中只靠背景色，不满足"焦点态不得只靠颜色"。

### 2.5 可访问性 / 键盘 / 国际化

| 指标 | 现状 | 评价 |
|---|---|---|
| `AutomationProperties` | 全项目 **5 处** | 图标按钮基本靠 ToolTip，读屏无名称 |
| `FocusVisualStyle` | 全项目 **2 处** | 键盘焦点不可见 |
| 导航按钮 UIA Name | `HomeNavigationButton` = `""`（2026-10-23 已修：顶栏按钮现在都有 `AutomationProperties.Name`；胶囊左右分区是**刻意留空**，语义由中键承担） | 读屏无名称 |
| 中文字符串 | XAML 里 **1653 段**硬编码 | `res:Strings` 只被引用 **1 次**，两个 resx 形同虚设 |
| 对比度 | `TextSecondary` 5.38:1 ✓；`TextTertiary` **3.78:1 ✗**；空列表 `#888` **3.54:1 ✗**；状态色 ≥4.9 ✓；白字/Accent 5.51 ✓ | 12px 正文用 Tertiary 不达标 |

### 2.6 性能与渲染

- 列表：`VirtualizingStackPanel` + `Recycling` + 像素滚动 + 一页缓存（`App.xaml:108-149`）——正确。
- 启动预热：真实但不可见的宿主（`Opacity=0` 而非 `Collapsed`），保证测量与虚拟化生效（`MainWindow.xaml:284-305`）。
- 双层过渡避免位图快照；拖拽边缘滚动 32ms 采样，不与渲染抢帧（`ModListPanel.xaml.cs:22-25`）。
- 行内药丸的 `DropShadowEffect` 同时最多存在一个，成本可控。
- 风险点：`ElevatedPanelShadow` 用在消息中心、底栏、浮动簇多处；`Effect` 走软件渲染路径，若将来叠加在滚动容器上会掉帧。目前用法克制，但值得写进规范。

### 2.7 一致性债务清单（可直接排期）

1. **死代码：** `LibraryPageView.xaml:6-20` 与 `ProfilePageView.xaml:6-20` 各定义 5 个图标 `DataTemplate`，实际模板已内联，从未被引用。
   > ⛔ 已过时（2026-11-08）：这 10 个死模板已于 2026-10-29 删除（两处各只剩一个 `*SearchActions` 模板）。
2. **标题栏按钮自成一派：** `CaptionButtonGreyHover/RedHover` 用 `#D9D9D9 / #BFBFBF / #E53935 / #B71C1C`，与语义色板无关（`MainWindow.xaml:124-165`）。
3. **散落色值：** `ModListPanel` 的空态 `#888`、药丸阴影 `#243042`、外部拖放遮罩 `#D9FFFFFF`；`ModDetails` 的 `#444/#555/#EEF1F6/#1E63D6`；`CrossArmor` 的 `#16803A/#4B5563/#9CA3AF`；`CapsuleButtonGroup` 的 `#333`。
4. **文案：** 首页的 `o'v'o`、"Mod 太 tm 多了"、"本次运行的消息"；中英标点混排。
5. **嵌套卡片缺少规则：** 详情页把 `ModListPanel`（本身是两张卡片）嵌进卡片堆，出现"卡中卡"，内边距没有统一约定。

---

## 3. 为什么 modlist 卡片是当前最好的一处

### 3.1 它真正解决的问题

不是"好不好看"，而是：**在 33 个（未来可能 300 个）Mod 里，快速找到一个、判断它的状态、并对它做 15 种操作之一，同时保持 60fps 和可拖放排序。**

这个列表要同时承受：数量多、每行信息维度多（类型/启用/顺序/告警/摘要/备注）、操作多、内部拖拽排序、跨页拖放、批量筛选、虚拟化。

### 3.2 七条原则（每条都能指到代码）

1. **一对象一行，一行一职责。**
   固定 48px（`App.xaml:151-161`）、名称 14px SemiBold 单行省略（`ModListPanel.xaml:127`）、36px 缩略图（`:113-121`）、元信息一行 12px。它是一张**索引卡**，不是仪表盘——把"详情"留给详情页，是它最克制的决定。

2. **视图不认识领域。**
   `ModListRowPresentation` 是不可变 record，只有 6 个可选事实 + 5 种色调（`ModListSelection.cs:33-53`）；由宿主 VM 决定显示什么，面板只负责画。于是同一张卡同时服务 Mod 库、配置、装饰、计划、护甲映射、批量装饰、资源重打包等 **8 个页面、7 种动作组合**，外观天然一致。
   → **一致性由结构保证，而不是靠自觉保证。** 这是它最值得复制的一点。

3. **每个事实都能单独缺席。**
   6 个 `Has*` 布尔 + `Visibility` 绑定（`ModListPanel.xaml:138-181`），不会出现空占位、"未设置"噪声或错位。

4. **操作按上下文裁剪，而不是全部禁用。**
   `RowActions` 是 flags 枚举（`ModListPanel.xaml.cs:98`），宿主只传自己真能做的；每行还能用谓词二次过滤（`IsCustomModel` / `CanJoinProfile` / `IsAttachable`）。所以同一个模板在配置页是 ↑↓✕，在库页是 ＋🗀🗑，**永远没有"看得见点不动"的按钮**。
   > ⛔ 已过时（2026-11-08）：宿主仍传 flags，但行级过滤已改为行对象实现 `IModListRowActionFilter.HiddenActions`（2026-10-29），不再按属性名猜那三个谓词。

5. **静止时安静，需要时密集。**
   操作药丸默认 `Opacity=0`、`IsHitTestVisible=False`，悬停 120ms 淡入（`ModListPanel.xaml:184-188, 306-327`）。静止态一行只有 3 个视觉元素；悬停态才展开成工具条。**信息密度是按意图分层的，不是按空间堆的。**

6. **状态有冗余编码。**
   选中同时改背景、加 4px 强调条、可选勾选框；强调条用 200ms `ScaleY` 生长（`:282-299`）。颜色之外还有形状和位置——单一通道失效时仍可辨认。

7. **性能是设计的一部分。**
   虚拟化 + 回收 + 像素滚动 + 一页缓存；批量筛选用双层真实列表交叉淡化；拖拽自动滚动与渲染解耦。**它没有为了"好看"牺牲滚动**——很多同类项目恰恰死在这一步。

补一条工程上的：**空态、拖放态、过渡态都被当成正式状态设计**——空列表文案（`:339-354`）、拖拽插入指示线（`:391-394`）、外部拖放遮罩带标题与后果说明（"仅从当前配置移除，不会删除模组文件"，`:395-402`）。

### 3.3 它也不是完美的

1. **元信息是 6 个等权 TextBlock 顺序排**（`:129-182`），没有优先级。窄宽度时先牺牲 summary，再牺牲 note，最后连 warning 都可能被挤掉——**告警应该拥有最高保留权**。
2. **hover / selected 视觉几乎重合**（见 2.4）。
3. **药丸遮挡元信息**（见 2.4）。
4. **15 个按钮硬编码在模板里**：加一个动作要同时改 XAML + 枚举 + 3 个 converter。已经到"一模板通吃"的上限，下一步要么抽成 `ItemsControl` 驱动的动作条，要么接受分叉。
   > ⛔ 已过时（2026-11-08）：已按第一条路做完（2026-10-29）——行模板是 `ItemsControl` + 一个 `Button` 模板，加动作 = 枚举 + `ModListRowActionCatalog` 一行 + 一个 `RowIcon.*` 几何。
5. **键盘路径不完整**：不可见按钮仍在 Tab 序列，行没有焦点框。
6. **行高锁死 48**：长名称只能省略，没有"两行名"的例外通道。

### 3.4 把它的语法抽出来：Row Card

建议把这套东西命名并参数化，作为非列表页面的复用单位：

| 属性 | 规范 |
|---|---|
| 节距 | 50（48 + 上下 1） |
| 圆角 / 内边距 | 6 / `6,4` |
| 视觉锚点 | 左 36px（图 / 图标 / 首字母） |
| 标题 | 14px SemiBold，单行省略 |
| 元信息 | 12px，最多 6 个事实，宽度优先级：**告警 > 状态 > 类型 > 顺序 > 摘要 > 备注** |
| 动作区 | 静止隐藏；悬停或键盘聚焦出现；最多 5 个，超出进"更多" |
| 状态 | hover = 中性底色 + `StrokeSubtle`；selected = `SurfaceSelected` + `Accent` 描边 + 4px 条；focus = 独立焦点环 |

首页的健康摘要、设置页的目录行、详情页的动作行，都可以直接用这个单位重排。

---

### 3.5 离屏渲染：这次真的看到了什么

本机图形会话不合成新帧，截屏不可用。为此新增了 `tools/UiSnapshot`：它加载真实的 `App.xaml` 资源、真实的 `ModListPanel`，用 `RenderTargetBitmap` 直接光栅化视觉树，2× 输出 PNG，不依赖 DWM。

```powershell
dotnet build tools\UiSnapshot\UiSnapshot.csproj
tools\UiSnapshot\bin\Debug\net10.0-windows\UiSnapshot.exe modlist-hover --scale 2
```

出图位置：`artifacts/ui-audit/render/`。已支持 `modlist` / `modlist-hover` / `modlist-selected` / `modlist-checkbox` / `modlist-empty`。

**首轮渲染确认的四件事（静态读代码看不出来）：**

1. **悬停与选中确实无法区分。** 两张图并排看：悬停行 = `#EEF4FF` 底 + 蓝描边；选中行 = `#EAF1FF` 底 + 蓝描边 + 左侧 4px 条。除那根 4px 条外，其余完全一致。
2. **悬停药丸压在元信息文字上（不是被裁切——放大 6× 复核过，药丸本身完整）。** 药丸 32px 高、垂直居中，正好覆盖元信息行右段；被盖住的文字不是被截断，而是从药丸下方露出来，看起来像渲染故障。备注（`Note`）右对齐，位置与药丸完全重合，所以悬停时最先"消失"的就是备注。
3. **空状态是一张几乎全白的卡片。** 只有左上角一行 `#888`（3.54:1）的"没有可显示的 Mod。"，没有图标、没有解释、没有下一步。设计语言计划里的 `AppEmptyState` 从未实现。
4. **宽栏下备注会飘到 700px 之外。** 元信息行没有最大宽度，`Note` 右对齐；单页模式（库独占整个窗口）时，"替换 RE-2310 全甲"会跑到窗口最右侧，与名称之间留出巨大空洞，行看起来像表格而不是卡片。

这四条都可在 `artifacts/ui-audit/render/` 的对应 PNG 里直接复现。

## 4. 改进路线

### P0 — 低风险，立刻能感知（建议先做）

1. **三态可分辨**（✅ 已完成 2026-10-22）：hover 改中性 `SurfaceRowHover #F7F9FC` + `StrokeRowHover #DFE5EE` 描边；selected 保留 `#EAF1FF` + `Accent` 描边 + 4px 条。**待办：** 给 `ListBoxItem` 补焦点环（需要键盘态验证手段，暂缓）。
2. **行操作可达性**：药丸在 `IsKeyboardFocusWithin` 时也显示；隐藏时移出 Tab 序列（`KeyboardNavigation.TabNavigation="None"` 或 `IsTabStop=False`）。
3. **告警优先**：元信息改为"告警/状态固定左侧，摘要弹性，备注可整段省略"，并给药丸留出右侧安全区。
4. **色值与对比度**：20 个字面色值改 token；空态文字与 `TextTertiary` 提到 ≥4.5:1。
5. **缓存页移出 Tab**：`LeftPreviousPageHost` / `RightPreviousPageHost` 补 `KeyboardNavigation.TabNavigation="None"`。

### P1 — 结构性，收益最大

6. ~~**首页重排**~~ **（已撤销：首页现状是预期，旧文档要求作废，改动前必须单独确认）**
7. **Mod 详情动作卡重排**：1 个主操作 + 分组次操作（修复 / 转换 / 导出 / 文件），不再 13 个平铺。
   > ⛔ 已过时（2026-11-08）：已以另一种形状做完——动作卡删除，动作迁进汉堡；没有"1 主 + 分组次操作"的卡片。
8. **设置页**：每行一个主操作，"浏览/打开"收进输入框右侧图标按钮；周期选择与开关同行右对齐。
9. **空状态**：补 `AppEmptyState`（图标 + 标题 + 解释 + 下一步动作），先用于库、配置、装饰候选三处。**（2026-10-24 完成：`Controls/EmptyState/`，库页按"库为空 / 搜索无结果 / 全部已加入配置"分三态；只给 `EmptyMessage` 的页面回落到单行文字，颜色从 `#888` 提到 `TextSecondary`）**

### P2 — 系统化

10. **排版阶梯 token**（24/17/14/13/12/11）+ 行高；新代码禁止字面间距。
11. **可访问性**：所有图标按钮补 `AutomationProperties.Name`；导航按钮命名；缓存页与隐藏控件退出 Tab 序列。
12. **国际化**：1653 段中文迁入 resx，或明确放弃并删除假 resx。
13. **窄窗口与 DPI**：`MinWidth` 降到 1024，工具栏换行，150% DPI 回归。

---

## 5. 验收清单

在基线计划 §8 之外，本次审查新增：

- [x] 悬停 / 选中 / 悬停+选中 三态在灰度下仍可区分；（2026-10-22：拆出 `SurfaceRowHover` / `StrokeRowHover`）
- [ ] Tab 不会落在 `Opacity=0` 的控件或已切走的缓存页上；
- [ ] 行内告警文字在任何窗口宽度下都不被截断；
- [x] 空态文字与 `TextTertiary` 达到 4.5:1；（2026-10-24：单行回落改为 `TextSecondary` 5.38:1；富空状态用 `TextPrimary` + `TextSecondary`）
- [x] 悬停药丸完整可见，不被行边界或滚动条裁切；（2026-10-22 复核：药丸本身完整，真问题是它盖住元信息）
- [ ] 单页宽栏下，元信息行的备注不会与名称脱节。

---

## 附录 A：证据索引

| 主题 | 位置 |
|---|---|
| 色板 / 间距 / 圆角 token | `Themes/Controls.xaml:4-31` |
| 卡片 / 列表 / 行 / 图标按钮样式 | `App.xaml:87-191` |
| 行模板（缩略图/名称/元信息/药丸/指示条） | `Views/ModListPanel.xaml:86-333` |
| 行呈现模型（6 事实 + 5 色调） | `ViewModels/ModListSelection.cs:33-53` |
| 库页动作组合 | `Views/LibraryPageView.xaml:30-48` |
| 配置页动作组合 | `Views/ProfilePageView.xaml:25-45` |
| 详情页按钮堆 | `Views/ModDetailsPageView.xaml:52-96` |
| 首页说明书 | `Views/HomePageView.xaml:49-114` |
| 巨型启动按钮 | `Views/HomePageView.xaml:208-216` |
| 设置页表单行 | `Views/SettingsPageView.xaml:24-37` |
| 标题栏与导航 | `MainWindow.xaml:86-275` |
| 胶囊控件 | `Views/CapsuleButtonGroup.xaml:8-71` |
| 底栏多行系统 | `Views/BottomBarSurface.xaml.cs:30-101` |
| 选中条动画 | `Views/ModListPanel.xaml.cs:982-1011` |
| UI 性能诊断 | `Services/UiPerformanceDiagnostics.cs:12-24` |
| 离屏渲染工具 | `tools/UiSnapshot/`（`Program.cs`、`ModListScenario.cs`） |

## 附录 B：本次审查的限制

- 图形会话在首帧后停止合成，截屏与 `PrintWindow` 都拿不到新画面。已用 `tools/UiSnapshot` 的 `RenderTargetBitmap` 路径绕过（见 §3.5），但它渲染的是**静态状态**：悬停态由工具按模板里的 setter 重放，动画中间帧不覆盖。
- 未覆盖运行时动画的实机帧率；性能结论来自代码结构与虚拟化配置。
- 未审查 `HD2ModCli` 与 Core 层；本文只谈 Manager 的 UI。
- 基线文档中的需求可能已经过时（首页已确认），本文所有"应该怎样"在动手前都要再确认一次。
