# sectional 分支：合并检查清单 + 一次性测试方案

> 分支 `sectional`（worktree `.agents/worktrees/sectional`），**38 提交 / 82 个改动文件**。
> 全量测试 **1637 通过 / 0 失败 / 1 跳过**（游戏必须关闭，见 §六）。
> 设计稿 `section-consolidation-20261104.md`；U0 结论 `sectional-u0-findings-20261104.md`；
> 进度与剩余工作 `sectional-branch-status-20261104.md`。

---

## 一、这次做成了什么

| 步 | 提交 | 内容 | 验证方式 |
|---|---|---|---|
| U1 组模型 | `772ed65` `292a7ff` | `components.json` → 组表（id / source / exclusiveGroup）；**旧格式零迁移读入** | 库里 8 份真实文件全部读入成功 |
| U2 组编辑器 | `054fba0` `e6e181e` | 新建 / 改名 / 删除 / 开关 / 三态（独立·耦合·失效）/ **勾选即保存** | 离线单测 + 需要你手工点一遍（§二.1） |
| W1 包围盒 | `74a59aa` | `CanonicalCullingBounds` 三模式 + 诊断；**默认关闭** | 单测断言"默认模式返回同一实例" |
| W2 可寻址 | `c95535c` | `uniqueSlotPerSection`；测试钉住"共用槽会并段 / 唯一槽保边界" | 直接打材质槽分配器 |
| U3 拆件器 | `1e6c656` `9ebf1ec` `49af034` `5b136dc` | 规划器 + 段来源账 + 执行器 + **两处真 bug 修复** | V0 离线三条判据全过（§三） |
| V0 配方 | `7ef81fa` `7363a71` | 离线判据 + 实机 A/B 配方 | 离线部分已跑过 |
| U4 就地烘焙 | `c8cf30e` | 「应用」从分钟级 → 秒级 | **真实产物端到端**：幂等 + 可逆 + 字节复原 |
| U5 掩码链 | `1f33815` `e619e64` `bdfda20` `2736764` | 四层全部退场 | 测试 951→870（删 81 条） |
| U5 装饰链 | `e61bb92` `51735ad` `7c77850` | 第 1–3 步：UI 入口 + CLI 入口 + 不可达类 | 测试 199→185（删 14 条） |

**关键性质：三条在役路径（装饰合并 / 头盔合并 / 多源合并）的产物字节不变** ——
W1/W2 默认关闭，且有"返回同一实例"的断言守住。

---

## 二、你只需要测这一次（约 15 分钟）

### 1. 组编辑器（不用进游戏）

打开任意有 `components.json` 的 Mod（如 `MourningVeil_Bill`）→ 详情页「衣物装饰组件」卡：

| 动作 | 期望 |
|---|---|
| 看组列表 | 名字、`N 块 · 来自源侧开关包`、勾选框都在 |
| 改一个组名，点别处，**切到别的 Mod 再回来** | 名字还在 |
| 勾掉一个组 | 树上对应的节跟着变灰 |
| **勾完立刻切走再回来** | 勾选还在（这是这次修的老毛病） |
| 树里选一块几何 → 填名字 → 「新建组」 | 新组出现，块数对得上 |
| 删一个组 | 组消失，**几何还在** |
| 重启管理器 | 以上改动全部还在 |

### 2. 就地烘焙（不用进游戏，但看耗时）

同一个 Mod 点「应用」：

| 期望 |
|---|
| **几秒内完成**（以前几分钟） |
| 状态栏说"已烘焙：关掉 N 个 section…" |
| 再点一次 → "已经是目标状态，没有写入任何字节"（幂等） |
| 把组勾回来再点 → "打开 N 个 section"，产物恢复 |

> 前提：该 Mod 得先换过一次目标护甲（有 `Overwrite/`）。否则会退回重跑换甲，那是正常的。

### 3. 实机最小版（只有这一条必须进游戏）

按 `sectional-v0-verification-20261104.md` §2：

1. 挑一件熟悉的护甲，装一个 Mod，进游戏
2. 关掉一个组 → 那块几何应消失
3. 勾回来 → 应回来
4. 勾另一个组 → 前一个应自动关掉（互斥组）

