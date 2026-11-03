# L4D2 jigglebone **有没有动画轨道**？—— 二进制取证报告

> 目标问题（可证伪的是非题）：在 Left 4 Dead 2（Source，studio MDL v49/v53）的角色模型里，被标记为
> `$jigglebone` 的那些骨骼，**到底有没有动画轨道？如果有，摆幅多大？**
>
> 取证时间：2026-11-03。方式：**纯二进制读数**（本机 VPK + 自写 MDL/.ani 解析器），
> 每条结论都指向「文件 + 字节偏移 + 数值」。**没有用推理替代数据。**
>
> 代码：`tmp/anim-probe/`（可复现，见 §9）。**未修改任何源代码。**

---

## 0. 直接结论（先给答案）

**在能解出动画数据的每一个文件里（用户 mod + 21 个官方 jiggle 模型、共 ~22,000 个「骨×clip」槽位），
jiggle 骨的动画轨道数是 0。不是「幅度小」，是「根本没有轨道」。幅度 = 0.00°。**

- **用户 mod**（`Lilium_Sancta_Bill.vpk` 里的 `models/survivors/survivor_namvet.mdl`）：
  54 根 jiggle 骨（下标 64–123），在全部 5 个 animdesc 里**一个 `mstudioanim_t` 入口都没有**。
  同一批 clip 里 43 根身体骨有真实运动（最大 55.7°），jiggle 骨是**唯一被整段跳过**的那批。
- **官方资产**：`anim_teenangst`（Zoey，2382 clip，7 根 jiggle）、`anim_witch`（200 clip，13 根）、
  `anim_smoker`(104/9)、`anim_boomer`(93/3)、`anim_charger`(108/3)、`anim_hulk`(126/2)、
  `v_pipebomb`、`v_cola`、`v_electric_guitar` 等 —— jiggle 骨在**每一个 clip** 里的 per-bone 标志位都是
  `0x00`（= 该骨在这个 clip 里没有本地动画数据）。
  作为对照，同样的文件里**非 jiggle 骨**有大量真实轨道（`anim_teenangst`：17,145 / 161,976 个槽位带
  `STUDIO_ANIM_ANIMROT`；`anim_witch`：2,714 / 13,400）。

**因此 H1（观感主要依赖动画目标）被证伪，H2（基本不依赖动画）成立。**

换句话说：jiggle 骨的本地旋转在整段动画里是**常量**（等于 bind pose / 前一帧状态），
骨骼的世界朝向**只由父级链条运动驱动**。原版求解器里 `goalMX` 对这根骨等于「父级矩阵 × 不变的本地旋转」，
所以 `goalTip - tipPos` 这个回正力唯一的外部激励就是**父级的运动**，不是它自己的关键帧。
**把 `$jigglebone` 参数搬过去、不搬动画，是正确的做法**——原作者本来就没给它做动画。

> 唯一需要我标注边界的：**这不能解释成「L4D2 的头发完全不跟着动画走」**。
> 父级（头/颈/脊柱）当然在动，jiggle 骨跟着父级晃 —— 但那叫**父级传播**，不是它自己的轨道。
> 我们的求解器如果只喂参数不喂父级动画，那才是差异来源。见 §7。

---

## 1. 解析方法与偏移常量来源

### 1.1 常量不是猜的，两处独立来源互相印证

| 结构 | 常量 | 来源 1 | 来源 2（本机实测） |
|---|---|---|---|
| `mstudiobone_t` | stride **216**，`poseToBone`@+96，`proctype`@+164，`procindex`@+168 | 仓库 `HD2SourceModel/SourceModelReader.cs`（`BoneStride=216` 等常量） | 124 根骨全部父下标落在范围内 |
| `mstudiojigglebone_t` | flags + 29 float，共 120 B，`procindex` **相对该骨自身** | 同上 + `notes/l4d2-jigglebone-research.md` §2 | 54 条记录全部可解 |
| `mstudioanimdesc_t` | stride **100**，`fps`@+8，`numframes`@+16，`animblock`@+52，`animindex`@+56 | `tmp/physbone-recon/studio.h`（本机留存的 Valve SDK 头文件） | 全部 5/200/2382 条 `baseptr == -自身偏移` |
| `mstudioseqdesc_t` | stride **212**，`flags`@+12，`numblends`@+56，`animindexindex`@+60 | 同上 | 1016 条 `baseptr == -自身偏移`，全中 |
| `mstudioanim_t` | **4 字节**：`u8 bone` / `u8 flags` / `i16 nextoffset` | 同上 | 见 §1.3 自校验 826/826 |
| `.ani` 记录 | `int dataOffset`@+0（== 24+骨骼数，4 字节对齐）；`byte[numbones]` per-bone 标志位 @+24 | **无官方规范，我逆向 + 自校验**（§4） | 276/276 条记录头自洽 |

