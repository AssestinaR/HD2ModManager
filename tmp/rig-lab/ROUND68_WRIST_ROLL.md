# 第 68 轮 · 腕缝拧转撕裂（接第 67 轮同一会话）

## 用户报告（第 67 轮实机）

> 本轮我已测试，但我发现遇到需要运动手臂和手腕的动作时，会出现手腕和手臂小臂连接手腕的
> 区域移动的方向略有连接不上的情况……就像是手腕多移动了一点或者是小臂多旋转了一点，
> 导致容易出现误差，误差的距离有时能多到一个手腕的距离，有时候则较少，常规放松姿态则正常没有偏移

> 我仔细查看了一下，看起来大多数时候误差很严重的原因是可能是手腕旋转了 180 度，
> 而小臂好像没旋转一样，但我也不太敢保证

> 我明白了，看起来我们这缺了一份骨骼？（Blender 截图，红圈标出腕部）
> 图片是正视图，不同的颜色分别是不同的纹理区域

## 结论一：**不缺骨**

用户的假设不成立，但指向了正确的地方。

* 工作骨架与导出 unit **都是 107 根、1:1 对应**（差异只有 10 根未命名骨：
  工作骨架叫 `bone_N`、unit 里显示 `0x…`）。
* HD2 骨架里**有 twist 骨**：`l/r_shoulder_twist`、**`l/r_hand_twist`**、
  `l/r_forearm_twist`、`l_wrist_twist`、`l/r_twist_joint`。
  承载 unit 的 `l_hand_twist` = slot 64、`r_hand_twist` = slot 70，
  `parent = l_elbow / r_elbow`，head 与 `l_hand` / `r_hand` **完全重合**（叶子骨）。
* 原版左臂护甲 unit `unit_0x4a213e0ae8acf74b` 的调色板 =
  `['chest','l_clavicle','l_shoulder','l_elbow','l_hand_twist','l_hand',…手指…,'l_shoulder_twist']`
  —— **明确包含 `l_hand_twist`**，权重 74.5。原版**没有** `l_forearm_twist`。
* 原版右臂 `unit_0x24a740753a92e788` 调色板 = `['r_shoulder_twist','r_elbow','r_hand_twist','r_hand']`。
* 层级逐项比对（原版 vs 我们）：
  * 原版 `l_elbow → children: [l_arm_culling, l_hand, l_hand_twist]`
  * 我们 `l_elbow → children: [l_hand, l_hand_twist, 0xa5c8115b]`
  * 两边 `l_hand_twist parent=l_elbow head=(-0.6566,0.1401,1.2189) children=[]` **完全一致**。

⇒ **骨架没缺**。真正的问题是「**我们对 `l_hand_twist` 零权重**」+「**第 67 轮把 hand 权重带收得太窄**」。

**同时推翻第 67 轮写进 README / 记忆的「HD2 没有 twist 骨」——那条是错的。**

用户截图里红圈标出的 `2781352283` = `0xA5C8115B` 是**小臂区域**的未命名骨（位于肘与手之间），
与 twist 无关。

## 结论二：真因 = 权重带比一条网格边还短

`_probe_wrist_roll.py`（180° 纯拧转 `l_hand`，测网格边长度变化）：

| | 最差边 t/L | 两端 hand 权重 | 半径 | JUMP |
|---|---|---|---|---|
| 第 67 轮 | 0.798 / 0.912 | **0.045 / 0.990** | 42 / 44 mm | **83.2 mm** |
| 第 66 轮 | 0.912 / 0.872 | 0.991 / 0.983 | 62 / 84 mm | 41.3 mm |

* 第 67 轮：`smoothstep(0.78L, 0.92L)` = **40 mm** 宽的带，而那条边的 t 跨度是
  `0.114 × 287.4 = 32.8 mm` —— **一条边横跨整条带**，0.945 的权重差全挤在它身上 ⇒ 真撕裂。
* 第 66 轮：宽带，最差边两端权重几乎相等 ⇒ 那 41.3 mm 是**纯几何**
  （两端半径差 22 mm，`2 × 22 ≈ 44`）。

⇒ **第 66 轮把「拧转」做对了、「弯折」做错了；第 67 轮把弯折修好、把拧转弄坏了。**

