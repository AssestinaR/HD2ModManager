# Copilot Instructions

## 项目指南
- When implementing core functionality, add a brief comment under the using directives in each code file describing the file’s purpose.

## 信息中心架构规则
- 稳定的 Mod 派生信息必须优先归入现有 `IModInformationCenter` 链路，不得在页面、ViewModel 或窗口中重复解析、扫描、统计或缓存同类事实。
- 实现涉及资产、引用、被引用、依赖、冲突、覆盖、材质缺失、Game Data 映射、分析、索引或状态时，必须先检查现有 `IModInformationCenter`、`ModInformationKind`、producer、cache、generation、invalidate 和 `DerivedStateCoordinator`。
- `IModInformationCenter` 负责信息产品请求、generation 计算、持久缓存读取、过期判断、producer 重建、结果保存、请求合并、取消和失效；已有 producer、`ModDataIndex`、SQLite facts store 不得被页面层另行创建或绕过其生命周期。
- `HD2ModManager` 页面层只能消费 `DerivedStateSnapshot`、已有派生投影或共享信息中心提供的结果；禁止自行调用 `CreateModDataIndex(...)`、直接创建信息 producer/cache/store、直接读取信息缓存文件，或重新实现 generation/缓存过期判断。
- 资产标签应消费 `ModContentFacts`/`ModAssetSummary`；当前状态应消费 `DerivedStateCoordinator` 的 `ExpectedGraph`、`MaterialDiagnostics`、部署状态等快照；高级详情中的引用目标、引用数和被引用数应复用已有 `AdvancedUnitAnalysis`、`ReferenceGraph`、SQLite 引用事实及其统一查询链路。
- 新增信息功能前必须明确复用哪个已有信息产品、producer、缓存、generation 和失效条件；只有确认不存在可复用产品后，才可以提出新信息产品，并保持 producer 与信息中心编排分离，避免把信息中心做成解析器。
- 若发现已有功能在页面层手工查询或重建稳定事实，应优先迁移到现有信息中心链路，而不是继续优化或复制该旁路实现。

## Export Guidelines
- Export should produce a zip with an embedded manifest containing portable names and notes only; legacy tags fields are ignored during import (asset tags are derived by scanning).
- Export keeps original filenames, and the zip is named after the top-level/root object name.

## 临时诊断工程规则
- `_diagnostics/` 下的工程都是临时诊断/实验工程：不进入 `WpfApp1.sln`、不参与构建、不属于交付物。
- 生产工程（`HD2ModManager`、`HD2ModCore`、`HD2ModAdaptation`、`HD2ModCli`、`HD2GameItemAssets`、`Decryptor`）不得通过 `ProjectReference`、`using` 或任何其他方式依赖 `_diagnostics/` 下的任何工程或类型。
- 若某个诊断工程里的逻辑确有生产价值，必须先把该逻辑迁入正式工程并补齐测试，再由生产代码引用；禁止让生产代码直接依赖诊断工程。
- 该规则由 `HD2ModCore.Tests/DiagnosticsIsolationArchitectureTests.cs` 强制校验。