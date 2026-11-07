# Manager 统一 UI 设计语言落地计划

**制定日期：** 2026-07-19  
**性质：** 仅限视觉、控件和交互一致性的实施基线；不改变业务流程、数据模型、Patch 重建、自动维护或页面路由语义。

## 1. 范围与边界

本计划解决 `HD2ModManager` 中原生 WPF 控件、局部自定义控件和新主题资源并存造成的视觉割裂问题。

### 本计划会做

- 收拢颜色、间距、圆角、描边、阴影、字体层级和动效 token；
- 建立可复用的按钮、输入、枚举、选择、卡片、状态、列表、表格、空/加载状态样式；
- 将既有页面逐批迁移到统一控件语义；
- 统一图标使用、键盘焦点、hover、disabled、selected 和 error 状态；
- 保持所有既有命令、绑定、数据加载、后台任务和业务行为不变。

### 本计划不会做

- 不修改 `HD2ModCore`、`HD2ModAdaptation` 的行为；
- 不修改 Patch 重建、same-key、材质、cross-armor 或 Game Data 的业务算法；
- 不改变输出目录、导入、部署和自动维护的产品语义；
- 不引入新的页面或重写工作区路由；
- 不复制 PCL 或其他参考项目的源码、资源、视觉资产。

### 验收原则

1. 每次只迁移一类视觉语义或一个低风险页面族；
2. 每批迁移后必须通过 Manager Release 构建；
3. 页面行为、命令可用性、绑定方向、数据虚拟化和键盘导航不得倒退；
4. 新资源必须使用语义名称，禁止继续扩散硬编码色值；
5. 系统文件选择器和危险操作最终确认保持原生 Windows 对话框。

---

## 2. 当前问题基线

### 2.1 三套视觉系统并存

当前 UI 同时存在：

1. 应用根资源中的卡片、列表和滚动条样式；
2. `Themes/Controls.xaml` 中已开始试点的输入与按钮样式；
3. `Themes/DataTables.xaml` 中自成一套硬编码色板的数据表样式；
4. `FloatingActionCluster`、顶部导航和局部页面中各自定义的颜色、圆角、阴影、动画；
5. 仍大量存在的原生 `Button`、`TextBox`、`ComboBox`、`CheckBox`。

这会使同级元素呈现不同高度、圆角、焦点色、hover 反馈和禁用态。问题不在于是否“原生”，而在于相同语义没有稳定外观。

### 2.2 主要不和谐区域

| 区域 | 当前问题 | 统一目标 |
|---|---|---|
| 按钮 | 原生矩形按钮、圆角主/次按钮、圆形图标按钮、悬浮按钮共存 | 按语义固定为主、次、危险、静默、图标、页面级浮动动作六类 |
| 输入 | 部分页面使用 `AppTextBox`，工具筛选仍为原生输入框 | 所有文本输入统一高度、圆角、焦点、禁用和错误状态 |
| 枚举/开关 | `ComboBox`、`CheckBox` 基本仍为系统默认样式 | 统一下拉、复选框、开关和筛选 chip 的视觉语义 |
| 卡片 | 普通信息卡片也有 hover 阴影，暗示可点击 | 区分静态卡片、可点击卡片和浮层 |
| 列表 | 紧凑列表、数据表、工具清单的行密度不同 | 列表行与表格行各有固定高度、选中和 hover 规则 |
| 图标 | Emoji、Unicode 文本、`Path` 几何图形混用 | 功能图标统一使用单色矢量，Emoji 仅用于用户内容 |
| 动效 | 悬浮动作簇有动画，其余控件缺少统一反馈 | 统一 hover/focus 与展开节奏，避免装饰性动画 |

---

## 3. 目标设计语言

### 3.1 风格定位

定义为：**冷静、轻量、技术工作台**。