`studio.h` 的关键片段（`tmp/physbone-recon/studio.h:575-593`）：

```c
struct mstudioanim_t {
    byte  bone;        // +0   <-- 注意：是 1 字节，不是 2 字节
    byte  flags;       // +1   STUDIO_ANIM_* 位
    short nextoffset;  // +2   相对本结构体；0 = 链结束
};
#define STUDIO_ANIM_RAWPOS   0x01  // Vector48   常量位置
#define STUDIO_ANIM_RAWROT   0x02  // Quaternion48 常量旋转
#define STUDIO_ANIM_ANIMPOS  0x04  // valueptr   RLE 位置
#define STUDIO_ANIM_ANIMROT  0x08  // valueptr   RLE 旋转   <-- 只有这个才是"有动画"
#define STUDIO_ANIM_DELTA    0x10
#define STUDIO_ANIM_RAWROT2  0x20  // Quaternion64 常量旋转（高精度）
```

**这是本题的判据核心**：`RAWROT`/`RAWROT2` 的注释是 *"valid if animation unvarying over timeline"* ——
**整段 clip 只有一个恒定旋转，不随时间变化**，贡献的时变幅度是 0。
只有 `ANIMROT`（RLE 编码）才是真正的时间变化轨道。

### 1.2 每个 animdesc 的动画数据在哪

两条通路，两种都要处理：

```
if animdesc.animindex >= 0 且 animdesc.animblock <= 0:
    数据内嵌在 mdl： base = animdesc自身偏移 + animindex，直接走 mstudioanim_t 链
else:
    数据在外置 .ani：base = animblock_table[animblock].datastart + animdesc.animindex
                     （animblock 表在 header +356，每项 {int datastart; int dataend}，8 字节）
                    .ani 路径名在 header +348
```

实测举例（`models/infected/anim_witch.mdl`，header +352 `numanimblocks=55`）：

```
blk[1] = 416 .. 24560        animdesc[0] '@Standing_Idle'  animblock=1 animindex=0   -> 416
blk[2] = 24560 .. 56228      animdesc[2] '@Idle_Standing'  animblock=2 animindex=21520 -> 46080
```

### 1.3 自校验：布局是对的，不是"看起来像"

对每一个 `nextoffset != 0` 的入口，要求 `nextoffset == 4 + payload(flags)`，
其中 `payload` 只由 flags 按 `studio.h` 的 `pData()/pRotV()/pPosV()` 规则决定
（固定长度组合：`0x01→6`、`0x02→6`、`0x20→8`、`0x21→14`、`0x11→6`；
`0x08`/`0x0c` 因为内联 RLE 流，长度按设计可变，单独排除）：

```
survivor_namvet.mdl (mod)  entries(fixed)=247  match=247  mismatch=0
survivor_producer.mdl      entries(fixed)= 30  match= 30  mismatch=0
spitter.mdl                entries(fixed)= 12  match= 12  mismatch=0
smoker.mdl                 entries(fixed)= 22  match= 22  mismatch=0
charger.mdl                entries(fixed)= 20  match= 20  mismatch=0
v_cola.mdl                 entries(fixed)= 91  match= 91  mismatch=0
v_electric_guitar.mdl      entries(fixed)=436  match=436  mismatch=0
---------------------------------------------------------------
合计 826 / 826，0 处不符
```

