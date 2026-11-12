# 给转换器侧：让每个 unit 只带它自己用得上的物理骨

> 写于 2026-11-12。运行时侧已实测确认问题与数字，本文给**转换器/分段转换线**的 agent 用。
> 相关背景见 `docs/agent/physbone-in-mission-starvation-20261112.md`（运行时侧的排查史）。

---

## 0. 一句话结论

**物理容器只存了一份（正确），但 7 个 unit 的骨架里都被塞进了同一整套 57 根物理骨，
于是运行时把同一套 39 条链在 7 个 unit 上各驱动了一遍** —— 273 链/玩家，其中大部分没有几何、
纯属空转。

转换器侧的活：**每根物理骨只加进"真正用到它的那个 unit"**，而不是每个 unit 都加全套。

---

## 1. 实测数据（用户外观 `E:\mod\Lunalice_Bill`）

样例是分段自定义模型（sectional），`sectional/custom-model.json` + `sections.json` + `patches.json`。

### 1.1 这套外观 = 7 个 unit

`sectional/custom-model.json`：13 条 pieces → **7 个不同 unit**，每个 unit 对应一个 slot，
`body 0` 与 `body 1` 各一条（男女体型）：

| unit | slot | 主要材质（`sections.json` 的 `sourceMaterial`） | 说明 |
|---|---|---|---|
| `5c44f3fe7b7c0001` | 0 | `shinano_hair_ear`、`headdress_white01_*`、`ornament_*headdress`、`shinano_face` | **头发 + 头饰** |
| `5c44f3fe7b7c0002` | 2 | `dress_white01_skirt`、`bra_white01_*`、`choker`、`dress_corset` | **裙摆 + 上身** |
| `5c44f3fe7b7c0003` | 3 | `dress_white01_skirt`、`lower_white01_*`、`garter` | **裙摆下段 + 腿饰** |
| `5c44f3fe7b7c0004` | 4 | `lower_white01_tights_left`、`shoes_*`、`leg_garter` | 左腿 + 鞋 |
| `5c44f3fe7b7c0005` | 5 | `..._right` 同上 | 右腿 + 鞋 |
| `5c44f3fe7b7c0006` | 6 | `glove_white01_*`、`bra_white01_armcuff` | 手臂 + 手套（左） |
| `5c44f3fe7b7c0007` | 7 | `..._right` 同上 | 手臂 + 手套（右） |

**每个 unit 的 sections 里都有一份 `shinano_body`**（身体网格被复制进每个 unit，这是分段装甲
为了按 section 开关而做的设计）。这是"每个 unit 都有整套骨骼"的直接原因。

### 1.2 物理容器只有 **1 份**

`sectional/9ba626afa44a3aa3.patch_0.gpu_resources` 里 `HD2SRG01`（容器 magic）出现 **1 次**（偏移 `0x1e14860`），
其余 12 个 patch 都没有（`patches.json` 里它们 `role = material`）。

`patch_0` 的 `role = "unit"`、`unitCount = 7` —— 7 个 unit 在同一个 patch 里，共享同一张名字表。

容器内容（`sectional/physbone-rig.bin`，10974 字节）：

- 57 根骨，`recordSize = 148`，骨名形如 `j4ad8bc381c40/source_extra_64`…
- 骨名在 patch 里**各出现 1 次**（共享名字表，不是 7 份拷贝）
- 由 `parentName` 归并成 **39 条链**，锚点分布（`parse-rig.py` 离线解析，与运行时日志完全一致）：
  `head × 15`、`chest × 4`、`hips × 10`、`spine2 × 6`、`l_knee × 2`、`r_knee × 2`

### 1.3 运行时的后果

- `select_rig` 的判据是"**该容器 57 根骨在这个 unit 上全部存在**"；
- 7 个 unit 的骨架里都有那 57 根骨 ⇒ **7 个全部"全命中"** ⇒ 全部挂载；
- 每件 39 条链 ⇒ **7 × 39 = 273 链/玩家**（2 人局 546 链）；
- 实测心跳：`unit=18 链=702`、`本帧驱动=10~11`、`引擎调用≈1400~1500`、`驱动=5~6 ms`。

> 用户预算模型是"4 玩家 × 每人数件 × 39 链"。这套外观是 **7 件**，所以对它是 7 倍；
> 4 人局全穿同类外观会明显超出 5 ms 预算。

---

## 2. 复现方法（离线，不需要游戏）