三次都对 ⇒ 组开关这条链通了。

---

## 三、V0 离线判据（已跑过，全过）

合成样本：把产物的 `Slot_LeftArm` 原地改成 `Slot_LeftLeg`（等长，长度前缀不受影响），
造出"两个 unit 抢同一个槽"。

```
预演   : 6 targets（LeftArm 并入 LeftLeg）/ 7 groups / 1 条预期警告
执行   : valid=True

判据 1 段数对账 : LeftLeg = 27 = 16（宿主）+ 11（donor）   ✅ 没有并段
判据 2 组员不交叠: overlap = 0，覆盖 92/92 段（100%）        ✅
判据 3 逐级 LOD : L0..L3 全是 27 段，consistent=True        ✅
组表   : 基础组 16 段 / 变体组 11 段，同属 LeftLeg/Any 互斥组 ⇒ 变体替换成立
```

---

## 四、合并前检查

| # | 检查 | 状态 |
|---|---|---|
| 1 | 全量测试绿 | ✅ **1637 / 0 / 1**（游戏关闭） |
| 2 | 三条在役路径字节不变 | ✅ 默认 `Preserve` + `uniqueSlotPerSection: false`，有引用同一实例的断言 |
| 3 | 老 `components.json` 仍能读 | ✅ 8 份真实文件实测 |
| 4 | 没碰材质方向的目录 | ✅ `git diff master...sectional` 里 `HD2SourceMigration/` 下的改动 **0 个** |
| 5 | 与 master 合并预演 | ✅ `git merge-tree` 只回一个树对象，**无冲突**（master 只多了一个材质方向的提交 `3740238`） |
| 6 | 管理器能启动、库能打开 | ⬜ 合并后由你验一次 |

---

## 五、剩余工作（详见状态文档）

| 步 | 内容 | 为什么没做 |
|---|---|---|
| 装饰链 4 | `ModLibraryService`（137 行）+ 4 个 VM | 不是"改一个文件"：7 个装饰 API 仍被 `LibraryPageViewModel` / `ModDetailsPageViewModel` / `ShellViewModel` / `BottomBarCoordinator` 调用，且 `FlushPendingDecorationRebuildsAsync` 在**部署前置**里、删除路径与 option 处理混在同一事务。**要一轮完整预算** |
| 装饰链 5 | 五个 Core 装饰服务 | 依赖第 4 步 |
| 装饰链 6 | `ModNodeKind.Decoration` + 19 个消费点 | 现网 0 个节点，删枚举不需数据迁移 |
| V0 实机 | 位置 / 遮挡 / 崩不崩 | 只有进游戏能判 |

---

## 六、跑全量之前必须退出游戏

`DeploymentCliTests` 有 5 条会**必然失败**，只要 Helldivers 2 在运行：

```
deploy validate 带 includeProcessCheck，检测到游戏在跑就返回 2
（CliApplication.cs `return validation.IsValid ? 0 : 2`），而测试断言 0。
```

这是护栏在起作用，不是回归。**已用 master 复现同一组失败确认过**。
症状：`Assert.Equal() Failure: Expected 0, Actual 2`，全在 `DeploymentCliTests` 里。

---

## 七、回滚

分支没合进 master 之前，master 一点没变：

```powershell
cd E:\Data\source\repos\WpfApp1
git worktree remove .agents\worktrees\sectional --force
git branch -D sectional
```

合进去之后要回滚就 revert 那次 merge commit。

---

## 八、合并后仍存在的风险

1. **`lunalice_bill` 的载荷丢了**（状态文档文末）。与本分支无关，但那 12 个 option 现在挂在空宿主上。
2. **拆件的 donor 合并没有实机证据**。V0 离线三条已过，实机三条待做。
3. **`uniqueSlotPerSection` 的代价**：材质槽与 `BoneInfo.Remaps` 条数随段数线性增长；
   100+ 段是否可行**没测过**。
4. **装饰链第 4 步之前，装饰功能已无入口但 Core 代码仍在**——不影响使用，只是仓库里还有死代码。
