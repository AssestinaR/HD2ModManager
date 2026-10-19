# section 级衣柜：运行期显隐双向验证通过（2026-10-19）

## 一句话

在真实游戏进程里，**勾选隐藏**与**取消勾选恢复**都生效了，且都只换装一次、
不重启游戏。至此阶段 1（schema）与阶段 3（运行时应用）的目标达成。

## 实测记录

部署：`patch_2` → `E:\mod\.runtime\lua\57c5064b…\9ba626afa44a3aa3.patch_0`
（含 `armor_section_*` 五个模块）

| 步骤 | `wardrobe.masks` | 协议 | `runtime.status` | 画面 |
|---|---|---|---|---|
| 隐藏 | 隐藏 unit `af431352d9b95e83` 的 section 0、1 | `revision=124` | `sections 10 hidden 2` | 面部消失 |
| 恢复 | 清空（只剩 schema/revision/enabled） | `revision=125` | `sections 10 hidden 0` | 面部回来 |

`sections=` 这个字段实际是**写入条数**（dword 写入数），不是 section 数量：
2 个 section × 5 级 LOD = 10。隐藏写 10 条 0，恢复写 10 条原值。

`state=ready`、`active_mappings=3`，说明同一事务里 blob 发布与 section 写入
都成功了，没有回滚。

## 真正跑起来才暴露的两个 bug（离线测不出来）

1. **8 字节对齐检查套错了对象**。写入器复用了 `slot_writable()`——那是给注册表
   8 字节索引槽用的。section 的 `NumIndices` 在 `sectionsAt + index*24 + 16`，
   实测落在 `0x5db2`（只 2 字节对齐），于是**每一次写入都被自己的保护检查拒绝**。
   `WriteProcessMemory` 按字节写、不要求对齐。已改成独立的 4 字节写入器：
   只校验"页已提交 + 可写 + 4 字节在区间内"。
   症状是 `write_failed at section 0; rolled back`——回滚是干净的，没有半关状态。

2. **原值没有缓存，导致"取消勾选"永远不生效**。section 被写成 0 之后，内存里
   就没有原值了；下一次 apply 如果掩码不再提到它，计划里一条写入都没有，
   几何会一直隐藏到重启游戏。修法：运行时在**清零之前**把原值按地址缓存，
   并记住动过哪些 unit；每次 apply 对"不再被覆盖但曾被动过"的 unit
   用缓存重算一遍、写回原值。

## 顺带确认的架构结论

* 运行时**不需要** `sections.json`：它自己走 TOC 拿到偏移（阶段 3 的离线验证 +
  本次实测都成立）。构建期映射表只服务于编辑器和 CLI。
* **改掩码不需要重启游戏**：改文件 + 递增协议 `revision` 即可触发一次重载。
  这验证了交接文档 §3.3 的"写入 + 一次重载 = 生效"。
* 失败回滚可用：第一次尝试写入失败时状态是 `apply_error`，
  消息明确写出 `rolled back`，没有留下部分隐藏的中间态。

## 仍未覆盖

* 只验证了**头盔**（unit `af431352d9b95e83`，22 section × 5 LOD）。
  身体、以及多 unit 同时生效未实测。
* **整体禁用判定**（整个 unit 全关时不注册 Piece）未实测，显存收益仍未知。
* 掩码目前只能手写。生成器（阶段 2）与 CLI（阶段 4）尚未实现。

## 环境侧踩到的坑（与本项目代码无关）

构建产物一度被我的执行环境打上 `Low Mandatory Level` 完整性标签，
导致从工作区启动的 exe 以低完整性运行，**写不了** `%LOCALAPPDATA%` 与 `E:\mod`
（表现为 `Access to the path ... denied`，一度被误判为 Defender 拦截）。
诊断方法：`icacls <exe> | findstr Mandatory`，必须是 `Medium`；
修复：`icacls <目录> /setintegritylevel "(OI)(CI)M" /T /C`。
