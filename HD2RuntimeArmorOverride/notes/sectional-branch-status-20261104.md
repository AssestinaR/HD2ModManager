# sectional 分支进度（2026-11-04）

> ⚠️ **先看这一条：库里 `lunalice_bill` 的载荷没了（见文末「数据事故」）。**
> 与本分支的代码无关，但影响验证与用户数据，必须先处理。

> 分支 `sectional`，worktree 在 `.agents/worktrees/sectional`，从 master `dc2da4e` 拉出。
> 设计稿：`section-consolidation-20261104.md`；U0 结论：`sectional-u0-findings-20261104.md`。
> **测试基线（每次提交前都跑全量）：1736 通过 / 0 失败 / 1 跳过。**

---

## 已完成

| 步 | 提交 | 内容 |
|---|---|---|
| 建分支 | `7cfa623` | worktree + 实测基线（1691 通过，比记录的 1685 多 6 条 Core 测试） |
| U1 机械改名 | `772ed65` | `VpkComponentSwitch*` → `SectionGroup*`（零行为变化） |
| U1 组模型 | `292a7ff` | 组表带 id / source / exclusiveGroup；schema → `hd2mod.section-groups/1`；**旧格式零迁移读入**（8 份真实 `components.json` 实测通过） |
| U2a 组编辑 | `054fba0` | `SectionGroupEditor`：新建 / 改名 / 删除 / 设互斥，纯函数 + 9 条测试 |
| U2b 组编辑器 | `e6e181e` | 改名 / 删除 / 新建 + 独立·耦合·失效三态 + **勾选即保存**（含"防抖写错目标"那个 bug 的修复） |
| W1 包围盒 | `74a59aa` | `CanonicalCullingBounds` 三模式 + 诊断；**默认 Preserve，三条在役路径字节不变** |
| W2 可寻址 | `c95535c` | `uniqueSlotPerSection`；测试直接钉住"共用槽会并段 / 唯一槽保边界" |
| U3a 规划器 | `1e6c656` | `SectionalSplitPlanner`（7 槽 × 身形，绝不跨身形合并）+ **互斥组强制**（变体替换的全部实现） |
| U3b 段来源账 | `9ebf1ec` | 追加结果带 `Origins`，供拆件器把 donor 几何翻成组员 |
| U5a 删死代码 | `adaaaea` | 装饰快照转发壳、孤儿 XAML 模板 |

### 几个值得记住的判断

- **`components.json` 这个文件名没改**：改名要迁移库里 8 份已有文件，而这件事没有用户可见收益。
  它现在装的是组表，属于和 `vpk2patch/` 同一类的命名债。
- **`SectionGroup` 不加 `defaultEnabled`**：开关状态本来就在 `wardrobe.json.disabled` 里，
  再加一个"默认值"只会多一个会和权威数据打架的真相来源。
- **变体替换不需要新字段**：基础件与各变体同属一个 `exclusiveGroup`，选一个变体就等于把基础件关掉。
- **`Origins` 在段数被合并改变时留空而不是报诊断**：段数变化在默认路径上是正常现象，
  把它变成诊断会让三条在役路径整体失败。

---

## 还没做

### U3c 拆件执行器（最大的一块）

规划器已经能给出"把哪些 unit 并进哪个槽、生成哪些组"，缺的是**真正写出一份新 patch**：

1. 读源 patch 的全部 unit（`PatchTocScanner` + `PatchUnitMeshReader`）。
2. 每个目标：宿主 `PatchUnitMesh` + donor 的 LOD 家族 → `TryAppendLodFamily(..., cullingBounds: UnionWithComputed, uniqueSlotPerSection: true)`。
3. 用 `CanonicalPatchSession` + `PatchWorkspaceWriter.WriteAsync(session, outDir, fileName, headerTemplateTocData)` 写出。
   **卡点**：`PatchTocScanner` 不暴露 `HeaderTemplateTocData`（只有 `PatchWorkspace` 有），
   需要先确认怎么从一份普通 patch 拿到表头模板。
4. 用 `WardrobeSectionMapFile.BuildAsync(新patch)` 产 `sections.json`。
5. 用 `Origins` 把 donor 的段翻译成组员，写 `components.json`。
6. CLI `hd2mod mod split <Mod> [--plan|--apply]`。