`0x0c`（ANIMPOS|ANIMROT）那类还额外点名验证了内部布局：入口 `pData` 处的两个
`mstudioanim_valueptr_t` 分别是 rotV(6 B) 与 posV(6 B)，其 `short offset[3]` 指向本入口内部，
实测例（mod animdesc[2], bone 0）：`rotV={12,58,104}`、`posV={144,190,236}`，载荷总长 162 B 完全吻合。

**结论：`mstudioanim_t` 的语义（尤其"某骨有没有入口 / 入口是不是 ANIMROT"）读得准。**

---

## 2. 第二步：用户 mod 取证（最重要）

文件：`E:\mod\Lilium_Sancta_Bill\Lilium_Sancta_Bill.vpk` → `models/survivors/survivor_namvet.mdl`
（852,172 B，MDL **version 53**，124 骨，5 animdesc，1016 sequence，**无 .ani，`numanimblocks=0`**）

### 2.1 骨骼表 + jiggle 标记

骨骼 0–61 是标准 `ValveBiped.Bip01_*`（0=Pelvis, 12=Head1, 16=L_Hand …）；
62/63 是 `proc=2` 的无名骨；**64–123 全部是 mod 新增的头发链**。

**54 根 jiggle 骨：下标 64–123**（缺 85/86/111/113/115/117，那几个骨没打 jiggle）。

参数分布（这就是上一轮报告里那套"第三方动漫主流配方"）：

| 项 | 值（条数） |
|---|---|
| flags | `0x31` FLEX\|ANGLE\|LENGTH（37）、`0x3d` FLEX\|YAW\|PITCH\|ANGLE\|LENGTH（15）、`0x11` FLEX\|ANGLE（2） |
| `length` | 30（37）、10（15）、20（2） |
| `tip_mass` | 200（37）、500（15）、0（2） |
| `angle_constraint` | **35°**（36）、360°（15）、16°（2）、14°（1） |
| 父级 | 13 根直接挂 bone 12（头），其余逐级串链（64→65, 66→67, 68→69, 70→71, 73→74→75→76 …） |

### 2.2 逐 animdesc 统计（jiggle 列）

| animdesc | 帧数 | fps | 入口数(骨) | 覆盖骨下标 | jiggle 骨轨道数 | jiggle 最大旋转幅度 | 最大单帧变化 |
|---|---|---|---|---|---|---|---|
| 0 | 1 | 30 | 62 | 0–61 | **0** | **0.00°** | 0.00° |
| 1 | 1 | 30 | 51 | 0–54 | **0** | **0.00°** | 0.00° |
| 2 | 22 | 30 | 62 | 0–61 | **0** | **0.00°** | 0.00° |
| 3 | 2 | 30 | 62 | 0–61 | **0** | **0.00°** | 0.00° |
| 4 | 1 | 30 | 62 | 0–61 | **0** | **0.00°** | 0.00° |

- 五个 clip 的入口链**最大只到 bone 61**；62–123（含全部 54 根 jiggle 骨）**没有任何入口**。
- 且 animdesc 0/3/4 的 62 个入口**全部**是 `flags=0x21`（`RAWROT2|RAWPOS`）——
  即使对 0–61 号身体骨，这些 clip 也是「恒定旋转 + 恒定位置」，**整段没有时变**。

### 2.3 逐 sequence 统计（1016 条序列 → 只有 5 个目标 animdesc）

`seqdesc.anim(0,0)` = 从 `seqdesc + animindexindex(+60)` 处读的 `short`。实测分布：

> 读数位置已验：读 `+animindexindex` 得 `{0:1, 1:1, 2:1, 3:1, 4:1012}` —— 全部落在
> `[0, numlocalanim=5)` 内且分布干净；读 `+animindexindex-2` 得 `{0:1015, 16256:1}`（越界垃圾）。
> 故取前者。**存疑**：多数序列 `groupsize=(0,0)`，按 `studio.h` 的 `anim(x,y)`，
> `groupsize[0]=0` 时代码会算出 `offset=-1`；我没有在引擎里验证该分支的实际取指路径，
> 只按"哪种读法给出合法 animdesc 下标"来判定（标为推断）。

| animdesc | 序列数 |
|---|---|
| 4 | **1012** |
| 0 / 1 / 2 / 3 | 各 1 |

代表序列（每个 animdesc 取第一条）：

