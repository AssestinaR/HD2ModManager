# 替换护甲：自定义素体目标

## 数据与操作边界

- 当前库的 `armor-registry.workspace.json` 是素体 ID、部位、身形和组件集合的权威来源。
- 替换目标目录在原版 Armor/Helmet 之外，追加具有显式 Pieces 的自定义素体。
- `custom-model:<key>` 是计划层标识，不是游戏 Archive 或 Item ID。
- 只生成当前启用的组件。禁用及预留 ID 不输出；以后启用这些 ID，必须提供包含它们的 Patch。
- 输出使用已有完整 Unit 复用链路，保留来源 TOC/GPU/stream 和依赖；不进行跨 Unit 几何合并。
- 部位必须相同，自动匹配优先相同身形，其次 Any，再按身形偏好回退。同一源可供应双身形，但不会自动重复供应重叠的额外槽位；需要时可手动指定。
- 缺少来源、强制隐藏启用组件、非法部位/身形、已知 ID 冲突均阻止生成，不读取不存在的原版目标。
- 原版目标保留原有规划、隐藏和共享部件策略。
- 生成不修改源 Mod、素体工作区、运行时协议或部署。安装输出后，仍须将素体映射到真实护甲并应用。
- 不改变 Lua 的头盔/身体隔离规则，也未扩展“自定义 Patch 作为下一次替换来源”的识别能力。

## GUI / CLI

GUI 的替换护甲目标列表显示“自定义素体”和启用组件数；生成前复核素体结构，变化后要求重新打开计划。

CLI 的 `equipment cross-armor inspect/plan` 输出 `CustomModel` 候选及 `customModelKey`。将候选的 `archiveId` 复制到请求的 `selectedTargetArchiveIds`，再 validate/apply。候选事实参与指纹，结构变化使旧计划失效。详细步骤见 CLI 的 cross-armor-transfer skill。

## 2026-09-25 验证

- 基线提交：`2285191`（开始此功能前的视觉组件卡状态）。
- 管理器常规 Debug 构建成功，0 错误；未使用外部构建目录。
- Core 全量测试 410 通过，CLI 全量测试 116 通过。
- 真实来源：测试库 Lunalice / RE-2310 Patch。
- 目标：工作区已有 `custom/lunalice-visible`，9 个启用 Unit。
- CLI validate/apply 成功，输出 9 个 Unit、10 个非 Unit 依赖、0 个隐藏处理、0 个诊断。
- 独立解析输出 TOC，确认 Unit ID 集合与目标一致；逐字节比较全部 9 个 Unit 的 TOC/GPU/stream，均与选定来源一致。
- 验证输出：`E:\mod-custom-target-verification-final-20260925`，含生成报告及 `payload-verification.json`。
- 未部署测试包、未启动游戏；GUI 操作与游戏内视觉仍需人工验收。
