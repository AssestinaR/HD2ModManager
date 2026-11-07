# Manager/Core 派生数据进度

## 已完成

- 导入完成后不再触发全库派生重建。
- 导入完成后支持只刷新本次新增 Mod。
- `LibraryDerivedDataService` 支持按 Mod 子集构建派生数据。
- 启动扫描新增 patch 文件组指纹：按 `archiveHex.patch_N` 分组，组内文件按名称和内容 SHA-256 计算 hash。
- 指纹 manifest 持久化到 `data/patch-group-fingerprints.json`。
- 启动时按文件组数量、组名称和组 hash 判断 dirty；未变化组复用现有派生数据。
- 当前阶段关闭 Manager 启动派生流程中的 Adaptation 兼容性分析。

## 暂缓

- Unit/Composite Unit 结构兼容性检测。
- 模型、材质、骨骼适配和依赖闭包。
- Adaptation patch 重建和输出回读。

## 验证

- `HD2ModManager` 构建成功。
- `HD2ModCore.Tests/LibraryDerivedDataServiceTests.cs` 通过。
- 仍有既有 `SharpCompress` 安全漏洞警告，未在本阶段处理。

## 下一阶段

在 Manager/Core 的文件层 dirty 基础稳定后，再专注于 Adaptation：设计独立的单 Mod 兼容性检测接口，并消费明确的 patch 文件、游戏数据和 archive 定位结果。