- 以数据可读性、操作优先级和低认知负担为核心；
- 不采用游戏 Launcher 的高装饰、强拟物或霓虹视觉；
- 不依赖 Windows 默认控件外观表达产品层级；
- 以浅冷灰画布、白色工作表面、克制蓝色强调和语义状态色构成；
- 对技术工具页保持高信息密度，但不牺牲筛选与主操作的可扫描性。

### 3.2 表面与层级

| 层级 | Token 类别 | 使用场景 | 交互规则 |
|---|---|---|---|
| Canvas | `SurfaceCanvas` | 应用/页面背景 | 不可交互 |
| Card | `SurfaceCard` | 普通信息分组 | 默认不产生 hover 阴影 |
| Muted | `SurfaceMuted` | 表头、只读区域、次级背景 | 不可承担主要动作 |
| Selected | `SurfaceSelected` | 选中行、当前筛选、激活状态 | 与强调描边共同使用 |
| Elevated | `SurfaceElevated` | 任务抽屉、浮动菜单、临时面板 | 固定轻阴影，不依赖 hover |
| Interactive | `SurfaceInteractiveHover` | 可点击整卡/行 | 仅确实可点击时提供 hover |

### 3.3 颜色 token

所有新样式和迁移后的样式只能引用语义 token，不在页面内新增硬编码颜色。

| 语义 | 推荐 token |
|---|---|
| 主文字 | `TextPrimary` |
| 次文字 | `TextSecondary` |
| 弱提示 | `TextTertiary` |
| 正常描边 | `StrokeSubtle` |
| 强调/焦点描边 | `StrokeFocus` |
| 品牌主色 | `Accent` |
| 品牌 hover | `AccentHover` |
| 成功 | `StatusSuccess` |
| 警告 | `StatusWarning` |
| 危险 | `StatusDanger` |
| 信息 | `StatusInfo` |

### 3.4 尺寸 token

间距只使用：$4, 8, 12, 16, 24$。

圆角仅保留：

| 圆角 | Token | 用途 |
|---:|---|---|
| 6 px | `RadiusControl` | 输入、按钮、行内控件 |
| 10 px | `RadiusItem` | 列表行、chip、小面板 |
| 14 px | `RadiusCard` | 页面卡片、数据表容器、任务抽屉 |

控件高度：

| 控件 | 高度 |
|---|---:|
| 普通输入/按钮/下拉 | 32 px |
| 紧凑工具栏控件 | 28 px |
| 图标按钮 | 28 px |
| 列表行 | 48 px |
| 数据表行 | 28 px |

### 3.5 动效与可访问性

- hover / focus：$120\text{–}160\mathrm{ms}$；
- 面板展开：$180\text{–}220\mathrm{ms}$；
- 仅动画化 `Opacity`、`Transform` 与颜色；
- 静态卡片不得因 hover 产生阴影；
- 所有仅 hover 可见的操作必须可通过键盘 Tab 聚焦；
- 焦点态不得只靠颜色：必须保留清晰焦点描边；
- 禁用态使用透明度与文字/表面色共同表达，不能仅降低对比度到不可读。

---

## 4. 控件语义合同

### 4.1 按钮

| 样式 | 用途 | 限制 |
|---|---|---|
| `AppPrimaryButton` | 推进当前区域的主要动作，如生成、建立、确认 | 每个独立操作区域最多一个 |
| `AppSecondaryButton` | 浏览、打开、刷新、返回、次级确认 | 不与主操作争夺视觉权重 |
| `AppDangerButton` | 删除、停用、不可逆入口 | 默认描边/文字危险色；最终确认才允许实色危险按钮 |
| `AppQuietButton` | 工具栏次级文字操作 | 不用于首次使用的重要动作 |
| `AppIconButton` | 行内快捷操作 | 必须有 `ToolTip` 与键盘焦点 |
| `AppFabButton` | 页面级浮动操作簇 | 不与普通按钮承载同一命令 |

### 4.2 输入与搜索

