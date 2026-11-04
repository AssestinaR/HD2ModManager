# sectional 分支：合并检查清单 + 一次性测试方案

> 分支 `sectional`（worktree `.agents/worktrees/sectional`），15+ 提交，全量测试
> **1739 通过 / 0 失败 / 1 跳过**。本文是"合进 master 之前要做的"与"你只需要测一次的那些"。

---

## 一、合并前检查（我来做，除非你要自己看）

| # | 检查 | 怎么判 |
|---|---|---|
| 1 | 全量测试绿 | `dotnet test WpfApp1.sln` → 1739 / 0 / 1 |
| 2 | 三条在役路径产物字节不变 | 装饰合并 / 头盔合并 / 多源合并都走默认 `Preserve` + `uniqueSlotPerSection: false`；W1/W2 的测试里各有一条"返回同一实例"的断言 |
| 3 | 老 `components.json` 仍能读 | 8 份真实文件实测通过（6/16/6/12/14/13/14/23 个组） |
| 4 | 没把材质方向会话的文件带进来 | `git diff master...sectional --name-only` 里不该出现 `HD2SourceMigration/VpkConversion/*` 的改动（除了 `UnitAuthor` 之类我明确没碰的） |
| 5 | master 合进来一次 | **已预演：无冲突**。`git merge-tree --write-tree master sectional` 只回一个树对象，没有冲突输出。master 上目前只多了一个材质方向的提交（`3740238` 接源法线图），与本分支 44 个改动文件零交集。合完再跑一遍全量 |
| 6 | 管理器能启动、库能打开 | 用 worktree 的 `bin` 启一次，确认 133 个节点正常列出 |

---

## 二、你只需要测这一次（按顺序，10 分钟）

### 1. 组编辑器（不需要进游戏）

打开任意一个有 `components.json` 的 Mod（比如 `MourningVeil_Bill`）→ 详情页「衣物装饰组件」卡：

| 动作 | 期望 |
|---|---|
| 看组列表 | 名字、`N 块 · 来自源侧开关包`、勾选框都在 |
| 改一个组的名字，点别处 | 名字留住；**切到别的 Mod 再回来，名字还在** |
| 勾掉一个组 | 树上对应的节跟着变灰 |
| **勾完立刻切到别的 Mod，再回来** | 勾选还在（这条是这次修的老毛病：以前只有点「应用」才写盘） |
| 点「新建组」（先在树里选一块几何） | 新组出现，覆盖块数对得上 |
| 删一个组 | 组消失，**几何还在**（树上那几节仍然可见） |
| 关掉窗口重开管理器 | 以上改动全部还在 |

### 2. 就地烘焙（不需要进游戏，但要看文件）

对同一个 Mod 点「应用」：

| 期望 | 怎么确认 |
|---|---|
| **几秒内完成**（以前是几分钟） | 看耗时 |
| 状态栏说"已烘焙：关掉 N 个 section…" | 卡片上的结论 |
| 再点一次「应用」 | 说"已经是目标状态，没有写入任何字节"（幂等） |
| 把刚才关掉的组勾回来再点「应用」 | 说"打开 N 个 section"，且**产物恢复到原样** |

> 前提：这个 Mod 得先换过一次目标护甲（有 `Overwrite/`），否则会退回重跑换甲那条路——
> 那是正常的，不是失败。

### 3. 实机（只有这一条必须进游戏）

按 `sectional-v0-verification-20261104.md` §2 做。**最小版**：

1. 挑一件你熟悉的护甲，装上一个 Mod，进游戏；
2. 关掉一个组 → 那块几何应该消失；
3. 勾回来 → 应该回来；
4. 换成另一个组 → 前一个应该自动关掉（互斥组）。

**判据**：三次都对 ⇒ 组开关这条链通了。

---

## 三、回滚

分支没合进 master 之前，master 一点没变。要放弃就删 worktree：

```powershell
cd E:\Data\source\repos\WpfApp1
git worktree remove .agents\worktrees\sectional --force
git branch -D sectional
```

合进 master 之后要回滚就 revert 那次 merge commit。

---

## 四、这次没做的（详见 `sectional-branch-status-20261104.md`）

- **装饰链退场**：底层 appender 必须留（拆件器在用），能退的只有上面那层。
- **护甲级掩码链退场**：运行时那条已经不可能生效，但 Core 里的身份规范化与投影思路还有复用价值。
- 三个一两行的小删除（`--unit-ids` / `WardrobeSectionSource` / 装饰 `.bin` 路线）。
- **V0 的实机那三条**（需要你进游戏）。

---

## 五、已知风险（合并后仍存在）

1. **`lunalice_bill` 的载荷丢了**（见状态文档文末）。与本分支无关，但那个 Mod 现在没有内容，
   12 个 option 挂在空宿主上。重新导入前先查清是什么删的。
2. **拆件的 donor 合并那段没有实机证据**。它依赖的追加链路是发版行为，但"拆件这个用法"
   没被单独验证过。V0 就是为它准备的。
3. **`uniqueSlotPerSection` 的代价**：材质槽与 `BoneInfo.Remaps` 条数随段数线性增长。
   段数上界（100+ 是否可行）还没测。

---

## 六、跑全量之前必须退出游戏

`DeploymentCliTests` 有 5 条会**必然失败**，只要 Helldivers 2 在运行：

```
deploy validate 带 includeProcessCheck，检测到游戏在跑就返回 2
（CliApplication.cs:1177 `return validation.IsValid ? 0 : 2`），而测试断言 0。
```

这是护栏在起作用，不是回归。**已用 master 复现同一组失败确认过**，与本分支无关。
症状是 `Assert.Equal() Failure: Expected 0, Actual 2`，全在 `DeploymentCliTests` 里。