三个脚本都在 `tools/physbone-audit/`，都接受路径参数：

```powershell
# ① 这个 mod 里有几个物理容器（期望：1）
python tools\physbone-audit\scan-containers.py "E:\mod\Lunalice_Bill\sectional"

# ② 容器内容：骨数 / 链数 / 锚点分布（期望：57 骨 / 39 链 / head15 hips10 spine2:6 chest4 knee2+2）
python tools\physbone-audit\parse-rig.py "E:\mod\Lunalice_Bill\sectional\physbone-rig.bin"
python tools\physbone-audit\parse-rig.py <文件> --dump-names     # 逐根骨名 ↔ 锚点

# ③ 骨名是不是被复制了多份（期望：每个骨名 1 次 = 共享名字表）
python tools\physbone-audit\count-bones.py "E:\mod\Lunalice_Bill\sectional\9ba626afa44a3aa3.patch_0.gpu_resources"
```

`parse-rig.py` 的字段偏移与 `HD2PhysBoneCompat/src/physbone_compat.lua` 的 `parse_rig`
严格一致（`HEADER=32`、`recordSize=148`、记录内 `+0 index / +4 parent / +8 name / +12 parentName`）。
**改容器格式的字段布局之前，先改 compat，再改这个脚本**，否则两边会静默错位。

运行时侧的对照（进一次游戏，看 `%LOCALAPPDATA%\hd2physbone.log`）：

```
[physbone] attach 进入：rig 57 根骨
[physbone] attach：可用链 39 条（缺节点 0、缺锚点 0）
[physbone] 已挂载 7~9 个 unit / 273~351 条链（… 玩家1=7~9件）
[physbone][预算] 自己 273 链/帧（不分帧）
```

---

## 3. 目标形态

**每根物理骨只加进真正用到它的那个 unit。** 具体：

- 容器本身保持不变：仍然是 **1 份** `physbone-rig.bin`（57 骨 / 39 链）；
- 但**不要把整套骨加进每个 unit 的骨架**：按该 unit 的 sections 实际蒙皮到的骨，
  只把那部分 `source_extra_*` 骨加进它的骨架；
- 每件驱动条数从 39 降到各自用到的条数。

锚点分布已经能整除成套（实测，见 §1.2）：

| 锚点 | 链数 | 该带这些骨的 unit |
|---|---|---|
| `head` | 15 | `...0001`（头发 / 头饰 / 面部） |
| `hips` | 10 | `...0002`、`...0003`（裙摆 / 连衣裙） |
| `spine2` | 6 | `...0002`、`...0003`（上身/胸衣的飘带） |
| `chest` | 4 | `...0006`、`...0007`（袖口 / 手腕缎带） |
| `l_knee` / `r_knee` | 各 2 | `...0004` / `...0005`（腿部缎带 / 袜边） |

**合计正好 39 条链** ⇒ 如果按这个分组切分，"全部 unit 的链数之和"≈39（而不是 7×39=273），
即理论上有 **~7 倍** 的下降空间。

判定"这个 unit 用到哪些骨"的**权威来源**是转换时手里的网格蒙皮数据（vertex → bone 权重）。
`sections.json` 的 `sourceMaterial` 只是启发式线索，可用于交叉校验：

| 材质关键词（`sourceMaterial`） | 大概挂在哪组骨上 |
|---|---|
| `hair`、`headdress`、`face`、`ear` | `head` |
| `skirt`、`dress`、`corset`、`bra`、`choker` | `hips` / `spine2` |
| `tights`、`garter`、`leg`、`shoes`、`highsocks` | `l_knee` / `r_knee` |
| `glove`、`armcuff`、`wristcuff` | `chest` |

⚠️ 启发式只能用来**挑候选**，最终必须以蒙皮数据为准：判错会表现为"某一块布料不动了"
（这正是验收要逐件看的东西，见 §5）。

⚠️ 允许重叠：某个 unit 可能同时需要两组锚点（比如裙摆带子挂在 `chest`）。**不要为了
"不重复"而删掉别的 unit 需要的骨**；目标是"每个 unit 只带自己用到的"，不是"每个骨只出现一次"。

---

## 4. 运行时侧的契约（我会配合改，转换器不用管）

现状与将要改的：

1. `Compat.attach` **本来就能容忍缺骨**：它逐链解析节点，缺节点的链跳过，
   日志会打 `attach：可用链 N 条（缺节点 M、缺锚点 K）`。
   ⇒ **只要 unit 只带自己那部分骨，运行时自然只驱动它那几条链**，不需要额外开关。
