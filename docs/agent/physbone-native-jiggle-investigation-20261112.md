# 原生 jigglebone 可行性再调查（2026-11-12）

> **一句话**：当年判"护甲 unit 的 state_machine 不被 tick"的那个实验，**很可能测错了对象** ——
> 我们只放了 SM 资产，从来没有在 unit 头里写 **`0x20`（= state machine 引用）**。
> 本轮找到了这个字段、验证了 SM 的离线读写全链路，并搞清了还差哪一块。

## 1. 为什么重开这个结论

`hd2-physics-roadmap-and-handover.md` §3.2 的 L1 结论是：

> **护甲 Unit 自己的 `state_machine` 不会被 tick。** 只是"在同一 FileId 下多放一份 SM 资产"不够。

L1 做的三件事：把 `bonesRef` 指向 patch 里的 bones（**无害**）、加 SM 资产、在 SM 里加一条条目
→ 游戏里无任何变化。

**但它没有写 unit 头的 `0x20`。** 而本轮证据表明 `0x20` 正是 state machine 引用。

## 2. 证据：`unit[0x20] == state machine 引用`

从原版归档 `18235e0c9ec0e636`（玩家 avatar 归档）取 5 件 unit，对比头部：

| unit | 0x08 | **0x20** | bones 资产 | state_machine 资产 |
|---|---|---|---|---|
| avatar `0x4d1c33…` | 自身 id | **自身 id** | ✓ | ✓ |
| 披风 `0x3c33cf…`（66 骨） | 自身 id | **0** | ✓ | ✗ |
| 披风 `0xea846e…`（44 骨） | 自身 id | **0** | ✓ | ✗ |
| `0x0c6567…` | 自身 id | **自身 id** | ✗ | ✓ |
| 护甲件 `0x2601db…` | 0 | **0** | ✗ | ✗ |

`0x20` 非零 **当且仅当**该 unit 有 state_machine 资产（4/4）。

> 注意：`0x4d1c334d294dfa97` 这个 FileId 下同时挂了 bones / state_machine / physics /
> ik_skeleton / ragdoll_profile（同一 FileId、不同类型），所以单看 avatar 一例无法区分；
> 是靠"披风有 bones 无 SM"和"0x0c6567 有 SM 无 bones"两件对照分开的。

## 3. 工具链：SM 可以离线读写（全部已验证）

`tools/ref/HD2SDK-CommunityEdition/stingray/state_machine.py` 是现成解析器（本地自带，无需外网）。

| 验证 | 结果 |
|---|---|
| 解析真实资产（归档里 756 B 的 SM） | ✅ layer=1 / state=3 / ragdoll_count=0 |
| `set_ragdoll()` 追加 1 条 → `Serialize` | ✅ 756 → 812 字节（正好 +56） |
| 回读校验 | ✅ `bone_index` 与 9 个 float 完全一致 |
| **从零构造最小 SM**（0 层，只有 jiggle 表） | ✅ **248 字节**，3 条条目，回读一致 |
| `stingray/bones.py` 的 `Serialize` | ✅ 支持写入（名字 + hashes + LOD） |

条目格式（与本仓文档一致）：`uint32 bone_index` + `float32 params[9]` + `uint64 hash` + `uint32 enum` + `uint32`。

类型哈希：`unit 0xe0a48d0be9a7453f` · `bones 0x18dead01056b72e9` · `state_machine 0xa486d4045106165c`。

## 4. 我们自己的资产长什么样（这是新发现的障碍）

扫 `E:\mod`（421 个 patch）找到物理日志里那 7 个 unit 的出处：
`E:\mod\恋星milfy_bill\Overwrite\9ba626afa44a3aa3.patch_0`（17 条目）。

- 7 个 unit：`1ee1f8c7bffe0001..0007`
- **全部 `0x08 = 0`（没有 bones 引用）、`0x20 = 0`**
- patch 里**没有 bones 资产**

也就是说：我们的抖动骨只是 **unit 场景图里的节点**（名字照抄源骨架，如 `Outer_B_005_R`），
没有独立骨表 —— 而 SM 的 `bone_index` 只能索引**骨表**。

⇒ 所以这个实验不是"加一个 SM"，而是要**同时补上骨表**：

1. 造 bones 资产（名字列表 = 我们要驱动的节点名，按 unit 场景图顺序）
2. unit `0x08` → 该 bones 的 FileId
3. 造最小 SM（条目 `bone_index` 指向该骨表下标）
4. unit `0x20` → 同一个 FileId
5. 三个条目一起打进 patch

## 5. 未验证、下一步要回答的

| # | 问题 | 怎么验 |
|---|---|---|
| Q1 | 明明 unit 场景图里有这些节点，为什么还要骨表？没有骨表时 SM 能工作吗？ | 先只做 2+3+4（不加 bone 表）看是否崩/是否 tick |
| Q2 | `bone_index` 索引的是骨表顺序还是场景图顺序？ | 造一份"名字与场景图同序"的骨表，先只放 1 条条目 |
| Q3 | 0 层 SM 会不会被引擎拒收 | 失败就换 756 B 模板（1 层 3 状态）改造 |
| Q4 | 索引对错了会不会崩 | 先在独立测试 mod 上做，随时可删 |

## 6. 风险与止损

- 全部改动都在 `E:\mod` 的测试对象上，**不进游戏本体**；`Overwrite\` 目录本身是部署产物，
  下次部署会被重写，天然可回滚。
- 若出现崩溃：删掉该 patch 的三条新增/修改即可恢复。
- 引擎侧无符号调试手段（EAC 会拦）；真要动态验证再上 HellForge（其 build 25480438 与当前
  26568081 只差文件校验，玩法无关 —— 用户已确认可以启动）。

## 7. 本轮没做的事

- 没有修改任何 mod 文件（只读 + 在 `E:\tmp` 写临时产物）。
- 没有把结论写进 `roadmap §3.2`；等实验有结果再改那份"过期口径"，避免又留一条半截结论。