**为什么停在这里**：写 patch 的代码一旦错了，产物是"能加载但几何乱"，
比不写更糟；而本机**没有任何"多槽位占满"的 legacy 模型 Mod 可当样本**
（唯一的 legacy 形态是 `【材质版】隐藏物品合集`，纯材质，没有 unit）。
验证数据要先造：拿**游戏本体的一件护甲 patch** 当 donor 侧（它天然多槽位占满），
配上我们自己的 7 unit 产物当主体。

### U4 就地烘焙（「应用」不再重跑整条换甲）

设计已定（`sectional-u0-findings` §6.3），实现路径：

1. `WardrobeSectionMapUnit` 加可选 `sourceUnit`；`CliVpkRekey` 重建表时（`:274`/`:439`）填上。
2. 「应用」→ 读 `Overwrite/sections.json`，按 `sourceUnit` 把 `disabled`（来源键空间）翻成目标键空间。
3. `WardrobeSectionBaker.BakeAsync(Overwrite/<patch>, overwriteMap, translated)`。
4. 判据：勾一下 3 秒内 `numIndices` 变了；连点 5 次一致；隐藏→恢复后字节复原。

### U5 剩下的修剪

- `.bin` 装饰旧路线（`DecorationPayloadFile` / `Payloads` / `DecorationHostRebuildService.cs:77-85,533-539`）
- `WardrobeSectionSource` + 无人发起的 `RequestUnitSectionStructureAsync`
- `vpk2patch --unit-ids`
- 护甲级掩码整链按层退场（停分发死 Lua → CLI 弃用一个周期 → Core 归档）
- `pieceOverrides` / `targetModes` / `CompositionMode` 的校验从致命降为警告

### V0 实机垂直切片

按用户判断**不阻塞开工**，但 **U3 交付前必须做一次**：
它是那三个"静默少几何"入口（同键并段、LOD 不齐跳过、深度代理遮挡）的唯一裁判。
配方见 `sectional-u0-findings` §6.1。

---

## 合并前检查清单

- [ ] 全量测试绿（当前 1730 / 0 / 1）
- [ ] legacy Mod 重构建一次，产物与拆件时逐字节一致（U3 的验收）
- [ ] V0 至少做过一次
- [ ] 确认没有把材质方向会话正在改的文件带进来（`HD2SourceMigration/VpkConversion/*`）
- [ ] master 合进分支一次，解决 `CliApplication.cs` 等热点文件的冲突

---

## 数据事故：`E:\mod\lunalice_bill` 的载荷与来源 VPK 不见了

发现时间 2026-11-04 14:05 左右（做 U4 端到端验证、想拿它当样本时）。

**现状**

```
E:\mod\lunalice_bill\
    vpk2patch\                       ← 目录本身还在
        physbone-rig.bin   10974 B   ← 只剩这一个文件（11-03 12:23）
```

**丢掉的**：来源 `.vpk`（约 12 MB）、`vpk2patch\*.patch_0` + `.gpu_resources` + `.stream`、
`sections.json`、`components.json`、`custom-model.json`、`wardrobe.json`。
目录的修改时间是 **2026-11-04 12:53:41**。

**连带影响**

- `library.json` 里这个节点**还在**（`kind=standard`），但 `patchGroups=0`、`children=0`
  —— 说明管理器之后重扫过，发现没有可部署的 patch。
- **12 个 option 把它当宿主，且全部 `enabled=true`**。它们现在挂在一个没有内容的宿主上。

**已排除的**

- 不是 `VpkPayloadSwap` 的换载荷操作：库里**没有** `.hd2modmanager-swap-backups` 目录
  （那个操作会先备份）。
- 不是我的 `unitprobe` 读取（那次是 13:35，在文件消失之后；且不带 `--patch-numindices` 就是只读）。
- 全库只有这一个 Mod 缺载荷，其余 130+ 个正常。

**没找到可恢复的副本**：`E:` 全盘按名字搜 `*lunalice*`，只有 9 月的几个部署计划 JSON
和另一个仓库的源码；回收站查询因 COM 枚举卡住未完成。

**恢复办法**：用户手里应该还有原始的 `lunalice_bill.vpk`（L4D2 addons 目录），
重新导入即可——**但先要确认是什么删的**，否则导回去可能再被删一次。

**不能确定的原因**：12:53 前后本机有两个会话在工作（本会话的 U0 子代理在 12:20–12:50 读库；
另一个会话在做材质方向）。我没有证据指向任何一方，只能说清事实与时间点。
