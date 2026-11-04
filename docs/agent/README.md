# docs/agent — 分析、设计与复核文档

本目录收录**由 agent 会话产出、面向 agent 消费**的文档：事实层审计、性能取证、能力盘点、目标架构、CLI 评估、独立复核报告。
产品定义与实施约束类文档不在本目录，仍在 `docs/` 根。

这些文档是**写作当时**的取证与判断快照，不是当前实现的描述。实现状态以代码、`docs/说明.md` 与提交历史为准。

## 约定

- 命名：`<主题>-<YYYYMMDD>.md`；复核报告为 HTML，保留原文件名。
- 归档不改写：文档正文按产出时原样保留，观点冲突时以更新的文档与代码为准。
- 代码注释引用本目录时写全路径，如 `docs/agent/armor-registry-page-perf-20261027.md §1`。

## 事实层审计

| 文档 | 内容 |
|---|---|
| `armor-unit-facts-audit-20261025.md` | 「某 unit 属于某件护甲的什么部位」被四套互不隶属的来源提供、被至少 18 处各自解释；结构结论已落到 `ArmorStructure` |
| `archive-facts-coverage-20261026.md` | 3567 个 archive 的归属 / 名字 / 分类为何三缺；根因是最大的事实源 `game_data_authority_entities` 解析结果为 0 行 |

## 性能与故障定位

| 文档 | 内容 |
|---|---|
| `armor-registry-page-perf-20261027.md` | 护甲映射页两段延迟（正常约 1.0 s，启动维护未完成时 14.5 s）的取数闸门取证；只定位不改码 |
| `mod-deletion-perf-residue-20261027.md` | 删除 Mod 一个条目的实际开销与残留原因，含修复 |

## 能力盘点与架构设计

| 文档 | 内容 |
|---|---|
| `mod-capability-fusion-20261026.md` | Mod 详情页平铺 22 个 `RelayCommand` 的现状盘点与操作页融合设计 |
| `mod-derivation-functions-20261026.md` | 「造一个新 Mod」十个功能逐个拆解，附护甲映射、自定义素体、VPK 三块 |
| `armor-cell-plan-20261027.md` | 替换护甲的单元格装箱方案：`ArmorCell` 作为 unit 身份（F1–F4 领域事实），旧 P1~P5 启发式待替换 |
| `professional-workbench-architecture-20261026.md` | 目标架构：先钉死七条不变量，再谈实现 |

## 排查交接

| 文档 | 内容 |
|---|---|
| `vpk-issues-20261028-plan.md` | 压缩上下文前的交接单：样本与日志路径、已修三笔提交、未闭合项 |

## CLI（`cli/`）

| 文档 | 内容 |
|---|---|
| `cli/HD2ModCli-capability-map.md` | 协议 1.27 下 21 个命令族的能力全景与评价；CLI 定位为面向 Agent 的「计划-验证-执行」骨架 |
| `cli/HD2ModCli-layer-boundaries-assessment.md` | 分层边界评估：架构方向可行，缺的是接口而非新项目 |
| `cli/HD2ModCli-riglab-gap-analysis.md` | 另一线程被迫手写的基层能力清单——本该由 CLI 暴露成命令 |

## 独立复核（HTML）

| 文档 | 内容 |
|---|---|
| `arm-elbow-independent-review.html` | 手臂变换复核：主线程移动了旋转中心却没真正拉长上臂；给出「先变形 T-pose 顶点、同步工作枢轴、再统一摆姿」方案 |
| `finger-retarget-independent-review.html` | 手指自动蜷曲指导：逐节对齐目标三维方向、循环十指；末节须另读骨轴 |
| `weight-transfer-independent-review.html` | 权重方案：六区域限制加附件规则；先筛同语义候选，再取最近三角面插值，按可信度回退 |
| `round60-independent-review.html` | 第 60 轮复核：推翻「材质打包正确」的结论（槽位键与 Texture ID 必须分段写）；小臂权重统计混入了壳层 |
| `unit-transform-independent-review.html` | Unit 故障复核：绑定数据确有改善，但旧躯干边界仍残留在包内，9 条槽位引用不等于单实例加载 |

## 未收录

`docs/` 根保留产品侧文档：`说明.md`（实现硬约束）、`内部Mod配置部署实施计划.md`（阶段与验收门槛，正文引用 `说明.md`）、`CoreRedesignRequirements.md`（Core 重做需求总结，讨论稿）、`memo.md`（个人待办备忘）、`逻辑说明2.md`（空文件，待补）。
