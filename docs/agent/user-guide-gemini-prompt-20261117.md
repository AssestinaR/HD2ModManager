你在仓库 D:\gitCode\HD2ModManager 里工作（Windows，绝地潜兵 2 Mod 管理器，WPF 项目）。任务：写面向普通用户的 HTML 图文教程。

## 先读
1. `AGENTS.md`（仓库规则，必须遵守）
2. `docs/agent/user-guide-plan-20261117.md`（本任务的规划，内容以它为准；§6 第 1、2 条已定：放 `docs/guide/`，随 Release 分发）

## 规则
- 只在 `docs/guide/` 下新建/修改文件。不要动其它任何文件，不要动 `docs/` 下未跟踪的东西。
- 不要 git commit / push，做完我来看。
- 写到界面上的按钮/开关名时，必须到源码里核对原文，不许凭印象写。主要出处：
  - `HD2ModManager/Views/SettingsPageView.xaml`（设置页：内置 lua、映射模式、低污染模式、游戏目录、Mod 库目录、导入后自动加入活动配置并部署）
  - `HD2ModManager/Views/ArmorRegistryPageView.xaml`、`HD2ModManager/ViewModels/ArmorRegistryPageViewModel.cs`（护甲映射页：启用映射、应用）
  - `HD2ModManager/ViewModels/ModDetailsPageViewModel.Actions.cs`（详情页动作：生成sectional版）
  - `HD2ModManager/ViewModels/CustomModelConversionBottomBarViewModel.cs`（生成sectional版底栏：名称/护甲/头盔/生成）
  - `HD2ModManager/Services/ImportService.cs`、`HD2ModManager/Services/VpkImportService.cs`（导入、VPK 导入）
  - `HD2ModManager/ViewModels/LibraryPageViewModel.cs`（模组库、启用、部署）
  核对时发现规划文档和代码不一致，以代码为准，并在最后的汇报里列出来。

## 技术约束
- 纯静态：HTML + 一个 `assets/guide.css` + 一个 `assets/guide.js`。不要构建步骤、不要框架、**不要任何外部 CDN/字体/网络请求**（要随 zip 离线分发）。
- 必须能直接双击用 `file://` 打开：不要 `type="module"`，不要 fetch 本地 JSON。
- 中文界面，简洁好看，窄窗口也能看（不出横向滚动条）。
- 每篇页面的 `<article>` 带 `id`、`data-verified="1.5.18"`、`data-sources="<分号分隔的依赖源码文件名或通配>"`，格式见规划 §5。id 定了就不再改。

## 交互组件（在 guide.js 里统一实现，页面里只写约定的 HTML 结构）
1. 截图编号标注：
   ```html
   <figure class="shot" data-src="img/t1-01.png">
     <a class="pin" style="left:12%;top:30%" data-step="1">说明文字</a>
   </figure>
   ```
   点编号显示/高亮对应说明。
2. 截图点击放大（全屏遮罩，Esc/点击关闭）。
3. 步骤"上一步/下一步"，同时保留整页滚动阅读。
4. **图片不存在时**显示一个占位框，框里写出期望的文件名（如 `img/t1-01.png`）。截图由用户后补。

截图命名：`t<教程号>-<两位步骤号>.png`。每篇末尾列出本篇需要的全部截图清单（文件名 + 要截什么）。

## 分两步做，第一步做完就停
**第一步**：`docs/guide/index.html`（目录页，三篇入口）+ `assets/guide.css` + `assets/guide.js` + 教程 1 `install-mod.html`。
做完后停下汇报：文件清单、截图清单、和规划/代码不一致的地方。等我看过再继续。

**第二步**（我确认后）：教程 2 `body-mapping.html`、教程 3 `l4d2-import.html`。
教程 3 有两点规划里标了"待核实"——去读 `VpkImportService.cs`、`ImportService.cs`、`VpkTargetPicker*`、`ModDetailsPageViewModel.Vpk*.cs` 查清楚：
- 一个包里有多个幸存者模型时，管理器是导入时让用户选，还是要用户事先自己挑出一个；
- 材质 VPK 导入后变成的"可配置选项"，用户在界面哪里开关。
查不清就在页面里写成"以实际界面为准"，并在汇报里说明。

## 每篇固定结构
适合谁 → 前提 → 步骤（每步一张截图）→ 怎么确认成功 → 常见问题 → 截图清单。语言面向普通玩家，少用术语；必须出现的术语（素体、映射、污染、低污染）第一次出现时用一句话解释。