`_probe_forearm_action.py` 的盲点：它只测**腕部弯折**（绕横轴 30°），
**从未测过拧转/旋前**（绕小臂长轴）。第 67 轮"0.0/0.0 mm 通过"只证明弯折没问题。

## 结论三：几何地板 = 37.8 mm

180° 拧转下，即使一条边**两端权重完全相同**，长度也变化 `2 × 垂直分量`。
本模型最差的等权边（`comp 3772`，半径 47 / 28 mm，`w 1.000/1.000`）给出 **37.8 mm**。
**任何权重方案都改不掉这 37.8 mm。**

## 结论四：twist 骨救不了远端

候选 D（`--twist-frac 1.0`，远端全挂 `l_hand_twist`）：

| 假设游戏怎么转 | cand-D 结果 |
|---|---|
| 只转 `l_hand` | **157.4 mm**（腕口 1.0 → 0.0 硬跳变，**比原来更糟**） |
| 两个都转 | 37.8 mm |
| 只转 `l_hand_twist` | 143.2 mm |

而 `--twist-frac 0`（远端全挂 `l_hand`）在**两种假设下都是 37.8 mm**（因为 twist 权重为 0，
结果与假设无关）⇒ **零风险的稳健选择**。

判据：`l_hand` 必然携带拧转 —— 手指是 `l_hand` 的子骨，若 `l_hand` 不转，手指就不转，手会明显错。
所以远端挂 `l_hand` 一定有效。

## 定稿：`smoothstep(0.50L, 1.00L, t)`，`twist_frac = 0`

参数扫描（左 / 右）：

| a | 拧转 180° 最差边 | 弯折 45° 最差边 | 小臂中段位移(30°) |
|---|---|---|---|
| 第 67 轮 0.78 | 83.2 / 100.0 mm | 30.7 / 39.9 | 0.0 / 0.0 |
| 0.40 | 37.8 / 37.8 | 36.8 / 36.9 | 25.4 / 25.6 |
| 0.45 | 37.8 / 37.8 | 35.0 / 35.5 | 21.6 / 22.0 |
| **0.50** | **37.8 / 37.8** | 32.8 / 33.7 | 17.1 / 17.8 |
| 0.55 | 37.8 / 42.1 | 30.0 / 31.4 | 12.1 / 13.1 |
| 0.60 | 37.8 / 48.2 | 26.5 / 28.5 | 6.6 / 7.8 |
| 0.65 | 42.1 / 55.5 | 23.2 / 24.7 | 1.7 / 2.7 |
| 0.70 | 48.8 / 62.2 | 23.3 / 23.3 | 0.0 / 0.0 |

选 0.50 的判据（不是挑最小的 MAX，MAX 是单条边的抽签）：

* `p99` 在 a ≤ 0.60 时**基本不变**（25.4 ~ 26.1 mm），说明"整体"早就到位了；
* 区别只在**那一条最差边**。a = 0.50 是**唯一**两侧「最差**权重驱动**边」都低于几何地板的取值
  （左 32.7 mm、右 37.2 mm，均 < 37.8）⇒ 残余误差**全部是几何**，一条权重撕裂都不剩。
* a = 0.50 的代价：中段 17.1 mm（第 67 轮 0.0、第 66 轮 52.1）。这是**本版唯一的取舍**。

`_probe_wrist_roll.py` 交付包实测：左右两侧最差 6 条边**全是 `w 1.000/1.000` 的等权几何边**
（左全部 6 条，右前 2 条；右第 3~6 条是 37.2 / 36.6 的权重边，刚好在地板下面）。

## 顺带发现的产品 bug（**未改产品代码**）

`GameDataPackageResolver.GetPackageResourceAsync` 对 bundled 归档的**所有**分区都用
基名去找包，但 `bundles.nxa` 里一个归档其实是**三个包**：

```
d923906f8b815987                  195632 B   ← TOC 区
d923906f8b815987.gpu_resources   8868504 B   ← GPU 区
d923906f8b815987.stream          9087920 B   ← Stream 区
```

后果：`hd2mod gamedata extract asset` 抽原版 unit 时**只写出 `.toc`**
（manifest 里 `gpuResourceSize` 明明写着 884120，文件却不出），
`GameDataUnitMeshReader` 也拿不到原版 GPU 载荷。
修法：`ReadPartAsync` / `GetPackageResourceAsync` 里按分区拼后缀。
本条属管理器/CLI，改动要重编，本轮**只记录不改**。

