# sectional 分支进度（2026-11-04）

> ⚠️ **先看这一条：库里 `lunalice_bill` 的载荷没了（见文末「数据事故」）。**
> 与本分支的代码无关，但影响验证与用户数据，必须先处理。

> 分支 `sectional`，worktree 在 `.agents/worktrees/sectional`，从 master `dc2da4e` 拉出。
> 设计稿：`section-consolidation-20261104.md`；U0 结论：`sectional-u0-findings-20261104.md`。
> **测试基线（每次提交前都跑全量）：1739 通过 / 0 失败 / 1 跳过。**

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
| U5a 删死代码 | daaaea | 装饰快照转发壳、孤儿 XAML 模板 |
| U4 就地烘焙 | c8cf30e | 「应用」不再重跑换甲；端到端验证过（幂等 + 可逆） |
| U3c 拆件执行器 | 49af034 | 读 patch / 合并 / 写回 / 产 sections.json + 组表；端到端验证过（7 units / 92 段 / 组表 100% 覆盖） |
| V0 配方 + 死字段容错 | 7363a71 | 验证配方；三个死字段不再阻断工作区读取 |
| U5b 掩码链停分发 | `1f33815` | 九个死 Lua 不再内嵌进 Core（文件保留，只停内嵌） |

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

### V0 实机那一段（配方已给，进游戏由用户执行）

`sectional-v0-verification-20261104.md` 里离线两条判据可以立刻跑；
实机那三条（位置 / 遮挡 / 崩不崩）必须有人进游戏看。

**前置**：需要一份多 unit 的 legacy 模型样本。本机没有——唯一的 legacy 形态是纯材质包。
造样本的两条路写在配方 §1.1。

### U5 剩下的两处大退场（刻意没做）

这两处不是"删几个文件"，而是**删掉一整套仍在编译、仍在测试覆盖里的子系统**。
astra 审核时明确建议按层退场而不是整包删，所以留给人拍板：

**① 装饰链退场**（`DecorationHostRebuildService` 788 行 / `HostDerivedOverwriteService` /
`DecorationActivationStore` / `DecorationPlanDocument` / 两个装饰页面 / CLI 的
`decoration *` 与 `host-derived *`）
- 现网 0 个装饰，机制上已被 `SectionalSplitter` 取代（后者把 donor 几何直接并进 7 槽）。
- 但 `CanonicalDecorationUnitAppender` 是它的底层，**拆件器也在用**——底层必须留，
  能退的只有它上面那层"独立包 + 启用状态 + 重建宿主"。
- 建议顺序：先停 GUI 入口 → 观察一个版本 → 再删 CLI → 最后删 Core 服务与测试。

**② 护甲级掩码链退场**（`wardrobe.workspace.json` / `wardrobe.masks` /
`WardrobeMaskCompiler` / `WardrobeMaskProjection` / `WardrobeMaskFile` /
`armor_section_*.lua` / `armor_wardrobe_masks.lua` / CLI `wardrobe compile|project|validate`）
- 运行时那条链**已经不可能生效**（读它的 Lua 第一行 `require('ffi')`，本版引擎没有 ffi）。
- 但 Core 里的身份规范化（`WardrobeSectionFingerprint`）与投影思路，将来做
  "护甲级策略 → 生成该护甲的烘焙产物"时还能复用，所以按层归档而不是直接删。
- `wardrobe sections` 要留（补表用）。

### 三个"小删除"——**核过之后发现都不是死代码，已纠正**

早先那份清单（来自 U0 的 A 线报告）说这三处没人用，逐条核实后不成立：

| 项 | 实际状态 |
|---|---|
| `vpk2patch --unit-ids` | **活的**。`CliApplication.ResolveUnitIdentities`（:3091）被 `source convert`（:2698）与 `vpk2patch`（:3489）两处调用。它只是**没写进帮助文本**、管理器也从不传——是"未文档化的可选参数"，不是死代码。 |
| `WardrobeSectionSource` + `RequestUnitSectionStructureAsync` | **活的**。`ModInformationCenter.cs:472` 调用它，而 `RequestUnitSectionStructureAsync` 是 `IModInformationCenter` 上的公开方法（有实现、有 Fake、有测试）。删它要动公开接口。 |
| 装饰 `.bin` 旧路线 | **只读不可达**：`Payloads` 全仓唯一的赋值是 `CliDecorationEditPlanArtifacts.cs:713` 的 `Payloads = []`，**没有写入者**；`DecorationHostRebuildService.cs:77-85` 只是读。这条是真的，但它在一整套要退场的子系统里，单独删没意义。 |

教训与之前几次相同：**"没人调"要先按当前代码查调用点，不能照抄报告结论。**
`--unit-ids` 那条特别典型——它不在帮助里，所以看起来像死参数，实际两个命令都在用。

---

## 合并前检查清单

- [ ] 全量测试绿（当前 1739 / 0 / 1）
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