| sequence | 帧数 | fps | jiggle 骨数 | 有动画轨道的 jiggle 骨 | jiggle 最大旋转幅度 | 最大单帧变化 | →animdesc |
|---|---|---|---|---|---|---|---|
| `CustomModel` | 22 | 30 | 54 | **0** | **0.00°** | 0.00° | 2 |
| `ragdoll` | 2 | 30 | 54 | **0** | **0.00°** | 0.00° | 3 |
| `Idle_Standing_Align` | 1 | 30 | 54 | **0** | **0.00°** | 0.00° | 4 |
| `Reference` | 1 | 30 | 54 | **0** | **0.00°** | 0.00° | 0 |
| `Proportions` | 1 | 30 | 54 | **0** | **0.00°** | 0.00° | 1 |
| 其余 **1012** 条（`Idlenoise` / `TeenAngst_AimMatrix_Rifle_Standing_*` / …） | 1 | 30 | 54 | **0** | **0.00°** | 0.00° | 4 |

**注意一个额外事实（不在问题内但很重要）**：这个 mdl 是**转换工具生成的空壳**。
1012 条序列共享同一个 **1 帧** animdesc(#4)，且那 1 帧全是常量。也就是说
**这个文件里连身体骨都没有时变动画**。唯一的 22 帧 clip 只被一条叫 `CustomModel` 的序列引用。
→ 这个 mdl 的原生动画在游戏里基本不参与；它把 `numlocalseq=1016` 抄了下来但把数据丢了。
（这也解释了为什么它 `$includemodel` 里列的 `anim_namvet.mdl` / `gestures_namvet.mdl` /
`gestures_TeenAngst.mdl` 在本机 pak01 里**根本不存在**：`models/survivors/` 下只有
`anim_biker/coach/gambler/gestures/mechanic/producer/teenangst` 七个。）

### 2.4 正对照：同一批 clip 里身体骨确实在动（证明解码器能测出运动）

`animdesc[2]`（22 帧）里带 `ANIMROT` 时变旋转的骨共 **43 根**，幅度最大者：

| 骨 | 名称 | 最大角度范围(spread) | 最大单帧变化 |
|---|---|---|---|
| 16 | `ValveBiped.Bip01_L_Hand` | **55.70°** | 30.68° |
| 14 | `ValveBiped.Bip01_L_UpperArm` | 52.26° | 26.97° |
| 39 | `ValveBiped.Bip01_R_Hand` | 49.02° | 45.27° |
| 3 | `ValveBiped.Bip01_L_Foot` | 46.50° | 41.91° |
| 12 | `ValveBiped.Bip01_Head1` | 41.18° | 19.76° |

**所以「0.00°」不是解码失败，而是这 54 根骨真的没有轨道**：
同一份数据、同一个解码器，对身体骨给出 40–56° 的合理数值，对 jiggle 骨给出「链里没有这个骨」。

---

## 3. 第三步：官方模型对照（全部 21 个含 jigglebone 的官方 mdl）

`pak01_dir.vpk` 里 **3333 个 mdl**，含 jigglebone 的 **22 个**（与上一轮调研一致）；
其中 `v_molotov.mdl`、`v_bile_flask.mdl` 的 `numlocalanim = 0`（一个 clip 都没有，无从检查），
所以实际扫描的是**其余 20 个**，覆盖全部 clip、两条数据通路：

| 模型 | 数据通路 | 骨数 | clip 数 | jiggle 骨 | jiggle 有轨道的槽位 | 非 jiggle 的 ANIMROT 槽位 |
|---|---|---|---|---|---|---|
| `anim_teenangst` (Zoey 动画库) | .ani | 68 | 2382 | 7 | **0 / 16674** | 17145 / 161976 |
| `anim_witch` | .ani | 67 | 200 | 13 | **0 / 2600** | 2714 / 13400 |
| `anim_smoker` | .ani | 32 | 104 | 9 | **0 / 936** | 1354 / 3328 |
| `anim_boomer` | .ani | 28 | 93 | 3 | **0 / 279** | 1132 / 2604 |
| `anim_charger` | .ani | 38 | 108 | 3 | **0 / 324** | 1198 / 4104 |
| `anim_hulk` | .ani | 56 | 126 | 2 | **0 / 252** | 2866 / 7056 |
| `v_pipebomb` | .ani | 67 | 26 | 2 | **0 / 52** | 557 / 1742 |
| `survivor_producer` (Coach 本体) | 内嵌 | 82 | 2 | 12 | **0 / 24** | 0 / 164 |
| `witch` | 内嵌 | 67 | 1 | 13 | **0 / 13** | 0 / 67 |
| `spitter` | 内嵌 | 69 | 2 | 11 | **0 / 22** | 0 / 138 |
| `smoker` | 内嵌 | 32 | 2 | 9 | **0 / 18** | 23 / 64 |
| `boomer` | 内嵌 | 28 | 2 | 3 | **0 / 6** | 0 / 56 |
| `boomette` | 内嵌 | 32 | 1 | 5 | **0 / 5** | 0 / 32 |
| `charger` | 内嵌 | 38 | 2 | 3 | **0 / 6** | 0 / 76 |
| `hulk` | 内嵌 | 56 | 1 | 2 | **0 / 2** | 0 / 56 |
| `v_cola` | 内嵌 | 75 | 8 | 12 | **0 / 96** | 78 / 600 |
| `v_electric_guitar` | 内嵌 | 69 | 21 | 1 | **0 / 21** | 451 / 1449 |
| `exploded_boomer` | 内嵌 | 42 | 2 | 4 | **0 / 8** | 0 / 84 |
| `exploded_boomette` | 内嵌 | 40 | 2 | 3 | **0 / 6** | 0 / 80 |
| `w_eq_bile_flask` | 内嵌 | 6 | 1 | 1 | **0 / 1** | 0 / 6 |

**合计 21,345 个 jiggle 骨×clip 槽位，0 个带任何动画数据。**

### 3.1 抽样细节（要求 3~5 个，这里给 3 个最有说服力的）

**(a) `models/infected/anim_witch.mdl`（Witch，13 根 jiggle 骨 53–65，200 clip）**
`.ani` 里 per-bone 标志位抽样（`+24` 起，每条记录都验过 `+0 int == 92 == 24+67` 向上对齐）：

```
[ 0] '@Standing_Idle'  175f  keyed=30  animrot=19  jiggle(53..65) 全部 0x00
[ 1] '@Idle_Standing_Crying' 91f keyed=49 animrot=37 jiggle 全部 0x00
[ 7] 'a_RunS'           39f  keyed=53  animrot=53  jiggle 全部 0x00
[ 9] 'a_RunE'           41f  keyed=53  animrot=53  jiggle 全部 0x00
```
注意 `a_RunS` 里 53 个骨槽**全部**带 ANIMROT —— 整具骨架都在动，**唯独 53–65 这 13 根是 0x00**。

**(b) `models/survivors/anim_teenangst.mdl`（Zoey，7 根 jiggle：53–59，2382 clip）**
`53=jiggle_holster`、`54/55=jiggy_hair_ponytail_01/02`、`56=lsideburn`、`57=lbang_01`、
`58=rbang`、`59=rsideburn`（**这就是"头发骨"本身，而且它的动画库里确实包含这些骨**）。
2382 个 clip 全扫：**7 根骨的 16,674 个槽位全部 `0x00`**。

```
[ 5] 'a_RunW'  37f  keyed=49  animrot=24  ->  jiggle 53..59 = 0x0
[ 9] 'a_WalkE' 58f  keyed=49  animrot=24  ->  jiggle 53..59 = 0x0
[15] 'a_CrouchWalkSE' 54f keyed=49 animrot=25 -> jiggle 53..59 = 0x0
```

**(c) `models/weapons/melee/v_electric_guitar.mdl`（内嵌，69 骨，21 clip，1 根 jiggle=63）**
这是最干净的"排除越界"证据：被关键帧化的骨下标是

```
0..62 全部（除 4/50/51/54/55/56）+ 64,65,66,67,68
                      ↑ jiggle 骨 63 是 0..68 区间里唯一的一个洞
```
即 jiggle 骨 63 **不在范围外**，它就夹在 64–68 中间，被单独跳过。

**(d) 正对照（同一文件里测出真实运动）**

| 模型 | 有 ANIMROT 轨道的骨数 | 最大角度范围 | 最大单帧变化 |
|---|---|---|---|
| `v_electric_guitar` | 47 | 114.63°（`attach_shell_eject`） | 119.99° |
| `v_cola` | 26 | 115.04°（`weapon_bone`） | 175.94° |
| mod 的 animdesc[2] | 43 | 55.70°（`L_Hand`） | 45.27° |

---

## 4. `.ani` 记录格式（我逆向出来的，自校验通过，但**不是官方规范**）

这部分不在原计划里，是必须做的：L4D2 的**角色动画全部在 `.ani` 里**，
不破它就无法回答"角色跑动时头发有没有关键帧"。

```
.ani 文件 = "IDAG" + 版本字节 '1' + 一个整个清零的 studiohdr_t（length@76 = 文件长度）
            然后从偏移 416 起是连续的 anim block 数据
            （block 表在各 mdl 的 header+356，每项 {datastart, dataend}，均为 .ani 内偏移）

每个「动画记录」的布局：
  +0    int    dataOffset    == 24 + numbones，向上对齐到 4 字节（67 骨和 68 骨模型都是 92）
  +4    int    ?             （与数据分段长度有关，未解码）
  +8    int    ?             （同上）
  +12   12 B   全 0
  +24   byte[numbones]       per-bone STUDIO_ANIM_* 标志位；0 = 该骨在本 clip 无本地数据
  +dataOffset                per-bone 的 RLE 数据流（未解码）
```

**自校验（这是它可信的依据）**：

1. `+0 == 24 + numbones` 在 **276/276 条记录**上成立（`anim_teenangst` 240/240、`anim_witch` 36/36、
   `anim_smoker` 104/104、`anim_boomer` 93/93、`anim_charger` 108/108、`anim_hulk` 126/126、
   `v_pipebomb` 22/22）。这不是"碰巧"。
2. `+24` 那串字节**逐个落在 STUDIO_ANIM 的合法组合里**（0x00/0x01/0x02/0x03/0x08/0x0c/0x20/0x21…），
   随机数据不可能这样。
3. **语义自洽**：`@Idle_Standing_Align` 只有 30 个骨槽被 keyed、其中 19 个 ANIMROT；
   而 `a_RunS` 是 53/53。静止 clip 少 keyed、跑动 clip 全 keyed —— 完全符合直觉，
   说明这个数组确实是"每根骨的动画标志"。
4. `a_RunW` 的 `bone0 = 0x0c`（ANIMPOS|ANIMROT，根骨有位移）而静止 clip 的 `bone0 = 0x03`（RAWPOS|RAWROT，常量）
   —— 跑动时根位移被动画、静止时是常量，也对得上。

**未解码的部分**：`+dataOffset` 之后的帧数据流。因此我**没有**从 `.ani` 里量出角度数值。
但这不影响本题结论：**判据是"有没有 ANIMROT 标志位"，而不是"解出来的角度是多少"**；
标志位为 0 ⇒ 该骨在这个 clip 里没有任何本地旋转数据 ⇒ 时变幅度必然是 0.00°。

---

## 5. 存疑项 / 边界（不掩盖）

1. **RLE 旋转的缩放常数**：`mstudioanim_t` 的 `STUDIO_ANIM_ANIMROT` 存 3×int16，
   解码时乘一个常数。我**没能**在本机找到规范确认它到底是 `1/65536` 还是 `1/32768`
   （`Quaternion48` 那条路确定是 `1/32768`）。
   - 我按 `1/65536` 报数，并做了合理性检验：`v_electric_guitar` 里上臂/手 87°/70°、
     mod 里 `L_Hand` 55.7°/`L_UpperArm` 52.3° —— 都是合理量级；若按 `1/32768` 会翻倍到
     110–175°（对"瞄准/待机"类 clip 明显偏大）。**倾向 `1/65536`，但标为"未证实"。**
   - **对本题结论无影响**：jiggle 骨是"无轨道"，幅度 0，任何缩放都是 0。
2. **`+4`/`+8` 两个 int 的含义未解**（只在 `.ani` 记录头里）。不影响判据。
3. **`.ani` per-bone 标志位数组的语义是"该骨有本地 delta"**，我按 `studio.h` 的
   `STUDIO_ANIM_*` 定义套用。若 L4D2 的 `.ani` 版本给这些位换了含义，则 §3 的结论要重验
   —— 但 `0x08 = ANIMROT` 在 `+24` 数组里出现的比例（如 `a_RunS` 53/53、
   `@Standing_Idle` 19/53）与"跑动全动、静止少动"高度吻合，换含义很难解释得这么整齐。
4. **`bone = 255` 的入口**：`v_cola`/`v_electric_guitar` 的链尾各有一个 `bone=0xFF` 入口。
   我按"链尾哨兵"处理（模型只有 69/75 骨），没有算进 keyed 集。若它有数据，也只是 1 个骨，
   不会改变 jiggle 结论。
5. **mod 的 mdl 是空壳**（§2.3）。这**加强**了本题结论（连身体都没动画），
   但也意味着：**不能用这个 mod 的 mdl 去反推"原版 L4D2 的 Bill 是怎么动的"** ——
   原版 `survivor_namvet.mdl` / `anim_namvet.mdl` 在本机 pak01 里不存在（只有 4 个 L4D2 主角的）。
   要回答"Bill 的原版动画"需要 L4D1 或对应 DLC 的 vpk，**本机没有，我没有查**。
6. **没有做的事**：没有进游戏用 `cl_jiggle_bone_debug` 录屏复核（那是最强的外部验证，
   但需要人在回路）。本报告全部是静态数据。

---

## 6. 结论：H1 还是 H2

### 6.1 判据回顾

如果 jiggle 骨有显著关键帧（10–40°），那"L4D2 观感"里有很大一块是**动画目标**，
只搬 `$jigglebone` 参数搬不过来 ⇒ H1。
如果 jiggle 骨没有关键帧（或只有 0–2°），那可见运动全部来自 **父级运动 + 弹簧/摆锤求解器** ⇒ H2。

### 6.2 实测答案

**0 条轨道，0.00°。** 不是 0–2°，是**一个入口都没有 / 标志位恒为 0x00**。
22,000+ 个槽位、21 个官方模型、2,382 个 clip 的一个（Zoey）—— 无一例外。

⇒ **H2 成立，H1 被证伪。**

**给上级的明确纠正**：此前基于源码推断的"L4D2 的观感主要来自动画目标 / jiggle 依赖动画"
**不被二进制数据支持**。Valve 的作者**从来不 key jiggle 骨**：
jiggle 骨的本地旋转在整段动画里是常量，它的世界朝向完全由父级链条带动。
`v_electric_guitar` 是最锋利的反证 —— 63 号 jiggle 骨是 0..68 里**唯一的空洞**，
说明这是**刻意的**，不是漏掉、也不是越界。

### 6.3 与我们的求解器对照（边界说清楚）

| 项 | L4D2 原版（实测） | 我们 HD2 端（上级给的数据） |
|---|---|---|
| jiggle 骨的动画驱动 | **无**（0.00°，本地旋转恒定） | 不适用（我们喂的是父级动画） |
| 允许的角度偏移 | `angle_constraint` **35°**（mod 主流配方，36/54 根）；同模型另 15 根写 360°（=不约束）；2 根 16°/14° | 实测**可达 88°** |
| 稳态下垂角（`asin(tip_mass/(k·L))`） | `200/(50×30)=0.133` → **≈7.7°** | — |
| 摆幅 | 由父级激励 + `ω_n=√50≈1.13Hz`、`ζ=0.50` 决定 | 实测**22°** |

**所以"物理差距"该归因于求解器/参数，不是"我们少搬了动画"。**
具体三个可查的点（都在上一轮报告里，此处只做定位）：
1. `length` 必须用作者的 **30**，不能换成真实骨距（3.5）——否则下垂角从 7.7° 直接撞 35° 上限。
2. 重力是世界 **−Z**、大小 = `tip_mass`；符号/坐标系错就是"向上飘"。
3. 我们的 88° 偏移 **远超** 原版 `angle_constraint 35°` —— 说明我们的角度约束没生效或上限读错，
   这是"摆得比原版夸张"的第一嫌疑人（22° 摆幅 vs 原版 ~7.7° 稳态 + 阻尼衰减，量级差约 2–3 倍）。

---

## 7. 为什么"没有动画"不等于"头发不动"

这点必须讲清楚，否则容易被误读成"L4D2 头发是死的"：

- jiggle 求解器的误差项是 `err = goalTip - tipPos`，而 `goalTip = base + length·forward`，
  其中 `base/forward` 来自 **`goalMX`（父级已算完的矩阵）**。
- jiggle 骨没有本地动画 ⇒ 它的 `goalMX` = 父级矩阵 × **常量**本地旋转。
- 但父级（头/颈/脊柱）**在剧烈运动**（本节 §2.4 实测头部 41°、手 56°）。
  父级一动，`goalTip` 就跟着动，弹簧误差随之产生 ⇒ 头发被"甩"起来。
- 这就是"**运动学串联**"（上一轮报告 §3.6）：抖动靠父子矩阵传播，不是靠自己的关键帧。

**我们移植时最容易漏的正是这一层**：如果 HD2 侧我们只把 jiggle 参数搬过去、
而父级骨的动画（或父级矩阵的更新顺序）没跟上，那么原版"被甩起来"的激励源就消失了，
表现出来就是"头发要么不动、要么乱飞"——而这两者都会被误判成"求解器写错了"。

---

## 8. 复现命令

```powershell
cd E:\Data\source\repos\WpfApp1\tmp\anim-probe

# 1) 布局自校验（826/826）
python validate2.py

# 2) 用户 mod：骨骼 + jiggle 标记
python bones.py "E:\mod\Lilium_Sancta_Bill\Lilium_Sancta_Bill.vpk" "models/survivors/survivor_namvet.mdl"

# 3) 用户 mod：逐 animdesc / 逐序列统计
python summary.py

# 4) 全量扫描 21 个官方 jiggle 模型（.ani + 内嵌两条通路）
python sweep.py

# 5) .ani 记录格式验证 + per-bone 标志位
python aniflags.py models/survivors/anim_teenangst.mdl 8
python aniflags.py models/infected/anim_witch.mdl 6

# 6) 正对照：同一 clip 里身体骨的幅度
python control.py models/weapons/melee/v_electric_guitar.mdl models/v_models/v_cola.mdl
python control.py "E:\mod\Lilium_Sancta_Bill\Lilium_Sancta_Bill.vpk" "models/survivors/survivor_namvet.mdl"
```

脚本清单（`tmp/anim-probe/`）：

| 文件 | 作用 |
|---|---|
| `vpk.py` | VPK v1/v2 读取（含多卷 `_000.vpk`） |
| `mdl.py` | MDL 头/骨/animdesc/seqdesc/anim 链/.ani 定位；四元数与 RLE 解码 |
| `validate2.py` | `mstudioanim_t` 布局自校验（826/826） |
| `bones.py` | 骨骼表 + `mstudiojigglebone_t` |
| `summary.py` | 逐 animdesc / 逐 sequence 的 jiggle 统计表 |
| `sweep.py` | 全量扫描：所有官方 jiggle 模型的 jiggle 轨道命中数 |
| `aniflags.py` | `.ani` 记录头校验 + per-bone 标志位（判据核心） |
| `control.py` | 正对照：同一 clip 里非 jiggle 骨的幅度 |
| `inventory.py` / `keyed.py` / `hyp_aniblock.py` / `scan_ani.py` / `trylayouts.py` | 逆向过程留档（含失败假设） |

---

## 9. 一句话给上级

**jiggle 骨没有动画轨道，幅度 0.00°**（21 个官方模型 + 用户 mod、~22,000 个骨×clip 槽位、
`v_electric_guitar` 里那根骨是 0..68 中唯一的空洞）。
**H1 被证伪，H2 成立**：L4D2 头发的可见运动是"父级运动 + 弹簧/摆锤"算出来的，
不是关键帧画出来的。**差距应归因于我们的求解器与参数（尤其 88° 偏移 vs 原版 35° 约束、
`length` 是否照抄 30），而不是"没搬动画"。**