探针 `_probe_vanilla_wrist.py` 自己实现了 bundle 解析（DSAR chunk walk + `bundles.nxa` 包表）
绕开了它，因此拿到了原版权重——这是本轮的**关键突破**。

## 产物

| 文件 | 值 |
|---|---|
| `E:/tmp/round68-final/` | 第 3.5 步产物（164404 B / 84027008 B） |
| `E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0` | 562176 B，sha256 `5e4e045c…`（**与第 67 轮相同**，只改 GPU 侧） |
| `…patch_0.gpu_resources` | 84229248 B，sha256 `de5f5dd1725fe878c953c5f09f3029ad7e2e8554af8c493a5811d93d814f6c69`（第 67 轮是 `bb6e3008…`） |
| `preview/wrist_roll_round67_vs_round68.png` | 目视验收图（条纹，0/90/180°） |
| `preview/unit_from_shipped_patch.png` | 从本包字节重新渲染（118014 v / 154066 t） |

归档：
* 第 67 轮规整产物 → `E:/tmp/round67-an-out-full/`
* 第 67 轮规整前输入 + round67 plan + library.json → `E:/tmp/round68-backup-native/`

## 复现命令

```bash
# 第 3.5 步（只改 GPU 侧权重字节）
python tmp/rig-lab/reweight_forearm_twist.py \
  --src "E:/tmp/lilium-final/9ba626afa44a3aa3.patch_0" \
  --out "E:/tmp/round68-final" --name lilium-forearm-roll \
  --a 0.50 --b 1.00 --twist-frac 0.0 --radius 0.10

# 4) 替换库节点输入 + 注册 + 规整
cp E:/tmp/round68-final/9ba626afa44a3aa3.patch_0{,.gpu_resources} E:/mod/Lilium-Sancta-Native-RE2310/
hd2mod library sync inspect|plan|validate|apply --manager-root E:/tmp/hd2-mgroot --library E:/mod
hd2mod equipment armor-normalize inspect|plan|validate|apply a74a1022 \
  --manager-root E:/tmp/hd2-mgroot --library E:/mod \
  --game-data "D:/SteamLibrary/steamapps/common/Helldivers 2/data" \
  --output "E:/tmp/lilium-an-out" --confirm

# 5) 组装
python tmp/rig-lab/assemble_re2310.py

# 验证
python tmp/rig-lab/_probe_wrist_roll.py   E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0
python tmp/rig-lab/_probe_forearm_action.py E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0
python tmp/rig-lab/_probe_weightdiff.py   E:/tmp/lilium-final/9ba626afa44a3aa3.patch_0 \
                                          E:/tmp/round68-final/9ba626afa44a3aa3.patch_0
python tmp/rig-lab/_probe_wrist_roll_render.py E:/tmp/r68-wrist2.png \
  E:/tmp/round67-an-out-full/9ba626afa44a3aa3.patch_0 \
  E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0 \
  --label "round 67" --label "round 68" --angles 0,90,180
python tmp/rig-lab/_verify_normalized.py \
  E:/tmp/round68-final/9ba626afa44a3aa3.patch_0 \
  E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0 \
  --our-unit cae35ee15168145f,9841b16544c8fbae \
  --vanilla-dir E:/tmp/hd2-mgroot/re2310-full/unit
```

## 待办 / 下一轮候选

1. **实机验收**：动臂 + 拧腕时腕缝是否还裂；中段会不会又变软；手指是否仍正常。
2. 若确认游戏**确实驱动** `l_hand_twist`，可试把远端一部分权重改挂 twist 骨
   （把「拧转」与「弯折」彻底解耦，中段能同时保持刚性）。
   验法：在实机里做一个纯拧转动作，看小臂上是否有**局部**旋转 —— 有就说明 twist 在动。
3. 拇指末节 12.2 mm、食指 ~4.8 mm 是**形状差**（源侧拇指 0→02 段短 9.3%、食指短 8.5%）
   ⇒ 需「按指沿轴缩放」新机制。
4. 产品侧 bug：bundled 归档的 GPU/stream 读不到（见上）。
5. 清理 `E:/tmp/round68-*` 系列临时目录（vanilla / van-u / van-arm / van-skin / sw-* / cand-* / final / backup-native）。