2. 但 `tryAttach` 目前要求"**全命中**"（`best.full == true`）才挂载。一旦转换器改成按 unit 切分骨，
   没有任何 unit 能全命中 57 根 ⇒ 必须放宽为"**该 unit 命中 ≥ 1 条完整链就挂**"。
   这个改动在运行时侧做（我已记在待办里），**转换器可以先落地**；两边一起发即可。
3. 容器格式、骨名写法（`j<hex>/source_extra_<n>`）、`HD2SRG01` 头都**不要动**。

---

## 5. 验收标准

### 5.1 离线（转换器自测）

- `HD2SRG01` 仍然只有 **1 个**（在 `role = unit` 的那个 patch 里）；
- 每个 unit 的骨架里，`source_extra_*` 骨的数量 < 57，且各 unit 之间**不相等**（说明真的按需切分）；
- 7 个 unit 的骨数量之和应显著小于 `7 × 57`。

### 5.2 进游戏看日志（关键）

```
[physbone] attach：可用链 N 条（缺节点 M）    ← 每个 unit 的 N 应各不相同、且小于 39
[physbone] 已挂载 7 个 unit / T 条链           ← T 应从 273 级降到几十
[physbone][预算] 自己 X 链/帧（不分帧）         ← X 同 T
心跳：引擎调用 ≈ 2×X + 常数，驱动 ms 应同比下降
```

### 5.3 逐件目视（回归清单）

这套外观的物理分布在 4 个区域，**每块都要单独确认还在动**：

| 区域 | 对应 unit | 检查方式 |
|---|---|---|
| 头发 / 头饰缎带 | `...0001` | 转头、跑动时头发飘动 |
| 裙摆（上段/下段） | `...0002`、`...0003` | 跑动时裙摆摆动 |
| 腿部缎带 / 高筒袜边 | `...0004`、`...0005` | 跑动时腿部布料摆动 |
| 手臂袖口 / 手腕缎带 | `...0006`、`...0007` | 挥动手臂时袖口摆动 |

任何一块从"动"变成"不动"，就是该 unit 的骨切少了 —— 回退那一块，别一刀切。

---

## 6. 风险

1. **切分过窄**：某个 unit 的布料其实绑在"看起来不相关"的骨上（例如裙摆带子挂在 `chest`），
   按材质名猜就会漏。→ 以蒙皮数据为准，不按名字猜。
2. **共享身体网格**：每个 unit 都含 `shinano_body`，身体部分的骨（`hips`/`spine2`）可能被多个 unit
   同时需要 → 允许重叠，不要为了"不重复"而删掉别的 unit 需要的骨。
3. **不要改容器**：运行时按骨名匹配（`has_node(unit, "骨名")`），骨名写法变了就不命中；
   `IdString32` 那条路已经废弃（会失效），别引入。
4. 改完之后**必须两边一起发**（转换器 + 运行时放宽全命中），否则会出现"一个都不挂"。

---

## 附录：三个审计脚本

| 脚本 | 作用 |
|---|---|
| `tools/physbone-audit/scan-containers.py` | 扫描目录下所有 `*.gpu_resources`，报出每个文件里的 `HD2SRG01` 容器数与偏移 |
| `tools/physbone-audit/parse-rig.py` | 解析容器：骨数 / 链数 / 锚点分布 / 源骨骼下标范围；`--dump-names` 逐根列出 |
| `tools/physbone-audit/count-bones.py` | 统计指定骨名在某个 `.gpu_resources` 里出现几次（判断名字表共享/复制） |

三者都是只读的，不需要游戏、不需要部署，可直接对着转换器的输出目录跑。

## 附：本仓实测记录（Lunalice，2026-11-12）

```
scan-containers: 9ba626afa44a3aa3.patch_0.gpu_resources  31552374 B  containers=1  at=0x1e14860
                 该 mod 共有 1 个 HD2SRG01 容器（13 个 patch 文件）
parse-rig     :   v2 / 57 根 / 名字表 offset=8468 size=2506 / recordSize=148
                 链数 39，锚点 {'head': 15, 'hips': 10, 'spine2': 6, 'chest': 4, 'l_knee': 2, 'r_knee': 2}
                 源骨骼下标 64..126，57/57 带命名空间前缀
count-bones   :   具体骨名各 ×1（名字表共享）；HD2SRG01 ×1；source_extra_ 共 75 次（含非容器骨名）
```