| 样式 | 用途 | 要求 |
|---|---|---|
| `AppTextBox` | 普通文本输入、只读路径 | 32 px、焦点描边、禁用态 |
| `AppSearchBox` | 所有搜索 | 搜索图标、清除动作、统一 placeholder/提示文字 |
| `AppComboBox` | 枚举选择 | 输入框外观、统一下拉箭头、键盘可用 |
| `AppCompactComboBox` | 数据表工具栏筛选 | 28 px，和 `AppSearchBox` 对齐 |

### 4.3 选择控件

| 样式 | 适用情形 |
|---|---|
| `AppCheckBox` | 多选列表、逐项启用、非设置类布尔选择 |
| `AppToggleSwitch` | 设置页和会影响自动行为的二元配置 |
| `AppFilterChip` | 当前筛选、可组合条件、可清除状态 |
| `AppRadioGroup` | 明确互斥且选项很少的模式选择 |

规则：设置页的自动维护选项统一使用 `AppToggleSwitch`；候选列表、多选目标和“仅异常”等局部选择使用 `AppCheckBox`；筛选条件不得以一排无语义原生 `ComboBox` 堆叠呈现。

### 4.4 容器与状态

| 样式 | 用途 |
|---|---|
| `AppCard` | 静态信息分组；无 hover 阴影 |
| `AppInteractiveCard` | 点击整卡进入详情/操作；有 hover 与焦点态 |
| `AppElevatedPanel` | 任务中心、浮动展开轨道、抽屉 |
| `AppInlineNotice` | 局部提示、警告、错误，带语义色与图标 |
| `AppBadge` | 小型状态标签，不替代主要文字 |
| `AppEmptyState` | 空列表/无结果；标题、解释、下一步动作 |
| `AppLoadingState` | 首次加载；不清空已有数据 |
| `AppErrorState` | 可恢复错误；明确重试或检查下一步 |

### 4.5 数据展示

`DataTableCard`、`DataTableToolbar`、`DataTableGrid`、`DataTableStatePanel` 保留为数据展示语义，但必须改为引用统一 token。

- 表格工具栏使用紧凑输入、下拉和筛选 chip；
- 所有宽表支持横向滚动，不用压缩列文字解决宽度不足；
- `DataGrid` 行高固定为 28 px，列表行固定为 48 px；
- 选中、hover、异常、成功状态必须与全局 `SurfaceSelected` 和状态 token 对齐。

---

## 5. 图标与文本规范

### 图标

- 壳层、导航、列表行操作、任务状态使用单色矢量 `Path` 图标；
- 统一 16 px 或 18 px 基准尺寸，图标按钮使用 28 px 点击热区；
- 不再为功能操作新增 Emoji；
- 现有 Emoji/Unicode 图标逐批替换，不影响命令与 `ToolTip`；
- 文字标签不得只依赖图标表达关键含义。

### 文本

| 层级 | 建议 |
|---|---|
| 页面标题 | 24–26 px，`SemiBold` |
| 卡片标题 | 16–18 px，`SemiBold` |
| 正文 | 默认字号，`TextPrimary` |
| 次级说明 | 12–13 px，`TextSecondary` |
| 辅助/时间/代码说明 | 11–12 px，`TextTertiary` |

禁止通过任意颜色、过多字体尺寸和加粗叠加制造层级。

---

## 6. 分批实施计划

### 批次 UI-0：Token 收敛

**目标：** 建立唯一 token 来源，不迁移页面结构。

1. 在 `Themes/Controls.xaml` 建立完整颜色、间距、圆角、阴影、动效资源；
2. 将应用根资源中现有的卡片、列表、滚动条资源改为引用这些 token；
3. 将 `Themes/DataTables.xaml` 的硬编码色值改为 token；
4. 确定 `AppCard` / `AppInteractiveCard` / `AppElevatedPanel`，移除静态卡片 hover 阴影；
5. 保持资源 key 兼容或提供兼容别名，避免一次性破坏页面。

