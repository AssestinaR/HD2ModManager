# 自定义素体转换与组件保存（2026-09-25）

## 本轮行为

- 自定义组件用 `isReserved` 区分备用 ID 与启用开关；`originalBody` 记录 Any 的原身形。
- 双身形合并为 Any 时保留另一身形的 ID；拆分优先复用旧 ID。
- 备用 ID 保留在 Mod 库工作区，不进入替换目标目录或运行时协议。
- 自定义 Unit 均允许删除；删除 Any 行也清除该行备用记录。
- 组件编辑按 400 ms 防抖自动保存；退出编辑卡、离开页面、正常关闭窗口时刷新队列。
- 工作区快照在 UI 线程创建；保存串行执行。保存失败会显示状态，不再静默误认为已保存。
- `armor-registry.applied.json` 记录上次应用的源快照，以区分已保存和待编译；重新进入页面仍可以应用未编译编辑。
- GUI 应用时根据实际部署 Patch 的 Unit 记录及载荷边界过滤不存在的自定义 ID，源 JSON 不被过滤。映射引用的素体过滤后为空时拒绝发布协议。

## 一键转换

Mod 详情按钮卡第五排右侧为“一键转为自定义素体”。底栏包含：

- 素体名；
- 来源 Archive（护甲、头盔分别显示），异步加载；
- 输出根目录；
- 自动导入为新 Mod；
- 生成与状态。

> ⛔ 已过时（2026-11-08）：底栏现在只有「素体名 / 护甲来源 / 头盔来源 / 生成」加一行状态。
> **「输出根目录」已不从界面选**（中间目录固定，不暴露给用户），**「自动导入为新 Mod」这个开关已删除** ——
> 生成后**无条件导入成 Mod**，没有"只导出"这条路。
> 产物还会先归位到基底目录 `sectional/`（老库 `vpk2patch/`）再拆成「标准 7 槽 × 身形」的 section 组，
> 所以「一键转为自定义素体」产出的是一具**分件**素体（见 `SectionalModBuilder.BuildAsync`）。
> 现行链路见 `docs/agent/sectional-compile-base-20261108.md` §1。

来源必须有至少一个仅属于该 Archive 的 Unit；共享 Unit 单独出现不足以成为来源证据。
选中 Archive 的可见 Unit 使用现有 Canonical direct-source-reuse 写包链路，重新编号，不修改原 Mod。
来源按完整 Unit 载荷大小降序分配递增 ID；保留标准部位与身形。肩部归胸部；下半身语义附件归胯部；无法判断的部位拒绝猜测。
生成后写出 `custom-model.json` 并登记工作区；不会自动映射或部署。

CLI：

```text
hd2mod custom-model inspect <Mod GUID> --manager-root <管理器目录> --library <Mod库> --game-data <data目录> --json
hd2mod custom-model convert <Mod GUID> <来源Archive> <素体名称> --manager-root <管理器目录> --library <Mod库> --game-data <data目录> --output <输出根目录> --confirm --json
```

CLI `armor-registry apply` 原有冻结协议计划路径未在本轮改为 GUI 工作区编译器；部署 Unit 过滤目前接在 GUI 应用路径。

## 验证

- Debug 标准输出路径构建成功，没有使用外部构建输出。
- Core 419 项通过，CLI 117 项通过。
- WPF App 资源与新底栏、护甲映射页面实例化/测量成功。
- 真实 Lunalice 护甲来源生成 8 个新 Unit；TOC/GPU/Stream 载荷与来源逐字节一致；旧 Unit ID 无残留。
- 验证只读原 `E:\mod`；生成用独立测试库 `E:\mod-custom-conversion-verification-20260925\library`。
- 没有部署测试包或启动游戏。

## 人工验收

1. 修改开关，不点应用，返回页面并重启；确认状态保留且待应用。
2. 双身形 → Any → 双身形，确认 ID 保持；Any 时替换目标仅一条。
3. 删除基础 Unit，确认警告与保存。
4. 一键转换选择来源、导出、自动导入，确认原 Mod 未变且视觉来源列表出现新素体。
5. 启用部署输出 Mod 后应用映射，确认运行时显示；未部署 ID 应不出现在编译协议。

头盔转换并不意味着本轮新增了头盔映射能力；Lua 的既有头盔安全策略没有修改。