**验收：** `App.xaml`、`Controls.xaml`、`DataTables.xaml` 不再存在互相竞争的颜色定义；新增页面不需要硬编码基础颜色。

### 批次 UI-1：基础控件族

**目标：** 消除原生输入、枚举与布尔控件造成的最大割裂。

1. 完成 `AppTextBox` 的只读、错误、禁用状态；
2. 新增 `AppSearchBox`，提供搜索图标与可选清除动作；
3. 新增 `AppComboBox` / `AppCompactComboBox`；
4. 新增 `AppCheckBox` 和 `AppToggleSwitch`；
5. 新增 `AppQuietButton` 与 `AppIconButton`；
6. 为所有控件补齐键盘焦点、hover、pressed、disabled 状态。

**试点页面：** 设置页、首页、模组库、配置页。

**验收：** 上述试点页中不再出现无样式的 `TextBox`、`ComboBox`、`CheckBox` 或普通按钮。

### 批次 UI-2：设置与首页样板页

**目标：** 用设置页和首页展示完整设计语言，但不改功能。

1. 设置页自动行为改用 `AppToggleSwitch`；
2. 周期选择改用 `AppComboBox`；
3. 游戏与资产各区块使用统一 `AppCard` 和 `AppInlineNotice`；
4. 首页摘要改为统一状态标签和静态卡片；
5. 仅可点击的首页动作使用 `AppInteractiveCard` 或标准按钮；
6. 所有按钮按主/次/危险语义重新归类。

**验收：** 首页与设置页可作为其他页面复制的视觉样板；相同控件的尺寸、焦点和状态完全一致。

### 批次 UI-3：配置、模组库与动作簇

**目标：** 统一高频管理体验。

1. 配置页和库页使用 `AppSearchBox`；
2. 列表行统一 hover、selected、键盘 focus 与行内操作显现规则；
3. 批量操作条使用主/次/危险按钮语义；
4. `FloatingActionCluster` 改用 token、`AppElevatedPanel`、矢量图标及统一动效；
5. 浮动动作簇展开内容使用 `AppCompactComboBox`、`AppTextBox` 和标准按钮；
6. 替换 Emoji 功能图标，保留明确 `ToolTip`。

**验收：** 库、配置、浮动操作簇不再使用 Emoji 功能图标，且鼠标与键盘均可完整执行操作。

### 批次 UI-4：数据表与技术工具页

**目标：** 统一高信息密度页面外围控件和状态呈现。

1. Game Data 浏览、archive 详情、高级详情统一 `DataTableToolbar`；
2. 所有搜索与筛选控件使用紧凑控件族；
3. Cross-armor 的表单、目标选择、手动映射与预测表使用同一容器/按钮/选择控件语义；
4. 材质页面局部白色 `Border` 改为 `AppCard`；
5. 数据表空、加载、错误状态统一为状态组件；
6. 列状态色改为全局语义 token。

**验收：** 技术工具页不再混用原生表单与数据表主题；相同筛选工具栏的高度、间距与按钮层级一致。

### 批次 UI-5：壳层、任务中心与收尾

**目标：** 统一应用外壳与跨页面反馈。

1. 顶部导航、任务入口、任务抽屉使用统一品牌色、焦点态和矢量图标；
2. 任务状态、消息级别与通知 toast 统一使用 `AppBadge` / `AppInlineNotice`；
3. 统一页面 loading、empty、error 内容；
4. 处理剩余局部硬编码色值、圆角和 padding；
5. 建立 UI 回归检查清单，覆盖高对比度、键盘导航、100% / 150% DPI 和窄窗口。

**验收：** 同类元素不再有不同形状、不同高度、不同焦点态或不同语义色；任务中心与页面内状态反馈属于同一视觉系统。

### 批次 UI-6：基础动效与状态连续性

**目标：** 为已有的状态切换补充低成本、低干扰的连续性反馈；不改动页面路由、数据加载或后台任务行为。

1. 双页面槽位的新页面使用 $200\mathrm{ms}$ 淡入与不超过 $6\mathrm{px}$ 的上移进入；
2. 库页与配置页的行内操作区域使用 $120\mathrm{ms}$ 淡入淡出，保持键盘焦点可达；
3. 已有的搜索标题卡、下拉箭头和浮动操作簇沿用相同节奏，避免重复或叠加装饰动画；
4. 暂不对虚拟化列表的每一行、数据表刷新、普通按钮 hover 或输入焦点添加进入动画；
5. 任务抽屉、通知与选择操作条仅在其状态生命周期稳定后，再按单独小批次处理退出动画。

**验收：** 页面与高频操作的出现不再突兀；动画仅影响 `Opacity` 与 `Transform`，不改变布局尺寸、不影响键盘导航，且不引入列表滚动或页面导航性能退化。

---

## 7. 页面迁移清单

| 页面/组件 | 主要迁移批次 | 重点 |
|---|---|---|
| `App.xaml` | UI-0 | token、全局滚动条、兼容资源 |
| `Themes/Controls.xaml` | UI-0 / UI-1 | 完整基础控件族 |
| `Themes/DataTables.xaml` | UI-0 / UI-4 | token 化数据表 |
| `HomePageView` | UI-2 | 健康摘要、状态标签、操作层级 |
| `SettingsPageView` | UI-1 / UI-2 | 开关、枚举、表单、状态提示 |
| `LibraryPageView` | UI-1 / UI-3 | 搜索、列表行、图标操作 |
| `ProfilePageView` | UI-1 / UI-3 | 搜索、列表行、动作簇协作 |
| `FloatingActionCluster` | UI-3 | 矢量图标、展开面板、焦点可达 |
| `GameDataBrowserPageView` | UI-4 | 紧凑工具栏、数据表状态 |
| `GameDataArchiveDetailsPageView` | UI-4 | 紧凑工具栏、数据表状态 |
| `AdvancedModDetailsPageView` | UI-4 | 工具栏、操作按钮、诊断状态 |
| `CrossArmorPlanPageView` | UI-4 | 表单、选择、表格和动作统一 |
| `MaterialPackagingPageView` | UI-4 | 容器卡片、候选列表、输出区 |
| `MainWindow` / 任务抽屉 | UI-5 | 顶部壳层、任务状态、通知 |
| 双页面槽位与高频列表操作 | UI-6 | 页面进入、行内操作显隐的连续性 |

---

## 8. UI 回归检查清单

每批完成后至少检查：

- [ ] Manager Release 构建成功；
- [ ] 所有迁移控件的鼠标 hover、pressed、disabled、focus 状态可辨识；
- [ ] Tab 顺序合理，键盘可以抵达关键操作；
- [ ] 100% 与 150% DPI 下文字、图标、按钮不裁切；
- [ ] 窄窗口下工具栏可换行或横向滚动，不重叠；
- [ ] 静态卡片不产生可点击暗示；
- [ ] 危险操作始终有文字语义，不能只显示图标；
- [ ] 搜索、筛选、列表和数据表的高频操作未引入性能退化；
- [ ] 不新增业务逻辑、数据持久化或 Patch 重建改动；
- [ ] 页面内不新增非语义硬编码基础色、圆角或间距。

---

## 9. 实施决策记录

- 采用 WPF 资源字典与样式，不为了视觉统一引入第三方 UI 框架；
- 保留系统文件/目录选择器和最终危险确认对话框；
- 不复制参考项目的代码、资源或视觉资产，仅借鉴“token → 基础控件 → 页面编排”的架构方法；
- 页面迁移优先改控件样式和容器语义，避免在 UI 批次顺带修改 ViewModel 或服务逻辑；
- 每次样式改动先在样板页验证，再扩展到同类页面；
- 如果视觉方案与可访问性、性能或现有 WPF 行为冲突，以可访问性、稳定性和业务正确性为先。
